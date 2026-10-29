using System.ComponentModel;
using Exhume.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli;

public class SharedSettings : CommandSettings
{
    [CommandOption("--home <DIRECTORY>")]
    [Description("Store application data in this directory.")]
    public string? Home { get; init; }

    [CommandOption("--ollama-url <URL>")]
    [Description("Use this configured model endpoint.")]
    public string? OllamaUrl { get; init; }

    [CommandOption("--model <TAG>")]
    [Description("Use this model tag.")]
    public string? Model { get; init; }

    [CommandOption("--timeout-seconds <SECONDS>")]
    [Description("Bound each model endpoint request by this timeout.")]
    public int? TimeoutSeconds { get; init; }

    [CommandOption("--max-attempts <COUNT>")]
    [Description("Set the maximum narration attempts.")]
    public int? MaximumAttempts { get; init; }

    public override ValidationResult Validate()
    {
        if (TimeoutSeconds is <= 0)
        {
            return ValidationResult.Error("--timeout-seconds must be positive.");
        }
        if (MaximumAttempts is <= 0)
        {
            return ValidationResult.Error("--max-attempts must be positive.");
        }
        return ValidationResult.Success();
    }

    public ConfigOverrides ToOverrides() => new()
    {
        DataDirectory = Home,
        OllamaUrl = OllamaUrl,
        ModelTag = Model,
        RequestTimeoutSeconds = TimeoutSeconds,
        MaximumAttempts = MaximumAttempts,
    };
}

public class ViewSettings : SharedSettings
{
    [CommandOption("--no-model")]
    [Description("Render raw evidence without model calls.")]
    public bool NoModel { get; init; }
}

public abstract class StubCommand<TSettings>(IAnsiConsole console) : Command<TSettings>
    where TSettings : SharedSettings
{
    public override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        console.Write(new Text($"not implemented{Environment.NewLine}"));
        return 0;
    }
}
