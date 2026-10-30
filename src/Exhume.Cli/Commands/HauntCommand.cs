using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class HauntCommand(CommandServices services) : AsyncCommand<HauntCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        services.RunHauntAsync(settings, cancellationToken);

    public sealed class Settings : ViewSettings
    {
        [CommandOption("--path|--repo <PATH>")]
        public string? Path { get; init; }

        [CommandOption("--author <AUTHOR>")]
        public string? Author { get; init; }

        [CommandOption("--cutoff <DATE>")]
        public DateTimeOffset? Cutoff { get; init; }

        [CommandOption("--report <NAME>")]
        public string? Report { get; init; }

        [CommandOption("--include-untracked")]
        public bool IncludeUntracked { get; init; }
    }
}
