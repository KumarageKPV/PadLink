import 'dart:typed_data';

/// What the input and feedback packets share (protocol/SPEC.md, "Packet
/// rules").
abstract final class Packet {
  static const magic = 0x50;

  /// The packet version, and the `proto` number in `hello`.
  static const version = 1;

  static const inputType = 0x01;
  static const feedbackType = 0x02;
  static const slotCount = 16;
  static const macSize = 8;
}

/// Why a packet was dropped. The names are the reasons the vectors use.
enum PacketError { length, magic, version, type, slot }

/// A packet to drop.
final class PacketException implements Exception {
  const PacketException(this.error);

  final PacketError error;

  @override
  String toString() => 'PacketException: ${error.name}';
}

/// The input packet's button bits. They are positional, so each means the same
/// button on both pad types (protocol/SPEC.md, "Button bits").
abstract final class PadButtons {
  static const faceBottom = 1 << 0; // A / Cross
  static const faceRight = 1 << 1; // B / Circle
  static const faceLeft = 1 << 2; // X / Square
  static const faceTop = 1 << 3; // Y / Triangle
  static const shoulderLeft = 1 << 4; // LB / L1
  static const shoulderRight = 1 << 5; // RB / R1
  static const back = 1 << 6; // Back / Share
  static const start = 1 << 7; // Start / Options
  static const guide = 1 << 8; // Guide / PS
  static const stickLeft = 1 << 9; // L3
  static const stickRight = 1 << 10; // R3
  static const dpadUp = 1 << 11;
  static const dpadDown = 1 << 12;
  static const dpadLeft = 1 << 13;
  static const dpadRight = 1 << 14;

  /// The phone never sets it, the codec passes it through, and the pad mapping
  /// ignores it.
  static const reserved = 1 << 15;
}

/// The phone's 30-byte input packet, which carries the whole pad state
/// (protocol/SPEC.md, "Input packet").
final class InputPacket {
  static const size = 30;

  /// The mac covers every byte before this offset.
  static const macOffset = 22;

  /// Throws [RangeError] for a field outside its wire range. Without a [mac],
  /// [write] writes 8 zero bytes there, for the sender to fill in.
  InputPacket({
    required this.slot,
    required this.sequence,
    required this.buttons,
    required this.lx,
    required this.ly,
    required this.rx,
    required this.ry,
    required this.lt,
    required this.rt,
    required this.sentMs,
    Uint8List? mac,
  }) : mac = _macOf(mac) {
    _checkSlot(slot);
    RangeError.checkValueInInterval(sequence, 0, 0xFFFFFFFF, 'sequence');
    RangeError.checkValueInInterval(buttons, 0, 0xFFFF, 'buttons');
    _checkInt16(lx, 'lx');
    _checkInt16(ly, 'ly');
    _checkInt16(rx, 'rx');
    _checkInt16(ry, 'ry');
    _checkByte(lt, 'lt');
    _checkByte(rt, 'rt');
    RangeError.checkValueInInterval(sentMs, 0, 0xFFFF, 'sentMs');
  }

  /// Reads a packet and checks its length, magic, version, type and slot.
  /// Throws [PacketException] for a packet to drop. The mac needs the session
  /// key, so the caller checks it.
  factory InputPacket.read(Uint8List source) {
    _checkHeader(source, size, Packet.inputType);
    final data = ByteData.sublistView(source);
    return InputPacket(
      slot: source[3],
      sequence: data.getUint32(4, Endian.little),
      buttons: data.getUint16(8, Endian.little),
      lx: data.getInt16(10, Endian.little),
      ly: data.getInt16(12, Endian.little),
      rx: data.getInt16(14, Endian.little),
      ry: data.getInt16(16, Endian.little),
      lt: source[18],
      rt: source[19],
      sentMs: data.getUint16(20, Endian.little),
      mac: source.sublist(macOffset),
    );
  }

  final int slot;

  /// A u32, +1 per packet.
  final int sequence;

  /// [PadButtons] bits.
  final int buttons;

  /// Left stick, right is positive.
  final int lx;

  /// Left stick, up is positive.
  final int ly;

  final int rx;
  final int ry;
  final int lt;
  final int rt;

  /// The phone's millisecond clock, which wraps at 16 bits.
  final int sentMs;

  final Uint8List mac;

