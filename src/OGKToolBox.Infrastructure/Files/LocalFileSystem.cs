using OGKToolBox.Core.Abstractions;

namespace OGKToolBox.Infrastructure.Files;

public sealed class LocalFileSystem : ILocalFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool FileExists(string path) => File.Exists(path);
    public DateTimeOffset? GetLastWriteTimeUtc(string path) => File.Exists(path)
        ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
        : null;
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}
