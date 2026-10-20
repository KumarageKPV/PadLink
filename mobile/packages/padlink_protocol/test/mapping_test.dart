import 'package:padlink_protocol/padlink_protocol.dart';
import 'package:test/test.dart';

import 'vectors.dart';

void main() {
  final mapping = vectorFile('mapping.json');

  for (final stick in (mapping['sticks'] as List).cast<Map>()) {
    test('stick ${stick['value']} is DualShock 4 '
        'X ${stick['ds4X']}, Y ${stick['ds4Y']}', () {
      expect(ds4StickX(stick['value'] as int), stick['ds4X']);
      expect(ds4StickY(stick['value'] as int), stick['ds4Y']);
    });
  }

  for (final dpad in (mapping['dpad'] as List).cast<Map>()) {
    test('d-pad ${dpad['pressed']} is hat ${dpad['ds4Hat']}', () {
      expect(ds4Hat(dpad['buttons'] as int), dpad['ds4Hat']);
    });
  }

  test('the hat reads only the d-pad bits', () {
    final others =
        0xFFFF &
        ~(PadButtons.dpadUp | PadButtons.dpadDown) &
        ~(PadButtons.dpadLeft | PadButtons.dpadRight);

    expect(ds4Hat(others), ds4HatNeutral);
    expect(ds4Hat(others | PadButtons.dpadUp | PadButtons.dpadLeft), 7);
  });
}
