"""Reference codec for the PadLink wire protocol, and the generator of its shared test vectors.

    python tools/gen_vectors.py [--out DIR]   write the vectors to protocol/vectors/ (or DIR)

protocol/SPEC.md is the contract. This file is a plain, slow, readable implementation of it.
The C# (pc/) and Dart (mobile/) protocol code is tested against the vectors it writes, and
tools/check_vectors.py regenerates them and fails on any difference. To change the protocol,
edit SPEC.md and this file, regenerate, and commit the vectors with both.

The vectors use fixed keys and nonces so they never change unless the protocol does: the X25519
keys are RFC 7748's Alice (PC) and Bob (phone).

Exit codes: 0 ok, 2 usage error.
"""

import argparse
import base64
import hashlib
import hmac
import json
import os
import re
import struct
import sys

from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

VECTORS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "protocol", "vectors")

MAGIC = 0x50
VERSION = 1
TYPE_INPUT = 0x01
TYPE_FEEDBACK = 0x02
SLOT_COUNT = 16
MAC_SIZE = 8
KEY_SIZE = 32
NONCE_SIZE = 16
MAX_FRAME_BYTES = 4096
FRAME_HEADER = struct.Struct("<I")

# Everything before the mac, in wire order.
INPUT_BODY = struct.Struct("<BBBBIHhhhhBBH")
INPUT_FIELDS = ("magic", "version", "type", "slot", "sequence", "buttons", "lx", "ly", "rx", "ry", "lt", "rt", "sentMs")
FEEDBACK_BODY = struct.Struct("<BBBBBBBBBH")
FEEDBACK_FIELDS = ("magic", "version", "type", "slot", "largeMotor", "smallMotor", "red", "green", "blue", "echoMs")

BUTTON_BITS = {
    name: bit
    for bit, name in enumerate((
        "faceBottom", "faceRight", "faceLeft", "faceTop", "shoulderLeft", "shoulderRight", "back", "start", "guide",
        "stickLeft", "stickRight", "dpadUp", "dpadDown", "dpadLeft", "dpadRight", "reserved",
    ))
}
DPAD_UP = 1 << BUTTON_BITS["dpadUp"]
DPAD_DOWN = 1 << BUTTON_BITS["dpadDown"]
DPAD_LEFT = 1 << BUTTON_BITS["dpadLeft"]
DPAD_RIGHT = 1 << BUTTON_BITS["dpadRight"]

# DualShock 4 hat: (vertical, horizontal) with 1 for up or right, -1 for down or left.
HAT_NEUTRAL = 8
HAT_DIRECTIONS = {(1, 0): 0, (1, 1): 1, (0, 1): 2, (-1, 1): 3, (-1, 0): 4, (-1, -1): 5, (0, -1): 6, (1, -1): 7}

PAIRING_LABEL = b"padlink/1 pairing"
SESSION_LABEL = b"padlink/1 session"
PAIRING_CODE = re.compile(r"[0-9]{6}")

# Fixed inputs for the vectors. RFC 7748 section 6.1: Alice's and Bob's private keys.
PC_PRIVATE = bytes.fromhex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
PHONE_PRIVATE = bytes.fromhex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb")
CODE = "042917"  # a leading zero, so a code read as a number fails
PC_NONCE = bytes(range(0xA0, 0xB0))
PHONE_NONCE = bytes(range(0xB0, 0xC0))
DEVICE_ID = "5b0e8f0c-6a8e-4d0a-9b8e-2f4c1d7e9a31"
STICK_VALUES = (-32768, -32767, -257, -256, -255, -1, 0, 1, 255, 256, 32767)
# (last accepted sequence or None, new sequence)
SEQUENCE_CASES = (
    (None, 0), (None, 0xFFFFFFFF), (5, 6), (5, 1000), (5, 5), (5, 4), (0xFFFFFFFE, 0xFFFFFFFF), (0xFFFFFFFF, 0),
    (0xFFFFFFFF, 0x7FFFFFFE), (0xFFFFFFFF, 0x7FFFFFFF), (0, 0xFFFFFFFF), (0, 0x7FFFFFFF), (0, 0x80000000),
)


