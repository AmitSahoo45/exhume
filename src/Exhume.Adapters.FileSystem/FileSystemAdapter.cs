using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using Exhume.Core;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Exhume.Adapters.FileSystem;

public sealed record FileEntry(string Path, FileAttributes Attributes, long Length,
    DateTimeOffset? LastWriteTime, DateTimeOffset? CreationTime, string? Error = null);

public interface IFileSystemReader
{
    bool DirectoryExists(string path);
    IEnumerable<FileEntry> EnumerateDirectory(string path);
    DateTimeOffset? ReadExifDate(string path);
}

public sealed class FileSystemAdapter : IEvidenceAdapter
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "$RECYCLE.BIN", "System Volume Information", "node_modules", ".git", "bin", "obj" };
    private static readonly IReadOnlyDictionary<string, string> Categories = BuildCategories();
    private readonly IFileSystemReader reader;
    private readonly TimeProvider timeProvider;

    public FileSystemAdapter() : this(new FileSystemReader()) { }
    public FileSystemAdapter(IFileSystemReader reader, TimeProvider? timeProvider = null)
    {
        this.reader = reader;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => "folder";

    public SourceStatus Check(ScanOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Path)) return new(SourceAvailability.Unavailable, "A folder path is required.");
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Path));
            if (!reader.DirectoryExists(root)) return new(SourceAvailability.Unavailable, "The folder is missing or unreadable.");
            using var entries = reader.EnumerateDirectory(root).GetEnumerator();
            _ = entries.MoveNext();
            return new(SourceAvailability.Available, "The folder is readable.");
        }
        catch (Exception error) when (IsReadError(error))
        {
            return new(SourceAvailability.Unavailable, "The folder is missing or unreadable.");
        }
    }

    public async IAsyncEnumerable<Artifact> ScanAsync(ScanOptions options, IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        string? root = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options.Path)) root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Path));
        }
        catch (Exception error) when (IsReadError(error)) { }
        if (root is null)
        {
            progress?.Report(new(Name, "folder unavailable", 0, Error: "A valid folder path is required."));
            progress?.Report(new(Name, "complete", 0));
            yield break;
        }

        var now = timeProvider.GetUtcNow();
        var buckets = new Dictionary<(string Month, string Category), Bucket>();
        var pending = new Stack<string>();
        pending.Push(root);
        long items = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerator<FileEntry>? entries = null;
            try { entries = reader.EnumerateDirectory(directory).GetEnumerator(); }
            catch (Exception error) when (IsReadError(error)) { ReportReadError("directory skipped"); }
            if (entries is null) continue;
            using (entries)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileEntry? entry = null;
                    try { if (entries.MoveNext()) entry = entries.Current; }
                    catch (Exception error) when (IsReadError(error)) { ReportReadError("directory skipped"); }
                    if (entry is null) break;
                    if (entry.Error is not null) { ReportReadError("file skipped"); continue; }
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                    var isLink = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    if (isDirectory && ExcludedDirectories.Contains(Path.GetFileName(entry.Path))) continue;
                    if (isDirectory && !isLink) { pending.Push(entry.Path); continue; }

                    var category = Categories.GetValueOrDefault(Path.GetExtension(entry.Path), "other");
                    DateTimeOffset? date = null;
                    var source = "mtime";
                    if (category == "photo" && !isLink && entry.Length > 0)
                    {
                        try { date = reader.ReadExifDate(entry.Path); }
                        catch (Exception error) when (IsReadError(error) || error is ImageProcessingException or MetadataException)
                        {
                            progress?.Report(new(Name, "photo metadata unavailable", items,
                                Error: "Photo metadata could not be read; filesystem dates were used."));
                        }
                        if (date is not null) source = "exif";
                    }
                    if (date is null) date = entry.LastWriteTime;
                    if (date is null) { date = entry.CreationTime; source = "ctime"; }
                    date = date?.ToUniversalTime();
                    if (date is not null && (date.Value.Year < 1990 || date > now)) date = null;
                    var month = date?.ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? "undated";
                    var key = (month, category);
                    if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new();
                    bucket.Count++;
                    bucket.Bytes += isLink ? 0 : Math.Max(0, entry.Length);
                    bucket.DateSources[source] = bucket.DateSources.GetValueOrDefault(source) + 1;
                    if (date is not null)
                    {
                        if (bucket.First is null || date < bucket.First) bucket.First = date;
                        if (bucket.Last is null || date > bucket.Last) bucket.Last = date;
                    }
                    if (!isLink && entry.Length > 0)
                    {
                        bucket.Samples.Add(Path.GetFileName(entry.Path));
                        if (bucket.Samples.Count > 5) bucket.Samples.Remove(bucket.Samples.Max!);
                    }
                    items++;
                    if (items % 256 == 0) progress?.Report(new(Name, "files", items));
                }
            }
        }

        var rootKey = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        foreach (var pair in buckets.OrderBy(p => p.Key.Month, StringComparer.Ordinal).ThenBy(p => p.Key.Category, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bucket = pair.Value;
            var dominantDate = bucket.DateSources.OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key == "exif" ? 0 : p.Key == "mtime" ? 1 : 2).First().Key;
            yield return new(Name, "FolderMonth", $"{rootKey}|{pair.Key.Month}|{pair.Key.Category}",
                $"{pair.Key.Month} {pair.Key.Category}", bucket.First, bucket.Last, root,
                new Dictionary<string, string>
                {
                    ["count"] = bucket.Count.ToString(CultureInfo.InvariantCulture),
                    ["bytes"] = bucket.Bytes.ToString(CultureInfo.InvariantCulture),
                    ["samples"] = string.Join(" | ", bucket.Samples),
                    ["dateSource"] = dominantDate
                });
        }
        progress?.Report(new(Name, "complete", buckets.Count));

        void ReportReadError(string stage) => progress?.Report(new(Name, stage, items,
            Error: "An entry could not be read and was skipped."));
    }

    private static bool IsReadError(Exception error) => error is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException;

    private static Dictionary<string, string> BuildCategories()
    {
        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add("photo", ".jpg .jpeg .jpe .png .gif .bmp .tif .tiff .webp .heic .heif .avif .dng .raw .cr2 .cr3 .nef .arw .orf .raf .rw2 .pef .psd");
        Add("video", ".mp4 .m4v .mov .avi .mkv .wmv .webm .mpeg .mpg .3gp .mts .m2ts");
        Add("audio", ".mp3 .wav .flac .m4a .aac .ogg .opus .wma .aiff .aif .mid .midi");
        Add("document", ".txt .md .pdf .doc .docx .xls .xlsx .ppt .pptx .odt .ods .odp .rtf .csv .epub");
        Add("code", ".cs .fs .vb .js .jsx .ts .tsx .py .rb .go .rs .java .kt .c .h .cpp .hpp .swift .php .html .css .scss .sql .json .xml .yaml .yml .toml .ps1 .sh .bat .cmd .ipynb .sln .slnx .csproj");
        Add("archive", ".zip .7z .rar .tar .gz .bz2 .xz .tgz .zst .iso");
        Add("installer", ".exe .msi .msix .appx .msixbundle .appxbundle .msp .deb .rpm .dmg .pkg");
        return categories;
        void Add(string category, string extensions)
        {
            foreach (var extension in extensions.Split(' ')) categories.Add(extension, category);
        }
    }

    private sealed class Bucket
    {
        public long Count { get; set; }
        public long Bytes { get; set; }
        public DateTimeOffset? First { get; set; }
        public DateTimeOffset? Last { get; set; }
        public SortedSet<string> Samples { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> DateSources { get; } = new(StringComparer.Ordinal);
    }
}

