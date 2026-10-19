import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:phone_spike/main.dart';
import 'package:phone_spike/packet.dart';
import 'package:phone_spike/sender.dart';

import 'fake_sink.dart';

/// Stands in for UdpSink.open: records each address and port, and hands out a FakeSink.
class Opened {
  final sinks = <FakeSink>[];
  final targets = <(InternetAddress, int)>[];

  Future<PacketSink> call(InternetAddress address, int port) async {
    targets.add((address, port));
    final sink = FakeSink();
    sinks.add(sink);
    return sink;
  }
}

final ipField = find.byKey(const Key('ip'));
final startButton = find.byKey(const Key('start'));
final status = find.byKey(const Key('status'));
final stick = find.byKey(const Key('stick'));
final buttonA = find.byKey(const Key('button-a'));
final buttonB = find.byKey(const Key('button-b'));

String statusText(WidgetTester tester) => tester.widget<Text>(status).data!;

Future<FakeSink> startSending(WidgetTester tester, Opened opened) async {
  await tester.enterText(ipField, '192.168.1.20');
  await tester.tap(startButton);
  await tester.pump();
  return opened.sinks.single;
}

void main() {
  testWidgets('shows the IP field, Start, the stick and both buttons', (
    tester,
  ) async {
    await tester.pumpWidget(SpikeApp(openSink: Opened().call));
    expect(ipField, findsOneWidget);
    expect(find.text('Start'), findsOneWidget);
    expect(stick, findsOneWidget);
    expect(buttonA, findsOneWidget);
    expect(buttonB, findsOneWidget);
    expect(statusText(tester), 'Not sending');
  });

  testWidgets('Start with a bad IP shows an error and sends nothing', (
    tester,
  ) async {
    final opened = Opened();
    await tester.pumpWidget(SpikeApp(openSink: opened.call));
    for (final text in ['', 'pc', '192.168.1', '::1']) {
      await tester.enterText(ipField, text);
      await tester.tap(startButton);
      await tester.pump();
      expect(statusText(tester), contains('IPv4 address'), reason: text);
    }
    expect(opened.targets, isEmpty);
  });

  testWidgets('Start sends to the IP on the input port, 60 times a second', (
    tester,
  ) async {
    final opened = Opened();
    await tester.pumpWidget(SpikeApp(openSink: opened.call));
    final sink = await startSending(tester, opened);

    expect(opened.targets.single.$1.address, '192.168.1.20');
    expect(opened.targets.single.$2, 47821);
    expect(statusText(tester), 'Sending to 192.168.1.20:47821');
    expect(find.text('Stop'), findsOneWidget);
    await tester.pump(const Duration(seconds: 1));
    expect(sink.packets.length, inInclusiveRange(60, 61));
    expect(sink.packets.last, {
      'seq': sink.packets.length - 1,
      'lx': 0.0,
      'ly': 0.0,
      'a': false,
      'b': false,
    });

    await tester.tap(startButton);
    await tester.pump();
  });

  testWidgets('holding A or B sends it pressed until released', (tester) async {
    final opened = Opened();
    await tester.pumpWidget(SpikeApp(openSink: opened.call));
    final sink = await startSending(tester, opened);

    final a = await tester.startGesture(tester.getCenter(buttonA));
    await tester.pump(sendInterval);
    expect(sink.packets.last, containsPair('a', true));
    final b = await tester.startGesture(tester.getCenter(buttonB), pointer: 2);
    await tester.pump(sendInterval);
    expect(
      sink.packets.last,
      allOf(containsPair('a', true), containsPair('b', true)),
    );
    await a.up();
    await tester.pump(sendInterval);
    expect(
      sink.packets.last,
      allOf(containsPair('a', false), containsPair('b', true)),
    );
    await b.up();
    await tester.pump(sendInterval);
    expect(
      sink.packets.last,
      allOf(containsPair('a', false), containsPair('b', false)),
    );

    await tester.tap(startButton);
    await tester.pump();
  });

  testWidgets('dragging the stick up sends ly = 1, and letting go centres it', (
    tester,
  ) async {
    final opened = Opened();
    await tester.pumpWidget(SpikeApp(openSink: opened.call));
    final sink = await startSending(tester, opened);

    // getCenter can land a rounding error off the stick's own centre, so moves use closeTo.
    final drag = await tester.startGesture(tester.getCenter(stick));
    await drag.moveBy(const Offset(0, -500));
    await tester.pump(sendInterval);
    expect(
      sink.packets.last,
      allOf(containsPair('lx', closeTo(0, 1e-9)), containsPair('ly', 1.0)),
    );
    await drag.moveBy(const Offset(500, 500));
    await tester.pump(sendInterval);
    expect(
      sink.packets.last,
      allOf(containsPair('lx', 1.0), containsPair('ly', closeTo(0, 1e-9))),
    );
    await drag.up();
    await tester.pump(sendInterval);
    expect(
      sink.packets.last,
      allOf(containsPair('lx', 0.0), containsPair('ly', 0.0)),
    );

    await tester.tap(startButton);
    await tester.pump();
  });

  testWidgets('Stop ends sending and closes the socket', (tester) async {
    final opened = Opened();
    await tester.pumpWidget(SpikeApp(openSink: opened.call));
    final sink = await startSending(tester, opened);

    await tester.tap(startButton);
    await tester.pump();
    final sent = sink.packets.length;
    await tester.pump(const Duration(seconds: 1));
    expect(sink.packets.length, sent);
    expect(sink.closed, isTrue);
    expect(statusText(tester), 'Not sending');
    expect(find.text('Start'), findsOneWidget);
  });

  testWidgets('closing the app while sending closes the socket', (
    tester,
  ) async {
    final opened = Opened();
    await tester.pumpWidget(SpikeApp(openSink: opened.call));
    final sink = await startSending(tester, opened);

    await tester.pumpWidget(const SizedBox());
    expect(sink.closed, isTrue);
  });

  testWidgets('fits a phone screen in portrait and landscape', (tester) async {
    addTearDown(tester.view.reset);
    for (final size in const [
      Size(360, 640),
      Size(640, 360),
      Size(412, 915),
      Size(915, 412),
    ]) {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      await tester.pumpWidget(SpikeApp(openSink: Opened().call));
      // An overflow would fail the test with a layout error.
      expect(tester.takeException(), isNull, reason: '$size');
      for (final control in [stick, buttonA, buttonB]) {
        final rect = tester.getRect(control);
        expect(
          rect.left >= 0 &&
              rect.top >= 0 &&
              rect.right <= size.width &&
              rect.bottom <= size.height,
          isTrue,
          reason: '$control at $size: $rect',
        );
      }
    }
  });

  testWidgets('a socket that will not open shows why', (tester) async {
    Future<PacketSink> failing(InternetAddress address, int port) async =>
        throw const SocketException('no network');
    await tester.pumpWidget(SpikeApp(openSink: failing));
    await tester.enterText(ipField, '192.168.1.20');
    await tester.tap(startButton);
    await tester.pump();
    expect(statusText(tester), contains('no network'));
    expect(find.text('Start'), findsOneWidget);
  });
}
