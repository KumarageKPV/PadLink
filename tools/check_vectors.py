"""Check protocol/vectors/ against tools/gen_vectors.py and protocol/SPEC.md.

    python tools/check_vectors.py [--vectors DIR] [--spec PATH]

Three checks, each listing every problem it finds:
1. Fresh: every file the generator writes exists with the same text, and the folder holds no
   other .json file. Line endings don't count, since git may check the files out as CRLF.
2. Consistent: the reference codec decodes every valid vector to what it states, ignores every
   ignored frame, and rejects every invalid vector for its stated reason. Keys, nonces and MACs
   agree across files.
3. Covered: every field in SPEC.md's input packet, feedback packet and control message tables
   appears in at least one valid vector, and every vector field appears in those tables. The
   button bit table matches input.json's buttonBits.

Exit codes: 0 ok, 1 a check failed, 2 usage error.
"""

import argparse
import base64
import difflib
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import gen_vectors as gv  # noqa: E402

SPEC_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "protocol", "SPEC.md")
DIFF_LINES = 20
DIFF_LINE_CHARS = 160
PACKET_TABLES = (("input.json", "Input packet"), ("feedback.json", "Feedback packet"))
BUTTON_TABLE = "Button bits"
CONTROL_TABLE = "Control messages"


def check_fresh(vectors_dir, files):
    problems = []
    for name, expected in files.items():
        try:
            with open(os.path.join(vectors_dir, name), encoding="utf-8") as file:  # text mode reads CRLF as LF
                actual = file.read()
        except FileNotFoundError:
            problems.append(f"{name}: missing; run python tools/gen_vectors.py")
            continue
        except ValueError:
            problems.append(f"{name}: not UTF-8; run python tools/gen_vectors.py")
            continue
        if actual != expected:
            diff = difflib.unified_diff(
                expected.splitlines(), actual.splitlines(), f"generated {name}", name, lineterm="", n=1
            )
            shown = [line[:DIFF_LINE_CHARS] for line in list(diff)[:DIFF_LINES]]
            problems.append(f"{name}: differs from what tools/gen_vectors.py writes; run it\n" + "\n".join(shown))
    for path in sorted(glob.glob(os.path.join(vectors_dir, "*.json"))):
        name = os.path.basename(path)
        if name not in files:
            problems.append(f"{name}: not written by tools/gen_vectors.py; delete it or generate it there")
    return problems


def _expect_rejected(problems, where, decode, data, reason):
    try:
        decode(data)
    except gv.DecodeError as error:
        if error.reason != reason:
            problems.append(f"{where}: rejected for {error.reason}, not {reason}")
    else:
        problems.append(f"{where}: accepted, but should be rejected for {reason}")


def _check_packets(problems, name, vectors, decode):
    key = bytes.fromhex(vectors["sessionKey"])
    for vector in vectors["valid"]:
        where = f'{name} valid "{vector["name"]}"'
        try:
            fields = decode(bytes.fromhex(vector["hex"]), key)
        except gv.DecodeError as error:
            problems.append(f"{where}: rejected for {error.reason}")
            continue
        if fields != vector["fields"]:
            problems.append(f"{where}: decodes to {fields}")
        elif "pressed" in vector and gv.pressed(fields["buttons"]) != vector["pressed"]:
            problems.append(f"{where}: pressed should be {gv.pressed(fields['buttons'])}")
    for vector in vectors["invalid"]:
        where = f'{name} invalid "{vector["name"]}"'
        _expect_rejected(problems, where, lambda data: decode(data, key), bytes.fromhex(vector["hex"]), vector["reason"])
    for case in vectors.get("sequenceOrder", []):
        if gv.sequence_newer(case["last"], case["sequence"]) != case["newer"]:
            problems.append(f"{name} sequenceOrder {case['last']} then {case['sequence']}: newer should be "
                            f"{not case['newer']}")


def _check_control(problems, vectors):
    for vector in vectors["valid"]:
        where = f'control.json valid "{vector["name"]}"'
        data = bytes.fromhex(vector["frameHex"])
        if data[gv.FRAME_HEADER.size :] != vector["json"].encode("utf-8"):
            problems.append(f"{where}: frameHex doesn't hold json")
        try:
            message = gv.decode_frame(data)
        except gv.DecodeError as error:
            problems.append(f"{where}: rejected for {error.reason}")
            continue
        if message != vector["message"]:
            problems.append(f"{where}: decodes to {message}")
            continue
        try:
            gv.frame(message)
        except ValueError as error:
            problems.append(f"{where}: can't be sent: {error}")
    for vector in vectors["ignored"]:
        where = f'control.json ignored "{vector["name"]}"'
        try:
            if gv.decode_frame(bytes.fromhex(vector["frameHex"])) is not None:
                problems.append(f"{where}: decoded, but should be ignored")
        except gv.DecodeError as error:
            problems.append(f"{where}: rejected for {error.reason}, but should be ignored")
    for vector in vectors["invalid"]:
        where = f'control.json invalid "{vector["name"]}"'
        _expect_rejected(problems, where, gv.decode_frame, bytes.fromhex(vector["frameHex"]), vector["reason"])


