using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class TimelineCommand(IAnsiConsole console) : StubCommand<TimelineCommand.Settings>(console)
{
    public sealed class Settings : ViewSettings
    {
        [CommandOption("--source <SOURCE>")]
        public string? Source { get; init; }

        [CommandOption("--year <YEAR>")]
        public int? Year { get; init; }
    }
}
