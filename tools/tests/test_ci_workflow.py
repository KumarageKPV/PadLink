"""Tests for .github/workflows/ci.yml: job layout, skip gates, pins and caches.

The detect job's script is run for real in bash: in an empty folder, in one with every
project's marker file, and in ones where a project's files sit under the wrong name, to check
that missing projects are skipped and misnamed ones fail instead of skipping.

Run: python -m unittest discover -s tools/tests -v
"""

import os
import re
import shutil
import subprocess
import tempfile
import unittest

import yaml

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
WORKFLOW = os.path.join(ROOT, ".github", "workflows", "ci.yml")

# detect output -> the file that says the project exists
MARKERS = {
    "pc": "pc/PadLink.sln",
    "dart": "mobile/packages/padlink_protocol/pubspec.yaml",
    "app": "mobile/app/pubspec.yaml",
    "vectors": "tools/check_vectors.py",
}
# Project files without their marker: a marker path that no longer matches the project.
MISNAMED = [
    "pc/PadLink.slnx",  # what `dotnet new sln` makes on the .NET 10 SDK
    "pc/src/PadLink.Core/PadLink.Core.csproj",
    "mobile/packages/padlink-protocol/pubspec.yaml",
    "mobile/app/lib/main.dart",
    "protocol/vectors/input.json",
]
# detect output -> the folder whose steps it gates
PROJECT_DIRS = {
    "dart": "mobile/packages/padlink_protocol",
    "app": "mobile/app",
}


def read(path):
    with open(os.path.join(ROOT, path), encoding="utf-8") as f:
        return f.read()


def gate(flag):
    return f"needs.detect.outputs.{flag} == 'true'"


def run_lines(step):
    return [line.strip() for line in step.get("run", "").splitlines() if line.strip()]


def touch(folder, path):
    full = os.path.join(folder, *path.split("/"))
    os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, "w").close()


def find_bash():
    """bash on PATH, or None. The WSL launchers (System32, WindowsApps) don't see GITHUB_OUTPUT."""
    bash = shutil.which("bash")
    if bash and os.name == "nt" and os.path.basename(os.path.dirname(bash)).lower() in ("system32", "windowsapps"):
        return None
    return bash


class CiWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(WORKFLOW, encoding="utf-8") as f:
            cls.workflow = yaml.safe_load(f)
        cls.jobs = cls.workflow["jobs"]

    def steps(self, job):
        return self.jobs[job]["steps"]

    def step_running(self, job, command):
        """The one step in `job` whose run block has `command` as a whole line."""
        matches = [s for s in self.steps(job) if command in run_lines(s)]
        self.assertEqual(len(matches), 1, f"{job}: steps running {command!r}")
        return matches[0]

    def step_using(self, job, action):
        matches = [s for s in self.steps(job) if s.get("uses", "").split("@")[0] == action]
        self.assertEqual(len(matches), 1, f"{job}: steps using {action}")
        return matches[0]

    def test_runs_on_main_pushes_and_pull_requests(self):
        triggers = self.workflow.get("on", self.workflow.get(True))  # PyYAML reads a bare `on` as True
        self.assertEqual(triggers["push"]["branches"], ["main"])
        self.assertIn("pull_request", triggers)

    def test_token_is_read_only(self):
        self.assertEqual(self.workflow["permissions"], {"contents": "read"})

    def test_only_pull_request_runs_cancel_each_other(self):
        # Every push to main keeps its own result, so a quick second push can't hide a red run.
        concurrency = self.workflow["concurrency"]
        self.assertEqual(concurrency["group"], "ci-${{ github.ref }}")
        self.assertEqual(concurrency["cancel-in-progress"], "${{ github.event_name == 'pull_request' }}")

    def test_has_the_four_jobs(self):
        self.assertEqual(set(self.jobs), {"detect", "pc", "mobile", "python"})

    def test_every_job_checks_out_the_repo_first(self):
        for name in self.jobs:
            self.assertEqual(self.steps(name)[0].get("uses", "").split("@")[0], "actions/checkout", name)

    def test_checkout_does_not_keep_the_token(self):
        for name in self.jobs:
            checkout = self.step_using(name, "actions/checkout")
            self.assertIs(checkout.get("with", {}).get("persist-credentials"), False, name)

    def test_github_actions_are_pinned_to_a_major_and_others_to_a_commit(self):
        # A third-party tag can be moved to new code; a commit SHA can't.
        for name in self.jobs:
            for step in self.steps(name):
                if "uses" not in step:
                    continue
                if step["uses"].startswith("actions/"):
                    self.assertRegex(step["uses"], r"^actions/[\w.-]+@v\d+$", name)
                else:
                    self.assertRegex(step["uses"], r"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$", name)

    # --- detect ---

    def test_detect_outputs_one_flag_per_project(self):
        outputs = self.jobs["detect"]["outputs"]
        self.assertEqual(set(outputs), set(MARKERS))
        for flag, value in outputs.items():
            self.assertEqual(value, "${{ steps.find.outputs.%s }}" % flag)

    def test_other_jobs_wait_for_detect(self):
        for name in ("pc", "mobile", "python"):
            self.assertEqual(self.jobs[name]["needs"], "detect", name)

    def run_detect(self, folder):
        bash = find_bash()
        if bash is None:
            self.skipTest("needs bash (Git Bash on Windows)")
        script = [s for s in self.steps("detect") if s.get("id") == "find"][0]["run"]
        with open(os.path.join(folder, "detect.sh"), "w", encoding="utf-8", newline="\n") as f:
            f.write(script)
        # Same flags GitHub uses for `shell: bash`. Relative paths sidestep Windows path conversion.
        # bash's own folder goes first on PATH so `find` is GNU find, not System32\find.exe.
        result = subprocess.run(
            [bash, "--noprofile", "--norc", "-eo", "pipefail", "detect.sh"],
            cwd=folder,
            env={
                **os.environ,
                "GITHUB_OUTPUT": "github_output.txt",
                "PATH": os.path.dirname(bash) + os.pathsep + os.environ.get("PATH", ""),
            },
            capture_output=True,
            text=True,
        )
        outputs = {}
        output_path = os.path.join(folder, "github_output.txt")
        if os.path.exists(output_path):
            with open(output_path, encoding="utf-8") as f:
                outputs = dict(line.strip().split("=", 1) for line in f if line.strip())
        return result, outputs

    def test_detect_reports_missing_projects_as_false_with_a_notice(self):
        with tempfile.TemporaryDirectory() as folder:
            result, outputs = self.run_detect(folder)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outputs, {flag: "false" for flag in MARKERS})
        for marker in MARKERS.values():
            self.assertIn(f"::notice::{marker}", result.stdout)

    def test_detect_reports_present_projects_as_true(self):
        with tempfile.TemporaryDirectory() as folder:
            for path in list(MARKERS.values()) + MISNAMED:
                touch(folder, path)
            result, outputs = self.run_detect(folder)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(outputs, {flag: "true" for flag in MARKERS})
        self.assertNotIn("::notice::", result.stdout)

    def test_detect_fails_when_project_files_have_no_marker(self):
        for path in MISNAMED:
            with self.subTest(path=path), tempfile.TemporaryDirectory() as folder:
                touch(folder, path)
                result, _ = self.run_detect(folder)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn("::error::", result.stdout)

    # --- pc ---

    def test_pc_runs_on_windows_only_when_the_solution_exists(self):
        self.assertTrue(self.jobs["pc"]["runs-on"].startswith("windows-"))
        self.assertEqual(self.jobs["pc"]["if"], gate("pc"))

    def test_pc_runs_the_claude_md_build_and_test_commands(self):
        claude_md = read("CLAUDE.md")
        for label in ("PC", "PC tests"):
            command = re.search(rf"^- {label}: `([^`]+)`", claude_md, re.M).group(1)
            self.step_running("pc", command)

    def test_no_job_runs_the_driver_tests(self):
        # Runners have no ViGEmBus, so every dotnet test run must filter the driver tests out.
        test_runs = 0
        for name in self.jobs:
            for step in self.steps(name):
                self.assertNotIn("Category=RequiresViGEm", step.get("run", ""), name)
                for line in run_lines(step):
                    if line.startswith("dotnet test"):
                        test_runs += 1
                        self.assertIn('--filter "Category!=RequiresViGEm"', line, name)
        self.assertGreater(test_runs, 0)

    def test_pc_uses_dotnet_10(self):
        self.assertEqual(self.step_using("pc", "actions/setup-dotnet")["with"]["dotnet-version"], "10.0.x")

    def test_pc_caches_nuget_packages(self):
        cache = self.step_using("pc", "actions/cache")["with"]
        self.assertEqual(cache["path"], "~/.nuget/packages")
        self.assertIn("hashFiles('pc/**/*.csproj'", cache["key"])
        steps = self.steps("pc")
        build = self.step_running("pc", "dotnet build pc/PadLink.sln -warnaserror")
        self.assertLess(steps.index(self.step_using("pc", "actions/cache")), steps.index(build))

    # --- mobile ---

    def test_mobile_runs_on_ubuntu_when_either_dart_project_exists(self):
        self.assertTrue(self.jobs["mobile"]["runs-on"].startswith("ubuntu-"))
        self.assertEqual(self.jobs["mobile"]["if"], f"{gate('dart')} || {gate('app')}")

    def test_mobile_pins_the_doctors_flutter_version_and_caches_sdk_and_pub(self):
        pin = re.search(r"\$script:FlutterPin = '([^']+)'", read("tools/doctor.ps1")).group(1)
        flutter = self.step_using("mobile", "subosito/flutter-action")["with"]
        self.assertEqual(flutter["channel"], "stable")
        self.assertEqual(flutter["flutter-version"], pin)
        self.assertIn(flutter["cache"], (True, "true"))
        self.assertIn(flutter["pub-cache"], (True, "true"))

    def test_mobile_runs_dart_test_in_the_protocol_package(self):
        for command in ("dart pub get", "dart test"):
            self.assertEqual(self.step_running("mobile", command)["working-directory"], PROJECT_DIRS["dart"])

    def test_mobile_runs_flutter_analyze_and_test_in_the_app(self):
        for command in ("flutter pub get", "flutter analyze", "flutter test"):
            self.assertEqual(self.step_running("mobile", command)["working-directory"], PROJECT_DIRS["app"])

    def test_mobile_steps_are_gated_on_their_own_project(self):
        project_steps = [s for s in self.steps("mobile") if "working-directory" in s]
        self.assertTrue(project_steps)
        for step in project_steps:
            flag = {d: f for f, d in PROJECT_DIRS.items()}[step["working-directory"]]
            self.assertEqual(step.get("if"), gate(flag), step.get("name"))

    # --- python ---

    def test_python_job_always_runs_on_ubuntu_with_python_3_14(self):
        self.assertTrue(self.jobs["python"]["runs-on"].startswith("ubuntu-"))
        self.assertNotIn("if", self.jobs["python"])
        setup = self.step_using("python", "actions/setup-python")["with"]
        self.assertEqual(setup["python-version"], "3.14")
        self.assertEqual(setup["cache"], "pip")
        self.assertEqual(setup["cache-dependency-path"], "requirements.txt")

    def test_python_installs_only_packages_pinned_in_requirements(self):
        install = [line for s in self.steps("python") for line in run_lines(s) if " pip install " in line]
        self.assertEqual(len(install), 1)
        args = install[0].split()
        self.assertEqual(args[:6], ["python", "-m", "pip", "install", "-c", "requirements.txt"])
        packages = args[6:]
        self.assertTrue({"PyYAML", "cryptography"} <= set(packages), packages)
        pinned = re.findall(r"^([A-Za-z0-9_.-]+)==", read("requirements.txt"), re.M)
        for package in packages:
            self.assertIn(package, pinned)

    def test_python_runs_the_tools_tests_every_time(self):
        step = self.step_running("python", "python -m unittest discover -s tools/tests -v")
        self.assertNotIn("if", step)

    def test_python_checks_vectors_only_when_the_checker_exists(self):
        step = self.step_running("python", "python tools/check_vectors.py")
        self.assertEqual(step.get("if"), gate("vectors"))


if __name__ == "__main__":
    unittest.main()