def _check_mapping(problems, vectors):
    for stick in vectors["sticks"]:
        value = stick["value"]
        expected = {"value": value, "x360": value, "ds4X": gv.ds4_stick_x(value), "ds4Y": gv.ds4_stick_y(value)}
        if stick != expected:
            problems.append(f"mapping.json stick {value}: should be {expected}")
    for entry in vectors["dpad"]:
        buttons = entry["buttons"]
        if entry["pressed"] != gv.pressed(buttons) or entry["ds4Hat"] != gv.ds4_hat(buttons):
            problems.append(f"mapping.json dpad {buttons:#06x}: should be {gv.pressed(buttons)}, {gv.ds4_hat(buttons)}")


def _check_crypto(problems, data):
    crypto = data["crypto.json"]
    hex_fields = ("sharedSecret", "proof", "pairingKey", "pcNonce", "phoneNonce", "authMac", "sessionKey")
    raw = {name: bytes.fromhex(crypto[name]) for name in hex_fields}
    pc_private, pc_public = bytes.fromhex(crypto["pc"]["privateKey"]), bytes.fromhex(crypto["pc"]["publicKey"])
    phone_private, phone_public = bytes.fromhex(crypto["phone"]["privateKey"]), bytes.fromhex(crypto["phone"]["publicKey"])
    pairing = raw["pairingKey"]
    expected = {
        "pc publicKey": (gv.x25519_public(pc_private), pc_public),
        "phone publicKey": (gv.x25519_public(phone_private), phone_public),
        "sharedSecret from the PC": (gv.x25519_shared(pc_private, phone_public), raw["sharedSecret"]),
        "sharedSecret from the phone": (gv.x25519_shared(phone_private, pc_public), raw["sharedSecret"]),
        "proof": (gv.pairing_proof(crypto["pairingCode"], pc_public, phone_public), raw["proof"]),
        "pairingKey": (gv.pairing_key(raw["sharedSecret"], pc_public, phone_public), pairing),
        "authMac": (gv.auth_mac(pairing, raw["pcNonce"]), raw["authMac"]),
        "sessionKey": (gv.session_key(pairing, raw["pcNonce"], raw["phoneNonce"]), raw["sessionKey"]),
        "input.json sessionKey": (raw["sessionKey"], bytes.fromhex(data["input.json"]["sessionKey"])),
        "feedback.json sessionKey": (raw["sessionKey"], bytes.fromhex(data["feedback.json"]["sessionKey"])),
        "packetMac": (
            gv.packet_mac(raw["sessionKey"], bytes.fromhex(crypto["packetMac"]["data"])),
            bytes.fromhex(crypto["packetMac"]["mac"]),
        ),
    }
    # Control messages carry the same values, as base64.
    b64 = {
        ("hello", "nonce"): raw["phoneNonce"],
        ("pair_required", "key"): pc_public,
        ("pair_required", "nonce"): raw["pcNonce"],
        ("pair", "key"): phone_public,
        ("pair", "proof"): raw["proof"],
        ("challenge", "nonce"): raw["pcNonce"],
        ("auth", "mac"): raw["authMac"],
    }
    for vector in data["control.json"]["valid"]:
        message = vector["message"]
        for field, value in message.items():
            if (message["type"], field) in b64:
                name = f'control.json "{vector["name"]}" {field}'
                expected[name] = (b64[(message["type"], field)], base64.b64decode(value))
    for name, (computed, stated) in expected.items():
        if computed != stated:
            problems.append(f"crypto.json {name}: {stated.hex()} should be {computed.hex()}")
    for entry in crypto["rejectedPublicKeys"]:
        try:
            gv.x25519_shared(pc_private, bytes.fromhex(entry["publicKey"]))
        except gv.CryptoError:
            continue
        problems.append(f'crypto.json rejectedPublicKeys "{entry["name"]}": accepted')


def check_consistent(data):
    problems = []
    _check_packets(problems, "input.json", data["input.json"], gv.decode_input)
    _check_packets(problems, "feedback.json", data["feedback.json"], gv.decode_feedback)
    _check_control(problems, data["control.json"])
    _check_mapping(problems, data["mapping.json"])
    _check_crypto(problems, data)
    return problems


def spec_table(text, heading):
    """The first table under the '## <heading>' or '### <heading>' line, as one {column: cell} dict
    per row, or None."""
    match = re.search(rf"^###? {re.escape(heading)}\b", text, re.MULTILINE)
    if match is None:
        return None
    rows = []
    for line in text[match.end() :].splitlines()[1:]:
        if line.startswith("|"):
            rows.append([cell.strip() for cell in line.strip().strip("|").split("|")])
        elif rows or line.startswith("#"):
            break
    if len(rows) < 3:  # header, separator, then at least one row
        return None
    return [dict(zip(rows[0], row)) for row in rows[2:]]


