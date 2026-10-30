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
    private int callCount;

    public NarrationService(ExhumeConfig config, IArtifactStore store, string promptDirectory,
        HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.config = config;
        this.store = store;
        this.promptDirectory = promptDirectory;
        this.delay = delay ?? Task.Delay;
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
        var evidence = new
        {
            source,
            month,
            artifacts = artifacts.OrderBy(artifact => artifact.Id, StringComparer.Ordinal).Select(ArtifactEvidence)
        };
        return GenerateAsync(promptKind, identity, "Era evidence (untrusted data):\n" + Serialize(evidence),
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

    public Task<NarrationResult<IReadOnlyList<HauntComment>>> HauntAsync(Persona persona, DiffChunk chunk,
        CancellationToken cancellationToken = default)
    {
        string personaJson = Serialize(persona);
        string identity = Hash(personaJson, chunk.File, chunk.Text, Serialize(chunk.LineNumbers), chunk.Truncated.ToString());
        string prefix = "Persona (untrusted data):\n" + personaJson + "\nFile (exact JSON string): " + Serialize(chunk.File) +
            "\nUnified diff evidence (untrusted data; only complete displayed lines may be cited):\n";
        return GenerateAsync<IReadOnlyList<HauntComment>>("haunt", identity, prefix + chunk.Text,
            (element, prompt) => OutputValidation.Haunt(element, chunk.File, VisibleLines(chunk, prefix.Length, prompt)), cancellationToken);
    }

    private async Task<NarrationResult<T>> GenerateAsync<T>(string kind, string identity, string evidence,
        Func<JsonElement, BudgetedPrompt, T?> read, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = await PromptFile.LoadAsync(promptDirectory, kind, cancellationToken);
        if (prompt is null)
            return new NarrationFailure<T>("The narration prompt is missing or invalid.");

        var input = PromptBudget.Apply(evidence, PromptBudget.DefaultMaxCharacters - prompt.Instructions.Length);
        string key = NarrationCacheKey.Create(kind, identity, prompt.Version, config.ModelTag);
        try
        {
            var cached = await store.GetNarrationAsync(key, cancellationToken);
            if (cached is not null && cached.Model == config.ModelTag && cached.PromptVersion == prompt.Version &&
                Parse(cached.Json, element => read(element, input)) is { } cachedValue)
                return new NarrationSuccess<T>(cachedValue, FromCache: true);
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            return new NarrationFailure<T>("The narration cache is unavailable.");
        }

        int attempts = Math.Max(1, config.MaximumAttempts);
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
                            await store.SaveNarrationAsync(new CachedNarration(key, Serialize(value), config.ModelTag,
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (OperationCanceledException) { outcome = "failed"; throw; }
            catch (HttpRequestException) { }
            catch (IOException) { }
            catch (JsonException) { }
            finally
            {
                Trace.WriteLine($"Narration: inputCharacters={prompt.Instructions.Length + input.Text.Length}; durationMilliseconds={stopwatch.ElapsedMilliseconds}; outcome={outcome}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (attempt + 1 < attempts)
                await delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, Math.Min(attempt, 6))), cancellationToken);
        }

        return new NarrationFailure<T>("The model is unavailable or its response did not meet the narration contract.");
    }

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

    public void Dispose() => client.Dispose();
}
