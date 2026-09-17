using System.Globalization;
using System.Xml.Linq;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Scanning;

public sealed class MusicMetadataReader(IChartSerializer charts) : IMusicMetadataReader
{
    private static readonly string[] DifficultyNames = ["BASIC", "ADVANCED", "EXPERT", "MASTER", "LUNATIC"];

    public Music Read(string musicXmlPath, DataPackage package, IReadOnlyDictionary<string, GameResource> effectiveResources)
    {
        var document = XDocument.Load(musicXmlPath, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException($"XML 没有根节点：{musicXmlPath}");
        var directory = Path.GetDirectoryName(musicXmlPath)!;
        var dataName = Value(root, "dataName");
        var id = Int(root.Element("Name")?.Element("id"));
        var origin = new ResourceOrigin(package.Id, musicXmlPath, package.LoadOrder);
        var chartModels = new List<Chart>();
        var chartElements = root.Element("FumenData")?.Elements("FumenData").ToArray() ?? [];

        for (var index = 0; index < chartElements.Length; index++)
        {
            var element = chartElements[index];
            var relativePath = element.Element("FumenFile")?.Element("path")?.Value.Trim() ?? string.Empty;
            if (relativePath.Length == 0)
            {
                continue;
            }

            var fullPath = Path.Combine(directory, relativePath);
            var integer = Int(element.Element("FumenConstIntegerPart"));
            var fraction = Int(element.Element("FumenConstFractionalPart"));
            var creator = string.Empty;
            decimal bpm = 0;
            var notes = 0;
            var taps = 0;
            var holds = 0;
            var flicks = 0;
            var bells = 0;
            if (File.Exists(fullPath))
            {
                var chart = charts.Read(fullPath);
                creator = chart.HeaderValue("CREATOR") ?? string.Empty;
                bpm = chart.HeaderDecimal("BPM_DEF");
                notes = HeaderOrCount(chart, "T_TOTAL", "TAP", "HOLD", "FLICK", "BEL");
                taps = HeaderOrCount(chart, "T_TAP", "TAP");
                holds = HeaderOrCount(chart, "T_HOLD", "HOLD");
                flicks = HeaderOrCount(chart, "T_FLICK", "FLICK");
                bells = HeaderOrCount(chart, "T_BELL", "BEL");
            }

            chartModels.Add(new(index, index < DifficultyNames.Length ? DifficultyNames[index] : $"DIFFICULTY {index}",
                integer + fraction / 100m, fullPath, creator, bpm, notes, taps, holds, flicks, bells, File.Exists(fullPath)));
        }

        var jacketKey = $"ui_jacket_{id:D4}";
        ResourceReference? jacket = effectiveResources.TryGetValue(jacketKey, out var jacketResource)
            ? new(jacketResource.Key, jacketResource.BundlePath, ResourceKind.Jacket)
            : null;

        var sourceId = Int(root.Element("MusicSourceName")?.Element("id"));
        var sourceName = $"music{sourceId:D4}";
        var sourceDirectory = FindSiblingDirectory(package.RootPath, "musicsource", $"musicsource{sourceId:D4}");
        var acb = effectiveResources.TryGetValue(sourceName + ".acb", out var acbResource)
            ? acbResource.BundlePath
            : sourceDirectory is null ? string.Empty : Path.Combine(sourceDirectory, sourceName + ".acb");
        var awb = effectiveResources.TryGetValue(sourceName + ".awb", out var awbResource)
            ? awbResource.BundlePath
            : sourceDirectory is null ? string.Empty : Path.Combine(sourceDirectory, sourceName + ".awb");
        AudioReference? audio = File.Exists(acb) ? new(acb, File.Exists(awb) ? awb : string.Empty, sourceId) : null;

        DateTime? releaseDate = DateTime.TryParse(Value(root, "ReleaseVersion"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var date) ? date : null;
        return new(id, dataName, NestedValue(root, "Name", "str"), NestedValue(root, "ArtistName", "str"),
            NestedValue(root, "Genre", "str"), NestedValue(root, "VersionID", "str"), releaseDate,
            chartModels, jacket, audio, origin);
    }

    private static string? FindSiblingDirectory(string root, string category, string name)
    {
        var candidate = Path.Combine(root, category, name);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static int CountCommands(OgkrDocument document, string command) => document.Sections
        .SelectMany(section => section.Lines)
        .Count(line => line.Command?.StartsWith(command, StringComparison.OrdinalIgnoreCase) == true);

    private static int HeaderOrCount(OgkrDocument document, string header, params string[] commands)
    {
        var value = document.HeaderInt(header);
        return value > 0 ? value : commands.Sum(command => CountCommands(document, command));
    }

    private static string Value(XElement root, string name) => root.Element(name)?.Value.Trim() ?? string.Empty;
    private static string NestedValue(XElement root, string name, string child) => root.Element(name)?.Element(child)?.Value.Trim() ?? string.Empty;
    private static int Int(XElement? element) => int.TryParse(element?.Value, out var value) ? value : 0;
}
