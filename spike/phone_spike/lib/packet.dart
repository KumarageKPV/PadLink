import 'dart:convert';
import 'dart:ui';

/// spike/server.py's UDP port: docs/design.md's input port.
const inputPort = 47821;

/// 60 packets a second, as PLAN.md 1.3 asks. The Dart VM rounds timers down to whole
/// milliseconds, so on a device this ticks every 16 ms: about 62.5 a second (measured in review).
const sendInterval = Duration(microseconds: 16667);

/// The whole input state, sent in full in every packet.
class PadState {
  const PadState({this.lx = 0, this.ly = 0, this.a = false, this.b = false});

  /// Left stick, -1 to 1; ly is +1 for up.
  final double lx;
  final double ly;
  final bool a;
  final bool b;

  PadState copyWith({double? lx, double? ly, bool? a, bool? b}) => PadState(
    lx: lx ?? this.lx,
    ly: ly ?? this.ly,
    a: a ?? this.a,
    b: b ?? this.b,
  );
}

/// One datagram for spike/server.py: `{"seq": 42, "lx": 0.25, "ly": -1.0, "a": true, "b": false}`.
List<int> encodePacket(int seq, PadState state) => utf8.encode(
  jsonEncode({
    'seq': seq,
    'lx': state.lx,
    'ly': state.ly,
    'a': state.a,
    'b': state.b,
  }),
);

/// Turns a touch's offset from the stick's centre into stick values.
///
/// The result is inside the unit circle, and y is flipped: screen y grows downwards, while a
/// stick's +y is up.
Offset stickValue(Offset fromCentre, double radius) {
  final value = Offset(fromCentre.dx / radius, -fromCentre.dy / radius);
  final distance = value.distance;
  return distance > 1 ? value / distance : value;
}
