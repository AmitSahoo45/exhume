using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class ObituariesCommand(IAnsiConsole console) : StubCommand<ObituariesCommand.Settings>(console)
{
    public sealed class Settings : ViewSettings
    {
        [CommandOption("--limit <COUNT>")]
        public int? Limit { get; init; }
    }
}
