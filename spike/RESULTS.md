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

## 1.3 — A phone drives a pad

**Setup:** 2026-10-19, the same dev PC and packages as 1.2. No other controllers were connected, and Steam was not running.

**How it was checked:** a helper script, not in the repo, ran these steps in order:
1. Start `python spike/server.py --seconds 25` and wait for its `ready` line.
2. Start `python tools/padprobe.py --watch --seconds 12` and wait for its `added` line.
3. Run `python spike/send_fake.py`: 10 steps of 0.5 s at 60 packets a second, over loopback.
4. Once the server exits, run `python tools/padprobe.py --expect "x360=0,ds4=0,other=0"`.

Each line is stamped with the helper's clock when it read the line from the program's pipe. Everything under [Raw output](#raw-output-1) is verbatim.

| send_fake step | Packet | padprobe `--watch` saw on the DS4 pad |
| --- | --- | --- |
| right | `lx` 1 | axis 0 → 1.0 |
| left | `lx` -1 | axis 0 → -1.0 |
| up | `ly` 1 | axis 0 → 0.0; axis 1 → 0.0, then -1.0 in the same poll (the first line padprobe printed for axis 1) |
| down | `ly` -1 | axis 1 → 1.0 |
| neutral | — | axis 1 → 0.0 |
| a, then neutral | `a` true, then false | button 0 pressed, then released |
| b, then neutral | `b` true, then false | button 1 pressed, then released |

- **Y is flipped once, in the right place.** A packet's `ly` = 1 means up. server.py writes DS4 byte 0 for it, which is up on a DS4. SDL reports up as -1, so axis 1 → -1.0 is correct.
- **Buttons:** `a` is DS4 Cross, which SDL lists as button 0, and `b` is Circle, button 1.
- **Packets:** the server applied 300 of 300, with none stale or bad. It set the pad neutral 0.56 s after the last one (send_fake's `done` at 6.76 s, `neutral` at 7.32 s): the 0.5 s silence, plus up to one 50 ms receive timeout. No pads were left after it exited.
- **Timing:** each padprobe line came about 20 ms after send_fake printed its step. That includes padprobe's 10 ms poll and both pipes, so it only shows that moves arrive promptly. It is not a measure for the latency budget.
- **First packet:** when the first (neutral) packet landed, SDL also reported axis 0 at 0.02 and axis 2 at -0.01, each then 0.0. The server never sets axis 2 (the right stick), so this was probably SDL settling on the pad's first reports. Both values were within 0.02 of centre.
- **padprobe fix:** `--watch` first printed nothing for this pad. SDL drops the added events of pads already connected when its joystick system starts before the video system, and padprobe starts them in that order. Xbox pads read through XInput are found by a later poll, so 1.1's checks didn't hit this. `--watch` now lists the pads already connected when it starts, and skips an added event for a pad it already has.
- **Phone:** see [Phone run](#phone-run).

### Phone run

**Result: pass.** On 2026-10-20, with the spike app sending over Wi-Fi, the user confirmed that `padprobe --watch` showed the DS4 pad's stick and buttons following the phone.

- **Phone:** Xiaomi 25098RA98G, Android 16 (SDK 36), on Wi-Fi at 192.168.1.9/24. Debug build of `spike/phone_spike` from `flutter run`, Flutter 3.47.7.
- **PC:** Wi-Fi at 192.168.1.10/24, on the same network, with the Windows profile Public. `python spike/server.py` listened on UDP 47821. Inbound UDP was allowed by the existing "Python" Allow rules for `C:\Python314\python.exe` on Public. That is the interpreter the `.venv` launcher starts, and the process that owns the socket.
- **App:** the app built, installed and ran with no Dart errors. Android needed NDK 28.2.13676358, the version Flutter 3.47.7 pins. Gradle's automatic install failed because the new Android SDK CLI rejects the old `ndk;<version>` package name. `android sdk install ndk/28.2.13676358` installed it. `tools/doctor.ps1` doesn't check for the NDK yet.

**First try failed: USB tethering took the PC off Wi-Fi.** The phone was plugged in with USB tethering on. The PC's Wi-Fi was then `disconnected`, and its only live network was the phone's Remote NDIS adapter (PC 10.130.54.13, phone 10.130.54.129). The app sent to 192.168.1.10 out of the phone's Wi-Fi, and nothing arrived. Five valid packets sent with `nc -u` from `adb shell` reached the server 0 times over Wi-Fi, but 5 of 5 over the USB link. That ruled out the firewall, the server and the app. With tethering off and the PC back on Wi-Fi, the same setup worked. For 4.5 (startup checks and help screens): check that the PC and the phone are on the same subnet, and warn when USB tethering is on.

### Raw output

```text
[  0.00] helper: start server.py --seconds 25
[  0.24] server: {"event": "ready", "port": 47821, "pad": "ds4"}
[  0.24] helper: start padprobe --watch --seconds 12
[  0.62] padprobe: {"t": 0.0, "event": "added", "pad": {"index": 0, "instance_id": 0, "name": "PS4 Controller", "guid": "03008fe54c050000c405000000016800", "type": "ds4", "vendor_id": "054c", "product_id": "05c4", "buttons": 16, "axes": 6, "hats": 0}}
[  1.62] helper: run send_fake.py
[  1.77] send_fake: {"event": "step", "name": "neutral", "t": 0.0, "lx": 0.0, "ly": 0.0, "a": false, "b": false}
[  1.77] server: {"event": "sender", "address": "127.0.0.1:55568"}
[  1.78] padprobe: {"t": 1.166, "event": "axis", "instance_id": 0, "axis": 0, "value": 0.02}
[  1.78] padprobe: {"t": 1.166, "event": "axis", "instance_id": 0, "axis": 0, "value": 0.0}
[  1.78] padprobe: {"t": 1.166, "event": "axis", "instance_id": 0, "axis": 2, "value": -0.01}
[  1.78] padprobe: {"t": 1.166, "event": "axis", "instance_id": 0, "axis": 2, "value": 0.0}
[  2.26] send_fake: {"event": "step", "name": "right", "t": 0.5, "lx": 1.0, "ly": 0.0, "a": false, "b": false}
[  2.28] padprobe: {"t": 1.665, "event": "axis", "instance_id": 0, "axis": 0, "value": 1.0}
[  2.76] send_fake: {"event": "step", "name": "left", "t": 1.0, "lx": -1.0, "ly": 0.0, "a": false, "b": false}
[  2.78] padprobe: {"t": 2.162, "event": "axis", "instance_id": 0, "axis": 0, "value": -1.0}
[  3.26] send_fake: {"event": "step", "name": "up", "t": 1.5, "lx": 0.0, "ly": 1.0, "a": false, "b": false}
[  3.28] padprobe: {"t": 2.66, "event": "axis", "instance_id": 0, "axis": 0, "value": 0.0}
[  3.28] padprobe: {"t": 2.66, "event": "axis", "instance_id": 0, "axis": 1, "value": 0.0}
[  3.28] padprobe: {"t": 2.66, "event": "axis", "instance_id": 0, "axis": 1, "value": -1.0}
[  3.76] send_fake: {"event": "step", "name": "down", "t": 2.0, "lx": 0.0, "ly": -1.0, "a": false, "b": false}
[  3.78] padprobe: {"t": 3.159, "event": "axis", "instance_id": 0, "axis": 1, "value": 1.0}
[  4.26] send_fake: {"event": "step", "name": "neutral", "t": 2.5, "lx": 0.0, "ly": 0.0, "a": false, "b": false}
[  4.28] padprobe: {"t": 3.666, "event": "axis", "instance_id": 0, "axis": 1, "value": 0.0}
[  4.76] send_fake: {"event": "step", "name": "a", "t": 3.0, "lx": 0.0, "ly": 0.0, "a": true, "b": false}
[  4.78] padprobe: {"t": 4.165, "event": "button", "instance_id": 0, "button": 0, "pressed": true}
[  5.26] send_fake: {"event": "step", "name": "neutral", "t": 3.5, "lx": 0.0, "ly": 0.0, "a": false, "b": false}
[  5.28] padprobe: {"t": 4.665, "event": "button", "instance_id": 0, "button": 0, "pressed": false}
[  5.76] send_fake: {"event": "step", "name": "b", "t": 4.0, "lx": 0.0, "ly": 0.0, "a": false, "b": true}
[  5.78] padprobe: {"t": 5.166, "event": "button", "instance_id": 0, "button": 1, "pressed": true}
[  6.26] send_fake: {"event": "step", "name": "neutral", "t": 4.5, "lx": 0.0, "ly": 0.0, "a": false, "b": false}
[  6.28] padprobe: {"t": 5.665, "event": "button", "instance_id": 0, "button": 1, "pressed": false}
[  6.76] send_fake: {"event": "done", "sent": 300}
[  6.77] helper: send_fake exit 0
[  7.32] server: {"event": "neutral", "silent_seconds": 0.5}
[ 12.65] helper: padprobe exit 0
[ 25.26] server: {"event": "stats", "received": 300, "applied": 300, "stale": 0, "bad": 0}
[ 25.27] server: {"event": "removed", "count": 1}
[ 25.30] helper: server exit 0
[ 25.61] helper: padprobe --expect after exit: ok: x360=0,ds4=0,other=0 (exit 0)
```
