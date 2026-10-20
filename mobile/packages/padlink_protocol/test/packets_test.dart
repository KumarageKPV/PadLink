import 'dart:typed_data';

import 'package:padlink_protocol/padlink_protocol.dart';
import 'package:test/test.dart';

import 'vectors.dart';

// The vectors' names for the button bits.
const _buttons = {
  'faceBottom': PadButtons.faceBottom,
  'faceRight': PadButtons.faceRight,
  'faceLeft': PadButtons.faceLeft,
  'faceTop': PadButtons.faceTop,
  'shoulderLeft': PadButtons.shoulderLeft,
  'shoulderRight': PadButtons.shoulderRight,
  'back': PadButtons.back,
  'start': PadButtons.start,
  'guide': PadButtons.guide,
  'stickLeft': PadButtons.stickLeft,
  'stickRight': PadButtons.stickRight,
  'dpadUp': PadButtons.dpadUp,
  'dpadDown': PadButtons.dpadDown,
  'dpadLeft': PadButtons.dpadLeft,
  'dpadRight': PadButtons.dpadRight,
  'reserved': PadButtons.reserved,
};

void main() {
  group('input.json', () {
    for (final vector in vectorGroup('input.json', 'valid')) {
      test('"${vector['name']}" reads to its fields and writes back', () {
        final bytes = hex(vector['hex']);

        final packet = InputPacket.read(bytes);

        expect(_inputFields(packet), vector['fields']);
        final written = Uint8List(InputPacket.size);
        packet.write(written);
        expect(toHex(written), vector['hex']);
      });

      test('"${vector['name']}" has the pressed buttons set', () {
        final pressed = (vector['pressed'] as List).fold<int>(
          0,
          (bits, name) => bits | _buttons[name]!,
        );

        expect(InputPacket.read(hex(vector['hex'])).buttons, pressed);
      });
    }

    for (final vector in vectorGroup('input.json', 'invalid')) {
      test('"${vector['name']}" is dropped for ${vector['reason']}', () {
        _expectDropped(
          vector,
          InputPacket.size,
          (bytes) => InputPacket.read(bytes).write,
        );
      });
    }

    test('button bits match the vectors', () {
      final bits = vectorFile('input.json')['buttonBits'] as Map;

      expect(bits, hasLength(16));
      expect({
        for (final MapEntry(:key, :value) in bits.entries)
          key: 1 << (value as int),
      }, _buttons);
    });
  });

  group('feedback.json', () {
    for (final vector in vectorGroup('feedback.json', 'valid')) {
      test('"${vector['name']}" reads to its fields and writes back', () {
        final bytes = hex(vector['hex']);

        final packet = FeedbackPacket.read(bytes);

        expect(_feedbackFields(packet), vector['fields']);
        final written = Uint8List(FeedbackPacket.size);
        packet.write(written);
        expect(toHex(written), vector['hex']);
      });
    }

    for (final vector in vectorGroup('feedback.json', 'invalid')) {
      test('"${vector['name']}" is dropped for ${vector['reason']}', () {
        _expectDropped(
          vector,
          FeedbackPacket.size,
          (bytes) => FeedbackPacket.read(bytes).write,
        );
      });
    }
  });

  test('input packets refuse fields outside their wire range', () {
    InputPacket input({
      int slot = 0,
      int sequence = 0,
      int buttons = 0,
      int lx = 0,
      int ry = 0,
      int lt = 0,
      int sentMs = 0,
    }) => InputPacket(
      slot: slot,
      sequence: sequence,
      buttons: buttons,
      lx: lx,
      ly: 0,
      rx: 0,
      ry: ry,
      lt: lt,
      rt: 0,
      sentMs: sentMs,
    );

    expect(input(slot: 15, sequence: 0xFFFFFFFF, lx: -32768).slot, 15);
    for (final make in <InputPacket Function()>[
      () => input(slot: 16),
      () => input(slot: -1),
      () => input(sequence: -1),
      () => input(sequence: 0x100000000),
      () => input(buttons: 0x10000),
      () => input(lx: 32768),
      () => input(ry: -32769),
      () => input(lt: 256),
      () => input(sentMs: 0x10000),
    ]) {
      expect(make, throwsRangeError);
    }
    expect(
      () => InputPacket(
        slot: 0,
        sequence: 0,
        buttons: 0,
        lx: 0,
        ly: 0,
        rx: 0,
        ry: 0,
        lt: 0,
        rt: 0,
        sentMs: 0,
        mac: Uint8List(7),
      ),
      throwsArgumentError,
    );
  });

  test('feedback packets refuse fields outside their wire range', () {
    FeedbackPacket feedback({int slot = 0, int red = 0, int echoMs = 0}) =>
        FeedbackPacket(
          slot: slot,
          largeMotor: 0,
          smallMotor: 0,
          red: red,
          green: 0,
          blue: 0,
          echoMs: echoMs,
        );

    expect(feedback(slot: 15, red: 255, echoMs: 0xFFFF).echoMs, 0xFFFF);
    expect(() => feedback(slot: 16), throwsRangeError);
    expect(() => feedback(red: 256), throwsRangeError);
    expect(() => feedback(echoMs: -1), throwsRangeError);
  });

  test('a packet without a mac writes 8 zero bytes for the sender to fill', () {
    final written = Uint8List(FeedbackPacket.size)..fillRange(0, 19, 0xFF);

    FeedbackPacket(
      slot: 1,
      largeMotor: 2,
      smallMotor: 3,
      red: 4,
      green: 5,
      blue: 6,
      echoMs: 7,
    ).write(written);

    expect(toHex(written), '5001020102030405060700${'00' * 8}');
  });

  test('write needs room for the whole packet', () {
    final packet = InputPacket.read(
      hex(vectorGroup('input.json', 'valid').first['hex']),
    );

    expect(() => packet.write(Uint8List(29)), throwsArgumentError);
  });
}

// Checking the mac needs the session key, which PLAN.md 2.4 adds, so a packet
// with a bad mac passes every other check and writes back as it was.
void _expectDropped(
  Map<String, Object?> vector,
  int size,
  void Function(Uint8List destination) Function(Uint8List bytes) readThenWrite,
) {
  final bytes = hex(vector['hex']);
  if (vector['reason'] == 'mac') {
    final written = Uint8List(size);
    readThenWrite(bytes)(written);
    expect(written, bytes);
    return;
  }
  expect(
    () => readThenWrite(bytes),
    throwsA(
      isA<PacketException>().having(
        (e) => e.error.name,
        'error',
        vector['reason'],
      ),
    ),
  );
}

Map<String, Object> _inputFields(InputPacket packet) => {
  'magic': Packet.magic,
  'version': Packet.version,
  'type': Packet.inputType,
  'slot': packet.slot,
  'sequence': packet.sequence,
  'buttons': packet.buttons,
  'lx': packet.lx,
  'ly': packet.ly,
  'rx': packet.rx,
  'ry': packet.ry,
  'lt': packet.lt,
  'rt': packet.rt,
  'sentMs': packet.sentMs,
  'mac': toHex(packet.mac),
};

Map<String, Object> _feedbackFields(FeedbackPacket packet) => {
  'magic': Packet.magic,
  'version': Packet.version,
  'type': Packet.feedbackType,
  'slot': packet.slot,
  'largeMotor': packet.largeMotor,
  'smallMotor': packet.smallMotor,
  'red': packet.red,
  'green': packet.green,
  'blue': packet.blue,
  'echoMs': packet.echoMs,
  'mac': toHex(packet.mac),
};
