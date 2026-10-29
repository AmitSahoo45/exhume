using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class DoctorCommand(CommandServices services) : AsyncCommand<SharedSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, SharedSettings settings, CancellationToken cancellationToken) =>
        services.RunDoctorAsync(settings, cancellationToken);
}
