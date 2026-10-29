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

        Assert.Equal('M', map["modified.cs"]);
        Assert.Equal('M', map["staged.cs"]);
        Assert.Equal('M', map["both.cs"]);
        Assert.Equal('?', map["untracked.cs"]);
        Assert.Equal('R', map["new.cs"]);
        Assert.False(map.ContainsKey("old.cs"));
        Assert.Equal('A', map["added.cs"]);
        Assert.False(map.ContainsKey("somedir/"));
    }

    [Fact]
    public void BuildTree_NestsDirectories_MarksLeaves_AppendsUntracked()
    {
        string[] files = ["src/a.cs", "src/b.cs", "readme.md"];
        var status = new Dictionary<string, char>
        {
            ["src/a.cs"] = 'M',
            ["fresh.cs"] = '?',
        };

        TreeNode root = Git.BuildTree("repo", files, status);

        Assert.Equal("repo", root.Label);
        Assert.Equal(3, root.Children.Count); // src/, readme.md, fresh.cs (untracked appended)

        var src = root.Children[0];
        Assert.Equal("src", src.Label);
        Assert.True(src.IsParent);
        Assert.Equal("M a.cs", src.Children[0].Label);
        Assert.Equal("src/a.cs", src.Children[0].Tag);
        Assert.Equal("  b.cs", src.Children[1].Label); // clean: blank marker

        var readme = root.Children[1];
        Assert.Equal("  readme.md", readme.Label);
        Assert.Equal("readme.md", readme.Tag);

        var fresh = root.Children[2];
        Assert.Equal("? fresh.cs", fresh.Label);
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
        string[] files = ["src/a.cs", "src/b.cs", "readme.md"];
        var status = new Dictionary<string, char>
        {
            ["src/a.cs"] = 'M',
            ["scratch.txt"] = '?',
        };

        TreeNode changed = Git.BuildTree("repo", files, status, changedOnly: true);

        // src/ (leads to a.cs) + scratch.txt; readme.md and b.cs hidden
        Assert.Equal(2, changed.Children.Count);
        var src = changed.Children[0];
        Assert.Equal("src", src.Label);
        Assert.Single(src.Children);           // only the modified leaf
        Assert.Equal("M a.cs", src.Children[0].Label);
        Assert.Equal("? scratch.txt", changed.Children[1].Label);
    }

    [Fact]
    public void BuildTree_ChangedOnly_CleanRepo_ReadsClean()
    {
        TreeNode changed = Git.BuildTree("repo", ["a.cs"], new Dictionary<string, char>(),
            changedOnly: true);
        Assert.Single(changed.Children);
        Assert.Equal("(worktree clean)", changed.Children[0].Label);
        Assert.Null(changed.Children[0].Tag); // not selectable as a diff target
    }
}
