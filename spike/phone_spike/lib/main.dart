// Spike (PLAN.md 1.3): one stick and two buttons, sent about 60 times a second as UDP JSON to the
// IP typed in. spike/server.py turns the packets into a DualShock 4 pad. No pairing or security.

import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'controls.dart';
import 'packet.dart';
import 'sender.dart';

void main() => runApp(const SpikeApp());

typedef SinkOpener = Future<PacketSink> Function(
  InternetAddress address,
  int port,
);

class SpikeApp extends StatelessWidget {
  const SpikeApp({super.key, this.openSink = UdpSink.open});

  final SinkOpener openSink;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'PadLink spike',
      theme: ThemeData(
        colorSchemeSeed: Colors.teal,
        brightness: Brightness.dark,
      ),
      home: ControllerPage(openSink: openSink),
    );
  }
}

class ControllerPage extends StatefulWidget {
  const ControllerPage({super.key, required this.openSink});

  final SinkOpener openSink;

  @override
  State<ControllerPage> createState() => _ControllerPageState();
}

class _ControllerPageState extends State<ControllerPage> {
  final _ip = TextEditingController();
  PadState _state = const PadState(); // read by the sender on every tick
  PadSender? _sender;
  bool _opening = false;
  String _status = 'Not sending';

  Future<void> _start() async {
    final address = InternetAddress.tryParse(_ip.text.trim());
    if (address == null || address.type != InternetAddressType.IPv4) {
      setState(() => _status = "Type the PC's IPv4 address, like 192.168.1.20");
      return;
    }
    setState(() => _opening = true);
    final PacketSink sink;
    try {
      sink = await widget.openSink(address, inputPort);
    } on SocketException catch (error) {
      if (mounted) {
        setState(() {
          _opening = false;
          _status = 'Could not open a socket: ${error.message}';
        });
      }
      return;
    }
    if (!mounted) {
      sink.close();
      return;
    }
    setState(() {
      _opening = false;
      _sender = PadSender(sink, () => _state)..start();
      _status = 'Sending to ${address.address}:$inputPort';
    });
  }

  void _stop() {
    _sender?.stop();
    setState(() {
      _sender = null;
      _status = 'Not sending';
    });
  }

  @override
  void dispose() {
    _sender?.stop();
    _ip.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final sending = _sender != null;
    return Scaffold(
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            children: [
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      key: const Key('ip'),
                      controller: _ip,
                      enabled: !sending,
                      keyboardType: const TextInputType.numberWithOptions(
                        decimal: true,
                      ),
                      decoration: const InputDecoration(
                        labelText: 'PC IP address',
                        hintText: '192.168.1.20',
                      ),
                    ),
                  ),
                  const SizedBox(width: 16),
                  FilledButton(
                    key: const Key('start'),
                    onPressed: _opening ? null : (sending ? _stop : _start),
                    child: Text(sending ? 'Stop' : 'Start'),
                  ),
                ],
              ),
              const SizedBox(height: 8),
              Text(_status, key: const Key('status')),
              const SizedBox(height: 16),
              Expanded(
                child: LayoutBuilder(
                  builder: (context, constraints) {
                    final stickSize =
                        math.min(
                          constraints.maxHeight,
                          constraints.maxWidth / 2,
                        ) *
                        0.9;
                    final buttonSize = math.min(stickSize / 2.2, 96.0);
                    return Row(
                      mainAxisAlignment: MainAxisAlignment.spaceAround,
                      children: [
                        Stick(
                          key: const Key('stick'),
                          size: stickSize,
                          onChanged: (value) => _state = _state.copyWith(
                            lx: value.dx,
                            ly: value.dy,
                          ),
                        ),
                        Column(
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            PadButton(
                              key: const Key('button-b'),
                              label: 'B',
                              size: buttonSize,
                              onChanged: (pressed) =>
                                  _state = _state.copyWith(b: pressed),
                            ),
                            SizedBox(height: buttonSize * 0.2),
                            PadButton(
                              key: const Key('button-a'),
                              label: 'A',
                              size: buttonSize,
                              onChanged: (pressed) =>
                                  _state = _state.copyWith(a: pressed),
                            ),
                          ],
                        ),
                      ],
                    );
                  },
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
