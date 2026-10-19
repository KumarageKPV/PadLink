# PadLink — Progress

One box per PLAN.md iteration. `/next` works on the first unticked box; `/finish` ticks it as
`- [x] <id> <title> — YYYY-MM-DD — <one-line summary>`.

## Phase 0 — Foundations

- [x] 0.1 Bootstrap the repo and Claude Code setup — 2026-10-19 — folder layout, CLAUDE.md, .claude settings/format hook/skills/reviewer agent, PROGRESS.md, decisions log, README, .gitignore
- [x] 0.2 Toolchain doctor — 2026-10-19 — tools/doctor.ps1 checks 9 items and prints ok/missing with fixes; ViGEmBus check runs tools/vigem_check.py and requires the 1.22.0 driver (file 1.21.442.0); pygame-ce chosen
- [x] 0.3 CI skeleton — 2026-10-19 — ci.yml: a detect job gates the pc (Windows), mobile and python (Ubuntu) jobs on marker files and fails on stale ones; NuGet, pub and pip caches; the python job also runs tools/tests

## Phase 1 — Spike (M0)

- [ ] 1.1 Pad probe
- [ ] 1.2 Spike: create both pad mixes
- [ ] 1.3 Spike: a phone drives a pad
- [ ] 1.4 Gate M0 and target games

## Phase 2 — Core (M1)

- [ ] 2.1 Protocol spec and shared test vectors
- [ ] 2.2 C# solution and protocol library
- [ ] 2.3 Dart protocol package
- [ ] 2.4 Crypto on both sides
- [ ] 2.5 Pad backend and mappings
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
