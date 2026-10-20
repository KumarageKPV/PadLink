"""Tests for tools/gen_vectors.py (the reference codec) and tools/check_vectors.py.

Crypto results are checked against RFC test vectors and against HMAC and HKDF computed here
with the standard library, so the generator's use of the cryptography package is cross-checked.

Run: python -m unittest discover -s tools/tests -v
"""

import contextlib
import hashlib
import hmac
import io
import json
import os
import shutil
import struct
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

import check_vectors  # noqa: E402
import gen_vectors as gv  # noqa: E402

SPEC_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "protocol", "SPEC.md")

# RFC 7748 section 6.1: Alice's and Bob's keys and their shared secret.
ALICE_PRIVATE = bytes.fromhex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a")
ALICE_PUBLIC = bytes.fromhex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a")
BOB_PRIVATE = bytes.fromhex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb")
BOB_PUBLIC = bytes.fromhex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f")
SHARED = bytes.fromhex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742")

KEY = bytes(range(32))
OTHER_KEY = bytes(32)
UP, DOWN, LEFT, RIGHT = 1 << 11, 1 << 12, 1 << 13, 1 << 14


def hkdf(ikm, salt, info, length):
    """RFC 5869 HKDF-SHA256, written out with the standard library."""
    prk = hmac.new(salt or bytes(32), ikm, hashlib.sha256).digest()
    okm, block, counter = b"", b"", 1
    while len(okm) < length:
        block = hmac.new(prk, block + info + bytes([counter]), hashlib.sha256).digest()
        okm += block
        counter += 1
    return okm[:length]


def mac8(key, data):
    return hmac.new(key, data, hashlib.sha256).digest()[:8]


def raw_frame(payload):
    return struct.pack("<I", len(payload)) + payload


def input_fields(**overrides):
    fields = dict(slot=0, sequence=0, buttons=0, lx=0, ly=0, rx=0, ry=0, lt=0, rt=0, sentMs=0)
    fields.update(overrides)
    return fields


def feedback_fields(**overrides):
    fields = dict(slot=0, largeMotor=0, smallMotor=0, red=0, green=0, blue=0, echoMs=0)
    fields.update(overrides)
    return fields


class HkdfHelperTests(unittest.TestCase):
    """The helper above is the yardstick for the key tests, so pin it to RFC 5869 first."""

    def test_case_1(self):
        okm = hkdf(b"\x0b" * 22, bytes(range(13)), bytes(range(0xF0, 0xFA)), 42)
        self.assertEqual(
            okm.hex(), "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
        )

    def test_case_3_no_salt_or_info(self):
        okm = hkdf(b"\x0b" * 22, b"", b"", 42)
        self.assertEqual(
            okm.hex(), "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"
        )


class CryptoTests(unittest.TestCase):
    def test_x25519_matches_rfc7748(self):
        self.assertEqual(gv.x25519_public(ALICE_PRIVATE), ALICE_PUBLIC)
        self.assertEqual(gv.x25519_public(BOB_PRIVATE), BOB_PUBLIC)
        self.assertEqual(gv.x25519_shared(ALICE_PRIVATE, BOB_PUBLIC), SHARED)
        self.assertEqual(gv.x25519_shared(BOB_PRIVATE, ALICE_PUBLIC), SHARED)

    def test_x25519_rejects_keys_that_give_an_all_zero_secret(self):
        for public in (bytes(32), b"\x01" + bytes(31)):
            with self.subTest(public=public.hex()), self.assertRaises(gv.CryptoError):
                gv.x25519_shared(ALICE_PRIVATE, public)

    def test_proof_is_hmac_keyed_by_the_code_digits_over_both_public_keys(self):
        expected = hmac.new(b"042917", ALICE_PUBLIC + BOB_PUBLIC, hashlib.sha256).digest()
        self.assertEqual(gv.pairing_proof("042917", ALICE_PUBLIC, BOB_PUBLIC), expected)

    def test_proof_needs_six_digits(self):
        for code in ("42917", "0429170", "04291a", 42917):
            with self.subTest(code=code), self.assertRaises(ValueError):
                gv.pairing_proof(code, ALICE_PUBLIC, BOB_PUBLIC)

    def test_pairing_key(self):
        expected = hkdf(SHARED, b"", b"padlink/1 pairing" + ALICE_PUBLIC + BOB_PUBLIC, 32)
        self.assertEqual(gv.pairing_key(SHARED, ALICE_PUBLIC, BOB_PUBLIC), expected)

    def test_session_key(self):
        pc_nonce, phone_nonce = bytes(range(0xA0, 0xB0)), bytes(range(0xB0, 0xC0))
        expected = hkdf(KEY, b"", b"padlink/1 session" + pc_nonce + phone_nonce, 32)
        self.assertEqual(gv.session_key(KEY, pc_nonce, phone_nonce), expected)

    def test_auth_mac_is_hmac_of_the_pc_nonce(self):
        nonce = bytes(range(16))
        self.assertEqual(gv.auth_mac(KEY, nonce), hmac.new(KEY, nonce, hashlib.sha256).digest())

    def test_packet_mac_is_the_first_8_bytes_of_hmac(self):
        data = bytes(range(22))
        self.assertEqual(gv.packet_mac(KEY, data), mac8(KEY, data))


