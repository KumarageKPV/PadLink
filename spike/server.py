"""Spike (PLAN.md 1.3): drive one DualShock 4 pad from JSON packets sent over UDP.

    python spike/server.py [--port 47821] [--seconds N]

Each datagram is one JSON object with the phone's whole state:

    {"seq": 42, "lx": 0.25, "ly": -1.0, "a": true, "b": false}

seq is a whole number that rises with every packet. lx and ly are the left stick from -1 to 1,
with ly = 1 for up; values past the range are clamped. a and b are DS4 Cross and Circle. Extra
fields are ignored. No pairing or security: this is spike code, and only the M0 gate reads it.

The pad follows one phone at a time: the first good packet's sender, until a good packet comes
from another address. Packets whose seq isn't above the last one applied are dropped. After
NEUTRAL_AFTER_SECONDS without a packet the pad goes neutral, so a closed app can't leave a stick
held, and the sender may then restart its seq.

stdout carries one JSON line per event: "ready" (listening, pad plugged in), "sender" (a new
phone), "neutral" (silence), "stats" (packet counts, at exit), "removed".
Exit codes: 0 stopped by --seconds or Ctrl+C; 2 usage error; 3 the driver could not be reached
or refused the pad; 4 vgamepad is not installed; 5 the UDP port could not be opened.
"""

import argparse
import gc
import json
import math
import socket
import sys
import time
from typing import NamedTuple

EXIT_OK = 0
EXIT_DRIVER_FAILED = 3
EXIT_NO_VGAMEPAD = 4
EXIT_PORT_IN_USE = 5

DEFAULT_PORT = 47821  # docs/design.md: UDP input port
BIND_HOST = "0.0.0.0"  # every interface, so a phone on the LAN reaches it
# The largest UDP payload. A smaller buffer makes Windows raise on a longer datagram.
MAX_DATAGRAM_BYTES = 65535
RECEIVE_TIMEOUT_SECONDS = 0.05
NEUTRAL_AFTER_SECONDS = 0.5


class PadState(NamedTuple):
    lx: float
    ly: float
    a: bool
    b: bool


NEUTRAL = PadState(lx=0.0, ly=0.0, a=False, b=False)


def _reject_constant(name):
    raise ValueError(f"{name} is not a number")


def _stick(fields, name):
    value = fields.get(name)
    # bool is an int subclass, so true would otherwise pass as 1.
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(f"{name} must be a number")
    try:
        value = float(value)
    except OverflowError:  # an integer literal too large for a float
        raise ValueError(f"{name} is out of range") from None
    if not math.isfinite(value):
        raise ValueError(f"{name} must be finite")
    return min(1.0, max(-1.0, value))


def _button(fields, name):
    value = fields.get(name)
    if not isinstance(value, bool):
        raise ValueError(f"{name} must be true or false")
    return value


def parse_packet(data):
    """Return (seq, PadState) from one datagram, or raise ValueError if it isn't a valid packet."""
    # Bad UTF-8 and bad JSON both raise ValueError subclasses. NaN and Infinity are rejected here;
    # a decimal too large for a float becomes inf, which _stick rejects. Any one datagram must
    # end in ValueError, or a single bad packet from the LAN would stop the server.
    try:
        fields = json.loads(data, parse_constant=_reject_constant)
    except RecursionError:
        raise ValueError("nested too deeply") from None
    if not isinstance(fields, dict):
        raise ValueError("not a JSON object")
    seq = fields.get("seq")
    if isinstance(seq, bool) or not isinstance(seq, int) or seq < 0:
        raise ValueError("seq must be a whole number of 0 or more")
    return seq, PadState(lx=_stick(fields, "lx"), ly=_stick(fields, "ly"),
                         a=_button(fields, "a"), b=_button(fields, "b"))


def stick_byte(value):
    """Map -1..1 onto a DS4 report's stick byte, 0..255 with 128 at rest."""
    return round((value + 1) * 127.5)


def apply_state(vgamepad, pad, state):
    """Send one report with the state. A DS4 report has 0 for up, so Y is flipped."""
    pad.left_joystick(x_value=stick_byte(state.lx), y_value=stick_byte(-state.ly))
    buttons = vgamepad.DS4_BUTTONS
    for pressed, button in ((state.a, buttons.DS4_BUTTON_CROSS), (state.b, buttons.DS4_BUTTON_CIRCLE)):
        if pressed:
            pad.press_button(button=button)
        else:
            pad.release_button(button=button)
    pad.update()


