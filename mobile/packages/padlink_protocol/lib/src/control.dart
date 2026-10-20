import 'dart:convert';
import 'dart:typed_data';

import 'packets.dart';

/// Where [ControlFrame.read] got to. The names match the vectors' reasons.
enum FrameStatus {
  complete,

  /// Wait for more bytes.
  incomplete,

  /// Length 0: close the connection without reading on.
  empty,

  /// Length over 4096: close the connection without reading the body.
  tooLong,
}

/// Why a frame body is a bad message. Every fault gets `error` `bad_message`,
/// then the connection closes.
enum ControlFault {
  /// Not UTF-8, or a string escape that stands for a lone surrogate.
  utf8,

  /// Not JSON (a byte-order mark included), or nested more than 32 levels
  /// deep.
  json,

  /// An object, at any depth, repeats a name.
  nameRepeated,

  notObject,

  /// No string `type` field.
  type,

  /// A known message with a missing or invalid field.
  field,
}

/// A frame body that breaks protocol/SPEC.md, "Control messages".
final class BadMessageException implements Exception {
  const BadMessageException(this.fault);

  final ControlFault fault;

  @override
  String toString() => 'BadMessageException: ${fault.name}';
}

/// Control-message framing over TCP: a little-endian u32 length, then 1–4096
/// bytes of UTF-8 JSON (protocol/SPEC.md, "Control messages").
abstract final class ControlFrame {
  static const headerSize = 4;
  static const maxBodySize = 4096;
  static const maxDepth = 32;

  /// Finds the frame at the start of buffer; body is set only when it is
  /// [FrameStatus.complete]. The length alone decides [FrameStatus.empty] and
  /// [FrameStatus.tooLong], so a reader can stop after the 4-byte header.
  static ({FrameStatus status, Uint8List? body}) read(Uint8List buffer) {
    FrameStatus? status;
    var length = 0;
    if (buffer.length < headerSize) {
      status = FrameStatus.incomplete;
    } else {
      length = ByteData.sublistView(buffer).getUint32(0, Endian.little);
      if (length == 0) {
        status = FrameStatus.empty;
      } else if (length > maxBodySize) {
        status = FrameStatus.tooLong;
      } else if (buffer.length - headerSize < length) {
        status = FrameStatus.incomplete;
      }
    }
    if (status != null) return (status: status, body: null);
    final body = Uint8List.sublistView(buffer, headerSize, headerSize + length);
    return (status: FrameStatus.complete, body: body);
  }

  /// Decodes a frame body. Returns null for a type this version doesn't know,
  /// which receivers ignore. Throws [BadMessageException].
  static ControlMessage? decode(Uint8List body) {
    final value = _parse(body);
    if (value is! Map<String, Object?>) _fail(ControlFault.notObject);
    final type = value['type'];
    if (type is! String) _fail(ControlFault.type);
    return _readers[type]?.call(_Fields(value));
  }

  /// The frame for a message: compact JSON, `type` first, then the fields in
  /// table order. Throws [ArgumentError] for a message a receiver would
  /// reject, or a frame over 4096 bytes.
  static Uint8List encode(ControlMessage message) {
    // jsonEncode escapes only what JSON requires. It writes a lone surrogate as
    // an escape, which decode rejects.
    final body = utf8.encode(jsonEncode(message.toJson()));
    if (body.length > maxBodySize) {
      throw ArgumentError.value(
        message,
        'message',
        'The frame body is ${body.length} bytes, over $maxBodySize',
      );
    }
    try {
      decode(body);
    } on BadMessageException catch (error) {
      throw ArgumentError.value(
        message,
        'message',
        'A receiver would reject it (${error.fault.name})',
      );
    }
    final frame = Uint8List(headerSize + body.length);
    ByteData.sublistView(frame).setUint32(0, body.length, Endian.little);
    frame.setRange(headerSize, frame.length, body);
    return frame;
  }

