using System.Text;
using Exhume.Adapters.FileSystem;
using Exhume.Core;

namespace Exhume.Adapters.FileSystem.Tests;

public sealed class FileSystemAdapterTests
{
    [Theory]
    [InlineData("picture.JPG", "photo")]
    [InlineData("movie.mkv", "video")]
    [InlineData("song.flac", "audio")]
    [InlineData("document.docx", "document")]
    [InlineData("source.cs", "code")]
    [InlineData("backup.7z", "archive")]
    [InlineData("setup.msi", "installer")]
    [InlineData("unknown.xyz", "other")]
    public async Task Uses_fixed_extension_categories(string filename, string category)
    {
        var reader = new FakeReader { Entries = [Entry(filename)] };
        Assert.Equal("2020-02 " + category, Assert.Single(await Collect(new FileSystemAdapter(reader), reader.Root)).Name);
    }

    [Fact]
    public async Task Groups_months_categories_and_keeps_five_nonempty_filename_samples()
    {
        using var tree = new TemporaryTree();
        for (var i = 0; i < 8; i++) tree.Write($"nested/document-{i}.pdf", "abc", new(2020, 2, 4, 0, 0, 0, TimeSpan.Zero));
        tree.Write("empty.pdf", "", new(2020, 2, 4, 0, 0, 0, TimeSpan.Zero));
        tree.Write("clip.mp4", "video", new(2020, 3, 5, 0, 0, 0, TimeSpan.Zero));
        var artifacts = await Collect(new FileSystemAdapter(), tree.Root);
        Assert.Equal(2, artifacts.Count);
        var documents = Assert.Single(artifacts, a => a.Name == "2020-02 document");
        Assert.Equal("9", documents.Metadata["count"]);
        Assert.Equal("24", documents.Metadata["bytes"]);
        Assert.Equal("mtime", documents.Metadata["dateSource"]);
        var samples = documents.Metadata["samples"].Split(" | ", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, samples.Length);
        Assert.All(samples, sample => Assert.DoesNotContain(Path.DirectorySeparatorChar, sample));
        Assert.DoesNotContain("empty.pdf", samples);
    }

