using System.Globalization;
using System.Text;
using Spectre.Console;

namespace Exhume.Cli;

internal static class ConsoleText
{
    public static void WriteLine(IAnsiConsole console, string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                escaped.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(character);
            }
        }
        escaped.AppendLine();
        console.Write(new Text(escaped.ToString()));
    }
}
