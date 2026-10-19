"""Tests for tools/padprobe.py, using fake pygame and vgamepad modules so no pads or driver are needed.

Run: python -m unittest discover -s tools/tests -v
"""

import contextlib
import importlib.util
import io
import json
import os
import sys
import types
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import padprobe  # noqa: E402

# GUIDs SDL 2.32 reported on the dev PC for ViGEmBus pads.
X360_GUID = "0300b9695e0400008e02000000007200"
DS4_GUID = "03008fe54c050000c405000000016800"
DS4_V2_GUID = "030000004c050000cc09000000016800"
XBOX_ONE_GUID = "030000005e040000ea02000000007200"
# Old-style XInput GUID: the text "xinput" instead of USB IDs.
NO_IDS_GUID = "78696e70757401000000000000000000"


class FakeJoystick:
    def __init__(self, instance_id, name, guid, buttons=11, axes=6, hats=1):
        self.instance_id = instance_id
        self.name = name
        self.guid = guid
        self.buttons = buttons
        self.axes = axes
        self.hats = hats

    def get_instance_id(self):
        return self.instance_id

    def get_name(self):
        return self.name

    def get_guid(self):
        return self.guid

    def get_numbuttons(self):
        return self.buttons

    def get_numaxes(self):
        return self.axes

    def get_numhats(self):
        return self.hats


def x360(instance_id=0):
    return FakeJoystick(instance_id, "Xbox 360 Controller", X360_GUID)


def ds4(instance_id=1):
    return FakeJoystick(instance_id, "PS4 Controller", DS4_GUID, buttons=16, hats=0)


class FakePygame:
    """The parts of pygame padprobe uses. Like real pygame, events need display.init() first."""

    JOYAXISMOTION = 1536
    JOYHATMOTION = 1538
    JOYBUTTONDOWN = 1539
    JOYBUTTONUP = 1540
    JOYDEVICEADDED = 1541
    JOYDEVICEREMOVED = 1542

    def __init__(self, pads=(), batches=()):
        self.pads = list(pads)  # connected pads, in device index order
        self.arriving = []  # pads that join self.pads on the next pump
        self.batches = list(batches)  # each event.get() returns the next batch
        self.display_ready = False
        self.quit_called = False
        self.joystick = types.SimpleNamespace(
            init=lambda: None, get_count=lambda: len(self.pads), Joystick=lambda index: self.pads[index]
        )
        self.display = types.SimpleNamespace(init=self._display_init)
        self.event = types.SimpleNamespace(get=self._get, pump=self._pump)

    def _display_init(self):
        self.display_ready = True

    def _pump(self):
        if not self.display_ready:
            raise RuntimeError("video system not initialized")
        self.pads.extend(self.arriving)
        self.arriving.clear()

    def _get(self):
        self._pump()
        batch = self.batches.pop(0) if self.batches else []
        if isinstance(batch, BaseException):
            raise batch
        return batch

    def quit(self):
        self.quit_called = True


def event(event_type, **attrs):
    return types.SimpleNamespace(type=event_type, **attrs)


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


def run(function, *args, **kwargs):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = function(*args, **kwargs)
    return code, out.getvalue(), err.getvalue()


def run_main(fake, argv):
    with mock.patch.dict(sys.modules, {"pygame": fake}):
        return run(padprobe.main, argv)


