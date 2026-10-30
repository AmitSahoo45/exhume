using System.Buffers.Binary;
using Exhume.Adapters.Windows;
using Exhume.Core;

namespace Exhume.Adapters.Windows.Tests;

public sealed class WindowsAdapterTests
{
    [Fact]
    public void Rot13_decodes_paths_without_changing_numbers_or_symbols() =>
        Assert.Equal(@"D:\Apps\Ghost42.exe", WindowsAdapter.DecodeRot13(@"Q:\Nccf\Tubfg42.rkr"));

    [Fact]
    public void UserAssist_layout_reads_count_and_last_run_and_rejects_short_or_invalid_values()
    {
        var date = new DateTimeOffset(2019, 3, 2, 4, 5, 6, TimeSpan.Zero);
        Assert.True(WindowsAdapter.TryReadUserAssist(UserAssistValue(17, date), out var count, out var lastRun));
        Assert.Equal(17U, count);
        Assert.Equal(date, lastRun);
        Assert.False(WindowsAdapter.TryReadUserAssist(new byte[20], out _, out _));
        var broken = UserAssistValue(2, date);
        BinaryPrimitives.WriteInt64LittleEndian(broken.AsSpan(60), long.MaxValue);
        Assert.True(WindowsAdapter.TryReadUserAssist(broken, out _, out var invalidTime));
        Assert.Null(invalidTime);
    }

    [Theory]
    [InlineData("{905e63b6-c1bf-494e-b29c-65b732d3d21a}", @"D:\Programs")]
    [InlineData("{7c5a40ef-a0fb-4bfc-874a-c0f2e0b9fa8e}", @"D:\Programs32")]
    [InlineData("{1ac14e77-02e7-4e5d-b744-2eb1ae5198b7}", @"D:\Windows\System32")]
    [InlineData("{d65231b0-b2f1-4857-a4ce-a8e7c6ea7d27}", @"D:\Windows\SysWOW64")]
    [InlineData("{f38bf404-1d43-42f2-9305-67de0b28fc23}", @"D:\Windows")]
    [InlineData("{9e3995ab-1f9c-4f13-b827-48b24b6c7174}", @"D:\Roaming\Pinned")]
    public void Known_folder_guids_resolve(string guid, string expected)
    {
        var path = Fixture().Adapter.ResolveKnownFolder(guid + @"\ghost.exe", out var resolved);
        Assert.True(resolved);
        Assert.Equal(expected + @"\ghost.exe", path);
    }

