# PadLink — Build Plan for Claude Code

*Last updated: 20 October 2026 · Builds the design in `docs/design.md` (the "Two Controller Modes" design doc)*

This plan takes PadLink from an empty folder to a launched app in 41 small iterations. Each iteration is sized for one Claude Code session, ends with checks Claude can run itself, and is marked 👤 where only you can do a step (installing drivers, holding a phone, store accounts).

**How to start**

1. Create an empty folder and run `git init` in it.
2. Put two files in it: this file as `PLAN.md`, and `padlink-design-doc-two-modes.md` as `docs/design.md`.
3. Open a terminal there and run `claude`.
4. Paste: `Do iteration 0.1 in PLAN.md.` From then on, start each session with `/next`.

---

## 1. How each iteration runs

Every iteration follows the same loop. The `/next`, `/verify` and `/finish` skills (created in iteration 0.1) carry this loop, so you rarely type long prompts.

1. **Start fresh** — run `/clear`, then `/next`. Claude reads `PROGRESS.md`, picks the first unticked iteration, and reads only that iteration's block here plus the design sections it names.
2. **Plan** — Claude writes a short plan (files, tests first, how it will prove each "Done when" item) and waits. Press `Ctrl+G` to edit the plan, or reply with changes. For a bigger iteration, start Claude in plan mode with `Shift+Tab`.
3. **Build** — approve, and Claude writes the tests first, then the code.
4. **Prove** — Claude runs `/verify` and pastes each command and its result. "It should work" is not evidence.
5. **Review** — `/finish` sends the diff to a `reviewer` subagent that didn't write it, fixes real findings, ticks `PROGRESS.md` and commits.
6. **👤 Your steps** — if the iteration has a 👤 step, Claude stops and lists it. Do it, report back, then let Claude finish.

**Rules that keep sessions on track**

- One iteration per session. If Claude's plan touches more than about 10 files, ask it to split the iteration.
- If you've corrected Claude twice on the same thing, run `/clear` and restart the iteration with what you learned in the prompt.
- Never skip a gate (iterations marked **Gate**). Each one decides whether the next phase is worth starting.
- When reality forces a design change, Claude edits `docs/design.md` and adds an entry to `docs/decisions.md` in the same commit.
- Optional, for long unattended checks: run Claude in auto mode and set the iteration's acceptance check as a goal, for example `/goal the soak script reports zero stuck inputs and p95 under 25 ms for both modes`.

---

## 2. What you need first (👤 one-time setup)

| Item | Version | Why | Check |
| --- | --- | --- | --- |
| Windows 10 or 11 PC, x64 | — | Dev and test machine; ViGEmBus is Windows-only | — |
| Git for Windows | Latest | Version control; also gives Claude Code a bash shell | `git --version` |
| Claude Code (native Windows install) | Latest | Builds the app | `claude --version` |
| GitHub account and `gh` CLI | Latest | Repo, CI, releases | `gh auth status` |
| .NET SDK | 10.0.x (LTS) | PC companion app | `dotnet --version` |
| Flutter SDK, stable channel | 3.47.x | Phone app | `flutter --version` |
| Android Studio (SDK, platform tools) | Latest | Android builds and `adb` | `flutter doctor` |
| An Android phone with USB debugging on | Android 8.0+ | Real-device tests | `adb devices` |
| ViGEmBus driver | 1.22.0 (final) | Creates the virtual pads; needs admin | `winget install --id ViGEm.ViGEmBus -e` |
| Python | 3.12+ | Pad probe, test vectors, M0 spike | `python --version` |
| Steam, PlayStation Configuration Support on | Latest | Game tests | Steam → Settings → Controller |
| Mac with Xcode, or GitHub's macOS runners | — | iOS builds | — |
| Apple Developer Program, Google Play developer account | — | Device builds, TestFlight, stores (Phase 5) | — |
| More phones (borrowed is fine) | Any 8 | 8-player gates | — |

---

## 3. Repository layout

```text
padlink/
├── CLAUDE.md                 # Short project rules (iteration 0.1)
├── PLAN.md                   # This file
├── PROGRESS.md               # Ticked iterations, dates, one-line notes
├── docs/
│   ├── design.md             # Source of truth
│   ├── decisions.md          # Design changes, one entry each
│   ├── target-games.md       # Chosen in M0
│   ├── compatibility.md      # Per-game results by mode
│   └── gates/                # Evidence for each gate (M0.md … M4.md)
├── protocol/
│   ├── SPEC.md               # Wire format, extracted from design.md
│   └── vectors/              # Shared test vectors (JSON)
├── pc/                       # .NET 10 solution PadLink.sln
│   ├── src/PadLink.Protocol  # Packets, framing, crypto
│   ├── src/PadLink.Core      # Receiver, control server, slots, modes, pad backends
│   ├── src/PadLink.Host      # Headless host (console)
│   ├── src/PadLink.Desktop   # WPF tray app
│   ├── src/PadLink.Simulator # Fake phones for load and soak tests
│   └── tests/                # xUnit test projects
├── mobile/
│   ├── packages/padlink_protocol  # Dart protocol and crypto
│   └── app/                       # Flutter app
├── tools/                    # padprobe.py, doctor.ps1, gen_vectors.py, soak.ps1
├── spike/                    # M0 throwaway code; nothing imports from here
├── installer/                # Inno Setup script
└── .claude/                  # Settings, hooks, skills, agents (iteration 0.1)
```

---

## 4. Claude Code setup files (written in iteration 0.1)

Claude creates these from the text below. Keep `CLAUDE.md` short: it loads every session, and long files get ignored.

### `CLAUDE.md`

