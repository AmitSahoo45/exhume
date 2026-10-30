using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Exhume.Core;
using Microsoft.Data.Sqlite;

namespace Exhume.Adapters.Browser;

public sealed class BrowserAdapter(string? localApplicationData = null) : IEvidenceAdapter
{
    private static readonly DateTimeOffset WebKitEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions ReadableJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string localApplicationData = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string Name => "browser";

    public SourceStatus Check(ScanOptions options)
    {
        var profiles = Discover(options.Profile);
        return profiles.Count == 0
            ? new(SourceAvailability.Unavailable, "No matching browser history profiles were found.")
            : new(SourceAvailability.Available, "Found " + string.Join(", ", profiles.Select(x => x.Browser + ":" + x.Name)) + ".");
    }

    public async IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options,
        IProgress<ScanProgress>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryRoot = DataDirectoryPolicy.Normalize(options.TemporaryDirectory ?? Path.Combine(".exhume", "temp"));
        var profiles = Discover(options.Profile);
        var discoveredProfileCount = profiles.Count;
        long emitted = 0;
        var readableProfiles = 0;
        var unsafeTemporaryRoot = false;
        foreach (var profile in Discover(null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sourceDirectory = EvidencePathResolver.Normalize(Path.GetDirectoryName(profile.History)!);
                var prefix = Path.EndsInDirectorySeparator(sourceDirectory) ? sourceDirectory : sourceDirectory + Path.DirectorySeparatorChar;
                if (temporaryRoot.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase) ||
                    temporaryRoot.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) unsafeTemporaryRoot = true;
            }
            catch (Exception error) when (IsSourceError(error))
            {
                if (profiles.Remove(profile))
                    progress?.Report(new(Name, "profile skipped", emitted, Warning: "A browser profile location could not be resolved safely."));
            }
        }
        if (unsafeTemporaryRoot)
        {
            progress?.Report(new(Name, "complete", 0, 0, Error: "Temporary browser snapshots cannot be placed inside a source profile."));
            yield break;
        }
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<Artifact> artifacts;
            try
            {
                artifacts = await ReadProfileAsync(profile, temporaryRoot, cancellationToken).ConfigureAwait(false);
                readableProfiles++;
            }
            catch (Exception error) when (IsSourceError(error))
            {
                progress?.Report(new(Name, "profile skipped", emitted, Warning: "A browser history profile could not be read safely."));
                continue;
            }
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return artifact;
                progress?.Report(new(Name, "aggregating", ++emitted));
            }
        }
        progress?.Report(new(Name, "complete", emitted, emitted,
            Error: discoveredProfileCount > 0 && readableProfiles == 0 ? "No discovered browser history profile could be read safely." : null));
    }

    private List<Profile> Discover(string? filter)
    {
        var result = new List<Profile>();
        (string Browser, string Relative)[] browsers =
        [
            ("chrome", Path.Combine("Google", "Chrome", "User Data")),
            ("edge", Path.Combine("Microsoft", "Edge", "User Data")),
            ("brave", Path.Combine("BraveSoftware", "Brave-Browser", "User Data"))
        ];
        foreach (var (browser, relative) in browsers)
        {
            try
            {
                var directory = Path.Combine(localApplicationData, relative);
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(path);
                    if (name != "Default" && !(name.StartsWith("Profile ", StringComparison.Ordinal) &&
                        int.TryParse(name.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out _))) continue;
                    if (!string.IsNullOrWhiteSpace(filter) &&
                        !string.Equals(filter, name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(filter, browser + ":" + name, StringComparison.OrdinalIgnoreCase)) continue;
                    var history = Path.Combine(path, "History");
                    if (File.Exists(history)) result.Add(new(browser, name, history));
                }
            }
            catch (Exception error) when (IsSourceError(error)) { }
        }
        return result;
    }

    private static async Task<List<Artifact>> ReadProfileAsync(Profile profile, string temporaryRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(temporaryRoot);
        var snapshotDirectory = Path.Combine(temporaryRoot, "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);
        var snapshot = Path.Combine(snapshotDirectory, "History");
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = Stamp(profile.History);
                var walBefore = Stamp(profile.History + "-wal");
                if (walBefore.Exists)
                    await CopySharedAsync(profile.History + "-wal", snapshot + "-wal", cancellationToken).ConfigureAwait(false);
                else if (File.Exists(snapshot + "-wal")) File.Delete(snapshot + "-wal");
                await CopySharedAsync(profile.History, snapshot, cancellationToken).ConfigureAwait(false);
                if (before == Stamp(profile.History) && walBefore == Stamp(profile.History + "-wal"))
                    return ReadSnapshot(profile, snapshot, cancellationToken);
            }
            throw new IOException("Browser history changed during every snapshot attempt.");
        }
        finally
        {
            // Only this newly-created private directory is removed, including copied WAL sidecars.
            Directory.Delete(snapshotDirectory, recursive: true);
        }
    }

    private static async Task CopySharedAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static (bool Exists, long Length, DateTime Written) Stamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (true, file.Length, file.LastWriteTimeUtc) : (false, 0, default);
    }

    private static List<Artifact> ReadSnapshot(Profile profile, string snapshot, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = snapshot, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT v.visit_time, u.url, u.title FROM visits AS v JOIN urls AS u ON u.id = v.url";
        using var reader = command.ExecuteReader();
        var buckets = new Dictionary<(string Month, string Domain), Bucket>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset when;
            try { when = WebKitEpoch.AddTicks(checked(reader.GetInt64(0) * 10)); }
            catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException or InvalidCastException) { continue; }
            var month = when.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var domain = Domain(reader.IsDBNull(1) ? "" : reader.GetString(1));
            var key = (month, domain);
            if (!buckets.TryGetValue(key, out var bucket)) buckets.Add(key, bucket = new());
            bucket.Add(when, reader.IsDBNull(2) ? "" : reader.GetString(2));
        }

        foreach (var item in buckets.Where(x => x.Key.Domain != "other" && x.Value.Visits < 3).ToArray())
        {
            var key = (item.Key.Month, "other");
            if (!buckets.TryGetValue(key, out var other)) buckets.Add(key, other = new());
            other.Merge(item.Value);
            buckets.Remove(item.Key);
        }
        return buckets.OrderBy(x => x.Key.Month, StringComparer.Ordinal).ThenBy(x => x.Key.Domain, StringComparer.Ordinal)
            .Select(x => new Artifact("browser", "BrowserMonth", string.Join('|', profile.Browser, profile.Name, x.Key.Month, x.Key.Domain),
                x.Key.Month + " " + x.Key.Domain, x.Value.First, x.Value.Last, profile.History,
                new Dictionary<string, string>
                {
                    ["browser"] = profile.Browser,
                    ["profile"] = profile.Name,
                    ["visits"] = x.Value.Visits.ToString(CultureInfo.InvariantCulture),
                    ["titles"] = JsonSerializer.Serialize(x.Value.Titles.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal)
                        .Take(3).Select(t => t.Key.Length > 160 ? t.Key[..160] : t.Key), ReadableJson)
                })).ToList();
    }

    private static string Domain(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host)) return "other";
        var host = parsed.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length >= 2 ? labels[^2] + "." + labels[^1] : host;
    }

    private static bool IsSourceError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or ArgumentException or InvalidOperationException;
    private sealed record Profile(string Browser, string Name, string History);

    private sealed class Bucket
    {
        public long Visits { get; private set; }
        public DateTimeOffset? First { get; private set; }
        public DateTimeOffset? Last { get; private set; }
        public Dictionary<string, long> Titles { get; } = new(StringComparer.Ordinal);
        public void Add(DateTimeOffset when, string title)
        {
            Visits++;
            if (First is null || when < First) First = when;
            if (Last is null || when > Last) Last = when;
            if (!string.IsNullOrWhiteSpace(title)) Titles[title] = Titles.GetValueOrDefault(title) + 1;
        }
        public void Merge(Bucket other)
        {
            Visits += other.Visits;
            if (First is null || other.First < First) First = other.First;
            if (Last is null || other.Last > Last) Last = other.Last;
            foreach (var title in other.Titles) Titles[title.Key] = Titles.GetValueOrDefault(title.Key) + title.Value;
        }
    }
}
