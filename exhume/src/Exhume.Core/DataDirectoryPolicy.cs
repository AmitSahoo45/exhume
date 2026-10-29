using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Exhume.Core.Tests")]

namespace Exhume.Core;

public static class DataDirectoryPolicy
{
    private const string Guidance = "Choose a local data directory off C: using EXHUME_HOME.";

    public static string Normalize(string path, string? workingDirectory = null) =>
        Normalize(path, workingDirectory, ReadDriveTarget, ReadLinkTarget);

    internal static string Normalize(string path, string? workingDirectory,
        Func<string, string?> readDriveTarget, Func<string, string?>? readLinkTarget = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw InvalidDirectory("The data directory must not be empty.");
        }

        RejectUnsupportedPath(path);
        try
        {
            var baseDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
            var fullPath = Path.GetFullPath(path, baseDirectory);
            RejectRoot(fullPath);
            var result = Resolve(fullPath, 0, readDriveTarget, readLinkTarget ?? ReadLinkTarget);
            RejectRoot(result);
            return result;
        }
        catch (IOException)
        {
            throw new IOException($"The data directory could not be safely resolved. {Guidance}");
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"The data directory could not be read. {Guidance}");
        }
        catch (NotSupportedException)
        {
            throw InvalidDirectory("The data directory could not be safely resolved.");
        }
    }

    private static string Resolve(string path, int followedLinks,
        Func<string, string?> readDriveTarget, Func<string, string?> readLinkTarget)
    {
        RejectUnsupportedPath(path);
        if (followedLinks > 64)
        {
            throw InvalidDirectory("The data directory contains a filesystem link or drive-alias cycle.");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw InvalidDirectory("The data directory must have an absolute local root.");
        }

        if (OperatingSystem.IsWindows())
        {
            var device = readDriveTarget(root[..2].ToUpperInvariant());
            if (string.IsNullOrEmpty(device))
            {
                throw new IOException($"The data-directory drive could not be identified. {Guidance}");
            }

            if (device.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                var target = NormalizeLinkTarget(device, root);
                return Resolve(Path.Combine(target, fullPath[root.Length..]), followedLinks + 1,
                    readDriveTarget, readLinkTarget);
            }

            if (!IsLocalVolume(device))
            {
                throw InvalidDirectory("Network or unrecognized device mappings cannot be used as the data directory.");
            }

            var systemDevice = readDriveTarget("C:");
            if (string.IsNullOrEmpty(systemDevice))
            {
                throw new IOException($"The C: volume could not be identified for data-directory validation. {Guidance}");
            }

            if (string.Equals(device, systemDevice, StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidDirectory("The data-directory drive maps to the C: volume.");
            }
        }

        var parts = fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            var rawTarget = readLinkTarget(current);
            if (rawTarget is not null)
            {
                var target = NormalizeLinkTarget(rawTarget, Path.GetDirectoryName(current)!);
                var resolvedTarget = Resolve(target, followedLinks + 1, readDriveTarget, readLinkTarget);
                if (!Directory.Exists(resolvedTarget))
                {
                    throw InvalidDirectory("The data directory contains an unresolved filesystem link.");
                }

                var remainder = Path.Combine(parts[(index + 1)..]);
                var resolvedPath = string.IsNullOrEmpty(remainder)
                    ? resolvedTarget
                    : Path.Combine(resolvedTarget, remainder);
                return Resolve(resolvedPath, followedLinks + 1, readDriveTarget, readLinkTarget);
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw InvalidDirectory("The data directory contains an unsupported filesystem link.");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException($"The data directory or one of its parents is an existing file. {Guidance}");
            }
        }

        return fullPath;
    }

    private static string NormalizeLinkTarget(string target, string containingDirectory)
    {
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)
            || target.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            target = target[4..];
            if (target.Length < 3 || !char.IsAsciiLetter(target[0]) || target[1] != ':'
                || (target[2] != '\\' && target[2] != '/'))
            {
                throw InvalidDirectory("The data directory contains an unsupported filesystem target.");
            }
        }

        RejectUnsupportedPath(target);
        return Path.GetFullPath(target, containingDirectory);
    }

    private static bool IsLocalVolume(string device)
    {
        const string prefix = @"\Device\HarddiskVolume";
        return device.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && device.Length > prefix.Length
            && device.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static void RejectRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null
            && string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), PathComparison))
        {
            throw InvalidDirectory("A filesystem root cannot be the data directory.");
        }
    }

    private static string? ReadLinkTarget(string path) => new DirectoryInfo(path).LinkTarget;

    private static string? ReadDriveTarget(string drive)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[32768];
        var count = QueryDosDevice(drive, buffer, (uint)buffer.Length);
        if (count == 0)
        {
            throw new IOException($"The data-directory drive mapping could not be read. {Guidance}");
        }

        var length = Array.IndexOf(buffer, '\0', 0, (int)count);
        return new string(buffer, 0, length < 0 ? (int)count : length);
    }

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint maximumCharacters);

    private static void RejectUnsupportedPath(string path)
    {
        if (path.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidDirectory("The data directory cannot be on C:.");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw InvalidDirectory("UNC and device-namespace data directories are unsupported.");
        }
    }

    private static ArgumentException InvalidDirectory(string message) => new($"{message} {Guidance}");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
