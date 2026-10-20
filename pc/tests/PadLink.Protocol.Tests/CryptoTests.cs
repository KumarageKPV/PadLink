using System.Text.Json.Nodes;
using PadLink.Protocol.Crypto;

namespace PadLink.Protocol.Tests;

// Every value in protocol/vectors/crypto.json, computed from its inputs (protocol/SPEC.md, "Keys and MACs").
public class CryptoTests
{
    private const string File = "crypto.json";

    private static readonly JsonObject Crypto = Vectors.File(File);
    private static readonly X25519KeyPair Pc = X25519KeyPair.FromPrivateKey(Value("pc", "privateKey"));
    private static readonly X25519KeyPair Phone = X25519KeyPair.FromPrivateKey(Value("phone", "privateKey"));
    private static readonly string Code = Crypto["pairingCode"]!.GetValue<string>();

    public static TheoryData<string> RejectedPublicKeys => Vectors.Names(File, "rejectedPublicKeys");

    [Fact]
    public void Key_pairs_have_the_vector_public_keys()
    {
        Assert.Equal(Hex("pc", "publicKey"), Convert.ToHexStringLower(Pc.PublicKey));
        Assert.Equal(Hex("phone", "publicKey"), Convert.ToHexStringLower(Phone.PublicKey));
    }

    [Fact]
    public void Both_sides_agree_on_the_vector_shared_secret()
    {
        Assert.True(Pc.TryAgree(Phone.PublicKey, out var pcSecret));
        Assert.True(Phone.TryAgree(Pc.PublicKey, out var phoneSecret));

        Assert.Equal(Hex("sharedSecret"), Convert.ToHexStringLower(pcSecret));
        Assert.Equal(Hex("sharedSecret"), Convert.ToHexStringLower(phoneSecret));
    }

    [Theory]
    [MemberData(nameof(RejectedPublicKeys))]
    public void Public_key_with_an_all_zero_shared_secret_is_refused(string name)
    {
        var key = Vectors.Hex(Vectors.Get(File, "rejectedPublicKeys", name)["publicKey"]);

        Assert.False(Pc.TryAgree(key, out var pcSecret));
        Assert.False(Phone.TryAgree(key, out var phoneSecret));
        Assert.Null(pcSecret);
        Assert.Null(phoneSecret);
    }

    [Fact]
    public void Proof_matches_the_vector()
    {
        var proof = JoinKeys.Proof(Code, Pc.PublicKey, Phone.PublicKey);

        Assert.Equal(Hex("proof"), Convert.ToHexStringLower(proof));
        Assert.True(JoinKeys.CheckProof(Code, Pc.PublicKey, Phone.PublicKey, Value("proof")));
    }

    [Fact]
    public void Pairing_key_and_its_info_match_the_vectors()
    {
        Assert.Equal(Hex("pairingKeyInfo"), Convert.ToHexStringLower(JoinKeys.PairingKeyInfo(Pc.PublicKey, Phone.PublicKey)));
        Assert.Equal(Hex("pairingKey"), Convert.ToHexStringLower(JoinKeys.PairingKey(Value("sharedSecret"), Pc.PublicKey, Phone.PublicKey)));
    }

    [Fact]
    public void Auth_mac_matches_the_vector()
    {
        var mac = JoinKeys.AuthMac(Value("pairingKey"), Value("pcNonce"));

        Assert.Equal(Hex("authMac"), Convert.ToHexStringLower(mac));
        Assert.True(JoinKeys.CheckAuthMac(Value("pairingKey"), Value("pcNonce"), Value("authMac")));
    }

    [Fact]
    public void Session_key_and_its_info_match_the_vectors()
    {
        Assert.Equal(Hex("sessionKeyInfo"), Convert.ToHexStringLower(JoinKeys.SessionKeyInfo(Value("pcNonce"), Value("phoneNonce"))));
        Assert.Equal(Hex("sessionKey"), Convert.ToHexStringLower(JoinKeys.SessionKey(Value("pairingKey"), Value("pcNonce"), Value("phoneNonce"))));
    }

    [Fact]
    public void Packet_mac_matches_the_vector()
    {
        var mac = new byte[Packet.MacSize];

        PacketMac.Compute(Value("sessionKey"), Value("packetMac", "data"), mac);

        Assert.Equal(Hex("packetMac", "mac"), Convert.ToHexStringLower(mac));
        Assert.True(PacketMac.Matches(Value("sessionKey"), Value("packetMac", "data"), Value("packetMac", "mac")));
    }

