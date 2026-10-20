import 'dart:math';

import 'packets.dart';

// Phone input to DualShock 4 report values (protocol/SPEC.md, "Mapping to pad
// reports"). Xbox 360 pads take the sticks and the d-pad bits as sent.

/// An int16 stick X value (right is positive) as a DualShock 4 byte: 0 left,
/// 128 centre, 255 right.
int ds4StickX(int value) => (value + 32768) >> 8;

/// An int16 stick Y value (up is positive) as a DualShock 4 byte, which
/// reports up as 0.
int ds4StickY(int value) => (min(-value, 32767) + 32768) >> 8;

/// The hat value with no d-pad direction held.
const ds4HatNeutral = 8;

/// The d-pad bits as a DualShock 4 hat value: 0 north, clockwise to 7
/// north-west, or [ds4HatNeutral]. Opposite directions cancel first.
int ds4Hat(int buttons) {
  int held(int bit) => (buttons & bit) != 0 ? 1 : 0;
  final vertical = held(PadButtons.dpadUp) - held(PadButtons.dpadDown);
  final horizontal = held(PadButtons.dpadRight) - held(PadButtons.dpadLeft);
  return switch ((vertical, horizontal)) {
    (1, 0) => 0,
    (1, 1) => 1,
    (0, 1) => 2,
    (-1, 1) => 3,
    (-1, 0) => 4,
    (-1, -1) => 5,
    (0, -1) => 6,
    (1, -1) => 7,
    _ => ds4HatNeutral,
  };
}
