namespace Exhume.Core;

public sealed record ExhumeConfig(
    Uri OllamaUrl,
    string ModelTag,
    string DataDirectory,
    TimeSpan RequestTimeout,
    int MaximumAttempts)
{
    public string TemporaryDirectory => Path.Combine(DataDirectory, "temp");
}

public sealed record ConfigOverrides
{
    public string? OllamaUrl { get; init; }
    public string? ModelTag { get; init; }
    public string? DataDirectory { get; init; }
    public int? RequestTimeoutSeconds { get; init; }
    public int? MaximumAttempts { get; init; }
}

public sealed record ConfigurationResult(
    ExhumeConfig Config,
    IReadOnlyDictionary<string, string> Origins);
