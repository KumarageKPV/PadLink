# PadLink — Progress

One box per PLAN.md iteration. `/next` works on the first unticked box; `/finish` ticks it as
`- [x] <id> <title> — YYYY-MM-DD — <one-line summary>`.

## Phase 0 — Foundations

- [x] 0.1 Bootstrap the repo and Claude Code setup — 2026-10-19 — folder layout, CLAUDE.md, .claude settings/format hook/skills/reviewer agent, PROGRESS.md, decisions log, README, .gitignore
- [x] 0.2 Toolchain doctor — 2026-10-19 — tools/doctor.ps1 checks 9 items and prints ok/missing with fixes; ViGEmBus check runs tools/vigem_check.py and requires the 1.22.0 driver (file 1.21.442.0); pygame-ce chosen
- [x] 0.3 CI skeleton — 2026-10-19 — ci.yml: a detect job gates the pc (Windows), mobile and python (Ubuntu) jobs on marker files and fails on stale ones; NuGet, pub and pip caches; the python job also runs tools/tests

## Phase 1 — Spike (M0)

- [x] 1.1 Pad probe — 2026-10-19 — tools/padprobe.py lists pads as JSON, typed x360/ds4/other by the GUID's USB IDs; --expect counts every Xbox pad, --watch [--seconds] reads them through XInput, --check-driver needs driver 1.21.442.0 and a DS4 pad the probe sees
- [x] 1.2 Spike: create both pad mixes — 2026-10-19 — spike/pads.py makes mixed (Xbox 360 slots 1–4, then DS4) or all-DS4 sets; padprobe saw x360=4,ds4=4 and ds4=8 at 8 pads, x360=4,ds4=12 and ds4=16 at 16, none left after; the driver's user-index call reads 0 for every Xbox pad while LED notifications give 0–3 (spike/RESULTS.md, for 2.5)
- [x] 1.3 Spike: a phone drives a pad — 2026-10-20 — spike/server.py drives one DS4 pad from UDP JSON (Y flipped, stale seq dropped, neutral after 0.5 s); send_fake.py moved it in padprobe --watch, and the Flutter spike app did over Wi-Fi from a Xiaomi phone; padprobe --watch now lists pads already connected; USB tethering took the PC off Wi-Fi on the first try (spike/RESULTS.md)
- [x] 1.4 Gate M0 and target games — 2026-10-20 — docs/gates/M0.md passes M0 on padprobe's counts and the phone run, and records that no phone drove an Xbox pad and SDL3's test wasn't run; docs/target-games.md has PICO PARK 2, Brawlhalla and Gang Beasts with sourced caveats, with PICO PARK 2's Steam Input API use still to check before 3.8

## Phase 2 — Core (M1)

- [x] 2.1 Protocol spec and shared test vectors — 2026-10-20 — protocol/SPEC.md fills the design's gaps (nonces in hello and pair_required, HKDF labels, strict base64 JSON, DS4 stick and hat mapping, serial-number sequence order, single-use pairing code); tools/gen_vectors.py writes 5 vector files (31 valid, 57 invalid, 1 ignored); check_vectors.py diffs them, decodes each, and fails on any SPEC table field no vector uses
- [x] 2.2 C# solution and protocol library — 2026-10-20 — pc/PadLink.sln with the 5 section-3 projects (only Protocol has code) and PadLink.Protocol.Tests; input and feedback packets as readonly record structs with span TryRead/Write that allocate 0 bytes, and ControlFrame framing with strict source-generated JSON messages; 94 vector cases pass, with the mac check left to 2.4; a lone surrogate escape is now bad_message (SPEC.md, 4 vectors); the other test projects come with their first code
- [x] 2.3 Dart protocol package — 2026-10-20 — mobile/packages/padlink_protocol has the packets on ByteData, button bits, framing, the 13 messages and the DS4 stick and hat mapping, and passes every vector (208 tests), with the mac check left to 2.4; a name repeated in any JSON object (new reason name_repeated), base64 with unused bits set, and a leading byte-order mark are now bad_message in SPEC.md, the vectors, Python and C#, since Dart's decoders disagreed (3 decisions)
- [x] 2.4 Crypto on both sides — 2026-10-20 — X25519, the pairing proof, HKDF pairing and session keys, the auth mac and the 8-byte packet mac in C# and Dart, matching every crypto.json entry; X25519 on the PC uses BouncyCastle.Cryptography 2.7.0, since .NET 10's CNG curve25519 can't import a private key (1 decision), and Dart's cryptography 2.9.0 needs its own all-zero secret check; packet reads and writes take the session key and drop any flipped mac or field bit for mac in constant time, still allocating 0 bytes; the mac vectors are real drops, C#'s extra bad frames name their fault, and Dart's sender-form test no longer passes empty (C# 224, Dart 241 tests)
- [x] 2.5 Pad backend and mappings — 2026-10-20 — IVirtualPadBackend in PadLink.Core with FakePadBackend (records every call; one contract test set runs on both backends) and ViGEmPadBackend on Nefarius.ViGEm.Client 1.21.256; PadMapping maps buttons through per-byte tables and a hat table and passes mapping.json; padprobe saw x360=4,ds4=4 then x360=0,ds4=8; the Xbox user index is the LED number in the driver's notification, XInput showed each pad's own state at it, and a fifth pad gets 255 (1 decision); Submit allocates 0 bytes, and removed pads are freed only at Dispose, so a racing Submit can't reach freed memory
- [ ] 2.6 UDP input receiver
- [ ] 2.7 TCP control server and pairing
- [ ] 2.8 Phone simulator
- [ ] 2.9 Headless host
- [ ] 2.10 PC discovery
- [ ] 2.11 Flutter app scaffold
- [ ] 2.12 Phone connection layer
- [ ] 2.13 Standard controller layout
- [ ] 2.14 Gate M1

## Phase 3 — Two modes and 8 players (M2)

- [ ] 3.1 Full slot manager
- [ ] 3.2 Mode manager
- [ ] 3.3 Phone mode awareness
- [ ] 3.4 Rumble and slot colour
- [ ] 3.5 Desktop app
- [ ] 3.6 iOS build
- [ ] 3.7 Soak and fault runs
- [ ] 3.8 Gate M2

## Phase 4 — Polish (M3)

- [ ] 4.1 Per-game mode profiles
- [ ] 4.2 Retro and Racing layouts
- [ ] 4.3 Layout editor
- [ ] 4.4 Diagnostics and logs
- [ ] 4.5 Startup checks and help screens
- [ ] 4.6 Security pass
- [ ] 4.7 Windows installer
- [ ] 4.8 Gate M3

## Phase 5 — Beta and launch (M4)

- [ ] 5.1 Release pipelines
- [ ] 5.2 Store listings and compliance
- [ ] 5.3 Beta
- [ ] 5.4 Gate M4 — launch
