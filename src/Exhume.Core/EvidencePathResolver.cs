using System.Runtime.InteropServices;

namespace Exhume.Core;

public static class EvidencePathResolver
{
    public static string Normalize(string path) => Normalize(path, ReadDriveTarget, ReadLinkTarget);

    internal static string Normalize(string path, Func<string, string?> readDriveTarget,
        Func<string, string?> readLinkTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Resolve(path, 0, readDriveTarget, readLinkTarget);
    }

    private static string Resolve(string path, int followedLinks, Func<string, string?> readDriveTarget,
        Func<string, string?> readLinkTarget)
    {
        if (followedLinks > 64) throw new IOException("The evidence path contains a filesystem link or drive-alias cycle.");
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(OrdinaryPath(path)));
        if (OperatingSystem.IsWindows() && (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal)))
            throw new IOException("Evidence paths must use a local filesystem drive.");
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException("The evidence path has no filesystem root.");
        if (OperatingSystem.IsWindows() && root.Length >= 2 && root[1] == ':')
        {
            var target = readDriveTarget(root[..2].ToUpperInvariant());
            if (string.IsNullOrEmpty(target)) throw new IOException("The evidence drive mapping could not be read.");
            if (target.StartsWith(@"\??\", StringComparison.Ordinal) || target.StartsWith(@"\\?\", StringComparison.Ordinal))
                return Resolve(Path.Combine(OrdinaryPath(target), fullPath[root.Length..]), followedLinks + 1, readDriveTarget, readLinkTarget);
            const string volumePrefix = @"\Device\HarddiskVolume";
            if (!target.StartsWith(volumePrefix, StringComparison.OrdinalIgnoreCase) || target.Length == volumePrefix.Length ||
                target.AsSpan(volumePrefix.Length).IndexOfAnyExceptInRange('0', '9') >= 0)
                throw new IOException("Evidence paths cannot use network or unrecognized drive mappings.");
        }
        var parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            var rawTarget = readLinkTarget(current);
            if (rawTarget is null) continue;
            var target = Path.GetFullPath(OrdinaryPath(rawTarget), Path.GetDirectoryName(current)!);
            return Resolve(Path.Combine([target, .. parts.Skip(index + 1)]), followedLinks + 1, readDriveTarget, readLinkTarget);
        }
        return fullPath;
    }

    private static string OrdinaryPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new IOException("The evidence path uses an unsupported device namespace.");
        if (!path.StartsWith(@"\??\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        var plain = path[4..];
        if (plain.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + plain[4..];
        if (plain.Length >= 3 && char.IsAsciiLetter(plain[0]) && plain[1] == ':' && plain[2] is '\\' or '/') return plain;
        throw new IOException("The evidence path uses an unsupported device namespace.");
    }

    private static string? ReadLinkTarget(string path) =>
        File.Exists(path) && !Directory.Exists(path) ? new FileInfo(path).LinkTarget : new DirectoryInfo(path).LinkTarget;

    private static string? ReadDriveTarget(string drive)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var buffer = new char[32768];
        var count = QueryDosDevice(drive, buffer, (uint)buffer.Length);
        if (count == 0) throw new IOException("The evidence drive mapping could not be read.");
        var length = Array.IndexOf(buffer, '\0', 0, (int)count);
        return new string(buffer, 0, length < 0 ? (int)count : length);
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint maximumCharacters);
}