class DecodeError(ValueError):
    """A packet or frame that breaks protocol/SPEC.md. reason is the vectors' name for the fault."""

    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


class CryptoError(ValueError):
    pass


# --- Keys and MACs ---


def x25519_public(private):
    return X25519PrivateKey.from_private_bytes(private).public_key().public_bytes_raw()


def x25519_shared(private, peer_public):
    """The X25519 shared secret. Raises CryptoError for a peer key that gives all zero bytes."""
    try:
        shared = X25519PrivateKey.from_private_bytes(private).exchange(X25519PublicKey.from_public_bytes(peer_public))
    except ValueError as error:  # OpenSSL refuses an all-zero result itself
        raise CryptoError(f"X25519 failed: {error}") from None
    if shared == bytes(len(shared)):
        raise CryptoError("X25519 shared secret is all zero")
    return shared


def _hkdf(ikm, info):
    # salt=None is HKDF's default of HashLen zero bytes, the same as an empty salt.
    return HKDF(algorithm=hashes.SHA256(), length=KEY_SIZE, salt=None, info=info).derive(ikm)


def pairing_proof(code, pc_public, phone_public):
    if not isinstance(code, str) or not PAIRING_CODE.fullmatch(code):
        raise ValueError("the pairing code must be 6 digits")
    return hmac.new(code.encode("ascii"), pc_public + phone_public, hashlib.sha256).digest()


def pairing_key(shared, pc_public, phone_public):
    return _hkdf(shared, PAIRING_LABEL + pc_public + phone_public)


def auth_mac(pairing, pc_nonce):
    return hmac.new(pairing, pc_nonce, hashlib.sha256).digest()


def session_key(pairing, pc_nonce, phone_nonce):
    return _hkdf(pairing, SESSION_LABEL + pc_nonce + phone_nonce)


def packet_mac(session, data):
    return hmac.new(session, data, hashlib.sha256).digest()[:MAC_SIZE]


# --- UDP packets ---


def _encode_packet(body_struct, names, defaults, fields, key):
    values = {**defaults, **fields}
    body = body_struct.pack(*(values[name] for name in names))
    return body + packet_mac(key, body)


def _decode_packet(body_struct, names, packet_type, data, key):
    if len(data) != body_struct.size + MAC_SIZE:
        raise DecodeError("length")
    body, mac = data[: body_struct.size], data[body_struct.size :]
    fields = dict(zip(names, body_struct.unpack(body)))
    if fields["magic"] != MAGIC:
        raise DecodeError("magic")
    if fields["version"] != VERSION:
        raise DecodeError("version")
    if fields["type"] != packet_type:
        raise DecodeError("type")
    if fields["slot"] >= SLOT_COUNT:
        raise DecodeError("slot")
    if not hmac.compare_digest(mac, packet_mac(key, body)):
        raise DecodeError("mac")
    fields["mac"] = mac.hex()
    return fields


def encode_input(fields, key):
    """30-byte input packet. fields holds every field but mac; magic, version and type default."""
    defaults = {"magic": MAGIC, "version": VERSION, "type": TYPE_INPUT}
    return _encode_packet(INPUT_BODY, INPUT_FIELDS, defaults, fields, key)


def decode_input(data, key):
    """The packet's fields, with mac as hex. Raises DecodeError."""
    return _decode_packet(INPUT_BODY, INPUT_FIELDS, TYPE_INPUT, data, key)


def encode_feedback(fields, key):
    """19-byte feedback packet, like encode_input."""
    defaults = {"magic": MAGIC, "version": VERSION, "type": TYPE_FEEDBACK}
    return _encode_packet(FEEDBACK_BODY, FEEDBACK_FIELDS, defaults, fields, key)


def decode_feedback(data, key):
    return _decode_packet(FEEDBACK_BODY, FEEDBACK_FIELDS, TYPE_FEEDBACK, data, key)


def pressed(buttons):
    """Names of the bits set in a buttons field, in bit order."""
    return [name for name, bit in BUTTON_BITS.items() if buttons & (1 << bit)]


