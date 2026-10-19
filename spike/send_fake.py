"""Spike (PLAN.md 1.3): play a fixed set of moves to spike/server.py, as a phone would send them.

    python spike/send_fake.py [--host 127.0.0.1] [--port 47821] [--rate 60] [--step-seconds 0.5]

Each step holds one state for --step-seconds, sending it --rate times a second: neutral, stick
right, left, up, down, then A and B each pressed and released. Watch the pad move with
`python tools/padprobe.py --watch` while it runs.

The packets are encoded here, not with server.py's code, so they test the wire format the way the
phone app's do. stdout carries one JSON line per step, then "done" with the packet count.
Exit codes: 0 every packet sent; 2 usage error.
"""

import argparse
import json
import socket
import sys
import time
from typing import NamedTuple

EXIT_OK = 0

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 47821  # docs/design.md: UDP input port
DEFAULT_RATE = 60
MAX_RATE = 1000
DEFAULT_STEP_SECONDS = 0.5


class State(NamedTuple):
    lx: float
    ly: float
    a: bool
    b: bool


NEUTRAL = State(lx=0.0, ly=0.0, a=False, b=False)
STEPS = [
    ("neutral", NEUTRAL),
    ("right", NEUTRAL._replace(lx=1.0)),
    ("left", NEUTRAL._replace(lx=-1.0)),
    ("up", NEUTRAL._replace(ly=1.0)),
    ("down", NEUTRAL._replace(ly=-1.0)),
    ("neutral", NEUTRAL),
    ("a", NEUTRAL._replace(a=True)),
    ("neutral", NEUTRAL),
    ("b", NEUTRAL._replace(b=True)),
    ("neutral", NEUTRAL),
]


def encode(seq, state):
    return json.dumps({"seq": seq, "lx": state.lx, "ly": state.ly, "a": state.a, "b": state.b}).encode()


def play(sock, address, rate, step_seconds, emit, clock, sleep):
    """Send every step's packets, the n-th at start + n / rate. Returns how many were sent."""
    per_step = max(1, round(step_seconds * rate))
    start = clock()
    seq = 0
    for name, state in STEPS:
        emit(event="step", name=name, t=round(seq / rate, 3), **state._asdict())
        for _ in range(per_step):
            # Pace from the start time, so sleep's overshoot doesn't add up over the run.
            sleep(max(0.0, start + seq / rate - clock()))
            sock.sendto(encode(seq, state), address)
            seq += 1
    return seq


def port_number(text):
    port = int(text)
    if not 1 <= port <= 65535:
        raise argparse.ArgumentTypeError("must be 1 to 65535")
    return port


def rate_hz(text):
    rate = int(text)
    if not 1 <= rate <= MAX_RATE:
        raise argparse.ArgumentTypeError(f"must be 1 to {MAX_RATE}")
    return rate


def positive_seconds(text):
    seconds = float(text)
    if not seconds > 0:
        raise argparse.ArgumentTypeError("must be above 0")
    return seconds


def parse_args(argv):
    parser = argparse.ArgumentParser(description="Play a fixed set of moves to spike/server.py over UDP.")
    parser.add_argument("--host", default=DEFAULT_HOST, help=f"server address (default {DEFAULT_HOST})")
    parser.add_argument("--port", type=port_number, default=DEFAULT_PORT,
                        help=f"server UDP port (default {DEFAULT_PORT})")
    parser.add_argument("--rate", type=rate_hz, default=DEFAULT_RATE,
                        help=f"packets a second (default {DEFAULT_RATE})")
    parser.add_argument("--step-seconds", type=positive_seconds, default=DEFAULT_STEP_SECONDS,
                        help=f"how long each step lasts (default {DEFAULT_STEP_SECONDS:g})")
    return parser.parse_args(argv)


def main(argv=None, out=None, clock=time.monotonic, sleep=time.sleep,
         make_socket=lambda: socket.socket(socket.AF_INET, socket.SOCK_DGRAM)):
    args = parse_args(argv)
    out = sys.stdout if out is None else out

    def emit(**fields):
        print(json.dumps(fields), file=out, flush=True)

    sock = make_socket()
    try:
        sent = play(sock, (args.host, args.port), args.rate, args.step_seconds, emit, clock, sleep)
    finally:
        sock.close()
    emit(event="done", sent=sent)
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
