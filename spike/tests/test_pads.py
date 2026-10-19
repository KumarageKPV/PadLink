"""Tests for spike/pads.py, using a fake vgamepad module so no driver is needed.

Run: python -m unittest discover -s spike/tests -v
"""

import contextlib
import inspect
import io
import json
import os
import sys
import types
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import pads  # noqa: E402

VIGEM_ERROR_INVALID_TARGET = 0xE0000017


def callback_pattern(client, target, large_motor, small_motor, led_number, user_data):
    """The only notification callback signature vgamepad accepts."""


class Log:
    """One ordered record of pad creates, unregisters, releases and stdout lines, shared by the fakes."""

    def __init__(self):
        self.entries = []
        self._line = ""

    # The file interface print() needs, so main's stdout lands in the same record.
    def write(self, text):
        self._line += text
        while "\n" in self._line:
            line, self._line = self._line.split("\n", 1)
            self.entries.append(("out", json.loads(line)))

    def flush(self):
        pass

    def lines(self):
        return [entry[1] for entry in self.entries if entry[0] == "out"]

    def events(self):
        return [line["event"] for line in self.lines()]

    def kinds(self):
        return [entry[0] for entry in self.entries]


class FakeVgamepad:
    """The parts of vgamepad pads.py uses. Device ids count constructor calls from 1.

    fail_at: the device id whose constructor raises; interrupt_at: whose constructor raises Ctrl+C;
    register_fail_at: whose register_notification raises. user_indices and leds map a device id
    to the driver's user index and the LED number its notification reports; a missing id means the
    call fails or no notification comes. By default every pad's LED number is its device id - 1.
    """

    def __init__(self, fail_at=None, interrupt_at=None, register_fail_at=None, user_indices=None, leds=None):
        self.log = Log()
        self.fail_at = fail_at
        self.interrupt_at = interrupt_at
        self.register_fail_at = register_fail_at
        self.user_indices = user_indices or {}
        self.leds = leds
        self.next_id = 1
        self.live = set()
        self.registered = set()
        fake = self

        class FakePad:
            pad_type = None

            def __init__(self):
                device_id = fake.next_id
                fake.next_id += 1
                if device_id == fake.fail_at:
                    raise AssertionError("The virtual device could not connect to ViGEmBus.")
                if device_id == fake.interrupt_at:
                    raise KeyboardInterrupt
                self._busp = "bus"
                self._devicep = device_id
                fake.live.add(device_id)
                fake.log.entries.append(("create", self.pad_type, device_id))

            def register_notification(self, callback_function):
                if inspect.signature(callback_function) != inspect.signature(callback_pattern):
                    raise TypeError(f"Needed callback function signature: {inspect.signature(callback_pattern)}")
                if self._devicep == fake.register_fail_at:
                    raise Exception("VIGEM_ERROR_INVALID_TARGET")
                fake.registered.add(self._devicep)
                led = self._devicep - 1 if fake.leds is None else fake.leds.get(self._devicep)
                if led is not None:
                    callback_function(None, None, 0, 0, led, None)

            def unregister_notification(self):
                fake.registered.discard(self._devicep)
                fake.log.entries.append(("unregister", self.pad_type, self._devicep))

            def __del__(self):
                if getattr(self, "_devicep", None) is not None:
                    fake.live.discard(self._devicep)
                    fake.log.entries.append(("release", self.pad_type, self._devicep))

        class VX360Gamepad(FakePad):
            pad_type = "x360"

        class VDS4Gamepad(FakePad):
            pad_type = "ds4"

        self.VX360Gamepad = VX360Gamepad
        self.VDS4Gamepad = VDS4Gamepad
        self.win = types.SimpleNamespace(
            vigem_client=types.SimpleNamespace(vigem_target_x360_get_user_index=self.get_user_index))

    def get_user_index(self, busp, devicep, index_pointer):
        if devicep not in self.user_indices:
            return VIGEM_ERROR_INVALID_TARGET
        index_pointer.contents.value = self.user_indices[devicep]
        return pads.VIGEM_ERROR_NONE


