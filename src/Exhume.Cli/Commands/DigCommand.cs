using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class DigCommand(CommandServices services) : AsyncCommand<DigCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        services.RunDigAsync(settings, cancellationToken);

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
