import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:phone_spike/packet.dart';

void main() {
  group('encodePacket', () {
    test('writes the JSON object spike/server.py reads', () {
      final bytes = encodePacket(42, const PadState(lx: 0.25, ly: -1, a: true));
      expect(jsonDecode(utf8.decode(bytes)), {
        'seq': 42,
        'lx': 0.25,
        'ly': -1.0,
        'a': true,
        'b': false,
      });
    });

    test('a neutral state is centred sticks and no buttons', () {
      expect(jsonDecode(utf8.decode(encodePacket(0, const PadState()))), {
        'seq': 0,
        'lx': 0.0,
        'ly': 0.0,
        'a': false,
        'b': false,
      });
    });
  });

  group('stickValue', () {
    const radius = 100.0;

    test('the centre is (0, 0)', () {
      expect(stickValue(Offset.zero, radius), Offset.zero);
    });

    test('right is +x and screen-up is +y', () {
      expect(stickValue(const Offset(100, 0), radius), const Offset(1, 0));
      expect(stickValue(const Offset(0, -50), radius), const Offset(0, 0.5));
      expect(stickValue(const Offset(0, 100), radius), const Offset(0, -1));
    });

    test(
      'a touch past the edge is clamped to the unit circle at the same angle',
      () {
        final value = stickValue(const Offset(300, -300), radius);
        expect(value.distance, closeTo(1, 1e-9));
        expect(value.dx, closeTo(0.70710678, 1e-6));
        expect(value.dy, closeTo(0.70710678, 1e-6));
      },
    );
  });
}