class InputPacketTests(unittest.TestCase):
    def test_layout(self):
        packet = gv.encode_input(
            input_fields(
                slot=3, sequence=0x04030201, buttons=0x0201, lx=-2, ly=0x1234, rx=-32768, ry=32767,
                lt=0xAA, rt=0xBB, sentMs=0xCDEF,
            ),
            KEY,
        )
        body = bytes.fromhex("50" "01" "01" "03" "01020304" "0102" "feff" "3412" "0080" "ff7f" "aa" "bb" "efcd")
        self.assertEqual(len(body), 22)
        self.assertEqual(packet, body + mac8(KEY, body))

    def test_round_trip_at_the_edges(self):
        fields = input_fields(
            slot=15, sequence=0xFFFFFFFF, buttons=0xFFFF, lx=-32768, ly=32767, rx=-1, ry=1, lt=255, rt=255,
            sentMs=0xFFFF,
        )
        packet = gv.encode_input(fields, KEY)
        decoded = gv.decode_input(packet, KEY)
        self.assertEqual({name: decoded[name] for name in fields}, fields)
        self.assertEqual((decoded["magic"], decoded["version"], decoded["type"]), (0x50, 1, 0x01))
        self.assertEqual(decoded["mac"], packet[22:].hex())

    def test_rejects_each_fault_with_its_reason(self):
        good = gv.encode_input(input_fields(slot=7, buttons=0x0801, lx=1000), KEY)
        cases = {
            "length": [good[:-1], good + b"\x00", b""],
            "magic": [gv.encode_input(input_fields(magic=0x51), KEY)],
            "version": [gv.encode_input(input_fields(version=2), KEY)],
            "type": [gv.encode_input(input_fields(type=0x02), KEY)],
            "slot": [gv.encode_input(input_fields(slot=16), KEY), gv.encode_input(input_fields(slot=255), KEY)],
            "mac": [good[:-1] + bytes([good[-1] ^ 0x01]), gv.encode_input(input_fields(), OTHER_KEY)],
        }
        for reason, packets in cases.items():
            for packet in packets:
                with self.subTest(reason=reason, packet=packet.hex()):
                    with self.assertRaises(gv.DecodeError) as caught:
                        gv.decode_input(packet, KEY)
                    self.assertEqual(caught.exception.reason, reason)

    def test_button_bits_follow_the_design(self):
        self.assertEqual(
            list(gv.BUTTON_BITS),
            [
                "faceBottom", "faceRight", "faceLeft", "faceTop", "shoulderLeft", "shoulderRight", "back", "start",
                "guide", "stickLeft", "stickRight", "dpadUp", "dpadDown", "dpadLeft", "dpadRight", "reserved",
            ],
        )
        self.assertEqual(list(gv.BUTTON_BITS.values()), list(range(16)))
        self.assertEqual(gv.pressed(0x8801), ["faceBottom", "dpadUp", "reserved"])
        self.assertEqual(gv.pressed(0), [])

    def test_sequence_order_is_serial_number_order(self):
        cases = {
            (None, 0): True, (None, 0xFFFFFFFF): True, (5, 6): True, (5, 5): False, (5, 4): False,
            # Wraps from 0xFFFFFFFF to 0.
            (0xFFFFFFFF, 0): True, (0xFFFFFFFF, 0x7FFFFFFE): True, (0xFFFFFFFF, 0x7FFFFFFF): False,
            (0, 0xFFFFFFFF): False, (0, 0x7FFFFFFF): True, (0, 0x80000000): False,
        }
        for (last, sequence), newer in cases.items():
            with self.subTest(last=last, sequence=sequence):
                self.assertEqual(gv.sequence_newer(last, sequence), newer)


