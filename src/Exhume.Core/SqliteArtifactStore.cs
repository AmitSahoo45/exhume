using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Exhume.Core;

public sealed class SqliteArtifactStore : IArtifactStore
{
    private readonly string dataDirectory;

    public SqliteArtifactStore(string dataDirectory)
    {
        this.dataDirectory = DataDirectoryPolicy.Normalize(dataDirectory);
        DatabasePath = Path.Combine(this.dataDirectory, "exhume.db");
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
    }

    public async Task UpsertAsync(IEnumerable<Artifact> artifacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO artifacts (id, source, kind, name, first_seen, last_seen, evidence, metadata, updated_at)
            VALUES ($id, $source, $kind, $name, $first, $last, $evidence, $metadata, $updated)
            ON CONFLICT(id) DO UPDATE SET
                source = excluded.source, kind = excluded.kind, name = excluded.name,
                first_seen = excluded.first_seen, last_seen = excluded.last_seen,
                evidence = excluded.evidence, metadata = excluded.metadata, updated_at = excluded.updated_at;
            """;

        foreach (Artifact artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(artifact);
            command.Parameters.Clear();
            Add(command, "$id", artifact.Id);
            Add(command, "$source", artifact.Source);
            Add(command, "$kind", artifact.Kind);
            Add(command, "$name", artifact.Name);
            Add(command, "$first", FormatTimestamp(artifact.FirstSeen));
            Add(command, "$last", FormatTimestamp(artifact.LastSeen));
            Add(command, "$evidence", artifact.Evidence);
            Add(command, "$metadata", JsonSerializer.Serialize(artifact.Metadata));
            Add(command, "$updated", FormatTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public async Task<IReadOnlyList<Artifact>> QueryAsync(ArtifactQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new ArtifactQuery();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, kind, name, first_seen, last_seen, evidence, metadata
            FROM artifacts
            WHERE ($source IS NULL OR source = $source)
              AND ($kind IS NULL OR kind = $kind)
              AND ($from IS NULL OR first_seen >= $from)
              AND ($to IS NULL OR first_seen <= $to)
            ORDER BY first_seen, source, kind, id;
            """;
        Add(command, "$source", query.Source);
        Add(command, "$kind", query.Kind);
        Add(command, "$from", FormatTimestamp(query.From));
        Add(command, "$to", FormatTimestamp(query.To));

        var result = new List<Artifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7))
                ?? throw new InvalidDataException("Stored artifact metadata must be a JSON object.");
            result.Add(Artifact.Restore(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), ReadTimestamp(reader, 4), ReadTimestamp(reader, 5), reader.GetString(6), metadata));
        }

        return result.AsReadOnly();
    }

    public async Task<CachedNarration?> GetNarrationAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_key, json, model, prompt_version, created_at FROM narrations WHERE cache_key = $key;";
        Add(command, "$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CachedNarration(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ParseTimestamp(reader.GetString(4)))
            : null;
    }

    public async Task SaveNarrationAsync(CachedNarration narration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(narration);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO narrations (cache_key, json, model, prompt_version, created_at)
            VALUES ($key, $json, $model, $version, $created)
            ON CONFLICT(cache_key) DO UPDATE SET json = excluded.json, model = excluded.model,
                prompt_version = excluded.prompt_version, created_at = excluded.created_at;
            """;
        Add(command, "$key", narration.CacheKey);
        Add(command, "$json", narration.Json);
        Add(command, "$model", narration.Model);
        Add(command, "$version", narration.PromptVersion);
        Add(command, "$created", FormatTimestamp(narration.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordScanAsync(ScanRecord scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scans (source, started_at, artifact_count, error)
            VALUES ($source, $started, $count, $error);
            """;
        Add(command, "$source", scan.Source);
        Add(command, "$started", FormatTimestamp(scan.StartedAt));
        Add(command, "$count", scan.ArtifactCount);
        Add(command, "$error", scan.Error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoreCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM artifacts), (SELECT COUNT(*) FROM narrations), (SELECT COUNT(*) FROM scans);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new StoreCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDirectory();
        if (!Directory.Exists(dataDirectory))
        {
            return;
        }

        if (File.Exists(DatabasePath))
        {
            await using (var connection = await OpenConnectionAsync(cancellationToken))
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM artifacts; DELETE FROM narrations; DELETE FROM scans;";
                await command.ExecuteNonQueryAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }

            ValidateDirectory();
            File.Delete(DatabasePath);
        }

        foreach (string suffix in new[] { "-journal", "-wal", "-shm" })
        {
            File.Delete(DatabasePath + suffix);
        }

        if (!Directory.EnumerateFileSystemEntries(dataDirectory).Any())
        {
            Directory.Delete(dataDirectory);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDirectory();
        Directory.CreateDirectory(dataDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA temp_store = MEMORY;
                CREATE TABLE IF NOT EXISTS artifacts (
                    id TEXT NOT NULL PRIMARY KEY,
                    source TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    name TEXT NOT NULL,
                    first_seen TEXT,
                    last_seen TEXT,
                    evidence TEXT NOT NULL,
                    metadata TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_artifacts_source_first_seen ON artifacts (source, first_seen);
                CREATE TABLE IF NOT EXISTS narrations (
                    cache_key TEXT NOT NULL PRIMARY KEY,
                    json TEXT NOT NULL,
                    model TEXT NOT NULL,
                    prompt_version TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS scans (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    source TEXT NOT NULL,
                    started_at TEXT NOT NULL,
                    artifact_count INTEGER NOT NULL,
                    error TEXT
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void ValidateDirectory()
    {
        string normalized = DataDirectoryPolicy.Normalize(dataDirectory);
        if (!string.Equals(normalized, dataDirectory, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new IOException("The data directory target changed after the store was opened.");
        }

        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            if (new FileInfo(DatabasePath + suffix).LinkTarget is not null)
            {
                throw new IOException("The store cannot use linked database files.");
            }
        }
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
