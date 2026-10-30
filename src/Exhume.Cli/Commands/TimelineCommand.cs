using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class TimelineCommand(CommandServices services) : AsyncCommand<TimelineCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        services.RunTimelineAsync(settings, cancellationToken);

    public sealed class Settings : ViewSettings
    {
        [CommandOption("--month <YYYY-MM>")]
        public string? Month { get; init; }

        [CommandOption("--source <SOURCE>")]
        public string? Source { get; init; }

        [CommandOption("--year <YEAR>")]
        public int? Year { get; init; }
    }
}
