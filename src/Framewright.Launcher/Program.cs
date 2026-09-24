using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// Framewright Studio: the thing an artist double-clicks.
//
// The studio is a local web service plus a browser page. Starting one used to
// mean a Start Menu shortcut that ran PowerShell with the execution policy
// bypassed, which is both a console flash and an instruction Windows users are
// right to distrust. This does the same job as scripts/start-installed.ps1,
// without PowerShell: if the studio is already running it opens the browser;
// otherwise it starts Framewright.exe hidden, with its output going to log
// files, waits until it answers, and opens the browser. A studio that never
// comes up is explained in a dialog with the end of its error log, not left as
// a silent nothing.
//
// Optional local voice still needs its PowerShell worker; the installer keeps a
// second shortcut for that. This launcher opens the studio without it.
//
// "--stop" stops the studio this launcher started, and nothing else: the one
// named by its state file, and only while that process is still the
// Framewright.exe beside the launcher with the recorded start time.
return Launcher.Run(args);

internal static class Launcher
{
    private const string Title = "Framewright";

    public static int Run(string[] args)
    {
        var port = ReadPort(args);
        var openBrowser = !args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("FRAMEWRIGHT_LAUNCHER_NO_BROWSER") != "1";
        var url = $"http://127.0.0.1:{port}";
        var root = AppContext.BaseDirectory;
        var application = Path.Combine(root, "Framewright.exe");
        var stateRoot = Path.Combine(
            Environment.GetEnvironmentVariable("FRAMEWRIGHT_VOICE_RUNTIME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FramewrightVoice"),
            "state");
        Directory.CreateDirectory(stateRoot);
        var stdout = Path.Combine(stateRoot, "framewright.stdout.log");
        var stderr = Path.Combine(stateRoot, "framewright.stderr.log");
        var statePath = Path.Combine(stateRoot, "framewright.pid");

        try
        {
            if (args.Contains("--stop", StringComparer.OrdinalIgnoreCase)) return Stop(statePath, application);
            if (!IsLive(url))
            {
                if (!File.Exists(application))
                    return Fail($"Framewright.exe is missing beside this launcher:\n{application}");
                var process = StartHidden(application, root, url, stdout, stderr);
                WriteState(statePath, process, application, url);
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (!process.HasExited && !IsLive(url) && DateTime.UtcNow < deadline) Thread.Sleep(250);
                if (!IsLive(url))
                {
                    if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                    return Fail("Framewright did not start.\n\n" + Tail(stderr) + $"\n\nFull log: {stderr}");
                }
            }
            if (openBrowser) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Console.WriteLine($"Framewright is live at {url}.");
            return 0;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Fail("Framewright could not be started: " + exception.Message);
        }
    }

    private static int Stop(string statePath, string application)
    {
        var owned = FindOwned(statePath, application);
        if (owned is null)
        {
            Console.WriteLine("Framewright is not running from this folder.");
            return 0;
        }
        using (owned)
        {
            owned.Kill(entireProcessTree: true);
            if (!owned.WaitForExit(15000)) return Fail("Framewright did not stop within 15 seconds.");
        }
        File.Delete(statePath);
        Console.WriteLine("Framewright stopped.");
        return 0;
    }

    /// <summary>The recorded studio, if it is still that process; a reused process id is someone else's.</summary>
    private static Process? FindOwned(string statePath, string application)
    {
        if (!File.Exists(statePath)) return null;
        try
        {
            using var state = JsonDocument.Parse(File.ReadAllBytes(statePath));
            var root = state.RootElement;
            var process = Process.GetProcessById(root.GetProperty("pid").GetInt32());
            var recordedStart = DateTime.Parse(root.GetProperty("processStartTimeUtc").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            var path = process.MainModule?.FileName;
            if (path is not null
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(application), StringComparison.OrdinalIgnoreCase)
                && Math.Abs((process.StartTime.ToUniversalTime() - recordedStart).TotalSeconds) < 2)
                return process;
            process.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException
            or KeyNotFoundException or FormatException or Win32Exception or IOException)
        {
            return null;
        }
    }

    private static int ReadPort(string[] args)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase));
        var text = index >= 0 && index + 1 < args.Length ? args[index + 1] : Environment.GetEnvironmentVariable("FRAMEWRIGHT_PORT");
        return int.TryParse(text, out var port) && port is > 0 and < 65536 ? port : 5179;
    }

    private static bool IsLive(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = client.GetAsync($"{url}/health/live").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return false;
            using var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return body.RootElement.TryGetProperty("status", out var status)
                && status.GetString() is { } value
                && value.ToLowerInvariant() is "live" or "ready" or "degraded";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the studio with no console window and its output in two files.
    /// The files are handed to the child as its own standard handles, so the
    /// launcher can exit while the studio keeps writing; a pipe would tie the
    /// studio's logging to this process staying alive.
    /// </summary>
    private static Process StartHidden(string application, string workingDirectory, string url, string stdout, string stderr)
    {
        using var output = OpenInheritable(stdout);
        using var error = OpenInheritable(stderr);
        using var input = OpenInheritable("NUL", FileMode.Open, FileAccess.Read);
        var startup = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseStdHandles,
            hStdInput = input.DangerousGetHandle(),
            hStdOutput = output.DangerousGetHandle(),
            hStdError = error.DangerousGetHandle(),
        };
        var environment = new StringBuilder();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (string.Equals(key, "Urls", StringComparison.OrdinalIgnoreCase)) continue;
            environment.Append(key).Append('=').Append(entry.Value).Append('\0');
        }
        environment.Append("Urls=").Append(url).Append('\0').Append('\0');
        // CreateProcessW may write into its command line, so it gets a buffer.
        var command = $"\"{application}\"\0".ToCharArray();
        if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, true,
                CreateNoWindow | CreateUnicodeEnvironment, environment.ToString(), workingDirectory,
                ref startup, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(information.hThread);
        CloseHandle(information.hProcess);
        return Process.GetProcessById(information.dwProcessId);
    }

    private static SafeFileHandle OpenInheritable(string path, FileMode mode = FileMode.Create, FileAccess access = FileAccess.Write)
    {
        var handle = File.OpenHandle(path, mode, access, FileShare.ReadWrite);
        if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    /// <summary>The same state file start-installed.ps1 writes, so tooling that stops the studio finds it.</summary>
    private static void WriteState(string path, Process process, string application, string url)
    {
        // Written field by field: a trimmed launcher has no reflection to spare.
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteNumber("pid", process.Id);
        json.WriteString("processStartTimeUtc", process.StartTime.ToUniversalTime().ToString("O"));
        json.WriteString("executable", Path.GetFullPath(application));
        json.WriteString("url", url);
        json.WriteEndObject();
    }

    private static string Tail(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path).Where(line => line.Trim().Length > 0).TakeLast(12).ToArray();
            return lines.Length == 0 ? "It wrote nothing to its error log." : string.Join("\n", lines);
        }
        catch (IOException) { return "Its error log could not be read."; }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        if (Environment.GetEnvironmentVariable("FRAMEWRIGHT_LAUNCHER_NO_DIALOG") != "1")
            _ = MessageBoxW(IntPtr.Zero, message, Title, 0x10 /* MB_ICONERROR */);
        return 1;
    }

    private const int StartfUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint HandleFlagInherit = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string? applicationName, char[] commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, string environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
