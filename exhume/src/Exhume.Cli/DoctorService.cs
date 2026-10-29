using System.Text.Json;
using Exhume.Core;

namespace Exhume.Cli;

public sealed record ModelStatus(bool Reachable, string TagPresence, string Reason);

public sealed class DoctorService(HttpClient client)
{
    private const int MaximumResponseBytes = 1_048_576;

    public async Task<ModelStatus> CheckModelAsync(ExhumeConfig config, CancellationToken cancellationToken = default)
    {
        var endpoint = new UriBuilder(config.OllamaUrl)
        {
            Path = config.OllamaUrl.AbsolutePath.TrimEnd('/') + "/api/tags",
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.RequestTimeout);
        try
        {
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new(true, "unknown", $"HTTP {(int)response.StatusCode}");
            }
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                return new(true, "unknown", "Model listing is too large.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (content.Length + bytesRead > MaximumResponseBytes)
                {
                    return new(true, "unknown", "Model listing is too large.");
                }
                content.Write(buffer, 0, bytesRead);
            }
            content.Position = 0;
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return new(true, "unknown", "Invalid model listing.");
            }
            var found = false;
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                {
                    return new(true, "unknown", "Invalid model listing.");
                }
                var name = ReadString(model, "name") ?? ReadString(model, "model");
                if (name is null)
                {
                    return new(true, "unknown", "Invalid model listing.");
                }
                found |= string.Equals(name, config.ModelTag, StringComparison.Ordinal);
            }
            return new(true, found ? "present" : "absent", "Model listing received.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "unknown", "Request timed out.");
        }
        catch (HttpRequestException exception)
        {
            return new(false, "unknown", exception.Message);
        }
        catch (IOException exception)
        {
            return new(false, "unknown", exception.Message);
        }
        catch (JsonException)
        {
            return new(true, "unknown", "Invalid model listing.");
        }
    }

    public static IReadOnlyDictionary<string, SourceStatus> CheckSources(
        IReadOnlyList<IEvidenceAdapter>? adapters, ScanOptions options)
    {
        var sources = new Dictionary<string, SourceStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "windows", "browser", "folder", "git" })
        {
            sources[name] = new(SourceAvailability.Unavailable, "not implemented");
        }
        foreach (var adapter in adapters ?? [])
        {
            try
            {
                sources[adapter.Name] = adapter.Check(options);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sources[adapter.Name] = new(SourceAvailability.Unavailable, exception.Message);
            }
        }
        return sources;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
