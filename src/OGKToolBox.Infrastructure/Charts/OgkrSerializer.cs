using System.Text;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Charts;

public sealed class OgkrSerializer : IChartSerializer
{
    public OgkrDocument Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewLine = text.EndsWith("\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (endsWithNewLine)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var sections = new List<OgkrSection>();
        OgkrSection? current = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                current = new OgkrSection
                {
                    Name = trimmed[1..^1],
                    HeaderLineNumber = index + 1,
                    Lines = []
                };
                sections.Add(current);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            string? command = null;
            IReadOnlyList<string> arguments = [];
            if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith('#'))
            {
                var fields = trimmed.Split('\t');
                if (fields.Length == 1)
                {
                    fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }

                command = fields[0];
                arguments = fields.Skip(1).ToArray();
            }

            current.Lines.Add(new(index + 1, raw, command, arguments));
        }

        var diagnostics = new List<LibraryDiagnostic>();
        if (!sections.Any(section => section.Name.Equals("HEADER", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(new(DiagnosticSeverity.Warning, "OGKR_HEADER_MISSING", "谱面没有 HEADER Section。", path));
        }

        return new OgkrDocument
        {
            SourcePath = path,
            NewLine = newLine,
            EndsWithNewLine = endsWithNewLine,
            OriginalLines = lines,
            Sections = sections,
            Diagnostics = diagnostics
        };
    }

    public string WriteToString(OgkrDocument document)
    {
        var result = string.Join(document.NewLine, document.OriginalLines);
        return document.EndsWithNewLine ? result + document.NewLine : result;
    }
}
