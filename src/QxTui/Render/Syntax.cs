namespace QxTui.Render;

/// <summary>What a highlighted span is — mapped to theme roles by the
/// caller, so highlighting follows the app's theme instead of hardcoding
/// 256-colors that clash with Amber/Mono.</summary>
public enum TokenKind
{
    Text,
    Comment,
    String,
    Keyword,
    Number,
    Type,
    Directive,
}

/// <summary>A highlighted span: [Start, Start+Length) in the source line.</summary>
public readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>Line-based syntax highlighter for the code views (qxgit's
/// clean-file viewer, log tails, config dumps). Deliberately small: a
/// scanner, not a parser — comments, strings (incl. block comments and
/// python triple-quotes across lines), keywords, numbers, Capitalized
/// types and #/ @ directives. Good enough to read at a glance, which is
/// the whole job in a terminal.
///
/// Stateful per file: block comments and triple-quoted strings carry
/// across lines, so feed lines in order through one instance.</summary>
public sealed class SyntaxHighlighter
{
    private readonly string _lang;
    private string? _blockComment;   // "*/" when inside /* … */
    private string? _blockString;    // "\"\"\"" or "'''" when inside one

    public SyntaxHighlighter(string lang) => _lang = lang;

    /// <summary>Map a file name/path to a language key by extension.</summary>
    public static string LanguageFor(string path)
    {
        var name = path.Split('/')[^1];
        int dot = name.LastIndexOf('.');
        string ext = dot >= 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
        return ext switch
        {
            "cs" => "cs",
            "py" => "py",
            "js" or "ts" or "jsx" or "tsx" => "js",
            "rs" => "rs",
            "go" => "go",
            "sh" or "bash" or "zsh" => "sh",
            "json" => "json",
            "md" or "markdown" => "md",
            "toml" or "yaml" or "yml" => "toml",
            "c" or "h" or "cpp" or "hpp" => "c",
            _ => name switch
            {
                "Dockerfile" or "Makefile" => "sh",
                _ => "",
            },
        };
    }

    private string? LineComment => _lang switch
    {
        "cs" or "js" or "rs" or "go" or "c" => "//",
        "py" or "sh" or "toml" => "#",
        _ => null,
    };

    private bool HasBlockComment => _lang is "cs" or "js" or "rs" or "go" or "c";

    private static readonly HashSet<string> Keywords = new()
    {
        // c#
        "abstract","as","base","bool","break","byte","case","catch","char","checked",
        "class","const","continue","decimal","default","delegate","do","double","else",
        "enum","event","explicit","extern","false","finally","fixed","float","for",
        "foreach","goto","if","implicit","in","int","interface","internal","is","lock",
        "long","namespace","new","null","object","operator","out","override","params",
        "private","protected","public","readonly","record","ref","return","sbyte",
        "sealed","short","sizeof","stackalloc","static","string","struct","switch",
        "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe",
        "ushort","using","var","virtual","void","volatile","while","yield","async",
        "await","init","get","set","value","when","required","global","file",
        // python
        "def","import","from","elif","lambda","pass","global","nonlocal","assert",
        "raise","del","and","or","not","None","True","False","match","with",
        // js/ts
        "let","function","extends","super","typeof","instanceof","export","undefined",
        "type","implements","of","delete",
        // rust
        "fn","let","mut","struct","impl","trait","pub","mod","move","dyn","crate",
        "self","super","Some","None","Ok","Err","loop","unsafe",
        // go
        "func","package","chan","defer","select","fallthrough","range","map",
        // shell
        "then","fi","done","esac","function","export","local","source","alias",
        // json
        "true","false","null",
    };

    /// <summary>Highlight one line; tokens are ordered, non-overlapping.
    /// Gaps between tokens are plain text.</summary>
    public List<Token> Line(string line)
    {
        var tokens = new List<Token>();
        int i = 0;

        // inside a block comment from a previous line
        if (_blockComment is not null)
        {
            int end = line.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0) { tokens.Add(new Token(0, line.Length, TokenKind.Comment)); return tokens; }
            tokens.Add(new Token(0, end + 2, TokenKind.Comment));
            i = end + 2;
            _blockComment = null;
        }
        // inside a triple-quoted string from a previous line
        if (_blockString is not null)
        {
            int end = line.IndexOf(_blockString, StringComparison.Ordinal);
            if (end < 0) { tokens.Add(new Token(0, line.Length, TokenKind.String)); return tokens; }
            tokens.Add(new Token(0, end + 3, TokenKind.String));
            i = end + 3;
            _blockString = null;
        }

        string? lineComment = LineComment;
        while (i < line.Length)
        {
            char c = line[i];

            // markdown: heading lines + list bullets read as structure
            if (_lang == "md" && i == 0 && c == '#')
            {
                tokens.Add(new Token(0, line.Length, TokenKind.Directive));
                return tokens;
            }

            if (lineComment is not null && line.AsSpan(i).StartsWith(lineComment))
            {
                tokens.Add(new Token(i, line.Length - i, TokenKind.Comment));
                return tokens;
            }

            if (HasBlockComment && line.AsSpan(i).StartsWith("/*"))
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new Token(i, line.Length - i, TokenKind.Comment));
                    _blockComment = "*/";
                    return tokens;
                }
                tokens.Add(new Token(i, end + 2 - i, TokenKind.Comment));
                i = end + 2;
                continue;
            }

            // python triple quotes
            if (_lang == "py" && i + 2 < line.Length &&
                (line.AsSpan(i, 3).SequenceEqual("\"\"\"") || line.AsSpan(i, 3).SequenceEqual("'''")))
            {
                string q = line.Substring(i, 3);
                int end = line.IndexOf(q, i + 3, StringComparison.Ordinal);
                if (end < 0)
                {
                    tokens.Add(new Token(i, line.Length - i, TokenKind.String));
                    _blockString = q;
                    return tokens;
                }
                tokens.Add(new Token(i, end + 3 - i, TokenKind.String));
                i = end + 3;
                continue;
            }

            // strings: optional cs prefix (@ $" etc), then quote…quote
            if (c is '"' or '\'')
            {
                int start = i;
                if (start > 0 && line[start - 1] is '@' or '$') start--;
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '\\' && i + 1 < line.Length) { i += 2; continue; }
                    if (line[i] == c) { i++; break; }
                    i++;
                }
                tokens.Add(new Token(start, i - start, TokenKind.String));
                continue;
            }

            // directives: #if/#include (c-family), @decorator (py), cs using-directives stay keywords
            if ((c == '#' && _lang is "cs" or "c") || (c == '@' && _lang == "py"))
            {
                int j = i + 1; // skip the # / @ itself
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                if (j > i + 1)
                {
                    tokens.Add(new Token(i, j - i, TokenKind.Directive));
                    i = j;
                    continue;
                }
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                string word = line[i..j];
                TokenKind kind = Keywords.Contains(word) ? TokenKind.Keyword
                    : char.IsUpper(c) ? TokenKind.Type
                    : TokenKind.Text;
                if (kind != TokenKind.Text)
                    tokens.Add(new Token(i, j - i, kind));
                i = j;
                continue;
            }

            if (char.IsDigit(c))
            {
                int j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '.' or '_')) j++;
                tokens.Add(new Token(i, j - i, TokenKind.Number));
                i = j;
                continue;
            }

            i++;
        }
        return tokens;
    }

    /// <summary>Convenience: highlight a whole document in order.</summary>
    public static List<Token[]> HighlightAll(IEnumerable<string> lines, string lang)
    {
        var h = new SyntaxHighlighter(lang);
        var all = new List<Token[]>();
        foreach (var line in lines) all.Add([.. h.Line(line)]);
        return all;
    }
}
