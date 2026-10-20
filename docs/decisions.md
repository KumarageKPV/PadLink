# Design decisions

One entry per change to `docs/design.md`, or per choice the design leaves open (for example the X25519 library in 2.4). Add the entry in the same commit as the change. Newest entries go at the top of **Entries**.

## Entry template

```markdown
### YYYY-MM-DD — <short title>
- **Iteration:** <PLAN.md id, e.g. 2.4>
- **Context:** what forced the decision (test result, platform limit, measurement)
- **Decision:** what we chose
- **Alternatives:** what we didn't choose, and why
- **Design change:** docs/design.md section(s) updated, or "none"
```

## Entries

### 2026-10-20 — 5 wrong pairing codes start a 30-second cooldown
- **Iteration:** 2.7
- **Context:** The design's defence against online guessing was "5 wrong tries make a new code". That caps the tries per code, not the tries per second: each guess still has about a one-in-a-million chance. 2.7's reviewer pointed out that a host on the same Wi-Fi can open connections in parallel, and each try costs the PC about two X25519 operations. At thousands of tries a second it would pair within minutes while a code is shown, and the headless host (2.9) shows one all the time.
- **Decision:** After the 5th wrong try, no code works for 30 seconds. `PairingCode.Current` is null and `IsCoolingDown` is true, every `pair` gets `bad_proof`, and none of those tries count. Then a new code appears. That caps guessing at 10 tries a minute, so finding the code takes about a month of nonstop tries on average. `Show` can't cut a cooldown short, and `Hide` during one keeps pairing off. A new code never repeats the last one.
- **Alternatives:** Hide the code after 5 wrong tries until someone on the PC shows it again. Rejected: it's stronger, but one player who mistypes 5 times needs the PC user to step in, and the headless host would need a key for it. Leave it for the 4.6 security pass. Rejected: 2.9's host would show a weak code until then.
- **Design change:** "Networking protocol", Join sequence step 2, and "Security and privacy", the online-guessing row. SPEC.md, "Join sequence" step 2, says the same.

### 2026-10-20 — When each side stores the pairing key
- **Iteration:** 2.7
- **Context:** SPEC.md said both sides store the pairing key, but not when. The PC stores it as soon as the proof matches, before the slot manager can answer `full`. A phone that stored its key only on `welcome` would get `full`, and on its next try the PC would send `challenge`, which it couldn't answer. Nothing let that `deviceId` recover (from 2.7's review).
- **Decision:** SPEC.md, "Join sequence": the PC stores the key once the proof matches, before it answers. The phone stores it once it has sent `pair`, and drops it on `bad_proof` or `bad_message`. A phone that gets `challenge` without a key for this PC starts over with a new `deviceId`. No bytes on the wire change, so the version stays 1.
- **Alternatives:** The PC stores the key only after a `welcome`. Rejected: a `full` after a correct code would use the code up and leave the phone unpaired, so the player would need a new code. A message for "I lost my key". Rejected: it's a wire change, and a new `deviceId` already does the job.
- **Design change:** none

