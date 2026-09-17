using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OGKToolBox.Infrastructure.Resources;

public sealed class ResourceExporter(
    IUnityResourceReader unityReader,
    IChartRenderAssetManifestBuilder chartRenderManifestBuilder) : IResourceExporter
{
    public async Task ExportFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    public async Task ExportImageAsync(ResourceReference resource, string destinationPath, CancellationToken cancellationToken)
    {
        var image = await unityReader.ReadFirstImageAsync(resource.BundlePath, cancellationToken)
            ?? throw new InvalidDataException($"资源包中没有可解码的 Texture2D：{resource.BundlePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, image.PngBytes, cancellationToken);
    }

    public async Task ExportIndexJsonAsync(LibrarySnapshot snapshot, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, snapshot, options, cancellationToken);
    }

    public async Task ExportMusicCsvAsync(IReadOnlyList<Music> music, string destinationPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteLineAsync("Id,Title,Artist,Genre,Version,Package,ChartCount,HasJacket,HasAudio".AsMemory(), cancellationToken);
        foreach (var item in music)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new[] { item.Id.ToString(), item.Title, item.Artist, item.Genre, item.VersionName,
                item.Origin.PackageId, item.Charts.Count.ToString(), (item.Jacket is not null).ToString(), (item.Audio is not null).ToString() };
            await writer.WriteLineAsync(string.Join(',', values.Select(Escape)).AsMemory(), cancellationToken);
        }
    }

    public async Task ExportUnityAssetGraphJsonAsync(
        string bundlePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var graph = await unityReader.ReadAssetGraphAsync(bundlePath, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, graph, options, cancellationToken);
    }

    public async Task ExportChartRenderAssetManifestJsonAsync(
        string gameRoot,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var manifest = await chartRenderManifestBuilder.BuildAsync(gameRoot, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, manifest,
            new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