  static Object? _parse(Uint8List body) {
    // utf8.decode drops a leading byte-order mark without telling.
    if (body.length >= 3 &&
        body[0] == 0xEF &&
        body[1] == 0xBB &&
        body[2] == 0xBF) {
      _fail(ControlFault.json);
    }
    final String text;
    final Object? value;
    try {
      text = utf8.decode(body);
    } on FormatException {
      _fail(ControlFault.utf8);
    }
    try {
      value = jsonDecode(text);
    } on FormatException {
      _fail(ControlFault.json);
    }
    _scan(text);
    return value;
  }

  // One pass over text that jsonDecode has accepted, so its structure is
  // sound, for what jsonDecode lets through: it keeps lone surrogate escapes
  // and only the last copy of a repeated name, and has no depth limit.
  static void _scan(String text) {
    // The names of each open container, innermost last; null for an array.
    final names = <Set<String>?>[];
    var nameIsNext = false;
    for (var i = 0; i < text.length; i++) {
      switch (text.codeUnitAt(i)) {
        case 0x7B: // {
          names.add({});
          nameIsNext = true;
        case 0x5B: // [
          names.add(null);
        case 0x7D || 0x5D: // } ]
          names.removeLast();
          nameIsNext = false;
        case 0x2C: // ,
          nameIsNext = names.last != null;
        case 0x22: // "
          final start = i;
          var escaped = false;
          for (i++; text.codeUnitAt(i) != 0x22; i++) {
            if (text.codeUnitAt(i) == 0x5C) {
              escaped = true;
              i++; // skip the escaped character, which may be "
            }
          }
          // Only an escape can stand for a lone surrogate: utf8.decode refuses
          // an encoded one.
          final string = escaped
              ? jsonDecode(text.substring(start, i + 1)) as String
              : text.substring(start + 1, i);
          if (escaped && _hasLoneSurrogate(string)) _fail(ControlFault.utf8);
          if (nameIsNext && !names.last!.add(string)) {
            _fail(ControlFault.nameRepeated);
          }
          nameIsNext = false;
      }
      if (names.length > maxDepth) _fail(ControlFault.json);
    }
  }

  static bool _hasLoneSurrogate(String text) {
    for (var i = 0; i < text.length; i++) {
      final unit = text.codeUnitAt(i);
      if (unit < 0xD800 || unit > 0xDFFF) continue;
      if (unit > 0xDBFF || i + 1 == text.length) return true;
      final next = text.codeUnitAt(++i);
      if (next < 0xDC00 || next > 0xDFFF) return true;
    }
    return false;
  }

  // Each known type's reader, with the field rules in protocol/SPEC.md's field
  // table. encode applies them too, by decoding what it wrote.
  static final Map<String, ControlMessage Function(_Fields)> _readers = {
    'hello': (f) => Hello(
      proto: f.integer('proto', 1, 0x7FFFFFFF),
      deviceId: f.text('deviceId'),
      deviceName: f.text('deviceName'),
      platform: f.oneOf('platform', PhonePlatform.values, (v) => v.wireName),
      appVersion: f.text('appVersion'),
      nonce: f.bytes('nonce', ControlMessage.nonceSize),
    ),
    'pair_required': (f) => PairRequired(
      key: f.bytes('key', ControlMessage.keySize),
      nonce: f.bytes('nonce', ControlMessage.nonceSize),
    ),
    'pair': (f) => Pair(
      key: f.bytes('key', ControlMessage.keySize),
      proof: f.bytes('proof', ControlMessage.hmacSize),
    ),
    'challenge': (f) =>
        Challenge(nonce: f.bytes('nonce', ControlMessage.nonceSize)),
    'auth': (f) => Auth(mac: f.bytes('mac', ControlMessage.hmacSize)),
    'welcome': (f) => Welcome(
      slot: f.slot('slot'),
      colour: f.colour('colour'),
      mode: f.oneOf('mode', PadMode.values, (v) => v.wireName),
      padType: f.oneOf('padType', PadType.values, (v) => v.wireName),
      udpPort: f.integer('udpPort', 1, 65535),
    ),
    'mode_changed': (f) =>
        ModeChanged(mode: f.oneOf('mode', PadMode.values, (v) => v.wireName)),
    'slot_changed': (f) => SlotChanged(
      slot: f.slot('slot'),
      colour: f.colour('colour'),
      padType: f.oneOf('padType', PadType.values, (v) => v.wireName),
    ),
    'ping': (_) => const Ping(),
    'pong': (_) => const Pong(),
    'battery': (f) => Battery(pct: f.integer('pct', 0, 100)),
    'bye': (_) => const Bye(),
    'error': (f) => ErrorMessage(code: f.text('code')),
  };
}

