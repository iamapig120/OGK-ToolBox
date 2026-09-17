using System.Text;
using OGKToolBox.Infrastructure.Configuration;

namespace OGKToolBox.Tests;

public sealed class RoundTripIniDocumentTests
{
    [Fact]
    public void RemovesOnlyTheRequestedKeyValueLine()
    {
        var document = RoundTripIniDocument.Parse(Encoding.UTF8.GetBytes(
            "[dns]\r\naimedb=https://aime.example\r\ndefault=https://server.example\r\n"));

        var changed = document.WithoutValue(2);

        Assert.Equal("[dns]\r\ndefault=https://server.example\r\n", changed.ToText());
    }

    [Fact]
    public void CommentsOutOnlyTheRequestedKeyValueLine()
    {
        var document = RoundTripIniDocument.Parse(Encoding.UTF8.GetBytes(
            "[dns]\r\naimedb=https://aime.example\r\ndefault=https://server.example\r\n"));

        var changed = document.WithCommentedOutValue(2);

        Assert.Equal("[dns]\r\n;AimeDB=\r\ndefault=https://server.example\r\n", changed.ToText());
    }

    [Fact]
    public void RestoresOneCommentedAimeDbAndRemovesDuplicatePlaceholders()
    {
        var document = RoundTripIniDocument.Parse(Encoding.UTF8.GetBytes(
            "[dns]\r\n;aimedb=\r\n;AimeDB=\r\ndefault=https://server.example\r\n"));

        var changed = document.WithAddedValue("dns", "AimeDB", "aime.mumur.net");

        Assert.Equal("[dns]\r\nAimeDB=aime.mumur.net\r\ndefault=https://server.example\r\n", changed.ToText());
    }

    [Fact]
    public void CommentingOutAimeDbRemovesExistingDuplicatePlaceholders()
    {
        var document = RoundTripIniDocument.Parse(Encoding.UTF8.GetBytes(
            "[dns]\r\n;aimedb=\r\nAimeDB=aime.mumur.net\r\n;AimeDB=\r\ndefault=https://server.example\r\n"));

        var changed = document.WithCommentedOutValue(3);

        Assert.Equal("[dns]\r\n;AimeDB=\r\ndefault=https://server.example\r\n", changed.ToText());
    }

    [Fact]
    public void PreservesUtf8BomCommentsDuplicatesUnknownLinesAndCrlf()
    {
        const string text = "; 保留注释\r\n[system]\r\nfreeplay = 1\r\nfreeplay=0\r\nnot valid ini\r\n\r\n";
        var content = Encoding.UTF8.GetBytes(text);
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, bytes, preamble.Length, content.Length);

        var document = RoundTripIniDocument.Parse(bytes);

        Assert.Equal("utf-8", document.EncodingName);
        Assert.Equal("CRLF", document.NewLineName);
        Assert.Equal(2, document.Lines.Count(line => line.Kind == IniLineKind.KeyValue));
        Assert.Contains(document.Lines, line => line.Kind == IniLineKind.Unknown);
        Assert.Equal(bytes, document.ToBytes());
    }

    [Fact]
    public void PreservesUtf8WithoutBomAndFinalLineWithoutNewline()
    {
        var bytes = Encoding.UTF8.GetBytes("[Sound]\nWasapiExclusive=1");

        var document = RoundTripIniDocument.Parse(bytes);

        Assert.Equal("LF", document.NewLineName);
        Assert.Equal(string.Empty, document.Lines[^1].LineEnding);
        Assert.Equal(bytes, document.ToBytes());
    }

    [Fact]
    public void ReplacesOnlyTheValueAndPreservesSpacingAndInlineComment()
    {
        var document = RoundTripIniDocument.Parse(Encoding.UTF8.GetBytes(
            "[system]\r\nfreeplay = 0  ; explanation\r\n"));

        var changed = document.WithValue(2, "1");

        Assert.Equal("0", document.Lines[1].Value);
        Assert.Equal("[system]\r\nfreeplay = 1  ; explanation\r\n", changed.ToText());
    }
}
