"""Check that ViGEmBus works: create one virtual Xbox 360 pad, then remove it.

Exit codes: 0 the driver created and removed a pad, 2 vgamepad is not installed,
3 the driver could not create a pad (reason on stderr). Used by tools/doctor.ps1.
"""

import gc
import sys

EXIT_OK = 0
EXIT_NO_VGAMEPAD = 2
EXIT_DRIVER_FAILED = 3


def main():
    try:
        # vgamepad connects to the ViGEmBus driver when it is imported.
        import vgamepad
    except ModuleNotFoundError as exc:
        if exc.name == "vgamepad":
            print("vgamepad is not installed", file=sys.stderr)
            return EXIT_NO_VGAMEPAD
        print(f"ViGEmBus could not create a pad: {exc}", file=sys.stderr)
        return EXIT_DRIVER_FAILED
    except Exception as exc:
        print(f"ViGEmBus could not create a pad: {exc}", file=sys.stderr)
        return EXIT_DRIVER_FAILED

    try:
        # The constructor plugs the pad in and sends one neutral report.
        pad = vgamepad.VX360Gamepad()
        # vgamepad has no explicit unplug; freeing the object removes the pad from the bus.
        del pad
        gc.collect()
    except Exception as exc:
        print(f"ViGEmBus could not create a pad: {exc}", file=sys.stderr)
        return EXIT_DRIVER_FAILED

    print("ok: created and removed one virtual Xbox 360 pad")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