    [Fact]
    public void Any_flipped_proof_bit_is_rejected()
    {
        foreach (var proof in EachBitFlipped(Value("proof")))
            Assert.False(JoinKeys.CheckProof(Code, Pc.PublicKey, Phone.PublicKey, proof));
    }

    [Fact]
    public void Any_flipped_auth_mac_bit_is_rejected()
    {
        foreach (var mac in EachBitFlipped(Value("authMac")))
            Assert.False(JoinKeys.CheckAuthMac(Value("pairingKey"), Value("pcNonce"), mac));
    }

    [Fact]
    public void Any_flipped_packet_mac_bit_is_rejected()
    {
        foreach (var mac in EachBitFlipped(Value("packetMac", "mac")))
            Assert.False(PacketMac.Matches(Value("sessionKey"), Value("packetMac", "data"), mac));
    }

    [Fact]
    public void A_proof_or_mac_of_the_wrong_size_is_rejected()
    {
        Assert.False(JoinKeys.CheckProof(Code, Pc.PublicKey, Phone.PublicKey, Value("proof")[..^1]));
        Assert.False(JoinKeys.CheckAuthMac(Value("pairingKey"), Value("pcNonce"), [.. Value("authMac"), 0]));
        Assert.False(PacketMac.Matches(Value("sessionKey"), Value("packetMac", "data"), []));
    }

    [Fact]
    public void Proof_needs_the_right_code()
    {
        Assert.False(JoinKeys.CheckProof("042918", Pc.PublicKey, Phone.PublicKey, Value("proof")));
        Assert.False(JoinKeys.CheckProof(Code, Phone.PublicKey, Pc.PublicKey, Value("proof"))); // keys swapped
    }

    [Theory]
    [InlineData("42917")] // the leading zero counts
    [InlineData("0429170")]
    [InlineData("04291a")]
    [InlineData("０４２９１７")] // full-width digits aren't ASCII
    [InlineData("")]
    public void Proof_refuses_a_code_that_is_not_6_ascii_digits(string code)
    {
        Assert.Throws<ArgumentException>(() => JoinKeys.Proof(code, Pc.PublicKey, Phone.PublicKey));
        Assert.Throws<ArgumentException>(() => JoinKeys.CheckProof(code, Pc.PublicKey, Phone.PublicKey, Value("proof")));
    }

    [Fact]
    public void Fresh_key_pairs_agree_with_each_other()
    {
        var pc = X25519KeyPair.Generate();
        var phone = X25519KeyPair.Generate();

        Assert.True(pc.TryAgree(phone.PublicKey, out var pcSecret));
        Assert.True(phone.TryAgree(pc.PublicKey, out var phoneSecret));
        Assert.Equal(pcSecret, phoneSecret);
        Assert.NotEqual(pc.PublicKey.ToArray(), phone.PublicKey.ToArray());
    }

    [Fact]
    public void Nonces_are_16_fresh_bytes()
    {
        var first = JoinKeys.NewNonce();
        var second = JoinKeys.NewNonce();

        Assert.Equal(JoinKeys.NonceSize, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Inputs_of_the_wrong_size_throw()
    {
        var key31 = new byte[31];
        var nonce15 = new byte[15];

        Assert.Throws<ArgumentException>(() => X25519KeyPair.FromPrivateKey(key31));
        Assert.Throws<ArgumentException>(() => Pc.TryAgree(key31, out _));
        Assert.Throws<ArgumentException>(() => JoinKeys.Proof(Code, key31, Phone.PublicKey));
        Assert.Throws<ArgumentException>(() => JoinKeys.PairingKey(key31, Pc.PublicKey, Phone.PublicKey));
        Assert.Throws<ArgumentException>(() => JoinKeys.AuthMac(Value("pairingKey"), nonce15));
        Assert.Throws<ArgumentException>(() => JoinKeys.SessionKey(Value("pairingKey"), Value("pcNonce"), nonce15));
        Assert.Throws<ArgumentException>(() => PacketMac.Compute(key31, Value("packetMac", "data"), new byte[Packet.MacSize]));
        Assert.Throws<ArgumentException>(() => PacketMac.Compute(Value("sessionKey"), Value("packetMac", "data"), new byte[Packet.MacSize - 1]));
    }

    private static byte[] Value(params string[] path)
    {
        JsonNode? node = Crypto;
        foreach (var name in path)
            node = node![name];
        return Vectors.Hex(node);
    }

    private static string Hex(params string[] path) => Convert.ToHexStringLower(Value(path));

    private static IEnumerable<byte[]> EachBitFlipped(byte[] bytes) =>
        Enumerable.Range(0, bytes.Length * 8).Select(bit => Vectors.Flipped(bytes, bit));
}