class PadTypeTests(unittest.TestCase):
    def test_vigem_xbox_360_pad_is_x360(self):
        self.assertEqual((0x045E, 0x028E), padprobe.usb_ids(X360_GUID))
        self.assertEqual("x360", padprobe.pad_type(padprobe.usb_ids(X360_GUID)))

    def test_vigem_dualshock_4_pad_is_ds4(self):
        self.assertEqual((0x054C, 0x05C4), padprobe.usb_ids(DS4_GUID))
        self.assertEqual("ds4", padprobe.pad_type(padprobe.usb_ids(DS4_GUID)))

    def test_dualshock_4_v2_is_ds4(self):
        self.assertEqual("ds4", padprobe.pad_type(padprobe.usb_ids(DS4_V2_GUID)))

    def test_xbox_one_pad_is_other(self):
        self.assertEqual("other", padprobe.pad_type(padprobe.usb_ids(XBOX_ONE_GUID)))

    def test_guid_without_usb_ids_is_other(self):
        for guid in (NO_IDS_GUID, "not hex", "0300"):
            with self.subTest(guid=guid):
                self.assertIsNone(padprobe.usb_ids(guid))
                self.assertEqual("other", padprobe.pad_type(padprobe.usb_ids(guid)))


class ListTests(unittest.TestCase):
    def test_prints_every_pad_as_json(self):
        fake = FakePygame([x360(0), ds4(1), FakeJoystick(2, "Mystery Pad", NO_IDS_GUID, 4, 2, 0)])
        code, out, _ = run_main(fake, [])
        self.assertEqual(padprobe.EXIT_OK, code)
        self.assertEqual(
            [
                {"index": 0, "instance_id": 0, "name": "Xbox 360 Controller", "guid": X360_GUID, "type": "x360",
                 "vendor_id": "045e", "product_id": "028e", "buttons": 11, "axes": 6, "hats": 1},
                {"index": 1, "instance_id": 1, "name": "PS4 Controller", "guid": DS4_GUID, "type": "ds4",
                 "vendor_id": "054c", "product_id": "05c4", "buttons": 16, "axes": 6, "hats": 0},
                {"index": 2, "instance_id": 2, "name": "Mystery Pad", "guid": NO_IDS_GUID, "type": "other",
                 "vendor_id": None, "product_id": None, "buttons": 4, "axes": 2, "hats": 0},
            ],
            json.loads(out),
        )
        self.assertTrue(fake.quit_called)

    def test_no_pads_prints_empty_list(self):
        code, out, _ = run_main(FakePygame(), [])
        self.assertEqual(padprobe.EXIT_OK, code)
        self.assertEqual([], json.loads(out))

    def environment_at_import(self, argv=(), **preset):
        """Run main(argv) and return (os.environ as it was when padprobe imported pygame, stdout)."""
        fake = FakePygame()
        seen = {}

        class Finder:
            def find_spec(self, name, path=None, target=None):
                return importlib.util.spec_from_loader(name, self) if name == "pygame" else None

            def create_module(self, spec):
                seen.update(os.environ)
                return fake

            def exec_module(self, module):
                pass

        with mock.patch.dict(os.environ), mock.patch.dict(sys.modules), mock.patch.object(sys, "meta_path", [Finder(), *sys.meta_path]):
            for name in ("PYGAME_HIDE_SUPPORT_PROMPT", "SDL_JOYSTICK_RAWINPUT"):
                os.environ.pop(name, None)
            os.environ.update(preset)
            sys.modules.pop("pygame", None)
            code, out, _ = run(padprobe.main, list(argv))
        self.assertEqual(padprobe.EXIT_OK, code)
        return seen, out

    def test_hides_pygame_banner_before_import(self):
        # pygame prints its banner to stdout on import unless PYGAME_HIDE_SUPPORT_PROMPT is set.
        seen, out = self.environment_at_import()
        self.assertIn("PYGAME_HIDE_SUPPORT_PROMPT", seen)
        self.assertEqual([], json.loads(out))

    def test_list_counts_every_xbox_pad(self):
        # SDL's default RAWINPUT driver lists Xbox pads past XInput's 4, so leftover pads fail --expect.
        seen, _ = self.environment_at_import()
        self.assertNotIn("SDL_JOYSTICK_RAWINPUT", seen)

    def test_watch_reads_xbox_pads_through_xinput(self):
        seen, _ = self.environment_at_import(["--watch", "--seconds", "0.01"])
        self.assertEqual("0", seen["SDL_JOYSTICK_RAWINPUT"])

    def test_watch_keeps_a_raw_input_setting_from_the_caller(self):
        seen, _ = self.environment_at_import(["--watch", "--seconds", "0.01"], SDL_JOYSTICK_RAWINPUT="1")
        self.assertEqual("1", seen["SDL_JOYSTICK_RAWINPUT"])

    def test_missing_pygame_exits_4(self):
        # A None entry in sys.modules makes `import pygame` raise ModuleNotFoundError.
        code, out, err = run_main(None, [])
        self.assertEqual(padprobe.EXIT_NO_PYGAME, code)
        self.assertEqual("", out)
        self.assertIn("pygame is not installed", err)


