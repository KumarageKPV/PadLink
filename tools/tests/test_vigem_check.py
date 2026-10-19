"""Tests for tools/vigem_check.py, using a fake vgamepad module so no driver is needed.

Run: python -m unittest discover -s tools/tests -v
"""

import contextlib
import io
import os
import sys
import types
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import vigem_check  # noqa: E402


class FakePad:
    created = 0
    released = 0
    fail_with = None

    def __init__(self):
        if FakePad.fail_with is not None:
            raise FakePad.fail_with
        FakePad.created += 1

    def __del__(self):
        FakePad.released += 1


def run_main():
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = vigem_check.main()
    return code, out.getvalue(), err.getvalue()


class VigemCheckTests(unittest.TestCase):
    def setUp(self):
        FakePad.created = 0
        FakePad.released = 0
        FakePad.fail_with = None
        self.fake_module = types.SimpleNamespace(VX360Gamepad=FakePad)

    def test_creates_and_removes_one_pad(self):
        with mock.patch.dict(sys.modules, {"vgamepad": self.fake_module}):
            code, out, _ = run_main()
        self.assertEqual(vigem_check.EXIT_OK, code)
        self.assertEqual(1, FakePad.created)
        self.assertEqual(1, FakePad.released)
        self.assertIn("created and removed", out)

    def test_missing_vgamepad_exits_2(self):
        # A None entry in sys.modules makes `import vgamepad` raise ModuleNotFoundError.
        with mock.patch.dict(sys.modules, {"vgamepad": None}):
            code, _, err = run_main()
        self.assertEqual(vigem_check.EXIT_NO_VGAMEPAD, code)
        self.assertIn("vgamepad is not installed", err)

    def test_driver_failure_exits_3_with_reason(self):
        FakePad.fail_with = AssertionError("The virtual device could not connect to ViGEmBus.")
        with mock.patch.dict(sys.modules, {"vgamepad": self.fake_module}):
            code, _, err = run_main()
        self.assertEqual(vigem_check.EXIT_DRIVER_FAILED, code)
        self.assertIn("could not connect to ViGEmBus", err)


if __name__ == "__main__":
    unittest.main()