```markdown
# PadLink
Phone-as-controller app: Flutter phone app + Windows C# companion that creates
virtual Xbox 360 / DualShock 4 pads through the ViGEmBus driver.
- Design (source of truth): docs/design.md — read only the sections an iteration names
- Build plan: PLAN.md · Progress: PROGRESS.md · Design changes: docs/decisions.md

## Commands
- PC: `dotnet build pc/PadLink.sln -warnaserror`
- PC tests: `dotnet test pc/PadLink.sln --filter "Category!=RequiresViGEm"`
- PC driver tests (Windows + ViGEmBus only): `dotnet test pc/PadLink.sln --filter "Category=RequiresViGEm"`
- Dart protocol: `dart test` in mobile/packages/padlink_protocol
- App: `flutter analyze` and `flutter test` in mobile/app
- Pads Windows can see: `python tools/padprobe.py`
- Load test: `dotnet run --project pc/src/PadLink.Simulator -- --phones 8 --seconds 30`

## Rules
- One PLAN.md iteration per session. Plan first; wait for approval before editing.
- Write tests first. Show every acceptance check as the command plus its result.
- Protocol change order: protocol/SPEC.md and vectors → C# → Dart, in one commit. Bump the packet version for any wire change.
- Hot path (UDP receive → pad submit): no per-packet allocations, LINQ, logging or locks held across I/O.
- Pad access only through IVirtualPadBackend; unit tests use FakePadBackend.
- Tests needing the driver carry [Trait("Category", "RequiresViGEm")].
- No "ViGEm", "Monect", Xbox or PlayStation logos in user-facing names or assets.
- If code must differ from docs/design.md, update it and add a docs/decisions.md entry in the same commit.

## Gotchas
- XInput exposes only 4 Xbox pads; DualShock 4 pads reach games through HID / Steam Input.
- ViGEmBus 1.22.0 is the final release; don't search for newer versions.
- Ports: TCP 47820 (control), UDP 47821 (input). Shell may be PowerShell or Git Bash.
```

### `.claude/settings.json`

```json
{
  "permissions": {
    "allow": [
      "Bash(dotnet build *)", "Bash(dotnet test *)", "Bash(dotnet run *)", "Bash(dotnet format *)",
      "Bash(flutter analyze *)", "Bash(flutter test *)", "Bash(flutter pub get *)",
      "Bash(dart test *)", "Bash(dart format *)",
      "Bash(python tools/*)",
      "Bash(git status *)", "Bash(git diff *)", "Bash(git log *)", "Bash(git add *)", "Bash(git commit *)"
    ],
    "deny": [
      "Bash(git push --force *)",
      "Read(./**/*.pfx)", "Read(./**/*.jks)", "Read(./**/*.keystore)", "Read(./.env*)"
    ]
  },
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write",
        "hooks": [
          { "type": "command", "command": "python \"$CLAUDE_PROJECT_DIR/.claude/hooks/format.py\"" }
        ]
      }
    ]
  }
}
```

`format.py` reads the hook's JSON from stdin, takes `tool_input.file_path`, runs `dart format` on `.dart` files and `dotnet format whitespace` on `.cs` files, and always exits 0 so a formatter problem never blocks work. If the hook command fails in your shell, ask Claude to adjust it for PowerShell.

### `.claude/skills/next/SKILL.md`

```markdown
---
name: next
description: Start the next PLAN.md iteration
disable-model-invocation: true
argument-hint: [iteration-id]
---
1. Read PROGRESS.md. Work on iteration $ARGUMENTS if given, otherwise the first unticked one.
2. Read that iteration's block in PLAN.md and only the docs/design.md sections it lists.
3. If any "Depends on" iteration is unticked, stop and say which.
4. Write a short plan: files to create or change, tests to write first, and how you will
   prove each "Done when" item. Ask blocking questions with AskUserQuestion.
   Wait for approval before editing anything.
5. After approval: tests first, then code. Run /verify. Show evidence for each "Done when" item.
6. If the iteration has 👤 steps, list them and stop until the user confirms them.
```

### `.claude/skills/verify/SKILL.md`

A project skill named `verify` is also run by Claude before each commit, which is what we want.

```markdown
---
name: verify
description: Run all checks for the parts of the repo that exist. Use before every commit and to prove an iteration's Done-when items.
---
Run in order, skipping steps whose project doesn't exist yet. Show each command and the end of its output.
1. `dotnet build pc/PadLink.sln -warnaserror`
2. `dotnet test pc/PadLink.sln --filter "Category!=RequiresViGEm"`
3. If pc/src/PadLink.Core changed and `python tools/padprobe.py --check-driver` succeeds:
   `dotnet test pc/PadLink.sln --filter "Category=RequiresViGEm"`
4. `python tools/check_vectors.py`
5. In mobile/packages/padlink_protocol: `dart test`
6. In mobile/app: `flutter analyze`, then `flutter test`
Stop at the first failure, fix the root cause, re-run from that step.
Never skip, weaken or delete a test to make it pass.
```

### `.claude/skills/finish/SKILL.md`

```markdown
---
name: finish
description: Close out the current PLAN.md iteration
disable-model-invocation: true
---
1. Run /verify.
2. Ask the reviewer subagent to review the uncommitted diff for this iteration.
3. Fix findings that break a Done-when item, the design, or security. List the rest as notes.
4. Tick the iteration in PROGRESS.md with today's date and a one-line summary.
   For a Gate iteration, write the evidence to docs/gates/<milestone>.md.
5. Commit with the iteration's commit message.
```

### `.claude/agents/reviewer.md`

```markdown
---
name: reviewer
description: Reviews an iteration's diff against PLAN.md and docs/design.md. Use after implementation, before commit.
tools: Read, Grep, Glob, Bash
---
You review changes you did not write. Read the iteration's block in PLAN.md, the design
sections it lists, and `git diff`. Report only:
1. Done-when items not clearly met
2. Mismatches with docs/design.md (wire format, ports, states, mappings, mode behaviour)
3. Drift between the C# and Dart protocol code
4. Allocations, locks or logging in the per-packet hot path
5. Security problems in pairing, keys or packet MACs
6. Tests that don't assert real behaviour
Give file:line and a one-line fix for each. No style comments.
```

---

## 5. Phases at a glance

| Phase | Milestone | Iterations | Ends with |
| --- | --- | --- | --- |
| 0 · Foundations | — | 0.1–0.3 | Repo, Claude Code setup, toolchain check, CI |
| 1 · Spike | M0 | 1.1–1.4 | **Gate:** both pad mixes visible; a phone drives a pad |
| 2 · Core | M1 | 2.1–2.14 | **Gate:** real phone plays; p95 under 25 ms with 4 phones |
| 3 · Two modes, 8 players | M2 | 3.1–3.8 | **Gate:** 8-phone playtest in each mode; switch under 2 s |
| 4 · Polish | M3 | 4.1–4.8 | **Gate:** every requirement met across the device matrix |
| 5 · Beta and launch | M4 | 5.1–5.4 | **Gate:** both stores approved; signed installer published |

---

## 6. Iterations

Each block below is the instruction Claude follows. "Design" lists the `docs/design.md` sections to read.

### Phase 0 — Foundations