class ExpectTests(unittest.TestCase):
    def test_parses_counts(self):
        self.assertEqual({"x360": 4, "ds4": 4}, padprobe.parse_expect("x360=4,ds4=4"))
        self.assertEqual({"x360": 0}, padprobe.parse_expect("x360=0"))
        self.assertEqual({"other": 1, "ds4": 8}, padprobe.parse_expect("other=1, ds4=8"))

    def test_rejects_bad_counts_with_exit_2(self):
        for spec in ("x360", "ps5=1", "x360=-1", "x360=a", "x360=1,x360=2", "", "x360=4,", "x360=²"):
            with self.subTest(spec=spec), self.assertRaises(SystemExit) as raised:
                run_main(FakePygame(), ["--expect", spec])
            self.assertEqual(2, raised.exception.code)

    def test_matching_counts_exit_0(self):
        code, out, err = run_main(FakePygame([x360(0), ds4(1)]), ["--expect", "x360=1,ds4=1"])
        self.assertEqual(padprobe.EXIT_OK, code)
        self.assertEqual(2, len(json.loads(out)))
        self.assertIn("ok: x360=1,ds4=1", err)

    def test_different_counts_exit_1_with_both_counts(self):
        code, _, err = run_main(FakePygame([x360(0), ds4(1)]), ["--expect", "x360=4,ds4=4"])
        self.assertEqual(padprobe.EXIT_MISMATCH, code)
        self.assertIn("expected x360=4,ds4=4, found x360=1,ds4=1", err)

    def test_zero_expected_with_no_pads_exit_0(self):
        code, _, _ = run_main(FakePygame(), ["--expect", "x360=0,ds4=0"])
        self.assertEqual(padprobe.EXIT_OK, code)

    def test_types_not_named_are_ignored(self):
        pads = [x360(0), FakeJoystick(1, "Mystery Pad", NO_IDS_GUID)]
        code, _, _ = run_main(FakePygame(pads), ["--expect", "x360=1"])
        self.assertEqual(padprobe.EXIT_OK, code)


class ArgumentTests(unittest.TestCase):
    def test_bad_combinations_exit_2(self):
        for argv in (["--seconds", "5"], ["--watch", "--seconds", "0"], ["--watch", "--expect", "x360=0"],
                     ["--watch", "--check-driver"]):
            with self.subTest(argv=argv), self.assertRaises(SystemExit) as raised:
                run_main(FakePygame(), argv)
            self.assertEqual(2, raised.exception.code)


