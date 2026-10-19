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
