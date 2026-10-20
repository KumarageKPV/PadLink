/// PadLink's wire protocol for the phone app: UDP input and feedback packets,
/// TCP control frames and the pad mapping (protocol/SPEC.md). Tested against
/// the shared vectors in protocol/vectors/.
library;

export 'src/control.dart';
export 'src/mapping.dart';
export 'src/packets.dart';
