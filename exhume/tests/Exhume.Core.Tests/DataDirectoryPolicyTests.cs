using System.Diagnostics;
using Exhume.Core;

namespace Exhume.Core.Tests;

public sealed class DataDirectoryPolicyTests : IDisposable
{
    private readonly string temporaryDirectory = CreateTemporaryDirectory();
    private readonly List<string> junctions = [];

    [Fact]
    public void RelativeDataDirectoryIsCanonicalAndDoesNotCreateAnything()
    {
        var actual = DataDirectoryPolicy.Normalize(Path.Combine("parent", "..", "data"), temporaryDirectory);

        Assert.Equal(Path.Combine(temporaryDirectory, "data"), actual);
        Assert.False(Directory.Exists(actual));
    }

    [Theory]
    [InlineData(@"C:\data")]
    [InlineData(@"c:/data")]
    [InlineData(@"C:data")]
    [InlineData(@"\\?\C:\data")]
    [InlineData(@"\\.\C:\data")]
    public void CDriveAndDevicePathsAreRejected(string path)
    {
        var exception = Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(path, temporaryDirectory));

        Assert.Contains("EXHUME_HOME", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyPathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(path, temporaryDirectory));
    }

    [Fact]
    public void DriveRootCannotBecomeADeletableDataDirectory()
    {
        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(Path.GetPathRoot(temporaryDirectory)!, temporaryDirectory));
    }

    [Fact]
    public void ExistingFileCannotBecomeADataDirectory()
    {
        var file = Path.Combine(temporaryDirectory, "occupied");
        File.WriteAllText(file, "fixture");

        Assert.Throws<IOException>(() => DataDirectoryPolicy.Normalize(file, temporaryDirectory));
    }

    [Fact]
    public void ExistingParentJunctionToCDriveIsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var link = CreateJunction("c-drive-link", @"C:\");

        var exception = Assert.Throws<ArgumentException>(() =>
            DataDirectoryPolicy.Normalize(Path.Combine(link, "uncreated", "data"), temporaryDirectory));

        Assert.Contains("EXHUME_HOME", exception.Message);
    }

    [Fact]
    public void ExistingSafeJunctionIsResolvedBeforeReturningDataDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Directory.CreateDirectory(Path.Combine(temporaryDirectory, "target")).FullName;
        var link = CreateJunction("safe-link", target);

        var actual = DataDirectoryPolicy.Normalize(Path.Combine(link, "data"), temporaryDirectory);

        Assert.Equal(Path.Combine(target, "data"), actual);
        Assert.False(Directory.Exists(actual));
    }

    [Theory]
    [InlineData(@"\\server\share\data")]
    [InlineData("//server/share/data")]
    public void DirectUncDataDirectoriesAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(path, temporaryDirectory));
    }

    [Theory]
    [InlineData(@"\Device\Mup\server\share")]
    [InlineData(@"\Device\LanmanRedirector\;Z:000000\server\share")]
    [InlineData(@"\??\UNC\server\share")]
    [InlineData(@"\Device\UnknownRedirector\server\share")]
    public void MappedNetworkAndUnknownRootsAreRejectedBeforeFilesystemInspection(string target)
    {
        var exception = Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(@"Z:\data",
            temporaryDirectory, drive => drive == "Z:" ? target : LocalDriveTarget(drive), RejectFilesystemProbe));

        Assert.Contains("EXHUME_HOME", exception.Message);
    }

    [Fact]
    public void SubstitutedCDriveIsRejectedBeforeFilesystemInspection()
    {
        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(@"Z:\data", temporaryDirectory,
            drive => drive == "Z:" ? @"\??\C:\archive" : LocalDriveTarget(drive), RejectFilesystemProbe));
    }

    [Fact]
    public void ChainedSubstitutionToCDriveIsRejectedBeforeFilesystemInspection()
    {
        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(@"Z:\data", temporaryDirectory,
            drive => drive switch
            {
                "Z:" => @"\??\Y:\archive",
                "Y:" => @"\??\C:\nested",
                _ => LocalDriveTarget(drive)
            }, RejectFilesystemProbe));
    }

    [Fact]
    public void AnotherDriveLetterForCVolumeIsRejectedBeforeFilesystemInspection()
    {
        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(@"Z:\data", temporaryDirectory,
            drive => drive == "Z:" ? LocalDriveTarget("C:") : LocalDriveTarget(drive), RejectFilesystemProbe));
    }

    [Fact]
    public void SubstitutionCycleIsRejectedBeforeFilesystemInspection()
    {
        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(@"Z:\data", temporaryDirectory,
            drive => drive switch
            {
                "Z:" => @"\??\Y:\archive",
                "Y:" => @"\??\Z:\nested",
                _ => LocalDriveTarget(drive)
            }, RejectFilesystemProbe));
    }

    [Fact]
    public void SafeSubstitutedDriveReturnsItsLocalPhysicalPath()
    {
        var target = Directory.CreateDirectory(Path.Combine(temporaryDirectory, "substitution-target")).FullName;

        var actual = DataDirectoryPolicy.Normalize(@"Z:\data", temporaryDirectory,
            drive => drive == "Z:" ? @"\??\" + target : LocalDriveTarget(drive), ReadOnlyLocalLinkTarget);

        Assert.Equal(Path.Combine(target, "data"), actual);
        Assert.False(Directory.Exists(actual));
    }

    [Theory]
    [InlineData(@"\\server\share\data")]
    [InlineData(@"\??\UNC\server\share\data")]
    [InlineData(@"\\?\UNC\server\share\data")]
    [InlineData(@"Z:\data")]
    public void UnsafeRawLinkTargetsAreRejectedBeforeFollowingThem(string rawTarget)
    {
        var link = Path.Combine(temporaryDirectory, "simulated-link");

        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(Path.Combine(link, "data"), temporaryDirectory,
            drive => drive == "Z:" ? @"\Device\Mup\server\share" : LocalDriveTarget(drive),
            path => path == link ? rawTarget : ReadOnlyLocalLinkTarget(path)));
    }

    [Fact]
    public void EveryLocalLinkHopIsValidatedBeforeFollowingAnUnsafeTarget()
    {
        var first = Path.Combine(temporaryDirectory, "first-link");
        var second = Path.Combine(temporaryDirectory, "second-link");

        Assert.Throws<ArgumentException>(() => DataDirectoryPolicy.Normalize(Path.Combine(first, "data"), temporaryDirectory,
            LocalDriveTarget, path => path == first ? second : path == second ? @"\\server\share" : ReadOnlyLocalLinkTarget(path)));
    }

    [Fact]
    public void RelativeLinkTargetsResolveAgainstTheirContainingDirectory()
    {
        var target = Directory.CreateDirectory(Path.Combine(temporaryDirectory, "relative-target")).FullName;
        var link = Path.Combine(temporaryDirectory, "relative-link");

        var actual = DataDirectoryPolicy.Normalize(Path.Combine(link, "data"), temporaryDirectory,
            LocalDriveTarget, path => path == link ? "relative-target" : ReadOnlyLocalLinkTarget(path));

        Assert.Equal(Path.Combine(target, "data"), actual);
    }

    private static string? LocalDriveTarget(string drive) => drive == "C:"
        ? @"\Device\HarddiskVolume1"
        : @"\Device\HarddiskVolume2";

    private static string? RejectFilesystemProbe(string path) =>
        throw new InvalidOperationException("A rejected drive reached filesystem inspection.");

    private string? ReadOnlyLocalLinkTarget(string path)
    {
        var drive = Path.GetPathRoot(temporaryDirectory);
        if (drive is null || !path.StartsWith(drive, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("An unvalidated target reached filesystem inspection.");
        }

        return new DirectoryInfo(path).LinkTarget;
    }

    private string CreateJunction(string name, string target)
    {
        var link = Path.Combine(temporaryDirectory, name);
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start junction fixture creation.");
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        junctions.Add(link);
        return link;
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        if (Path.GetPathRoot(root)?.StartsWith("C:", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("Configure TEMP and TMP to a project-local directory off C: before testing.");
        }

        return Directory.CreateDirectory(Path.Combine(root, "exhume-policy-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        foreach (var junction in junctions)
        {
            Directory.Delete(junction, recursive: false);
        }

        Directory.Delete(temporaryDirectory, recursive: true);
    }
}
