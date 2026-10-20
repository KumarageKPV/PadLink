import 'dart:typed_data';

import 'package:padlink_protocol/padlink_protocol.dart';
import 'package:padlink_protocol/src/crypto.dart'
    show pairingKeyInfo, sessionKeyInfo;
import 'package:test/test.dart';

import 'vectors.dart';

// Every value in protocol/vectors/crypto.json, computed from its inputs
// (protocol/SPEC.md, "Keys and MACs"). The phone checks no proof or auth mac,
// only packet macs.
const _file = 'crypto.json';

void main() {
  late X25519KeyPair pc;
  late X25519KeyPair phone;
  final code = vectorFile(_file)['pairingCode'] as String;

  setUpAll(() async {
    pc = await X25519KeyPair.fromPrivateKey(_value(['pc', 'privateKey']));
    phone = await X25519KeyPair.fromPrivateKey(_value(['phone', 'privateKey']));
  });

  test('key pairs have the vector public keys', () {
    expect(toHex(pc.publicKey), _hex(['pc', 'publicKey']));
    expect(toHex(phone.publicKey), _hex(['phone', 'publicKey']));
  });

  test('both sides agree on the vector shared secret', () async {
    expect(toHex((await pc.agree(phone.publicKey))!), _hex(['sharedSecret']));
    expect(toHex((await phone.agree(pc.publicKey))!), _hex(['sharedSecret']));
  });

  for (final vector in vectorGroup(_file, 'rejectedPublicKeys')) {
    test('"${vector['name']}" is refused by both key pairs', () async {
      final key = hex(vector['publicKey']);

      expect(await pc.agree(key), isNull);
      expect(await phone.agree(key), isNull);
    });
  }

  test('proof matches the vector', () async {
    final proof = await JoinKeys.proof(code, pc.publicKey, phone.publicKey);

    expect(toHex(proof), _hex(['proof']));
  });

  test('pairing key and its info match the vectors', () async {
    expect(
      toHex(pairingKeyInfo(pc.publicKey, phone.publicKey)),
      _hex(['pairingKeyInfo']),
    );
    expect(
      toHex(
        await JoinKeys.pairingKey(
          _value(['sharedSecret']),
          pc.publicKey,
          phone.publicKey,
        ),
      ),
      _hex(['pairingKey']),
    );
  });

  test('auth mac matches the vector', () async {
    final mac = await JoinKeys.authMac(
      _value(['pairingKey']),
      _value(['pcNonce']),
    );

    expect(toHex(mac), _hex(['authMac']));
  });

  test('session key and its info match the vectors', () async {
    expect(
      toHex(sessionKeyInfo(_value(['pcNonce']), _value(['phoneNonce']))),
      _hex(['sessionKeyInfo']),
    );
    expect(
      toHex(
        await JoinKeys.sessionKey(
          _value(['pairingKey']),
          _value(['pcNonce']),
          _value(['phoneNonce']),
        ),
      ),
      _hex(['sessionKey']),
    );
  });

  test('packet mac matches the vector', () {
    final sessionKey = _value(['sessionKey']);
    final data = _value(['packetMac', 'data']);

    expect(
      toHex(PacketMac.compute(sessionKey, data)),
      _hex(['packetMac', 'mac']),
    );
    expect(
      PacketMac.matches(sessionKey, data, _value(['packetMac', 'mac'])),
      isTrue,
    );
  });

  test('any flipped packet mac bit is rejected', () {
    final sessionKey = _value(['sessionKey']);
    final data = _value(['packetMac', 'data']);
    final mac = _value(['packetMac', 'mac']);

    for (var bit = 0; bit < mac.length * 8; bit++) {
      expect(
        PacketMac.matches(sessionKey, data, flipped(mac, bit)),
        isFalse,
        reason: 'bit $bit',
      );
    }
  });

  test('a packet mac of the wrong size is rejected', () {
    final sessionKey = _value(['sessionKey']);
    final data = _value(['packetMac', 'data']);
    final mac = _value(['packetMac', 'mac']);

    expect(PacketMac.matches(sessionKey, data, mac.sublist(1)), isFalse);
    expect(PacketMac.matches(sessionKey, data, Uint8List(0)), isFalse);
  });

  test('proof refuses a code that is not 6 ASCII digits', () {
    for (final bad in ['42917', '0429170', '04291a', '０４２９１７', '']) {
      expect(
        () => JoinKeys.proof(bad, pc.publicKey, phone.publicKey),
        throwsArgumentError,
        reason: bad,
      );
    }
  });

  test('fresh key pairs agree with each other', () async {
    final pc = await X25519KeyPair.generate();
    final phone = await X25519KeyPair.generate();

    final pcSecret = await pc.agree(phone.publicKey);
    expect(pcSecret, isNotNull);
    expect(await phone.agree(pc.publicKey), pcSecret);
    expect(pc.publicKey, isNot(phone.publicKey));
  });

  test('nonces are 16 fresh bytes', () {
    final first = JoinKeys.newNonce();

    expect(first, hasLength(JoinKeys.nonceSize));
    expect(JoinKeys.newNonce(), isNot(first));
  });

  test('a key pair keeps its public key to itself', () {
    expect(() => pc.publicKey[0] ^= 1, throwsUnsupportedError);
  });

  test('inputs of the wrong size throw', () {
    final key31 = Uint8List(31);
    final nonce15 = Uint8List(15);

    expect(() => X25519KeyPair.fromPrivateKey(key31), throwsArgumentError);
    expect(() => pc.agree(key31), throwsArgumentError);
    expect(
      () => JoinKeys.proof(code, key31, phone.publicKey),
      throwsArgumentError,
    );
    expect(
      () => JoinKeys.pairingKey(key31, pc.publicKey, phone.publicKey),
      throwsArgumentError,
    );
    expect(
      () => JoinKeys.authMac(_value(['pairingKey']), nonce15),
      throwsArgumentError,
    );
    expect(
      () => JoinKeys.sessionKey(
        _value(['pairingKey']),
        _value(['pcNonce']),
        nonce15,
      ),
      throwsArgumentError,
    );
    expect(
      () => PacketMac.compute(key31, _value(['packetMac', 'data'])),
      throwsArgumentError,
    );
  });
}

Uint8List _value(List<String> path) {
  Object? node = vectorFile(_file);
  for (final name in path) {
    node = (node as Map)[name];
  }
  return hex(node);
}

String _hex(List<String> path) => toHex(_value(path));
