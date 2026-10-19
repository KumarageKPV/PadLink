# phone_spike

Throwaway M0 spike (PLAN.md 1.3): one stick and two buttons, sent about 60 times a second as UDP
JSON to the PC IP typed in (62.5 on a device, as Dart rounds the timer to 16 ms). `spike/server.py`
turns the packets into one DualShock 4 pad. No pairing or security. Android only.

On the PC: `python spike/server.py`, then `python tools/padprobe.py --watch` to see the pad move.
On the phone, with USB debugging on: `flutter run` here, type the PC's IPv4 address, press Start.

Tests: `flutter analyze` and `flutter test` here.