    [Fact]
    public async Task Unresolved_guid_is_retained_but_never_promoted_to_confirmed_dead()
    {
        var f = Fixture();
        const string raw = @"{00000000-0000-0000-0000-000000000001}\ghost.exe";
        Assert.Equal(raw, f.Adapter.ResolveKnownFolder(raw, out var resolved));
        Assert.False(resolved);
        f.Registry.Add(WindowsAdapter.UserAssist, subKeys: ["counter"]);
        f.Registry.Add(WindowsAdapter.UserAssist + @"\counter\Count", (WindowsAdapter.DecodeRot13(raw), UserAssistValue(5, Past)));
        var progress = new RecordingProgress();
        Assert.Empty(await Collect(f.Adapter, progress: progress));
        var warning = Assert.Single(progress.Values, value => value.Warning is not null);
        Assert.Contains("unresolved", warning.Stage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(raw, warning.Warning!);
        Assert.DoesNotContain(progress.Values, value => value.Error is not null);
    }

    [Fact]
    public async Task Merges_registry_sources_and_folder_with_friendly_name_and_UserAssist_last_run()
    {
        var f = Fixture();
        const string executable = @"D:\Gone\ghost.exe";
        f.Registry.Add(WindowsAdapter.UserAssist, subKeys: ["counter"]);
        f.Registry.Add(WindowsAdapter.UserAssist + @"\counter\Count", (WindowsAdapter.DecodeRot13(executable), UserAssistValue(8, Past)));
        f.Registry.Add(WindowsAdapter.AppCompat, (executable, new byte[4]));
        f.Registry.Add(WindowsAdapter.MuiCache, (executable + ".FriendlyAppName", "Friendly Ghost"),
            (executable + ".ApplicationCompany", "Example Publisher"));
        f.Probe.FolderChildren[Folders.LocalData] = [@"D:\Local\ghost"];
        f.Probe.PathTimes[@"D:\Local\ghost"] = new(Past.AddYears(-1), Past.AddYears(3));
        var artifact = Assert.Single(await Collect(f.Adapter));
        Assert.Equal("Friendly Ghost", artifact.Name);
        Assert.Equal(Past.AddYears(-1), artifact.FirstSeen);
        Assert.Equal(Past, artifact.LastSeen);
        Assert.Equal("8", artifact.Metadata["runCount"]);
        Assert.Equal(executable, artifact.Metadata["exePath"]);
        Assert.Equal("high", artifact.Metadata["confidence"]);
        Assert.Contains("UserAssist", artifact.Metadata["sources"]);
        Assert.Contains("AppCompat", artifact.Metadata["sources"]);
        Assert.Contains("MuiCache", artifact.Metadata["sources"]);
        Assert.Contains("LeftoverFolder", artifact.Metadata["sources"]);
        Assert.Contains(WindowsAdapter.AppCompat, artifact.Evidence);
    }

    [Theory]
    [InlineData(WindowsAdapter.UninstallMachine)]
    [InlineData(WindowsAdapter.UninstallMachine32)]
    [InlineData(WindowsAdapter.UninstallUser)]
    public async Task Every_uninstall_hive_suppresses_existing_installation_paths(string hive)
    {
        var f = Fixture();
        f.Registry.Add(hive, subKeys: ["live"]);
        f.Registry.Add(hive + @"\live", ("DisplayName", "Live Tool"), ("InstallLocation", @"D:\Apps\Live"));
        f.Registry.Add(WindowsAdapter.AppCompat, (@"D:\Apps\Live\missing.exe", new byte[0]),
            (@"D:\Apps\Lively\ghost.exe", new byte[0]), (@"D:\Elsewhere\live.exe", new byte[0]));
        f.Probe.ExistingFiles.Add(@"D:\Elsewhere\live.exe");
        var artifact = Assert.Single(await Collect(f.Adapter));
        Assert.Equal(@"D:\Apps\Lively\ghost.exe", artifact.Metadata["exePath"]);
    }

    [Fact]
    public async Task Incomplete_installed_inventory_does_not_claim_missing_executable_is_dead()
    {
        var f = Fixture();
        f.Registry.Denied.Add(WindowsAdapter.UninstallMachine);
        f.Registry.Add(WindowsAdapter.AppCompat, (@"D:\Apps\Live\missing-helper.exe", new byte[0]));
        f.Probe.FolderChildren[Folders.LocalData] = [@"D:\Local\Live"];
        var progress = new RecordingProgress();
        Assert.Empty(await Collect(f.Adapter, progress: progress));
        Assert.Contains(progress.Values, p => p.Error is not null);
        Assert.Equal("complete", progress.Values[^1].Stage);
    }

    [Fact]
    public async Task Expanded_install_location_keeps_missing_helpers_in_the_living_installation()
    {
        var f = Fixture();
        var variable = "EXHUME_TEST_INSTALL_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, @"D:\Apps");
        try
        {
            f.Registry.Add(WindowsAdapter.UninstallUser, subKeys: ["live"]);
            f.Registry.Add(WindowsAdapter.UninstallUser + @"\live", ("DisplayName", "Live Tool"),
                ("InstallLocation", "%" + variable + @"%\Live"));
            f.Registry.Add(WindowsAdapter.AppCompat, (@"D:\Apps\Live\missing-helper.exe", new byte[0]));
            Assert.Empty(await Collect(f.Adapter));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task Leftover_allowlist_and_installed_publisher_name_location_avoid_false_positives()
    {
        var f = Fixture();
        f.Registry.Add(WindowsAdapter.UninstallUser, subKeys: ["live"]);
        f.Registry.Add(WindowsAdapter.UninstallUser + @"\live", ("DisplayName", "Live Tool"),
            ("Publisher", "Example Publisher"), ("InstallLocation", @"D:\Apps\InstalledFolder"));
        f.Probe.FolderChildren[Folders.LocalData] = [@"D:\Local\Microsoft", @"D:\Local\Packages", @"D:\Local\Temp",
            @"D:\Local\Live Tool", @"D:\Local\Example Publisher", @"D:\Local\InstalledFolder", @"D:\Local\forgotten"];
        var artifact = Assert.Single(await Collect(f.Adapter));
        Assert.Equal("forgotten", artifact.Name);
        Assert.Equal("low", artifact.Metadata["confidence"]);
    }

    [Fact]
    public async Task Installed_siblings_are_derived_from_MuiCache_publisher()
    {
        var f = Fixture();
        f.Registry.Add(WindowsAdapter.UninstallUser, subKeys: ["live"]);
        f.Registry.Add(WindowsAdapter.UninstallUser + @"\live", ("DisplayName", "Sibling"), ("Publisher", "Example Publisher"));
        f.Registry.Add(WindowsAdapter.MuiCache, (@"D:\Gone\old.exe.FriendlyAppName", "Old"),
            (@"D:\Gone\old.exe.ApplicationCompany", "Example Publisher"));
        Assert.Equal("Sibling", Assert.Single(await Collect(f.Adapter)).Metadata["installedSiblings"]);
    }

    [Fact]
    public async Task Prefetch_is_never_read_without_opt_in_and_uses_only_filename_dates()
    {
        var f = Fixture();
        var prefetch = Path.Combine(Folders.Windows, "Prefetch");
        f.Probe.FileChildren[prefetch] = [prefetch + @"\GHOST.EXE-12AB34CD.pf"];
        f.Probe.PathTimes[prefetch + @"\GHOST.EXE-12AB34CD.pf"] = new(Past.AddDays(-4), Past);
        Assert.Empty(await Collect(f.Adapter));
        Assert.Equal(0, f.Probe.FileEnumerations);
        var artifact = Assert.Single(await Collect(f.Adapter, new() { Elevated = true }));
        Assert.Equal("GHOST", artifact.Name);
        Assert.Equal("Prefetch", artifact.Metadata["sources"]);
        Assert.Equal("low", artifact.Metadata["confidence"]);
        Assert.Equal(Past, artifact.LastSeen);
    }

    [Fact]
    public async Task Denied_Prefetch_reports_NeedsAdmin_without_losing_standard_sources()
    {
        var f = Fixture();
        f.Probe.Denied.Add(Path.Combine(Folders.Windows, "Prefetch"));
        f.Registry.Add(WindowsAdapter.AppCompat, (@"D:\Gone\ghost.exe", new byte[0]));
        Assert.Equal(SourceAvailability.NeedsAdmin, f.Adapter.CheckPrefetch(new() { Elevated = true }).Availability);
        Assert.Equal(SourceAvailability.Available, f.Adapter.Check(new() { Elevated = true }).Availability);
        var progress = new RecordingProgress();
        Assert.Single(await Collect(f.Adapter, new() { Elevated = true }, progress));
        Assert.Contains(progress.Values, p => p.Warning is not null && p.Stage.Contains("Prefetch", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Values, p => p.Error is not null);
    }

    [Fact]
    public async Task Prefetch_cannot_override_a_known_living_portable_executable()
    {
        var f = Fixture();
        const string living = @"D:\Portable\live.exe";
        f.Registry.Add(WindowsAdapter.AppCompat, (living, new byte[0]));
        f.Probe.ExistingFiles.Add(living);
        var prefetch = Path.Combine(Folders.Windows, "Prefetch");
        f.Probe.FileChildren[prefetch] = [prefetch + @"\LIVE.EXE-12AB34CD.pf"];
        Assert.Empty(await Collect(f.Adapter, new() { Elevated = true }));
    }

    [Theory]
    [InlineData(@"D:\Windows\System32", "SVCHOST.EXE")]
    [InlineData(@"D:\Windows\SysWOW64", "DLLHOST.EXE")]
    [InlineData(@"D:\Windows", "EXPLORER.EXE")]
    [InlineData(@"D:\Apps\Installed", "HELPER.EXE")]
    public async Task Prefetch_probes_system_and_known_installation_directories_before_classifying(string directory, string executable)
    {
        var f = Fixture();
        f.Registry.Add(WindowsAdapter.UninstallUser, subKeys: ["live"]);
        f.Registry.Add(WindowsAdapter.UninstallUser + @"\live", ("DisplayName", "Different product name"),
            ("InstallLocation", @"D:\Apps\Installed"));
        f.Probe.ExistingFiles.Add(Path.Combine(directory, executable));
        var prefetch = Path.Combine(Folders.Windows, "Prefetch");
        f.Probe.FileChildren[prefetch] = [Path.Combine(prefetch, executable + "-12AB34CD.pf")];
        Assert.Empty(await Collect(f.Adapter, new() { Elevated = true }));
    }

    [Fact]
    public async Task Unreadable_Prefetch_living_probe_is_a_warning_and_never_a_false_death()
    {
        var f = Fixture();
        var prefetch = Path.Combine(Folders.Windows, "Prefetch");
        f.Probe.FileChildren[prefetch] = [Path.Combine(prefetch, "UNCERTAIN.EXE-12AB34CD.pf")];
        f.Probe.Denied.Add(Path.Combine(Folders.System32, "UNCERTAIN.EXE"));
        f.Registry.Add(WindowsAdapter.AppCompat, (@"D:\Gone\kept.exe", new byte[0]));
        var progress = new RecordingProgress();
        Assert.Equal("kept", Assert.Single(await Collect(f.Adapter, new() { Elevated = true }, progress)).Name);
        Assert.Contains(progress.Values, value => value.Warning is not null);
        Assert.DoesNotContain(progress.Values, value => value.Error is not null);
    }

    [Fact]
    public async Task Bad_registry_item_or_disk_probe_error_is_skipped_and_cancellation_propagates()
    {
        var f = Fixture();
        f.Registry.Add(WindowsAdapter.AppCompat, (@"D:\private\denied.exe", new byte[0]), (@"D:\Gone\kept.exe", new byte[0]));
        f.Probe.Denied.Add(@"D:\private\denied.exe");
        var progress = new RecordingProgress();
        Assert.Single(await Collect(f.Adapter, progress: progress));
        Assert.Contains(progress.Values, p => p.Warning is not null && !p.Warning.Contains("private", StringComparison.Ordinal));
        Assert.DoesNotContain(progress.Values, p => p.Error is not null);
        Assert.Equal("complete", progress.Values[^1].Stage);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in f.Adapter.ScanAsync(new(), cancellationToken: cancelled.Token)) { }
        });
    }

    private static readonly DateTimeOffset Past = new(2019, 6, 7, 8, 9, 10, TimeSpan.Zero);
    private static readonly WindowsFolders Folders = new(@"D:\Programs", @"D:\Programs32", @"D:\Windows",
        @"D:\Windows\System32", @"D:\Windows\SysWOW64", @"D:\Roaming\Pinned\TaskBar", @"D:\Roaming", @"D:\Local", @"D:\ProgramData");
    private static (WindowsAdapter Adapter, FakeRegistry Registry, FakeProbe Probe) Fixture()
    {
        var registry = new FakeRegistry();
        var probe = new FakeProbe();
        return (new WindowsAdapter(registry, probe, Folders), registry, probe);
    }
    private static byte[] UserAssistValue(uint count, DateTimeOffset date)
    {
        var bytes = new byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), count);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(60), date.ToFileTime());
        return bytes;
    }
    private static async Task<List<Artifact>> Collect(WindowsAdapter adapter, ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null)
    {
        var artifacts = new List<Artifact>();
        await foreach (var artifact in adapter.ScanAsync(options ?? new(), progress)) artifacts.Add(artifact);
        return artifacts;
    }
    private sealed class FakeRegistry : IRegistryReader
    {
        private readonly Dictionary<string, FakeKey> keys = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IRegistryKey? OpenKey(string path)
        {
            if (Denied.Contains(path)) throw new UnauthorizedAccessException("private key");
            return keys.GetValueOrDefault(path);
        }
        public void Add(string path, params (string Name, object Value)[] values) => keys[path] = new([], values);
        public void Add(string path, string[] subKeys) => keys[path] = new(subKeys, []);
    }
    private sealed class FakeKey(string[] children, (string Name, object Value)[] values) : IRegistryKey
    {
        public IEnumerable<string> SubKeyNames => children;
        public IEnumerable<string> ValueNames => values.Select(v => v.Name);
        public object? ReadValue(string name) => values.FirstOrDefault(v => v.Name == name).Value;
        public void Dispose() { }
    }
    private sealed class FakeProbe : IWindowsDiskProbe
    {
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> FolderChildren { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> FileChildren { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, EvidenceTimes> PathTimes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int FileEnumerations { get; private set; }
        public bool FileExists(string path) { Guard(path); return ExistingFiles.Contains(path); }
        public bool DirectoryExists(string path) { Guard(path); return FolderChildren.ContainsKey(path) || FileChildren.ContainsKey(path); }
        public IEnumerable<string> Directories(string path) { Guard(path); return FolderChildren.GetValueOrDefault(path, []); }
        public IEnumerable<string> Files(string path, string pattern) { FileEnumerations++; Guard(path); return FileChildren.GetValueOrDefault(path, []); }
        public EvidenceTimes Times(string path) { Guard(path); return PathTimes.GetValueOrDefault(path, new(Past, Past)); }
        private void Guard(string path) { if (Denied.Contains(path)) throw new UnauthorizedAccessException("private path"); }
    }
    private sealed class RecordingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Values { get; } = [];
        public void Report(ScanProgress value) => Values.Add(value);
    }
}
