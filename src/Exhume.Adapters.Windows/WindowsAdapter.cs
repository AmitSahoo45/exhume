using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using Exhume.Core;

namespace Exhume.Adapters.Windows;

public interface IRegistryReader
{
    IRegistryKey? OpenKey(string path);
}

public interface IRegistryKey : IDisposable
{
    IEnumerable<string> SubKeyNames { get; }
    IEnumerable<string> ValueNames { get; }
    object? ReadValue(string name);
}

public sealed record EvidenceTimes(DateTimeOffset? Created, DateTimeOffset? Modified);

public interface IWindowsDiskProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IEnumerable<string> Directories(string path);
    IEnumerable<string> Files(string path, string pattern);
    EvidenceTimes Times(string path);
}

public sealed record WindowsFolders(string ProgramFiles, string ProgramFilesX86, string Windows,
    string System32, string SysWow64, string PinnedTaskbar, string RoamingData, string LocalData,
    string ProgramData);

public sealed class WindowsAdapter : IEvidenceAdapter
{
    public const string UninstallMachine = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallMachine32 = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallUser = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UserAssist = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    public const string AppCompat = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store";
    public const string MuiCache = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Microsoft.NET", "Windows", "WindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Packages", "Package Cache", "PackageManagement", "Temp", "Temporary Internet Files", "Application Data",
        "ConnectedDevicesPlatform", "Comms", "D3DSCache", "Diagnostics", "CrashDumps", "History", "INetCache", "INetCookies",
        "Local", "LocalLow", "Roaming", "Start Menu", "Templates", "USOPrivate", "USOShared", "regid.1991-06.com.microsoft",
        "VirtualStore", "Fonts", "Programs", "Publishers", "SquirrelTemp"
    };
    private readonly IRegistryReader reader;
    private readonly IWindowsDiskProbe probe;
    private readonly WindowsFolders folders;

    public WindowsAdapter() : this(new WindowsRegistryReader(), new WindowsDiskProbe(), WindowsEnvironment.Folders()) { }
    public WindowsAdapter(IRegistryReader reader, IWindowsDiskProbe probe, WindowsFolders folders)
    {
        this.reader = reader;
        this.probe = probe;
        this.folders = folders;
    }

    public string Name => "windows";

    public SourceStatus Check(ScanOptions options)
    {
        var available = false;
        foreach (var path in new[] { UserAssist, AppCompat, MuiCache })
        {
            try { using var key = reader.OpenKey(path); available |= key is not null; }
            catch (Exception error) when (IsReadError(error)) { }
        }
        foreach (var path in DataRoots())
        {
            try { available |= probe.DirectoryExists(path); }
            catch (Exception error) when (IsReadError(error)) { }
        }
        var prefetch = options.Elevated ? CheckPrefetch(options) : null;
        if (available) return new(SourceAvailability.Available, prefetch?.Availability == SourceAvailability.NeedsAdmin
            ? "Standard Windows evidence is available; Prefetch needs administrator access and will be skipped."
            : "Standard Windows evidence is available.");
        return prefetch ?? new(SourceAvailability.Unavailable, "No readable Windows evidence source was found.");
    }

    public SourceStatus CheckPrefetch(ScanOptions options)
    {
        if (!options.Elevated) return new(SourceAvailability.Unavailable, "Prefetch is disabled; use the explicit elevated-source option to attempt it.");
        try
        {
            var path = Path.Combine(folders.Windows, "Prefetch");
            if (!probe.DirectoryExists(path)) return new(SourceAvailability.Unavailable, "The Prefetch directory is unavailable.");
            using var entries = probe.Files(path, "*.pf").GetEnumerator();
            _ = entries.MoveNext();
            return new(SourceAvailability.Available, "Prefetch is readable; only filenames and timestamps are read.");
        }
        catch (Exception error) when (IsReadError(error))
        {
            return new(SourceAvailability.NeedsAdmin, "Prefetch needs administrator access; standard sources remain available.");
        }
    }

    public async IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var inventory = ReadInstalled(progress, cancellationToken);
        if (!inventory.Complete)
        {
            progress?.Report(new(Name, "residue classification skipped", 0,
                Error: "The installed-program inventory was incomplete; no programs were classified as dead."));
            progress?.Report(new(Name, "complete", 0));
            yield break;
        }
        var installed = inventory.Programs;
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var livingKeys = new HashSet<string>(StringComparer.Ordinal);
        long examined = 0;

        foreach (var child in SubKeys(UserAssist, progress))
        {
            var key = UserAssist + "\\" + child + "\\Count";
            foreach (var value in Values(key, progress))
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                if (value.Value is not byte[] bytes || !TryReadUserAssist(bytes, out var count, out var lastRun)) continue;
                var path = DecodeRot13(value.Name);
                if (path.StartsWith("UEME_RUNPATH:", StringComparison.OrdinalIgnoreCase)) path = path[13..];
                AddExecutable(path, "UserAssist", key + "\\" + value.Name, null, null, count, lastRun);
            }
        }
        progress?.Report(new(Name, "UserAssist", examined));
        foreach (var value in Values(AppCompat, progress))
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            AddExecutable(value.Name, "AppCompat", AppCompat + "\\" + value.Name, null, null, 0, null);
        }
        progress?.Report(new(Name, "AppCompat", examined));

        var muiValues = Values(MuiCache, progress);
        var publishers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in muiValues)
        {
            if (value.Name.EndsWith(".ApplicationCompany", StringComparison.OrdinalIgnoreCase) && value.Value is string publisher)
                publishers[value.Name[..^19]] = publisher;
        }
        foreach (var value in muiValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            examined++;
            if (value.Value is not string friendly || value.Name.EndsWith(".ApplicationCompany", StringComparison.OrdinalIgnoreCase)) continue;
            var path = value.Name.EndsWith(".FriendlyAppName", StringComparison.OrdinalIgnoreCase) ? value.Name[..^16] : value.Name;
            AddExecutable(path, "MuiCache", MuiCache + "\\" + value.Name, friendly, publishers.GetValueOrDefault(path), 0, null);
        }
        progress?.Report(new(Name, "MuiCache", examined));

        foreach (var root in DataRoots())
        {
            foreach (var path in ReadDirectories(root, progress))
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (SystemFolders.Contains(folderName) || installed.Any(app => MatchesFolder(app, folderName))) continue;
                try
                {
                    var times = probe.Times(path);
                    var candidate = GetCandidate(folderName.ToLowerInvariant(), folderName);
                    if (candidate.NameRank < 2) { candidate.Name = folderName; candidate.NameRank = 2; }
                    candidate.AddEvidence("LeftoverFolder", path, times.Created, times.Modified);
                }
                catch (Exception error) when (IsReadError(error)) { ReportWarning(progress, "leftover folder skipped"); }
            }
        }
        progress?.Report(new(Name, "LeftoverFolder", examined));

        if (options.Elevated)
        {
            var status = CheckPrefetch(options);
            if (status.Availability != SourceAvailability.Available)
                progress?.Report(new(Name, "Prefetch skipped", examined, Warning: status.Reason));
            else
            {
                var executableDirectories = new[] { folders.System32, folders.SysWow64, folders.Windows }
                    .Concat(installed.Select(app => app.Location))
                    .Where(directory => !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var path in ReadPrefetch(progress))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    examined++;
                    var filename = Path.GetFileNameWithoutExtension(path);
                    var separator = filename.LastIndexOf('-');
                    if (separator < 1) continue;
                    var executable = filename[..separator];
                    if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
                    if (livingKeys.Contains(key) || installed.Any(app => MatchesProgramName(app, key))) continue;
                    try
                    {
                        if (executableDirectories.Any(directory =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return probe.FileExists(Path.Combine(directory, executable));
                        }))
                        {
                            livingKeys.Add(key);
                            continue;
                        }
                        var times = probe.Times(path);
                        var candidate = GetCandidate(key, Path.GetFileNameWithoutExtension(executable));
                        candidate.AddEvidence("Prefetch", path, times.Created, times.Modified);
                    }
                    catch (Exception error) when (IsReadError(error)) { ReportWarning(progress, "Prefetch item skipped"); }
                }
            }
        }

        var emitted = 0;
        foreach (var candidate in candidates.Values.Where(c => !livingKeys.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var siblings = candidate.Publisher is null ? [] : installed.Where(app =>
                string.Equals(app.Publisher, candidate.Publisher, StringComparison.OrdinalIgnoreCase))
                .Select(app => app.Name).Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
            yield return new(Name, "DeadProgram", candidate.Key, candidate.Name, candidate.First,
                candidate.UserAssistLast ?? candidate.Last, string.Join(" | ", candidate.Evidence), new Dictionary<string, string>
                {
                    ["exePath"] = candidate.Executable ?? "",
                    ["runCount"] = candidate.RunCount.ToString(CultureInfo.InvariantCulture),
                    ["sources"] = string.Join(", ", candidate.Sources),
                    ["confidence"] = candidate.Executable is not null ? "high" : "low",
                    ["installedSiblings"] = string.Join(" | ", siblings)
                });
            emitted++;
        }
        progress?.Report(new(Name, "complete", emitted));

        Candidate GetCandidate(string key, string displayName)
        {
            if (!candidates.TryGetValue(key, out var candidate)) candidates[key] = candidate = new(key, displayName);
            return candidate;
        }

        void AddExecutable(string rawPath, string source, string evidence, string? friendly, string? publisher,
            uint runCount, DateTimeOffset? lastRun)
        {
            var path = ResolveKnownFolder(rawPath.Trim().Trim('"'), out var resolved);
            if (!resolved)
            {
                progress?.Report(new(Name, source + " unresolved path skipped", examined,
                    Warning: "An executable location was unresolved and could not be classified as dead."));
                return;
            }
            if (!IsExecutablePath(path)) return;
            var key = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            try
            {
                if (probe.FileExists(path) || installed.Any(app => ContainsPath(app.Location, path)))
                {
                    livingKeys.Add(key);
                    return;
                }
            }
            catch (Exception error) when (IsReadError(error)) { ReportWarning(progress, source + " item skipped"); return; }
            var candidate = GetCandidate(key, Path.GetFileNameWithoutExtension(path));
            candidate.Executable ??= path;
            candidate.Publisher ??= publisher;
            if (!string.IsNullOrWhiteSpace(friendly)) { candidate.Name = Limit(friendly, 160); candidate.NameRank = 3; }
            candidate.RunCount = Math.Max(candidate.RunCount, runCount);
            if (source == "UserAssist" && lastRun is not null && (candidate.UserAssistLast is null || lastRun > candidate.UserAssistLast))
                candidate.UserAssistLast = lastRun;
            candidate.AddEvidence(source, evidence, lastRun, lastRun);
        }
    }

    public static string DecodeRot13(string text) => string.Create(text.Length, text, static (output, input) =>
    {
        for (var index = 0; index < input.Length; index++)
        {
            var value = input[index];
            output[index] = value is >= 'a' and <= 'z' ? (char)('a' + (value - 'a' + 13) % 26)
                : value is >= 'A' and <= 'Z' ? (char)('A' + (value - 'A' + 13) % 26) : value;
        }
    });

    public static bool TryReadUserAssist(byte[] bytes, out uint count, out DateTimeOffset? lastRun)
    {
        count = 0;
        lastRun = null;
        if (bytes.Length < 72) return false;
        count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var timestamp = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(60));
        if (timestamp <= 0) return true;
        try { lastRun = DateTimeOffset.FromFileTime(timestamp).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { }
        return true;
    }

    public string ResolveKnownFolder(string path, out bool resolved)
    {
        resolved = true;
        if (!path.StartsWith('{')) return path;
        var end = path.IndexOf('}');
        if (end < 0) { resolved = false; return path; }
        var prefix = path[..(end + 1)].ToUpperInvariant() switch
        {
            "{905E63B6-C1BF-494E-B29C-65B732D3D21A}" => folders.ProgramFiles,
            "{6D809377-6AF0-444B-8957-A3773F02200E}" => folders.ProgramFiles,
            "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}" => folders.ProgramFilesX86,
            "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}" => folders.System32,
            "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}" => folders.SysWow64,
            "{F38BF404-1D43-42F2-9305-67DE0B28FC23}" => folders.Windows,
            "{9E3995AB-1F9C-4F13-B827-48B24B6C7174}" => Path.GetDirectoryName(folders.PinnedTaskbar),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(prefix)) { resolved = false; return path; }
        return Path.Combine(prefix, path[(end + 1)..].TrimStart('\\', '/'));
    }

    private (List<InstalledProgram> Programs, bool Complete) ReadInstalled(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var installed = new List<InstalledProgram>();
        var complete = true;
        foreach (var hive in new[] { UninstallMachine, UninstallMachine32, UninstallUser })
        {
            foreach (var child in SubKeys(hive, progress, () => complete = false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = Values(hive + "\\" + child, progress, () => complete = false)
                    .ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
                installed.Add(new(Text("DisplayName"), Text("Publisher"),
                    Environment.ExpandEnvironmentVariables(Text("InstallLocation")).Trim().Trim('"'),
                    Text("InstallDate"), Environment.ExpandEnvironmentVariables(Text("DisplayIcon"))));
                string Text(string name) => values.GetValueOrDefault(name) as string ?? "";
            }
        }
        return (installed, complete);
    }

    private string[] SubKeys(string path, IProgress<ScanProgress>? progress, Action? onError = null)
    {
        try { using var key = reader.OpenKey(path); return key?.SubKeyNames.ToArray() ?? []; }
        catch (Exception error) when (IsReadError(error))
        {
            onError?.Invoke();
            ReportWarning(progress, "registry key skipped");
            return [];
        }
    }

    private List<(string Name, object? Value)> Values(string path, IProgress<ScanProgress>? progress, Action? onError = null)
    {
        var result = new List<(string Name, object? Value)>();
        try
        {
            using var key = reader.OpenKey(path);
            if (key is null) return result;
            foreach (var name in key.ValueNames)
            {
                try { result.Add((name, key.ReadValue(name))); }
                catch (Exception error) when (IsReadError(error)) { onError?.Invoke(); ReportWarning(progress, "registry value skipped"); }
            }
        }
        catch (Exception error) when (IsReadError(error)) { onError?.Invoke(); ReportWarning(progress, "registry key skipped"); }
        return result;
    }

    private string[] ReadDirectories(string path, IProgress<ScanProgress>? progress)
    {
        try { return probe.Directories(path).ToArray(); }
        catch (Exception error) when (IsReadError(error)) { ReportWarning(progress, "application data folder skipped"); return []; }
    }

    private string[] ReadPrefetch(IProgress<ScanProgress>? progress)
    {
        try { return probe.Files(Path.Combine(folders.Windows, "Prefetch"), "*.pf").ToArray(); }
        catch (Exception error) when (IsReadError(error)) { ReportWarning(progress, "Prefetch skipped"); return []; }
    }

    private IEnumerable<string> DataRoots() => new[] { folders.RoamingData, folders.LocalData, folders.ProgramData }
        .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool ContainsPath(string location, string path)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
            var fullPath = Path.GetFullPath(path);
            return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (IsReadError(error)) { return false; }
    }

    private static bool MatchesFolder(InstalledProgram app, string folder)
    {
        if (SameProduct(app.Name, folder) || SameProduct(app.Publisher, folder)) return true;
        return app.Location.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment => SameProduct(segment, folder));
    }

    private static bool MatchesProgramName(InstalledProgram app, string name)
    {
        var icon = app.Icon.Trim();
        var comma = icon.LastIndexOf(',');
        if (comma >= 0) icon = icon[..comma];
        icon = icon.Trim('"');
        return SameProduct(app.Name, name) || Path.GetFileNameWithoutExtension(icon).Equals(name, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(Path.TrimEndingDirectorySeparator(app.Location)).Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameProduct(string installed, string candidate) => installed.Equals(candidate, StringComparison.OrdinalIgnoreCase)
        || installed.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase)
        || installed.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutablePath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        return Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".com" or ".bat" or ".cmd";
    }

    private static bool IsReadError(Exception error) => error is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException;
    private void ReportWarning(IProgress<ScanProgress>? progress, string stage) =>
        progress?.Report(new(Name, stage, 0, Warning: "A Windows evidence item could not be read and was skipped."));
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
    private sealed record InstalledProgram(string Name, string Publisher, string Location, string InstallDate, string Icon);

    private sealed class Candidate(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; set; } = name;
        public int NameRank { get; set; } = 1;
        public string? Executable { get; set; }
        public string? Publisher { get; set; }
        public uint RunCount { get; set; }
        public DateTimeOffset? First { get; set; }
        public DateTimeOffset? Last { get; set; }
        public DateTimeOffset? UserAssistLast { get; set; }
        public SortedSet<string> Evidence { get; } = new(StringComparer.Ordinal);
        public SortedSet<string> Sources { get; } = new(StringComparer.Ordinal);
        public void AddEvidence(string source, string locator, DateTimeOffset? first, DateTimeOffset? last)
        {
            Evidence.Add(locator);
            Sources.Add(source);
            if (first is not null && (First is null || first < First)) First = first;
            if (last is not null && (Last is null || last > Last)) Last = last;
        }
    }
}