class Receiver:
    """Applies packets from the current phone to the pad, and sets it neutral when they stop."""

    def __init__(self, vgamepad, pad, emit, clock):
        self.vgamepad = vgamepad
        self.pad = pad
        self.emit = emit
        self.clock = clock
        self.sender = None
        self.last_seq = None  # None: the next good packet from the sender is applied whatever its seq
        self.last_applied = None  # clock time of the last packet applied; None once neutral
        self.counts = {"received": 0, "applied": 0, "stale": 0, "bad": 0}

    def on_datagram(self, data, address):
        self.counts["received"] += 1
        try:
            seq, state = parse_packet(data)
        except ValueError:
            self.counts["bad"] += 1
            return
        if address != self.sender:
            self.sender = address
            self.last_seq = None
            self.emit(event="sender", address=f"{address[0]}:{address[1]}")
        elif self.last_seq is not None and seq <= self.last_seq:
            self.counts["stale"] += 1
            return
        self.last_seq = seq
        apply_state(self.vgamepad, self.pad, state)
        self.counts["applied"] += 1
        self.last_applied = self.clock()

    def check_silence(self):
        if self.last_applied is None or self.clock() - self.last_applied < NEUTRAL_AFTER_SECONDS:
            return
        apply_state(self.vgamepad, self.pad, NEUTRAL)
        self.last_applied = None
        self.last_seq = None
        self.emit(event="neutral", silent_seconds=NEUTRAL_AFTER_SECONDS)


def serve(sock, receiver, clock, seconds):
    """Feed datagrams to the receiver until `seconds` pass, or forever when None."""
    deadline = None if seconds is None else clock() + seconds
    while deadline is None or clock() < deadline:
        try:
            data, address = sock.recvfrom(MAX_DATAGRAM_BYTES)
        except TimeoutError:
            pass
        else:
            receiver.on_datagram(data, address)
        receiver.check_silence()


def open_socket(port, host=BIND_HOST):
    """Return a UDP socket bound to host:port whose recvfrom times out. Raises OSError if taken."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.bind((host, port))
    except OSError:
        sock.close()
        raise
    # The timeout lets silence be noticed, and Ctrl+C land, while no packets come.
    sock.settimeout(RECEIVE_TIMEOUT_SECONDS)
    return sock


def port_number(text):
    port = int(text)
    if not 1 <= port <= 65535:
        raise argparse.ArgumentTypeError("must be 1 to 65535")
    return port


def positive_seconds(text):
    seconds = float(text)
    if not seconds > 0:
        raise argparse.ArgumentTypeError("must be above 0")
    return seconds


def parse_args(argv):
    parser = argparse.ArgumentParser(description="Drive one DualShock 4 pad from JSON packets over UDP.")
    parser.add_argument("--port", type=port_number, default=DEFAULT_PORT,
                        help=f"UDP port to listen on (default {DEFAULT_PORT})")
    parser.add_argument("--seconds", type=positive_seconds, help="stop after this many seconds (default: Ctrl+C)")
    return parser.parse_args(argv)


def main(argv=None, out=None, clock=time.monotonic, open_socket=open_socket):
    args = parse_args(argv)
    out = sys.stdout if out is None else out

    def emit(**fields):
        print(json.dumps(fields), file=out, flush=True)

    try:
        # vgamepad connects to the ViGEmBus driver when it is imported.
        import vgamepad
    except ModuleNotFoundError as exc:
        if exc.name == "vgamepad":
            print("vgamepad is not installed; use the repo's .venv (README.md, \"Python tools\")", file=sys.stderr)
            return EXIT_NO_VGAMEPAD
        print(f"ViGEmBus could not create a pad: {exc}", file=sys.stderr)
        return EXIT_DRIVER_FAILED
    except Exception as exc:
        print(f"ViGEmBus could not create a pad: {exc}", file=sys.stderr)
        return EXIT_DRIVER_FAILED

    try:
        sock = open_socket(args.port)
    except OSError as exc:
        print(f"could not listen on UDP port {args.port}: {exc}", file=sys.stderr)
        return EXIT_PORT_IN_USE
    try:
        try:
            # The constructor plugs the pad in and sends one neutral report.
            pad = vgamepad.VDS4Gamepad()
        except Exception as exc:
            print(f"ViGEmBus could not create a pad: {exc}", file=sys.stderr)
            return EXIT_DRIVER_FAILED
        receiver = Receiver(vgamepad, pad, emit, clock)
        emit(event="ready", port=args.port, pad="ds4")
        try:
            serve(sock, receiver, clock, args.seconds)
        except KeyboardInterrupt:
            pass
        finally:
            emit(event="stats", **receiver.counts)
            # vgamepad has no explicit unplug; freeing the object removes the pad from the bus.
            receiver.pad = None
            del pad
            gc.collect()
            emit(event="removed", count=1)
    finally:
        sock.close()
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
