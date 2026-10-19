"""List the game controllers Windows exposes, as JSON, using pygame's joystick API (SDL).

Every later pad check uses this tool (PLAN.md 1.1), so pads can be verified without anyone
watching a screen.

    python tools/padprobe.py                          JSON list of every pad
    python tools/padprobe.py --expect "x360=4,ds4=4"  same list; exit 1 unless those counts match
    python tools/padprobe.py --watch [--seconds N]    one JSON line per change, until Ctrl+C or N s
    python tools/padprobe.py --check-driver           exit 0 only if ViGEmBus can create a pad

A pad's type comes from the USB IDs in its SDL GUID: x360 for 045E:028E (what ViGEmBus's Xbox 360
pads report), ds4 for Sony's DualShock 4 IDs, other for everything else.

The list and --expect count every Xbox pad, even past the 4 that XInput games see, so leftover
pads fail a check. --watch reads Xbox pads through XInput, which shows their stick moves on time
but lists at most 4. SDL_JOYSTICK_RAWINPUT=1 or 0 picks either way for any mode (docs/decisions.md).

Exit codes: 0 ok, 1 --expect counts differ, 2 usage error, 3 --check-driver failed,
4 pygame is not installed. Messages go to stderr, so stdout carries only JSON.
"""

import argparse
import gc
import json
import os
import re
import sys
import time

EXIT_OK = 0
EXIT_MISMATCH = 1
EXIT_DRIVER_FAILED = 3
EXIT_NO_PYGAME = 4

PAD_TYPES = ("x360", "ds4", "other")
X360_IDS = {(0x045E, 0x028E)}
# DualShock 4 v1 (also ViGEmBus's DS4 pads), v2, and the wireless adapter.
DS4_IDS = {(0x054C, 0x05C4), (0x054C, 0x09CC), (0x054C, 0x0BA0)}

DRIVER_PATH = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "drivers", "ViGEmBus.sys")
# The ViGEmBus 1.22.0 setup installs driver file 1.21.442.0 (docs/decisions.md).
DRIVER_MINIMUM = (1, 21, 442, 0)
CHECK_TIMEOUT_SECONDS = 5.0
POLL_SECONDS = 0.01


def usb_ids(guid):
    """Return (vendor, product) from an SDL joystick GUID, or None when it holds no USB IDs.

    SDL packs the GUID as eight little-endian 16-bit words: bus, CRC, vendor, 0, product, 0,
    version, driver. Older GUIDs, such as XInput's "xinput" text, have no zero words there.
    """
    try:
        raw = bytes.fromhex(guid)
    except ValueError:
        return None
    if len(raw) != 16:
        return None
    words = [int.from_bytes(raw[i:i + 2], "little") for i in range(0, 16, 2)]
    if words[3] or words[5] or not words[2]:
        return None
    return words[2], words[4]


def pad_type(ids):
    if ids in X360_IDS:
        return "x360"
    if ids in DS4_IDS:
        return "ds4"
    return "other"


def describe(joystick, index):
    guid = joystick.get_guid()
    ids = usb_ids(guid)
    return {
        "index": index,
        "instance_id": joystick.get_instance_id(),
        "name": joystick.get_name(),
        "guid": guid,
        "type": pad_type(ids),
        "vendor_id": f"{ids[0]:04x}" if ids else None,
        "product_id": f"{ids[1]:04x}" if ids else None,
        "buttons": joystick.get_numbuttons(),
        "axes": joystick.get_numaxes(),
        "hats": joystick.get_numhats(),
    }


def list_pads(pygame):
    return [describe(pygame.joystick.Joystick(index), index) for index in range(pygame.joystick.get_count())]


def count_types(pads):
    counts = dict.fromkeys(PAD_TYPES, 0)
    for pad in pads:
        counts[pad["type"]] += 1
    return counts


def format_counts(counts):
    return ",".join(f"{name}={count}" for name, count in counts.items())


def parse_expect(text):
    """Parse "x360=4,ds4=4" into {"x360": 4, "ds4": 4}. argparse turns errors into exit 2."""
    expected = {}
    for item in text.split(","):
        match = re.fullmatch(r"(\w+)=(\d+)", item.strip(), re.ASCII)
        if not match or match[1] not in PAD_TYPES:
            raise argparse.ArgumentTypeError(f"{item!r} is not <type>=<count> with a type from {', '.join(PAD_TYPES)}")
        if match[1] in expected:
            raise argparse.ArgumentTypeError(f"{match[1]} is given twice")
        expected[match[1]] = int(match[2])
    return expected


