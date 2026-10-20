import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:cryptography/cryptography.dart';
import 'package:cryptography/dart.dart';
import 'package:cryptography/helpers.dart';

import 'packets.dart';

// The pure-Dart implementations, so the packet mac can run synchronously.
const _x25519 = DartX25519();
const _hmac = DartHmac(DartSha256());
const _hkdf = DartHkdf(hmac: _hmac, outputLength: JoinKeys.keySize);

// RFC 5869's salt when none is given. Passed explicitly, as SPEC.md asks.
final _salt = Uint8List(32);

final _random = Random.secure();

/// An X25519 key pair (RFC 7748). The phone makes a fresh one for each pairing
/// (protocol/SPEC.md, "Join sequence").
final class X25519KeyPair {
  X25519KeyPair._(this._keyPair, List<int> publicKey)
    : publicKey = Uint8List.fromList(publicKey).asUnmodifiableView();

  static const keySize = 32;

  static Future<X25519KeyPair> generate() =>
      fromPrivateKey(_randomBytes(keySize));

  /// A key pair from a fixed private key, such as the vectors' RFC 7748 keys.
  static Future<X25519KeyPair> fromPrivateKey(Uint8List privateKey) async {
    _checkSize(privateKey, keySize, 'privateKey');
    final keyPair = await (await _x25519.newKeyPairFromSeed(privateKey))
        .extract();
    return X25519KeyPair._(keyPair, keyPair.publicKey.bytes);
  }

  final SimpleKeyPairData _keyPair;
  final Uint8List publicKey;

  /// The shared secret with a peer's public key, or null for a key whose
  /// shared secret is all zero bytes, which protocol/SPEC.md says to refuse.
  /// The `cryptography` package returns those without complaint.
  Future<Uint8List?> agree(Uint8List peerPublicKey) async {
    _checkSize(peerPublicKey, keySize, 'peerPublicKey');
    final secret = await _x25519.sharedSecretKey(
      keyPair: _keyPair,
      remotePublicKey: SimplePublicKey(peerPublicKey, type: KeyPairType.x25519),
    );
    final bytes = Uint8List.fromList(await secret.extractBytes());
    return bytes.fold(0, (bits, byte) => bits | byte) == 0 ? null : bytes;
  }
}

/// The keys and MACs of the join sequence (protocol/SPEC.md, "Keys and MACs").
/// Inputs of the wrong size throw [ArgumentError]. The phone sends the proof
/// and the auth mac; the PC checks them.
abstract final class JoinKeys {
  static const nonceSize = 16;

  /// The size of the pairing key and the session key.
  static const keySize = 32;

  /// The size of the pairing proof and the auth mac.
  static const hmacSize = 32;

  static const codeLength = 6;

  static Uint8List newNonce() => _randomBytes(nonceSize);

  /// HMAC-SHA256 of the PC's then the phone's public key, keyed with the code
  /// as ASCII. Throws [ArgumentError] for a code that isn't 6 ASCII digits.
  static Future<Uint8List> proof(
    String code,
    Uint8List pcPublicKey,
    Uint8List phonePublicKey,
  ) async {
    if (!RegExp(r'^[0-9]{6}$').hasMatch(code)) {
      throw ArgumentError.value(code, 'code', 'Must be 6 ASCII digits');
    }
    _checkSize(pcPublicKey, X25519KeyPair.keySize, 'pcPublicKey');
    _checkSize(phonePublicKey, X25519KeyPair.keySize, 'phonePublicKey');
    return _hmacOf(ascii.encode(code), [...pcPublicKey, ...phonePublicKey]);
  }

  static Future<Uint8List> pairingKey(
    Uint8List sharedSecret,
    Uint8List pcPublicKey,
    Uint8List phonePublicKey,
  ) async {
    _checkSize(sharedSecret, X25519KeyPair.keySize, 'sharedSecret');
    return _hkdfOf(sharedSecret, pairingKeyInfo(pcPublicKey, phonePublicKey));
  }

  /// The known phone's answer to `challenge`.
  static Future<Uint8List> authMac(
    Uint8List pairingKey,
    Uint8List pcNonce,
  ) async {
    _checkSize(pairingKey, keySize, 'pairingKey');
    _checkSize(pcNonce, nonceSize, 'pcNonce');
    return _hmacOf(pairingKey, pcNonce);
  }

  /// The key for every packet mac of one session.
  static Future<Uint8List> sessionKey(
    Uint8List pairingKey,
    Uint8List pcNonce,
    Uint8List phoneNonce,
  ) async {
    _checkSize(pairingKey, keySize, 'pairingKey');
    return _hkdfOf(pairingKey, sessionKeyInfo(pcNonce, phoneNonce));
  }
}

/// The packet mac: the first 8 bytes of HMAC-SHA256(session key, every packet
/// byte before it) (protocol/SPEC.md, "Keys and MACs").
abstract final class PacketMac {
  static Uint8List compute(Uint8List sessionKey, Uint8List data) {
    checkSessionKey(sessionKey);
    return _hmacOf(sessionKey, data).sublist(0, Packet.macSize);
  }

  /// Whether mac is the packet mac of data, compared in constant time.
  static bool matches(Uint8List sessionKey, Uint8List data, Uint8List mac) =>
      constantTimeBytesEquality.equals(compute(sessionKey, data), mac);

  static void checkSessionKey(Uint8List sessionKey) =>
      _checkSize(sessionKey, JoinKeys.keySize, 'sessionKey');
}

/// HKDF's info for the pairing key. Not exported: the tests check it against
/// the vectors.
Uint8List pairingKeyInfo(Uint8List pcPublicKey, Uint8List phonePublicKey) {
  _checkSize(pcPublicKey, X25519KeyPair.keySize, 'pcPublicKey');
  _checkSize(phonePublicKey, X25519KeyPair.keySize, 'phonePublicKey');
  return Uint8List.fromList([
    ...ascii.encode('padlink/1 pairing'),
    ...pcPublicKey,
    ...phonePublicKey,
  ]);
}

/// HKDF's info for the session key, like [pairingKeyInfo].
Uint8List sessionKeyInfo(Uint8List pcNonce, Uint8List phoneNonce) {
  _checkSize(pcNonce, JoinKeys.nonceSize, 'pcNonce');
  _checkSize(phoneNonce, JoinKeys.nonceSize, 'phoneNonce');
  return Uint8List.fromList([
    ...ascii.encode('padlink/1 session'),
    ...pcNonce,
    ...phoneNonce,
  ]);
}

Uint8List _hmacOf(List<int> key, List<int> data) => Uint8List.fromList(
  _hmac
      .calculateMacSync(
        data,
        secretKeyData: SecretKeyData(key),
        nonce: const [],
      )
      .bytes,
);

Future<Uint8List> _hkdfOf(Uint8List inputKey, Uint8List info) async {
  final key = await _hkdf.deriveKey(
    secretKey: SecretKey(inputKey),
    nonce: _salt,
    info: info,
  );
  return Uint8List.fromList(key.bytes);
}

Uint8List _randomBytes(int length) =>
    Uint8List.fromList([for (var i = 0; i < length; i++) _random.nextInt(256)]);

void _checkSize(Uint8List value, int size, String name) {
  if (value.length != size) {
    throw ArgumentError.value(value, name, 'Must be $size bytes');
  }
}