#### 0.1 Bootstrap the repo and Claude Code setup
- **Depends on:** —
- **Design:** none (read sections 3–4 of this plan)
- **Task:** Create the folder layout from section 3 (empty folders get a `.gitkeep`). Create every file in section 4 exactly as written, plus `.claude/hooks/format.py` as described. Create `PROGRESS.md` with one unticked checkbox per iteration in section 6 (id and title). Create `docs/decisions.md` with a heading and an entry template, `README.md` with a three-line summary, and a `.gitignore` covering .NET, Flutter, Python, IDE folders and signing files.
- **Done when:** `git status` shows all files; `/skills` lists `next`, `verify` and `finish`; `/context` shows 1 custom agent; `PROGRESS.md` has 41 boxes.
- **Commit:** `chore: bootstrap repo and Claude Code setup`

#### 0.2 Toolchain doctor
- **Depends on:** 0.1
- **Design:** Tech stack
- **Task:** Write `tools/doctor.ps1`: checks Git, gh, .NET 10 SDK, Flutter 3.47.x, `adb`, Python 3.12+ with `vgamepad` and `pygame`, and whether ViGEmBus works (by running `tools/vigem_check.py`, which creates and removes one virtual pad). Prints a table of ok/missing with the fix command for each, and exits non-zero if anything required is missing.
- **Done when:** the script runs and its table is pasted.
- **👤** Install anything it reports missing, especially ViGEmBus (needs admin), then re-run until clean.
- **Commit:** `chore: add toolchain doctor`