def sequence_newer(last, sequence):
    """Whether a packet's sequence is newer than the last one accepted (None before the first).

    Serial-number order (RFC 1982): newer when it is 1 to 2^31 - 1 ahead, modulo 2^32, so the
    u32 wraps from 0xFFFFFFFF to 0.
    """
    return last is None or 1 <= (sequence - last) % 2**32 < 2**31


# --- Mapping to pad reports ---


def ds4_stick_x(value):
    """int16 stick X (right positive) to a DualShock 4 byte: 0 left, 128 centre, 255 right."""
    return (value + 32768) >> 8


def ds4_stick_y(value):
    """int16 stick Y (up positive) to a DualShock 4 byte, which reports up as 0."""
    return (min(-value, 32767) + 32768) >> 8


def ds4_hat(buttons):
    """The d-pad bits as a DualShock 4 hat value; opposite directions cancel."""
    vertical = bool(buttons & DPAD_UP) - bool(buttons & DPAD_DOWN)
    horizontal = bool(buttons & DPAD_RIGHT) - bool(buttons & DPAD_LEFT)
    return HAT_DIRECTIONS.get((vertical, horizontal), HAT_NEUTRAL)


# --- TCP control messages ---


def _integer(low, high):
    return lambda value: isinstance(value, int) and not isinstance(value, bool) and low <= value <= high


def _one_of(*options):
    return lambda value: isinstance(value, str) and value in options


def _text(value):
    return isinstance(value, str) and value != ""


def _base64_bytes(size):
    def check(value):
        if not isinstance(value, str):
            return False
        try:
            return len(base64.b64decode(value, validate=True)) == size
        except ValueError:  # binascii.Error, or non-ASCII text
            return False

    return check


def _colour(value):
    return isinstance(value, str) and re.fullmatch(r"#[0-9A-Fa-f]{6}", value) is not None


_SLOT = _integer(0, SLOT_COUNT - 1)
_MODE = _one_of("mixed", "all_ds4")
_PAD_TYPE = _one_of("x360", "ds4")

# Each message's fields in sending order, with the check a receiver applies to each.
MESSAGES = {
    "hello": {
        "proto": _integer(1, 2**31 - 1),
        "deviceId": _text,
        "deviceName": _text,
        "platform": _one_of("android", "ios"),
        "appVersion": _text,
        "nonce": _base64_bytes(NONCE_SIZE),
    },
    "pair_required": {"key": _base64_bytes(KEY_SIZE), "nonce": _base64_bytes(NONCE_SIZE)},
    "pair": {"key": _base64_bytes(KEY_SIZE), "proof": _base64_bytes(32)},
    "challenge": {"nonce": _base64_bytes(NONCE_SIZE)},
    "auth": {"mac": _base64_bytes(32)},
    "welcome": {"slot": _SLOT, "colour": _colour, "mode": _MODE, "padType": _PAD_TYPE, "udpPort": _integer(1, 65535)},
    "mode_changed": {"mode": _MODE},
    "slot_changed": {"slot": _SLOT, "colour": _colour, "padType": _PAD_TYPE},
    "ping": {},
    "pong": {},
    "battery": {"pct": _integer(0, 100)},
    "bye": {},
    "error": {"code": _text},
}


def _reject_constant(name):
    raise ValueError(f"{name} is not JSON")


def decode_frame(data):
    """Decode the control frame at the start of data.

    Returns the message with only the fields its type defines, or None for a type this version
    doesn't know (receivers ignore it). Raises DecodeError.
    """
    if len(data) < FRAME_HEADER.size:
        raise DecodeError("incomplete")
    (length,) = FRAME_HEADER.unpack_from(data)
    if length == 0:
        raise DecodeError("frame_empty")
    if length > MAX_FRAME_BYTES:
        raise DecodeError("frame_too_long")
    if len(data) < FRAME_HEADER.size + length:
        raise DecodeError("incomplete")
    payload = data[FRAME_HEADER.size : FRAME_HEADER.size + length]
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError:
        raise DecodeError("utf8") from None
    try:
        value = json.loads(text, parse_constant=_reject_constant)
    except ValueError:
        raise DecodeError("json") from None
    if not isinstance(value, dict):
        raise DecodeError("not_object")
    kind = value.get("type")
    if not isinstance(kind, str):
        raise DecodeError("type")
    checks = MESSAGES.get(kind)
    if checks is None:
        return None
    message = {"type": kind}
    for name, valid in checks.items():
        if name not in value:
            raise DecodeError("field_missing")
        if not valid(value[name]):
            raise DecodeError("field_invalid")
        message[name] = value[name]
    return message


