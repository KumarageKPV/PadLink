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
- **Decision:** Keep the git-ignored `.venv` at the repo root, with its packages pinned in `requirements.txt`. Activate it before running any `python` command or starting Claude Code. README.md, "Python tools", has the setup steps.
- **Alternatives:** Install the packages into the system Python. Rejected: unpinned, and shared with every other project. Have `tools/doctor.ps1` find `.venv` on its own. Not done: activating covers every `python` command, not only the doctor.
- **Design change:** none
