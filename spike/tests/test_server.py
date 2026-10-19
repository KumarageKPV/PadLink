"""Tests for spike/server.py, using a fake vgamepad module and a fake socket so no driver is needed.

Run: python -m unittest discover -s spike/tests -v
"""

import contextlib
import io
import json
import os
import socket
import sys
import types
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import server  # noqa: E402

CROSS = 1 << 5  # vgamepad's DS4_BUTTONS values
CIRCLE = 1 << 6
PHONE = ("192.168.1.20", 50000)
OTHER_PHONE = ("192.168.1.21", 50001)


def packet(seq, lx=0.0, ly=0.0, a=False, b=False):
    return json.dumps({"seq": seq, "lx": lx, "ly": ly, "a": a, "b": b}).encode()


class FakeClock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now


class FakeVgamepad:
    """The parts of vgamepad server.py uses. Each pad keeps its report and logs it on update().

    fail: the pad constructor raises, as when the driver refuses a pad.
    """

    def __init__(self, fail=False):
        self.fail = fail
        # Each pad's reports, kept apart from the pad so tests can read them after it is freed.
        self.reports = []
        self.live = 0
        self.DS4_BUTTONS = types.SimpleNamespace(DS4_BUTTON_CROSS=CROSS, DS4_BUTTON_CIRCLE=CIRCLE)
        fake = self

        class VDS4Gamepad:
            def __init__(self):
                if fake.fail:
                    raise AssertionError("The virtual device could not connect to ViGEmBus.")
                self.x, self.y, self.buttons = 128, 128, 0
                # The real constructor sends one neutral report.
                self.updates = [(128, 128, 0)]
                fake.reports.append(self.updates)
                fake.live += 1

            def left_joystick(self, x_value, y_value):
                self.x, self.y = x_value, y_value

            def press_button(self, button):
                self.buttons |= button

            def release_button(self, button):
                self.buttons &= ~button

            def update(self):
                self.updates.append((self.x, self.y, self.buttons))

            def __del__(self):
                fake.live -= 1

        self.VDS4Gamepad = VDS4Gamepad


class Gap:
    """A fake socket item: no datagram for this many seconds."""

    def __init__(self, seconds):
        self.seconds = seconds


class FakeSocket:
    """Hands out queued items from recvfrom: (data, address) pairs, Gap or KeyboardInterrupt.

    A datagram moves the clock on by a 60 Hz tick. A Gap, or an empty queue, moves it on and
    times out, as the real socket does after RECEIVE_TIMEOUT_SECONDS.
    """

    TICK = 1 / 60

    def __init__(self, clock, items=(), port=server.DEFAULT_PORT):
        self.clock = clock
        self.items = list(items)
        self.port = port
        self.closed = False

    def recvfrom(self, size):
        if not self.items:
            self.clock.now += server.RECEIVE_TIMEOUT_SECONDS
            raise TimeoutError("timed out")
        item = self.items.pop(0)
        if item is KeyboardInterrupt:
            raise KeyboardInterrupt
        if isinstance(item, Gap):
            self.clock.now += item.seconds
            raise TimeoutError("timed out")
        self.clock.now += self.TICK
        return item

    def close(self):
        self.closed = True


class Out:
    """Stands in for stdout. Keeps each JSON line, and how many fake pads were live as it was written."""

    def __init__(self, module):
        self.module = module
        self.lines = []
        self.live = {}  # event -> live pads when its line was written
        self._line = ""

    def write(self, text):
        self._line += text
        while "\n" in self._line:
            line, self._line = self._line.split("\n", 1)
            fields = json.loads(line)
            self.lines.append(fields)
            self.live[fields["event"]] = getattr(self.module, "live", None)

    def flush(self):
        pass


def run_main(argv, module, items=(), open_error=None, out=None):
    """Run server.main with fakes; return (exit code, stdout lines, stderr text, fake socket, ports opened).

    Pass an Out as `out` to read how many pads were live as each line was written.
    """
    clock = FakeClock()
    sock = FakeSocket(clock, items)
    opened = []

    def open_socket(port):
        if open_error is not None:
            raise open_error
        opened.append(port)
        return sock

    out = Out(module) if out is None else out
    err = io.StringIO()
    with mock.patch.dict(sys.modules, {"vgamepad": module}), contextlib.redirect_stderr(err):
        code = server.main(argv, out=out, clock=clock, open_socket=open_socket)
    return code, out.lines, err.getvalue(), sock, opened