Never _fail(ControlFault fault) => throw BadMessageException(fault);

/// A known message's JSON object. Each read throws [BadMessageException] for a
/// field that is missing or breaks its rule.
extension type _Fields(Map<String, Object?> json) {
  static final _colour = RegExp(r'^#[0-9A-Fa-f]{6}$');

  /// A JSON number with no fraction or exponent: jsonDecode reads `57.0` and
  /// `5.7e1` as doubles.
  int integer(String name, int min, int max) => switch (json[name]) {
    final int value when value >= min && value <= max => value,
    _ => _fail(ControlFault.field),
  };

  int slot(String name) => integer(name, 0, Packet.slotCount - 1);

  String text(String name) => switch (json[name]) {
    final String value when value.isNotEmpty => value,
    _ => _fail(ControlFault.field),
  };

  String colour(String name) => switch (json[name]) {
    final String value when _colour.hasMatch(value) => value,
    _ => _fail(ControlFault.field),
  };

  T oneOf<T>(String name, List<T> values, String Function(T) wireName) {
    final value = json[name];
    for (final candidate in values) {
      if (wireName(candidate) == value) return candidate;
    }
    _fail(ControlFault.field);
  }

  /// Standard base64 with padding and zero unused bits. Dart's decoder also
  /// takes base64url and `%3D` for `=`, so the bytes must encode back to the
  /// same text.
  Uint8List bytes(String name, int size) {
    final value = json[name];
    if (value is String) {
      try {
        final bytes = base64.decode(value);
        if (bytes.length == size && base64.encode(bytes) == value) return bytes;
      } on FormatException {
        // not base64
      }
    }
    _fail(ControlFault.field);
  }
}

/// A control message: one flat JSON object whose `type` names it
/// (protocol/SPEC.md, "Control messages"). [ControlFrame] reads and writes
/// them, and checks their fields.
sealed class ControlMessage {
  const ControlMessage();

  static const nonceSize = 16;

  /// An X25519 public key.
  static const keySize = 32;

  /// A pairing proof or auth mac: a whole HMAC-SHA256.
  static const hmacSize = 32;

  // The fields SPEC.md writes as base64.
  static const _byteFields = {'nonce', 'key', 'proof', 'mac'};

  /// The JSON object: `type` first, then the fields in table order, which is
  /// the order senders write them. Bytes are base64.
  Map<String, Object> toJson();

  /// The type and fields, with byte fields shown only by size: a logged proof,
  /// with the PC key from `pair_required`, would let anyone test every code.
  @override
  String toString() {
    final json = toJson();
    final fields = [
      for (final MapEntry(:key, :value) in json.entries)
        if (_byteFields.contains(key))
          '$key: ${base64.decode(value as String).length} bytes'
        else if (key != 'type')
          '$key: ${jsonEncode(value)}',
    ];
    return '${json['type']}(${fields.join(', ')})';
  }
}

final class Hello extends ControlMessage {
  const Hello({
    required this.proto,
    required this.deviceId,
    required this.deviceName,
    required this.platform,
    required this.appVersion,
    required this.nonce,
  });

  final int proto;
  final String deviceId;
  final String deviceName;
  final PhonePlatform platform;
  final String appVersion;
  final Uint8List nonce;

  @override
  Map<String, Object> toJson() => {
    'type': 'hello',
    'proto': proto,
    'deviceId': deviceId,
    'deviceName': deviceName,
    'platform': platform.wireName,
    'appVersion': appVersion,
    'nonce': base64.encode(nonce),
  };
}

