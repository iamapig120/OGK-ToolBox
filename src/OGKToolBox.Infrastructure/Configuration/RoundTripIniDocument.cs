using System.Text;

namespace OGKToolBox.Infrastructure.Configuration;

public enum IniLineKind
{
    Blank,
    Comment,
    Section,
    KeyValue,
    Unknown
}

public sealed record IniLine(
    string RawText,
    string LineEnding,
    IniLineKind Kind,
    string Section,
    string? Key,
    string? Value,
    int LineNumber);

public sealed class RoundTripIniDocument
{
    private readonly Encoding _encoding;
    private readonly bool _hasPreamble;

    private RoundTripIniDocument(
        IReadOnlyList<IniLine> lines,
        Encoding encoding,
        bool hasPreamble)
    {
        Lines = lines;
        _encoding = encoding;
        _hasPreamble = hasPreamble;
    }

    public IReadOnlyList<IniLine> Lines { get; }
    public string EncodingName => _encoding.WebName;
    public string NewLineName
    {
        get
        {
            var endings = Lines.Select(line => line.LineEnding).Where(value => value.Length > 0).ToArray();
            if (endings.Length == 0) return "无换行";
            var dominant = endings.GroupBy(value => value).MaxBy(group => group.Count())!.Key;
            return dominant switch { "\r\n" => "CRLF", "\n" => "LF", "\r" => "CR", _ => "未知" };
        }
    }