class FeedbackPacketTests(unittest.TestCase):
    def test_layout(self):
        packet = gv.encode_feedback(
            feedback_fields(slot=2, largeMotor=0x11, smallMotor=0x22, red=0x33, green=0x44, blue=0x55, echoMs=0xBEEF),
            KEY,
        )
        body = bytes.fromhex("50" "01" "02" "02" "11" "22" "33" "44" "55" "efbe")
        self.assertEqual(len(body), 11)
        self.assertEqual(packet, body + mac8(KEY, body))

    def test_round_trip_at_the_edges(self):
        fields = feedback_fields(slot=15, largeMotor=255, smallMotor=255, red=255, green=255, blue=255, echoMs=0xFFFF)
        decoded = gv.decode_feedback(gv.encode_feedback(fields, KEY), KEY)
        self.assertEqual({name: decoded[name] for name in fields}, fields)
        self.assertEqual((decoded["magic"], decoded["version"], decoded["type"]), (0x50, 1, 0x02))

    def test_rejects_each_fault_with_its_reason(self):
        good = gv.encode_feedback(feedback_fields(slot=4, largeMotor=200), KEY)
        cases = {
            "length": [good[:-1], good + b"\x00"],
            "magic": [gv.encode_feedback(feedback_fields(magic=0x51), KEY)],
            "version": [gv.encode_feedback(feedback_fields(version=2), KEY)],
            "type": [gv.encode_feedback(feedback_fields(type=0x01), KEY)],
            "slot": [gv.encode_feedback(feedback_fields(slot=16), KEY)],
            "mac": [good[:-1] + bytes([good[-1] ^ 0x80]), gv.encode_feedback(feedback_fields(), OTHER_KEY)],
        }
        for reason, packets in cases.items():
            for packet in packets:
                with self.subTest(reason=reason, packet=packet.hex()):
                    with self.assertRaises(gv.DecodeError) as caught:
                        gv.decode_feedback(packet, KEY)
                    self.assertEqual(caught.exception.reason, reason)

    def test_an_input_packet_is_not_feedback(self):
        with self.assertRaises(gv.DecodeError):
            gv.decode_feedback(gv.encode_input(input_fields(), KEY), KEY)