final class PairRequired extends ControlMessage {
  const PairRequired({required this.key, required this.nonce});

  final Uint8List key;
  final Uint8List nonce;

  @override
  Map<String, Object> toJson() => {
    'type': 'pair_required',
    'key': base64.encode(key),
    'nonce': base64.encode(nonce),
  };
}

final class Pair extends ControlMessage {
  const Pair({required this.key, required this.proof});

  final Uint8List key;
  final Uint8List proof;

  @override
  Map<String, Object> toJson() => {
    'type': 'pair',
    'key': base64.encode(key),
    'proof': base64.encode(proof),
  };
}

final class Challenge extends ControlMessage {
  const Challenge({required this.nonce});

  final Uint8List nonce;

  @override
  Map<String, Object> toJson() => {
    'type': 'challenge',
    'nonce': base64.encode(nonce),
  };
}

final class Auth extends ControlMessage {
  const Auth({required this.mac});

  final Uint8List mac;

  @override
  Map<String, Object> toJson() => {'type': 'auth', 'mac': base64.encode(mac)};
}

final class Welcome extends ControlMessage {
  const Welcome({
    required this.slot,
    required this.colour,
    required this.mode,
    required this.padType,
    required this.udpPort,
  });

  final int slot;

  /// `#RRGGBB`.
  final String colour;

  final PadMode mode;
  final PadType padType;
  final int udpPort;

  @override
  Map<String, Object> toJson() => {
    'type': 'welcome',
    'slot': slot,
    'colour': colour,
    'mode': mode.wireName,
    'padType': padType.wireName,
    'udpPort': udpPort,
  };
}

final class ModeChanged extends ControlMessage {
  const ModeChanged({required this.mode});

  final PadMode mode;

  @override
  Map<String, Object> toJson() => {
    'type': 'mode_changed',
    'mode': mode.wireName,
  };
}

final class SlotChanged extends ControlMessage {
  const SlotChanged({
    required this.slot,
    required this.colour,
    required this.padType,
  });

  final int slot;

  /// `#RRGGBB`.
  final String colour;

  final PadType padType;

  @override
  Map<String, Object> toJson() => {
    'type': 'slot_changed',
    'slot': slot,
    'colour': colour,
    'padType': padType.wireName,
  };
}

final class Ping extends ControlMessage {
  const Ping();

  @override
  Map<String, Object> toJson() => {'type': 'ping'};
}

final class Pong extends ControlMessage {
  const Pong();

  @override
  Map<String, Object> toJson() => {'type': 'pong'};
}

final class Battery extends ControlMessage {
  const Battery({required this.pct});

  final int pct;

  @override
  Map<String, Object> toJson() => {'type': 'battery', 'pct': pct};
}

final class Bye extends ControlMessage {
  const Bye();

  @override
  Map<String, Object> toJson() => {'type': 'bye'};
}

/// An `error`. Receivers treat a code they don't know as a general error.
/// Named so it doesn't hide dart:core's [Error].
final class ErrorMessage extends ControlMessage {
  const ErrorMessage({required this.code});

  final String code;

  @override
  Map<String, Object> toJson() => {'type': 'error', 'code': code};
}

abstract final class ErrorCodes {
  static const unsupportedProto = 'unsupported_proto';
  static const badMessage = 'bad_message';
  static const badProof = 'bad_proof';
  static const badAuth = 'bad_auth';
  static const full = 'full';
}

enum PhonePlatform {
  android('android'),
  ios('ios');

  const PhonePlatform(this.wireName);

  final String wireName;
}

/// Mixed: Xbox 360 pads for the first 4 slots, then DualShock 4. All
/// DualShock 4: DualShock 4 for every slot.
enum PadMode {
  mixed('mixed'),
  allDs4('all_ds4');

  const PadMode(this.wireName);

  final String wireName;
}

enum PadType {
  x360('x360'),
  ds4('ds4');

  const PadType(this.wireName);

  final String wireName;
}
