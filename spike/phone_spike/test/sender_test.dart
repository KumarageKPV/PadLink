import 'dart:convert';
import 'dart:io';

import 'package:fake_async/fake_async.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phone_spike/packet.dart';
import 'package:phone_spike/sender.dart';

import 'fake_sink.dart';

void main() {
  test('sends one packet at once, then one per interval with the next seq', () {
    fakeAsync((async) {
      final sink = FakeSink();
      final sender = PadSender(sink, () => const PadState())..start();
      expect(sink.packets.map((p) => p['seq']), [0]);
      async.elapse(sendInterval * 3);
      expect(sink.packets.map((p) => p['seq']), [0, 1, 2, 3]);
      sender.stop();
    });
  });

  test('sends 60 packets a second', () {
    fakeAsync((async) {
      final sink = FakeSink();
      final sender = PadSender(sink, () => const PadState())..start();
      async.elapse(const Duration(seconds: 1));
      expect(sink.packets.length, inInclusiveRange(60, 61));
      sender.stop();
    });
  });

  test('reads the state again for every packet', () {
    fakeAsync((async) {
      final sink = FakeSink();
      var state = const PadState();
      final sender = PadSender(sink, () => state)..start();
      state = const PadState(lx: 1, b: true);
      async.elapse(sendInterval);
      expect(sink.packets.last, {
        'seq': 1,
        'lx': 1.0,
        'ly': 0.0,
        'a': false,
        'b': true,
      });
      sender.stop();
    });
  });

  test('stop ends the ticks and closes the sink', () {
    fakeAsync((async) {
      final sink = FakeSink();
      final sender = PadSender(sink, () => const PadState())..start();
      sender.stop();
      async.elapse(sendInterval * 5);
      expect(sink.packets.length, 1);
      expect(sink.closed, isTrue);
    });
  });

  test('UdpSink sends datagrams to the address and port', () async {
    final receiver = await RawDatagramSocket.bind(
      InternetAddress.loopbackIPv4,
      0,
    );
    addTearDown(receiver.close);
    final sink = await UdpSink.open(
      InternetAddress.loopbackIPv4,
      receiver.port,
    );
    addTearDown(sink.close);
    // A read event can come with no datagram ready yet, so wait for the first real one.
    final received = receiver
        .where((event) => event == RawSocketEvent.read)
        .map((_) => receiver.receive())
        .firstWhere((datagram) => datagram != null);

    sink.send(encodePacket(7, const PadState(a: true)));

    final datagram = await received.timeout(const Duration(seconds: 5));
    expect(jsonDecode(utf8.decode(datagram!.data))['seq'], 7);
  });
}