    public static async Task<RoundTripIniDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Parse(bytes);
    }

    public static RoundTripIniDocument Parse(byte[] bytes)
    {
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new RoundTripIniDocument(ParseLines(text), encoding, preambleLength > 0);
    }

    public string ToText() => string.Concat(Lines.Select(line => line.RawText + line.LineEnding));

    public byte[] ToBytes()
    {
        var content = _encoding.GetBytes(ToText());
        if (!_hasPreamble) return content;
        var preamble = _encoding.GetPreamble();
        var result = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
        return result;
    }

    public RoundTripIniDocument WithValue(int lineNumber, string newValue)
    {
        if (newValue.Contains('\r') || newValue.Contains('\n') || newValue.Contains('\0'))
            throw new ArgumentException("配置值不能包含换行或 NUL 字符。", nameof(newValue));
        var index = lineNumber - 1;
        if (index < 0 || index >= Lines.Count || Lines[index].Kind != IniLineKind.KeyValue)
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "目标行不是有效的 INI 配置项。");

        var source = Lines[index];
        var equals = source.RawText.IndexOf('=');
        var afterEquals = source.RawText[(equals + 1)..];
        var leadingLength = afterEquals.Length - afterEquals.TrimStart().Length;
        var suffixStart = FindSuffixStart(afterEquals, leadingLength);
        var keyPrefix = source.Section.Equals("dns", StringComparison.OrdinalIgnoreCase)
            && source.Key!.Equals("aimedb", StringComparison.OrdinalIgnoreCase)
            ? source.RawText[..(source.RawText.Length - source.RawText.TrimStart().Length)] + "AimeDB="
            : source.RawText[..(equals + 1)];
        var raw = keyPrefix + afterEquals[..leadingLength] + newValue + afterEquals[suffixStart..];
        var lines = Lines.ToArray();
        lines[index] = source with { RawText = raw, Value = newValue };
        return new RoundTripIniDocument(lines, _encoding, _hasPreamble);
    }

    public RoundTripIniDocument WithoutValue(int lineNumber)
    {
        var index = lineNumber - 1;
        if (index < 0 || index >= Lines.Count || Lines[index].Kind != IniLineKind.KeyValue)
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "目标行不是有效的 INI 配置项。");

        var lines = Lines.ToList();
        lines.RemoveAt(index);
        return new RoundTripIniDocument(lines, _encoding, _hasPreamble);
    }

    public RoundTripIniDocument WithCommentedOutValue(int lineNumber)
    {
        var index = lineNumber - 1;
        if (index < 0 || index >= Lines.Count || Lines[index].Kind != IniLineKind.KeyValue || Lines[index].Key is null)
            throw new ArgumentOutOfRangeException(nameof(lineNumber), "目标行不是有效的 INI 配置项。");

        var source = Lines[index];
        var indentationLength = source.RawText.Length - source.RawText.TrimStart().Length;
        var key = source.Section.Equals("dns", StringComparison.OrdinalIgnoreCase)
            && source.Key!.Equals("aimedb", StringComparison.OrdinalIgnoreCase) ? "AimeDB" : source.Key!;
        var raw = source.RawText[..indentationLength] + ";" + key + "=";
        var lines = Lines.ToArray();
        lines[index] = source with { RawText = raw, Kind = IniLineKind.Comment, Key = null, Value = null };
        if (!IsAimeDb(source)) return new RoundTripIniDocument(lines, _encoding, _hasPreamble);

        // AimeDB is intentionally represented by one commented placeholder when disabled.
        // Remove placeholders left by older versions before writing the current one.
        return new RoundTripIniDocument(lines.Where((line, lineIndex) =>
            lineIndex == index || !IsCommentedOutAimeDb(line)).ToArray(), _encoding, _hasPreamble);
    }

    public RoundTripIniDocument WithAddedValue(string section, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("新增的 INI 配置必须包含段名和键名。");
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('\0'))
            throw new ArgumentException("配置值不能包含换行或 NUL 字符。", nameof(value));

        if (IsAimeDb(section, key))
        {
            var commented = Lines
                .Select((line, index) => (line, index))
                .Where(item => IsCommentedOutAimeDb(item.line))
                .ToArray();
            if (commented.Length > 0)
            {
                var restoredIndex = commented[0].index;
                var source = commented[0].line;
                var indentationLength = source.RawText.Length - source.RawText.TrimStart().Length;
                var restored = source with
                {
                    RawText = source.RawText[..indentationLength] + $"AimeDB={value}",
                    Kind = IniLineKind.KeyValue,
                    Key = "AimeDB",
                    Value = value
                };
                var lines = Lines.Select((line, index) => index == restoredIndex ? restored : line)
                    .Where((line, index) => index == restoredIndex || !IsCommentedOutAimeDb(line))
                    .ToArray();
                return new RoundTripIniDocument(lines, _encoding, _hasPreamble);
            }
        }

        var lineEnding = Lines.Select(line => line.LineEnding).FirstOrDefault(ending => ending.Length > 0) ?? "\r\n";
        var targetSection = Lines
            .Select((line, index) => (line, index))
            .LastOrDefault(item => item.line.Kind == IniLineKind.Section
                && item.line.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
        var text = ToText();
        if (targetSection.line is null)
        {
            if (text.Length > 0 && !EndsWithLineEnding(text)) text += lineEnding;
            text += $"[{section}]{lineEnding}{key}={value}{lineEnding}";
        }
        else
        {
            var insertAfter = targetSection.index + 1;
            while (insertAfter < Lines.Count && Lines[insertAfter].Kind != IniLineKind.Section) insertAfter++;
            var offset = Lines.Take(insertAfter).Sum(line => line.RawText.Length + line.LineEnding.Length);
            var before = text[..offset];
            if (before.Length > 0 && !EndsWithLineEnding(before)) before += lineEnding;
            text = before + $"{key}={value}{lineEnding}" + text[offset..];
        }
        return new RoundTripIniDocument(ParseLines(text), _encoding, _hasPreamble);
    }

    private static bool EndsWithLineEnding(string value) => value.EndsWith('\r') || value.EndsWith('\n');

    private static bool IsAimeDb(IniLine line) => IsAimeDb(line.Section, line.Key);

    private static bool IsAimeDb(string section, string? key) =>
        section.Equals("dns", StringComparison.OrdinalIgnoreCase)
        && key?.Equals("aimedb", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCommentedOutAimeDb(IniLine line)
    {
        if (line.Kind != IniLineKind.Comment || !line.Section.Equals("dns", StringComparison.OrdinalIgnoreCase))
            return false;
        var text = line.RawText.TrimStart();
        if (text.Length == 0 || text[0] is not ';' and not '#') return false;
        var equals = text.IndexOf('=');
        return equals >= 0 && text[1..equals].Trim().Equals("aimedb", StringComparison.OrdinalIgnoreCase);
    }

    private static int FindSuffixStart(string value, int contentStart)
    {
        for (var index = contentStart; index < value.Length; index++)
        {
            if (value[index] is not ';' and not '#') continue;
            if (index > contentStart && !char.IsWhiteSpace(value[index - 1])) continue;
            while (index > contentStart && char.IsWhiteSpace(value[index - 1])) index--;
            return index;
        }
        var end = value.Length;
        while (end > contentStart && char.IsWhiteSpace(value[end - 1])) end--;
        return end;
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return (new UTF8Encoding(true), Encoding.UTF8.GetPreamble().Length);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            return (Encoding.Unicode, Encoding.Unicode.GetPreamble().Length);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            return (Encoding.BigEndianUnicode, Encoding.BigEndianUnicode.GetPreamble().Length);

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false), 0);
        }
        catch (DecoderFallbackException)
        {
            // Latin-1 is a byte-preserving fallback. Unknown legacy text remains round-trippable.
            return (Encoding.Latin1, 0);
        }
    }

    private static IReadOnlyList<IniLine> ParseLines(string text)
    {
        var result = new List<IniLine>();
        var section = string.Empty;
        var index = 0;
        var lineNumber = 1;
        while (index < text.Length)
        {
            var contentStart = index;
            while (index < text.Length && text[index] is not '\r' and not '\n') index++;
            var raw = text[contentStart..index];
            var ending = string.Empty;
            if (index < text.Length)
            {
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    ending = "\r\n";
                    index += 2;
                }
                else
                {
                    ending = text[index].ToString();
                    index++;
                }
            }

            var trimmed = raw.Trim();
            var kind = IniLineKind.Unknown;
            string? key = null;
            string? value = null;
            if (trimmed.Length == 0)
                kind = IniLineKind.Blank;
            else if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                kind = IniLineKind.Comment;
            else if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
            {
                kind = IniLineKind.Section;
                section = trimmed[1..^1].Trim();
            }
            else
            {
                var equals = raw.IndexOf('=');
                if (equals >= 0)
                {
                    key = raw[..equals].Trim();
                    var valueText = raw[(equals + 1)..];
                    var valueStart = valueText.Length - valueText.TrimStart().Length;
                    value = valueText[valueStart..FindSuffixStart(valueText, valueStart)].TrimEnd();
                    kind = key.Length == 0 ? IniLineKind.Unknown : IniLineKind.KeyValue;
                }
            }

            result.Add(new IniLine(raw, ending, kind, section, key, value, lineNumber++));
        }

        return result;
    }
}
