/// PadLink's wire protocol for the phone app: UDP input and feedback packets,
/// TCP control frames, the pad mapping and the join crypto (protocol/SPEC.md).
/// Tested against the shared vectors in protocol/vectors/.
library;

export 'src/control.dart';
export 'src/crypto.dart' hide pairingKeyInfo, sessionKeyInfo;
export 'src/mapping.dart';
export 'src/packets.dart';