class ParsePacketTests(unittest.TestCase):
    def test_reads_every_field(self):
        seq, state = server.parse_packet(packet(7, lx=0.25, ly=-1.0, a=True, b=False))
        self.assertEqual(seq, 7)
        self.assertEqual(state, server.PadState(lx=0.25, ly=-1.0, a=True, b=False))

    def test_takes_whole_numbers_for_sticks(self):
        _, state = server.parse_packet(b'{"seq": 0, "lx": 1, "ly": -1, "a": false, "b": true}')
        self.assertEqual(state, server.PadState(lx=1.0, ly=-1.0, a=False, b=True))

    def test_ignores_extra_fields(self):
        _, state = server.parse_packet(b'{"seq": 1, "lx": 0, "ly": 0, "a": false, "b": false, "rx": 0.5}')
        self.assertEqual(state, server.NEUTRAL)

    def test_clamps_sticks_to_the_unit_range(self):
        _, state = server.parse_packet(packet(1, lx=1.5, ly=-7))
        self.assertEqual((state.lx, state.ly), (1.0, -1.0))

    def test_rejects_bad_packets(self):
        bad = {
            "not JSON": b"hello",
            "not UTF-8": b"\xff\xfe{}",
            "not an object": b"[1, 2]",
            "missing seq": b'{"lx": 0, "ly": 0, "a": false, "b": false}',
            "missing a stick": b'{"seq": 1, "ly": 0, "a": false, "b": false}',
            "missing a button": b'{"seq": 1, "lx": 0, "ly": 0, "a": false}',
            "negative seq": packet(-1),
            "fractional seq": b'{"seq": 1.5, "lx": 0, "ly": 0, "a": false, "b": false}',
            "bool seq": b'{"seq": true, "lx": 0, "ly": 0, "a": false, "b": false}',
            "string stick": b'{"seq": 1, "lx": "0", "ly": 0, "a": false, "b": false}',
            "bool stick": b'{"seq": 1, "lx": true, "ly": 0, "a": false, "b": false}',
            "NaN stick": b'{"seq": 1, "lx": NaN, "ly": 0, "a": false, "b": false}',
            "infinite stick": b'{"seq": 1, "lx": 0, "ly": -Infinity, "a": false, "b": false}',
            "overflowing stick": b'{"seq": 1, "lx": 1e400, "ly": 0, "a": false, "b": false}',
            # float() raises OverflowError on an integer this large, not ValueError.
            "huge integer stick": b'{"seq": 1, "lx": 1' + b"0" * 400 + b', "ly": 0, "a": false, "b": false}',
            # json raises RecursionError on nesting this deep.
            "deeply nested": b"[" * 100_000,
            "number button": b'{"seq": 1, "lx": 0, "ly": 0, "a": 1, "b": false}',
            "null button": b'{"seq": 1, "lx": 0, "ly": 0, "a": false, "b": null}',
        }
        for name, data in bad.items():
            with self.subTest(name), self.assertRaises(ValueError):
                server.parse_packet(data)


class StickByteTests(unittest.TestCase):
    def test_maps_the_unit_range_onto_every_byte(self):
        self.assertEqual(server.stick_byte(-1.0), 0)
        self.assertEqual(server.stick_byte(0.0), 128)
        self.assertEqual(server.stick_byte(1.0), 255)
        self.assertEqual(server.stick_byte(0.5), 191)

    def test_apply_flips_y_so_up_is_byte_0(self):
        # The phone sends ly = +1 for up; a DualShock 4 report has 0 for up.
        vgamepad = FakeVgamepad()
        pad = vgamepad.VDS4Gamepad()
        server.apply_state(vgamepad, pad, server.PadState(lx=1.0, ly=1.0, a=False, b=False))
        self.assertEqual(pad.updates[-1], (255, 0, 0))
        server.apply_state(vgamepad, pad, server.PadState(lx=-1.0, ly=-1.0, a=False, b=False))
        self.assertEqual(pad.updates[-1], (0, 255, 0))


class ApplyStateTests(unittest.TestCase):
    def test_a_is_cross_and_b_is_circle_and_each_call_sends_one_report(self):
        vgamepad = FakeVgamepad()
        pad = vgamepad.VDS4Gamepad()
        server.apply_state(vgamepad, pad, server.PadState(0.0, 0.0, a=True, b=False))
        server.apply_state(vgamepad, pad, server.PadState(0.0, 0.0, a=True, b=True))
        server.apply_state(vgamepad, pad, server.PadState(0.0, 0.0, a=False, b=True))
        server.apply_state(vgamepad, pad, server.NEUTRAL)
        self.assertEqual(pad.updates[1:], [(128, 128, CROSS), (128, 128, CROSS | CIRCLE),
                                           (128, 128, CIRCLE), (128, 128, 0)])


