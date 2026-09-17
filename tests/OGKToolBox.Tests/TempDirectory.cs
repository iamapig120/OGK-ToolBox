namespace OGKToolBox.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OGKToolBox.Tests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public string CreateDirectory(params string[] parts)
    {
        var path = parts.Aggregate(Path, System.IO.Path.Combine);
        Directory.CreateDirectory(path);
        return path;
    }
    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
