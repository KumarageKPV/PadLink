import 'package:flutter/material.dart';

import 'packet.dart';

// Both controls use raw pointer events rather than gestures, so a touch counts at once and a
// stick touch and a button touch can be held together.

/// A round stick. Reports values from [stickValue] while touched, and (0, 0) on release.
class Stick extends StatefulWidget {
  const Stick({super.key, required this.size, required this.onChanged});

  final double size;
  final ValueChanged<Offset> onChanged;

  @override
  State<Stick> createState() => _StickState();
}

class _StickState extends State<Stick> {
  int? _pointer; // the touch that owns the stick; others are ignored
  Offset _value = Offset.zero;

  void _move(PointerEvent event) {
    if (event.pointer != _pointer) return;
    final radius = widget.size / 2;
    _set(stickValue(event.localPosition - Offset(radius, radius), radius));
  }

  void _release(PointerEvent event) {
    if (event.pointer != _pointer) return;
    _pointer = null;
    _set(Offset.zero);
  }

  void _set(Offset value) {
    setState(() => _value = value);
    widget.onChanged(value);
  }

  @override
  Widget build(BuildContext context) {
    return Listener(
      onPointerDown: (event) {
        _pointer ??= event.pointer;
        _move(event);
      },
      onPointerMove: _move,
      onPointerUp: _release,
      onPointerCancel: _release,
      child: CustomPaint(
        size: Size.square(widget.size),
        painter: _StickPainter(_value, Theme.of(context).colorScheme),
      ),
    );
  }
}

class _StickPainter extends CustomPainter {
  _StickPainter(this.value, this.colors);

  final Offset value;
  final ColorScheme colors;

  @override
  void paint(Canvas canvas, Size size) {
    final radius = size.width / 2;
    final centre = Offset(radius, radius);
    canvas.drawCircle(
      centre,
      radius,
      Paint()..color = colors.surfaceContainerHighest,
    );
    // value's +y is up, the canvas's is down.
    final knob = centre + Offset(value.dx, -value.dy) * radius * 0.6;
    canvas.drawCircle(knob, radius * 0.4, Paint()..color = colors.primary);
  }

  @override
  bool shouldRepaint(_StickPainter old) =>
      old.value != value || old.colors != colors;
}

/// A round button, held while any touch is on it.
class PadButton extends StatefulWidget {
  const PadButton({
    super.key,
    required this.label,
    required this.size,
    required this.onChanged,
  });

  final String label;
  final double size;
  final ValueChanged<bool> onChanged;

  @override
  State<PadButton> createState() => _PadButtonState();
}

class _PadButtonState extends State<PadButton> {
  final _pointers = <int>{};

  void _down(PointerDownEvent event) {
    if (_pointers.isEmpty) widget.onChanged(true);
    setState(() => _pointers.add(event.pointer));
  }

  void _up(PointerEvent event) {
    if (!_pointers.remove(event.pointer)) return;
    setState(() {});
    if (_pointers.isEmpty) widget.onChanged(false);
  }

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final pressed = _pointers.isNotEmpty;
    return Listener(
      onPointerDown: _down,
      onPointerUp: _up,
      onPointerCancel: _up,
      child: Container(
        width: widget.size,
        height: widget.size,
        alignment: Alignment.center,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          color: pressed ? colors.primary : colors.surfaceContainerHighest,
        ),
        child: Text(
          widget.label,
          style: TextStyle(
            fontSize: widget.size * 0.4,
            fontWeight: FontWeight.bold,
            color: pressed ? colors.onPrimary : colors.onSurface,
          ),
        ),
      ),
    );
  }
}