class ReceiverTests(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.vgamepad = FakeVgamepad()
        self.pad = self.vgamepad.VDS4Gamepad()
        self.lines = []
        self.receiver = server.Receiver(self.vgamepad, self.pad, lambda **f: self.lines.append(f), self.clock)

    def test_first_packet_announces_the_sender_and_moves_the_pad(self):
        self.receiver.on_datagram(packet(1, lx=1.0), PHONE)
        self.assertEqual(self.lines, [{"event": "sender", "address": "192.168.1.20:50000"}])
        self.assertEqual(self.pad.updates[-1], (255, 128, 0))

    def test_drops_old_and_repeated_seq(self):
        self.receiver.on_datagram(packet(5, lx=1.0), PHONE)
        self.receiver.on_datagram(packet(5, lx=-1.0), PHONE)
        self.receiver.on_datagram(packet(3, lx=-1.0), PHONE)
        self.receiver.on_datagram(packet(6, a=True), PHONE)
        self.assertEqual(self.pad.updates[1:], [(255, 128, 0), (128, 128, CROSS)])
        self.assertEqual(self.receiver.counts, {"received": 4, "applied": 2, "stale": 2, "bad": 0})

    def test_bad_packets_are_counted_and_change_nothing(self):
        for data in (b"junk", b"[" * 100_000, b'{"seq": 1, "lx": 1' + b"0" * 400 + b', "ly": 0, "a": false, "b": false}'):
            self.receiver.on_datagram(data, PHONE)
        self.assertEqual(self.pad.updates, [(128, 128, 0)])
        self.assertEqual(self.lines, [])  # a bad packet doesn't make its sender the phone
        self.assertEqual(self.receiver.counts, {"received": 3, "applied": 0, "stale": 0, "bad": 3})

    def test_a_new_sender_takes_over_with_its_own_seq(self):
        self.receiver.on_datagram(packet(900), PHONE)
        self.receiver.on_datagram(packet(1, b=True), OTHER_PHONE)
        self.assertEqual(self.pad.updates[-1], (128, 128, CIRCLE))
        self.assertEqual(self.lines[-1], {"event": "sender", "address": "192.168.1.21:50001"})
        # The first phone's packets now come from a stranger again, so they take back over.
        self.receiver.on_datagram(packet(901, a=True), PHONE)
        self.assertEqual(self.pad.updates[-1], (128, 128, CROSS))

    def test_silence_sets_the_pad_neutral_once(self):
        self.receiver.on_datagram(packet(1, lx=1.0, a=True), PHONE)
        self.clock.now += server.NEUTRAL_AFTER_SECONDS - 0.01
        self.receiver.check_silence()
        self.assertEqual(self.pad.updates[-1], (255, 128, CROSS))
        self.clock.now += 0.01
        self.receiver.check_silence()
        self.receiver.check_silence()
        self.assertEqual(self.pad.updates[1:], [(255, 128, CROSS), (128, 128, 0)])
        self.assertEqual(self.lines[-1], {"event": "neutral", "silent_seconds": server.NEUTRAL_AFTER_SECONDS})

    def test_no_neutral_before_the_first_packet(self):
        self.clock.now += 10
        self.receiver.check_silence()
        self.assertEqual(self.pad.updates, [(128, 128, 0)])
        self.assertEqual(self.lines, [])

    def test_after_silence_the_sender_may_restart_its_seq(self):
        self.receiver.on_datagram(packet(500), PHONE)
        self.clock.now += server.NEUTRAL_AFTER_SECONDS
        self.receiver.check_silence()
        self.receiver.on_datagram(packet(0, ly=1.0), PHONE)
        self.assertEqual(self.pad.updates[-1], (128, 0, 0))


class MainTests(unittest.TestCase):
    def test_runs_for_the_given_seconds_then_removes_the_pad(self):
        vgamepad = FakeVgamepad()
        items = [(packet(1, lx=1.0), PHONE), (packet(2, a=True), PHONE), (b"junk", PHONE),
                 (packet(2), PHONE), Gap(0.6)]
        out = Out(vgamepad)
        code, lines, err, sock, opened = run_main(["--seconds", "2"], vgamepad, items, out=out)
        self.assertEqual(code, server.EXIT_OK, err)
        self.assertEqual(opened, [server.DEFAULT_PORT])
        self.assertEqual([line["event"] for line in lines], ["ready", "sender", "neutral", "stats", "removed"])
        self.assertEqual(lines[0], {"event": "ready", "port": server.DEFAULT_PORT, "pad": "ds4"})
        self.assertEqual(lines[3], {"event": "stats", "received": 4, "applied": 2, "stale": 1, "bad": 1})
        self.assertEqual(vgamepad.reports[0][1:], [(255, 128, 0), (128, 128, CROSS), (128, 128, 0)])
        # The pad is held until the stats line, and gone before "removed" is printed.
        self.assertEqual(out.live["stats"], 1)
        self.assertEqual(out.live["removed"], 0)
        self.assertTrue(sock.closed)

    def test_listens_on_the_given_port(self):
        code, lines, _, _, opened = run_main(["--port", "50123", "--seconds", "0.1"], FakeVgamepad())
        self.assertEqual(code, server.EXIT_OK)
        self.assertEqual(opened, [50123])
        self.assertEqual(lines[0]["port"], 50123)

    def test_ctrl_c_still_removes_the_pad(self):
        vgamepad = FakeVgamepad()
        out = Out(vgamepad)
        code, lines, _, sock, _ = run_main([], vgamepad, [(packet(1, b=True), PHONE), KeyboardInterrupt], out=out)
        self.assertEqual(code, server.EXIT_OK)
        self.assertEqual([line["event"] for line in lines][-2:], ["stats", "removed"])
        self.assertEqual(out.live["stats"], 1)
        self.assertEqual(out.live["removed"], 0)
        self.assertTrue(sock.closed)

    def test_port_in_use(self):
        vgamepad = FakeVgamepad()
        error = OSError(10048, "Only one usage of each socket address is normally permitted")
        code, lines, err, _, _ = run_main(["--seconds", "1"], vgamepad, open_error=error)
        self.assertEqual(code, server.EXIT_PORT_IN_USE)
        self.assertIn("UDP port 47821", err)
        self.assertEqual(lines, [])
        self.assertEqual(vgamepad.reports, [])  # no pad is plugged in when there is no port

    def test_driver_refuses_the_pad(self):
        code, lines, err, sock, _ = run_main(["--seconds", "1"], FakeVgamepad(fail=True))
        self.assertEqual(code, server.EXIT_DRIVER_FAILED)
        self.assertIn("ViGEmBus could not create a pad", err)
        self.assertEqual(lines, [])
        self.assertTrue(sock.closed)

    def test_vgamepad_not_installed(self):
        # A None entry in sys.modules makes `import vgamepad` raise ModuleNotFoundError.
        code, lines, err, _, opened = run_main(["--seconds", "1"], None)
        self.assertEqual(code, server.EXIT_NO_VGAMEPAD)
        self.assertEqual(lines, [])
        self.assertEqual(opened, [])
        self.assertIn("vgamepad is not installed", err)

    def test_driver_connect_failure_on_import(self):
        # vgamepad connects to the driver at import, so a missing driver fails the import itself.
        real_import = __import__

        def failing_import(name, *args, **kwargs):
            if name == "vgamepad":
                raise OSError("ViGEmBus is not installed")
            return real_import(name, *args, **kwargs)

        with mock.patch("builtins.__import__", failing_import):
            code, lines, err, _, opened = run_main(["--seconds", "1"], types.ModuleType("vgamepad"))
        self.assertEqual(code, server.EXIT_DRIVER_FAILED)
        self.assertEqual(lines, [])
        self.assertEqual(opened, [])
        self.assertIn("ViGEmBus is not installed", err)

    def test_usage_errors(self):
        for argv in (["--port", "0"], ["--port", "70000"], ["--seconds", "0"], ["--seconds", "-1"]):
            with self.subTest(argv), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
                server.main(argv)
            self.assertEqual(caught.exception.code, 2)


class OpenSocketTests(unittest.TestCase):
    def test_receives_a_datagram_and_times_out_when_idle(self):
        # Loopback only, so no firewall prompt.
        sock = server.open_socket(0, host="127.0.0.1")
        try:
            port = sock.getsockname()[1]
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                sender.sendto(packet(1), ("127.0.0.1", port))
                data, _ = sock.recvfrom(server.MAX_DATAGRAM_BYTES)
            self.assertEqual(server.parse_packet(data)[0], 1)
            with self.assertRaises(TimeoutError):
                sock.recvfrom(server.MAX_DATAGRAM_BYTES)
        finally:
            sock.close()

    def test_a_taken_port_raises(self):
        first = server.open_socket(0, host="127.0.0.1")
        try:
            with self.assertRaises(OSError):
                server.open_socket(first.getsockname()[1], host="127.0.0.1")
        finally:
            first.close()


if __name__ == "__main__":
    unittest.main()
