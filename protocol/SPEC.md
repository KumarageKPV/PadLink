# PadLink wire protocol, version 1

This is the contract between the phone app and the PC companion. It is the "Networking protocol" section of `docs/design.md`, with the byte-level details that section leaves open, plus the stick and d-pad mapping from its "Virtual controller layer". If this file and the design disagree, fix one of them and add a `docs/decisions.md` entry in the same commit.

`tools/gen_vectors.py` is a plain Python reference implementation. It writes the shared test vectors in `protocol/vectors/`, which the C# (`pc/src/PadLink.Protocol`) and Dart (`mobile/packages/padlink_protocol`) code is tested against. `python tools/check_vectors.py` fails if the vectors are stale, if they don't decode as they state, or if a field in this file's packet or message tables appears in no vector.

**Changing the protocol.** Edit this file and `tools/gen_vectors.py`, run `python tools/gen_vectors.py`, then update the C# and then the Dart code, all in one commit. Any change to the bytes on the wire raises the packet `version`, and `proto` with it.

## Conventions

- Multi-byte integers are little-endian. u16 and u32 are unsigned; int16 is signed two's complement.
- Byte strings inside JSON messages are standard base64 with padding (RFC 4648 section 4: `A–Z a–z 0–9 + /` and `=`). The unused bits before the padding are zero (section 3.5), so each byte string has exactly one text.
- `slot` is 0–15 everywhere on the wire. The apps show it as player 1–16.
- ‖ means concatenation. The vectors write bytes as lowercase hex.

## Ports and discovery

- The PC listens on TCP 47820 (control) and UDP 47821 (input); both are configurable. The PC answers each phone from UDP 47821 to the phone's source port.
- Bonjour service type `_padlink._tcp`; TXT record `proto=1`, `name=<PC name>`, `id=<random install id>`.
- QR payload: `padlink://<PC IP>:47820?code=<6 digits>&id=<install id>`.

## Join sequence

1. The phone opens TCP to the PC and sends `hello`, which carries a fresh 16-byte phone `nonce`.
2. New phone: the PC replies `pair_required` with a fresh X25519 public `key`, from a key pair it makes for this pairing, and a fresh 16-byte PC `nonce`. The phone sends `pair` with its own fresh X25519 public `key` and the `proof`; the code itself never crosses the network. After 5 wrong codes the PC makes a new code, and a successful `pair` uses the code up, so the PC shows a new one. Both sides derive the pairing key and store it (phone: Keychain / Android Keystore; PC: Windows DPAPI).
3. Known phone: the PC sends `challenge` with a fresh 16-byte PC `nonce`; the phone answers `auth` with the auth `mac`.
4. The PC sends `welcome`. Both sides derive the session key from the pairing key, the PC's nonce (from `pair_required` or `challenge`) and the phone's (from `hello`).
5. The phone starts sending UDP input packets; the PC sends feedback packets back. Both carry a `mac` made with the session key.
6. Either side sends `bye` to leave. A dropped TCP link or 2 s of UDP silence moves the slot to Grace.

Nonces and key pairs come from a cryptographically secure random generator, new for every connection, so every join gets a new session key.

**Join rules**

- Until `welcome`, each side accepts only the message the sequence expects next, plus `ping`, `pong`, `error` and `bye` at any time. After `welcome`, `hello`, `pair_required`, `pair`, `challenge` and `auth` are out of place. An out-of-place message gets `error` with code `bad_message`, and the connection closes.
- A new connection for a `deviceId` that already holds a slot changes nothing until it passes `pair` or `auth`. Only then does the PC close the old connection and move the slot to the new one, so a `hello` with someone else's `deviceId` can't kick them.
- A `pair` whose `key` gives an all-zero shared secret gets `error` with code `bad_message`.

## Keys and MACs

| Value | Bytes | Computed as |
| --- | --- | --- |
| X25519 key pair | 32 each | RFC 7748 X25519. Refuse a peer public key whose shared secret is all zero bytes. |
| Pairing `proof` | 32 | HMAC-SHA256(key = the 6-digit code as ASCII, data = PC public key ‖ phone public key) |
| Pairing key | 32 | HKDF-SHA256(IKM = X25519 shared secret, salt = 32 zero bytes, info = `padlink/1 pairing` ‖ PC public key ‖ phone public key) |
| Auth `mac` | 32 | HMAC-SHA256(pairing key, PC nonce) |
| Session key | 32 | HKDF-SHA256(IKM = pairing key, salt = 32 zero bytes, info = `padlink/1 session` ‖ PC nonce ‖ phone nonce) |
| Packet `mac` | 8 | First 8 bytes of HMAC-SHA256(session key, every packet byte before the mac) |