class FakeTime:
    """A clock that only sleep() moves. interrupt_at: a sleep of exactly that many seconds raises Ctrl+C."""

    def __init__(self, interrupt_at=None):
        self.now = 100.0
        self.sleeps = []
        self.interrupt_at = interrupt_at

    def clock(self):
        return self.now

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        if seconds == self.interrupt_at:
            raise KeyboardInterrupt
        self.now += seconds


def run_main(argv, module, fake_time=None):
    log = module.log if isinstance(module, FakeVgamepad) else Log()
    fake_time = FakeTime() if fake_time is None else fake_time
    err = io.StringIO()
    with mock.patch.dict(sys.modules, {"vgamepad": module}), contextlib.redirect_stderr(err):
        code = pads.main(argv, out=log, clock=fake_time.clock, sleep=fake_time.sleep)
    return code, log, fake_time, err.getvalue()


class PlanTests(unittest.TestCase):
    def test_mixed_fills_slots_1_to_4_with_xbox_first(self):
        self.assertEqual(["x360"] * 4 + ["ds4"] * 4, pads.plan_pads("mixed", 8))

    def test_mixed_below_5_is_all_xbox(self):
        self.assertEqual(["x360"] * 3, pads.plan_pads("mixed", 3))

    def test_mixed_16_is_4_xbox_and_12_ds4(self):
        self.assertEqual(["x360"] * 4 + ["ds4"] * 12, pads.plan_pads("mixed", 16))

    def test_all_ds4_is_ds4_in_every_slot(self):
        self.assertEqual(["ds4"] * 8, pads.plan_pads("all_ds4", 8))


class ArgsTests(unittest.TestCase):
    def assert_usage_error(self, argv):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as raised:
            pads.parse_args(argv)
        self.assertEqual(2, raised.exception.code)

    def test_defaults(self):
        args = pads.parse_args(["--mode", "mixed", "--count", "8"])
        self.assertEqual(("mixed", 8, pads.DEFAULT_SECONDS), (args.mode, args.count, args.seconds))

    def test_count_must_be_1_to_16(self):
        self.assert_usage_error(["--mode", "mixed", "--count", "0"])
        self.assert_usage_error(["--mode", "mixed", "--count", "17"])
        self.assertEqual(16, pads.parse_args(["--mode", "all_ds4", "--count", "16"]).count)

    def test_seconds_must_be_above_0(self):
        self.assert_usage_error(["--mode", "mixed", "--count", "8", "--seconds", "0"])

    def test_mode_and_count_are_required(self):
        self.assert_usage_error(["--count", "8"])
        self.assert_usage_error(["--mode", "mixed"])

    def test_unknown_mode(self):
        self.assert_usage_error(["--mode", "all_x360", "--count", "8"])


