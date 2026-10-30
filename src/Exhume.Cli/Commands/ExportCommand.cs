using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class ExportCommand(CommandServices services) : AsyncCommand<ExportCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        services.RunExportAsync(settings, cancellationToken);

    public sealed class Settings : ViewSettings
    {
        [CommandOption("--path|--output <PATH>")]
        public string? Path { get; init; }

        [CommandOption("--redact")]
        public bool Redact { get; init; }
    }
}
