using Exhume.Core;

namespace Exhume.Core.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string temporaryDirectory = CreateTemporaryDirectory();

    [Fact]
    public void MissingConfigurationUsesDefaultsWithoutCreatingDataDirectory()
    {
        var result = Load();

        Assert.Equal(new Uri("http://localhost:11434"), result.Config.OllamaUrl);
        Assert.Equal("gemma4:e4b", result.Config.ModelTag);
        Assert.Equal(Path.Combine(temporaryDirectory, ".exhume"), result.Config.DataDirectory);
        Assert.Equal(Path.Combine(temporaryDirectory, ".exhume", "temp"), result.Config.TemporaryDirectory);
        Assert.Equal(TimeSpan.FromSeconds(120), result.Config.RequestTimeout);
        Assert.Equal(3, result.Config.MaximumAttempts);
        Assert.Equal(5, result.Origins.Count);
        Assert.All(result.Origins.Values, origin => Assert.Equal("default", origin));
        Assert.False(Directory.Exists(result.Config.DataDirectory));
    }

    [Fact]
    public void CommandLineWinsOverEnvironmentAndConfigurationFile()
    {
        WriteConfig("""
            { "OllamaUrl": "http://localhost:11431", "ModelTag": "file:model",
              "DataDirectory": "file-data", "RequestTimeoutSeconds": 31, "MaximumAttempts": 4 }
            """);
        var result = Load(new ConfigOverrides
        {
            OllamaUrl = "http://localhost:11433",
            ModelTag = "command:model",
            DataDirectory = "command-data",
            RequestTimeoutSeconds = 33,
            MaximumAttempts = 6
        }, CompleteEnvironment());

        Assert.Equal(new Uri("http://localhost:11433"), result.Config.OllamaUrl);
        Assert.Equal("command:model", result.Config.ModelTag);
        Assert.Equal(Path.Combine(temporaryDirectory, "command-data"), result.Config.DataDirectory);
        Assert.Equal(TimeSpan.FromSeconds(33), result.Config.RequestTimeout);
        Assert.Equal(6, result.Config.MaximumAttempts);
        Assert.All(result.Origins.Values, origin => Assert.Equal("command line", origin));
    }

    [Fact]
    public void EnvironmentWinsOverFileForEverySetting()
    {
        WriteConfig("""
            { "OllamaUrl": "http://localhost:11431", "ModelTag": "file:model",
              "DataDirectory": "file-data", "RequestTimeoutSeconds": 31, "MaximumAttempts": 4 }
            """);
        var result = Load(environment: CompleteEnvironment());

        Assert.Equal(new Uri("http://localhost:11432"), result.Config.OllamaUrl);
        Assert.Equal("environment:model", result.Config.ModelTag);
        Assert.Equal(Path.Combine(temporaryDirectory, "environment-data"), result.Config.DataDirectory);
        Assert.Equal(TimeSpan.FromSeconds(32), result.Config.RequestTimeout);
        Assert.Equal(5, result.Config.MaximumAttempts);
        Assert.All(result.Origins.Values, origin => Assert.Equal("environment", origin));
    }

    [Fact]
    public void ConfigurationFileOverridesDefaultsWithCaseInsensitiveProperties()
    {
        WriteConfig("""
            { "ollamaUrl": "https://localhost:11431/prefix", "modelTag": "file:model",
              "dataDirectory": "file-data", "requestTimeoutSeconds": 31, "maximumAttempts": 4 }
            """);
        var result = Load();

        Assert.Equal(new Uri("https://localhost:11431/prefix"), result.Config.OllamaUrl);
        Assert.Equal("file:model", result.Config.ModelTag);
        Assert.Equal(Path.Combine(temporaryDirectory, "file-data"), result.Config.DataDirectory);
        Assert.Equal(TimeSpan.FromSeconds(31), result.Config.RequestTimeout);
        Assert.Equal(4, result.Config.MaximumAttempts);
        Assert.All(result.Origins.Values, origin => Assert.Equal("config file", origin));
    }

    [Fact]
    public void EachSettingRetainsItsOwnOriginAndBlankEnvironmentValuesAreUnset()
    {
        WriteConfig("""{ "modelTag": "file:model", "maximumAttempts": 7 }""");
        var result = Load(new ConfigOverrides { MaximumAttempts = 8 }, new Dictionary<string, string?>
        {
            ["EXHUME_HOME"] = "environment-data",
            ["EXHUME_MODEL"] = " ",
            ["EXHUME_OLLAMA_URL"] = null
        });

        Assert.Equal("default", result.Origins["OllamaUrl"]);
        Assert.Equal("config file", result.Origins["ModelTag"]);
        Assert.Equal("environment", result.Origins["DataDirectory"]);
        Assert.Equal("default", result.Origins["RequestTimeout"]);
        Assert.Equal("command line", result.Origins["MaximumAttempts"]);
        Assert.Equal("file:model", result.Config.ModelTag);
        Assert.Equal(8, result.Config.MaximumAttempts);
    }

    [Theory]
    [InlineData("EXHUME_TIMEOUT_SECONDS", "0")]
    [InlineData("EXHUME_TIMEOUT_SECONDS", "-1")]
    [InlineData("EXHUME_TIMEOUT_SECONDS", "not-a-number")]
    [InlineData("EXHUME_TIMEOUT_SECONDS", "2147483648")]
    [InlineData("EXHUME_MAX_ATTEMPTS", "0")]
    [InlineData("EXHUME_MAX_ATTEMPTS", "-2")]
    [InlineData("EXHUME_MAX_ATTEMPTS", "1.5")]
    public void InvalidEnvironmentNumbersAreRejectedWithoutEchoingValues(string name, string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => Load(environment:
            new Dictionary<string, string?> { [name] = value }));

        Assert.Contains(name, exception.Message);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    [InlineData(120, 0)]
    [InlineData(120, -1)]
    public void NonpositiveCommandLineNumbersAreRejected(int seconds, int attempts)
    {
        Assert.Throws<ArgumentException>(() => Load(new ConfigOverrides
        {
            RequestTimeoutSeconds = seconds,
            MaximumAttempts = attempts
        }));
    }

    [Theory]
    [InlineData("file:///D:/model")]
    [InlineData("not a URI")]
    [InlineData("http://owner:secret@localhost:11434")]
    [InlineData("http://localhost:11434?secret=value")]
    [InlineData("http://localhost:11434#secret")]
    public void InvalidEndpointIsRejectedWithoutEchoingItsContents(string endpoint)
    {
        var exception = Assert.Throws<ArgumentException>(() => Load(new ConfigOverrides { OllamaUrl = endpoint }));

        Assert.Contains("Ollama", exception.Message);
        Assert.DoesNotContain(endpoint, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("model\nname")]
    public void EmptyOrControlCharacterModelTagsAreRejected(string modelTag)
    {
        Assert.Throws<ArgumentException>(() => Load(new ConfigOverrides { ModelTag = modelTag }));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"maximumAttempts\":\"secret\"}")]
    public void MalformedConfigurationHasSafeActionableError(string json)
    {
        WriteConfig(json);

        var exception = Assert.Throws<InvalidDataException>(() => Load());

        Assert.Contains("configuration", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HigherPriorityNumericValueShadowsMalformedEnvironmentValue()
    {
        var result = Load(new ConfigOverrides { MaximumAttempts = 2 },
            new Dictionary<string, string?> { ["EXHUME_MAX_ATTEMPTS"] = "invalid" });

        Assert.Equal(2, result.Config.MaximumAttempts);
    }

    [Fact]
    public void CDriveDataDirectoryIsRejectedBeforeAnyDirectoryIsCreated()
    {
        var exception = Assert.Throws<DataDirectoryAccessException>(() => Load(new ConfigOverrides
        {
            DataDirectory = @"C:\exhume-policy-test"
        }));

        Assert.Contains("EXHUME_HOME", exception.Message);
        Assert.Equal(@"C:\exhume-policy-test", exception.RequestedPath);
    }

    [Fact]
    public void TimeoutBeyondTimerRangeIsRejectedDuringConfigurationLoading()
    {
        Assert.Throws<ArgumentException>(() => Load(new ConfigOverrides
        {
            RequestTimeoutSeconds = int.MaxValue
        }));
        Assert.False(Directory.Exists(Path.Combine(temporaryDirectory, ".exhume")));
    }

    private ConfigurationResult Load(
        ConfigOverrides? overrides = null,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        ConfigurationLoader.Load(overrides, environment ?? new Dictionary<string, string?>(),
            Path.Combine(temporaryDirectory, "exhume.json"), temporaryDirectory);

    private void WriteConfig(string content) => File.WriteAllText(Path.Combine(temporaryDirectory, "exhume.json"), content);

    private static Dictionary<string, string?> CompleteEnvironment() => new()
    {
        ["EXHUME_OLLAMA_URL"] = "http://localhost:11432",
        ["EXHUME_MODEL"] = "environment:model",
        ["EXHUME_HOME"] = "environment-data",
        ["EXHUME_TIMEOUT_SECONDS"] = "32",
        ["EXHUME_MAX_ATTEMPTS"] = "5"
    };

    private static string CreateTemporaryDirectory()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        if (Path.GetPathRoot(root)?.StartsWith("C:", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("Configure TEMP and TMP to a project-local directory off C: before testing.");
        }

        return Directory.CreateDirectory(Path.Combine(root, "exhume-config-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose() => Directory.Delete(temporaryDirectory, recursive: true);
}