class MainTests(unittest.TestCase):
    def test_mixed_creates_xbox_first_reads_xinput_holds_then_removes_every_pad(self):
        fake = FakeVgamepad(user_indices={1: 0, 2: 0, 3: 0, 4: 0})
        code, log, fake_time, _ = run_main(["--mode", "mixed", "--count", "8", "--seconds", "20"], fake)

        self.assertEqual(pads.EXIT_OK, code)
        creates = [entry[1] for entry in log.entries if entry[0] == "create"]
        self.assertEqual(["x360"] * 4 + ["ds4"] * 4, creates)
        self.assertEqual([20.0], fake_time.sleeps)  # every LED number came at once, so no wait
        self.assertEqual(set(), fake.live)

        lines = log.lines()
        self.assertEqual([{"event": "created", "slot": slot, "type": "x360"} for slot in range(1, 5)]
                         + [{"event": "created", "slot": slot, "type": "ds4"} for slot in range(5, 9)], lines[:8])
        self.assertEqual([{"event": "xinput", "slot": slot, "user_index": 0, "led_number": slot - 1}
                          for slot in range(1, 5)], lines[8:12])
        self.assertEqual({"event": "ready", "mode": "mixed", "asked": 8, "x360": 4, "ds4": 4, "seconds": 20.0},
                         lines[12])
        self.assertEqual({"event": "removed", "count": 8}, lines[13])
        self.assertEqual(14, len(lines))

    def test_every_pad_is_released_before_removed_is_printed(self):
        fake = FakeVgamepad()
        _, log, _, _ = run_main(["--mode", "all_ds4", "--count", "8"], fake)
        kinds = log.kinds()
        ready_at = log.entries.index(("out", log.lines()[-2]))
        self.assertEqual("ready", log.lines()[-2]["event"])
        self.assertEqual(["release"] * 8 + ["out"], kinds[ready_at + 1:])
        self.assertEqual(("out", {"event": "removed", "count": 8}), log.entries[-1])

    def test_xbox_notifications_are_unregistered_before_their_pads_are_released(self):
        fake = FakeVgamepad()
        run_main(["--mode", "mixed", "--count", "5"], fake)
        self.assertEqual(set(), fake.registered)
        teardown = [(entry[0], entry[2]) for entry in fake.log.entries if entry[0] in ("unregister", "release")]
        self.assertEqual(["unregister"] * 4 + ["release"] * 5, [kind for kind, _ in teardown])
        self.assertEqual({1, 2, 3, 4}, {device for kind, device in teardown if kind == "unregister"})

    def test_all_ds4_reads_no_xinput(self):
        fake = FakeVgamepad()
        _, log, _, _ = run_main(["--mode", "all_ds4", "--count", "2"], fake)
        self.assertEqual(["created", "created", "ready", "removed"], log.events())
        self.assertEqual(set(), fake.registered)

    def test_waits_for_led_numbers_then_reports_the_missing_one_as_null(self):
        fake = FakeVgamepad(leds={1: 0}, user_indices={1: 0})
        code, log, fake_time, _ = run_main(["--mode", "mixed", "--count", "2", "--seconds", "20"], fake)
        self.assertEqual(pads.EXIT_OK, code)
        xinput = [line for line in log.lines() if line["event"] == "xinput"]
        self.assertEqual([{"event": "xinput", "slot": 1, "user_index": 0, "led_number": 0},
                          {"event": "xinput", "slot": 2, "user_index": None, "led_number": None}], xinput)
        waited = sum(fake_time.sleeps[:-1])
        self.assertAlmostEqual(pads.LED_WAIT_SECONDS, waited, delta=pads.POLL_SECONDS)
        self.assertEqual(20.0, fake_time.sleeps[-1])

    def test_driver_refusing_a_pad_keeps_and_holds_the_earlier_ones_then_exits_3(self):
        fake = FakeVgamepad(fail_at=14)
        code, log, fake_time, err = run_main(["--mode", "all_ds4", "--count", "16", "--seconds", "5"], fake)

        self.assertEqual(pads.EXIT_DRIVER_FAILED, code)
        self.assertEqual([5.0], fake_time.sleeps)
        self.assertEqual(set(), fake.live)
        lines = log.lines()
        self.assertEqual(13, log.events().count("created"))
        failed = [line for line in lines if line["event"] == "failed"]
        self.assertEqual([{"event": "failed", "slot": 14, "type": "ds4",
                           "error": "The virtual device could not connect to ViGEmBus."}], failed)
        self.assertEqual({"event": "ready", "mode": "all_ds4", "asked": 16, "x360": 0, "ds4": 13, "seconds": 5.0},
                         lines[-2])
        self.assertEqual({"event": "removed", "count": 13}, lines[-1])
        self.assertIn("13 of 16", err)

    def test_a_refused_xbox_pad_still_reads_xinput_for_the_earlier_ones(self):
        fake = FakeVgamepad(fail_at=3)
        code, log, _, _ = run_main(["--mode", "mixed", "--count", "8"], fake)
        self.assertEqual(pads.EXIT_DRIVER_FAILED, code)
        self.assertEqual(["created", "created", "failed", "xinput", "xinput", "ready", "removed"], log.events())
        self.assertEqual(set(), fake.registered)

    def test_a_failed_notification_registration_frees_that_pad_and_removes_the_rest(self):
        fake = FakeVgamepad(register_fail_at=2)
        code, log, _, err = run_main(["--mode", "mixed", "--count", "4"], fake)

        self.assertEqual(pads.EXIT_DRIVER_FAILED, code)
        self.assertEqual(["created", "failed", "xinput", "ready", "removed"], log.events())
        self.assertEqual({"event": "failed", "slot": 2, "type": "x360", "error": "VIGEM_ERROR_INVALID_TARGET"},
                         log.lines()[1])
        self.assertEqual({"event": "removed", "count": 1}, log.lines()[-1])
        self.assertEqual(set(), fake.live)
        self.assertEqual(set(), fake.registered)
        teardown = [(entry[0], entry[2]) for entry in log.entries if entry[0] in ("unregister", "release")]
        # Pad 2 is freed before the hold; pad 1 is unregistered, then released.
        self.assertEqual([("release", 2), ("unregister", 1), ("release", 1)], teardown)
        self.assertLess(log.entries.index(("release", "x360", 2)), log.entries.index(("out", log.lines()[3])))
        self.assertIn("1 of 4", err)

    def test_driver_refusing_the_first_pad_skips_the_hold(self):
        fake = FakeVgamepad(fail_at=1)
        code, log, fake_time, _ = run_main(["--mode", "mixed", "--count", "8"], fake)
        self.assertEqual(pads.EXIT_DRIVER_FAILED, code)
        self.assertEqual([], fake_time.sleeps)
        self.assertEqual(["failed", "removed"], log.events())

    def test_ctrl_c_during_the_hold_still_removes_every_pad(self):
        fake = FakeVgamepad()
        code, log, _, _ = run_main(["--mode", "mixed", "--count", "6", "--seconds", "30"], fake,
                                   FakeTime(interrupt_at=30.0))
        self.assertEqual(pads.EXIT_OK, code)
        self.assertEqual(set(), fake.live)
        self.assertEqual(set(), fake.registered)
        self.assertEqual({"event": "removed", "count": 6}, log.lines()[-1])

    def test_ctrl_c_during_the_led_wait_still_removes_every_pad(self):
        fake = FakeVgamepad(leds={})
        code, log, _, _ = run_main(["--mode", "mixed", "--count", "2"], fake, FakeTime(interrupt_at=pads.POLL_SECONDS))
        self.assertEqual(pads.EXIT_OK, code)
        self.assertEqual(["created", "created", "removed"], log.events())
        self.assertEqual(set(), fake.live)
        self.assertEqual(set(), fake.registered)

    def test_ctrl_c_while_creating_removes_the_pads_made_so_far(self):
        fake = FakeVgamepad(interrupt_at=3)
        code, log, _, _ = run_main(["--mode", "mixed", "--count", "8"], fake)
        self.assertEqual(pads.EXIT_OK, code)
        self.assertEqual(["created", "created", "removed"], log.events())
        self.assertEqual({"event": "removed", "count": 2}, log.lines()[-1])
        self.assertEqual(set(), fake.live)
        self.assertEqual(set(), fake.registered)

    def test_missing_vgamepad_exits_4(self):
        # A None entry in sys.modules makes `import vgamepad` raise ModuleNotFoundError.
        code, log, _, err = run_main(["--mode", "mixed", "--count", "8"], None)
        self.assertEqual(pads.EXIT_NO_VGAMEPAD, code)
        self.assertEqual([], log.lines())
        self.assertIn("vgamepad is not installed", err)

    def test_driver_connect_failure_on_import_exits_3(self):
        # vgamepad connects to the driver at import, so a missing driver fails the import itself.
        real_import = __import__

        def failing_import(name, *args, **kwargs):
            if name == "vgamepad":
                raise OSError("ViGEmBus is not installed")
            return real_import(name, *args, **kwargs)

        with mock.patch("builtins.__import__", failing_import):
            code, log, _, err = run_main(["--mode", "mixed", "--count", "8"], types.ModuleType("vgamepad"))
        self.assertEqual(pads.EXIT_DRIVER_FAILED, code)
        self.assertEqual([], log.lines())
        self.assertIn("ViGEmBus is not installed", err)


if __name__ == "__main__":
    unittest.main()