internal sealed class FileSystemReader : IFileSystemReader
{
    public bool DirectoryExists(string path) => System.IO.Directory.Exists(path);

    public IEnumerable<FileEntry> EnumerateDirectory(string path)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            FileEntry entry;
            try
            {
                var attributes = info.Attributes;
                var length = info is FileInfo file && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 ? file.Length : 0;
                entry = new(info.FullName, attributes, length, info.LastWriteTimeUtc, info.CreationTimeUtc);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                entry = new(info.FullName, 0, 0, null, null, "File metadata was unavailable.");
            }
            yield return entry;
        }
    }

    public DateTimeOffset? ReadExifDate(string path)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);
        foreach (var directory in directories.OfType<ExifSubIfdDirectory>())
        {
            if (directory.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var date))
            {
                var offsetText = directory.GetString(0x9011);
                if (offsetText is { Length: 6 } && offsetText[0] is '+' or '-'
                    && TimeSpan.TryParseExact(offsetText[1..], @"hh\:mm", CultureInfo.InvariantCulture, out var offset)
                    && offset <= TimeSpan.FromHours(14))
                    return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified),
                        offsetText[0] == '-' ? -offset : offset).ToUniversalTime();
                // EXIF without an offset has no timezone evidence; keep its recorded clock as UTC.
                return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc));
            }
        }
        return null;
    }
}
