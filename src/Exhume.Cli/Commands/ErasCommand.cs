using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class ErasCommand(CommandServices services) : AsyncCommand<ErasCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        services.RunErasAsync(settings, cancellationToken);

    public sealed class Settings : ViewSettings
    {
        [CommandOption("--source <SOURCE>")]
        public string? Source { get; init; }

        [CommandOption("--year <YEAR>")]
        public int? Year { get; init; }
    }
}