def frame(message):
    """A control frame: u32 little-endian length, then compact UTF-8 JSON. Raises ValueError for a
    message a receiver would reject or ignore."""
    payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    framed = FRAME_HEADER.pack(len(payload)) + payload
    try:
        decoded = decode_frame(framed)
    except DecodeError as error:
        raise ValueError(f"not a valid message ({error.reason}): {message}") from None
    if decoded != message:
        raise ValueError(f"not a version {VERSION} message: {message}")
    return framed


def _raw_frame(payload):
    return FRAME_HEADER.pack(len(payload)) + payload


# --- Vectors ---


def _b64(data):
    return base64.b64encode(data).decode("ascii")


def _crypto_values():
    pc_public = x25519_public(PC_PRIVATE)
    phone_public = x25519_public(PHONE_PRIVATE)
    shared = x25519_shared(PC_PRIVATE, phone_public)
    pairing = pairing_key(shared, pc_public, phone_public)
    return {
        "pcPublic": pc_public,
        "phonePublic": phone_public,
        "shared": shared,
        "proof": pairing_proof(CODE, pc_public, phone_public),
        "pairingKey": pairing,
        "authMac": auth_mac(pairing, PC_NONCE),
        "sessionKey": session_key(pairing, PC_NONCE, PHONE_NONCE),
    }


# The input packet most vectors build on: a few buttons, sticks off centre, a trigger half down.
_TYPICAL_INPUT = {
    "slot": 7, "sequence": 1234567, "buttons": 0x2821, "lx": 12345, "ly": -23456, "rx": -300, "ry": 4000,
    "lt": 128, "rt": 1, "sentMs": 54321,
}
_TYPICAL_FEEDBACK = {
    "slot": 7, "largeMotor": 200, "smallMotor": 40, "red": 0x1E, "green": 0x90, "blue": 0xFF, "echoMs": 54321,
}


def _packet_vectors(names, defaults, encode, valid_cases, typical, key, wrong_key, other_type):
    def fields_of(packet, values):
        fields = {name: {**defaults, **values}[name] for name in names}
        fields["mac"] = packet[-MAC_SIZE:].hex()
        return fields

    valid = []
    for name, overrides in valid_cases:
        values = {**{field: 0 for field in names if field not in defaults}, **overrides}
        packet = encode(values, key)
        vector = {"name": name, "hex": packet.hex(), "fields": fields_of(packet, values)}
        if "buttons" in values:
            vector["pressed"] = pressed(values["buttons"])
        valid.append(vector)

    good = encode(typical, key)
    invalid = [
        ("one byte short", good[:-1], "length"),
        ("one byte long", good + b"\x00", "length"),
        ("magic 0x51", encode({**typical, "magic": 0x51}, key), "magic"),
        ("version 2", encode({**typical, "version": 2}, key), "version"),
        (f"type 0x{other_type:02x}", encode({**typical, "type": other_type}, key), "type"),
        ("slot 16", encode({**typical, "slot": 16}, key), "slot"),
        ("mac with its last bit flipped", good[:-1] + bytes([good[-1] ^ 0x01]), "mac"),
        ("mac made with the pairing key", encode(typical, wrong_key), "mac"),
        ("all-zero mac", good[:-MAC_SIZE] + bytes(MAC_SIZE), "mac"),
    ]
    return valid, [{"name": name, "hex": data.hex(), "reason": reason} for name, data, reason in invalid]


