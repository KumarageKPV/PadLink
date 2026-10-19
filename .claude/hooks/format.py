"""PostToolUse hook: format the file Claude just edited or wrote.

Reads the hook's JSON from stdin, takes tool_input.file_path, and runs
`dart format` on .dart files or `dotnet format whitespace` on .cs files.
Always exits 0 so a formatter problem never blocks work.
"""

import json
import os
import shutil
import subprocess
import sys

TIMEOUT_SECONDS = 60


def formatter_command(path):
    """Return the formatter command for path, or None if there is nothing to run."""
    ext = os.path.splitext(path)[1].lower()
    if ext == ".dart":
        # shutil.which honours PATHEXT, so it finds dart.bat in a Flutter SDK on Windows.
        dart = shutil.which("dart")
        return [dart, "format", path] if dart else None
    if ext == ".cs":
        dotnet = shutil.which("dotnet")
        if not dotnet:
            return None
        # Folder mode needs no project or restore; parent .editorconfig files still apply.
        folder, name = os.path.split(path)
        return [dotnet, "format", "whitespace", folder, "--folder", "--include", name]
    return None


def main():
    try:
        payload = json.loads(sys.stdin.buffer.read().decode("utf-8"))
        path = (payload.get("tool_input") or {}).get("file_path")
        if not path or not os.path.isfile(path):
            return
        command = formatter_command(os.path.abspath(path))
        if command is None:
            return
        result = subprocess.run(
            command,
            cwd=os.path.dirname(os.path.abspath(path)),
            stdin=subprocess.DEVNULL,
            capture_output=True,
            timeout=TIMEOUT_SECONDS,
        )
        if result.returncode != 0:
            tool = os.path.basename(command[0])
            print(f"format.py: {tool} exited {result.returncode} for {path}", file=sys.stderr)
    except Exception as exc:  # never block the edit
        print(f"format.py: {exc}", file=sys.stderr)


if __name__ == "__main__":
    main()
    sys.exit(0)
