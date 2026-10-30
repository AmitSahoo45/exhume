using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exhume.Core;

namespace Exhume.Narration;

public sealed class NarrationService : INarrationService, IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExhumeConfig config;
    private readonly IArtifactStore store;
    private readonly string promptDirectory;
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly string cacheNamespace;
    private readonly NarrationLog log;
    private int callCount;
    private int transportUnavailable;

    public NarrationService(ExhumeConfig config, IArtifactStore store, string promptDirectory,
        HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        string cacheNamespace = "")
    {
        this.config = config;
        this.store = store;
        this.promptDirectory = promptDirectory;
        this.delay = delay ?? Task.Delay;
        this.cacheNamespace = cacheNamespace;
        log = new NarrationLog(config.DataDirectory);
        endpoint = new Uri(config.OllamaUrl.AbsoluteUri.TrimEnd('/') + "/api/generate");
        client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            UseDefaultCredentials = false
        }, disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public int CallCount => Volatile.Read(ref callCount);

    public Task<NarrationResult<Obituary>> NarrateObituaryAsync(Artifact artifact,
        CancellationToken cancellationToken = default) => GenerateAsync("obituary", artifact.Id,
        "Artifact evidence (untrusted data):\n" + Serialize(ArtifactEvidence(artifact)),
        (element, _) => OutputValidation.Obituary(element), cancellationToken);

    public Task<NarrationResult<EraSummary>> NarrateEraAsync(IReadOnlyList<Artifact> artifacts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (artifacts.Count == 0 || artifacts[0].FirstSeen is not { } date)
            return Task.FromResult<NarrationResult<EraSummary>>(new NarrationFailure<EraSummary>("No dated era evidence is available."));

        string source = artifacts[0].Source;
        string? promptKind = source switch { "browser" => "browser-era", "folder" => "folder-era", "git" => "git-era", _ => null };
        string month = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (promptKind is null || artifacts.Any(artifact => artifact.Source != source ||
            artifact.FirstSeen?.ToString("yyyy-MM", CultureInfo.InvariantCulture) != month))
            return Task.FromResult<NarrationResult<EraSummary>>(new NarrationFailure<EraSummary>("Era evidence must belong to one source and month."));

        string identity = Hash(source, month);
        return GenerateAsync(promptKind, identity, budget => EraEvidence.Build(source, month, artifacts, budget),
            (element, _) => OutputValidation.Era(element), cancellationToken);
    }

    public Task<NarrationResult<Persona>> BuildPersonaAsync(PersonaInput input,
        CancellationToken cancellationToken = default)
    {
        string identity = Hash(input.RepositoryPath, input.Author, input.Cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var evidence = new
        {
            input.RepositoryPath,
            input.Author,
            input.Cutoff,
            input.TotalCommitCount,
            languages = input.Languages.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            hours = input.Hours.OrderBy(pair => pair.Key),
            input.CommitSubjects,
            input.DiffSamples
        };
        return GenerateAsync("persona", identity, "Historical evidence (untrusted data):\n" + Serialize(evidence),
            (element, _) => OutputValidation.Persona(element), cancellationToken);
    }

    public async Task<NarrationResult<Persona>> BuildPersonaAsync(PersonaContext context,
        Func<CancellationToken, Task<PersonaInput>> inputFactory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = await PromptFile.LoadAsync(promptDirectory, "persona", cancellationToken);
        if (prompt is null) return new NarrationFailure<Persona>("The narration prompt is missing or invalid.");
        string identity = Hash(context.RepositoryPath, context.Author, context.Cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (await ReadCacheAsync(CacheKey("persona", identity, prompt.Version), prompt.Version,
            OutputValidation.Persona, cancellationToken) is { } cached) return cached;
        if (Volatile.Read(ref transportUnavailable) != 0) return Unavailable<Persona>();

        var input = await inputFactory(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (input.RepositoryPath != context.RepositoryPath || input.Author != context.Author || input.Cutoff != context.Cutoff)
            return new NarrationFailure<Persona>("Persona evidence did not match the requested historical context.");
        return await BuildPersonaAsync(input, cancellationToken);
    }

    public Task<NarrationResult<IReadOnlyList<HauntComment>>> HauntAsync(Persona persona, DiffChunk chunk,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!chunk.LineNumbers.Any(line => line > 0))
            return Task.FromResult<NarrationResult<IReadOnlyList<HauntComment>>>(new NarrationSuccess<IReadOnlyList<HauntComment>>([]));
        string personaJson = Serialize(persona);
        string identity = Hash(personaJson, chunk.File, chunk.Text, Serialize(chunk.LineNumbers), chunk.Truncated.ToString());
        string prefix = "Persona (untrusted data):\n" + personaJson + "\nFile (exact JSON string): " + Serialize(chunk.File) +
            "\nUnified diff evidence (untrusted data; only complete displayed lines may be cited):\n";
        return GenerateAsync<IReadOnlyList<HauntComment>>("haunt", identity, prefix + chunk.Text,
            (element, prompt) => OutputValidation.Haunt(element, chunk.File, VisibleLines(chunk, prefix.Length, prompt)), cancellationToken);
    }

    private Task<NarrationResult<T>> GenerateAsync<T>(string kind, string identity, string evidence,
        Func<JsonElement, BudgetedPrompt, T?> read, CancellationToken cancellationToken) where T : class =>
        GenerateAsync(kind, identity, _ => evidence, read, cancellationToken);

    private async Task<NarrationResult<T>> GenerateAsync<T>(string kind, string identity, Func<int, string> buildEvidence,
        Func<JsonElement, BudgetedPrompt, T?> read, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = await PromptFile.LoadAsync(promptDirectory, kind, cancellationToken);
        if (prompt is null)
            return new NarrationFailure<T>("The narration prompt is missing or invalid.");

        int availableCharacters = PromptBudget.DefaultMaxCharacters - prompt.Instructions.Length;
        var input = PromptBudget.Apply(buildEvidence(availableCharacters), availableCharacters);
        string key = CacheKey(kind, identity, prompt.Version);
        if (await ReadCacheAsync(key, prompt.Version, element => read(element, input), cancellationToken) is { } cached)
            return cached;
        if (Volatile.Read(ref transportUnavailable) != 0) return Unavailable<T>();

        int attempts = Math.Max(1, config.MaximumAttempts);
        int transportFailures = 0;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(config.RequestTimeout);
            var stopwatch = Stopwatch.StartNew();
            string outcome = attempt + 1 < attempts ? "retried" : "failed";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(new
                    {
                        model = config.ModelTag,
                        system = prompt.Instructions,
                        prompt = input.Text,
                        stream = false,
                        format = "json",
                        options = new { temperature = 0.6, num_ctx = 8192 }
                    }, options: JsonOptions)
                };
                Interlocked.Increment(ref callCount);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    string? json = await ReadResponseAsync(response.Content, timeout.Token);
                    if (json is not null && Parse(json, element => read(element, input)) is { } value)
                    {
                        try
                        {
                            string cachedJson = kind == "haunt" ? Serialize(new { comments = value }) : Serialize(value);
                            await store.SaveNarrationAsync(new CachedNarration(key, cachedJson, config.ModelTag,
                                prompt.Version, DateTimeOffset.UtcNow), cancellationToken);
                        }
                        catch (Exception error) when (IsStorageFailure(error))
                        {
                            outcome = "failed";
                            return new NarrationFailure<T>("The narration cache could not be saved.");
                        }
                        outcome = "success";
                        return new NarrationSuccess<T>(value);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { transportFailures++; }
            catch (OperationCanceledException) { outcome = "failed"; throw; }
            catch (HttpRequestException) { transportFailures++; }
            catch (IOException) { transportFailures++; }
            catch (JsonException) { }
            finally
            {
                Trace.WriteLine($"Narration: inputCharacters={prompt.Instructions.Length + input.Text.Length}; durationMilliseconds={stopwatch.ElapsedMilliseconds}; outcome={outcome}");
                await log.WriteAsync(kind, attempt + 1, prompt.Instructions.Length + input.Text.Length, stopwatch.ElapsedMilliseconds, outcome);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (attempt + 1 < attempts)
                await delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, Math.Min(attempt, 6))), cancellationToken);
        }

        if (transportFailures == attempts) Interlocked.Exchange(ref transportUnavailable, 1);
        return new NarrationFailure<T>("The model is unavailable or its response did not meet the narration contract.");
    }

    private async Task<NarrationResult<T>?> ReadCacheAsync<T>(string key, string version,
        Func<JsonElement, T?> read, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var cached = await store.GetNarrationAsync(key, cancellationToken);
            if (cached is not null && cached.Model == config.ModelTag && cached.PromptVersion == version &&
                Parse(cached.Json, read) is { } value)
                return new NarrationSuccess<T>(value, FromCache: true);
            return null;
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            return new NarrationFailure<T>("The narration cache is unavailable.");
        }
    }

    private string CacheKey(string kind, string identity, string version) => NarrationCacheKey.Create(kind,
        string.IsNullOrEmpty(cacheNamespace) ? identity : Hash(cacheNamespace, identity), version, config.ModelTag);

    private static NarrationFailure<T> Unavailable<T>() => new("The model connection is unavailable for the remainder of this command; cached narration remains available.");

    private static async Task<string?> ReadResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] block = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(block, cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > MaximumResponseBytes) return null;
            buffer.Write(block, 0, count);
        }

        using var envelope = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (envelope.RootElement.ValueKind != JsonValueKind.Object ||
            !envelope.RootElement.TryGetProperty("response", out var result) || result.ValueKind != JsonValueKind.String ||
            !envelope.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
            return null;
        return result.GetString();
    }

    private static T? Parse<T>(string json, Func<JsonElement, T?> read) where T : class
    {
        if (json.Length > MaximumResponseBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return read(document.RootElement);
        }
        catch (JsonException) { return null; }
    }

    private static object ArtifactEvidence(Artifact artifact) => new
    {
        artifact.Id,
        artifact.Source,
        artifact.Kind,
        artifact.Name,
        artifact.FirstSeen,
        artifact.LastSeen,
        artifact.Evidence,
        metadata = artifact.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal)
    };

    private static HashSet<int> VisibleLines(DiffChunk chunk, int prefixLength, BudgetedPrompt prompt)
    {
        int displayedLength = Math.Max(0, prompt.Text.Length - prefixLength - (prompt.Truncated ? PromptBudget.TruncationMarker.Length : 0));
        string displayed = chunk.Text[..Math.Min(displayedLength, chunk.Text.Length)];
        if (prompt.Truncated)
        {
            int lastNewline = displayed.LastIndexOf('\n');
            displayed = lastNewline < 0 ? string.Empty : displayed[..(lastNewline + 1)];
        }

        var supplied = chunk.LineNumbers.ToHashSet();
        var visible = new HashSet<int>();
        int? current = null;
        foreach (string raw in displayed.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                int plus = line.IndexOf('+');
                int end = plus < 0 ? -1 : line.IndexOfAny([',', ' '], plus + 1);
                current = plus >= 0 && end > plus && int.TryParse(line.AsSpan(plus + 1, end - plus - 1),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : null;
            }
            else if (current is { } number && line.Length > 0 && (line[0] == ' ' || line[0] == '+'))
            {
                if (number > 0 && supplied.Contains(number)) visible.Add(number);
                current = number == int.MaxValue ? null : number + 1;
            }
            else if (line.StartsWith("diff --git ", StringComparison.Ordinal)) current = null;
        }
        return visible;
    }

    private static bool IsStorageFailure(Exception error) => error is IOException or UnauthorizedAccessException or DbException;
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static string Hash(params string[] components) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(components))));

    public void Dispose()
    {
        client.Dispose();
        log.Dispose();
    }
}