class WatchTests(unittest.TestCase):
    def watch(self, fake, seconds=1.0):
        clock = FakeClock()
        out = io.StringIO()
        padprobe.watch(fake, seconds, out=out, clock=clock, sleep=clock.sleep)
        text = out.getvalue()
        return text, [json.loads(line) for line in text.splitlines()]

    def test_prints_one_json_line_per_change(self):
        pad = x360(7)
        fake = FakePygame(batches=[
            [event(FakePygame.JOYDEVICEADDED, device_index=0)],
            [event(FakePygame.JOYBUTTONDOWN, instance_id=7, button=0)],
            [event(FakePygame.JOYAXISMOTION, instance_id=7, axis=1, value=-0.4987)],
            [event(FakePygame.JOYHATMOTION, instance_id=7, hat=0, value=(0, 1))],
            [event(FakePygame.JOYBUTTONUP, instance_id=7, button=0)],
            [event(FakePygame.JOYDEVICEREMOVED, instance_id=7)],
        ])
        fake.pads = [pad]
        _, lines = self.watch(fake)
        self.assertEqual(
            [
                {"t": 0.0, "event": "added", "pad": padprobe.describe(pad, 0)},
                {"t": 0.01, "event": "button", "instance_id": 7, "button": 0, "pressed": True},
                {"t": 0.02, "event": "axis", "instance_id": 7, "axis": 1, "value": -0.5},
                {"t": 0.03, "event": "hat", "instance_id": 7, "hat": 0, "value": [0, 1]},
                {"t": 0.04, "event": "button", "instance_id": 7, "button": 0, "pressed": False},
                {"t": 0.05, "event": "removed", "instance_id": 7},
            ],
            lines,
        )
        self.assertTrue(fake.display_ready)

    def test_pads_already_connected_are_added_without_an_event(self):
        # SDL drops the added events of pads already connected when its joystick system starts
        # before the video system, as padprobe starts them.
        pad = ds4(3)
        _, lines = self.watch(FakePygame([pad]))
        self.assertEqual([{"t": 0.0, "event": "added", "pad": padprobe.describe(pad, 0)}], lines)

    def test_an_added_event_for_a_pad_already_listed_is_not_repeated(self):
        fake = FakePygame([ds4(3)], batches=[[event(FakePygame.JOYDEVICEADDED, device_index=0)]])
        _, lines = self.watch(fake)
        self.assertEqual(["added"], [line["event"] for line in lines])

    def test_a_pad_plugged_in_later_is_added_by_its_event(self):
        pad = x360(5)
        fake = FakePygame([ds4(3)], batches=[[], [event(FakePygame.JOYDEVICEADDED, device_index=1)]])
        fake.arriving = [pad]
        _, lines = self.watch(fake)
        self.assertEqual({"t": 0.01, "event": "added", "pad": padprobe.describe(pad, 1)}, lines[-1])
        self.assertEqual(2, len(lines))

    def test_axis_prints_only_when_rounded_value_changes(self):
        axis = FakePygame.JOYAXISMOTION
        fake = FakePygame(batches=[[
            event(axis, instance_id=0, axis=0, value=0.501),
            event(axis, instance_id=0, axis=0, value=0.504),
            event(axis, instance_id=0, axis=1, value=0.504),
            event(axis, instance_id=0, axis=0, value=0.51),
            event(axis, instance_id=0, axis=0, value=-0.001),
            event(axis, instance_id=0, axis=0, value=0.002),
        ]])
        text, lines = self.watch(fake)
        self.assertEqual([(0, 0.5), (1, 0.5), (0, 0.51), (0, 0.0)], [(line["axis"], line["value"]) for line in lines])
        self.assertNotIn("-0.0", text)

    def test_stops_after_seconds(self):
        clock = FakeClock()
        padprobe.watch(FakePygame(), 2.0, out=io.StringIO(), clock=clock, sleep=clock.sleep)
        self.assertGreaterEqual(clock.now, 102.0)
        self.assertLess(clock.now, 102.1)

    def test_main_watch_exits_0(self):
        fake = FakePygame([x360(0)], batches=[[event(FakePygame.JOYDEVICEADDED, device_index=0)]])
        code, out, _ = run_main(fake, ["--watch", "--seconds", "0.05"])
        self.assertEqual(padprobe.EXIT_OK, code)
        self.assertEqual("added", json.loads(out.splitlines()[0])["event"])
        self.assertTrue(fake.quit_called)

    def test_ctrl_c_exits_0(self):
        fake = FakePygame(batches=[[], KeyboardInterrupt()])
        code, _, _ = run_main(fake, ["--watch"])
        self.assertEqual(padprobe.EXIT_OK, code)
        self.assertTrue(fake.quit_called)


