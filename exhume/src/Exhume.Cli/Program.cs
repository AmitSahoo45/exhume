using Exhume.Cli;
using Spectre.Console;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
AnsiConsole.Profile.Width = Math.Min(100, AnsiConsole.Profile.Width);
return await new CliHost(AnsiConsole.Console).RunAsync(args, cancellation.Token);
