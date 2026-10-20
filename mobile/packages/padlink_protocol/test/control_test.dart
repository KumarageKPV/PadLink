import 'dart:convert';
import 'dart:typed_data';

import 'package:padlink_protocol/padlink_protocol.dart';
import 'package:test/test.dart';

import 'vectors.dart';

const _file = 'control.json';

void main() {
  group('control.json', () {
    for (final vector in vectorGroup(_file, 'valid')) {
      test('"${vector['name']}" decodes to its message and back', () {
        final message = _decode(hex(vector['frameHex']));

        expect(message.toJson(), vector['message']);
        expect(
          _decode(ControlFrame.encode(message)).toJson(),
          vector['message'],
        );
      });

      // Senders write compact JSON, type first, fields in table order, escaping
      // only what JSON requires. So where the vector's JSON is in that form, the
      // bytes match too. Some vectors show what other senders may write.
      test('"${vector['name']}" encodes to its bytes in sender form', () {
        final json = vector['json'] as String;
        if (json != jsonEncode(vector['message'])) return;

        final frame = ControlFrame.encode(_decode(hex(vector['frameHex'])));

        expect(utf8.decode(frame.sublist(ControlFrame.headerSize)), json);
        expect(toHex(frame), vector['frameHex']);
      });
    }

    for (final vector in vectorGroup(_file, 'ignored')) {
      test('"${vector['name']}" is ignored', () {
        final (:status, :body) = ControlFrame.read(hex(vector['frameHex']));

        expect(status, FrameStatus.complete);
        expect(ControlFrame.decode(body!), isNull);
      });
    }

    for (final vector in vectorGroup(_file, 'invalid')) {
      test('"${vector['name']}" is rejected for ${vector['reason']}', () {
        final (:status, :body) = ControlFrame.read(hex(vector['frameHex']));

        switch (vector['reason']) {
          case 'incomplete':
            expect(status, FrameStatus.incomplete);
          case 'frame_empty':
            expect(status, FrameStatus.empty);
          case 'frame_too_long':
            expect(status, FrameStatus.tooLong);
          case final String reason:
            // These all mean bad_message, so SPEC.md lets a decoder report any
            // of them; this one tells them apart.
            expect(status, FrameStatus.complete);
            expect(
              () => ControlFrame.decode(body!),
              _badMessage(_faults[reason]!),
            );
        }
        if (status != FrameStatus.complete) expect(body, isNull);
      });
    }
  });

  test('the length alone decides before the body arrives', () {
    expect(_status([0x0f, 0x00, 0x00]), FrameStatus.incomplete);
    expect(_status(_header(15)), FrameStatus.incomplete);
    expect(_status(_header(0)), FrameStatus.empty);
    expect(_status(_header(4097)), FrameStatus.tooLong);
    expect(_status(_header(4096)), FrameStatus.incomplete);
  });

  test('read takes one frame and leaves what follows', () {
    final two = Uint8List.fromList([
      ...ControlFrame.encode(const Ping()),
      ...ControlFrame.encode(const Bye()),
    ]);

    final (:status, :body) = ControlFrame.read(two);

    expect(status, FrameStatus.complete);
    expect(utf8.decode(body!), '{"type":"ping"}');
  });

  test('decoded messages carry typed values', () {
    final crypto = vectorFile('crypto.json');
    Object? vectorMessage(String name) => _decode(
      hex(
        vectorGroup(
          _file,
          'valid',
        ).singleWhere((v) => v['name'] == name)['frameHex'],
      ),
    );

    final hello = vectorMessage('hello') as Hello;
    expect(hello.proto, 1);
    expect(hello.deviceId, '5b0e8f0c-6a8e-4d0a-9b8e-2f4c1d7e9a31');
    expect(hello.deviceName, 'Pixel 8');
    expect(hello.platform, PhonePlatform.android);
    expect(hello.appVersion, '1.0.0');
    expect(hello.nonce, hex(crypto['phoneNonce']));

    final pair = vectorMessage('pair') as Pair;
    expect(pair.key, hex((crypto['phone'] as Map)['publicKey']));
    expect(pair.proof, hex(crypto['proof']));

    final welcome =
        vectorMessage('welcome, All DualShock 4, last slot') as Welcome;
    expect(welcome.slot, 15);
    expect(welcome.colour, '#FF8800');
    expect(welcome.mode, PadMode.allDs4);
    expect(welcome.padType, PadType.ds4);
    expect(welcome.udpPort, 65535);

    final error = vectorMessage('an escaped surrogate pair') as ErrorMessage;
    expect(error.code, '\u{1f3ae}');
  });

  test(
    'encode writes a little-endian length, then compact JSON, type first',
    () {
      expect(
        toHex(ControlFrame.encode(const Ping())),
        '0f000000${toHex(utf8.encode('{"type":"ping"}'))}',
      );
      expect(
        _bodyText(
          const SlotChanged(slot: 4, colour: '#00C853', padType: PadType.ds4),
        ),
        '{"type":"slot_changed","slot":4,"colour":"#00C853","padType":"ds4"}',
      );
      expect(
        _bodyText(const ErrorMessage(code: "café +/<>&' 🎮")),
        '{"type":"error","code":"café +/<>&\' 🎮"}',
      );
    },
  );

  test('encode refuses a frame over 4096 bytes', () {
    final shell = '{"type":"error","code":""}'.length;

    expect(
      ControlFrame.encode(ErrorMessage(code: 'x' * (4096 - shell))),
      hasLength(ControlFrame.headerSize + 4096),
    );
    expect(
      () => ControlFrame.encode(ErrorMessage(code: 'x' * (4097 - shell))),
      throwsArgumentError,
    );
  });

  test('encode refuses a message a receiver would reject', () {
    Hello hello({
      int proto = 1,
      String deviceId = 'id',
      String deviceName = 'name',
      String appVersion = '1.0',
      int nonceSize = 16,
    }) => Hello(
      proto: proto,
      deviceId: deviceId,
      deviceName: deviceName,
      platform: PhonePlatform.ios,
      appVersion: appVersion,
      nonce: Uint8List(nonceSize),
    );
    Welcome welcome({
      int slot = 0,
      String colour = '#1E90FF',
      int udpPort = 47821,
    }) => Welcome(
      slot: slot,
      colour: colour,
      mode: PadMode.mixed,
      padType: PadType.x360,
      udpPort: udpPort,
    );

    expect(ControlFrame.encode(hello()), isNotEmpty);
    expect(ControlFrame.encode(welcome()), isNotEmpty);
    for (final message in <ControlMessage>[
      hello(proto: 0),
      hello(proto: 0x80000000),
      hello(deviceId: ''),
      hello(deviceName: 'Ana\ud800'), // a lone surrogate has no UTF-8 form
      hello(deviceName: '\udc00\ud800'),
      hello(appVersion: ''),
      hello(nonceSize: 15),
      PairRequired(key: Uint8List(31), nonce: Uint8List(16)),
      Pair(key: Uint8List(32), proof: Uint8List(33)),
      Challenge(nonce: Uint8List(17)),
      Auth(mac: Uint8List(0)),
      welcome(slot: 16),
      welcome(slot: -1),
      welcome(colour: 'blue'),
      welcome(colour: '#1E90F'),
      welcome(colour: '#1E90FG'),
      welcome(colour: '#1E90FF\n'),
      welcome(udpPort: 0),
      welcome(udpPort: 65536),
      const SlotChanged(slot: 16, colour: '#00C853', padType: PadType.ds4),
      const Battery(pct: 101),
      const Battery(pct: -1),
      const ErrorMessage(code: ''),
    ]) {
      expect(
        () => ControlFrame.encode(message),
        throwsArgumentError,
        reason: '$message',
      );
    }
  });

  test('every message type encodes and decodes', () {
    final key = Uint8List.fromList(List.generate(32, (i) => i));
    final nonce = key.sublist(0, 16);
    final messages = <ControlMessage>[
      Hello(
        proto: 0x7FFFFFFF,
        deviceId: 'id',
        deviceName: 'ü 🎮',
        platform: PhonePlatform.ios,
        appVersion: '2.0',
        nonce: nonce,
      ),
      PairRequired(key: key, nonce: nonce),
      Pair(key: key, proof: key),
      Challenge(nonce: nonce),
      Auth(mac: key),
      const Welcome(
        slot: 7,
        colour: '#abcdef',
        mode: PadMode.mixed,
        padType: PadType.x360,
        udpPort: 1,
      ),
      const ModeChanged(mode: PadMode.allDs4),
      const SlotChanged(slot: 0, colour: '#000000', padType: PadType.x360),
      const Ping(),
      const Pong(),
      const Battery(pct: 0),
      const Bye(),
      const ErrorMessage(code: ErrorCodes.full),
    ];

    for (final message in messages) {
      expect(_decode(ControlFrame.encode(message)).toJson(), message.toJson());
    }
    expect(messages.map((m) => m.runtimeType).toSet(), hasLength(13));
  });

  // A logged proof, with the PC key from pair_required, lets anyone test every
  // 6-digit code offline, so no byte field's contents may show.
  test('toString shows byte fields by size only', () {
    final proof = Uint8List.fromList(List.filled(32, 7));

    expect(
      Pair(key: Uint8List(32), proof: proof).toString(),
      'pair(key: 32 bytes, proof: 32 bytes)',
    );
    expect(
      Hello(
        proto: 1,
        deviceId: 'id',
        deviceName: 'Pixel 8',
        platform: PhonePlatform.android,
        appVersion: '1.0',
        nonce: Uint8List(16),
      ).toString(),
      'hello(proto: 1, deviceId: "id", deviceName: "Pixel 8", '
      'platform: "android", appVersion: "1.0", nonce: 16 bytes)',
    );
    expect(Auth(mac: proof).toString(), isNot(contains(base64.encode(proof))));
    expect(const Ping().toString(), 'ping()');
  });

  test('JSON nested more than 32 levels deep is a bad message', () {
    // The object and 31 arrays: 32 levels.
    String nested(int arrays) =>
        '{"type":"ping","x":${'[' * arrays}${']' * arrays}}';

    expect(ControlFrame.decode(_utf8(nested(31))), isA<Ping>());
    expect(
      () => ControlFrame.decode(_utf8(nested(32))),
      _badMessage(ControlFault.json),
    );
  });

  group('frames the vectors do not cover are bad messages:', () {
    for (final (json, fault) in [
      (
        '{"type":"mode_changed","mode":"Mixed"}',
        ControlFault.field,
      ), // names are case-sensitive
      ('{"type":"mode_changed","mode":0}', ControlFault.field),
      (
        '{"type":"battery","pct":"57"}',
        ControlFault.field,
      ), // no numbers in strings
      ('{"type":"battery","pct":null}', ControlFault.field),
      ('{"type":"battery","PCT":57}', ControlFault.field), // so pct is missing
      ('{"type":"battery"}', ControlFault.field), // a missing number isn't 0
      ('{"type":"battery","pct":1e2}', ControlFault.field),
      ('{"type":"battery","pct":99999999999999999999}', ControlFault.field),
      (
        '{"type":"challenge","nonce":"oKGio6SlpqeoqaqrrK2urw==\\n"}',
        ControlFault.field,
      ),
      (
        '{"type":"challenge","nonce":"oKGio6Sl-qeoqaqrrK2urw=="}',
        ControlFault.field,
      ), // base64url
      (
        '{"type":"challenge","nonce":"oKGio6SlpqeoqaqrrK2urx=="}',
        ControlFault.field,
      ), // unused bits
      (
        '{"type":"challenge","nonce":"=oKGio6SlpqeoqaqrrK2urw="}',
        ControlFault.field,
      ),
      ('{"type":"ping"} {}', ControlFault.json), // two values
      ('{"type":"ping",}', ControlFault.json), // a trailing comma
      ('{"type":"ping"/* a comment */}', ControlFault.json),
      ('   ', ControlFault.json),
      ('{"type":"ping","x":"\\ud800"}', ControlFault.utf8),
      ('{"type":"ping","x":"\\udc00"}', ControlFault.utf8),
      ('{"type":"ping","x":"\\ud800\\u0041"}', ControlFault.utf8),
      ('{"type":"ping","x":"\\ud800\\ud800"}', ControlFault.utf8),
      ('{"type":"ping","x":["\\uDBFF"]}', ControlFault.utf8),
      (
        '{"type":"ping","type":5}',
        ControlFault.nameRepeated,
      ), // no object repeats a name
      ('{"type":"bye","type":"ping"}', ControlFault.nameRepeated),
      ('{"type":"ping","x":[{"a":1,"a":2}]}', ControlFault.nameRepeated),
      (
        '{"type":"ping","x":{"\\u0061":1,"a":2}}',
        ControlFault.nameRepeated,
      ), // compared unescaped
      ('{"type":"error","code":null}', ControlFault.field),
      ('{"type":{"a":1},"x":{"a":1}}', ControlFault.type),
    ]) {
      test(json, () {
        expect(() => ControlFrame.decode(_utf8(json)), _badMessage(fault));
      });
    }
  });

  group('frames other senders may write decode:', () {
    for (final (json, type) in [
      ('{"type":"pong","x":"\\ud83c\\udfae","\\u00e9":[1,{"a":null}]}', Pong),
      ('{"\\u0074ype":"\\u0070ing"}', Ping), // escapes in names and the type
      (
        '{"x":{"type":"bye"},"type":"ping"}',
        Ping,
      ), // only the top-level type counts
      (
        '{"x":{"a":1},"type":"ping","y":[{"a":1},{"a":1}]}',
        Ping,
      ), // each object has its own names
      ('{"x":{},"y":[],"z":[{}],"type":"ping"}', Ping),
      (
        '{"x":"\\"{[,","type":"ping"}',
        Ping,
      ), // structure inside a string is text
      (' \t\r\n{"type":"ping"} \t\r\n', Ping),
    ]) {
      test(json, () {
        expect(ControlFrame.decode(_utf8(json)).runtimeType, type);
      });
    }
  });
}

const _faults = {
  'utf8': ControlFault.utf8,
  'json': ControlFault.json,
  'name_repeated': ControlFault.nameRepeated,
  'not_object': ControlFault.notObject,
  'type': ControlFault.type,
  'field_missing': ControlFault.field,
  'field_invalid': ControlFault.field,
};

Matcher _badMessage(ControlFault fault) =>
    throwsA(isA<BadMessageException>().having((e) => e.fault, 'fault', fault));

ControlMessage _decode(Uint8List frame) {
  final (:status, :body) = ControlFrame.read(frame);
  expect(status, FrameStatus.complete);
  expect(body, hasLength(frame.length - ControlFrame.headerSize));
  return ControlFrame.decode(body!)!;
}

FrameStatus _status(List<int> buffer) =>
    ControlFrame.read(Uint8List.fromList(buffer)).status;

String _bodyText(ControlMessage message) =>
    utf8.decode(ControlFrame.encode(message).sublist(ControlFrame.headerSize));

Uint8List _header(int length) =>
    Uint8List(ControlFrame.headerSize)
      ..buffer.asByteData().setUint32(0, length, Endian.little);

Uint8List _utf8(String text) => Uint8List.fromList(utf8.encode(text));
