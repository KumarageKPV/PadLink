using System.Diagnostics;
using System.Runtime.InteropServices;
using PadLink.Core.Pads;

namespace PadLink.Core.Tests;

/// <summary>
/// Tests that plug pads into the driver run one at a time: padprobe counts every pad on the PC,
/// and XInput hands out user indices in the order pads appear.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DriverCollection
{
    public const string Name = "Driver";
}

/// <summary>Runs tools/padprobe.py from the repo's .venv (CLAUDE.md, Gotchas).</summary>
internal static class PadProbe
{
    // Windows takes a moment to list new pads; the spike's probes passed within 1 s.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly string Root = FindRoot();

    /// <summary>
    /// Runs <c>padprobe.py --expect counts</c> until it exits 0 or the timeout passes, and returns its
    /// last exit code with its messages.
    /// </summary>
    public static (int ExitCode, string Messages) Expect(string counts)
    {
        var python = Path.Combine(Root, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
            throw new FileNotFoundException("padprobe needs the repo's .venv (README.md, \"Python tools\")", python);
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            var (exitCode, messages) = Run(python, counts);
            if (exitCode == 0 || clock.Elapsed > Timeout)
                return (exitCode, $"{messages}  (try {attempt}, {clock.Elapsed.TotalSeconds:0.0} s)");
            Thread.Sleep(250);
        }
    }

    private static (int ExitCode, string Messages) Run(string python, string counts)
    {
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine("tools", "padprobe.py"));
        start.ArgumentList.Add("--expect");
        start.ArgumentList.Add(counts);
        using var process = Process.Start(start)!;
        var list = process.StandardOutput.ReadToEndAsync(); // the JSON list; read so the pipe can't fill
        var messages = process.StandardError.ReadToEnd();
        list.Wait();
        process.WaitForExit();
        return (process.ExitCode, messages.Trim());
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tools", "padprobe.py")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"No tools/padprobe.py above {AppContext.BaseDirectory}");
    }
}

/// <summary>Reads Xbox pads the way XInput games do.</summary>
internal static class XInput
{
    private const int Success = 0;

    /// <summary>
    /// The report XInput shows at a user index, or null when nothing is connected there. XInput leaves
    /// out the Guide button.
    /// </summary>
    public static X360Report? Read(int userIndex)
    {
        if (XInputGetState(userIndex, out var state) != Success)
            return null;
        return new X360Report
        {
            Buttons = (X360Buttons)state.Buttons,
            LeftTrigger = state.LeftTrigger,
            RightTrigger = state.RightTrigger,
            ThumbLX = state.ThumbLX,
            ThumbLY = state.ThumbLY,
            ThumbRX = state.ThumbRX,
            ThumbRY = state.ThumbRY,
        };
    }

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int userIndex, out State state);

    // XINPUT_STATE: a packet number, then XINPUT_GAMEPAD.
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }
}
