"""Tests for spike/send_fake.py: its packets must be ones spike/server.py applies.

Run: python -m unittest discover -s spike/tests -v
"""

import contextlib
import io
import json
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import send_fake  # noqa: E402
import server  # noqa: E402


class FakeTime:
    """A clock that only sleep() moves."""

    def __init__(self):
        self.now = 100.0

    def clock(self):
        return self.now

    def sleep(self, seconds):
        assert seconds >= 0
        self.now += seconds


class RecordingSocket:
    def __init__(self, fake_time):
        self.fake_time = fake_time
        self.sent = []  # (time, data, address)
        self.closed = False

    def sendto(self, data, address):
        self.sent.append((self.fake_time.now, data, address))
        return len(data)

    def close(self):
        self.closed = True


def run_main(argv):
    fake_time = FakeTime()
    sock = RecordingSocket(fake_time)
    out = io.StringIO()
    code = send_fake.main(argv, out=out, clock=fake_time.clock, sleep=fake_time.sleep, make_socket=lambda: sock)
    lines = [json.loads(line) for line in out.getvalue().splitlines()]
    return code, lines, sock, fake_time


class EncodeTests(unittest.TestCase):
    def test_server_reads_back_what_was_encoded(self):
        data = send_fake.encode(9, server.PadState(lx=-0.5, ly=1.0, a=True, b=False))
        self.assertEqual(server.parse_packet(data), (9, server.PadState(lx=-0.5, ly=1.0, a=True, b=False)))


class StepsTests(unittest.TestCase):
    def test_moves_each_stick_direction_and_presses_each_button_then_ends_neutral(self):
        states = [state for _, state in send_fake.STEPS]
        for wanted in (dict(lx=1.0), dict(lx=-1.0), dict(ly=1.0), dict(ly=-1.0), dict(a=True), dict(b=True)):
            with self.subTest(wanted):
                self.assertIn(server.NEUTRAL._replace(**wanted), states)
        self.assertEqual(states[0], server.NEUTRAL)
        self.assertEqual(states[-1], server.NEUTRAL)

    def test_every_press_is_followed_by_a_release(self):
        states = [state for _, state in send_fake.STEPS]
        for index, state in enumerate(states):
            if state.a or state.b:
                self.assertEqual(states[index + 1], server.NEUTRAL)


class MainTests(unittest.TestCase):
    def test_sends_every_step_at_the_rate_to_the_address(self):
        code, lines, sock, _ = run_main(["--host", "10.0.0.5", "--port", "50000", "--rate", "20",
                                         "--step-seconds", "0.5"])
        self.assertEqual(code, send_fake.EXIT_OK)
        per_step = 10  # 20 Hz for 0.5 s
        self.assertEqual(len(sock.sent), per_step * len(send_fake.STEPS))
        self.assertEqual({address for _, _, address in sock.sent}, {("10.0.0.5", 50000)})
        self.assertTrue(sock.closed)

        decoded = [server.parse_packet(data) for _, data, _ in sock.sent]
        self.assertEqual([seq for seq, _ in decoded], list(range(len(decoded))))
        for index, (_, state) in enumerate(send_fake.STEPS):
            with self.subTest(step=index):
                self.assertEqual({s for _, s in decoded[index * per_step:(index + 1) * per_step]}, {state})

    def test_paces_packets_from_the_start_time(self):
        _, _, sock, _ = run_main(["--rate", "20", "--step-seconds", "0.5"])
        times = [sent_at - 100.0 for sent_at, _, _ in sock.sent]
        for index, sent_at in enumerate(times):
            self.assertAlmostEqual(sent_at, index / 20)

    def test_prints_each_step_then_done(self):
        _, lines, sock, _ = run_main(["--rate", "20", "--step-seconds", "0.5"])
        steps = [line for line in lines if line["event"] == "step"]
        self.assertEqual([line["name"] for line in steps], [name for name, _ in send_fake.STEPS])
        self.assertEqual(steps[1]["t"], 0.5)
        self.assertEqual(lines[-1], {"event": "done", "sent": len(sock.sent)})

    def test_defaults_to_the_local_server_at_60_hz(self):
        _, _, sock, fake_time = run_main([])
        self.assertEqual(sock.sent[0][2], ("127.0.0.1", server.DEFAULT_PORT))
        self.assertEqual(len(sock.sent), round(send_fake.DEFAULT_STEP_SECONDS * 60) * len(send_fake.STEPS))

    def test_usage_errors(self):
        for argv in (["--rate", "0"], ["--rate", "1001"], ["--port", "0"], ["--step-seconds", "0"]):
            with self.subTest(argv), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as caught:
                send_fake.main(argv)
            self.assertEqual(caught.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
