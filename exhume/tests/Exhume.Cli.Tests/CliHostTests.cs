using System.Net;
using Exhume.Core;
using Spectre.Console;

namespace Exhume.Cli.Tests;

public sealed class CliHostTests
{
    [Fact]
    public async Task Doctor_creates_an_empty_store_and_succeeds_when_the_model_is_unreachable()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => throw new HttpRequestException("Connection refused"));
        var host = CreateHost(directory, output, handler);

        var code = await host.RunAsync(["doctor"]);

        Assert.Equal(0, code);
        Assert.Contains("unreachable", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Artifacts: 0", output.ToString());
        Assert.Contains("Narrations: 0", output.ToString());
        Assert.Contains("Scans: 0", output.ToString());
        Assert.Contains("Narration calls: 0", output.ToString());
        Assert.True(Directory.Exists(Path.Combine(directory.Path, ".exhume")));
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("http://localhost:11434/api/tags", handler.Requests[0].Uri);
        foreach (var source in new[] { "windows", "browser", "folder", "git" })
        {
            Assert.Contains(source, output.ToString());
        }
        Assert.Contains("not implemented", output.ToString());
    }

    [Fact]
    public async Task Doctor_uses_the_configured_endpoint_and_reports_exact_model_tag_presence()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json("""{"models":[{"name":"test:model","model":"test:model"}]}"""));
        var host = CreateHost(directory, output, handler);

        var code = await host.RunAsync(["doctor", "--ollama-url", "http://localhost:19191/custom/", "--model", "test:model"]);

        Assert.Equal(0, code);
        Assert.Contains("Model tag: present", output.ToString());
        Assert.Contains("command line", output.ToString());
        Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:19191/custom/api/tags", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task Doctor_reports_a_missing_tag_without_failing()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json("""{"models":[{"name":"other:model"}]}"""));

        var code = await CreateHost(directory, output, handler).RunAsync(["doctor", "--model", "test:model"]);

        Assert.Equal(0, code);
        Assert.Contains("Model tag: absent", output.ToString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"models\":null}")]
    public async Task Doctor_reports_an_invalid_model_listing_without_failing(string body)
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json(body));

        var code = await CreateHost(directory, output, handler).RunAsync(["doctor"]);

        Assert.Equal(0, code);
        Assert.Contains("Model tag: unknown", output.ToString());
        Assert.Contains("invalid", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_does_not_follow_a_redirect_response()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://localhost:19192/elsewhere") },
        });

        var code = await CreateHost(directory, output, handler).RunAsync(["doctor"]);

        Assert.Equal(0, code);
        Assert.Contains("302", output.ToString());
        Assert.Contains("Model tag: unknown", output.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Doctor_bounds_the_model_listing_response()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json(new string(' ', 1_048_577)));

        var code = await CreateHost(directory, output, handler).RunAsync(["doctor"]);

        Assert.Equal(0, code);
        Assert.Contains("too large", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_bounds_an_unresponsive_endpoint_by_the_configured_timeout()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new WaitingHandler();

        var code = await CreateHost(directory, output, handler).RunAsync(["doctor", "--timeout-seconds", "1"]);

        Assert.Equal(0, code);
        Assert.Contains("timed out", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_checks_supplied_sources_without_scanning_them()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json("{\"models\":[]}"));
        var host = new CliHost(
            CreateConsole(output), handler,
            adapters: [new FakeAdapter("folder", new(SourceAvailability.Unavailable, "[red]missing fixture[/]")),
                new FakeAdapter("windows", new(SourceAvailability.NeedsAdmin, "elevated source not enabled"))],
            environment: new Dictionary<string, string?>(), workingDirectory: directory.Path,
            configFilePath: Path.Combine(directory.Path, "absent.json"));

        var code = await host.RunAsync(["doctor"]);

        Assert.Equal(0, code);
        Assert.Contains("[red]missing fixture[/]", output.ToString());
        Assert.Contains("NeedsAdmin", output.ToString());
    }

    [Fact]
    public async Task Doctor_renders_source_control_characters_as_visible_text()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json("{\"models\":[]}"));
        var host = new CliHost(CreateConsole(output), handler,
            adapters: [new FakeAdapter("folder", new(SourceAvailability.Unavailable, "\u001b[2Jstatus"))],
            environment: new Dictionary<string, string?>(), workingDirectory: directory.Path,
            configFilePath: Path.Combine(directory.Path, "absent.json"));

        var code = await host.RunAsync(["doctor"]);

        Assert.Equal(0, code);
        Assert.Contains("\\u001b[2Jstatus", output.ToString());
        Assert.True(!output.ToString().Contains('\u001b'), output.ToString().Replace("\u001b", "<ESC>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Doctor_prints_untrusted_configuration_as_literal_text()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json("{\"models\":[]}"));

        var code = await CreateHost(directory, output, handler).RunAsync(["doctor", "--model", "[red]model[/]"]);

        Assert.Equal(0, code);
        Assert.Contains("[red]model[/]", output.ToString());
    }

    [Fact]
    public async Task Doctor_reports_unwritable_storage_as_exit_three_with_override_guidance()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => Json("{\"models\":[]}"));
        var home = Path.Combine(directory.Path, "existing-file");
        File.WriteAllText(home, "occupied");

        var code = await CreateHost(directory, output, handler).RunAsync(["doctor", "--home", home]);

        Assert.Equal(3, code);
        Assert.Contains(home, output.ToString());
        Assert.Contains("EXHUME_HOME", output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not-a-command")]
    [InlineData("doctor", "--not-an-option")]
    [InlineData("doctor", "--timeout-seconds", "0")]
    public async Task Invalid_arguments_return_usage_error(params string[] arguments)
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => throw new InvalidOperationException("HTTP must not run"));

        var code = await CreateHost(directory, output, handler).RunAsync(arguments);

        Assert.Equal(1, code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Malformed_configuration_returns_usage_error_without_contacting_the_model()
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => throw new InvalidOperationException("HTTP must not run"));
        var configurationPath = Path.Combine(directory.Path, "invalid.json");
        File.WriteAllText(configurationPath, "{");
        var host = new CliHost(CreateConsole(output), handler,
            environment: new Dictionary<string, string?>(), workingDirectory: directory.Path,
            configFilePath: configurationPath);

        var code = await host.RunAsync(["doctor"]);

        Assert.Equal(1, code);
        Assert.Contains("configuration", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("dig", "--source", "folder", "--path", "fixture")]
    [InlineData("timeline", "--no-model", "--source", "folder", "--year", "2024")]
    [InlineData("obituaries", "--no-model", "--limit", "5")]
    [InlineData("eras", "--no-model", "--source", "folder", "--year", "2024")]
    [InlineData("haunt", "--no-model", "--path", "fixture", "--author", "owner")]
    [InlineData("export", "--no-model", "--path", "report.md", "--redact")]
    [InlineData("forget")]
    public async Task Future_commands_are_safe_stubs_without_model_or_storage_work(params string[] arguments)
    {
        using var directory = new TestDirectory();
        using var output = new StringWriter();
        using var handler = new ResponseHandler(_ => throw new InvalidOperationException("HTTP must not run"));

        var code = await CreateHost(directory, output, handler).RunAsync(arguments);

        Assert.Equal(0, code);
        Assert.Contains("not implemented", output.ToString());
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, ".exhume")));
    }

    private static CliHost CreateHost(TestDirectory directory, StringWriter output, HttpMessageHandler handler) => new(
        CreateConsole(output),
        handler,
        environment: new Dictionary<string, string?>(),
        workingDirectory: directory.Path,
        configFilePath: Path.Combine(directory.Path, "absent.json"));

    private static IAnsiConsole CreateConsole(StringWriter output)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = 240;
        return console;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request timeout must cancel this handler.");
        }
    }

    private sealed class FakeAdapter(string name, SourceStatus status) : IEvidenceAdapter
    {
        public string Name => name;

        public SourceStatus Check(ScanOptions options) => status;

        public IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Doctor must not scan.");
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "exhume-cli-" + Guid.NewGuid().ToString("N"));
            if (string.Equals(System.IO.Path.GetPathRoot(Path), "C:\\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Set TEMP and TMP to the project-local temporary directory before testing.");
            }
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
