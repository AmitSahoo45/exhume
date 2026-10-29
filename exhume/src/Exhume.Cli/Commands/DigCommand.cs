using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class DigCommand(IAnsiConsole console) : StubCommand<DigCommand.Settings>(console)
{
    public sealed class Settings : SharedSettings
    {
        [CommandOption("--source <SOURCE>")]
        public string[] Sources { get; init; } = [];

        [CommandOption("--path <PATH>")]
        public string? Path { get; init; }

        [CommandOption("--author <AUTHOR>")]
        public string? Author { get; init; }

        [CommandOption("--profile <PROFILE>")]
        public string? Profile { get; init; }

        [CommandOption("--cutoff <DATE>")]
        public DateTimeOffset? Cutoff { get; init; }

        [CommandOption("--elevated")]
        public bool Elevated { get; init; }

        [CommandOption("--scan-repos")]
        public bool ScanRepositories { get; init; }
    }
}
