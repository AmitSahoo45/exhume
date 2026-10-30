using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class ForgetCommand(CommandServices services) : AsyncCommand<ForgetCommand.Settings>
{
    public override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken) =>
        services.RunForgetAsync(settings, cancellationToken);

    public sealed class Settings : SharedSettings
    {
        [CommandOption("--yes")]
        public bool Yes { get; init; }
    }
}