- The code keeps its leading zeros: `042917` is the 6 bytes `30 34 32 39 31 37`.
- The labels are ASCII with no terminator. 32 zero bytes is RFC 5869's salt when none is given, and an empty salt gives the same result. Pass the 32 zero bytes explicitly, since some HMAC libraries refuse an empty key.
- Compare proofs and MACs in constant time.
- Input and feedback packets use the same session key; their `type` byte tells them apart.

## Control messages

Framing: a little-endian u32 length, then that many bytes of UTF-8 JSON. The length counts the JSON only, and must be 1–4096. Read the 4-byte length first. If it is 0 or over 4096, close the connection without reading the body.

Each message is one JSON object. Its `type` field names the message, and the message's fields sit beside `type`.

| Message | Direction | Fields |
| --- | --- | --- |
| `hello` | Phone → PC | `proto`, `deviceId`, `deviceName`, `platform`, `appVersion`, `nonce` |
| `pair_required` | PC → phone | `key`, `nonce` |
| `pair` | Phone → PC | `key`, `proof` |
| `challenge` | PC → phone | `nonce` |
| `auth` | Phone → PC | `mac` |
| `welcome` | PC → phone | `slot`, `colour`, `mode`, `padType`, `udpPort` |
| `mode_changed` | PC → all phones | `mode` |
| `slot_changed` | PC → phone | `slot`, `colour`, `padType` |
| `ping` | Both | — (each side every 1 s) |
| `pong` | Both | — (the answer to a ping) |
| `battery` | Phone → PC | `pct` |
| `bye` | Both | — |
| `error` | Both | `code` |

| Field | JSON type | Values |
| --- | --- | --- |
| `proto` | integer | 1 to 2^31−1: the sender's protocol version, the same number as the packet `version`. A PC that doesn't speak it answers `unsupported_proto` |
| `deviceId` | string | Not empty; a random id the phone makes once and keeps, such as a UUID |
| `deviceName` | string | Not empty; shown on the PC |
| `platform` | string | `android` or `ios` |
| `appVersion` | string | Not empty |
| `nonce` | string | Base64 of 16 random bytes |
| `key` | string | Base64 of the sender's 32-byte X25519 public key |
| `proof` | string | Base64, 32 bytes |
| `mac` | string | Base64, 32 bytes |
| `slot` | integer | 0–15 |
| `colour` | string | `#RRGGBB`; senders write capital hex digits, receivers accept either case |
| `mode` | string | `mixed` or `all_ds4` |
| `padType` | string | `x360` or `ds4` |
| `udpPort` | integer | 1–65535 |
| `pct` | integer | 0–100 |
| `code` | string | Not empty; see the error codes below |

An integer is a JSON number with no fraction or exponent, so `57.0` and `5.7e1` are not integers, and neither is `true`. `null` is not a string.

On a mode switch the PC sends `mode_changed` to every phone first, then `slot_changed` to each phone once its new pad is plugged in. Phones keep sending input throughout; the PC drops packets for a slot while it is Switching.

**Rules**

