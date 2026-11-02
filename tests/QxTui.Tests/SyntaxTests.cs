using QxTui.Render;

namespace QxTui.Tests;

public class SyntaxTests
{
    [Fact]
    public void CSharp_Line_YieldsKeywordStringComment()
    {
        var h = new SyntaxHighlighter("cs");
        var toks = h.Line("var s = \"hi\"; // note");

        Assert.Contains(toks, t => t.Kind == TokenKind.Keyword &&
            "var" == "var s = \"hi\"; // note".Substring(t.Start, t.Length));
        Assert.Contains(toks, t => t.Kind == TokenKind.String &&
            "\"hi\"" == "var s = \"hi\"; // note".Substring(t.Start, t.Length));
        Assert.Contains(toks, t => t.Kind == TokenKind.Comment &&
            "// note" == "var s = \"hi\"; // note".Substring(t.Start, t.Length));
    }

    [Fact]
    public void BlockComment_CarriesAcrossLines()
    {
        var h = new SyntaxHighlighter("cs");
        var l1 = h.Line("/* start");
        Assert.Single(l1);
        Assert.Equal(TokenKind.Comment, l1[0].Kind);

        var l2 = h.Line("still comment */ int x = 42;");
        Assert.Equal(TokenKind.Comment, l2[0].Kind);
        Assert.Equal("still comment */", "still comment */ int x = 42;".Substring(l2[0].Start, l2[0].Length));
        Assert.Contains(l2, t => t.Kind == TokenKind.Number);
        Assert.Contains(l2, t => t.Kind == TokenKind.Keyword); // int
    }

    [Fact]
    public void Python_TripleString_CarriesAcrossLines()
    {
        var h = new SyntaxHighlighter("py");
        Assert.Single(h.Line("x = \"\"\"doc"));
        var l2 = h.Line("doc\"\"\" + 1");
        Assert.Equal(TokenKind.String, l2[0].Kind);
        Assert.Contains(l2, t => t.Kind == TokenKind.Number);
    }

    [Fact]
    public void TypesAndDirectives()
    {
        var h = new SyntaxHighlighter("cs");
        var toks = h.Line("#if DEBUG Widget w;");
        Assert.Equal(TokenKind.Directive, toks[0].Kind);
        Assert.Contains(toks, t => t.Kind == TokenKind.Type &&
            "Widget" == "#if DEBUG Widget w;".Substring(t.Start, t.Length));
    }

    [Fact]
    public void LanguageFor_MapsExtensionsAndSpecialNames()
    {
        Assert.Equal("cs", SyntaxHighlighter.LanguageFor("a/b/Table.cs"));
        Assert.Equal("py", SyntaxHighlighter.LanguageFor("script.py"));
        Assert.Equal("sh", SyntaxHighlighter.LanguageFor("Dockerfile"));
        Assert.Equal("", SyntaxHighlighter.LanguageFor("notes.txt"));
    }
}