def _input_vectors(values):
    defaults = {"magic": MAGIC, "version": VERSION, "type": TYPE_INPUT}
    valid_cases = [
        ("neutral", {}),
        (
            "every button but reserved, highest values",
            {"slot": 15, "sequence": 0xFFFFFFFF, "buttons": 0x7FFF, "lx": 32767, "ly": 32767, "rx": 32767,
             "ry": 32767, "lt": 255, "rt": 255, "sentMs": 0xFFFF},
        ),
        ("lowest stick values", {"slot": 1, "sequence": 2, "lx": -32768, "ly": -32768, "rx": -32768, "ry": -32768}),
        ("sticks one step off centre", {"slot": 2, "sequence": 3, "lx": -1, "ly": 1, "rx": 1, "ry": -1}),
        ("reserved bit set, which receivers ignore", {"slot": 3, "sequence": 4, "buttons": 0x8000}),
        ("typical", _TYPICAL_INPUT),
    ]
    valid, invalid = _packet_vectors(
        INPUT_FIELDS, defaults, encode_input, valid_cases, _TYPICAL_INPUT, values["sessionKey"], values["pairingKey"],
        TYPE_FEEDBACK,
    )
    return {
        "description": (
            "Input packets (protocol/SPEC.md, Input packet). Every packet's mac uses sessionKey, the session key "
            "in crypto.json. Decoders must turn each valid hex into its fields (mac as hex), and encoders must turn "
            "the fields back into the same bytes. pressed names the bits set in buttons. Decoders must drop each "
            "invalid packet for its reason: length, magic, version, type, slot or mac. sequenceOrder says whether a "
            "sequence is newer than the last one accepted (null before the first), in serial-number order."
        ),
        "sessionKey": values["sessionKey"].hex(),
        "buttonBits": BUTTON_BITS,
        "valid": valid,
        "invalid": invalid,
        "sequenceOrder": [
            {"last": last, "sequence": sequence, "newer": sequence_newer(last, sequence)}
            for last, sequence in SEQUENCE_CASES
        ],
    }


def _feedback_vectors(values):
    defaults = {"magic": MAGIC, "version": VERSION, "type": TYPE_FEEDBACK}
    valid_cases = [
        ("off", {}),
        (
            "highest values",
            {"slot": 15, "largeMotor": 255, "smallMotor": 255, "red": 255, "green": 255, "blue": 255,
             "echoMs": 0xFFFF},
        ),
        ("typical", _TYPICAL_FEEDBACK),
    ]
    valid, invalid = _packet_vectors(
        FEEDBACK_FIELDS, defaults, encode_feedback, valid_cases, _TYPICAL_FEEDBACK, values["sessionKey"],
        values["pairingKey"], TYPE_INPUT,
    )
    return {
        "description": (
            "Feedback packets (protocol/SPEC.md, Feedback packet), in the same format as input.json, with the same "
            "sessionKey."
        ),
        "sessionKey": values["sessionKey"].hex(),
        "valid": valid,
        "invalid": invalid,
    }


