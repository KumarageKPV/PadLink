import 'dart:convert';

import 'package:phone_spike/sender.dart';

/// Keeps every packet sent, decoded, in place of a UDP socket.
class FakeSink implements PacketSink {
  final packets = <Map<String, Object?>>[];
  bool closed = false;

  @override
  void send(List<int> bytes) {
    if (closed) throw StateError('sent after close');
    packets.add(jsonDecode(utf8.decode(bytes)) as Map<String, Object?>);
  }

  @override
  void close() => closed = true;
}
