using Microsoft.Extensions.Configuration;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Finding Blender when nobody said where it is, proved against a described
/// machine rather than whichever one runs the tests. No Blender is started
/// here: every "Blender" below is a file the fake host says exists and an
/// answer it says that file gives to --version.
/// </summary>
public sealed class BlenderLocatorTests
{
    private const string Setting = "Integrations:ReferenceAssetCompiler:BlenderPath";
    private const string Discovery = "Integrations:ReferenceAssetCompiler:DiscoverBlender";

    /// <summary>A machine, described: its files, its variables, and what each executable says.</summary>
    private sealed class FakeHost(BlenderPlatform platform) : IBlenderHost
    {
        private readonly List<string> files = [];
        private readonly Dictionary<string, BlenderProbe> answers = [];
        private readonly HashSet<string> throwing = [];

        public BlenderPlatform Platform { get; } = platform;
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<BlenderFolder, string> Folders { get; } = [];
        public Dictionary<string, string> Texts { get; } = [];
        public string? Steam { get; set; }
        public List<string> Probed { get; } = [];

        public FakeHost Install(string path, string version = "5.2.2 LTS") => Answer(path,
            new BlenderProbe(true, 0, $"Blender {version}\n\tbuild date: 2026-09-01\n\tbuild hash: 0123abcd\n", ""));

        /// <summary>A file that starts and fails, as a Blender missing a DLL does.</summary>
        public FakeHost Broken(string path) => Answer(path, new BlenderProbe(true, -1073741515, "", ""));

        /// <summary>Something called blender that is not Blender.</summary>
        public FakeHost Impostor(string path) => Answer(path, new BlenderProbe(true, 0, "Python 3.12.4\n", ""));

        public FakeHost Throwing(string path)
        {
            files.Add(path);
            throwing.Add(Key(path));
            return this;
        }

        public FakeHost Text(string path, string text)
        {
            Texts[Key(path)] = text;
            return this;
        }

        private FakeHost Answer(string path, BlenderProbe probe)
        {
            files.Add(path);
            answers[Key(path)] = probe;
            return this;
        }

        public string? Environment(string name) => Variables.GetValueOrDefault(name);
        public string? Folder(BlenderFolder folder) => Folders.GetValueOrDefault(folder);
        public bool FileExists(string path) => files.Any(file => Key(file) == Key(path));
        public string? ReadText(string path) => Texts.GetValueOrDefault(Key(path));
        public string? SteamPath() => Steam;