#### 0.3 CI skeleton
- **Depends on:** 0.1
- **Design:** Tech stack (Shared and tooling)
- **Task:** Add `.github/workflows/ci.yml`: a Windows job running `dotnet build` and the non-driver tests; an Ubuntu job running `dart test` and `flutter analyze` / `flutter test`; a job running `python tools/check_vectors.py`. Each job skips cleanly when its project doesn't exist yet. Cache NuGet and pub.
- **Done when:** the YAML parses (`python -c "import yaml,sys; yaml.safe_load(open(sys.argv[1]))" .github/workflows/ci.yml`).
- **👤** Push `main` to the existing `origin` (don't create a new GitHub repo), and confirm the workflow goes green.
- **Commit:** `ci: add build and test workflow`

### Phase 1 — Spike (M0)

Spike code lives in `spike/`, is throwaway, and nothing outside it may import from it.

#### 1.1 Pad probe
- **Depends on:** 0.2
- **Design:** Background; Controller modes
- **Task:** Write `tools/padprobe.py` using pygame's joystick API: print a JSON list of every controller Windows exposes (index, name, GUID, button and axis counts). Flags: `--watch` prints changes live; `--check-driver` exits 0 only if ViGEmBus can create a pad; `--expect "x360=4,ds4=4"` exits non-zero if the counts differ. Every later pad check uses this tool, so Claude can verify pads without you.
- **Done when:** `python tools/padprobe.py` prints valid JSON, and `--expect "x360=0,ds4=0"` passes with no pads connected.
- **Commit:** `tools: add padprobe`

#### 1.2 Spike: create both pad mixes
- **Depends on:** 1.1
- **Design:** Controller modes; Virtual controller layer
- **Task:** Write `spike/pads.py` with vgamepad: `--mode mixed --count 8` creates 4 Xbox 360 pads and then 4 DualShock 4 pads; `--mode all_ds4 --count 8` creates 8 DualShock 4 pads. It holds the pads for `--seconds`, then removes them. Also try `--mode mixed --count 16` and `--mode all_ds4 --count 16`.
- **Done when:** with each mode running, `padprobe --expect` shows `x360=4,ds4=4` for mixed/8 and `x360=0,ds4=8` for all_ds4/8; the 16-pad results are recorded whatever they are.
- **Commit:** `spike: create mixed and all-DS4 pad sets`

#### 1.3 Spike: a phone drives a pad
- **Depends on:** 1.2
- **Design:** System architecture
- **Task:** Write `spike/server.py` (UDP JSON, one DualShock 4 pad) and a minimal Flutter app in `spike/phone_spike` with one stick and two buttons that sends state 60 times a second to an IP typed in a text field. No pairing or security.
- **Done when:** a local Python script sending fake packets moves the pad, shown by `padprobe --watch` output.
- **👤** Install the spike app on your phone (`flutter run`), connect it to the PC, and confirm in `padprobe --watch` that the stick and buttons move.
- **Commit:** `spike: phone to pad over UDP`

#### 1.4 **Gate M0** and target games
- **Depends on:** 1.2, 1.3
- **Design:** Testing and compatibility plan (Game compatibility); Roadmap
- **Task:** Write `docs/gates/M0.md` with the probe results from 1.2 and the phone result from 1.3. Draft `docs/target-games.md` with the selection rules from the design doc and empty slots for 3 games.
- **👤** Pick the 3 target games: at least one Steam game using the Steam Input API directly and one that reads XInput. Fill them in. If either pad mix failed in 1.2, stop and decide with Claude what changes before Phase 2.
- **Done when:** both files exist and the gate result is "pass".
- **Commit:** `docs: M0 gate and target games`

### Phase 2 — Core (M1)

#### 2.1 Protocol spec and shared test vectors
- **Depends on:** 1.4
- **Design:** Networking protocol; Virtual controller layer (mapping table)
- **Task:** Copy the protocol section into `protocol/SPEC.md` as the wire contract. Write `tools/gen_vectors.py`, a plain Python reference encoder, and generate `protocol/vectors/`: `input.json` (packets and decoded fields, including edge values), `feedback.json`, `control.json` (framed JSON messages), `mapping.json` (int16 sticks → DS4 bytes with Y flipped, at -32768, -1, 0, 1, 32767), and `crypto.json` (fixed keys → X25519 shared secret, HKDF outputs, pairing proof, packet MAC). Write `tools/check_vectors.py` that re-generates and diffs them.
- **Done when:** `python tools/check_vectors.py` passes, and every field in SPEC.md's packet tables appears in at least one vector.
- **Commit:** `protocol: spec and shared test vectors`

#### 2.2 C# solution and protocol library
- **Depends on:** 2.1
- **Design:** Networking protocol; Tech stack
- **Task:** Create `pc/PadLink.sln` targeting `net10.0` with the projects from section 3 (empty for now except Protocol) and matching xUnit test projects. In `PadLink.Protocol`, implement the input and feedback packets as `readonly struct`s with span-based `TryRead`/`Write` (no allocations), button bit constants, control-message framing (u32 length + UTF-8 JSON, 4 KB max) with System.Text.Json source generation. Tests load `protocol/vectors/*.json`. Follow protocol/SPEC.md, "Control messages", where 2.1's review found C# traps:
  - Build the context with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, or the 4096-byte vector grows past the limit.
  - .NET's base64 decoders accept spaces, so check the text first.
  - System.Text.Json doesn't check UTF-8 in values it skips, so validate the whole body first.
  - It binds `null` to a string without error.
  - Lone UTF-16 surrogates (`"\ud800"`) have no vector yet: Python's reference decoder accepts them, and System.Text.Json's `GetString` throws. Pick a rule (likely `bad_message`), and add it to SPEC.md and an invalid vector in `tools/gen_vectors.py`, in the same commit.
- **Done when:** every vector round-trips in tests; a benchmark test shows decoding allocates 0 bytes (`GC.GetAllocatedBytesForCurrentThread`).
- **Commit:** `pc: protocol library with vector tests`

#### 2.3 Dart protocol package
- **Depends on:** 2.1
- **Design:** Networking protocol
- **Task:** Create `mobile/packages/padlink_protocol` with the same packets, button bits, framing and stick mapping, using `ByteData`. Tests load the same `protocol/vectors/*.json` (path relative to the repo root). First settle two rules from 2.2's review, where the C# and Python decoders disagree with Dart. For each, add the rule to protocol/SPEC.md, a vector in `tools/gen_vectors.py`, the C# check in `pc/src/PadLink.Protocol/Control/` and a docs/decisions.md entry, all in this iteration's commit (CLAUDE.md's protocol change order). Neither changes what valid senders write, so the version stays 1.
  - **Repeated field names.** SPEC.md doesn't say what `{"type":"battery","pct":"x","pct":5}` means. Python's `json.loads` and Dart's `jsonDecode` keep the last copy and accept it. C# reads each copy as it comes, so the bad first one makes it `bad_message`. The review suggests making a repeated top-level name `bad_message`, as I-JSON (RFC 7493) does: reject it in C#'s first pass over the body (`ControlFrame.Scan`), and in Python with an `object_pairs_hook`. Dart's `jsonDecode` drops the earlier copies, so check how Dart can see them before picking. If repeats become `bad_message`, 2.2's C# test that a repeated `type` takes the last one changes too.
  - **Base64 padding bits.** The bits before `=` that no byte uses may be non-zero: a 16-byte nonce ending `…urx==` decodes to the same bytes as `…urw==`. C#'s `Convert.FromBase64String` and Python's `b64decode` accept it; Dart 3.13.5's `base64.decode` throws `FormatException`. The review suggests that SPEC.md require those bits to be zero (RFC 4648 section 3.5 lets decoders reject them), with an invalid vector. C# and Python then check that re-encoding the decoded bytes gives the same text.
- **Done when:** `dart test` passes on every vector, including the new ones, and `/verify` passes for C# and the vectors.
- **Commit:** `mobile: Dart protocol package with vector tests`

#### 2.4 Crypto on both sides
- **Depends on:** 2.2, 2.3
- **Design:** Networking protocol (Join sequence); Security and privacy
- **Task:** Implement pairing-key derivation (X25519 + HKDF-SHA256), the pairing proof (HMAC-SHA256 of the code over both public keys), session-key derivation from both nonces, and the 8-byte truncated packet MAC with constant-time compare. C#: HMAC and HKDF from System.Security.Cryptography; for X25519, first check whether .NET 10's built-in APIs support Curve25519 on Windows 10 and 11, and if not use the BouncyCastle.Cryptography package. Record the choice in `docs/decisions.md`. Dart: the `cryptography` package. Also close three test gaps from 2.3's review:
  - **The `mac` vectors.** With no session key yet, the packet tests read a packet with a bad mac and check that it writes back unchanged. These are `Invalid_vector_is_dropped_for_its_reason` in `InputPacketTests.cs` and `FeedbackPacketTests.cs`, and the "is dropped for mac" tests in Dart's `packets_test.dart` (`_expectDropped`), whose names say the opposite of what they check. Turn them into real drops once the mac is checked.
  - **C#'s extra bad frames.** `Frames_the_vectors_do_not_cover_are_bad_messages` in `ControlFrameTests.cs` checks only that some fault is reported, so `{"type":"ping","type":5}` would pass under 2.2's last-copy-wins code too. Give each case its expected `ControlFault`, as the matching group in Dart's `control_test.dart` does.
  - **Dart's sender-form tests.** In `control_test.dart`, "encodes to its bytes in sender form" returns early for the 4 vectors whose JSON isn't in sender form ("unknown field", "fields in another order, with spaces", "an escaped surrogate pair", "names repeated only across objects"). They pass while checking nothing. Leave them out before calling `test`, or mark them skipped.
- **Done when:** both sides match every entry in `crypto.json`; a flipped bit in any MAC is rejected in tests.
- **Commit:** `protocol: pairing and packet crypto`

#### 2.5 Pad backend and mappings
- **Depends on:** 2.2, 1.1
- **Design:** Virtual controller layer
- **Task:** In `PadLink.Core`, define `IVirtualPadBackend` (create a pad of type X360 or DS4, submit state, remove, feedback event, read XInput user index). Implement `FakePadBackend` (records every call in order) and `ViGEmPadBackend` with Nefarius.ViGEm.Client 1.21.256. Put the phone-to-report mappings in pure functions with precomputed lookups per pad type. First read docs/gates/M0.md, "Carried forward". In the spike, the driver's user-index call read 0 for every Xbox pad while the LED numbers gave 0–3, and the gate suggests an input check on an Xbox slot.
- **Done when:** mapping tests pass against `mapping.json`; a `RequiresViGEm` test plugs 4 Xbox + 4 DS4 pads and `padprobe --expect "x360=4,ds4=4"` passes, then 8 DS4 with `x360=0,ds4=8`.
- **Commit:** `core: pad backend with ViGEm and fake implementations`

#### 2.6 UDP input receiver
- **Depends on:** 2.4, 2.5
- **Design:** Networking protocol (Rules); Performance and latency budget
- **Task:** One UDP socket on 47821 on an above-normal-priority thread with a pooled buffer. Per packet: check magic, version, slot, source address and MAC; drop non-newer sequences; submit to the backend only when state changed; record sentMs for round-trip timing.
  - **Sequences:** follow protocol/SPEC.md, "Packet rules". A packet is compared with the last one *accepted*, so only packets that pass every other check move the mark. The comparison is serial-number order, and the u32 wraps. `input.json`'s `sequenceOrder` cases are the test.
  - **Source address:** SPEC.md doesn't yet define the slot's address. It could be the IP alone, or the IP and port of the first accepted packet; a phone's port changes if its app recreates the socket. Choose one, write it into SPEC.md's packet rules, and add a docs/decisions.md entry (open from 2.1's review).
  - **No session, no read:** `InputPacket.TryRead` throws `ArgumentException` for a session key that isn't 32 bytes, before any header check (`Packet.Check` in `pc/src/PadLink.Protocol/Packet.cs`), and the exception allocates. Drop a packet whose slot or sender has no session before calling it, and say so in `TryRead`'s doc comment (from 2.4's review).
  - **Forged packets:** `AllocationTests.cs` measures only packets that pass. A flood of forged packets from the same Wi-Fi takes the `PacketError.Mac` path, which nothing measures. Add `input.json`'s "mac with its last bit flipped" to the decode test there, and send wrong-MAC packets in the receiver's allocation test too (from 2.4's review).
  - **The fake in the allocation test (from 2.5's review):** on every Submit, `FakePadBackend` (`pc/src/PadLink.Core/Pads/FakePadBackend.cs`) takes a lock, allocates a `PadCall.Submitted` record and a closure in `Find`, and keeps the record. If the receiver's 0-bytes test submits to it, those allocations count. Give the fake a mode that records nothing, or only the last state per pad, or measure against a counting backend written for the test.
  - **Submit and Remove (from 2.5's review):** `IVirtualPadBackend` allows one writer thread per pad, and callers must stop submitting before they remove a pad. A Submit already under way when Remove runs does nothing. A later one throws `ArgumentException`, which allocates, and would end the receive loop if nothing catches it. Drop a slot's packets before its pad goes.
- **Done when:** loopback tests cover valid, wrong MAC, wrong address, replayed, reordered and gapped packets; a test sends 120 packets a second from 16 fake slots for 10 s with 0 bytes allocated per packet after warm-up.
- **Commit:** `core: UDP input receiver`

#### 2.7 TCP control server and pairing
- **Depends on:** 2.4
- **Design:** Networking protocol (Join sequence, Control messages); Security and privacy
- **Task:** TCP listener on 47820, one connection per phone, framed JSON. Implement the join state machine: hello → pair_required/pair or challenge/auth → welcome; 6-digit codes that expire after 5 minutes and regenerate after 5 wrong tries; pairing keys stored with DPAPI (System.Security.Cryptography.ProtectedData); ping/pong every second; bye; 4 KB frame limit. Also follow protocol/SPEC.md, "Join sequence" and "Join rules", added in 2.1's review:
  - A fresh X25519 key pair for each `pair_required`.
  - A successful `pair` uses the code up.
  - Until `welcome`, accept only the expected next message, plus ping, pong, error and bye.
  - A new connection for a `deviceId` that holds a slot takes it over only after passing `pair` or `auth`.
  - An all-zero shared secret gets `bad_message`.
  - Check a frame's length from its 4-byte header before reading the body.
  - **Low-order keys (from 2.4's review):** `crypto.json`'s `rejectedPublicKeys` holds only u=0 and u=1. 2.4's reviewer found that both X25519 libraries also refuse the two points of order 8, p-1, p and p+1, and 0, 1, an order-8 point, p and p+1 with the top bit set, but no vector pins that, so a later library switch could lose it. Add them in `tools/gen_vectors.py`; `CryptoTests.cs` and `crypto_test.dart` already run every entry. Vectors only, so the version stays 1.
  - **Key material (from 2.4's review):** `X25519KeyPair` (`pc/src/PadLink.Protocol/Crypto/X25519KeyPair.cs`) keeps its private key, and `TryAgree` returns the shared secret, in `byte[]`s that nothing overwrites, so a memory dump after pairing holds what derives the pairing key. The review suggests making the key pair `IDisposable`, clearing it with `CryptographicOperations.ZeroMemory`, and clearing the shared secret once HKDF has used it.
  - **C#'s sender-form test (from 2.4's review):** `Encoding_matches_the_vector_bytes_when_its_json_is_in_sender_form` in `ControlFrameTests.cs` returns early for the vectors whose JSON isn't in sender form, so they pass while checking nothing; 2.4 closed the same gap in Dart. Build its MemberData from the sender-form vectors only, and pin the left-out names in a separate test, as `control_test.dart` does.
- **Done when:** state-machine tests cover every message in both orders that matter, an expired code, five wrong codes, an oversize frame and a dropped connection.
- **Commit:** `core: control server and pairing`

#### 2.8 Phone simulator
- **Depends on:** 2.6, 2.7
- **Design:** Testing and compatibility plan (Load and soak)
- **Task:** `PadLink.Simulator` console app that acts as N phones: pairs (with a code passed on the command line), authenticates, streams random human-like input at 120 Hz with 20 Hz keepalive, and prints p50/p95/p99 round-trip time, jitter and loss. Flags: `--phones`, `--seconds`, `--loss`, `--drop-rejoin`, `--json`. This is the tool Claude uses to prove most later iterations without real phones.
- **Done when:** `--phones 4 --seconds 10 --json` against a test host prints valid stats.
- **Commit:** `pc: phone simulator`

#### 2.9 Headless host
- **Depends on:** 2.8
- **Design:** PC companion app design (Internal modules, Slot states)
- **Task:** `PadLink.Host` console app wiring receiver, control server and a basic slot manager (lowest free slot; Empty and Active only for now) to a backend chosen by `--backend fake|vigem`. Prints the pairing code and a slot table.
  - **The fake backend in a long run (from 2.5's review):** `FakePadBackend` keeps every call it records. At 16 phones sending 120 times a second, that grows by about 300 MB an hour, so a `--backend fake` run longer than a test needs the non-recording mode from 2.6's note.
  - **DualShock 4 input check (from 2.5's review):** no test reads input back from a DualShock 4 pad. XInput can't see them, and 2.5's driver tests read only Xbox pads. This host is the first to drive DualShock 4 pads, so with `--backend vigem`, check that a known input arrives with `padprobe --watch`, which prints SDL's button, axis and hat events for every pad. SDL numbers the DualShock 4's buttons and axes its own way, so map them first. `PadMapping.ToDs4` and `Ds4Report.ButtonWord` are already unit-tested against `mapping.json`.
  - **DualShock 4 L2 and R2 bits (from 2.5's review):** SPEC.md maps the triggers to the DualShock 4's analog L2 and R2 only. A real DualShock 4 also sets the L2 and R2 button bits when they're pressed. Games that read DualShock 4 pads directly, not through Steam Input, may look only at those bits. Decide before the 3.8 playtest whether to set them, and how far down. Setting them changes SPEC.md's mapping table and `mapping.json`, and needs a docs/decisions.md entry. Senders don't change, so the version stays 1.
- **Done when:** an end-to-end test runs the host with the fake backend and the simulator with 4 phones, then asserts 4 slots, 4 pads and no stuck input. With `--backend vigem`, `padprobe --expect "x360=0,ds4=4"` passes (all-DS4 is temporary until 3.2).
- **Commit:** `pc: headless host`

#### 2.10 PC discovery
- **Depends on:** 2.9
- **Design:** Networking protocol (Ports and discovery); PC companion app design
- **Task:** Advertise `_padlink._tcp` on 47820 with TXT `proto=1`, `name`, `id` using Windows `DnsServiceRegister` through P/Invoke; unregister on exit. Generate the QR payload string. docs/design.md disagrees with itself here: "Networking protocol" and protocol/SPEC.md put `id` in both the TXT record and the QR payload. "PC companion app design" leaves it out of both: the Discovery module row, and the QR code in "Main window". Align those two places with SPEC.md (open from 2.1's review).
- **Done when:** a Python browse script using the `zeroconf` package finds the service with the right TXT record while the host runs.
- **Commit:** `pc: Bonjour advertising`

#### 2.11 Flutter app scaffold
- **Depends on:** 2.3
- **Design:** Mobile app design; Platform considerations
- **Task:** `flutter create` in `mobile/app` (org placeholder `com.example`, change later). Add Riverpod and go_router, empty screens for Find PC, Pair, Lobby, Controller and Settings, a theme, and a dependency on the local protocol package. Set Android manifest permissions and iOS Info.plist keys exactly as listed in the design. Lock to landscape on the Controller screen. Android builds need NDK 28.2.13676358. Gradle's automatic install of it fails, and `tools/doctor.ps1` doesn't check for it (docs/gates/M0.md, "Carried forward").
- **Done when:** `flutter analyze` is clean; a widget test navigates through all 5 screens.
- **Commit:** `mobile: app scaffold`

#### 2.12 Phone connection layer
- **Depends on:** 2.10, 2.11
- **Design:** Networking protocol; Mobile app design (Screen flow)
- **Task:** Discovery with bonsoir, manual IP entry, QR scan with mobile_scanner, the TCP control client and join flow, keys in flutter_secure_storage, and the UDP sender (send on button change, 120 Hz frame-synced stick updates, 20 Hz keepalive). Wire Find PC → Pair → Lobby. Keep the connection core in pure Dart with the plugins behind interfaces, so it can run outside Flutter. 2.4's review found that `X25519KeyPair` in `padlink_protocol`'s `lib/src/crypto.dart` keeps its private key, and `agree` returns the shared secret, in bytes that nothing overwrites. Once the pairing key is derived, clear the shared secret and destroy the key pair (the `cryptography` package's `SimpleKeyPairData.destroy()` overwrites its bytes), as 2.7's note asks on the PC.
- **Done when:** Dart tests pass against an in-process fake server; `tools/e2e_phone.ps1` runs the connection core with `dart run` against `PadLink.Host --backend fake` and the host shows the slot.
- **Commit:** `mobile: discovery, pairing and input sender`

#### 2.13 Standard controller layout
- **Depends on:** 2.12
- **Design:** Mobile app design (Built-in layouts, Touch handling, Button labels)
- **Task:** One CustomPainter for the Standard layout: floating sticks (15 mm travel, 8% deadzone), buttons of 12 mm or larger with a 2 mm extended hit area, per-pointer tracking with slide-to-press, a 10 ms haptic tick, and labels taken from the slot's pad type. Touches update a state object without rebuilding widgets.
- **Done when:** widget tests simulate multi-finger gestures and assert the exact input state, including slide-across-buttons and a stick drag to each edge.
- **Commit:** `mobile: Standard controller layout`

#### 2.14 **Gate M1**
- **Depends on:** 2.9, 2.13
- **Design:** Roadmap (M1 gate); Performance and latency budget
- **Task:** Write `tools/gate_m1.ps1`: starts the host with ViGEm, runs the simulator with 3 phones, and collects round-trip stats from the host for every slot. Draft `docs/gates/M1.md`.
- **👤** Run the gate script, install the app on your Android phone (`flutter run --release`), pair it as the 4th phone, and play for 10 minutes.
- **Done when:** the host reports p95 under 25 ms for all 4 slots including your phone, and `docs/gates/M1.md` has the numbers.
- **Commit:** `docs: M1 gate`

### Phase 3 — Two modes and 8 players (M2)

#### 3.1 Full slot manager
- **Depends on:** 2.14
- **Design:** PC companion app design (Slot states); Requirements
- **Task:** Add Switching and Grace states, a 2 s silence timeout, a 60 s grace hold with a neutral report sent once, reconnect into the same slot, swap and kick. Use an injectable clock.
- **Done when:** tests with a fake clock cover every transition in the slot-state table; the simulator's `--drop-rejoin` run shows each phone back in its own slot.
- **Commit:** `core: full slot manager`

#### 3.2 Mode manager
- **Depends on:** 3.1
- **Design:** Controller modes (all of it); Virtual controller layer
- **Task:** Mixed and All DualShock 4 modes; the switch sequence (neutral reports → unplug all → plug Xbox pads first in slot order, then DS4 → `slot_changed` per phone); `mode_changed` broadcast; drop packets for a slot while Switching; XInput user-index check with a warning that names any extra physical Xbox pad; persist the current mode.
  - **User index (from 2.5):** 2.5 settled the user-index questions in docs/gates/M0.md, "Carried forward". See docs/decisions.md, "An Xbox pad's user index comes from the driver's LED notification", and the Xbox input check in `ViGEmPadBackendTests.cs`.
    - `ReadUserIndex` returns null until the driver's first notification, about 20 ms after `Create`, so wait for each pad's index before comparing, as `PadBackendContract.WaitForUserIndex` does. A fifth Xbox pad's notification carries 255, which reads as null.
    - `FakePadBackend` gives the index at once. To test a late index, or one shifted by a physical Xbox pad, call `RaiseFeedback` with a `LedNumber`. Add a null-until-notified option to the fake if the tests need one.
  - **Dropping packets while Switching (from 2.5's review):** stop a slot's Submits before its pad is removed. A Submit already under way when Remove runs does nothing, and a later one throws `ArgumentException` (`IVirtualPadBackend`'s remarks).
- **Done when:** fake-backend tests assert the exact call order for both directions of switch and that slots, colours and pairings are unchanged; a `RequiresViGEm` test switches with 8 simulated phones in under 2 s and `padprobe --expect` matches each mode.
- **Commit:** `core: controller mode manager`

#### 3.3 Phone mode awareness
- **Depends on:** 3.2, 2.13
- **Design:** Mobile app design (Screen flow, Button labels follow the pad type)
- **Task:** Handle `welcome.mode`, `mode_changed` and `slot_changed`; show the pad type and mode in the Lobby; swap button labels per the design's table; show the 2-second "You're now a DualShock 4 pad" banner.
- **Done when:** widget tests show the right labels for both pad types and the banner on a change; button positions are identical before and after.
- **Commit:** `mobile: mode and pad-type awareness`

#### 3.4 Rumble and slot colour
- **Depends on:** 3.2, 2.12
- **Design:** Mobile app design (Tilt and rumble); Networking protocol (Feedback packet)
- **Task:** Feedback relay from each pad's feedback event to a 19-byte UDP packet; on the phone, a platform channel for Android VibrationEffect amplitudes and iOS Core Haptics continuous events; the DS4 lightbar shows the slot colour.
  - **DualShock 4 feedback (from 2.5):** Nefarius.ViGEm.Client 1.21.256 marks `IDualShock4Controller.FeedbackReceived` obsolete ("might not behave as expected") in favour of `AwaitRawOutputReport()`. That is a blocking read of the game's raw output report, so it needs a thread per DualShock 4 pad and code that parses the rumble and lightbar bytes. `ViGEmPadBackend` still uses the event, with the warning suppressed on that line. Pick one here, test it on the driver, and record the choice in docs/decisions.md.
  - **Feedback handlers (from 2.5's review):** the backend raises `Feedback` on the driver's notification thread, where an exception ends the process, so the relay must catch its own errors. An Xbox pad's first feedback, with its LED number and no rumble, arrives within about 20 ms of `Create`, possibly before `Create` returns. In tests, `FakePadBackend.RaiseFeedback` raises it on the caller's thread.
- **Done when:** relay tests assert packet bytes from fake feedback events; Dart tests assert the channel calls; a `RequiresViGEm` test triggers rumble and sees the feedback packet.
- **👤** Feel the rumble on your phone using any rumble test tool or a target game.
- **Commit:** `feat: rumble and slot colour feedback`

#### 3.5 Desktop app
- **Depends on:** 3.2
- **Design:** PC companion app design (Main window, Startup checks)
- **Task:** `PadLink.Desktop` WPF app hosting the core: tray icon, main window with the mode switch, pairing code and QR (QRCoder package), 16 slot cards (player, colour, phone name, pad type, latency, battery, Swap, Kick), the first-run mode picker with Mixed pre-selected, the Steam checklist, and the confirmation dialog when switching while a game runs. MVVM with testable view models; single instance.
- **Done when:** view-model tests cover mode switching, slot updates and first run; the app starts and closes cleanly from the command line.
- **👤** Click through the window and the first-run flow; report anything confusing.
- **Commit:** `pc: desktop app`

#### 3.6 iOS build
- **Depends on:** 3.3
- **Design:** Platform considerations (iOS)
- **Task:** Make the app build for iOS: Info.plist keys, Bonjour service type, no multicast use, the "local network denied" screen with a Settings link, and reconnect on return to the foreground. Add a macOS CI job running `flutter build ios --no-codesign`.
- **Done when:** the macOS CI job passes.
- **👤** If you have a Mac and an iPhone, run it on the device and pair with the PC.
- **Commit:** `mobile: iOS build`

#### 3.7 Soak and fault runs
- **Depends on:** 3.2
- **Design:** Testing and compatibility plan (Load and soak)
- **Task:** `tools/soak.ps1` runs the simulator for 60 minutes with 16 phones in each mode, then the fault runs: 5% and 20% loss, drop and rejoin, a drop during a mode switch. Writes `docs/gates/soak-<date>.md` with pass/fail per threshold.
- **Done when:** zero stuck inputs, p95 under 25 ms, PC CPU under 2% of one core, every switch under 2 s. Use `/goal` in auto mode for the long run if you like.
- **Commit:** `test: soak and fault runs`

#### 3.8 **Gate M2**
- **Depends on:** 3.3, 3.4, 3.5, 3.7
- **Design:** Roadmap (M2 gate); Testing and compatibility plan (Game compatibility)
- **Task:** Prepare `docs/gates/M2.md` and `docs/compatibility.md` with an empty results table per target game and mode.
- **👤** With 8 phones (real where possible, simulator for the rest), play each target game for 60 minutes in Mixed and again in All DualShock 4, with PlayStation Configuration Support on. Switch modes once mid-session. Report players seen per mode and any problems; Claude fills in the tables.
- **Done when:** both modes pass in at least one target game with 8 players, and every result is recorded.
- **Commit:** `docs: M2 gate and compatibility results`

### Phase 4 — Polish (M3)

#### 4.1 Per-game mode profiles
- **Depends on:** 3.8
- **Design:** Controller modes (How the host chooses, step 3); PC companion app design
- **Task:** Profiles keyed by executable name, a Game profiles tab with "Add running game", and a switch to the saved mode when the game starts. The design names WMI `Win32_ProcessStartTrace`, which needs admin rights; if the app runs unelevated, poll the process list every 2 s instead. Record the choice in `docs/decisions.md`. Seed defaults from `docs/compatibility.md`.
- **Done when:** tests with a fake process source switch modes on start and only when the mode differs.
- **Commit:** `pc: per-game mode profiles`

#### 4.2 Retro and Racing layouts
- **Depends on:** 3.3
- **Design:** Mobile app design (Built-in layouts, Tilt and rumble)
- **Task:** Retro layout; Racing layout with gyroscope tilt at 100 Hz through a complementary filter mapped to the left stick X axis, throttle on RT, brake on LT, and a Recenter button.
- **Done when:** widget tests for both layouts; filter unit tests with recorded sensor samples.
- **Commit:** `mobile: Retro and Racing layouts`

#### 4.3 Layout editor
- **Depends on:** 4.2
- **Design:** Mobile app design (Layout editor)
- **Task:** Drag, resize and opacity per control on a 4 mm grid; save as JSON; export and import as a file; layouts work in both modes.
- **Done when:** tests round-trip a layout through JSON and through a simulated drag.
- **Commit:** `mobile: layout editor`

#### 4.4 Diagnostics and logs
- **Depends on:** 3.5
- **Design:** PC companion app design (Diagnostics); Requirements (item 13)
- **Task:** Per-slot p50/p95 latency, loss, jitter and battery in the slot cards; rotating log files; an "Export logs" button on the PC and the phone for bug reports.
- **Done when:** stats tests against known inputs; the simulator run shows matching numbers in the host's diagnostics output.
- **Commit:** `feat: diagnostics and log export`

#### 4.5 Startup checks and help screens
- **Depends on:** 3.5, 3.6
- **Design:** PC companion app design (Startup checks); Platform considerations
- **Task:** PC: ViGEmBus check with an install offer (bundled setup or winget), firewall profile check with a Public-network warning, client-isolation hint, hotspot fallback steps. Phone: local-network-denied (iOS), battery-saver tips (Android), "can't find PC" checklist. Also from M0 (docs/gates/M0.md, "Carried forward"): check that the PC and the phone are on the same subnet, and warn when the phone's USB tethering is on. In the spike, tethering took the PC off Wi-Fi.
- **Done when:** each check has a test with a faked environment.
- **Commit:** `feat: startup checks and help`

#### 4.6 Security pass
- **Depends on:** 2.7, 2.6
- **Design:** Security and privacy
- **Task:** Re-check code expiry, attempt limits and key storage; add a randomized fuzz test for the UDP parser and the frame reader (10 million inputs, no exceptions, no allocations on the drop path); confirm a phone can't change the mode. Ask the reviewer subagent for a security-only review of the pairing and packet code. Feedback packets carry no sequence, so a feedback packet can be replayed within a session, for example full rumble. The threat table's replay row covers input only. Either add the threat and its acceptance to "Security and privacy", or add a sequence to feedback. That is a wire change: bump the packet version and follow the protocol change order in CLAUDE.md (open from 2.1's review).
- **Done when:** fuzz tests pass and every reviewer finding is fixed or recorded in `docs/decisions.md`.
- **Commit:** `security: hardening pass`

#### 4.7 Windows installer
- **Depends on:** 3.5
- **Design:** Distribution, licensing and legal; Platform considerations (Windows)
- **Task:** `installer/padlink.iss` (Inno Setup): installs the desktop app, adds firewall rules for TCP 47820 and UDP 47821 on the Private profile, chains the official ViGEmBus 1.22.0 setup when missing, adds an About/licences page with the ViGEmBus, Nefarius.ViGEm.Client and Flutter notices, and uninstalls cleanly. Build it in CI.
- **Done when:** CI produces the installer artifact.
- **👤** Install and uninstall on a clean Windows machine or VM.
- **Commit:** `build: Windows installer`

#### 4.8 **Gate M3**
- **Depends on:** 4.1–4.7
- **Design:** Requirements; Testing and compatibility plan (Device and network matrix; Checking what Windows sees)
- **Task:** Generate `docs/gates/M3.md`: one row per functional and non-functional requirement, with the test, script or gate file that proves it, and blanks where only a person can confirm. Add rows for the design's two Windows checks, in both modes: SDL3's controller test program (every pad appears, with correct buttons) and joy.cpl (DirectInput visibility). M0 counted the pads with padprobe only (docs/gates/M0.md).
- **👤** Run the device and network matrix and the two Windows checks, and fill the blanks.
- **Done when:** every requirement row is proven or has an agreed exception in `docs/decisions.md`.
- **Commit:** `docs: M3 gate`

### Phase 5 — Beta and launch (M4)

#### 5.1 Release pipelines
- **Depends on:** 4.8
- **Design:** Distribution, licensing and legal (Channels); Tech stack (Shared and tooling)
- **Task:** Release workflow on version tags: signed Android AAB to the Play internal track, signed iOS IPA to TestFlight (Fastlane), signed Windows installer to GitHub Releases. All signing material comes from GitHub secrets; nothing secret is committed. Write `docs/release.md`.
- **👤** Create the store apps, signing keys, code-signing certificate and GitHub secrets.
- **Done when:** a test tag produces all three artifacts.
- **Commit:** `ci: release pipelines`

#### 5.2 Store listings and compliance
- **Depends on:** 5.1
- **Design:** Distribution, licensing and legal; Security and privacy
- **Task:** Draft store descriptions that name pad types plainly ("Xbox 360-compatible", "DualShock 4-compatible"), a privacy policy page stating no data is collected, answers for Google's data safety form and Apple's privacy labels, review notes for Apple with a demo-video script, and screenshot captions.
- **👤** Capture screenshots, record the demo video, and paste the listings into the store consoles.
- **Done when:** all drafts are in `docs/store/`.
- **Commit:** `docs: store listings and privacy`

#### 5.3 Beta
- **Depends on:** 5.2
- **Design:** Risks and mitigations
- **Task:** Each session: read tester reports and exported logs the user drops into `beta/`, reproduce with the simulator where possible, write a failing test first, fix, and add the result to `docs/beta.md`.
- **👤** Run the beta with real players for at least two game nights.
- **Done when:** no open crash or stuck-input bugs, and every risk in the design's table has a status in `docs/beta.md`.
- **Commit:** `fix: beta round <n>` per round

#### 5.4 **Gate M4** — launch
- **Depends on:** 5.3
- **Design:** Roadmap (M4 gate)
- **Task:** Produce the launch checklist in `docs/gates/M4.md` from every earlier gate, then bump versions and tag v1.0.0.
- **👤** Submit to both stores, publish the installer, and confirm approvals.
- **Done when:** both stores have approved the app and the signed installer is public.
- **Commit:** `release: v1.0.0`

---

## 7. After launch (v2 backlog)

Add each as a new iteration block in PLAN.md when you start it: 16-player support across the UI and tests; the vJoy backend for DirectInput-only games; SPAKE2 pairing; gyro passthrough; a phone-side "request mode change" for the host to approve.

## 8. Steps only you can do

| When | Step |
| --- | --- |
| Setup | Install the toolchain and ViGEmBus (admin) |
| 0.3 | Create the GitHub repo and push |
| 1.3, 2.14 | Run the app on your phone |
| 1.4 | Choose the 3 target games |
| 3.4, 3.5 | Feel the rumble; click through the desktop app |
| 3.6 | Run on an iPhone (needs a Mac) |
| 3.8, 4.8 | Group playtests and the device matrix |
| 4.7 | Test the installer on a clean machine |
| 5.1–5.4 | Store accounts, signing, screenshots, submissions, beta players |

## Sources

- [Claude Code best practices](https://code.claude.com/docs/en/best-practices.md)
- [Claude Code skills](https://code.claude.com/docs/en/skills.md)
- [Claude Code /goal](https://code.claude.com/docs/en/goal.md)
- [Claude Code docs map](https://code.claude.com/docs/en/claude_code_docs_map.md)