    [Theory]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    [InlineData("node_modules")]
    [InlineData(".git")]
    [InlineData("BIN")]
    [InlineData("obj")]
    public async Task Skips_named_directories_at_every_depth(string excluded)
    {
        using var tree = new TemporaryTree();
        tree.Write($"nested/{excluded}/ignored.txt", "hidden", new(2020, 2, 4, 0, 0, 0, TimeSpan.Zero));
        tree.Write("kept.txt", "keep", new(2020, 2, 4, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("1", Assert.Single(await Collect(new FileSystemAdapter(), tree.Root)).Metadata["count"]);
    }

    [Fact]
    public async Task Exif_capture_date_takes_priority_over_copy_timestamps()
    {
        using var tree = new TemporaryTree();
        var path = tree.Write("photo.jpg", "", new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        File.WriteAllBytes(path, ExifJpeg("2017:08:09 10:11:12"));
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var artifact = Assert.Single(await Collect(new FileSystemAdapter(), tree.Root));
        Assert.Equal("2017-08 photo", artifact.Name);
        Assert.Equal("exif", artifact.Metadata["dateSource"]);
    }

    [Theory]
    [InlineData("2017:08:31 23:30:00", "-02:00", "2017-09 photo")]
    [InlineData("2017:09:01 00:30:00", "+02:00", "2017-08 photo")]
    public async Task Exif_offset_is_applied_before_UTC_month_assignment(string date, string offset, string expected)
    {
        using var tree = new TemporaryTree();
        var path = tree.Write("offset.jpg", "", new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        File.WriteAllBytes(path, ExifJpeg(date, offset));
        Assert.Equal(expected, Assert.Single(await Collect(new FileSystemAdapter(), tree.Root)).Name);
    }

    [Fact]
    public async Task Invalid_dates_remain_undated_and_creation_is_only_a_missing_mtime_fallback()
    {
        var reader = new FakeReader();
        reader.Entries = [Entry("old.txt", new(1985, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("future.txt", new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Entry("creation.txt") with { LastWriteTime = null, CreationTime = new(2001, 4, 1, 0, 0, 0, TimeSpan.Zero) }];
        var artifacts = await Collect(new FileSystemAdapter(reader), reader.Root);
        var undated = Assert.Single(artifacts, a => a.Name == "undated document");
        Assert.Null(undated.FirstSeen);
        Assert.Null(undated.LastSeen);
        Assert.Equal("2", undated.Metadata["count"]);
        Assert.Equal("ctime", Assert.Single(artifacts, a => a.Name == "2001-04 document").Metadata["dateSource"]);
    }

    [Fact]
    public async Task Counts_links_without_following_or_sampling_and_skips_hidden_system_entries()
    {
        var reader = new FakeReader
        {
            Entries = [Entry("shortcut.txt") with { Attributes = FileAttributes.ReparsePoint },
                Entry("loop") with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint },
                Entry("hidden.txt") with { Attributes = FileAttributes.Hidden },
                Entry("system.txt") with { Attributes = FileAttributes.System }]
        };
        var artifacts = await Collect(new FileSystemAdapter(reader), reader.Root);
        Assert.Equal(2, artifacts.Sum(a => int.Parse(a.Metadata["count"])));
        Assert.All(artifacts, a => Assert.Equal("", a.Metadata["samples"]));
        Assert.All(artifacts, a => Assert.Equal("0", a.Metadata["bytes"]));
        Assert.Equal(1, reader.Enumerations);
    }

    [Fact]
    public async Task Skips_failed_directory_reports_sanitized_error_and_finishes_with_count()
    {
        var reader = new FakeReader { Entries = [Entry("denied") with { Attributes = FileAttributes.Directory },
            Entry("private.txt") with { Error = "File metadata was unavailable." }, Entry("kept.txt")] };
        var progress = new RecordingProgress();
        var artifacts = await Collect(new FileSystemAdapter(reader), reader.Root, progress);
        Assert.Single(artifacts);
        Assert.Contains(progress.Values, p => p.Error is not null && !p.Error.Contains("private", StringComparison.Ordinal));
        Assert.Equal("complete", progress.Values[^1].Stage);
        Assert.Equal(1, progress.Values[^1].ItemsDone);
    }

    [Fact]
    public async Task Canonical_root_keeps_identity_and_cancellation_propagates()
    {
        using var tree = new TemporaryTree();
        tree.Write("one.cs", "class X {}", new(2020, 2, 4, 0, 0, 0, TimeSpan.Zero));
        var first = Assert.Single(await Collect(new FileSystemAdapter(), tree.Root));
        var second = Assert.Single(await Collect(new FileSystemAdapter(), Path.Combine(tree.Root, ".")));
        Assert.Equal(first.Id, second.Id);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new FileSystemAdapter().ScanAsync(new() { Path = tree.Root }, cancellationToken: cancelled.Token)) { }
        });
    }

    [Fact]
    public void Availability_requires_a_readable_existing_root()
    {
        var adapter = new FileSystemAdapter(new FakeReader());
        Assert.Equal(SourceAvailability.Unavailable, adapter.Check(new()).Availability);
        Assert.Equal(SourceAvailability.Unavailable, adapter.Check(new() { Path = "missing" }).Availability);
        Assert.Equal(SourceAvailability.Available, adapter.Check(new() { Path = FakeReader.Folder }).Availability);
    }

    private static FileEntry Entry(string name, DateTimeOffset? date = null) => new(Path.Combine(FakeReader.Folder, name),
        FileAttributes.Normal, 4, date ?? new DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero), null);

    private static async Task<List<Artifact>> Collect(IEvidenceAdapter adapter, string root, IProgress<ScanProgress>? progress = null)
    {
        var artifacts = new List<Artifact>();
        await foreach (var artifact in adapter.ScanAsync(new() { Path = root }, progress)) artifacts.Add(artifact);
        return artifacts;
    }

    private static byte[] ExifJpeg(string captureDate, string? offset = null)
    {
        using var tiff = new MemoryStream();
        using (var writer = new BinaryWriter(tiff, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("II")); writer.Write((ushort)42); writer.Write(8U);
            writer.Write((ushort)1); writer.Write((ushort)0x8769); writer.Write((ushort)4); writer.Write(1U); writer.Write(26U); writer.Write(0U);
            var dataOffset = offset is null ? 44U : 56U;
            writer.Write((ushort)(offset is null ? 1 : 2)); writer.Write((ushort)0x9003); writer.Write((ushort)2); writer.Write(20U); writer.Write(dataOffset);
            if (offset is not null) { writer.Write((ushort)0x9011); writer.Write((ushort)2); writer.Write(7U); writer.Write(dataOffset + 20); }
            writer.Write(0U);
            writer.Write(Encoding.ASCII.GetBytes(captureDate + '\0'));
            if (offset is not null) writer.Write(Encoding.ASCII.GetBytes(offset + '\0'));
        }
        using var jpeg = new MemoryStream();
        jpeg.Write([0xff, 0xd8, 0xff, 0xe1]);
        var length = (int)tiff.Length + 8;
        jpeg.WriteByte((byte)(length >> 8)); jpeg.WriteByte((byte)length);
        jpeg.Write(Encoding.ASCII.GetBytes("Exif\0\0")); tiff.Position = 0; tiff.CopyTo(jpeg);
        jpeg.Write([0xff, 0xd9]);
        return jpeg.ToArray();
    }

    private sealed class FakeReader : IFileSystemReader
    {
        public static string Folder => Path.Combine(Path.GetTempPath(), "exhume-virtual-tree");
        public string Root => Folder;
        public List<FileEntry> Entries { get; set; } = [];
        public int Enumerations { get; private set; }
        public bool DirectoryExists(string path) => path == Folder;
        public IEnumerable<FileEntry> EnumerateDirectory(string path)
        {
            Enumerations++;
            if (path != Folder) throw new UnauthorizedAccessException("private path");
            return Entries;
        }
        public DateTimeOffset? ReadExifDate(string path) => null;
    }

    private sealed class RecordingProgress : IProgress<ScanProgress>
    {
        public List<ScanProgress> Values { get; } = [];
        public void Report(ScanProgress value) => Values.Add(value);
    }

    private sealed class TemporaryTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "exhume-folder-" + Guid.NewGuid().ToString("N"));
        public TemporaryTree()
        {
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(Root)), @"C:\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Set TEMP and TMP to a project-local temporary directory off C: before testing.");
            Directory.CreateDirectory(Root);
        }
        public string Write(string relative, string content, DateTimeOffset timestamp)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
