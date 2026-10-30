using Microsoft.Data.Sqlite;

namespace Exhume.Core.Tests;

public sealed class StorageRecoveryTests : IDisposable
{
    private readonly string directory;

    public StorageRecoveryTests()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (string.Equals(Path.GetPathRoot(temporaryRoot), "C:\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Set TEMP and TMP to a project-local temporary directory off C: before testing.");
        directory = Path.Combine(temporaryRoot, "exhume-storage-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [Fact]
    public async Task ConcurrentLegacyStoreOpensMigrateOnceAndPreserveBothScanWrites()
    {
        var firstStore = new SqliteArtifactStore(directory);
        var secondStore = new SqliteArtifactStore(directory);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = firstStore.DatabasePath,
            Pooling = false
        }.ToString();
        await using (var legacy = new SqliteConnection(connectionString))
        {
            await legacy.OpenAsync();
            using var create = legacy.CreateCommand();
            create.CommandText = """
                CREATE TABLE scans (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    artifact_count INTEGER NOT NULL,
                    error TEXT
                );
                INSERT INTO scans (source, started_at, artifact_count)
                VALUES ('folder', '2020-01-01', 2);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = Task.Run(async () =>
        {
            await start.Task;
            await firstStore.RecordScanAsync(new ScanRecord("browser", DateTimeOffset.UtcNow, 3, WarningCount: 2));
        });
        Task second = Task.Run(async () =>
        {
            await start.Task;
            await secondStore.RecordScanAsync(new ScanRecord("git", DateTimeOffset.UtcNow, 4, WarningCount: 1));
        });
        start.SetResult();
        await Task.WhenAll(first, second);

        await using var check = new SqliteConnection(connectionString);
        await check.OpenAsync();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT source, artifact_count, warning_count, error FROM scans ORDER BY id;";
        var rows = new Dictionary<string, (int Artifacts, int Warnings, bool ErrorAbsent)>(StringComparer.Ordinal);
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0), (reader.GetInt32(1), reader.GetInt32(2), reader.IsDBNull(3)));

        Assert.Equal(3, rows.Count);
        Assert.Equal((2, 0, true), rows["folder"]);
        Assert.Equal((3, 2, true), rows["browser"]);
        Assert.Equal((4, 1, true), rows["git"]);
        query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('scans') WHERE name = 'warning_count';";
        Assert.Equal(1L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task WindowsForgetPreservesDatabaseAndSidecarWhenDeletionSharingIsDenied()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SqliteArtifactStore(directory);
        await store.RecordScanAsync(new ScanRecord("folder", DateTimeOffset.UtcNow, 2));
        string sidecar = store.DatabasePath + "-wal";
        await File.WriteAllTextAsync(sidecar, "preserved sidecar bytes");
        byte[] databaseBefore = await File.ReadAllBytesAsync(store.DatabasePath);
        byte[] sidecarBefore = await File.ReadAllBytesAsync(sidecar);

        using (var held = new FileStream(store.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await Assert.ThrowsAsync<IOException>(() => store.ForgetAsync());
            Assert.Equal(databaseBefore, await File.ReadAllBytesAsync(store.DatabasePath));
            Assert.Equal(sidecarBefore, await File.ReadAllBytesAsync(sidecar));
        }

        await store.ForgetAsync();
        Assert.False(Directory.Exists(directory));
    }

    public void Dispose()
    {
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test cleanup escaped its temporary root.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
