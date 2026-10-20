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

// Every packet vector's mac uses this key, the session key in crypto.json.
final _sessionKey = hex(vectorFile('input.json')['sessionKey']);

void main() {
  test('feedback.json uses the same session key', () {
    expect(vectorFile('feedback.json')['sessionKey'], toHex(_sessionKey));
  });

  group('input.json', () {
    for (final vector in vectorGroup('input.json', 'valid')) {
      test('"${vector['name']}" reads to its fields and writes back', () {
        final bytes = hex(vector['hex']);

        final packet = InputPacket.read(bytes, _sessionKey);

        expect(_inputFields(packet), _withoutMac(vector['fields']));
        final written = Uint8List(InputPacket.size);
        packet.write(written, _sessionKey);
        expect(toHex(written), vector['hex']);
      });

      test('"${vector['name']}" has the pressed buttons set', () {
        final pressed = (vector['pressed'] as List).fold<int>(
          0,
          (bits, name) => bits | _buttons[name]!,
        );

        expect(
          InputPacket.read(hex(vector['hex']), _sessionKey).buttons,
          pressed,
        );
      });

      test(
        '"${vector['name']}" with any mac bit flipped is dropped for mac',
        () {
          _expectEachFlipDropped(
            hex(vector['hex']),
            InputPacket.macOffset,
            InputPacket.size,
            (bytes) => InputPacket.read(bytes, _sessionKey),
          );
        },
      );

      // The header bytes have their own checks, so a flip there may be dropped
      // for another reason.
      test(
        '"${vector['name']}" with any field bit flipped is dropped for mac',
        () {
          _expectEachFlipDropped(
            hex(vector['hex']),
            4,
            InputPacket.macOffset,
            (bytes) => InputPacket.read(bytes, _sessionKey),
          );
        },
      );
    }

    for (final vector in vectorGroup('input.json', 'invalid')) {
      test('"${vector['name']}" is dropped for ${vector['reason']}', () {
        _expectDropped(
          () => InputPacket.read(hex(vector['hex']), _sessionKey),
          vector['reason'],
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

        final packet = FeedbackPacket.read(bytes, _sessionKey);

        expect(_feedbackFields(packet), _withoutMac(vector['fields']));
        final written = Uint8List(FeedbackPacket.size);
        packet.write(written, _sessionKey);
        expect(toHex(written), vector['hex']);
      });

      test(
        '"${vector['name']}" with any mac bit flipped is dropped for mac',
        () {
          _expectEachFlipDropped(
            hex(vector['hex']),
            FeedbackPacket.macOffset,
            FeedbackPacket.size,
            (bytes) => FeedbackPacket.read(bytes, _sessionKey),
          );
        },
      );

      test(
        '"${vector['name']}" with any field bit flipped is dropped for mac',
        () {
          _expectEachFlipDropped(
            hex(vector['hex']),
            4,
            FeedbackPacket.macOffset,
            (bytes) => FeedbackPacket.read(bytes, _sessionKey),
          );
        },
      );
    }

    for (final vector in vectorGroup('feedback.json', 'invalid')) {
      test('"${vector['name']}" is dropped for ${vector['reason']}', () {
        _expectDropped(
          () => FeedbackPacket.read(hex(vector['hex']), _sessionKey),
          vector['reason'],
        );
      });
    }
  });

  test('reading and writing need a 32-byte session key', () {
    final bytes = hex(vectorGroup('input.json', 'valid').first['hex']);
    final packet = InputPacket.read(bytes, _sessionKey);

    for (final key in [_sessionKey.sublist(1), Uint8List(0), Uint8List(33)]) {
      expect(() => InputPacket.read(bytes, key), throwsArgumentError);
      expect(
        () => packet.write(Uint8List(InputPacket.size), key),
        throwsArgumentError,
      );
    }
    final feedback = hex(vectorGroup('feedback.json', 'valid').first['hex']);
    expect(
      () => FeedbackPacket.read(feedback, Uint8List(0)),
      throwsArgumentError,
    );
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

  test('a packet built from its fields writes its vector, mac and all', () {
    final vector = vectorGroup(
      'feedback.json',
      'valid',
    ).firstWhere((vector) => vector['name'] == 'typical');
    final fields = vector['fields'] as Map;
    final written = Uint8List(FeedbackPacket.size);

    FeedbackPacket(
      slot: fields['slot'] as int,
      largeMotor: fields['largeMotor'] as int,
      smallMotor: fields['smallMotor'] as int,
      red: fields['red'] as int,
      green: fields['green'] as int,
      blue: fields['blue'] as int,
      echoMs: fields['echoMs'] as int,
    ).write(written, _sessionKey);

    expect(toHex(written), vector['hex']);
  });

  test('write needs room for the whole packet', () {
    final packet = InputPacket.read(
      hex(vectorGroup('input.json', 'valid').first['hex']),
      _sessionKey,
    );

    expect(() => packet.write(Uint8List(29), _sessionKey), throwsArgumentError);
  });
}

void _expectDropped(Object? Function() read, Object? reason, {String? bit}) {
  expect(
    read,
    throwsA(
      isA<PacketException>().having((e) => e.error.name, 'error', reason),
    ),
    reason: bit,
  );
}

// Flips each bit of bytes start to end - 1 in turn.
void _expectEachFlipDropped(
  Uint8List bytes,
  int start,
  int end,
  Object? Function(Uint8List bytes) read,
) {
  for (var bit = start * 8; bit < end * 8; bit++) {
    _expectDropped(() => read(flipped(bytes, bit)), 'mac', bit: 'bit $bit');
  }
}

// The packets keep no mac: the written bytes check it.
Map<Object?, Object?> _withoutMac(Object? fields) =>
    Map.of(fields as Map)..remove('mac');

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
};
