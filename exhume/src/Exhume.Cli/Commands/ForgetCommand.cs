using Spectre.Console;

namespace Exhume.Cli.Commands;

public sealed class ForgetCommand(IAnsiConsole console) : StubCommand<SharedSettings>(console);