def check_expect(pads, expected):
    found = count_types(pads)
    actual = {name: found[name] for name in expected}
    if actual != expected:
        print(f"expected {format_counts(expected)}, found {format_counts(actual)}", file=sys.stderr)
        return EXIT_MISMATCH
    print(f"ok: {format_counts(actual)}", file=sys.stderr)
    return EXIT_OK


def watch(pygame, seconds=None, out=None, clock=time.monotonic, sleep=time.sleep):
    """Print one JSON line per change to `out` (stdout) until `seconds` pass, or forever when None.

    Pads already connected arrive first, as "added" lines. Axis values are rounded to 2 places and
    printed only when the rounded value changes, so stick noise doesn't flood the output.
    """
    out = sys.stdout if out is None else out
    # SDL's joystick events need its video and event system, though no window is opened.
    pygame.display.init()
    start = clock()
    opened = {}  # instance_id -> Joystick: SDL sends input events only for open joysticks
    axes = {}  # (instance_id, axis) -> last value printed

    def emit(**fields):
        print(json.dumps({"t": round(clock() - start, 3), **fields}), file=out, flush=True)

    def add(index):
        joystick = pygame.joystick.Joystick(index)
        if joystick.get_instance_id() not in opened:
            opened[joystick.get_instance_id()] = joystick
            emit(event="added", pad=describe(joystick, index))

    # SDL drops the added events of pads already connected when its joystick system starts before
    # the video system, as it does here, so list those pads instead (found in PLAN.md 1.3).
    for index in range(pygame.joystick.get_count()):
        add(index)
    while seconds is None or clock() - start < seconds:
        for event in pygame.event.get():
            if event.type == pygame.JOYDEVICEADDED:
                add(event.device_index)
            elif event.type == pygame.JOYDEVICEREMOVED:
                opened.pop(event.instance_id, None)
                emit(event="removed", instance_id=event.instance_id)
            elif event.type in (pygame.JOYBUTTONDOWN, pygame.JOYBUTTONUP):
                emit(event="button", instance_id=event.instance_id, button=event.button,
                     pressed=event.type == pygame.JOYBUTTONDOWN)
            elif event.type == pygame.JOYAXISMOTION:
                value = round(event.value, 2) + 0.0  # + 0.0 turns -0.0 into 0.0
                if axes.get((event.instance_id, event.axis)) != value:
                    axes[(event.instance_id, event.axis)] = value
                    emit(event="axis", instance_id=event.instance_id, axis=event.axis, value=value)
            elif event.type == pygame.JOYHATMOTION:
                emit(event="hat", instance_id=event.instance_id, hat=event.hat, value=list(event.value))
        sleep(POLL_SECONDS)


def read_driver_version(path):
    """Return a file's version as a 4-tuple, or None when the file or its version is missing."""
    if sys.platform != "win32":
        return None
    import ctypes
    from ctypes import wintypes

    class FixedFileInfo(ctypes.Structure):
        # The first fields of VS_FIXEDFILEINFO; the rest aren't needed.
        _fields_ = [("signature", wintypes.DWORD), ("struc_version", wintypes.DWORD),
                    ("file_version_ms", wintypes.DWORD), ("file_version_ls", wintypes.DWORD)]

    version_dll = ctypes.WinDLL("version")
    size = version_dll.GetFileVersionInfoSizeW(path, None)
    if not size:
        return None
    data = ctypes.create_string_buffer(size)
    if not version_dll.GetFileVersionInfoW(path, 0, size, data):
        return None
    info = ctypes.POINTER(FixedFileInfo)()
    length = wintypes.UINT()
    if not version_dll.VerQueryValueW(data, "\\", ctypes.byref(info), ctypes.byref(length)) or not length.value:
        return None
    high, low = info.contents.file_version_ms, info.contents.file_version_ls
    return high >> 16, high & 0xFFFF, low >> 16, low & 0xFFFF


def format_version(version):
    return ".".join(str(part) for part in version)


