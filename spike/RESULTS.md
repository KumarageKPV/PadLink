# Spike results

Raw results from the M0 spike, for `docs/gates/M0.md` (PLAN.md 1.4).

## 1.2 — Create both pad mixes

**Setup:** 2026-10-19, dev PC, Windows 11 10.0.26300.9711. ViGEmBus 1.22.0 setup (driver file 1.21.442.0), vgamepad 0.1.0 (its ViGEmClient.dll reports version 1.0.0.1), pygame-ce 2.5.8 with SDL 2.32.10, Python 3.14.8. No other controllers were connected, and Steam was not running.

**How each run was checked:** start `python spike/pads.py --mode <mode> --count <n> --seconds 20` and wait for its `ready` line. Then run `python tools/padprobe.py --expect "<counts>"` while the pads are held. Once pads.py exits, run `python tools/padprobe.py --expect "x360=0,ds4=0,other=0"`.

A helper script, not in the repo, ran these steps in that order:
- It timestamped pads.py's lines from launch.
- It would have retried a failing probe for up to 15 s. None needed it: each passed on its first try, 0.6–0.9 s after `ready`.
- It also printed a type count of padprobe's JSON list.

Everything under [Raw output](#raw-output) is verbatim.

| Run | pads.py `ready` line, and exit code | Probe while held (stderr) | After exit |
| --- | --- | --- | --- |
| mixed / 8 | `"asked": 8, "x360": 4, "ds4": 4`, exit 0 | `--expect "x360=4,ds4=4"` → `ok: x360=4,ds4=4`, exit 0 | `ok: x360=0,ds4=0,other=0`, exit 0 |
| all_ds4 / 8 | `"asked": 8, "x360": 0, "ds4": 8`, exit 0 | `--expect "x360=0,ds4=8"` → `ok: x360=0,ds4=8`, exit 0 | `ok: x360=0,ds4=0,other=0`, exit 0 |
| mixed / 16 | `"asked": 16, "x360": 4, "ds4": 12`, exit 0 | `--expect "x360=4,ds4=12"` → `ok: x360=4,ds4=12`, exit 0 | `ok: x360=0,ds4=0,other=0`, exit 0 |
| all_ds4 / 16 | `"asked": 16, "x360": 0, "ds4": 16`, exit 0 | `--expect "x360=0,ds4=16"` → `ok: x360=0,ds4=16`, exit 0 | `ok: x360=0,ds4=0,other=0`, exit 0 |

- **Pads listed:** every Xbox pad was "Xbox 360 Controller" 045e:028e, and every DualShock 4 was "PS4 Controller" 054c:05c4. No `other` pads appeared.
- **Speed:** creating 16 pads took about 0.2 s, roughly 10–15 ms a pad (see the timestamps below). An earlier set of the same four runs, made before the review fixes, took up to 0.42 s, or 10–40 ms a pad.
- **XInput view:** for both mixed runs, `SDL_JOYSTICK_RAWINPUT=0` (SDL reading Xbox pads through XInput, as XInput-only games do) also listed all 4 Xbox pads. The DualShock 4 pads were listed as well (4 and 12).

### Reading back an Xbox pad's XInput user index

docs/design.md ("Virtual controller layer", Rules) says to create the Xbox pads in slot order, then read each pad's XInput user index back from the driver and warn if it doesn't match the player number. pads.py reads it back two ways once every pad is created (0.2 s after slot 1 in mixed / 16), and both mixed runs gave the same result:

| Slot | `vigem_target_x360_get_user_index` (`user_index`) | LED number in the pad's notification (`led_number`) |
| --- | --- | --- |
| 1 | 0 | 0 |
| 2 | 0 | 1 |
| 3 | 0 | 2 |
| 4 | 0 | 3 |

- **The user-index call** returned success (`0x20000000`) with index 0 for all four pads.
- **The LED number** from vgamepad's notification callback matched the slot every time.
- **One-off checks**, made with scratch scripts during 1.2 and not part of pads.py:
  - The user-index call still gave 0 after 3.4 s, and after another program had polled XInput.
  - With the output seeded to 99, it still came back as 0. That only shows the client wrote an output, not that the driver filled it in. If ViGEmClient copies the index from its own zero-filled request, a driver that never sets it would also give 0.
  - `XInputGetState` reported user indices 0–3 connected. It shows which indices are in use, not which pad holds each one.
  - Each pad's first LED notification came within 20 ms of creating it.
- **For 2.5 (pad backend):** check this again with Nefarius.ViGEm.Client: compare its `UserIndex` property with the `LedNumber` in its `FeedbackReceived` event. If `UserIndex` also reads 0, the rule as written would warn about players 2–4 on every Mixed start. In that case 2.5 takes the player number from `LedNumber`, and updates the docs/design.md rule with a docs/decisions.md entry in the same commit.

### Raw output

```text
$ python spike/pads.py --mode mixed --count 8 --seconds 20
  [ 0.33 s] {"event": "created", "slot": 1, "type": "x360"}
  [ 0.35 s] {"event": "created", "slot": 2, "type": "x360"}
  [ 0.37 s] {"event": "created", "slot": 3, "type": "x360"}
  [ 0.38 s] {"event": "created", "slot": 4, "type": "x360"}
  [ 0.40 s] {"event": "created", "slot": 5, "type": "ds4"}
  [ 0.41 s] {"event": "created", "slot": 6, "type": "ds4"}
  [ 0.43 s] {"event": "created", "slot": 7, "type": "ds4"}
  [ 0.44 s] {"event": "created", "slot": 8, "type": "ds4"}
  [ 0.44 s] {"event": "xinput", "slot": 1, "user_index": 0, "led_number": 0}
  [ 0.44 s] {"event": "xinput", "slot": 2, "user_index": 0, "led_number": 1}
  [ 0.44 s] {"event": "xinput", "slot": 3, "user_index": 0, "led_number": 2}
  [ 0.44 s] {"event": "xinput", "slot": 4, "user_index": 0, "led_number": 3}
  [ 0.44 s] {"event": "ready", "mode": "mixed", "asked": 8, "x360": 4, "ds4": 4, "seconds": 20.0}

$ python tools/padprobe.py --expect "x360=4,ds4=4"
  ok: x360=4,ds4=4  (exit 0; try 1, 0.9 s after ready)
  all types in its JSON list: {'ds4': 4, 'x360': 4}
    4 x ds4 "PS4 Controller" 054c:05c4
    4 x x360 "Xbox 360 Controller" 045e:028e

$ SDL_JOYSTICK_RAWINPUT=0 python tools/padprobe.py   (the XInput view)
  types: {'ds4': 4, 'x360': 4}  (exit 0)
  [20.52 s] {"event": "removed", "count": 8}
  spike/pads.py exit 0

$ python tools/padprobe.py --expect "x360=0,ds4=0,other=0"   (after pads.py exits)
  ok: x360=0,ds4=0,other=0  (exit 0)
```

```text
$ python spike/pads.py --mode all_ds4 --count 8 --seconds 20
  [ 0.23 s] {"event": "created", "slot": 1, "type": "ds4"}
  [ 0.25 s] {"event": "created", "slot": 2, "type": "ds4"}
  [ 0.26 s] {"event": "created", "slot": 3, "type": "ds4"}
  [ 0.27 s] {"event": "created", "slot": 4, "type": "ds4"}
  [ 0.28 s] {"event": "created", "slot": 5, "type": "ds4"}
  [ 0.30 s] {"event": "created", "slot": 6, "type": "ds4"}
  [ 0.31 s] {"event": "created", "slot": 7, "type": "ds4"}
  [ 0.32 s] {"event": "created", "slot": 8, "type": "ds4"}
  [ 0.32 s] {"event": "ready", "mode": "all_ds4", "asked": 8, "x360": 0, "ds4": 8, "seconds": 20.0}

$ python tools/padprobe.py --expect "x360=0,ds4=8"
  ok: x360=0,ds4=8  (exit 0; try 1, 0.6 s after ready)
  all types in its JSON list: {'ds4': 8}
    8 x ds4 "PS4 Controller" 054c:05c4
  [20.37 s] {"event": "removed", "count": 8}
  spike/pads.py exit 0

$ python tools/padprobe.py --expect "x360=0,ds4=0,other=0"   (after pads.py exits)
  ok: x360=0,ds4=0,other=0  (exit 0)
```

```text
$ python spike/pads.py --mode mixed --count 16 --seconds 20
  [ 0.22 s] {"event": "created", "slot": 1, "type": "x360"}
  [ 0.24 s] {"event": "created", "slot": 2, "type": "x360"}
  [ 0.25 s] {"event": "created", "slot": 3, "type": "x360"}
  [ 0.27 s] {"event": "created", "slot": 4, "type": "x360"}
  [ 0.28 s] {"event": "created", "slot": 5, "type": "ds4"}
  [ 0.30 s] {"event": "created", "slot": 6, "type": "ds4"}
  [ 0.31 s] {"event": "created", "slot": 7, "type": "ds4"}
  [ 0.32 s] {"event": "created", "slot": 8, "type": "ds4"}
  [ 0.33 s] {"event": "created", "slot": 9, "type": "ds4"}
  [ 0.34 s] {"event": "created", "slot": 10, "type": "ds4"}
  [ 0.35 s] {"event": "created", "slot": 11, "type": "ds4"}
  [ 0.37 s] {"event": "created", "slot": 12, "type": "ds4"}
  [ 0.38 s] {"event": "created", "slot": 13, "type": "ds4"}
  [ 0.39 s] {"event": "created", "slot": 14, "type": "ds4"}
  [ 0.40 s] {"event": "created", "slot": 15, "type": "ds4"}
  [ 0.41 s] {"event": "created", "slot": 16, "type": "ds4"}
  [ 0.41 s] {"event": "xinput", "slot": 1, "user_index": 0, "led_number": 0}
  [ 0.41 s] {"event": "xinput", "slot": 2, "user_index": 0, "led_number": 1}
  [ 0.41 s] {"event": "xinput", "slot": 3, "user_index": 0, "led_number": 2}
  [ 0.41 s] {"event": "xinput", "slot": 4, "user_index": 0, "led_number": 3}
  [ 0.41 s] {"event": "ready", "mode": "mixed", "asked": 16, "x360": 4, "ds4": 12, "seconds": 20.0}

$ python tools/padprobe.py --expect "x360=4,ds4=12"
  ok: x360=4,ds4=12  (exit 0; try 1, 0.8 s after ready)
  all types in its JSON list: {'ds4': 12, 'x360': 4}
    12 x ds4 "PS4 Controller" 054c:05c4
    4 x x360 "Xbox 360 Controller" 045e:028e

$ SDL_JOYSTICK_RAWINPUT=0 python tools/padprobe.py   (the XInput view)
  types: {'ds4': 12, 'x360': 4}  (exit 0)
  [20.52 s] {"event": "removed", "count": 16}
  spike/pads.py exit 0

$ python tools/padprobe.py --expect "x360=0,ds4=0,other=0"   (after pads.py exits)
  ok: x360=0,ds4=0,other=0  (exit 0)
```

```text
$ python spike/pads.py --mode all_ds4 --count 16 --seconds 20
  [ 0.24 s] {"event": "created", "slot": 1, "type": "ds4"}
  [ 0.25 s] {"event": "created", "slot": 2, "type": "ds4"}
  [ 0.26 s] {"event": "created", "slot": 3, "type": "ds4"}
  [ 0.28 s] {"event": "created", "slot": 4, "type": "ds4"}
  [ 0.29 s] {"event": "created", "slot": 5, "type": "ds4"}
  [ 0.30 s] {"event": "created", "slot": 6, "type": "ds4"}
  [ 0.31 s] {"event": "created", "slot": 7, "type": "ds4"}
  [ 0.32 s] {"event": "created", "slot": 8, "type": "ds4"}
  [ 0.33 s] {"event": "created", "slot": 9, "type": "ds4"}
  [ 0.35 s] {"event": "created", "slot": 10, "type": "ds4"}
  [ 0.36 s] {"event": "created", "slot": 11, "type": "ds4"}
  [ 0.37 s] {"event": "created", "slot": 12, "type": "ds4"}
  [ 0.38 s] {"event": "created", "slot": 13, "type": "ds4"}
  [ 0.39 s] {"event": "created", "slot": 14, "type": "ds4"}
  [ 0.40 s] {"event": "created", "slot": 15, "type": "ds4"}
  [ 0.42 s] {"event": "created", "slot": 16, "type": "ds4"}
  [ 0.42 s] {"event": "ready", "mode": "all_ds4", "asked": 16, "x360": 0, "ds4": 16, "seconds": 20.0}

$ python tools/padprobe.py --expect "x360=0,ds4=16"
  ok: x360=0,ds4=16  (exit 0; try 1, 0.9 s after ready)
  all types in its JSON list: {'ds4': 16}
    16 x ds4 "PS4 Controller" 054c:05c4
  [20.49 s] {"event": "removed", "count": 16}
  spike/pads.py exit 0

$ python tools/padprobe.py --expect "x360=0,ds4=0,other=0"   (after pads.py exits)
  ok: x360=0,ds4=0,other=0  (exit 0)
```