class FramingTests(unittest.TestCase):
    def test_frame_is_a_little_endian_length_then_compact_utf8_json(self):
        self.assertEqual(gv.frame({"type": "ping"}), bytes.fromhex("0f000000") + b'{"type":"ping"}')
        framed = gv.frame({"type": "error", "code": "café"})
        # 31 bytes: é is two bytes in UTF-8.
        self.assertEqual(framed, bytes.fromhex("1f000000") + '{"type":"error","code":"café"}'.encode())

    def test_frame_refuses_a_message_that_would_not_decode(self):
        for message in ({"type": "battery", "pct": 101}, {"type": "welcome"}, {"type": "rename"}):
            with self.subTest(message=message), self.assertRaises(ValueError):
                gv.frame(message)

    def test_decode_returns_known_fields_only(self):
        message = gv.decode_frame(raw_frame(b'{ "pct": 57, "type": "battery", "charging": true }'))
        self.assertEqual(message, {"type": "battery", "pct": 57})

    def test_decode_ignores_an_unknown_type(self):
        self.assertIsNone(gv.decode_frame(raw_frame(b'{"type":"rename","name":"x"}')))

    def test_4096_bytes_is_the_largest_frame(self):
        shell = len('{"type":"error","code":""}')
        biggest = raw_frame(b'{"type":"error","code":"' + b"x" * (4096 - shell) + b'"}')
        self.assertEqual(len(biggest), 4 + 4096)
        self.assertEqual(gv.decode_frame(biggest)["code"], "x" * (4096 - shell))
        too_big = raw_frame(b'{"type":"error","code":"' + b"x" * (4097 - shell) + b'"}')
        with self.assertRaises(gv.DecodeError) as caught:
            gv.decode_frame(too_big)
        self.assertEqual(caught.exception.reason, "frame_too_long")
        with self.assertRaises(ValueError):
            gv.frame({"type": "error", "code": "x" * (4097 - shell)})

    def test_rejects_each_fault_with_its_reason(self):
        welcome = {"type": "welcome", "slot": 0, "colour": "#1E90FF", "mode": "mixed", "padType": "x360",
                   "udpPort": 47821}

        def changed(message, **fields):
            return raw_frame(json.dumps({**message, **fields}).encode())

        no_port = raw_frame(json.dumps({k: v for k, v in welcome.items() if k != "udpPort"}).encode())
        cases = {
            "incomplete": [b"\x0f\x00", struct.pack("<I", 20) + b'{"type":"pi'],
            "frame_empty": [struct.pack("<I", 0)],
            # The length alone decides: the body isn't read.
            "frame_too_long": [struct.pack("<I", 4097), struct.pack("<I", 0xFFFFFFFF)],
            "utf8": [raw_frame(b'{"type":"error","code":"\xff"}'), raw_frame(b'{"type":"ping","x":"\xff"}')],
            "json": [raw_frame(b'{"type":"ping"'), raw_frame(b'{"type":"battery","pct":NaN}')],
            "not_object": [raw_frame(b'["ping"]'), raw_frame(b'"ping"')],
            "type": [raw_frame(b'{"pct":50}'), raw_frame(b'{"type":5}')],
            "field_missing": [no_port],
            "field_invalid": [
                changed(welcome, slot=16),
                changed(welcome, slot=-1),
                changed(welcome, mode="both"),
                changed(welcome, padType="xone"),
                changed(welcome, colour="blue"),
                changed(welcome, udpPort=0),
                changed({"type": "battery"}, pct=101),
                changed({"type": "battery"}, pct=True),
                changed({"type": "battery"}, pct=57.0),
                changed({"type": "pair", "proof": "A" * 43 + "="}, key="A" * 42 + "=="),  # 31 bytes
                changed({"type": "challenge"}, nonce="not base64!"),
                changed({"type": "challenge"}, nonce="oKGio6Sl pqeoqaqrrK2urw=="),  # a space
                changed({"type": "challenge"}, nonce="oKGio6Sl-qeoqaqrrK2urw=="),  # base64url
                changed({"type": "challenge"}, nonce="oKGio6SlpqeoqaqrrK2urw"),  # no padding
                changed({"type": "challenge"}, nonce="é" * 24),
                changed({"type": "challenge"}, nonce=None),
                changed({"type": "error"}, code=""),
                changed({"type": "error"}, code=None),
            ],
        }
        for reason, frames in cases.items():
            for data in frames:
                with self.subTest(reason=reason, frame=data[:60]):
                    with self.assertRaises(gv.DecodeError) as caught:
                        gv.decode_frame(data)
                    self.assertEqual(caught.exception.reason, reason)


class MappingTests(unittest.TestCase):
    def test_ds4_sticks_centre_on_128_with_y_flipped(self):
        values = [-32768, -1, 0, 1, 32767]
        self.assertEqual([gv.ds4_stick_x(v) for v in values], [0, 127, 128, 128, 255])
        self.assertEqual([gv.ds4_stick_y(v) for v in values], [255, 128, 128, 127, 0])

    def test_ds4_hat(self):
        cases = {
            0: 8, UP: 0, UP | RIGHT: 1, RIGHT: 2, DOWN | RIGHT: 3, DOWN: 4, DOWN | LEFT: 5, LEFT: 6, UP | LEFT: 7,
            # Opposite directions cancel.
            UP | DOWN: 8, LEFT | RIGHT: 8, UP | DOWN | LEFT: 6, UP | LEFT | RIGHT: 0, UP | DOWN | LEFT | RIGHT: 8,
            # Other bits don't move the hat.
            UP | 0x87FF: 0, 0x87FF: 8,
        }
        for buttons, hat in cases.items():
            with self.subTest(buttons=hex(buttons)):
                self.assertEqual(gv.ds4_hat(buttons), hat)


class CheckVectorsTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.dir)
        gv.write_vectors(self.dir)

    def run_check(self, spec_path=SPEC_PATH):
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            code = check_vectors.main(["--vectors", self.dir, "--spec", spec_path])
        return code, out.getvalue()

    def edit_file(self, name, edit):
        path = os.path.join(self.dir, name)
        with open(path, encoding="utf-8", newline="") as file:
            text = file.read()
        with open(path, "w", encoding="utf-8", newline="") as file:
            file.write(edit(text))

    def spec_with(self, edit):
        with open(SPEC_PATH, encoding="utf-8") as file:
            text = edit(file.read())
        path = os.path.join(self.dir, "SPEC.md")
        with open(path, "w", encoding="utf-8") as file:
            file.write(text)
        return path

    def test_writes_the_five_files(self):
        self.assertEqual(
            sorted(os.listdir(self.dir)), ["control.json", "crypto.json", "feedback.json", "input.json", "mapping.json"]
        )

    def test_passes_on_fresh_vectors(self):
        code, output = self.run_check()
        self.assertEqual(code, 0, output)

    def test_ignores_crlf_line_endings(self):
        # git's core.autocrlf checks the files out with CRLF on Windows.
        self.edit_file("input.json", lambda text: text.replace("\n", "\r\n"))
        code, output = self.run_check()
        self.assertEqual(code, 0, output)

    def test_fails_on_an_edited_file(self):
        self.edit_file("input.json", lambda text: text.replace('"neutral"', '"resting"', 1))
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("input.json", output)
        self.assertIn("resting", output)

    def test_fails_on_a_missing_file(self):
        os.remove(os.path.join(self.dir, "crypto.json"))
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("crypto.json", output)

    def test_fails_on_a_file_the_generator_does_not_write(self):
        with open(os.path.join(self.dir, "stale.json"), "w") as file:
            file.write("{}\n")
        code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("stale.json", output)

    def test_fails_on_a_packet_field_no_vector_uses(self):
        spec = self.spec_with(lambda text: text.replace(
            "| 22 | 8 | `mac` |", "| 22 | 1 | `turbo` | Not real |\n| 22 | 8 | `mac` |", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("turbo", output)

    def test_fails_on_a_feedback_field_no_vector_uses(self):
        spec = self.spec_with(lambda text: text.replace(
            "| 9 | 2 | `echoMs` |", "| 9 | 1 | `flash` | Not real |\n| 9 | 2 | `echoMs` |", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("flash", output)

    def test_fails_on_a_control_message_or_field_no_vector_uses(self):
        spec = self.spec_with(lambda text: text.replace(
            "| `bye` |", "| `rename` | PC → phone | `name` |\n| `bye` |", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("rename", output)
        spec = self.spec_with(lambda text: text.replace("`pct` |", "`pct`, `charging` |", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("charging", output)

    def test_fails_when_the_button_bit_table_differs(self):
        spec = self.spec_with(lambda text: text.replace("| `faceBottom` |", "| `faceSouth` |", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("faceSouth", output)
        spec = self.spec_with(lambda text: text.replace("| 6 | `back` |", "| 7 | `back` |", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("buttonBits", output)

    def test_fails_on_a_vector_field_the_spec_does_not_list(self):
        spec = self.spec_with(lambda text: text.replace("| 19 | 1 | `rt` | 0–255 |\n", "", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("field `rt`", output)

    def test_fails_when_a_spec_table_is_missing(self):
        spec = self.spec_with(lambda text: text.replace("## Feedback packet", "## Feedback", 1))
        code, output = self.run_check(spec)
        self.assertEqual(code, 1)
        self.assertIn("Feedback packet", output)

    def test_fails_when_a_vector_does_not_decode_as_stated(self):
        def reject_everything(data, key):
            raise gv.DecodeError("mac")

        with mock.patch.object(gv, "decode_input", reject_everything):
            code, output = self.run_check()
        self.assertEqual(code, 1)
        self.assertIn("neutral", output)


if __name__ == "__main__":
    unittest.main()
