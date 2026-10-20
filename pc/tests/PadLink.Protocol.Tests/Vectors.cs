using System.Text.Json.Nodes;

namespace PadLink.Protocol.Tests;

/// <summary>Reads the shared test vectors in protocol/vectors/ (protocol/SPEC.md, "Test vectors").</summary>
internal static class Vectors
{
    private static readonly string Folder = FindFolder();

    public static JsonObject File(string file) =>
        JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(Folder, file)))!.AsObject();

    /// <summary>The names in one group ("valid", "invalid" or "ignored") of a file, one test case each.</summary>
    public static TheoryData<string> Names(string file, string group)
    {
        var names = new TheoryData<string>();
        foreach (var vector in Group(file, group))
            names.Add(vector["name"]!.GetValue<string>());
        return names;
    }

    public static JsonObject Get(string file, string group, string name) =>
        Group(file, group).Single(vector => vector["name"]!.GetValue<string>() == name);

    public static IEnumerable<JsonObject> Group(string file, string group) =>
        File(file)[group]!.AsArray().Select(vector => vector!.AsObject());

    public static byte[] Hex(JsonNode? node) => Convert.FromHexString(node!.GetValue<string>());

    /// <summary>A copy of bytes with one bit flipped. Bit 0 is the low bit of the first byte.</summary>
    public static byte[] Flipped(byte[] bytes, int bit)
    {
        var flipped = (byte[])bytes.Clone();
        flipped[bit / 8] ^= (byte)(1 << (bit % 8));
        return flipped;
    }

    // Tests run from bin/<configuration>/<framework>/, so walk up to the repository root.
    private static string FindFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var folder = Path.Combine(dir.FullName, "protocol", "vectors");
            if (Directory.Exists(folder))
                return folder;
        }
        throw new DirectoryNotFoundException($"No protocol/vectors folder above {AppContext.BaseDirectory}");
    }
}