        public IReadOnlyList<string> DirectoryNames(string path)
        {
            var prefix = Key(path) + "/";
            return [.. files
                .Where(file => Key(file).StartsWith(prefix, StringComparison.Ordinal))
                .Select(file => file.Replace('\\', '/')[prefix.Length..].Split('/'))
                .Where(parts => parts.Length > 1)
                .Select(parts => parts[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        public Task<BlenderProbe> ProbeAsync(string executable, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Probed.Add(executable);
            if (throwing.Contains(Key(executable))) throw new IOException("The disk went away.");
            return Task.FromResult(answers.TryGetValue(Key(executable), out var answer)
                ? answer
                : new BlenderProbe(false, -1, "", "The system cannot find the file specified."));
        }

        /// <summary>Both separators and any case name the same file, as on Windows.</summary>
        private static string Key(string path) => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static FakeHost Windows() => new(BlenderPlatform.Windows)
    {
        Folders =
        {
            [BlenderFolder.ProgramFiles] = @"C:\Program Files",
            [BlenderFolder.ProgramFilesX86] = @"C:\Program Files (x86)",
            [BlenderFolder.LocalApplicationData] = @"C:\Profiles\artist\AppData\Local",
        },
    };

    private static IConfigurationRoot Settings(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private static Task<StoryboardStudio.Core.BlenderInstallSummary> Locate(
        FakeHost host, IConfiguration? configuration = null, TimeProvider? clock = null) =>
        new BlenderLocator(configuration ?? Settings(), host, clock ?? new ManualClock())
            .LocateAsync(CancellationToken.None);

    /// <summary>A Steam library list as current Steam clients write it.</summary>
    private const string LibraryFolders = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"6219485763018472316"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"512937411"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		""
        		"apps"
        		{
        		}
        	}
        	"2"
        	{
        		"path"		"E:\\Games\\Steam Library"
        		"label"		"Fast disk"
        		"apps"
        		{
        			"365670"		"987654321"
        		}
        	}
        }
        """;

    [Fact]
    public async Task TheSettingWinsOverEverythingElse()
    {
        var host = Windows()
            .Install(@"E:\Tools\Blender 4.2\blender.exe", "4.2.3 LTS")
            .Install(@"F:\Elsewhere\blender.exe", "4.5.0")
            .Install(@"C:\Apps\blender.exe", "4.4.0")
            .Install(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe", "5.2.2 LTS");
        host.Variables["BLENDER"] = @"F:\Elsewhere\blender.exe";
        host.Variables["PATH"] = @"C:\Apps";

        var found = await Locate(host, Settings((Setting, @"E:\Tools\Blender 4.2\blender.exe")));

        Assert.Equal(@"E:\Tools\Blender 4.2\blender.exe", found.Executable);
        Assert.Equal("4.2.3 LTS", found.Version);
        Assert.Equal("setting", found.Source);
        Assert.Contains("BlenderPath", found.FoundBy, StringComparison.Ordinal);
        Assert.Null(found.Problem);
        // Nothing else was even asked: the setting is an answer, not a hint.
        Assert.Equal([@"E:\Tools\Blender 4.2\blender.exe"], host.Probed);
    }

    [Fact]
    public async Task ASettingThatDoesNotAnswerIsStillTheOneUsedAndSaysWhy()
    {
        // Somebody chose this Blender. Handing the compiler a different one
        // because it looked broken would be a substitution nobody asked for.
        var host = Windows()
            .Broken(@"E:\Tools\blender.exe")
            .Install(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe");

        var found = await Locate(host, Settings((Setting, @"E:\Tools\blender.exe")));

        Assert.Equal(@"E:\Tools\blender.exe", found.Executable);
        Assert.Null(found.Version);
        Assert.Equal("setting", found.Source);
        Assert.Contains("did not answer --version", found.Problem, StringComparison.Ordinal);
        Assert.Contains(@"E:\Tools\blender.exe", found.Problem, StringComparison.Ordinal);
        Assert.Single(host.Probed);
    }

    [Fact]
    public async Task TheBlenderVariableIsReadBeforeRacBlender()
    {
        var host = Windows()
            .Install(@"D:\Blender\blender.exe", "5.0.1")
            .Install(@"D:\RacBlender\blender.exe", "4.2.3 LTS");
        host.Variables["BLENDER"] = @"""D:\Blender\blender.exe""";
        host.Variables["RAC_BLENDER"] = @"D:\RacBlender\blender.exe";

        var both = await Locate(host);
        Assert.Equal(@"D:\Blender\blender.exe", both.Executable);
        Assert.Equal("environment", both.Source);
        Assert.Contains("BLENDER environment variable", both.FoundBy, StringComparison.Ordinal);

        host.Variables.Remove("BLENDER");
        var compilers = await Locate(host);
        Assert.Equal(@"D:\RacBlender\blender.exe", compilers.Executable);
        Assert.Contains("RAC_BLENDER", compilers.FoundBy, StringComparison.Ordinal);
        Assert.Equal("4.2.3 LTS", compilers.Version);
    }

    [Fact]
    public async Task PathIsSearchedBeforeTheUsualInstallPlaces()
    {
        var host = Windows()
            .Install(@"C:\Portable\Blender\blender.exe", "4.5.1")
            .Install(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe");
        host.Variables["PATH"] = @"C:\Windows\System32;""C:\Portable\Blender"";C:\Tools";

        var found = await Locate(host);

        Assert.Equal(@"C:\Portable\Blender\blender.exe", found.Executable);
        Assert.Equal("path", found.Source);
        Assert.Equal("4.5.1", found.Version);
    }

    [Fact]
    public async Task TheNewestProgramFilesInstallIsChosen()
    {
        var host = Windows()
            .Install(@"C:\Program Files\Blender Foundation\Blender 3.6\blender.exe", "3.6.9 LTS")
            .Install(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe", "5.2.2 LTS")
            .Install(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe", "4.2.3 LTS");

        var found = await Locate(host);

        Assert.Equal(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe", found.Executable);
        Assert.Equal("program-files", found.Source);
        Assert.Equal("found in Program Files", found.FoundBy);
        // The newest answered, so nothing older was started.
        Assert.Single(host.Probed);
    }

    [Fact]
    public async Task VersionsAreComparedAsNumbersNotAsText()
    {
        var host = Windows()
            .Install(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe", "4.2.3 LTS")
            .Install(@"C:\Program Files\Blender Foundation\Blender 4.10\blender.exe", "4.10.0");

        var found = await Locate(host);

        Assert.Equal("4.10.0", found.Version);
    }

    [Fact]
    public async Task ABrokenNewestInstallIsSkippedForTheNextOne()
    {
        var host = Windows()
            .Broken(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe")
            .Install(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe", "4.2.3 LTS");

        var found = await Locate(host);

        Assert.Equal(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe", found.Executable);
        Assert.Equal(2, host.Probed.Count);
    }

    [Fact]
    public async Task EverySteamLibraryIsSearchedFromTheRegisteredSteamInstall()
    {
        // The registry spells it the way Steam does: forward slashes, lower case.
        var host = Windows()
            .Text(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", LibraryFolders)
            .Install(@"E:\Games\Steam Library\steamapps\common\Blender\blender.exe", "5.2.2 LTS");
        host.Steam = "c:/program files (x86)/steam";

        var found = await Locate(host);

        Assert.Equal(@"E:\Games\Steam Library\steamapps\common\Blender\blender.exe", found.Executable);
        Assert.Equal("steam", found.Source);
        Assert.Equal("found in your Steam library", found.FoundBy);
        Assert.Equal("5.2.2 LTS", found.Version);
    }

    [Fact]
    public async Task ABlenderInTheSteamInstallItselfIsNamedTheWaySteamListsIt()
    {
        // The registry says "c:/program files (x86)/steam"; the library list
        // says "C:\\Program Files (x86)\\Steam". The second is what a person
        // reads in a receipt, so it is the one handed to the compiler.
        var host = Windows()
            .Text(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", LibraryFolders)
            .Install(@"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe", "5.2.2 LTS");
        host.Steam = "c:/program files (x86)/steam";

        var found = await Locate(host);

        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe", found.Executable);
        Assert.Equal("steam", found.Source);
    }

    [Fact]
    public async Task WithoutARegistryEntrySteamIsLookedForWhereItInstallsByDefault()
    {
        var host = Windows()
            .Text(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", LibraryFolders)
            .Install(@"D:\SteamLibrary\steamapps\common\Blender\blender.exe", "4.2.3 LTS");

        var found = await Locate(host);

        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Blender\blender.exe", found.Executable);
        Assert.Equal("steam", found.Source);
    }

    [Fact]
    public async Task AnOlderSteamLibraryListIsReadToo()
    {
        var host = Windows()
            .Text(@"C:\Steam\config\libraryfolders.vdf", """
                "LibraryFolders"
                {
                	"TimeNextStatsReport"		"1700000000"
                	"ContentStatsID"		"-4917204371958431627"
                	"1"		"G:\\Older Library"
                }
                """)
            .Install(@"G:\Older Library\steamapps\common\Blender\blender.exe", "3.6.9 LTS");
        host.Steam = @"C:\Steam";

        var found = await Locate(host);

        Assert.Equal(@"G:\Older Library\steamapps\common\Blender\blender.exe", found.Executable);
    }

    [Fact]
    public void ALibraryListYieldsEveryLibraryAndNothingElse()
    {
        var libraries = BlenderLocator.LibraryPaths(LibraryFolders);

        // App ids and sizes are numbered entries too, and are not libraries.
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\Games\Steam Library"], libraries);
    }

    [Fact]
    public async Task SomethingCalledBlenderThatIsNotBlenderIsSkipped()
    {
        var host = Windows()
            .Impostor(@"C:\Scripts\blender.exe")
            .Text(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", LibraryFolders)
            .Install(@"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe", "5.2.2 LTS");
        host.Variables["PATH"] = @"C:\Scripts";

        var found = await Locate(host);

        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe", found.Executable);
        Assert.Contains(@"C:\Scripts\blender.exe", host.Probed);
    }

    [Fact]
    public async Task ACandidateThatThrowsIsSkippedRatherThanFatal()
    {
        var host = Windows()
            .Throwing(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe")
            .Install(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe", "4.2.3 LTS");

        var found = await Locate(host);

        Assert.Equal("4.2.3 LTS", found.Version);
    }

    [Fact]
    public async Task NothingFoundSaysSoAndHowToChooseOne()
    {
        var found = await Locate(Windows());

        Assert.Null(found.Executable);
        Assert.Null(found.Version);
        Assert.Equal("none", found.Source);
        Assert.Contains("No Blender was found", found.Problem, StringComparison.Ordinal);
        Assert.Contains("BlenderPath", found.Override, StringComparison.Ordinal);
        Assert.Contains("BLENDER", found.Override, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyBrokenBlendersFoundNamesThemRatherThanClaimingNone()
    {
        var found = await Locate(Windows().Broken(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"));

        Assert.Null(found.Executable);
        Assert.Contains("No working Blender", found.Problem, StringComparison.Ordinal);
        Assert.Contains(@"Blender 5.2\blender.exe", found.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAMacTheApplicationsFoldersAreSearched()
    {
        var host = new FakeHost(BlenderPlatform.MacOS) { Folders = { [BlenderFolder.Home] = "/Volumes/Work/artist" } }
            .Install("/Volumes/Work/artist/Applications/Blender.app/Contents/MacOS/Blender", "4.2.3 LTS");

        var found = await Locate(host);

        Assert.Equal("/Volumes/Work/artist/Applications/Blender.app/Contents/MacOS/Blender", found.Executable);
        Assert.Equal("applications", found.Source);
        // The system-wide one is looked at first, and simply is not there.
        Assert.DoesNotContain("/Applications/Blender.app/Contents/MacOS/Blender", host.Probed);
    }

    [Fact]
    public async Task OnLinuxSystemPlacesAndSteamAreSearched()
    {
        var host = new FakeHost(BlenderPlatform.Linux) { Folders = { [BlenderFolder.Home] = "/home/artist" } }
            .Broken("/snap/bin/blender")
            .Install("/home/artist/.local/share/Steam/steamapps/common/Blender/blender", "5.2.2 LTS");
        host.Variables["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/bin";

        var found = await Locate(host);

        Assert.Equal("/home/artist/.local/share/Steam/steamapps/common/Blender/blender", found.Executable);
        Assert.Equal("steam", found.Source);
        Assert.Equal(["/snap/bin/blender", "/home/artist/.local/share/Steam/steamapps/common/Blender/blender"],
            host.Probed);
    }

    [Fact]
    public async Task OnLinuxASystemBlenderIsFound()
    {
        var host = new FakeHost(BlenderPlatform.Linux) { Folders = { [BlenderFolder.Home] = "/home/artist" } }
            .Install("/usr/bin/blender", "4.2.3 LTS");

        var found = await Locate(host);

        Assert.Equal("/usr/bin/blender", found.Executable);
        Assert.Equal("system", found.Source);
    }

    [Fact]
    public async Task TheAnswerIsRememberedUntilTheSettingsChange()
    {
        var host = Windows()
            .Install(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe")
            .Install(@"E:\Chosen\blender.exe", "4.2.3 LTS");
        var settings = Settings();
        var locator = new BlenderLocator(settings, host, new ManualClock());

        var first = await locator.LocateAsync(CancellationToken.None);
        var again = await locator.LocateAsync(CancellationToken.None);
        Assert.Same(first, again);
        Assert.Single(host.Probed);

        settings[Setting] = @"E:\Chosen\blender.exe";
        var changed = await locator.LocateAsync(CancellationToken.None);
        Assert.Equal(@"E:\Chosen\blender.exe", changed.Executable);
        Assert.Equal(2, host.Probed.Count);
    }

    [Fact]
    public async Task ABlenderInstalledAfterAMissIsNoticedWithoutARestart()
    {
        var host = Windows();
        var clock = new ManualClock();
        var locator = new BlenderLocator(Settings(), host, clock);

        Assert.Null((await locator.LocateAsync(CancellationToken.None)).Executable);
        host.Install(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe");

        // Not on every request -- a miss is remembered briefly too ...
        Assert.Null((await locator.LocateAsync(CancellationToken.None)).Executable);
        Assert.Empty(host.Probed);

        // ... but not for long.
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal("5.2.2 LTS", (await locator.LocateAsync(CancellationToken.None)).Version);
    }

    [Fact]
    public async Task DiscoveryCanBeSwitchedOffLeavingOnlyTheSetting()
    {
        var host = Windows().Install(@"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe");
        host.Variables["BLENDER"] = @"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe";

        var found = await Locate(host, Settings((Discovery, "false")));

        Assert.Null(found.Executable);
        Assert.Equal("disabled", found.Source);
        Assert.Contains("DiscoverBlender", found.Problem, StringComparison.Ordinal);
        Assert.Empty(host.Probed);
    }
}
