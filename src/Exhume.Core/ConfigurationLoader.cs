using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace Exhume.Core;

public static class ConfigurationLoader
{
    public static ConfigurationResult Load(
        ConfigOverrides? overrides = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? configFilePath = null,
        string? workingDirectory = null)
    {
        var file = ReadFile(configFilePath ?? Path.Combine(AppContext.BaseDirectory, "exhume.json"));
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);
        var urlText = Choose("OllamaUrl", overrides?.OllamaUrl, "EXHUME_OLLAMA_URL", file.OllamaUrl,
            "http://localhost:11434");
        var modelTag = Choose("ModelTag", overrides?.ModelTag, "EXHUME_MODEL", file.ModelTag, "gemma4:e4b");
        var dataDirectory = Choose("DataDirectory", overrides?.DataDirectory, "EXHUME_HOME", file.DataDirectory, ".exhume");
        var timeout = ChooseNumber("RequestTimeout", overrides?.RequestTimeoutSeconds, "EXHUME_TIMEOUT_SECONDS",
            file.RequestTimeoutSeconds, 120);
        var maximumAttempts = ChooseNumber("MaximumAttempts", overrides?.MaximumAttempts, "EXHUME_MAX_ATTEMPTS",
            file.MaximumAttempts, 3);

        // CancellationTokenSource timers accept at most UInt32.MaxValue - 1 milliseconds.
        if ((long)timeout * 1000 > uint.MaxValue - 1L)
        {
            throw new ArgumentException("RequestTimeout exceeds the supported timer range; configure EXHUME_TIMEOUT_SECONDS.");
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var ollamaUrl)
            || !ollamaUrl.IsWellFormedOriginalString()
            || (ollamaUrl.Scheme != Uri.UriSchemeHttp && ollamaUrl.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(ollamaUrl.Host)
            || !string.IsNullOrEmpty(ollamaUrl.UserInfo)
            || !string.IsNullOrEmpty(ollamaUrl.Query)
            || !string.IsNullOrEmpty(ollamaUrl.Fragment))
        {
            throw new ArgumentException("Ollama URL must be an absolute HTTP or HTTPS endpoint without credentials, a query or a fragment.");
        }

        if (string.IsNullOrWhiteSpace(modelTag) || modelTag.Any(char.IsControl))
        {
            throw new ArgumentException("Model tag must be nonempty and contain no control characters; configure EXHUME_MODEL.");
        }

        var config = new ExhumeConfig(ollamaUrl, modelTag.Trim(),
            DataDirectoryPolicy.Normalize(dataDirectory, workingDirectory),
            TimeSpan.FromSeconds(timeout), maximumAttempts);
        return new ConfigurationResult(config, new ReadOnlyDictionary<string, string>(origins));

        string Choose(string property, string? commandLine, string variable, string? configured, string defaultValue)
        {
            var environmentValue = environment is null
                ? Environment.GetEnvironmentVariable(variable)
                : environment.GetValueOrDefault(variable);
            var (value, origin) = commandLine is not null
                ? (commandLine, "command line")
                : !string.IsNullOrWhiteSpace(environmentValue)
                    ? (environmentValue, "environment")
                    : configured is not null
                        ? (configured, "config file")
                        : (defaultValue, "default");
            origins.Add(property, origin);
            return value;
        }

        int ChooseNumber(string property, int? commandLine, string variable, int? configured, int defaultValue)
        {
            var text = Choose(property, commandLine?.ToString(CultureInfo.InvariantCulture), variable,
                configured?.ToString(CultureInfo.InvariantCulture), defaultValue.ToString(CultureInfo.InvariantCulture));
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
            {
                throw new ArgumentException($"{property} must be a positive integer; configure {variable}.");
            }

            return value;
        }
    }

    private static ConfigOverrides ReadFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ConfigOverrides>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The configuration file must contain a JSON object.");
        }
        catch (FileNotFoundException)
        {
            return new ConfigOverrides();
        }
        catch (DirectoryNotFoundException)
        {
            return new ConfigOverrides();
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The configuration file must contain a JSON object with valid setting types.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidDataException("The configuration file could not be read. Check its read permissions.");
        }
        catch (IOException)
        {
            throw new InvalidDataException("The configuration file could not be read. Check its availability.");
        }
    }
}
