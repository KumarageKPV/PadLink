"""Spike (PLAN.md 1.2): create one mode's set of virtual pads with vgamepad, hold them, remove them.

    python spike/pads.py --mode mixed --count 8 [--seconds 10]     4 Xbox 360 pads, then 4 DualShock 4
    python spike/pads.py --mode all_ds4 --count 8 [--seconds 10]   8 DualShock 4 pads

Slots follow docs/design.md, "Virtual controller layer": in mixed mode slots 1-4 are Xbox 360
pads, created first and in slot order, and slots 5-16 are DualShock 4; in all_ds4 mode every slot
is DualShock 4. Check the pads with tools/padprobe.py while they're held. Ctrl+C at any point
stops early, and the pads made so far are still removed.

stdout carries one JSON line per event: "created" (slot and type), "failed" (the slot the driver
refused), "xinput" (an Xbox pad's XInput user index, read back two ways), "ready" (the pads now
held), "removed".
Exit codes: 0 every pad created and removed, or stopped by Ctrl+C; 2 usage error; 3 the driver
could not be reached, or created fewer pads than asked (those it created are still held, then
removed); 4 vgamepad is not installed.
"""

import argparse
import ctypes
import gc
import json
import sys
import time

EXIT_OK = 0
EXIT_DRIVER_FAILED = 3
EXIT_NO_VGAMEPAD = 4

MODES = ("mixed", "all_ds4")
MAX_PADS = 16  # docs/design.md: the ceiling in both modes
XBOX_SLOTS = 4  # mixed mode's Xbox 360 slots, 1-4
DEFAULT_SECONDS = 10.0
VIGEM_ERROR_NONE = 0x20000000  # ViGEmClient's success code
# On the dev PC each Xbox pad's first LED notification came within 20 ms of plugging it in
# (a one-off check, spike/RESULTS.md).
LED_WAIT_SECONDS = 2.0
POLL_SECONDS = 0.01


def plan_pads(mode, count):
    """Return each slot's pad type, slot 1 first."""
    xbox = min(count, XBOX_SLOTS) if mode == "mixed" else 0
    return ["x360"] * xbox + ["ds4"] * (count - xbox)


def led_recorder(leds, slot):
    """Return a notification callback that keeps the slot's latest player LED number in leds."""
    # vgamepad accepts only a callback with exactly these parameter names.
    def on_notification(client, target, large_motor, small_motor, led_number, user_data):
        leds[slot] = led_number
    return on_notification


def driver_user_index(vgamepad, pad):
    """Return the XInput user index the driver call reports for an Xbox 360 pad, or None on an error.

    vgamepad wraps the call but doesn't expose it on the pad, so this uses its private fields.
    """
    index = ctypes.c_ulong()
    error = vgamepad.win.vigem_client.vigem_target_x360_get_user_index(pad._busp, pad._devicep,
                                                                        ctypes.pointer(index))
    return index.value if error == VIGEM_ERROR_NONE else None


def create_pads(vgamepad, plan, emit, leds, pads):
    """Plug in a pad per slot in order, adding each to pads, and stop at the first the driver refuses.

    Each Xbox pad reports its player LED number into leds. Pads go into the caller's list as they
    are made, so a Ctrl+C partway still leaves them for remove_pads. Returns the refusal's
    message, or None if there was none.
    """
    constructors = {"x360": vgamepad.VX360Gamepad, "ds4": vgamepad.VDS4Gamepad}
    for slot, kind in enumerate(plan, start=1):
        try:
            # The constructor plugs the pad in and sends one neutral report.
            pad = constructors[kind]()
            if kind == "x360":
                pad.register_notification(led_recorder(leds, slot))
        except Exception as exc:
            # A pad that was made but couldn't register is freed, so unplugged, when this returns.
            emit(event="failed", slot=slot, type=kind, error=str(exc))
            return str(exc)
        pads.append(pad)
        emit(event="created", slot=slot, type=kind)
    return None


def report_xinput(vgamepad, pads, plan, leds, emit, clock, sleep):
    """Emit each Xbox pad's XInput user index: the driver call's, and the player LED number's.

    On the dev PC the driver call gave 0 for every pad while the LED numbers gave 0-3, so both go
    out (spike/RESULTS.md). The LED number arrives in a notification, so wait for every Xbox pad's.
    """
    slots = [slot for slot, kind in enumerate(plan[:len(pads)], start=1) if kind == "x360"]
    deadline = clock() + LED_WAIT_SECONDS
    while any(slot not in leds for slot in slots) and clock() < deadline:
        sleep(POLL_SECONDS)
    for slot in slots:
        emit(event="xinput", slot=slot, user_index=driver_user_index(vgamepad, pads[slot - 1]),
             led_number=leds.get(slot))


def remove_pads(pads, plan):
    """Unplug every pad and return how many there were."""
    count = len(pads)
    for pad, kind in zip(pads, plan):
        if kind == "x360":
            # Stop the driver's notifications before their callback's pad is freed.
            pad.unregister_notification()
    # vgamepad has no explicit unplug; freeing the object removes the pad from the bus.
    pads.clear()
    gc.collect()
    return count


def pad_count(text):
    count = int(text)
    if not 1 <= count <= MAX_PADS:
        raise argparse.ArgumentTypeError(f"must be 1 to {MAX_PADS}")
    return count


def positive_seconds(text):
    seconds = float(text)
    if not seconds > 0:
        raise argparse.ArgumentTypeError("must be above 0")
    return seconds


def parse_args(argv):
    parser = argparse.ArgumentParser(description="Create one mode's virtual pads, hold them, then remove them.")
    parser.add_argument("--mode", required=True, choices=MODES,
                        help="mixed: Xbox 360 for slots 1-4, DualShock 4 after; all_ds4: DualShock 4 for every slot")
    parser.add_argument("--count", required=True, type=pad_count, help=f"pads to create, 1 to {MAX_PADS}")
    parser.add_argument("--seconds", type=positive_seconds, default=DEFAULT_SECONDS,
                        help=f"how long to hold the pads (default {DEFAULT_SECONDS:g})")
    return parser.parse_args(argv)


def main(argv=None, out=None, clock=time.monotonic, sleep=time.sleep):
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

    plan = plan_pads(args.mode, args.count)
    pads = []
    leds = {}  # slot -> player LED number, written by the driver's notification thread
    error = None
    try:
        error = create_pads(vgamepad, plan, emit, leds, pads)
        if pads:
            report_xinput(vgamepad, pads, plan, leds, emit, clock, sleep)
            created = plan[:len(pads)]
            emit(event="ready", mode=args.mode, asked=args.count, x360=created.count("x360"),
                 ds4=created.count("ds4"), seconds=args.seconds)
            sleep(args.seconds)
    except KeyboardInterrupt:
        pass
    finally:
        held = remove_pads(pads, plan)
        emit(event="removed", count=held)

    if error is not None:
        print(f"ViGEmBus created {held} of {args.count} pads: {error}", file=sys.stderr)
        return EXIT_DRIVER_FAILED
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