class CheckDriverTests(unittest.TestCase):
    def setUp(self):
        self.fake = FakePygame()
        self.created = 0
        self.released = 0
        self.fail_with = None
        self.appears = True
        test = self

        class FakeVDS4Gamepad:
            def __init__(self):
                if test.fail_with is not None:
                    raise test.fail_with
                test.created += 1
                if test.appears:
                    test.fake.arriving.append(ds4(10 + test.created))

            def __del__(self):
                test.released += 1

        self.vgamepad = types.SimpleNamespace(VDS4Gamepad=FakeVDS4Gamepad)
        self.version = padprobe.DRIVER_MINIMUM

    def check(self):
        clock = FakeClock()
        with mock.patch.dict(sys.modules, {"vgamepad": self.vgamepad}), \
                mock.patch.object(padprobe, "read_driver_version", lambda path: self.version):
            return run(padprobe.check_driver, self.fake, clock=clock, sleep=clock.sleep)

    def test_pad_the_probe_sees_exits_0(self):
        code, out, err = self.check()
        self.assertEqual(padprobe.EXIT_OK, code)
        self.assertEqual((1, 1), (self.created, self.released))
        self.assertEqual("", out)
        self.assertIn("ok:", err)

    def test_counts_only_the_new_pad(self):
        self.fake.pads = [ds4(0)]
        self.appears = False
        code, _, err = self.check()
        self.assertEqual(padprobe.EXIT_DRIVER_FAILED, code)
        self.assertIn("never saw it", err)

    def test_pad_never_seen_exits_3_and_removes_pad(self):
        self.appears = False
        code, _, err = self.check()
        self.assertEqual(padprobe.EXIT_DRIVER_FAILED, code)
        self.assertEqual((1, 1), (self.created, self.released))
        self.assertIn("never saw it", err)

    def test_old_driver_exits_3_without_creating_a_pad(self):
        self.version = (1, 17, 333, 0)
        code, _, err = self.check()
        self.assertEqual(padprobe.EXIT_DRIVER_FAILED, code)
        self.assertEqual(0, self.created)
        self.assertIn("1.17.333.0 is older than 1.21.442.0", err)

    def test_missing_driver_file_exits_3(self):
        self.version = None
        code, _, err = self.check()
        self.assertEqual(padprobe.EXIT_DRIVER_FAILED, code)
        self.assertEqual(0, self.created)
        self.assertIn("ViGEmBus.sys not found", err)

    def test_missing_vgamepad_exits_3(self):
        self.vgamepad = None
        code, _, err = self.check()
        self.assertEqual(padprobe.EXIT_DRIVER_FAILED, code)
        self.assertIn("vgamepad is not installed", err)

    def test_pad_creation_failure_exits_3_with_reason(self):
        self.fail_with = AssertionError("The virtual device could not connect to ViGEmBus.")
        code, _, err = self.check()
        self.assertEqual(padprobe.EXIT_DRIVER_FAILED, code)
        self.assertIn("could not connect to ViGEmBus", err)

    def test_main_check_driver_uses_check(self):
        with mock.patch.object(padprobe, "check_driver", lambda pygame: padprobe.EXIT_DRIVER_FAILED):
            code, _, _ = run_main(self.fake, ["--check-driver"])
        self.assertEqual(padprobe.EXIT_DRIVER_FAILED, code)
        self.assertTrue(self.fake.quit_called)


@unittest.skipUnless(sys.platform == "win32", "reads Windows file versions")
class DriverVersionTests(unittest.TestCase):
    def test_reads_a_windows_file_version(self):
        version = padprobe.read_driver_version(os.path.join(os.environ["SystemRoot"], "System32", "kernel32.dll"))
        self.assertEqual(4, len(version))
        self.assertGreaterEqual(version[0], 6)

    def test_missing_file_is_none(self):
        self.assertIsNone(padprobe.read_driver_version(os.path.join(os.environ["SystemRoot"], "no-such-file.sys")))


if __name__ == "__main__":
    unittest.main()
