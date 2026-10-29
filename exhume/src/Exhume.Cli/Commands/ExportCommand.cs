using Spectre.Console;
using Spectre.Console.Cli;

namespace Exhume.Cli.Commands;

public sealed class ExportCommand(IAnsiConsole console) : StubCommand<ExportCommand.Settings>(console)
{
    public sealed class Settings : ViewSettings
    {
        [CommandOption("--path|--output <PATH>")]
        public string? Path { get; init; }

        [CommandOption("--redact")]
        public bool Redact { get; init; }
    }
}
