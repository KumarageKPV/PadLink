# PadLink

PadLink turns Android and iOS phones into game controllers for one Windows PC over local Wi-Fi, for up to 8 players in v1.
A Flutter phone app streams input to a Windows companion app, which creates virtual Xbox 360 and DualShock 4 pads.
The host picks Mixed (4 Xbox + DualShock 4) or All DualShock 4 mode per game; see `docs/design.md` for the design and `PLAN.md` for the build plan.

## Python tools

The Python tools (`tools/*.py`, and `spike/` during M0) run from a virtual environment in `.venv`. It is built on Python 3.14 and holds every package they need, pinned in `requirements.txt`. The system Python has none of them.

Activate it before running `python tools/...` or `tools/doctor.ps1`, or before starting Claude Code so its commands use it too:

- PowerShell: `.venv\Scripts\Activate.ps1`
- Git Bash: `source .venv/Scripts/activate`

`.venv` is git-ignored, so each machine makes its own:

```powershell
py -3.14 -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
```

Installing vgamepad opens its bundled ViGEmBus 1.17 setup. Cancel it: PadLink needs ViGEmBus 1.22.0 (`winget install --id ViGEm.ViGEmBus -e`, as admin), and `tools/doctor.ps1` fails on an older driver.