### 2026-10-20 — How the PC ends a control connection
- **Iteration:** 2.7
- **Context:** protocol/SPEC.md said that a `bad_message` closes the connection, but not whether `bad_proof`, `bad_auth`, `full` and `unsupported_proto` do. It also said nothing about a known message sent the wrong way, or what the old connection hears when a newer one takes its slot over. Each side pings every second, but nothing said what happens when the pings stop. A phone whose Wi-Fi dies sends no TCP close, so its connection could hold a socket, and before `welcome` a pending join, for as long as the PC runs. And closing a socket with unread bytes resets it, which can lose the last `error` or `bye` before the phone reads it.
- **Decision:** Every `error` the PC sends ends the connection. A phone that typed a wrong code reconnects and sends `hello` again, so each `pair` meets a fresh PC key pair. A message sent in the wrong direction, such as `welcome` from a phone, is out of place: `bad_message`. The PC sends `bye` before closing a connection for a takeover or because it is shutting down. Each side closes a connection on which no frame has arrived for 5 s, before or after `welcome`; the PC's 5 s timer starts again with every frame. To the slot, that is a dropped TCP link, which moves it to Grace (3.1). To close, `ControlServer` sends what is queued, shuts its sending side, and reads until the phone closes too, for up to a second. It closes at once for a quiet link, or for a frame length of 0 or over 4096. A phone that stops reading while it keeps sending fills its outbox of 64 frames and is cut off. SPEC.md, "Join rules", says all this, except the outbox. No bytes on the wire change, so the version stays 1.
- **Alternatives:** Let a phone retry `pair` on the same connection after `bad_proof`. Rejected: it reuses the PC's key pair, and the phone has to handle two retry paths. No quiet timeout, relying on TCP closes and on 3.1's 2 s of UDP silence. Rejected: dead connections would linger until a rejoin took them over. A connection that keeps pinging without joining still stays open; a limit on connections that haven't joined, or a deadline to join, is left to the 4.6 security pass. A shorter timeout, such as 3 s. Not chosen: 5 missed pings rides out a short Wi-Fi stall.
- **Design change:** none (the design says each side pings every second and a dropped link moves the slot to Grace; SPEC.md now says when the link counts as dropped)

