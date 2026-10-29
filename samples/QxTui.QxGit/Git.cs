using System.Diagnostics;

namespace QxTui.QxGit;

/// <summary>Git subprocess runner + pure parsers. ALL process I/O lives
/// here; Program.cs only ever talks to it inside Effect callbacks, and
/// results travel back as messages. The parsers are static and string-in /
/// data-out, so they are unit-testable without touching a repository.</summary>
internal static class Git
{
    public sealed record Commit(string Hash, string Author, string Date, string Subject);

    /// <summary>Run git with redirected pipes (never a tty, so git never
    /// colorizes — the app owns all coloring). Exit code + both streams
    /// come back; callers decide what "failure" means (diff --no-index
    /// "fails" with code 1 and the answer on stdout).</summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(
        string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start git");
        string so = await p.StandardOutput.ReadToEndAsync(ct);
        string se = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, so, se);
    }

    /// <summary>Tab-separated log lines → commits. Subjects containing
    /// tabs survive (the tail is rejoined).</summary>
    public static IReadOnlyList<Commit> ParseLog(string stdout)
    {
        var list = new List<Commit>();
        foreach (string line in stdout.Split('\n'))
        {
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f.Length < 4) continue;
            list.Add(new Commit(f[0], f[1], f[2], string.Join('\t', f[3..])));
        }
        return list;
    }

    /// <summary>porcelain v1 → path → status char. The worktree letter
    /// wins over the index letter (what you'd see in `git status -s`);
    /// renames key the NEW path. Directory entries ("dir/") are skipped —
    /// their files are listed individually.</summary>
    public static Dictionary<string, char> ParseStatus(string porcelain)
    {
        var map = new Dictionary<string, char>();
        foreach (string line in porcelain.Split('\n'))
        {
            if (line.Length < 4) continue;
            char x = line[0], y = line[1];
            string path = line[3..];
            char st = y != ' ' ? y : x;
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0 && st is 'R' or 'C') path = path[(arrow + 4)..];
            if (path.EndsWith('/')) continue;
            map[path] = st;
        }
        return map;
    }

    /// <summary>Flat paths → a directory TreeNode hierarchy. Leaves carry
    /// a status-marker prefix (M/A/D/R/?) and their repo-relative path as
    /// Tag — the app's selection→diff wiring keys off that Tag. Untracked
    /// files (status only, absent from ls-files) are appended.
    ///
    /// changedOnly (the default view in qxgit): a STATUS tree, not a file
    /// browser — only files that differ from HEAD (plus untracked) and the
    /// directories leading to them. A clean repo reads "(worktree clean)"
    /// instead of burying the eye in 1500 untouched files.</summary>
    public static Widgets.TreeNode BuildTree(string repoName, string[] lsFiles,
        IReadOnlyDictionary<string, char> status, bool changedOnly = false)
    {
        var root = new Widgets.TreeNode(repoName);
        var dirs = new Dictionary<string, Widgets.TreeNode> { [""] = root };
        var tracked = new HashSet<string>(lsFiles);

        void AddLeaf(string path, char st)
        {
            string[] parts = path.Split('/');
            var parent = root;
            string acc = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                acc = acc.Length == 0 ? parts[i] : acc + "/" + parts[i];
                if (!dirs.TryGetValue(acc, out var d))
                {
                    d = new Widgets.TreeNode(parts[i]);
                    dirs[acc] = d;
                    parent.Add(d);
                }
                parent = d;
            }
            string marker = st switch
            {
                'M' => "M ", 'A' => "A ", 'D' => "D ", 'R' => "R ", '?' => "? ",
                _ => "  ",
            };
            parent.Add(new Widgets.TreeNode(marker + parts[^1]) { Tag = path });
        }

        int leaves = 0;
        foreach (string path in lsFiles)
        {
            char st = status.TryGetValue(path, out char s) ? s : ' ';
            if (changedOnly && st == ' ') continue;
            AddLeaf(path, st);
            leaves++;
        }
        foreach (var (path, st) in status)
            if (st == '?' && !tracked.Contains(path))
            {
                AddLeaf(path, '?');
                leaves++;
            }

        if (leaves == 0)
            root.Add(new Widgets.TreeNode("(worktree clean)"));

        return root;
    }
}
