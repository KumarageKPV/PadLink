using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PadLink.Protocol.Crypto;

namespace PadLink.Core.Control;

/// <summary>
/// Keeps the pairing keys in one file that Windows DPAPI encrypts for the current user (docs/design.md,
/// "Security and privacy"), so the device names are hidden too. It reads the file once, and writes it
/// whole on every save, through a temporary file flushed to disk first, so a crash leaves the old file
/// or the new one.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiPairingStore : IPairingStore
{
    // DPAPI's optional entropy: another program running as the user must know it to decrypt the file.
    internal static readonly byte[] Entropy = "padlink/1 pairings"u8.ToArray();

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, StoredPhone> _phones;

    /// <summary>
    /// Reads the store at path; a missing file is an empty store. Throws <see cref="InvalidDataException"/>
    /// for a file it can't decrypt, such as one another user wrote, or whose contents aren't a store. I/O
    /// errors, such as a file another program holds, come through as they are.
    /// </summary>
    public DpapiPairingStore(string path)
    {
        _path = Path.GetFullPath(path);
        _phones = Load(_path);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PadLink", "pairings.bin");

    public bool Contains(string deviceId)
    {
        lock (_gate)
            return _phones.ContainsKey(deviceId);
    }

    public bool TryGetKey(string deviceId, Span<byte> pairingKey)
    {
        CheckSize(pairingKey);
        lock (_gate)
        {
            if (!_phones.TryGetValue(deviceId, out var phone))
                return false;
            phone.Key.CopyTo(pairingKey);
            return true;
        }
    }

    /// <summary>Throws, and changes nothing, if the file can't be written.</summary>
    public void Save(string deviceId, string deviceName, ReadOnlySpan<byte> pairingKey)
    {
        CheckSize(pairingKey);
        lock (_gate)
        {
            var phone = new StoredPhone(deviceId, deviceName, pairingKey.ToArray());
            var phones = new Dictionary<string, StoredPhone>(_phones) { [deviceId] = phone };
            try
            {
                Write(phones.Values);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(phone.Key);
                throw;
            }
            if (_phones.TryGetValue(deviceId, out var old))
                CryptographicOperations.ZeroMemory(old.Key);
            _phones[deviceId] = phone;
        }
    }

    private static void CheckSize(ReadOnlySpan<byte> pairingKey)
    {
        if (pairingKey.Length != JoinKeys.KeySize)
            throw new ArgumentException($"A pairing key is {JoinKeys.KeySize} bytes, not {pairingKey.Length}.", nameof(pairingKey));
    }

    private static Dictionary<string, StoredPhone> Load(string path)
    {
        byte[] file;
        try
        {
            file = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        byte[]? json = null;
        try
        {
            json = ProtectedData.Unprotect(file, Entropy, DataProtectionScope.CurrentUser);
            var phones = JsonSerializer.Deserialize(json, PairingJsonContext.Default.ListStoredPhone);
            if (phones is null || phones.Any(phone => string.IsNullOrEmpty(phone.DeviceId) || phone.Key is not { Length: JoinKeys.KeySize }))
                throw new JsonException("A phone without an id or a 32-byte key.");
            return phones.ToDictionary(phone => phone.DeviceId);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidDataException($"Can't read the pairing file {path}: {e.Message} Delete it to pair every phone again.", e);
        }
        finally
        {
            if (json is not null)
                CryptographicOperations.ZeroMemory(json);
        }
    }

    // Under the lock.
    private void Write(IEnumerable<StoredPhone> phones)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(phones.ToList(), PairingJsonContext.Default.ListStoredPhone);
        var temporary = _path + ".tmp";
        try
        {
            var file = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(file);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The error that got here matters more.
            }
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }
}

internal sealed record StoredPhone(string DeviceId, string DeviceName, byte[] Key);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<StoredPhone>))]
internal sealed partial class PairingJsonContext : JsonSerializerContext;