def _control_vectors(values):
    hello = {
        "type": "hello", "proto": 1, "deviceId": DEVICE_ID, "deviceName": "Pixel 8", "platform": "android",
        "appVersion": "1.0.0", "nonce": _b64(PHONE_NONCE),
    }
    welcome = {"type": "welcome", "slot": 0, "colour": "#1E90FF", "mode": "mixed", "padType": "x360", "udpPort": 47821}
    # The hello's length with an empty name, so the padded name fills a frame exactly.
    shell = len(json.dumps({**hello, "deviceName": ""}, separators=(",", ":")))
    messages = [
        ("hello", hello),
        ("hello with a non-ASCII name", {**hello, "deviceName": "Ana’s iPhone \U0001f3ae", "platform": "ios"}),
        ("hello at the 4096-byte limit", {**hello, "deviceName": "P" * (MAX_FRAME_BYTES - shell)}),
        # Valid here; a PC that doesn't speak proto 2 answers error unsupported_proto.
        ("hello from a newer app, proto 2", {**hello, "proto": 2}),
        ("pair_required", {"type": "pair_required", "key": _b64(values["pcPublic"]), "nonce": _b64(PC_NONCE)}),
        ("pair", {"type": "pair", "key": _b64(values["phonePublic"]), "proof": _b64(values["proof"])}),
        ("challenge", {"type": "challenge", "nonce": _b64(PC_NONCE)}),
        ("auth", {"type": "auth", "mac": _b64(values["authMac"])}),
        ("welcome, Mixed", welcome),
        (
            "welcome, All DualShock 4, last slot",
            {"type": "welcome", "slot": 15, "colour": "#FF8800", "mode": "all_ds4", "padType": "ds4",
             "udpPort": 65535},
        ),
        ("mode_changed to Mixed", {"type": "mode_changed", "mode": "mixed"}),
        ("mode_changed to All DualShock 4", {"type": "mode_changed", "mode": "all_ds4"}),
        ("slot_changed", {"type": "slot_changed", "slot": 4, "colour": "#00C853", "padType": "ds4"}),
        ("ping", {"type": "ping"}),
        ("pong", {"type": "pong"}),
        ("battery empty", {"type": "battery", "pct": 0}),
        ("battery full", {"type": "battery", "pct": 100}),
        ("bye", {"type": "bye"}),
        ("error", {"type": "error", "code": "bad_proof"}),
    ]
    valid = []
    for name, message in messages:
        framed = frame(message)
        valid.append({
            "name": name, "message": message, "json": framed[FRAME_HEADER.size :].decode("utf-8"),
            "frameHex": framed.hex(),
        })
    # Frames other senders may write: unknown fields (dropped), any order and spacing, lowercase hex.
    for name, text, message in [
        ("unknown field", '{"type":"battery","pct":57,"charging":true}', {"type": "battery", "pct": 57}),
        ("fields in another order, with spaces", '{ "pct" : 57 , "type" : "battery" }', {"type": "battery", "pct": 57}),
        (
            "slot_changed with lowercase hex",
            '{"type":"slot_changed","slot":4,"colour":"#00c853","padType":"ds4"}',
            {"type": "slot_changed", "slot": 4, "colour": "#00c853", "padType": "ds4"},
        ),
    ]:
        valid.append({"name": name, "message": message, "json": text, "frameHex": _raw_frame(text.encode()).hex()})

    ignored = [
        {"name": "unknown type", "json": text, "frameHex": _raw_frame(text.encode()).hex()}
        for text in ['{"type":"rename","name":"Couch"}']
    ]

    def json_frame(message):
        return _raw_frame(json.dumps(message, separators=(",", ":")).encode())

    too_long = '{"type":"error","code":"' + "x" * (MAX_FRAME_BYTES + 1 - len('{"type":"error","code":""}')) + '"}'
    nonce = hello["nonce"]
    url_nonce = nonce.replace("+", "-").replace("/", "_")
    assert url_nonce != nonce, "the phone nonce's base64 needs a + or / to show base64url"
    invalid = [
        ("header cut short", b"\x0f\x00", "incomplete"),
        ("body cut short", FRAME_HEADER.pack(15) + b'{"type":"pi', "incomplete"),
        ("length 0", FRAME_HEADER.pack(0), "frame_empty"),
        # The length alone decides, before any of the body arrives.
        ("length 4097, header only", FRAME_HEADER.pack(MAX_FRAME_BYTES + 1), "frame_too_long"),
        ("length 0xFFFFFFFF, header only", FRAME_HEADER.pack(0xFFFFFFFF), "frame_too_long"),
        ("length 4097, with the body", _raw_frame(too_long.encode()), "frame_too_long"),
        ("invalid UTF-8", _raw_frame(b'{"type":"error","code":"\xff"}'), "utf8"),
        # The whole body must be UTF-8, even a field the receiver skips.
        ("invalid UTF-8 in an unknown field", _raw_frame(b'{"type":"battery","pct":5,"x":"\xff"}'), "utf8"),
        ("truncated JSON", _raw_frame(b'{"type":"ping"'), "json"),
        ("NaN, which JSON doesn't have", _raw_frame(b'{"type":"battery","pct":NaN}'), "json"),
        ("an array", _raw_frame(b'["ping"]'), "not_object"),
        ("a string", _raw_frame(b'"ping"'), "not_object"),
        ("no type", _raw_frame(b'{"pct":50}'), "type"),
        ("type is a number", _raw_frame(b'{"type":5}'), "type"),
        ("welcome without udpPort", json_frame({k: v for k, v in welcome.items() if k != "udpPort"}), "field_missing"),
        ("hello without nonce", json_frame({k: v for k, v in hello.items() if k != "nonce"}), "field_missing"),
        ("hello with proto 0", json_frame({**hello, "proto": 0}), "field_invalid"),
        ("hello with proto as a string", json_frame({**hello, "proto": "1"}), "field_invalid"),
        ("hello from platform windows", json_frame({**hello, "platform": "windows"}), "field_invalid"),
        ("hello with an empty deviceName", json_frame({**hello, "deviceName": ""}), "field_invalid"),
        ("hello with deviceName null", json_frame({**hello, "deviceName": None}), "field_invalid"),
        ("hello with a 15-byte nonce", json_frame({**hello, "nonce": _b64(PHONE_NONCE[:15])}), "field_invalid"),
        # Only standard, padded base64: no spaces, no base64url alphabet, no missing padding.
        ("hello nonce with a space", json_frame({**hello, "nonce": nonce[:8] + " " + nonce[8:]}), "field_invalid"),
        ("hello nonce in base64url", json_frame({**hello, "nonce": url_nonce}), "field_invalid"),
        ("hello nonce without padding", json_frame({**hello, "nonce": nonce.rstrip("=")}), "field_invalid"),
        ("hello nonce with non-ASCII letters", json_frame({**hello, "nonce": "é" * 24}), "field_invalid"),
        ("challenge nonce not base64", json_frame({"type": "challenge", "nonce": "not base64!"}), "field_invalid"),
        ("pair with a 31-byte key", json_frame({"type": "pair", "key": _b64(values["phonePublic"][:31]),
                                                 "proof": _b64(values["proof"])}), "field_invalid"),
        ("welcome slot 16", json_frame({**welcome, "slot": 16}), "field_invalid"),
        ("welcome mode both", json_frame({**welcome, "mode": "both"}), "field_invalid"),
        ("welcome padType xone", json_frame({**welcome, "padType": "xone"}), "field_invalid"),
        ("welcome colour by name", json_frame({**welcome, "colour": "blue"}), "field_invalid"),
        ("welcome udpPort 0", json_frame({**welcome, "udpPort": 0}), "field_invalid"),
        ("battery 101", json_frame({"type": "battery", "pct": 101}), "field_invalid"),
        ("battery true", json_frame({"type": "battery", "pct": True}), "field_invalid"),
        ("battery 57.5", json_frame({"type": "battery", "pct": 57.5}), "field_invalid"),
        ("battery 57.0", _raw_frame(b'{"type":"battery","pct":57.0}'), "field_invalid"),
        ("battery 5.7e1", _raw_frame(b'{"type":"battery","pct":5.7e1}'), "field_invalid"),
        ("error with an empty code", json_frame({"type": "error", "code": ""}), "field_invalid"),
    ]
    return {
        "description": (
            "Control frames (protocol/SPEC.md, Control messages): a u32 little-endian length, then UTF-8 JSON. "
            "Byte strings in messages are base64 and match crypto.json. Decoders must turn each valid frameHex into "
            "its message (unknown fields dropped), and the encoder's frame for a message must decode back to it; "
            "the bytes may differ, since JSON escaping varies. Decoders must ignore each ignored frame, and reject "
            "each invalid one. The reasons incomplete, frame_empty and frame_too_long must match; the others all "
            "mean bad_message, so a decoder may report any of them."
        ),
        "maxFrameBytes": MAX_FRAME_BYTES,
        "valid": valid,
        "ignored": ignored,
        "invalid": [{"name": name, "frameHex": data.hex(), "reason": reason} for name, data, reason in invalid],
    }


