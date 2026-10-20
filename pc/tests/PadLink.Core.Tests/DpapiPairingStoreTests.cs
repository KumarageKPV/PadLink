using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PadLink.Core.Control;
using PadLink.Protocol.Crypto;

namespace PadLink.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class DpapiPairingStoreTests : IDisposable
{
    private const string DeviceId = "5b0e8f0c-6a8e-4d0a-9b8e-2f4c1d7e9a31";

    private static readonly byte[] Key = Enumerable.Range(1, JoinKeys.KeySize).Select(i => (byte)(i * 7)).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(1, JoinKeys.KeySize).Select(i => (byte)(255 - i)).ToArray();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "padlink-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public DpapiPairingStoreTests()
    {
        _path = Path.Combine(_folder, "nested", "pairings.bin");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_missing_file_is_an_empty_store_and_stays_missing_until_a_save()
    {
        var store = new DpapiPairingStore(_path);

        Assert.False(store.Contains(DeviceId));
        Assert.False(store.TryGetKey(DeviceId, new byte[JoinKeys.KeySize]));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_saved_key_is_read_back_by_a_new_store()
    {
        new DpapiPairingStore(_path).Save(DeviceId, "Ana's phone", Key);

        var store = new DpapiPairingStore(_path);

        var key = new byte[JoinKeys.KeySize];
        Assert.True(store.Contains(DeviceId));
        Assert.True(store.TryGetKey(DeviceId, key));
        Assert.Equal(Key, key);
        Assert.False(store.Contains("another phone"));
    }

    [Fact]
    public void Saving_a_phone_again_replaces_its_key_and_keeps_the_others()
    {
        var store = new DpapiPairingStore(_path);
        store.Save(DeviceId, "Ana's phone", Key);
        store.Save("second", "Ben's phone", Key);

        store.Save(DeviceId, "Ana's new phone", OtherKey);

        var reread = new DpapiPairingStore(_path);
        var key = new byte[JoinKeys.KeySize];
        Assert.True(reread.TryGetKey(DeviceId, key));
        Assert.Equal(OtherKey, key);
        Assert.True(reread.TryGetKey("second", key));
        Assert.Equal(Key, key);
    }

    [Fact]
    public void The_file_holds_neither_the_key_nor_the_device_in_plain_text()
    {
        new DpapiPairingStore(_path).Save(DeviceId, "Ana's phone", Key);

        var file = File.ReadAllBytes(_path);

        Assert.Equal(-1, file.AsSpan().IndexOf(Key));
        foreach (var text in new[] { DeviceId, "Ana's phone", Convert.ToBase64String(Key) })
        {
            Assert.Equal(-1, file.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)));
            Assert.Equal(-1, file.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)));
        }
    }

    [Fact]
    public void A_damaged_file_throws_InvalidDataException()
    {
        new DpapiPairingStore(_path).Save(DeviceId, "Ana's phone", Key);
        var file = File.ReadAllBytes(_path);
        file[^1] ^= 1;
        File.WriteAllBytes(_path, file);

        Assert.Throws<InvalidDataException>(() => new DpapiPairingStore(_path));
    }

    public static TheoryData<string> BadContents => new()
    {
        "null",
        "{}",
        $$"""[{"deviceId":"","deviceName":"x","key":"{{Convert.ToBase64String(Key)}}"}]""",
        $$"""[{"deviceId":"a","deviceName":"x","key":"{{Convert.ToBase64String(Key[..^1])}}"}]""",
        """[{"deviceId":"a","deviceName":"x"}]""",
        $$"""[{"deviceId":"a","deviceName":"x","key":"{{Convert.ToBase64String(Key)}}"},{"deviceId":"a","deviceName":"y","key":"{{Convert.ToBase64String(Key)}}"}]""",
    };

    [Theory]
    [MemberData(nameof(BadContents))]
    public void Decrypted_contents_that_are_not_a_list_of_phones_with_ids_and_keys_throw_InvalidDataException(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), DpapiPairingStore.Entropy, DataProtectionScope.CurrentUser));

        Assert.Throws<InvalidDataException>(() => new DpapiPairingStore(_path));
    }

    [Fact]
    public void A_save_that_fails_to_write_changes_nothing_and_leaves_no_temporary_file()
    {
        var store = new DpapiPairingStore(_path);
        store.Save(DeviceId, "Ana's phone", Key);
        var saved = File.ReadAllBytes(_path);
        // A folder where the file goes: the write works, the move onto it fails.
        File.Delete(_path);
        Directory.CreateDirectory(_path);

        var failure = Record.Exception(() => store.Save("second", "Ben's phone", OtherKey));

        Assert.True(failure is IOException or UnauthorizedAccessException, $"threw {failure}");

        Assert.False(store.Contains("second"));
        var key = new byte[JoinKeys.KeySize];
        Assert.True(store.TryGetKey(DeviceId, key));
        Assert.Equal(Key, key);
        Assert.False(File.Exists(_path + ".tmp"));
        Directory.Delete(_path);
        File.WriteAllBytes(_path, saved);
        Assert.False(new DpapiPairingStore(_path).Contains("second"));
    }

    [Fact]
    public void A_save_leaves_no_temporary_file()
    {
        new DpapiPairingStore(_path).Save(DeviceId, "Ana's phone", Key);

        Assert.Equal([_path], Directory.GetFiles(Path.GetDirectoryName(_path)!));
    }

    [Fact]
    public void Keys_of_the_wrong_size_throw()
    {
        var store = new DpapiPairingStore(_path);
        store.Save(DeviceId, "Ana's phone", Key);

        Assert.Throws<ArgumentException>(() => store.Save(DeviceId, "Ana's phone", Key[..^1]));
        Assert.Throws<ArgumentException>(() => store.TryGetKey(DeviceId, new byte[JoinKeys.KeySize - 1]));
    }

    [Fact]
    public void The_default_path_is_in_local_app_data()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PadLink", "pairings.bin"),
            DpapiPairingStore.DefaultPath);
    }
}
