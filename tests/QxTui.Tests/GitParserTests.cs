using QxTui.Core;
using QxTui.Layout;
using QxTui.QxGit;
using QxTui.Widgets;

namespace QxTui.Tests;

/// <summary>The pure half of qxgit: git output parsing and tree building.
/// No repository, no subprocess — string in, data out.</summary>
public class GitParserTests
{
    [Fact]
    public void ParseLog_SplitsTabSeparated_AndKeepsTabsInSubjects()
    {
        var log = "abc123\tAlice\t2026-01-02\tplain subject\n" +
                  "def456\tBob\t2026-01-03\tsubject\twith\ttabs\n";
        var commits = Git.ParseLog(log);

        Assert.Equal(2, commits.Count);
        Assert.Equal("abc123", commits[0].Hash);
        Assert.Equal("Alice", commits[0].Author);
        Assert.Equal("2026-01-02", commits[0].Date);
        Assert.Equal("plain subject", commits[0].Subject);
        Assert.Equal("subject\twith\ttabs", commits[1].Subject);
    }

    [Fact]
    public void ParseLog_SkipsJunkLines()
    {
        var commits = Git.ParseLog("\nwarning: something\nshort\tline\n");
        Assert.Empty(commits);
    }

    [Fact]
    public void ParseStatus_WorktreeLetterWins_RenamesKeyNewPath_DirsSkipped()
    {
        var porcelain =
            " M modified.cs\n" +
            "M  staged.cs\n" +
            "MM both.cs\n" +          // worktree letter wins → 'M'
            "?? untracked.cs\n" +
            "R  old.cs -> new.cs\n" +
            "A  added.cs\n" +
            "?? somedir/\n";           // directory entry: skipped
        var map = Git.ParseStatus(porcelain);

        Assert.Equal(new Git.FileStatus(' ', 'M'), map["modified.cs"]);
        Assert.Equal(new Git.FileStatus('M', ' '), map["staged.cs"]);
        Assert.Equal(new Git.FileStatus('M', 'M'), map["both.cs"]);
        Assert.Equal(new Git.FileStatus('?', '?'), map["untracked.cs"]);
        Assert.Equal('R', map["new.cs"].Index);
        Assert.False(map.ContainsKey("old.cs"));
        Assert.Equal(new Git.FileStatus('A', ' '), map["added.cs"]);
        Assert.False(map.ContainsKey("somedir/"));
        Assert.True(map["modified.cs"].Changed);
        Assert.False(new Git.FileStatus(' ', ' ').Changed);
    }

    [Fact]
    public void ParseBranches_MarksCurrent_AndAuthorFields()
    {
        var sout = "*\tmain\tabc1234\t2026-01-02\tAlice\t<alice@x>\tlatest subject\n" +
                   " \torigin/main\tdef5678\t2026-01-01\tBob\t<bob@x>\tolder\n";
        var branches = Git.ParseBranches(sout);

        Assert.Equal(2, branches.Count);
        Assert.True(branches[0].Current);
        Assert.Equal("main", branches[0].Name);
        Assert.Equal("abc1234", branches[0].Hash);
        Assert.Equal("Alice", branches[0].Author);
        Assert.Equal("<alice@x>", branches[0].Email);
        Assert.False(branches[1].Current);
        Assert.Equal("origin/main", branches[1].Name);
    }

    [Fact]
    public void ParseWorktrees_HandlesBranchAndDetached()
    {
        var sout =
            "worktree /repo/main\n" +
            "HEAD 1234567890abcdef\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree /repo/hotfix\n" +
            "HEAD abcdef1234567890\n" +
            "detached\n" +
            "\n";
        var wts = Git.ParseWorktrees(sout);

        Assert.Equal(2, wts.Count);
        Assert.Equal("/repo/main", wts[0].Path);
        Assert.Equal("main", wts[0].Branch);
        Assert.Equal("/repo/hotfix", wts[1].Path);
        Assert.Contains("detached", wts[1].Branch);
    }

    [Fact]
    public void ParseStashIndex_FindsTheLinkedEntry()
    {
        var list =
            "stash@{0}: On main: qxgit:feature-x\n" +
            "stash@{1}: WIP on main: abc1234 old\n" +
            "stash@{2}: On main: qxgit:main\n";
        Assert.Equal(2, Git.ParseStashIndex(list, "main"));
        Assert.Equal(0, Git.ParseStashIndex(list, "feature-x"));
        Assert.Equal(-1, Git.ParseStashIndex(list, "nope"));
    }