def _mapping_vectors():
    dpad_bits = ("dpadUp", "dpadDown", "dpadLeft", "dpadRight")
    dpad = []
    for combination in range(16):
        buttons = sum(1 << BUTTON_BITS[name] for i, name in enumerate(dpad_bits) if combination & (1 << i))
        dpad.append({"buttons": buttons, "pressed": pressed(buttons), "ds4Hat": ds4_hat(buttons)})
    return {
        "description": (
            "Phone input to pad reports (protocol/SPEC.md, Mapping to pad reports). sticks: an int16 stick value, "
            "what the Xbox 360 report gets (passed through), and the DualShock 4 byte for an X axis and for a Y "
            "axis. dpad: every combination of the 4 d-pad bits and the DualShock 4 hat value (0 north, clockwise "
            "to 7 north-west, 8 neutral); opposite directions cancel. Xbox 360 pads get the d-pad bits as they are."
        ),
        "sticks": [
            {"value": value, "x360": value, "ds4X": ds4_stick_x(value), "ds4Y": ds4_stick_y(value)}
            for value in STICK_VALUES
        ],
        "dpad": dpad,
    }


def _crypto_vectors(values):
    typical = encode_input(_TYPICAL_INPUT, values["sessionKey"])
    return {
        "description": (
            "Keys and MACs (protocol/SPEC.md, Keys and MACs), all hex. The X25519 keys are RFC 7748 section 6.1's "
            "Alice (pc) and Bob (phone). pairingKeyInfo and sessionKeyInfo are the HKDF info inputs; the salt is "
            "32 zero bytes. packetMac is the mac of input.json's typical packet. Every key in rejectedPublicKeys gives an "
            "all-zero shared secret, which both sides must refuse."
        ),
        "pc": {"privateKey": PC_PRIVATE.hex(), "publicKey": values["pcPublic"].hex()},
        "phone": {"privateKey": PHONE_PRIVATE.hex(), "publicKey": values["phonePublic"].hex()},
        "sharedSecret": values["shared"].hex(),
        "pairingCode": CODE,
        "proof": values["proof"].hex(),
        "pairingKeyInfo": (PAIRING_LABEL + values["pcPublic"] + values["phonePublic"]).hex(),
        "pairingKey": values["pairingKey"].hex(),
        "pcNonce": PC_NONCE.hex(),
        "phoneNonce": PHONE_NONCE.hex(),
        "authMac": values["authMac"].hex(),
        "sessionKeyInfo": (SESSION_LABEL + PC_NONCE + PHONE_NONCE).hex(),
        "sessionKey": values["sessionKey"].hex(),
        "packetMac": {"data": typical[:-MAC_SIZE].hex(), "mac": typical[-MAC_SIZE:].hex()},
        "rejectedPublicKeys": [
            {"name": "zero", "publicKey": bytes(32).hex()},
            {"name": "one, a point of order 4", "publicKey": (b"\x01" + bytes(31)).hex()},
        ],
    }


def generate():
    """Every vector file as {file name: JSON text}."""
    values = _crypto_values()
    files = {
        "input.json": _input_vectors(values),
        "feedback.json": _feedback_vectors(values),
        "control.json": _control_vectors(values),
        "mapping.json": _mapping_vectors(),
        "crypto.json": _crypto_vectors(values),
    }
    return {name: json.dumps(data, indent=2, ensure_ascii=False) + "\n" for name, data in files.items()}


def write_vectors(out_dir):
    """Write every vector file into out_dir, with LF line endings. Returns the file names."""
    os.makedirs(out_dir, exist_ok=True)
    files = generate()
    for name, text in files.items():
        with open(os.path.join(out_dir, name), "w", encoding="utf-8", newline="\n") as file:
            file.write(text)
    return list(files)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Write PadLink's shared protocol test vectors.")
    parser.add_argument("--out", default=VECTORS_DIR, help="folder to write to (default: protocol/vectors)")
    args = parser.parse_args(argv)
    names = write_vectors(args.out)
    print(f"wrote {', '.join(names)} to {os.path.normpath(args.out)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
