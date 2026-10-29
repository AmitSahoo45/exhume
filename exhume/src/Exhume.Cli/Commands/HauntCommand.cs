using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class HauntCommand(IAnsiConsole console) : StubCommand<HauntCommand.Settings>(console)
{
    public sealed class Settings : ViewSettings
    {
        [CommandOption("--path|--repo <PATH>")]
        public string? Path { get; init; }

        [CommandOption("--author <AUTHOR>")]
        public string? Author { get; init; }

        [CommandOption("--cutoff <DATE>")]
        public DateTimeOffset? Cutoff { get; init; }
    }
}
