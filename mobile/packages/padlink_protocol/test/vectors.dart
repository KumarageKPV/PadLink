import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

/// Reads the shared test vectors in protocol/vectors/ (protocol/SPEC.md,
/// "Test vectors").
final Directory _folder = _findFolder();
final Map<String, Map<String, Object?>> _files = {};

Map<String, Object?> vectorFile(String name) => _files.putIfAbsent(
  name,
  () =>
      jsonDecode(File('${_folder.path}/$name').readAsStringSync())
          as Map<String, Object?>,
);

/// The vectors in one group ("valid", "invalid" or "ignored") of a file.
List<Map<String, Object?>> vectorGroup(String file, String group) =>
    (vectorFile(file)[group] as List).cast<Map<String, Object?>>();

Uint8List hex(Object? text) {
  final digits = text as String;
  return Uint8List.fromList([
    for (var i = 0; i < digits.length; i += 2)
      int.parse(digits.substring(i, i + 2), radix: 16),
  ]);
}

String toHex(List<int> bytes) =>
    bytes.map((byte) => byte.toRadixString(16).padLeft(2, '0')).join();

// dart test runs from the package folder, so walk up to the repository root.
Directory _findFolder() {
  for (var dir = Directory.current.absolute; ; dir = dir.parent) {
    final folder = Directory('${dir.path}/protocol/vectors');
    if (folder.existsSync()) return folder;
    if (dir.parent.path == dir.path) {
      throw StateError(
        'No protocol/vectors folder above ${Directory.current.path}',
      );
    }
  }
}