def _names(cell):
    return re.findall(r"`([^`]+)`", cell)


def check_covered(data, spec_text):
    """Returns (problems, counts), counts being how many SPEC.md names each table has."""
    problems, counts = [], {}
    for file, heading in PACKET_TABLES:
        rows = spec_table(spec_text, heading)
        if not rows or "Field" not in rows[0]:
            problems.append(f"SPEC.md: no table with a Field column under '## {heading}'")
            continue
        spec_fields = {name for row in rows for name in _names(row["Field"])}
        vector_fields = {name for vector in data[file]["valid"] for name in vector["fields"]}
        problems += [f"SPEC.md {heading} field `{name}`: in no valid vector in {file}"
                     for name in sorted(spec_fields - vector_fields)]
        problems += [f"{file} field `{name}`: not in SPEC.md's {heading} table"
                     for name in sorted(vector_fields - spec_fields)]
        counts[heading] = len(spec_fields)

    rows = spec_table(spec_text, BUTTON_TABLE)
    if not rows or "Bit" not in rows[0] or "Name" not in rows[0]:
        problems.append(f"SPEC.md: no table with Bit and Name columns under '### {BUTTON_TABLE}'")
    else:
        spec_bits = {name: int(row["Bit"]) for row in rows for name in _names(row["Name"])}
        vector_bits = data["input.json"]["buttonBits"]
        if spec_bits != vector_bits:
            problems.append(f"SPEC.md {BUTTON_TABLE} {spec_bits} differ from input.json buttonBits {vector_bits}")
        counts[BUTTON_TABLE] = len(spec_bits)

    rows = spec_table(spec_text, CONTROL_TABLE)
    if not rows or "Message" not in rows[0] or "Fields" not in rows[0]:
        problems.append(f"SPEC.md: no table with Message and Fields columns under '## {CONTROL_TABLE}'")
        return problems, counts
    spec_messages = {message: set(_names(row["Fields"])) for row in rows for message in _names(row["Message"])}
    vector_messages = {}
    for vector in data["control.json"]["valid"]:
        message = vector["message"]
        vector_messages.setdefault(message["type"], set()).update(name for name in message if name != "type")
    for message, fields in sorted(spec_messages.items()):
        if message not in vector_messages:
            problems.append(f"SPEC.md control message `{message}`: in no valid vector in control.json")
            continue
        problems += [f"SPEC.md control message `{message}` field `{name}`: in no valid `{message}` vector"
                     for name in sorted(fields - vector_messages[message])]
    for message, fields in sorted(vector_messages.items()):
        if message not in spec_messages:
            problems.append(f"control.json message `{message}`: not in SPEC.md's {CONTROL_TABLE} table")
            continue
        problems += [f"control.json `{message}` field `{name}`: not in SPEC.md's {CONTROL_TABLE} table"
                     for name in sorted(fields - spec_messages[message])]
    counts[CONTROL_TABLE] = (len(spec_messages), len(set().union(*spec_messages.values())))
    return problems, counts


def main(argv=None):
    parser = argparse.ArgumentParser(description="Check PadLink's protocol vectors against the generator and SPEC.md.")
    parser.add_argument("--vectors", default=gv.VECTORS_DIR, help="vector folder (default: protocol/vectors)")
    parser.add_argument("--spec", default=SPEC_PATH, help="spec file (default: protocol/SPEC.md)")
    args = parser.parse_args(argv)

    files = gv.generate()
    data = {name: json.loads(text) for name, text in files.items()}
    try:
        with open(args.spec, encoding="utf-8") as file:
            spec_text = file.read()
    except OSError as error:
        print(f"FAIL can't read the spec: {error}", file=sys.stderr)
        return 1

    problems = check_fresh(args.vectors, files) + check_consistent(data)
    coverage, counts = check_covered(data, spec_text)
    problems += coverage
    if problems:
        for problem in problems:
            print(f"FAIL {problem}", file=sys.stderr)
        print(f"{len(problems)} problem(s)", file=sys.stderr)
        return 1

    def count(kind):
        return sum(len(data[name].get(kind, [])) for name in data)

    messages, fields = counts[CONTROL_TABLE]
    print(f"ok  fresh: {', '.join(files)} match tools/gen_vectors.py")
    print(f"ok  consistent: {count('valid')} valid, {count('ignored')} ignored and {count('invalid')} invalid "
          "vectors decode as stated; mapping and crypto values agree across files")
    print(f"ok  covered: every SPEC.md table field is in a vector ({counts['Input packet']} input packet, "
          f"{counts['Feedback packet']} feedback packet, {fields} fields in {messages} control messages); "
          f"the {counts[BUTTON_TABLE]} button bits match")
    return 0


if __name__ == "__main__":
    sys.exit(main())