- Senders write compact JSON (no spaces), `type` first and then the fields in table order. They escape only what JSON requires (`"`, `\` and control characters), so a message's size doesn't depend on the encoder. In C#, write with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, set on the `Utf8JsonWriter` or on the source-generated context's `JsonSerializerOptions`; System.Text.Json's default encoder escapes `+` (common in base64) and every non-ASCII character. The relaxed encoder still writes characters outside the Basic Multilingual Plane, such as emoji, as `\u` pairs, so two correct encoders may still produce different bytes.
- A sender never sends a frame over 4096 bytes.
- Receivers accept any spacing, field order and string escaping. They ignore fields they don't know, and whole messages whose `type` they don't know, so a newer peer can add both. Messages never nest objects or arrays, so a receiver may reject JSON nested more than 32 levels deep as `bad_message`.
- The whole body must be UTF-8, including fields the receiver skips. A frame whose body is not UTF-8, is not a JSON object, or has no string `type`, and a known message with a missing or invalid field, all get `error` with code `bad_message`, and then the connection closes.
- The body starts with the JSON itself. A UTF-8 byte-order mark before it is not JSON, and senders must not add one (RFC 8259 section 8.1). Dart's `utf8.decode` drops a leading one without telling, so check the first bytes.
- No JSON object in the body may repeat a name, at any depth, as I-JSON requires (RFC 7493 section 2.3). Names are compared after unescaping, so `type` and `\u0074ype` are the same name. Python's `json.loads` and Dart's `jsonDecode` keep the last copy without telling, so check for repeats yourself.
- A string escape for a UTF-16 surrogate must be a high one (`\ud800`–`\udbff`) followed at once by an escaped low one (`\udc00`–`\udfff`), which together stand for one character. A lone or reversed surrogate escape has no UTF-8 form, so it breaks the rule above, anywhere in the body: in names, in values, and in fields the receiver skips. Python's `json.loads` and Dart's `jsonDecode` accept them, so check the decoded strings. System.Text.Json's reader reads and skips them without complaint, and only `GetString` throws, so unescape every escaped name and string, skipped ones included.
- Base64 fields use only the standard alphabet, with padding and no spaces or line breaks, and the unused bits before the padding are zero. Most decoders are lenient: .NET's accepts spaces, Python's and .NET's ignore the unused bits, and Dart's accepts base64url and `%3D` for `=`. So check that the decoded bytes encode back to the same text.

**Error codes.** Receivers treat a code they don't know as a general error.

| Code | Meaning |
| --- | --- |
| `unsupported_proto` | The PC doesn't speak the `proto` in `hello` |
| `bad_message` | A frame or message broke the rules above |
| `bad_proof` | The `proof` in `pair` doesn't match the code the PC shows |
| `bad_auth` | The `mac` in `auth` is wrong |
| `full` | Every slot is taken |

## Input packet (UDP, phone to PC, 30 bytes)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| 0 | 1 | `magic` | 0x50 |
| 1 | 1 | `version` | 1 |
| 2 | 1 | `type` | 0x01 = input |
| 3 | 1 | `slot` | 0–15 |
| 4 | 4 | `sequence` | u32, +1 per packet |
| 8 | 2 | `buttons` | u16 bitmask, below |
| 10 | 2 | `lx` | int16, right is positive |
| 12 | 2 | `ly` | int16, up is positive |
| 14 | 2 | `rx` | int16, right is positive |
| 16 | 2 | `ry` | int16, up is positive |
| 18 | 1 | `lt` | 0–255 |
| 19 | 1 | `rt` | 0–255 |
| 20 | 2 | `sentMs` | Phone clock in ms, u16, wraps |
| 22 | 8 | `mac` | Packet mac over bytes 0–21 |

### Button bits

Button bits are positional, so they mean the same thing for both pad types. The names are the ones the vectors use.

| Bit | Name | Xbox 360 | DualShock 4 |
| --- | --- | --- | --- |
| 0 | `faceBottom` | A | Cross |
| 1 | `faceRight` | B | Circle |
| 2 | `faceLeft` | X | Square |
| 3 | `faceTop` | Y | Triangle |
| 4 | `shoulderLeft` | Left shoulder | L1 |
| 5 | `shoulderRight` | Right shoulder | R1 |
| 6 | `back` | Back | Share |
| 7 | `start` | Start | Options |
| 8 | `guide` | Guide | PS |
| 9 | `stickLeft` | Left stick click | L3 |
| 10 | `stickRight` | Right stick click | R3 |
| 11 | `dpadUp` | D-pad up | Hat, below |
| 12 | `dpadDown` | D-pad down | Hat |
| 13 | `dpadLeft` | D-pad left | Hat |
| 14 | `dpadRight` | D-pad right | Hat |
| 15 | `reserved` | — | — |

Bit 15 is reserved. The phone app never sets it, the codec passes it through unchanged both ways, and the pad mapping ignores it.

## Feedback packet (UDP, PC to phone, 19 bytes)

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| 0 | 1 | `magic` | 0x50 |
| 1 | 1 | `version` | 1 |
| 2 | 1 | `type` | 0x02 = feedback |
| 3 | 1 | `slot` | 0–15 |
| 4 | 1 | `largeMotor` | Rumble, 0–255 |
| 5 | 1 | `smallMotor` | Rumble, 0–255 |
| 6 | 1 | `red` | Slot colour |
| 7 | 1 | `green` | Slot colour |
| 8 | 1 | `blue` | Slot colour |
| 9 | 2 | `echoMs` | u16, the `sentMs` of the last input packet the PC accepted from this phone |
| 11 | 8 | `mac` | Packet mac over bytes 0–10 |

## Packet rules

- A packet must be exactly 30 bytes (input) or 19 bytes (feedback); drop any other length.
- The PC drops any packet with a wrong magic, version, type or mac, a slot over 15, or from an address that is not the slot's phone. The phone drops feedback the same way, and feedback for a slot that isn't its own.
- The slot's phone address is the IP address of the phone's control connection. The PC takes the phone's input from that IP on any source port, so a phone that recreates its UDP socket keeps its slot, and sends feedback to the source port of the newest accepted packet. An IPv4 address in IPv6 form (`::ffff:a.b.c.d`) counts as the IPv4 address. The phone sends input to the PC address its control connection uses.
- The PC drops input for a slot that has no session without checking its mac, since it has no key for it.
- The PC drops any input packet whose sequence is not newer than the last one it accepted for that slot in this session. Only packets that pass every other check count as accepted, so a forged packet can't move the mark. Gaps are fine: the newest state wins.
- "Newer" is serial-number order (RFC 1982), so the u32 wraps from 0xFFFFFFFF to 0: a sequence is newer when (sequence − last) mod 2^32 is 1 to 2^31−1. The first packet of a session has nothing to compare against and is accepted with any sequence. The phone may start each session anywhere.
- The phone sends at once on any button change, merges stick moves into a 120 Hz frame-synced loop, and repeats the current state at 20 Hz when idle.
- Round-trip time comes from `echoMs`: the phone's u16 ms clock minus `echoMs`, modulo 65536. The PC shows half of it as one-way latency.

## Mapping to pad reports

The phone always sends the same 30-byte packet; only the PC's mapping differs by pad type.

| Phone input | Xbox 360 report | DualShock 4 report |
| --- | --- | --- |
| Face, shoulder, menu and stick-click bits | The buttons in the bit table | The buttons in the bit table |
| `lt`, `rt` | Triggers, 0–255, as sent | L2 / R2 analog, 0–255, as sent |
| `lx`, `ly`, `rx`, `ry` | As sent: int16, up is positive | One byte each, formulas below |
| D-pad bits | The 4 d-pad buttons, as sent | 8-way hat, below |
| Rumble out | Large and small motor, 0–255 | Large and small motor, 0–255 |
| Light out | Player LED index | Lightbar RGB, mapped to the slot colour |

**Sticks to DualShock 4 bytes.** DualShock 4 sticks are one byte, with 128 at the centre and up reported as 0, so the Y axes flip. `>>` is an arithmetic shift, which floors.

- X axes (`lx`, `rx`): `(v + 32768) >> 8`
- Y axes (`ly`, `ry`): `(min(-v, 32767) + 32768) >> 8`

| v | -32768 | -1 | 0 | 1 | 32767 |
| --- | --- | --- | --- | --- | --- |
| X byte | 0 | 127 | 128 | 128 | 255 |
| Y byte | 255 | 128 | 128 | 127 | 0 |

**D-pad to the DualShock 4 hat.** Opposite directions cancel first: up with down gives neither, and left with right gives neither. What's left picks the hat value: 0 north, 1 north-east, 2 east, 3 south-east, 4 south, 5 south-west, 6 west, 7 north-west, 8 neutral. Xbox 360 pads get the 4 bits as they are.

## Test vectors

`protocol/vectors/` holds one JSON file per area. Load them from the repository root. Each file starts with a `description` of its format.

| File | Holds |
| --- | --- |
| `input.json` | Input packets: valid ones with their decoded fields and `pressed` button names, and invalid ones with their reason. Also `buttonBits`, and `sequenceOrder` cases for the newer-sequence rule |
| `feedback.json` | Feedback packets, in the same format |
| `control.json` | Control frames: valid ones with their message, ignored ones, and invalid ones with their reason |
| `mapping.json` | Stick values to Xbox 360 and DualShock 4 values, and all 16 d-pad combinations to a hat value |
| `crypto.json` | Fixed X25519 keys (RFC 7748 section 6.1), shared secret, proof, pairing key, nonces, auth mac, session key, a packet mac, and public keys to refuse |

An implementation passes when:

- **Packets.** It decodes every valid vector to its fields, encodes those fields back to the same bytes, and drops every invalid vector for the stated reason: `length`, `magic`, `version`, `type`, `slot` or `mac`.
- **Control frames.** It decodes every valid frame to its message, without fields its type doesn't define. Its own frame for that message decodes to the same message; the bytes needn't match. It ignores every ignored frame and rejects every invalid one. The reasons `incomplete`, `frame_empty` and `frame_too_long` must match. The others (`utf8`, `json`, `name_repeated`, `not_object`, `type`, `field_missing`, `field_invalid`) all mean `bad_message`, so it may report any of them.
- **Mapping and crypto.** It computes every value in `mapping.json` and `crypto.json`, and refuses every key in `rejectedPublicKeys`.
- **Sequence order.** The PC's receiver gives every `sequenceOrder` case its `newer` result.
