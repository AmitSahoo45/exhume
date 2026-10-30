namespace Exhume.Core.Tests;

public sealed class EvidencePathResolverTests
{
    [Theory]
    [InlineData(@"Z:\nested", @"\??\D:\repository", @"D:\repository\nested")]
    [InlineData(@"Z:\nested", @"\??\C:\evidence", @"C:\evidence\nested")]
    public void ResolvesDriveAliasesWithoutRejectingReadOnlyEvidenceOnC(string path, string target, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;

        var actual = EvidencePathResolver.Normalize(path,
            drive => drive == "Z:" ? target : @"\Device\HarddiskVolume7", _ => null);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"\\?\D:\repository\nested")]
    [InlineData(@"\??\D:\repository\nested")]
    public void CanonicalizesExtendedDriveSpelling(string path)
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal(@"D:\repository\nested", EvidencePathResolver.Normalize(path, _ => @"\Device\HarddiskVolume7", _ => null));
    }

    [Fact]
    public void ResolvesParentLinksAndRelativeTargets()
    {
        if (!OperatingSystem.IsWindows()) return;

        var actual = EvidencePathResolver.Normalize(@"D:\alias\nested",
            _ => @"\Device\HarddiskVolume7", path => path == @"D:\alias" ? "actual" : null);

        Assert.Equal(@"D:\actual\nested", actual);
    }

    [Fact]
    public void AliasAndLinkCyclesFailWithoutWalkingForever()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Throws<IOException>(() => EvidencePathResolver.Normalize(@"Z:\repository",
            drive => drive == "Z:" ? @"\??\Y:\" : @"\??\Z:\", _ => null));
        Assert.Throws<IOException>(() => EvidencePathResolver.Normalize(@"D:\alias",
            _ => @"\Device\HarddiskVolume7", path => path == @"D:\alias" ? @"D:\again" : @"D:\alias"));
    }

    [Theory]
    [InlineData(@"\Device\LanmanRedirector\server\share")]
    [InlineData(@"\Device\HarddiskVolume7\repository")]
    [InlineData(@"\Device\UnknownDevice")]
    [InlineData(@"\??\UNC\server\share")]
    public void RejectsNonlocalOrUnrecognizedDriveMappingsBeforeReadingLinks(string target)
    {
        if (!OperatingSystem.IsWindows()) return;
        var linkReads = 0;

        Assert.Throws<IOException>(() => EvidencePathResolver.Normalize(@"N:\source", _ => target,
            _ => { linkReads++; return null; }));

        Assert.Equal(0, linkReads);
    }

    [Theory]
    [InlineData(@"\\server\share\repository")]
    [InlineData(@"\\?\UNC\server\share\repository")]
    public void RejectsUncEvidenceBeforeReadingLinks(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var linkReads = 0;

        Assert.Throws<IOException>(() => EvidencePathResolver.Normalize(path, _ => @"\Device\HarddiskVolume7",
            _ => { linkReads++; return null; }));

        Assert.Equal(0, linkReads);
    }

    [Fact]
    public void ResolvesExistingOrdinaryDirectoryWithoutChangingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "exhume-evidence-path-" + Guid.NewGuid().ToString("N"));
        root = DataDirectoryPolicy.Normalize(root);
        Directory.CreateDirectory(root);
        try
        {
            var before = Directory.GetLastWriteTimeUtc(root);
            Assert.Equal(root, EvidencePathResolver.Normalize(root));
            Assert.Equal(before, Directory.GetLastWriteTimeUtc(root));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root); }
    }
}