    [Fact]
    public void BuildTree_NestsDirectories_MarksLeaves_AppendsUntracked()
    {
        string[] files = ["src/a.cs", "src/b.cs", "readme.md"];
        var status = new Dictionary<string, Git.FileStatus>
        {
            ["src/a.cs"] = new Git.FileStatus(' ', 'M'),
            ["fresh.cs"] = new Git.FileStatus('?', '?'),
        };

        TreeNode root = Git.BuildTree("repo", files, status);

        Assert.Equal("repo", root.Label);
        Assert.Equal(3, root.Children.Count); // src/, readme.md, fresh.cs (untracked appended)

        var src = root.Children[0];
        Assert.Equal("src", src.Label);
        Assert.True(src.IsParent);
        // porcelain-style two-letter prefix: index column, worktree column
        Assert.Equal(" M a.cs", src.Children[0].Label);
        Assert.Equal("src/a.cs", src.Children[0].Tag);
        Assert.Equal("   b.cs", src.Children[1].Label); // clean: blank marker

        var readme = root.Children[1];
        Assert.Equal("   readme.md", readme.Label);
        Assert.Equal("readme.md", readme.Tag);

        var fresh = root.Children[2];
        Assert.Equal("?? fresh.cs", fresh.Label);
        Assert.Null(src.Tag); // directories carry no path
    }

    [Fact]
    public async Task RunAsync_RealRepo_RevParseSucceeds()
    {
        // walk up from the test binary to the repository root
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "qxtui.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var (code, sout, _) = await Git.RunAsync(
            dir!.FullName, CancellationToken.None, "rev-parse", "--show-toplevel");

        Assert.Equal(0, code);
        Assert.Contains("qxtui", sout);
    }
}

public class GitTreeViewTests
{
    [Fact]
    public void BuildTree_ChangedOnly_HidesClean_ShowsModifiedAndUntracked()
    {
        // staged additions ARE tracked: ls-files lists them, porcelain
        // marks them index-side only
        string[] files = ["src/a.cs", "src/b.cs", "readme.md", "staged.cs"];
        var status = new Dictionary<string, Git.FileStatus>
        {
            ["src/a.cs"] = new Git.FileStatus(' ', 'M'),
            ["staged.cs"] = new Git.FileStatus('A', ' '), // staged-only counts as changed
            ["scratch.txt"] = new Git.FileStatus('?', '?'),
        };

        TreeNode changed = Git.BuildTree("repo", files, status, changedOnly: true);

        // src/ (leads to a.cs) + staged.cs + scratch.txt; readme/b hidden
        Assert.Equal(3, changed.Children.Count);
        var src = changed.Children[0];
        Assert.Equal("src", src.Label);
        Assert.Single(src.Children);           // only the modified leaf
        Assert.Equal(" M a.cs", src.Children[0].Label);
        Assert.Equal("A  staged.cs", changed.Children[1].Label);
        Assert.Equal("?? scratch.txt", changed.Children[2].Label);
    }

    [Fact]
    public void BuildTree_ChangedOnly_CleanRepo_ReadsClean()
    {
        TreeNode changed = Git.BuildTree("repo", ["a.cs"],
            new Dictionary<string, Git.FileStatus>(), changedOnly: true);
        Assert.Single(changed.Children);
        Assert.Equal("(worktree clean)", changed.Children[0].Label);
        Assert.Null(changed.Children[0].Tag); // not selectable as a diff target
    }
}

public class TreeStatusColorTests
{
    private static (App app, FakeTerminal term) MakeApp(LayoutNode root, int w = 40, int h = 12)
    {
        var term = new FakeTerminal(w, h);
        var app = new App(term, root, targetFps: 60,
            time: new QxTui.Animation.VirtualTimeSource(), asyncPresenting: false);
        return (app, term);
    }

    private static int FgOf(App app, string label)
    {
        var cells = app.Back.Cells;
        int ni = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            ni = cells[i].Char == label[ni] ? ni + 1 : 0;
            if (ni == label.Length) return cells[i - label.Length + 1].Fg;
        }
        return -2;
    }

    [Fact]
    public void StatusColors_PaintLeaves_AndWorstDescendantOnDirs()
    {
        var status = new Dictionary<string, Git.FileStatus>
        {
            ["src/del.cs"] = new Git.FileStatus('D', ' '),
            ["src/mod.cs"] = new Git.FileStatus(' ', 'M'),
            ["new.cs"] = new Git.FileStatus('?', '?'),
            ["staged.cs"] = new Git.FileStatus('A', ' '),
        };
        var root = Git.BuildTree("repo",
            ["src/del.cs", "src/mod.cs", "staged.cs"], status, changedOnly: true);
        var tv = new TreeView(root);
        tv.Node.Grow = 1;
        var (app, _) = MakeApp(UI.Column(0, tv));
        app.RunFrame();
        var theme = app.Theme;

        Assert.Equal(theme.Get(ThemeRole.Danger), FgOf(app, "del.cs"));
        Assert.Equal(theme.Get(ThemeRole.Warning), FgOf(app, "mod.cs"));
        Assert.Equal(theme.Get(ThemeRole.Accent), FgOf(app, "new.cs"));
        Assert.Equal(theme.Get(ThemeRole.Success), FgOf(app, "staged.cs"));
        // the directory shows its worst descendant (deletion beats modify)
        Assert.Equal(theme.Get(ThemeRole.Danger), FgOf(app, "src"));
    }
}
