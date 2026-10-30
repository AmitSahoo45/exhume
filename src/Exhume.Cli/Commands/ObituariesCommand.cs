using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class ObituariesCommand(CommandServices services) : AsyncCommand<ObituariesCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        services.RunObituariesAsync(settings, cancellationToken);

    public sealed class Settings : ViewSettings
    {
        [CommandOption("--limit <COUNT>")]
        public int? Limit { get; init; }
    }
}
