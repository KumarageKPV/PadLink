import 'dart:async';
import 'dart:io';

import 'packet.dart';

/// Where packets go: a UDP socket in the app, a fake in tests.
abstract interface class PacketSink {
  void send(List<int> bytes);
  void close();
}

/// Sends datagrams to one address and port.
class UdpSink implements PacketSink {
  UdpSink._(this._socket, this._address, this._port);

  final RawDatagramSocket _socket;
  final InternetAddress _address;
  final int _port;

  static Future<PacketSink> open(InternetAddress address, int port) async {
    final socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
    return UdpSink._(socket, address, port);
  }

  @override
  void send(List<int> bytes) => _socket.send(bytes, _address, _port);

  @override
  void close() => _socket.close();
}

/// Sends the current state every [sendInterval], each packet with the next seq.
class PadSender {
  PadSender(this._sink, this._readState);

  final PacketSink _sink;
  final PadState Function() _readState;
  Timer? _timer;
  int _seq = 0;

  void start() {
    _send();
    _timer = Timer.periodic(sendInterval, (_) => _send());
  }

  /// Stops the ticks and closes the sink.
  void stop() {
    _timer?.cancel();
    _timer = null;
    _sink.close();
  }

  void _send() => _sink.send(encodePacket(_seq++, _readState()));
}
