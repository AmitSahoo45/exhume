using Microsoft.Win32;

namespace Exhume.Adapters.Windows;

internal sealed class WindowsRegistryReader : IRegistryReader
{
    public IRegistryKey? OpenKey(string path)
    {
        var separator = path.IndexOf('\\');
        if (separator < 1) return null;
        var hive = path[..separator] switch { "HKLM" => RegistryHive.LocalMachine, "HKCU" => RegistryHive.CurrentUser, _ => (RegistryHive?)null };
        if (hive is null) return null;
        using var root = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
        var key = root.OpenSubKey(path[(separator + 1)..], writable: false);
        return key is null ? null : new ReadOnlyRegistryKey(key);
    }

    private sealed class ReadOnlyRegistryKey(RegistryKey key) : IRegistryKey
    {
        public IEnumerable<string> SubKeyNames => key.GetSubKeyNames();
        public IEnumerable<string> ValueNames => key.GetValueNames();
        public object? ReadValue(string name) => key.GetValue(name, null, RegistryValueOptions.None);
        public void Dispose() => key.Dispose();
    }
}

internal sealed class WindowsDiskProbe : IWindowsDiskProbe
{
    public bool FileExists(string path) => Exists(path, directory: false);
    public bool DirectoryExists(string path) => Exists(path, directory: true);
    public IEnumerable<string> Directories(string path) => Directory.EnumerateDirectories(path);
    public IEnumerable<string> Files(string path, string pattern) => Directory.EnumerateFiles(path, pattern);
    public EvidenceTimes Times(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked evidence folders are skipped.");
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        return new(info.CreationTimeUtc, info.LastWriteTimeUtc);
    }
    private static bool Exists(string path, bool directory)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory) == directory; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}

internal static class WindowsEnvironment
{
    public static WindowsFolders Folders()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), windows,
            Environment.GetFolderPath(Environment.SpecialFolder.System), Path.Combine(windows, "SysWOW64"),
            Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"),
            roaming, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }
}