### 2026-10-20 — v1 runs over IPv4 only
- **Iteration:** 2.7
- **Context:** The input receiver takes a slot's packets only from the IP of the phone's control connection (2.6's decision). With IPv6, a phone's privacy addresses can give its TCP and UDP sockets different source IPs, so its input would be dropped for `address`. The receiver can bind dual-mode, but nothing chose a family for the control listener and the receiver together (open from 2.6's review).
- **Decision:** IPv4 for both. `ControlServer` (`pc/src/PadLink.Core/Control/ControlServer.cs`) takes only an IPv4 endpoint, so a phone's address is always IPv4. The host (2.9) binds both the listener and the receiver to IPv4 endpoints, such as 0.0.0.0. SPEC.md, "Ports and discovery", tells the phone to connect to an IPv4 address of the PC and send its input there. `InputReceiver` keeps its tested dual-mode binding, which the host doesn't use.
- **Alternatives:** Dual-stack for both. Rejected for v1: the phone would have to pin one source IP for both sockets, and every LAN and phone hotspot the design targets has IPv4.
- **Design change:** none (the design names no address family)

### 2026-10-20 — A slot's phone address is the IP of its control connection, on any port
- **Iteration:** 2.6
- **Context:** docs/design.md and protocol/SPEC.md drop input "from an address that is not the slot's phone" without saying what that address is (open from 2.1's review). A phone's UDP source port changes when its app recreates the socket, while its IP stays the same. The mac and the sequence check are what stop forged and replayed packets. The address check is an extra filter, done before the mac so a flood from other hosts costs no HMAC.
- **Decision:** The slot's address is the IP of the phone's control connection, known by `welcome`, before any UDP arrives. The PC takes input from that IP on any source port and sends feedback to the source port of the newest accepted packet. An IPv4 address in IPv6 form, as a dual-stack TCP socket reports it, counts as the IPv4 address. SPEC.md, "Packet rules", says so, and that the PC drops input for a slot with no session before checking its mac. `InputReceiver` (`pc/src/PadLink.Core/Input/InputReceiver.cs`) compares the IP bytes in the received socket address, which allocates nothing. Given an IPv6 endpoint it binds dual-mode, and an IPv4 phone's address is then compared in IPv6 form. No bytes on the wire change, so the version stays 1.
- **Alternatives:** The IP and port of the first accepted packet. Rejected: a phone that recreates its socket would lose its slot until it rejoined, and the first copy of the phone's first packet to arrive would set the port, so someone who forges the phone's IP and wins that race would hold the slot's feedback.
- **Design change:** none (the design doesn't define the address; SPEC.md now does)

### 2026-10-20 — An Xbox pad's user index comes from the driver's LED notification
- **Iteration:** 2.5
- **Context:** The design says to read each Xbox pad's XInput user index back from the driver. In the 1.2 spike, the driver's user-index call (`vigem_target_x360_get_user_index`) read 0 for all four pads, while the LED numbers in their notifications gave 0–3. Nefarius.ViGEm.Client 1.21.256 never makes that call. Its notification callback stores the LED number, `UserIndex` returns it, and `UserIndex` throws `Xbox360UserIndexNotReportedException` until the first notification arrives. On the driver, `UserIndex` and the `LedNumber` in `FeedbackReceived` both gave 0–3 for four pads, in the order they were created. `XInputGetState` at each number showed that pad's own state. After a removal, the next pad took the lowest free number, and a fifth Xbox pad's notification carried 255.
- **Decision:** `ViGEmPadBackend.ReadUserIndex` returns the LED number from the pad's latest notification. It returns null before the first notification, for 255, and for DualShock 4 pads. `FakePadBackend` gives the same lowest free index of 0–3, but when it creates the pad rather than about 20 ms later. A test's `RaiseFeedback` with a `LedNumber` changes the index, as a notification does.
- **Alternatives:** The driver's user-index call. Rejected: it reads 0 for every pad. The library's `UserIndex`. Not used: it gives the same number, but it throws until the first notification and gives 255 for a fifth pad.
- **Design change:** "Virtual controller layer", Rules: the user index is the LED number in the driver's notification, not the result of the driver's user-index call.

### 2026-10-20 — X25519 on the PC through BouncyCastle.Cryptography
- **Iteration:** 2.4
- **Context:** .NET 10 (SDK 10.0.401) has no X25519 type. Windows CNG reaches the curve through `ECDiffieHellman` with `ECCurve.CreateFromFriendlyName("curve25519")`. On Windows 11 (build 26300) that can generate a key pair and derive a secret from a peer's public key, but importing a private key throws `CryptographicException: The requested operation is not supported`. So it can't use RFC 7748's fixed keys, and nothing could check it against `crypto.json`. The design's PC row named only System.Security.Cryptography. On the phone, Dart's `cryptography` 2.9.0 matches RFC 7748, but it returns an all-zero shared secret for the `rejectedPublicKeys` instead of failing, so `X25519KeyPair.agree` checks for that itself.
- **Decision:** `PadLink.Protocol` uses BouncyCastle.Cryptography 2.7.0 (MIT) for X25519 only, through its RFC 7748 `X25519` class, whose `CalculateAgreement` returns false for an all-zero secret. Private keys come from `RandomNumberGenerator`. HMAC-SHA256, HKDF and the constant-time compare stay in System.Security.Cryptography.
- **Alternatives:** CNG's curve25519 through `ECDiffieHellman`. Rejected: no private-key import, so it can't be tested against the vectors, and it was only tried on Windows 11. Write X25519 by hand. Rejected: hand-written curve code needs its own review, while the library is maintained and widely used.
- **Design change:** "Tech stack", PC companion: the Crypto row adds BouncyCastle.Cryptography for X25519. "Distribution, licensing and legal", Licences: rows for BouncyCastle.Cryptography and the phone's `cryptography` package, which now ship.

### 2026-10-20 — A byte-order mark before a control message is bad_message
- **Iteration:** 2.3
- **Context:** RFC 8259 forbids senders to put a byte-order mark before networked JSON, but lets parsers ignore one, and SPEC.md didn't say which. Python's `json.loads` and System.Text.Json reject it as not JSON. Dart's `utf8.decode` drops a leading one without telling, so Dart accepted the frame.
- **Decision:** It stays `bad_message` with reason `json`: the body is not a JSON text. SPEC.md says so, and warns that Dart needs its own check of the first 3 bytes. `control.json` gains an invalid vector. Python and C# already passed it. Not a wire change, so the version stays 1.
- **Alternatives:** Ignore it, as RFC 8259 allows. Rejected: Python and C# would need code to skip it, so that no valid sender's frame changes, and a receiver that misses the rule would disagree on the same bytes.
- **Design change:** none (protocol/SPEC.md holds byte-level rules)

### 2026-10-20 — Base64 in control messages must have zero unused bits
- **Iteration:** 2.3
- **Context:** Before the padding, base64 text can carry bits that no byte uses. A 16-byte nonce ending `…urx==` decodes to the same bytes as `…urw==`. C#'s `Convert.FromBase64String` and Python's `b64decode` accept it, but Dart 3.13.5's `base64.decode` throws `FormatException`. Dart's decoder also accepts base64url and `%3D` for `=`, which the others reject.
- **Decision:** SPEC.md requires the unused bits to be zero (RFC 4648 section 3.5 lets decoders reject them), so each byte string has one text. All three decoders check that the decoded bytes encode back to the same text, which also catches Dart's base64url and `%3D`. `control.json` gains invalid vectors for a nonce (`==`) and a key (`=`) with an unused bit set, and one for `%3D`. Not a wire change, since valid senders write the same bytes, so the version stays 1.
- **Alternatives:** Accept any unused bits, and make Dart decode leniently. Rejected: Dart would need its own decoder, and two texts for the same bytes leave room for disagreement over what was sent.
- **Design change:** none (protocol/SPEC.md holds byte-level rules)

### 2026-10-20 — No JSON object in a control message may repeat a name
- **Iteration:** 2.3
- **Context:** SPEC.md didn't say what `{"type":"battery","pct":"x","pct":5}` means. Python's `json.loads` and Dart's `jsonDecode` keep the last copy and accept it. C# read each copy in turn, so the bad first one made it `bad_message`. Dart can't see a repeat through `jsonDecode`: its `reviver` callback gets every copy, but not which object each one belongs to.
- **Decision:** A repeated name in any object, at any depth, is `bad_message` with the new reason `name_repeated`. That matches I-JSON (RFC 7493 section 2.3) and the rule that lone surrogates are faults anywhere in the body. Names are compared after unescaping. Python rejects repeats in `json.loads`'s `object_pairs_hook`. C#'s `ControlFrame.Scan` keeps a set of names for each open object (`ControlFault.NameRepeated`). Dart makes one pass over text that `jsonDecode` has accepted, which also finds lone surrogate escapes and nesting over 32 levels. 2.2's C# test that a repeated `type` takes the last one now expects a rejection. `control.json` gains 3 invalid vectors and a valid one where names repeat only across objects. Not a wire change, so the version stays 1.
- **Alternatives:** Only top-level names. Rejected: no simpler in C# or Dart, and it would split the body into parts with different rules. Keep the last copy, as most readers do. Rejected: C# binds each copy as it reads it, so it would need a second parse to agree. Reason `json` instead of a new one. Not chosen: a separate reason shows exactly which rule a vector tests.
- **Design change:** none (protocol/SPEC.md holds byte-level rules)

### 2026-10-20 — A lone surrogate escape in a control message is bad_message
- **Iteration:** 2.2
- **Context:** JSON lets a string escape a UTF-16 surrogate on its own (`"\ud800"`). Such a string has no UTF-8 form, and readers disagree: Python's `json.loads` and Dart's `jsonDecode` accept it, while System.Text.Json's `GetString` throws. 2.1's review left this without a rule or a vector.
- **Decision:** A surrogate escape must be a high one followed at once by an escaped low one. A lone or reversed one gets `bad_message` with reason `utf8`, anywhere in the body: names, values, and fields the receiver skips. That matches the existing rule that the whole body must be UTF-8, and I-JSON (RFC 7493), which forbids surrogates in strings. `tools/gen_vectors.py` rejects them, `control.json` gains 4 invalid vectors and a valid escaped pair, and SPEC.md says Python and Dart need their own check. C# finds them in its first pass over the body, before any field is bound. Not a wire change, so the version stays 1: valid senders write the same bytes.
- **Alternatives:** Only check the fields a receiver reads. Rejected: senders could then put text in skipped fields that one reader keeps and another refuses, which is the gap the UTF-8 rule already closed. Replace them with U+FFFD. Rejected: two receivers could then see different strings.
- **Design change:** none (protocol/SPEC.md holds byte-level rules)

### 2026-10-20 — PC solution: xunit.v3 through VSTest, lock files, test projects as code arrives
- **Iteration:** 2.2
- **Context:** PLAN.md 2.2 asks for the section 3 projects, "matching xUnit test projects", and the design says to pin versions in lock files. The .NET 10 SDK's `dotnet new sln` now writes `.slnx`, and its xUnit template uses xunit 2.
- **Decision:** `pc/PadLink.sln` stays a classic `.sln`, which is CI's marker file. Packages are pinned in `pc/Directory.Packages.props`, and every project has a `packages.lock.json`; CI restores in locked mode. Tests use xunit.v3 3.2.2 run through VSTest, so `--filter "Category!=RequiresViGEm"` works as CLAUDE.md writes it. Only `PadLink.Protocol.Tests` exists now. Each other test project comes with the first code in its project (Core in 2.5, Simulator in 2.8, Host in 2.9, Desktop in 3.5). Host and Simulator stay class libraries until then, since an empty Exe doesn't build.
- **Alternatives:** Empty test projects for every project now. Not done: four projects with no tests to run. xunit.v3 4.0. Not chosen: a new major version, and nothing in it is needed here.
- **Design change:** none

### 2026-10-20 — Protocol details the design leaves open, fixed in protocol/SPEC.md
- **Iteration:** 2.1
- **Context:** Writing byte-exact shared vectors needs choices that "Networking protocol" doesn't make. The session key comes from "both nonces", but only the PC's `challenge` sends one, and a new phone's pairing path has none. HKDF's salt and info aren't given, and neither are the encodings of keys, proofs and MACs in JSON. Exact field names, value formats and validation are missing, as are the DualShock 4 stick formula and what the hat does when opposite d-pad bits are both set. The iteration's review also found three gaps. Packets were dropped against the last sequence "seen", so one forged packet with sequence 0xFFFFFFFF would make the PC drop all real input. A pairing code was never used up: a passive listener can recover it from one `pair` message in about 10^6 HMACs, then pair their own phone while the PC still shows the code. And nothing said which messages may come when, so a `hello` with someone else's `deviceId`, sent in clear, could kick them.
- **Decision:**
  - **Nonces.** `hello` carries the phone's 16-byte `nonce`, and `pair_required` carries a PC `nonce` as `challenge` does. Both join paths then have both nonces, with no extra round trip.
  - **Keys.** Pairing key = HKDF-SHA256(X25519 secret, a salt of 32 zero bytes, info `padlink/1 pairing` ‖ PC key ‖ phone key). Session key = HKDF-SHA256(pairing key, the same salt, info `padlink/1 session` ‖ PC nonce ‖ phone nonce). Proof is keyed by the code's 6 ASCII digits, over PC key ‖ phone key. An all-zero X25519 secret is refused.
  - **Pairing.** The PC makes a fresh X25519 key pair for each `pair_required`, and a successful `pair` uses the code up. Until `welcome`, only the expected next message (plus `ping`, `pong`, `error` and `bye`) is accepted. A new connection for a `deviceId` that holds a slot takes it over only after passing `pair` or `auth`.
  - **Sequence.** Packets are compared with the last sequence the PC *accepted*, in RFC 1982 serial-number order, so the u32 wraps. That keeps the design's "sequence wrap at 2^32" test. `input.json` gains `sequenceOrder` cases.
  - **JSON.** Messages are flat objects with a `type` field. Byte strings are strict padded base64, `slot` is 0–15 on the wire, `colour` is `#RRGGBB`, `proto` is 1 to 2^31−1, and unknown fields and types are ignored. Senders escape only what JSON requires; in C#, that means `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`. With the default encoder, the 4096-byte limit vector grows to 4101 bytes. The relaxed encoder still escapes emoji, so control-frame vectors are compared by decoding, not byte for byte.
  - **Error codes.** The first set is `unsupported_proto`, `bad_message`, `bad_proof`, `bad_auth` and `full`.
  - **Mapping.** DualShock 4 sticks: X = `(v + 32768) >> 8`, Y = `(min(-v, 32767) + 32768) >> 8`, so 0 maps to 128 on both axes. Opposite d-pad directions cancel before the hat value is picked. Xbox 360 pads get the d-pad bits as sent, as the design says.
- **Alternatives:**
  - A phone nonce in `auth` instead of `hello`. Rejected: the pairing path sends no `auth`, so it would still lack one.
  - A `challenge` after every pairing as well. Rejected: an extra round trip to get a nonce that `pair_required` can carry.
  - Hex instead of base64. Not chosen: longer, and both .NET and Dart have base64 built in.
  - For the Y axis, `255 - X byte`. Rejected: it puts the centre at 127.
  - A plain unsigned sequence compare with no wrap. Rejected: it drops the design's wrap test for no gain, since the MAC check already stops forged packets.
  - Making the 4096-byte vector decode-only, instead of requiring minimal escaping. Rejected: a C# phone-name frame could then pass the tests and still go over the limit on the wire.
- **Design change:**
  - "Networking protocol": points to protocol/SPEC.md. `hello` and `pair_required` gain a nonce, in the join sequence and the message table. Step 4 names where each nonce comes from. The sequence rule says "accepted" and serial-number order.
  - "Virtual controller layer": the d-pad row says opposite directions cancel.
  - "Security and privacy": the online-guessing row adds that a successful pairing uses the code up. The offline brute-force row adds that the code is used up, and that the PC's fresh key pair per pairing makes a replayed `pair` fail.

### 2026-10-20 — Target games: PICO PARK 2, Brawlhalla and Gang Beasts
- **Iteration:** 1.4
- **Context:** The design leaves the 3 target games open ("Open questions"). Its rules ask for at least one Steam game that uses the Steam Input API directly and one that reads XInput. Gate M2 (PLAN.md 3.8) also needs one that supports 8 local players.
- **Decision:** The user picked PICO PARK 2 (Steam app 2644470) as the Steam Input baseline, Brawlhalla as the XInput game with a 4-pad cap that players report, and Gang Beasts, which has an XInput toggle. PCGamingWiki lists 8 local players for all three. No source confirms that PICO PARK 2 uses the Steam Input API, though a player report suggests its 2021 predecessor reads pads only through Steam Input. So docs/target-games.md gives a check to run before Gate M2, and also records each game's caveats and sources. Some caveats clash with the design's rule to have PlayStation Configuration Support on. Where fewer than 8 players appear, 3.8 adds short diagnostic runs with Steam Input off. They are recorded, but only runs that follow the design's rule count toward the gate.
- **Alternatives:** none were weighed in this session.
- **Design change:** "Open questions": the target-games question is ticked, with a link to docs/target-games.md. "Testing and compatibility plan", Game compatibility: links docs/target-games.md.

### 2026-10-20 — Gate M0 passes on padprobe, without a phone driving an Xbox pad
- **Iteration:** 1.4
- **Context:** Three parts of the design's M0 weren't met as written. (1) The gate says the pads appear in "SDL's controller test", and "Checking what Windows sees" names SDL3's controller test program. M0 checked them with `tools/padprobe.py`, which uses SDL 2.32.10's joystick API through pygame-ce. (2) The M0 scope says a phone drives an Xbox pad and a DualShock 4 pad. Following PLAN.md 1.3, `spike/server.py` drove only a DualShock 4 pad. (3) The risk table said to test two game risks in M0, with 8 phones: doubled or missing players in Mixed, and a game that gets pads through Steam's virtual Xbox pads. PLAN.md runs every game test at 3.8, and M0 had no host or phones for one.
- **Decision:** The user accepted (1) and (2) at 1.4, and M0 passes with both recorded in docs/gates/M0.md. padprobe is PLAN.md 1.1's tool for every pad check, and it shows that pads appear and their type. SDL3's test program does the button checks at Gate M3 (4.8), together with joy.cpl. A phone first drives an Xbox pad in the 3.8 playtest. For (3), the two risk tests move to M2, where PLAN.md runs the target games with 8 phones.
- **Alternatives:** Build SDL3's test program and check both mixes in it before M0 passes. Not chosen: it needs CMake and MSVC, for a check padprobe already covers for pad presence. Add an Xbox mode to `spike/server.py` and re-run the phone. Not chosen: it would change spike code outside 1.4's task, and the phone-to-server path doesn't depend on the pad type.
- **Design change:** "Risks and mitigations": the two rows say M2 instead of M0.

### 2026-10-19 — padprobe counts every Xbox pad, but watches them through XInput
- **Iteration:** 1.1
- **Context:** pygame-ce 2.5.8 bundles SDL 2.32.10, which reads Xbox pads through its RAWINPUT driver by default. On the dev PC it listed 5 of 5 virtual Xbox 360 pads, although games that use XInput see only 4. It also delivered a pad's stick move only at the next button change. With `SDL_JOYSTICK_RAWINPUT=0`, SDL reads them through XInput instead: it listed 4 of the 5, the stick moves arrived when sent, and the GUIDs still carried the 045E:028E USB IDs.
- **Decision:** The list and `--expect` keep SDL's default, so they count every Xbox pad. The design never creates more than 4, so `--expect "x360=4"` fails when leftover Xbox pads exist. `--watch` sets `SDL_JOYSTICK_RAWINPUT=0`, so stick moves show on time, and lists at most 4 Xbox pads. Every mode keeps a value the caller has set. `--check-driver` creates a DualShock 4 pad, so its result doesn't depend on how SDL reads Xbox pads.
- **Alternatives:** XInput for every mode. Rejected: with 5 or more Xbox pads `--expect "x360=4"` would still pass, hiding leftover pads. Count both ways and fail if either differs. Not done: the default already catches extra pads, and `SDL_JOYSTICK_RAWINPUT=0` shows the XInput view on demand. SDL's default for `--watch` too. Rejected: stick moves would show late.
- **Design change:** none

### 2026-10-19 — pygame-ce instead of pygame for the Python tools
- **Iteration:** 0.2
- **Context:** The dev machine runs Python 3.14. pygame 2.6.1 (the latest) ships Windows wheels only up to cp313, so `pip install pygame` would try a source build. pygame-ce 2.5.8 ships a cp314 win_amd64 wheel.
- **Decision:** Install pygame-ce for `tools/padprobe.py` and other Python tools. It installs the same `import pygame` module with the same joystick API, so PLAN.md's "pygame" steps hold as written. `tools/doctor.ps1` accepts either distribution and suggests `pip install pygame-ce`.
- **Alternatives:** Python 3.13 next to 3.14 to keep upstream pygame. Rejected: a second interpreter on PATH for one package.
- **Design change:** none (the design doesn't name pygame)

### 2026-10-19 — Check ViGEmBus 1.22.0 by its driver file version, 1.21.442.0
- **Iteration:** 0.2
- **Context:** The design pins ViGEmBus 1.22.0, but that is the setup's version: the driver it installs, `ViGEmBus.sys`, reports 1.21.442.0. Older drivers still create pads, so a pad test alone can't tell them apart. This machine shows the "1.22.0" uninstall entry while running a 1.17.333.0 driver. That driver comes from vgamepad, whose pip install runs its bundled ViGEmBus 1.17.333 setup.
- **Decision:** `tools/doctor.ps1` fails unless `System32\drivers\ViGEmBus.sys` is version 1.21.442.0 or later, and its vgamepad fix says to cancel the setup vgamepad opens. Later driver checks should compare against 1.21.442.0 too, not 1.22.
- **Alternatives:** Read the version from the setup's uninstall entry. Rejected: it stays at 1.22.0 after another installer replaces the driver.
- **Design change:** none

### 2026-10-19 — Python tools run from a repo-root .venv
- **Iteration:** 0.2
- **Context:** The Python tools need vgamepad, pygame-ce, PyYAML, cryptography and zeroconf. They are installed in a `.venv` on Python 3.14.8, and the system Python has none of them, so a shell without the venv active fails the doctor.
- **Decision:** Keep the git-ignored `.venv` at the repo root, with its packages pinned in `requirements.txt`. Activate it before running any `python` command or starting Claude Code. README.md, "Python tools", has the setup steps. `tools/doctor.ps1` warns, without failing, when `python` is not the .venv one: a system Python that has the packages still runs the tools.
- **Alternatives:** Install the packages into the system Python. Rejected: unpinned, and shared with every other project. Have `tools/doctor.ps1` find `.venv` on its own. Not done: activating covers every `python` command, not only the doctor.
- **Design change:** none
