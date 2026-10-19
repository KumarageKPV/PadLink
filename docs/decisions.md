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
