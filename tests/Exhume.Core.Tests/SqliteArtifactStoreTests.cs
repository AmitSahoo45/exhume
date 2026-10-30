using Xunit;

namespace Exhume.Core.Tests;

public sealed class SqliteArtifactStoreTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), "exhume-store-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UpsertIsIdempotentAndRehydratesTheOriginalIdentity()
    {
        var store = CreateStore();
        var first = Make("folder", "FolderMonth", "key", "original");
        var updated = new Artifact("folder", "FolderMonth", "key", "updated", null, null, "updated evidence",
            new Dictionary<string, string> { ["count"] = "7" });

        await store.UpsertAsync([first]);
        await store.UpsertAsync([updated]);
        var loaded = Assert.Single(await new SqliteArtifactStore(temporaryDirectory).QueryAsync());

        Assert.Equal(first.Id, loaded.Id);
        Assert.Equal("updated", loaded.Name);
        Assert.Equal("updated evidence", loaded.Evidence);
        Assert.Null(loaded.FirstSeen);
        Assert.Equal("7", loaded.Metadata["count"]);
        Assert.Equal(new StoreCounts(1, 0, 0), await store.GetCountsAsync());
    }

    [Fact]
    public async Task QueryCombinesSourceKindAndInclusiveUtcDateRange()
    {
        var store = CreateStore();
        DateTimeOffset boundary = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertAsync([
            Make("folder", "FolderMonth", "before", "before", boundary.AddTicks(-1)),
            Make("folder", "FolderMonth", "at", "at", boundary.ToOffset(TimeSpan.FromHours(5.5))),
            Make("folder", "FolderMonth", "after", "after", boundary.AddTicks(1)),
            Make("git", "FolderMonth", "source", "wrong source", boundary),
            Make("folder", "Other", "kind", "wrong kind", boundary),
            Make("folder", "FolderMonth", "undated", "undated")
        ]);

        var loaded = Assert.Single(await store.QueryAsync(new ArtifactQuery("folder", "FolderMonth",
            boundary.ToOffset(TimeSpan.FromHours(-2)), boundary.ToOffset(TimeSpan.FromHours(3)))));

        Assert.Equal("at", loaded.Name);
        Assert.Equal(TimeSpan.Zero, loaded.FirstSeen!.Value.Offset);
        Assert.Equal(6, (await store.QueryAsync()).Count);
    }

    [Fact]
    public async Task QueryValuesAreParametersRatherThanSql()
    {
        var store = CreateStore();
        await store.UpsertAsync([Make("folder", "FolderMonth", "key", "name")]);

        Assert.Empty(await store.QueryAsync(new ArtifactQuery(Source: "folder' OR 1=1 --")));
        Assert.Equal(1, (await store.GetCountsAsync()).Artifacts);
    }

    [Fact]
    public async Task NarrationRoundTripReplacesOnlyItsOwnCacheEntry()
    {
        var store = CreateStore();
        var created = new DateTimeOffset(2024, 2, 29, 23, 0, 0, TimeSpan.FromHours(-2));
        await store.SaveNarrationAsync(new CachedNarration("key", "{\"body\":\"first\"}", "model", "v1", created));
        await store.SaveNarrationAsync(new CachedNarration("other", "{}", "model", "v2", created));
        await store.SaveNarrationAsync(new CachedNarration("key", "{\"body\":\"updated\"}", "model", "v1", created));

        var loaded = await new SqliteArtifactStore(temporaryDirectory).GetNarrationAsync("key");

        Assert.NotNull(loaded);
        Assert.Equal("{\"body\":\"updated\"}", loaded.Json);
        Assert.Equal("model", loaded.Model);
        Assert.Equal("v1", loaded.PromptVersion);
        Assert.Equal(created.ToUniversalTime(), loaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
        Assert.Null(await store.GetNarrationAsync("missing"));
        Assert.Equal(2, (await store.GetCountsAsync()).Narrations);
    }

    [Fact]
    public async Task ScansRecordSuccessfulAndFailedRuns()
    {
        var store = CreateStore();
        await store.RecordScanAsync(new ScanRecord("folder", DateTimeOffset.UtcNow, 3));
        await store.RecordScanAsync(new ScanRecord("git", DateTimeOffset.UtcNow, 0, "cancelled"));

        Assert.Equal(new StoreCounts(0, 0, 2), await store.GetCountsAsync());
    }

    [Fact]
    public async Task CancellationRollsBackOnlyTheCurrentBatch()
    {
        var store = CreateStore();
        await store.UpsertAsync([Make("folder", "FolderMonth", "existing", "existing")]);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<Artifact> CurrentBatch()
        {
            yield return Make("folder", "FolderMonth", "new", "new");
            cancellation.Cancel();
            yield return Make("folder", "FolderMonth", "cancelled", "cancelled");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertAsync(CurrentBatch(), cancellation.Token));

        Assert.Equal("existing", Assert.Single(await store.QueryAsync()).Name);
    }

    [Fact]
    public async Task EnumerationFailureRollsBackTheCurrentBatch()
    {
        var store = CreateStore();
        IEnumerable<Artifact> BrokenBatch()
        {
            yield return Make("folder", "FolderMonth", "partial", "partial");
            throw new InvalidDataException("source failed");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpsertAsync(BrokenBatch()));

        Assert.Empty(await store.QueryAsync());
    }

    [Fact]
    public async Task CancelledInitializationDoesNotCreateFiles()
    {
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InitializeAsync(cancellation.Token));

        Assert.False(Directory.Exists(temporaryDirectory));
    }

    [Fact]
    public async Task ForgetRemovesDatabaseAndEmptyDirectoryThenSupportsAnEmptyReopen()
    {
        var store = CreateStore();
        await SeedAllTables(store);

        await store.ForgetAsync();

        Assert.False(File.Exists(store.DatabasePath));
        Assert.False(Directory.Exists(temporaryDirectory));
        Assert.Equal(new StoreCounts(0, 0, 0), await store.GetCountsAsync());
    }

    [Fact]
    public async Task ForgetPreservesUnknownFilesAndSubdirectories()
    {
        var store = CreateStore();
        await SeedAllTables(store);
        string unknownPath = Path.Combine(temporaryDirectory, "keep.txt");
        string childDirectory = Path.Combine(temporaryDirectory, "unrelated");
        await File.WriteAllTextAsync(unknownPath, "preserved");
        Directory.CreateDirectory(childDirectory);

        await store.ForgetAsync();

        Assert.False(File.Exists(store.DatabasePath));
        Assert.Equal("preserved", await File.ReadAllTextAsync(unknownPath));
        Assert.True(Directory.Exists(childDirectory));
    }

    [Fact]
    public async Task ForgetOnMissingDirectoryDoesNotCreateIt()
    {
        var store = CreateStore();

        await store.ForgetAsync();

        Assert.False(Directory.Exists(temporaryDirectory));
    }

    [Fact]
    public async Task ForgetRemovesCorruptDatabaseWithoutOpeningIt()
    {
        var store = CreateStore();
        Directory.CreateDirectory(temporaryDirectory);
        await File.WriteAllTextAsync(store.DatabasePath, "not a database; private evidence");
        await File.WriteAllTextAsync(store.DatabasePath + "-wal", "private journal");
        await store.ForgetAsync();
        Assert.False(Directory.Exists(temporaryDirectory));
    }

    [Fact]
    public async Task ScanWarningCountsMigrateExistingStoreWithoutMarkingFailure()
    {
        var store = CreateStore();
        Directory.CreateDirectory(temporaryDirectory);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE scans (id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, started_at TEXT NOT NULL, artifact_count INTEGER NOT NULL, error TEXT); INSERT INTO scans(source,started_at,artifact_count) VALUES ('folder','2020-01-01',2);";
            await command.ExecuteNonQueryAsync();
        }
        await store.RecordScanAsync(new("folder", DateTimeOffset.UtcNow, 4, WarningCount: 3));
        await using var check = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await check.OpenAsync();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT SUM(warning_count), COUNT(*), COUNT(error) FROM scans;";
        using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
    }

    public void Dispose()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string target = Path.GetFullPath(temporaryDirectory);
        if (!target.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Test cleanup escaped its temporary root.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    private SqliteArtifactStore CreateStore() => new(temporaryDirectory);

    private static Artifact Make(string source, string kind, string key, string name, DateTimeOffset? date = null) =>
        new(source, kind, key, name, date, date, "evidence");

    private static async Task SeedAllTables(SqliteArtifactStore store)
    {
        await store.UpsertAsync([Make("folder", "FolderMonth", "key", "name")]);
        await store.SaveNarrationAsync(new CachedNarration("key", "{}", "model", "v1", DateTimeOffset.UtcNow));
        await store.RecordScanAsync(new ScanRecord("folder", DateTimeOffset.UtcNow, 1));
    }
}
