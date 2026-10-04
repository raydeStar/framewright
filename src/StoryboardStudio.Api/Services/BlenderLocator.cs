using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>Which operating system's install conventions to search.</summary>
public enum BlenderPlatform { Windows, MacOS, Linux, Other }

/// <summary>A well-known folder on this machine, by meaning rather than by path.</summary>
public enum BlenderFolder { ProgramFiles, ProgramFilesX86, LocalApplicationData, Home }

/// <summary>What one <c>blender --version</c> said, or that it never started.</summary>
public sealed record BlenderProbe(bool Started, int ExitCode, string Output, string? Error);

/// <summary>
/// The machine, as far as finding Blender needs to see it. Everything the
/// locator reads goes through here, so its rules can be proved against a
/// described machine instead of whichever one runs the tests.
/// </summary>
public interface IBlenderHost
{
    BlenderPlatform Platform { get; }
    string? Environment(string name);
    string? Folder(BlenderFolder folder);
    bool FileExists(string path);
    /// <summary>The names, not the paths, of the directories directly inside one.</summary>
    IReadOnlyList<string> DirectoryNames(string path);
    string? ReadText(string path);
    /// <summary>Where Steam says it is installed (Windows registry), or null.</summary>
    string? SteamPath();
    Task<BlenderProbe> ProbeAsync(string executable, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Finds the Blender the compiler should be handed.</summary>
public interface IBlenderLocator
{
    Task<BlenderInstallSummary> LocateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Finds Blender when nobody said where it is, so the compiler can be told
/// exactly which executable to use.
///
/// The compiler deliberately does no searching of its own: its receipt has to
/// name the exact Blender a stage ran with, and a guess made inside it would be
/// invisible there. So the search happens here and its answer is passed as
/// <c>--blender</c>, exactly as a configured path always was.
///
/// The order is the order of how explicitly somebody said it. The setting,
/// then <c>BLENDER</c>, then <c>RAC_BLENDER</c> are used as given -- each is a
/// person naming a file, and quietly substituting a different Blender for a
/// broken one would hide the mistake. They are still asked their version, so
/// a wrong one is reported rather than discovered an hour into a stage. Below
/// those, PATH and each operating system's usual install places are searched,
/// and a candidate is only taken if it answers <c>--version</c> as Blender.
///
/// Optional throughout. Nothing here runs at startup, every failure becomes an
/// answer rather than an exception, and a machine without Blender is told so
/// while everything that does not need it carries on.
/// </summary>
public sealed partial class BlenderLocator(
    IConfiguration configuration, IBlenderHost host, TimeProvider timeProvider) : IBlenderLocator, IDisposable
{
    private const string Section = "Integrations:ReferenceAssetCompiler";
    public const string SettingName = Section + ":BlenderPath";
    public const string DiscoverySettingName = Section + ":DiscoverBlender";
    public const string OverrideAdvice =
        "To use a different Blender, set " + SettingName + " in appsettings.Local.json, or the BLENDER "
        + "environment variable, to the full path of its executable.";

    /// <summary>
    /// Long enough for a cold start behind a virus scanner, short enough that
    /// a hung candidate does not hold a readiness answer hostage.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A Blender that answered is remembered until the settings change. One
    /// that did not is asked about again after this long, so installing Blender
    /// while the studio is open does not need a restart to be noticed.
    /// </summary>
    private static readonly TimeSpan UnusableLifetime = TimeSpan.FromMinutes(1);

    private const string SteamAppFolder = "Blender";
    private static readonly string[] ExplicitVariables = ["BLENDER", "RAC_BLENDER"];

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile Remembered? remembered;

    private sealed record Remembered(string Key, BlenderInstallSummary Answer, DateTimeOffset At);
    private sealed record Candidate(string Path, string Source, string FoundBy);

    public void Dispose() => gate.Dispose();

    public async Task<BlenderInstallSummary> LocateAsync(CancellationToken cancellationToken)
    {
        var key = SettingsKey();
        if (Fresh(remembered, key) is { } known) return known;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(remembered, key) is { } raced) return raced;
            BlenderInstallSummary answer;
            try
            {
                answer = await FindAsync(cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Looking for an optional tool must never be what breaks the
                // studio. Whatever went wrong is the answer.
                answer = Unusable("none", "not found",
                    $"Framewright could not look for Blender: {error.Message}");
            }
            remembered = new Remembered(key, answer, timeProvider.GetUtcNow());
            return answer;
        }
        finally
        {
            gate.Release();
        }
    }

    private BlenderInstallSummary? Fresh(Remembered? candidate, string key)
    {
        if (candidate is null || candidate.Key != key) return null;
        if (candidate.Answer.Version is not null) return candidate.Answer;
        return timeProvider.GetUtcNow() - candidate.At < UnusableLifetime ? candidate.Answer : null;
    }

    /// <summary>Everything an answer depends on that somebody can change.</summary>
    private string SettingsKey() => string.Join('\n',
        configuration[SettingName] ?? "",
        configuration[DiscoverySettingName] ?? "",
        host.Environment("BLENDER") ?? "",
        host.Environment("RAC_BLENDER") ?? "",
        host.Environment("PATH") ?? "");

    private async Task<BlenderInstallSummary> FindAsync(CancellationToken cancellationToken)
    {
        if (Unquoted(configuration[SettingName]) is { } configured)
            return await ExplicitAsync(configured, "setting", $"set by {SettingName}", cancellationToken);

        if (!configuration.GetValue(DiscoverySettingName, true))
            return Unusable("disabled", "not looked for",
                $"Blender discovery is switched off ({DiscoverySettingName} is false) and no BlenderPath is set, "
                + "so the compiler is not given a Blender.");

        foreach (var variable in ExplicitVariables)
            if (Unquoted(host.Environment(variable)) is { } named)
                return await ExplicitAsync(named, "environment", $"set by the {variable} environment variable",
                    cancellationToken);

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refused = new List<string>();
        foreach (var candidate in Candidates())
        {
            if (!tried.Add(Normalised(candidate.Path)) || !host.FileExists(candidate.Path)) continue;
            var (version, problem) = await VerifyAsync(candidate.Path, cancellationToken);
            if (version is not null)
                return new BlenderInstallSummary(candidate.Path, version, candidate.Source, candidate.FoundBy,
                    OverrideAdvice, null);
            // Skipped, and remembered: a machine whose only Blender is broken
            // should be told that, not told it has none.
            refused.Add($"{candidate.Path} ({problem})");
        }

        return Unusable("none", "not found", refused.Count == 0
            ? "No Blender was found: none is set, none is on PATH, and none is in the usual install places "
              + "for this system. Install Blender, or tell Framewright where it is."
            : "No working Blender was found. These did not answer --version as Blender: "
              + string.Join("; ", refused) + ".");
    }

    /// <summary>
    /// A path somebody named. It is passed on as given even when it does not
    /// answer, because using a different Blender from the one somebody chose
    /// would be a substitution nobody asked for; the problem is reported instead.
    /// </summary>
    private async Task<BlenderInstallSummary> ExplicitAsync(
        string path, string source, string foundBy, CancellationToken cancellationToken)
    {
        var (version, problem) = await VerifyAsync(path, cancellationToken);
        return new BlenderInstallSummary(path, version, source, foundBy, OverrideAdvice,
            version is not null
                ? null
                : $"{path} did not answer --version as Blender ({problem}). It is still passed to the compiler "
                  + "because it was set explicitly; correct it, or remove it to let Framewright look for one.");
    }

    private async Task<(string? Version, string? Problem)> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        BlenderProbe probe;
        try
        {
            probe = await host.ProbeAsync(path, ProbeTimeout, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return (null, error.Message);
        }
        if (!probe.Started) return (null, probe.Error is { Length: > 0 } why ? why : "it could not be started");
        if (probe.ExitCode != 0) return (null, $"it exited with code {probe.ExitCode}");
        // The version line, wherever it is: some builds print a warning first.
        var match = probe.Output.Split('\n')
            .Select(line => VersionLine().Match(line.Trim()))
            .FirstOrDefault(found => found.Success);
        return match is not null
            ? (match.Groups["version"].Value.Trim(), null)
            : (null, "it answered, but not as Blender does");
    }

    /// <summary>The places Blender is usually installed, best first, for this platform.</summary>
    private IEnumerable<Candidate> Candidates()
    {
        var windows = host.Platform == BlenderPlatform.Windows;
        var executable = windows ? "blender.exe" : "blender";
        foreach (var directory in (host.Environment("PATH") ?? "")
                     .Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return new Candidate(Join(Unquoted(directory) ?? directory, executable), "path", "found on PATH");

        switch (host.Platform)
        {
            case BlenderPlatform.Windows:
                foreach (var candidate in ProgramFilesCandidates()) yield return candidate;
                foreach (var library in SteamLibraries())
                    yield return new Candidate(Join(library, "steamapps", "common", SteamAppFolder, "blender.exe"),
                        "steam", "found in your Steam library");
                // The Store's app execution alias, for a PATH that has lost it.
                if (host.Folder(BlenderFolder.LocalApplicationData) is { } local)
                    yield return new Candidate(Join(local, "Microsoft", "WindowsApps", "blender.exe"),
                        "microsoft-store", "found as a Microsoft Store app");
                break;
            case BlenderPlatform.MacOS:
                yield return new Candidate("/Applications/Blender.app/Contents/MacOS/Blender",
                    "applications", "found in Applications");
                if (host.Folder(BlenderFolder.Home) is { } macHome)
                    yield return new Candidate(Join(macHome, "Applications", "Blender.app", "Contents", "MacOS", "Blender"),
                        "applications", "found in your Applications folder");
                break;
            case BlenderPlatform.Linux:
                yield return new Candidate("/usr/bin/blender", "system", "found in /usr/bin");
                yield return new Candidate("/usr/local/bin/blender", "system", "found in /usr/local/bin");
                yield return new Candidate("/snap/bin/blender", "system", "installed as a snap");
                foreach (var library in SteamLibraries())
                    yield return new Candidate(Join(library, "steamapps", "common", SteamAppFolder, "blender"),
                        "steam", "found in your Steam library");
                break;
        }
    }

    /// <summary>
    /// <c>Program Files\Blender Foundation\Blender X.Y</c>, newest first. The
    /// installer keeps one folder per version, and an artist who installed a
    /// new one almost always means it.
    /// </summary>
    private IEnumerable<Candidate> ProgramFilesCandidates()
    {
        if (host.Folder(BlenderFolder.ProgramFiles) is not { } programFiles) yield break;
        var foundation = Join(programFiles, "Blender Foundation");
        var versions = host.DirectoryNames(foundation)
            .Select(name => (Name: name, Version: FolderVersion(name)))
            .Where(entry => entry.Version is not null)
            .OrderByDescending(entry => entry.Version)
            .ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in versions)
            yield return new Candidate(Join(foundation, name, "blender.exe"), "program-files", "found in Program Files");
    }

    /// <summary>
    /// Every Steam library on this machine. Steam installs apps into whichever
    /// library the person chose, and lists them all in libraryfolders.vdf.
    /// </summary>
    private IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (host.Platform == BlenderPlatform.Windows)
        {
            if (Unquoted(host.SteamPath()) is { } registered) roots.Add(registered);
            if (host.Folder(BlenderFolder.ProgramFilesX86) is { } x86) roots.Add(Join(x86, "Steam"));
        }
        else if (host.Platform == BlenderPlatform.Linux && host.Folder(BlenderFolder.Home) is { } home)
        {
            roots.Add(Join(home, ".steam", "steam"));
            roots.Add(Join(home, ".local", "share", "Steam"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            // The list first: it spells each library the way Steam shows it,
            // where the registry's own value is lower case with forward
            // slashes, and that path ends up in every receipt.
            // The current file lives in steamapps; older clients kept it in config.
            foreach (var listing in new[] { Join(root, "steamapps", "libraryfolders.vdf"), Join(root, "config", "libraryfolders.vdf") })
            {
                if (host.ReadText(listing) is not { } text) continue;
                foreach (var library in LibraryPaths(text))
                    if (seen.Add(Normalised(library))) yield return library;
            }
            if (seen.Add(Normalised(root))) yield return root;
        }
    }

    /// <summary>
    /// The library paths in a libraryfolders.vdf: <c>"path" "D:\\Games"</c>
    /// inside each numbered entry today, or the numbered entry's own value in
    /// files older Steam clients wrote.
    /// </summary>
    internal static IReadOnlyList<string> LibraryPaths(string vdf)
    {
        var found = new List<string>();
        foreach (Match match in LibraryPathEntry().Matches(vdf))
            found.Add(Unescape(match.Groups["value"].Value));
        foreach (Match match in LegacyLibraryEntry().Matches(vdf))
        {
            var value = Unescape(match.Groups["value"].Value);
            // App ids and sizes are numbered entries too; a library is a path.
            if (value.Contains('\\') || value.Contains('/') || value.Contains(':')) found.Add(value);
        }
        return found;
    }

    private static string Unescape(string value) => value.Replace(@"\\", @"\", StringComparison.Ordinal);

    private static Version? FolderVersion(string name)
    {
        var match = FolderVersionPattern().Match(name);
        if (!match.Success) return null;
        return Version.TryParse(match.Groups["version"].Value, out var version) ? version : null;
    }

    private static BlenderInstallSummary Unusable(string source, string foundBy, string problem) =>
        new(null, null, source, foundBy, OverrideAdvice, problem);

    /// <summary>
    /// Joined with the separator the described platform uses, so a Windows
    /// machine is described faithfully even to a test running elsewhere.
    /// </summary>
    private string Join(string first, params string[] rest)
    {
        var windows = host.Platform == BlenderPlatform.Windows;
        var separator = windows ? '\\' : '/';
        var builder = new StringBuilder((windows ? first.Replace('/', '\\') : first).TrimEnd('\\', '/'));
        foreach (var part in rest) builder.Append(separator).Append(part);
        return builder.ToString();
    }

    private static string Normalised(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static string? Unquoted(string? value)
    {
        var trimmed = value?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    [GeneratedRegex(@"^Blender\s+(?<version>\d+\.\d+(?:\.\d+)?(?:\s.*)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex FolderVersionPattern();

    [GeneratedRegex(@"""path""\s+""(?<value>(?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathEntry();

    [GeneratedRegex(@"^\s*""\d+""\s+""(?<value>(?:[^""\\]|\\.)*)""\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex LegacyLibraryEntry();
}

/// <summary>This machine, as the Blender locator sees it.</summary>
public sealed class SystemBlenderHost : IBlenderHost
{
    public BlenderPlatform Platform =>
        OperatingSystem.IsWindows() ? BlenderPlatform.Windows
        : OperatingSystem.IsMacOS() ? BlenderPlatform.MacOS
        : OperatingSystem.IsLinux() ? BlenderPlatform.Linux
        : BlenderPlatform.Other;

    public string? Environment(string name) => System.Environment.GetEnvironmentVariable(name);

    public string? Folder(BlenderFolder folder)
    {
        var path = folder switch
        {
            BlenderFolder.ProgramFiles => System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            BlenderFolder.ProgramFilesX86 => System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
            BlenderFolder.LocalApplicationData => System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            BlenderFolder.Home => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            _ => "",
        };
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public IReadOnlyList<string> DirectoryNames(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? [.. Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>()]
                : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public string? SteamPath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public async Task<BlenderProbe> ProbeAsync(string executable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // An immediate end of input, so nothing it might ask can block it.
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new BlenderProbe(false, -1, "", "it could not be started");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
                                          or PlatformNotSupportedException)
        {
            // The runtime's own message names the working directory and the
            // whole command; what a person needs is which of two things it was.
            return new BlenderProbe(false, -1, "", Path.IsPathRooted(executable) && !FileExists(executable)
                ? "there is no file at that path"
                : $"it could not be started: {error.Message}");
        }
        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var failure = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // Whoever stopped waiting, nothing is left running behind them.
            try { process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new BlenderProbe(false, -1, "",
                string.Create(CultureInfo.InvariantCulture, $"it did not answer within {timeout.TotalSeconds:0} seconds"));
        }
        return new BlenderProbe(true, process.ExitCode, await output, await failure);
    }
}
