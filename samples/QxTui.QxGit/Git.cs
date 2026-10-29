using System.Diagnostics;
using QxTui.Core;

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

    /// <summary>Both porcelain letters for a path: the index (staged)
    /// column and the worktree column. '?' (untracked) lives in the
    /// worktree slot. Staging decisions need BOTH — which side a change
    /// sits on is exactly what `git add` / `git restore --staged` flip.</summary>
    public sealed record FileStatus(char Index, char Worktree)
    {
        public bool Changed => Index != ' ' || Worktree != ' ';
    }

    /// <summary>porcelain v1 → path → (index, worktree) letters. Renames
    /// key the NEW path. Directory entries ("dir/") are skipped — their
    /// files are listed individually.</summary>
    public static Dictionary<string, FileStatus> ParseStatus(string porcelain)
    {
        var map = new Dictionary<string, FileStatus>();
        foreach (string line in porcelain.Split('\n'))
        {
            if (line.Length < 4) continue;
            char x = line[0], y = line[1];
            string path = line[3..];
            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0 && x is 'R' or 'C') path = path[(arrow + 4)..];
            if (path.EndsWith('/')) continue;
            map[path] = new FileStatus(x, y);
        }
        return map;
    }

    public sealed record Branch(bool Current, string Name, string Hash, string Date,
        string Author, string Email, string Subject);

    /// <summary>for-each-ref lines (HEAD flag, name, hash, date, author
    /// name, author email, subject) → branches. The author fields are the
    /// commit each ref points at — what the branch picker searches.</summary>
    public static IReadOnlyList<Branch> ParseBranches(string stdout)
    {
        var list = new List<Branch>();
        foreach (string line in stdout.Split('\n'))
        {
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f.Length < 7) continue;
            list.Add(new Branch(f[0] == "*", f[1], f[2], f[3], f[4], f[5],
                string.Join('\t', f[6..])));
        }
        return list;
    }

    /// <summary>Index of the newest stash whose message ends with the
    /// qxgit link marker for `branch` ("stash@{N}: On X: qxgit:X"), or -1.
    /// The marker in the stash message IS the branch↔stash link.</summary>
    public static int ParseStashIndex(string stashList, string branch)
    {
        string marker = "qxgit:" + branch;
        var lines = stashList.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].TrimEnd().EndsWith(marker, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public sealed record Worktree(string Path, string Branch);

    /// <summary>`worktree list --porcelain` blocks → (path, branch).
    /// Detached worktrees report their short HEAD as the branch.</summary>
    public static IReadOnlyList<Worktree> ParseWorktrees(string stdout)
    {
        var list = new List<Worktree>();
        string? path = null, head = null, branch = null;
        void Flush()
        {
            if (path is not null)
                list.Add(new Worktree(path, branch ?? (head is { Length: >= 7 } h ? h[..7] + " (detached)" : "(detached)")));
            path = head = branch = null;
        }
        foreach (string line in stdout.Split('\n'))
        {
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..];
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line[5..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line[7..].Replace("refs/heads/", "");
            else if (line == "detached") branch = null;
        }
        Flush();
        return list;
    }

    /// <summary>Flat paths → a directory TreeNode hierarchy. Leaves carry
    /// a porcelain-style two-letter prefix (index column, worktree
    /// column), a semantic <see cref="ThemeRole"/> color, and their
    /// repo-relative path as Tag — the app's selection→diff wiring keys
    /// off that Tag. Untracked files (status only, absent from ls-files)
    /// are appended.
    ///
    /// changedOnly (the default view in qxgit): a STATUS tree, not a file
    /// browser — only files that differ from HEAD (plus untracked) and the
    /// directories leading to them. A clean repo reads "(worktree clean)"
    /// instead of burying the eye in 1500 untouched files.</summary>
    public static Widgets.TreeNode BuildTree(string repoName, string[] lsFiles,
        IReadOnlyDictionary<string, FileStatus> status, bool changedOnly = false)
    {
        var root = new Widgets.TreeNode(repoName);
        var dirs = new Dictionary<string, Widgets.TreeNode> { [""] = root };
        var tracked = new HashSet<string>(lsFiles);

        void AddLeaf(string path, FileStatus st)
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
            parent.Add(new Widgets.TreeNode($"{st.Index}{st.Worktree} {parts[^1]}")
            {
                Tag = path,
                Role = RoleOf(st),
            });
        }

        int leaves = 0;
        foreach (string path in lsFiles)
        {
            var st = status.TryGetValue(path, out var fs) ? fs : new FileStatus(' ', ' ');
            if (changedOnly && !st.Changed) continue;
            AddLeaf(path, st);
            leaves++;
        }
        foreach (var (path, st) in status)
            if (st.Worktree == '?' && !tracked.Contains(path))
            {
                AddLeaf(path, st);
                leaves++;
            }

        if (leaves == 0)
            root.Add(new Widgets.TreeNode("(worktree clean)"));

        ColorDirectories(root);
        return root;
    }

    /// <summary>Status color for a leaf: deleted red, unstaged changes
    /// yellow, untracked/new cyan, staged-only green, clean default.</summary>
    private static ThemeRole? RoleOf(FileStatus st) =>
        st.Index == 'D' || st.Worktree == 'D' ? ThemeRole.Danger :
        st.Worktree == '?' ? ThemeRole.Accent :
        st.Worktree != ' ' ? ThemeRole.Warning :
        st.Index != ' ' ? ThemeRole.Success :
        null;

    private static int Rank(ThemeRole? r) => r switch
    {
        ThemeRole.Danger => 3,
        ThemeRole.Warning => 2,
        ThemeRole.Accent => 2,
        ThemeRole.Success => 1,
        _ => 0,
    };

    /// <summary>Directories take the most urgent color among their
    /// descendants — a folder with a deletion inside reads red at a
    /// glance, even collapsed.</summary>
    private static ThemeRole? ColorDirectories(Widgets.TreeNode n)
    {
        ThemeRole? worst = n.Role;
        foreach (var c in n.Children)
        {
            var cr = ColorDirectories(c);
            if (Rank(cr) > Rank(worst)) worst = cr;
        }
        n.Role = worst;
        return worst;
    }
}