def check_driver(pygame, clock=time.monotonic, sleep=time.sleep, timeout=CHECK_TIMEOUT_SECONDS):
    """Exit 0 only if the ViGEmBus 1.22.0 driver creates a DualShock 4 pad that SDL then lists.

    DualShock 4, so the result doesn't depend on how SDL reads Xbox pads: through XInput, a fifth
    one would stay hidden.
    """
    def fail(reason):
        print(reason, file=sys.stderr)
        return EXIT_DRIVER_FAILED

    version = read_driver_version(DRIVER_PATH)
    if version is None:
        return fail(f"{DRIVER_PATH} not found: winget install --id ViGEm.ViGEmBus -e (as admin)")
    if version < DRIVER_MINIMUM:
        return fail(f"ViGEmBus driver {format_version(version)} is older than {format_version(DRIVER_MINIMUM)} "
                    "(the 1.22.0 setup): winget install --id ViGEm.ViGEmBus -e --force (as admin)")
    try:
        # vgamepad connects to the ViGEmBus driver when it is imported.
        import vgamepad
    except ModuleNotFoundError as exc:
        if exc.name == "vgamepad":
            return fail("vgamepad is not installed; use the repo's .venv (README.md, \"Python tools\")")
        return fail(f"ViGEmBus could not create a pad: {exc}")
    except Exception as exc:
        return fail(f"ViGEmBus could not create a pad: {exc}")

    # Pumping events is what makes SDL notice new pads, and it needs the video system.
    pygame.display.init()
    before = count_types(list_pads(pygame))["ds4"]
    try:
        # The constructor plugs the pad in and sends one neutral report.
        pad = vgamepad.VDS4Gamepad()
    except Exception as exc:
        return fail(f"ViGEmBus could not create a pad: {exc}")
    try:
        deadline = clock() + timeout
        while clock() < deadline:
            pygame.event.pump()
            if count_types(list_pads(pygame))["ds4"] > before:
                print(f"ok: ViGEmBus driver {format_version(version)} created a DualShock 4 pad and the probe saw it",
                      file=sys.stderr)
                return EXIT_OK
            sleep(POLL_SECONDS)
        return fail(f"ViGEmBus created a DualShock 4 pad, but the probe never saw it within {timeout:g} s")
    finally:
        # vgamepad has no explicit unplug; freeing the object removes the pad from the bus.
        del pad
        gc.collect()


def positive_seconds(text):
    seconds = float(text)
    if not seconds > 0:
        raise argparse.ArgumentTypeError("must be above 0")
    return seconds


def parse_args(argv):
    parser = argparse.ArgumentParser(description="List the game controllers Windows exposes, as JSON.")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--watch", action="store_true",
                      help="print one JSON line per change: pad added or removed, button, axis, hat")
    mode.add_argument("--check-driver", action="store_true",
                      help="exit 0 only if ViGEmBus 1.22.0 creates a pad that the probe sees")
    mode.add_argument("--expect", type=parse_expect, metavar="COUNTS",
                      help='exit 1 unless the pad counts match, e.g. "x360=4,ds4=4" (types: x360, ds4, other)')
    parser.add_argument("--seconds", type=positive_seconds, help="with --watch, stop after this many seconds")
    args = parser.parse_args(argv)
    if args.seconds is not None and not args.watch:
        parser.error("--seconds needs --watch")
    return args


def import_pygame(watching):
    """Import pygame without its banner, which would break the JSON on stdout. None if missing."""
    os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")
    if watching:
        # SDL's default RAWINPUT driver delivered Xbox pads' stick moves late on the dev PC.
        # Without it, Xbox pads come through XInput, which also caps them at 4.
        os.environ.setdefault("SDL_JOYSTICK_RAWINPUT", "0")
    try:
        import pygame
    except ModuleNotFoundError as exc:
        if exc.name == "pygame":
            return None
        raise
    pygame.joystick.init()
    return pygame


def main(argv=None):
    args = parse_args(argv)
    pygame = import_pygame(args.watch)
    if pygame is None:
        print("pygame is not installed; use the repo's .venv (README.md, \"Python tools\")", file=sys.stderr)
        return EXIT_NO_PYGAME
    try:
        if args.watch:
            try:
                watch(pygame, args.seconds)
            except KeyboardInterrupt:
                pass
            return EXIT_OK
        if args.check_driver:
            return check_driver(pygame)
        pads = list_pads(pygame)
        print(json.dumps(pads, indent=2))
        if args.expect is not None:
            return check_expect(pads, args.expect)
        return EXIT_OK
    finally:
        pygame.quit()


if __name__ == "__main__":
    sys.exit(main())