  /// Writes the packet to the first [size] bytes of destination.
  void write(Uint8List destination) {
    _writeHeader(destination, size, Packet.inputType, slot);
    ByteData.sublistView(destination)
      ..setUint32(4, sequence, Endian.little)
      ..setUint16(8, buttons, Endian.little)
      ..setInt16(10, lx, Endian.little)
      ..setInt16(12, ly, Endian.little)
      ..setInt16(14, rx, Endian.little)
      ..setInt16(16, ry, Endian.little)
      ..setUint8(18, lt)
      ..setUint8(19, rt)
      ..setUint16(20, sentMs, Endian.little);
    destination.setRange(macOffset, size, mac);
  }
}

/// The PC's 19-byte feedback packet: rumble, the slot colour and the echo for
/// round-trip time (protocol/SPEC.md, "Feedback packet").
final class FeedbackPacket {
  static const size = 19;

  /// The mac covers every byte before this offset.
  static const macOffset = 11;

  /// Throws [RangeError] for a field outside its wire range. Without a [mac],
  /// [write] writes 8 zero bytes there, for the sender to fill in.
  FeedbackPacket({
    required this.slot,
    required this.largeMotor,
    required this.smallMotor,
    required this.red,
    required this.green,
    required this.blue,
    required this.echoMs,
    Uint8List? mac,
  }) : mac = _macOf(mac) {
    _checkSlot(slot);
    _checkByte(largeMotor, 'largeMotor');
    _checkByte(smallMotor, 'smallMotor');
    _checkByte(red, 'red');
    _checkByte(green, 'green');
    _checkByte(blue, 'blue');
    RangeError.checkValueInInterval(echoMs, 0, 0xFFFF, 'echoMs');
  }

  /// Reads a packet like [InputPacket.read].
  factory FeedbackPacket.read(Uint8List source) {
    _checkHeader(source, size, Packet.feedbackType);
    return FeedbackPacket(
      slot: source[3],
      largeMotor: source[4],
      smallMotor: source[5],
      red: source[6],
      green: source[7],
      blue: source[8],
      echoMs: ByteData.sublistView(source).getUint16(9, Endian.little),
      mac: source.sublist(macOffset),
    );
  }

  final int slot;
  final int largeMotor;
  final int smallMotor;
  final int red;
  final int green;
  final int blue;

  /// The `sentMs` of the last input packet the PC accepted from this phone.
  final int echoMs;

  final Uint8List mac;

  /// Writes the packet to the first [size] bytes of destination.
  void write(Uint8List destination) {
    _writeHeader(destination, size, Packet.feedbackType, slot);
    destination
      ..[4] = largeMotor
      ..[5] = smallMotor
      ..[6] = red
      ..[7] = green
      ..[8] = blue;
    ByteData.sublistView(destination).setUint16(9, echoMs, Endian.little);
    destination.setRange(macOffset, size, mac);
  }
}

void _checkHeader(Uint8List source, int size, int type) {
  final error = _headerError(source, size, type);
  if (error != null) throw PacketException(error);
}

PacketError? _headerError(Uint8List source, int size, int type) {
  if (source.length != size) return PacketError.length;
  if (source[0] != Packet.magic) return PacketError.magic;
  if (source[1] != Packet.version) return PacketError.version;
  if (source[2] != type) return PacketError.type;
  if (source[3] >= Packet.slotCount) return PacketError.slot;
  return null;
}

void _writeHeader(Uint8List destination, int size, int type, int slot) {
  if (destination.length < size) {
    throw ArgumentError.value(
      destination.length,
      'destination',
      'A packet needs $size bytes',
    );
  }
  destination
    ..[0] = Packet.magic
    ..[1] = Packet.version
    ..[2] = type
    ..[3] = slot;
}

// A copy, so the caller's list can't change the packet.
Uint8List _macOf(Uint8List? mac) {
  if (mac == null) return Uint8List(Packet.macSize);
  if (mac.length != Packet.macSize) {
    throw ArgumentError.value(mac, 'mac', 'Must be ${Packet.macSize} bytes');
  }
  return Uint8List.fromList(mac);
}

void _checkSlot(int slot) =>
    RangeError.checkValueInInterval(slot, 0, Packet.slotCount - 1, 'slot');

void _checkByte(int value, String name) =>
    RangeError.checkValueInInterval(value, 0, 0xFF, name);

void _checkInt16(int value, String name) =>
    RangeError.checkValueInInterval(value, -0x8000, 0x7FFF, name);
