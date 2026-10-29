using QxTui.Core;
using QxTui.Layout;
using QxTui.QxGit;
using QxTui.Reactive;
using QxTui.Render;
using QxTui.Terminal;
using QxTui.Widgets;

// ---------------------------------------------------------------------------
// qxgit — a keyboard+mouse git browser built on qxtui.
//
// A real application, not a demo: every git call is a subprocess on a pool
// thread (App.Effect), every result travels back as a message, every piece
// of UI state is a signal. The frame thread never blocks on git; a slow
// repository shows "loading…" instead of stuttering the render loop.
//
//   worktree tree ── select a file ──► diff pane (git diff HEAD -- path)
//   commit table  ── select a row  ──► diff pane (git show <hash>)
//                   Enter            ► full-diff overlay modal
//   /             ── focus filter    ► live subject/author/hash filter
//   r             ── refresh log+files    q ── quit    Tab ── cycle focus
// ---------------------------------------------------------------------------

if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("qxgit: POSIX only (the terminal layer is).");
    return 1;
}

string startDir = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
const string LogFormat = "%H%x09%an%x09%ad%x09%s";

// ---- state (frame-thread only; effects return messages, never touch this) --

Git.Commit[] commits = [];
Git.Commit[] filtered = [];
string[] diffLines = [];
int diffReq = 0;          // monotonic; stale DiffMsg results are dropped
string repoRoot = startDir;
Dictionary<string, char> fileStatus = new();
string[] lastFiles = [];      // cached FilesMsg payload for tree rebuilds
bool showAllFiles = false;    // default: the STATUS view (changed files only)
Modal? diffModal = null;  // the open full-diff window (its close chip needs it)

var branch = new Signal<string>("…");
var busy = new Signal<string>("");
var diffTitle = new Signal<string>("select a commit or file");

var terminal = new PosixTerminal();
var app = new App(terminal, new LayoutNode(), targetFps: 120);

// ---- widgets ----------------------------------------------------------------

var tree = new TreeView(new TreeNode("(loading…)"))
{
    Tooltip = "worktree · select a file for its diff · Enter opens it full-size",
};

var logTable = new Table(() => filtered.Length,
    new TableColumn("hash", Width: 9),
    new TableColumn("date", Width: 11),
    new TableColumn("author", Width: 14),
    new TableColumn("subject", Weight: 3))
{
    Cell = (r, c) =>
    {
        var cm = filtered[r];
        return c switch
        {
            0 => cm.Hash[..8],
            1 => cm.Date,
            2 => cm.Author,
            _ => cm.Subject,
        };
    },
    Tooltip = "commit log · arrows/click select · Enter opens the full diff",
};

var filterField = new TextField { Placeholder = "filter commits… (Esc clears)" };

// one diff renderer, shared by the pane and the full-size overlay
static int DiffFg(string line, Theme theme) =>
    line.StartsWith("+++") || line.StartsWith("---") ? theme.Get(ThemeRole.Faint) :
    line.StartsWith('+') ? theme.Get(ThemeRole.Success) :
    line.StartsWith('-') ? theme.Get(ThemeRole.Danger) :
    line.StartsWith("@@") ? theme.Get(ThemeRole.Accent) :
    line.StartsWith("diff ") || line.StartsWith("index ") ? theme.Get(ThemeRole.Dim) :
    line.StartsWith("commit ") || line.StartsWith("Author:") || line.StartsWith("Date:")
        ? theme.Get(ThemeRole.Title) :
    theme.Get(ThemeRole.Text);

var diffList = new ListView(() => diffLines.Length)
{
    RenderRow = (buffer, clip, rect, i, selected) =>
    {
        var theme = app.Theme;
        if (selected)
            buffer.FillRect(rect.X, rect.Y, rect.W, rect.H,
                new Cell { Char = ' ', Fg = -1, Bg = theme.Get(ThemeRole.PanelBg) });
        buffer.WriteTextClipped(clip, rect.X, rect.Y, diffLines[i], fg: DiffFg(diffLines[i], theme));
    },
};

var hud = new Label(() =>
    $" qxgit · {repoRoot} [{branch.Value}]{(busy.Value.Length > 0 ? "  " + busy.Value : "")}",
    fg: ThemeRole.Title, bg: ThemeRole.Faint);
var help = new Label(() =>
    " Tab: focus   /: filter   Enter/dbl-click: full diff   a: all/changed files   r: refresh   q: quit ",
    fg: ThemeRole.Text, bg: ThemeRole.Faint);

var filesPanel = new Panel(() => showAllFiles ? "worktree · all" : "worktree · changed");
tree.Node.Grow = 1f;
filesPanel.AddChild(tree);
filesPanel.Node.Width = 36;

var commitsPanel = new Panel(() => $"commits ({filtered.Length})");
var commitsCol = new Container();
commitsCol.Node.Direction = FlexDirection.Column;
commitsCol.Node.Grow = 1f; // fill the panel's inner box (the table grows within)
commitsCol.AddChild(filterField);
filterField.Node.Height = 1;
commitsCol.AddChild(logTable);
logTable.Node.Grow = 1f;
commitsPanel.AddChild(commitsCol);
commitsPanel.Node.Grow = 1f;

var mainRow = new Container();
mainRow.Node.Direction = FlexDirection.Row;
mainRow.Node.Gap = 1;
mainRow.Node.Grow = 3f;
mainRow.AddChild(filesPanel);
mainRow.AddChild(commitsPanel);

var diffPanel = new Panel(() => "diff · " + diffTitle.Value);
diffList.Node.Grow = 1f;
diffPanel.AddChild(diffList);
diffPanel.Node.Grow = 2f;

var page = new Container();
page.Node.Direction = FlexDirection.Column;
page.AddChild(hud);
hud.Node.Height = 1;
page.AddChild(mainRow);
page.AddChild(diffPanel);
page.AddChild(help);
help.Node.Height = 1;

app.SetRoot(page.Node);

// ---- data loading: effects out, messages back --------------------------------

void LoadDiff(string key, params string[] gitArgs)
{
    int req = ++diffReq;
    diffTitle.Set(key);
    busy.Set("loading diff…");
    string cwd = repoRoot;
    app.Effect(async ct =>
    {
        var (code, sout, serr) = await Git.RunAsync(cwd, ct, gitArgs);
        // diff-style commands "fail" with code 1 when differences exist —
        // stdout is the answer either way; stderr only when stdout is empty
        string body = sout.Length > 0 ? sout : serr;
        if (body.Length == 0)
            body = code == 0 ? "(no changes)\n" : "(git failed)\n";
        return new DiffMsg(req, key, body);
    });
}

void LoadCommitDiff(Git.Commit cm) =>
    LoadDiff($"{cm.Hash[..8]} · {cm.Subject}",
        "show", "--patch", "--date=short",
        "--format=commit %H%nAuthor: %an <%ae>%nDate:   %ad%n%n    %s%n",
        cm.Hash);

void LoadFileDiff(string path, char status)
{
    if (status == '?')
        // untracked: diff against nothing (exits 1 with the answer on stdout)
        LoadDiff($"? {path}", "diff", "--no-index", "--", "/dev/null", path);
    else
        LoadDiff(path, "diff", "HEAD", "--", path);
}

void RebuildTree() =>
    tree.Root = Git.BuildTree(Path.GetFileName(repoRoot), lastFiles, fileStatus,
        changedOnly: !showAllFiles);

void ApplyFilter()
{
    string f = filterField.Text.Trim();
    filtered = f.Length == 0
        ? commits
        : [.. commits.Where(c =>
            c.Subject.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            c.Author.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            c.Hash.StartsWith(f, StringComparison.OrdinalIgnoreCase))];
    logTable.SelectedRow = Math.Min(logTable.SelectedRow, Math.Max(0, filtered.Length - 1));
    logTable.Invalidate();
    if (filtered.Length > 0) LoadCommitDiff(filtered[logTable.SelectedRow]);
}

void LoadLog()
{
    busy.Set("loading log…");
    string cwd = repoRoot;
    app.Effect(async ct =>
    {
        var (code, sout, serr) = await Git.RunAsync(cwd, ct,
            "log", "--date=short", $"--format={LogFormat}", "-n", "400");
        return code == 0
            ? new LogMsg(Git.ParseLog(sout))
            : new ToastMsg($"git log: {serr.Trim()}");
    });
}

void LoadFiles()
{
    string cwd = repoRoot;
    app.Effect(async ct =>
    {
        var (c1, filesOut, _) = await Git.RunAsync(cwd, ct, "ls-files");
        var (c2, statusOut, _) = await Git.RunAsync(cwd, ct, "status", "--porcelain");
        if (c1 != 0 || c2 != 0) return new ToastMsg("git ls-files/status failed");
        string[] files = [.. filesOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(1500)];
        return new FilesMsg(files, Git.ParseStatus(statusOut));
    });
}

void LoadRepo()
{
    busy.Set("opening repo…");
    app.Effect(async ct =>
    {
        var (c1, rootOut, _) = await Git.RunAsync(startDir, ct, "rev-parse", "--show-toplevel");
        if (c1 != 0) return new ToastMsg($"not a git repository: {startDir}");
        string root = rootOut.Trim();
        var (_, brOut, _) = await Git.RunAsync(root, ct, "branch", "--show-current");
        string br = brOut.Trim();
        return new RepoMsg(root, br.Length > 0 ? br : "(detached)");
    });
}

void OpenDiffOverlay()
{
    var lines = diffLines; // freeze: the pane may reload underneath the modal
    var body = new ListView(() => lines.Length)
    {
        RenderRow = (buffer, clip, rect, i, _) =>
        {
            buffer.WriteTextClipped(clip, rect.X, rect.Y, lines[i],
                fg: DiffFg(lines[i], app.Theme));
        },
    };
    diffModal = new Modal(diffTitle.Value, () => [], dim: false, chips:
    [
        // close THIS window even if a toast floated above it meanwhile
        new ChipAction("close", Act: () => { if (diffModal is { } m) app.PopOverlay(m); }),
    ])
    {
        Body = body,
        BodyRows = Math.Max(6, app.BackHeight - 10),
        Resizable = true, // it's a window: drag the title bar, right-drag to size
        ForceW = Math.Max(40, app.BackWidth - 12),
        ForceH = Math.Max(12, app.BackHeight - 6),
    };
    app.PushOverlay(diffModal);
}

// ---- intents: the ONE place results become state ------------------------------

app.OnMessage<RepoMsg>(m =>
{
    repoRoot = m.Root;
    branch.Set(m.Branch);
    LoadLog();
    LoadFiles();
});

app.OnMessage<LogMsg>(m =>
{
    commits = [.. m.Commits];
    busy.Set("");
    ApplyFilter();
});

app.OnMessage<FilesMsg>(m =>
{
    fileStatus = m.Status;
    lastFiles = m.Files;
    RebuildTree();
});

app.OnMessage<DiffMsg>(m =>
{
    if (m.Req != diffReq) return; // a newer request is in flight — drop the stale one
    busy.Set("");
    diffLines = m.Text.Split('\n');
    if (diffLines.Length > 0 && diffLines[^1].Length == 0)
        diffLines = diffLines[..^1]; // trailing newline artifact
    diffList.ScrollTo(0);
    diffList.Invalidate();
});

app.OnMessage<ToastMsg>(m =>
{
    busy.Set("");
    app.Toast(m.Text, ToastKind.Error);
});

app.OnMessage<EffectFault>(f =>
{
    busy.Set("");
    app.Toast("qxgit: " + f.Error.Message, ToastKind.Error);
});

// selection → diff pane (events become loads; the pane follows the cursor)
logTable.SelectionChanged += (r, _) =>
{
    if (r < filtered.Length) LoadCommitDiff(filtered[r]);
};
logTable.Activated += (_, _) => OpenDiffOverlay();

tree.SelectionChanged += n =>
{
    if (n.Tag is string path)
        LoadFileDiff(path, fileStatus.TryGetValue(path, out char st) ? st : ' ');
};
tree.Activated += n => { if (n.Tag is not null) OpenDiffOverlay(); };

filterField.Changed += _ => ApplyFilter();

// ---- keys ---------------------------------------------------------------------

app.OnKey = ev =>
{
    switch (ev.Key)
    {
        case Key.Char when ev.Char is 'q':
            app.Quit();
            return false;

        case Key.Char when ev.Char is '/' or 'f':
            app.Focus(filterField);
            return true;

        case Key.Char when ev.Char is 'r':
            LoadLog();
            LoadFiles();
            return true;

        case Key.Char when ev.Char is 'a':
            showAllFiles = !showAllFiles; // status view ↔ full file browser
            RebuildTree();
            return true;

        case Key.Escape:
            if (filterField.Text.Length > 0)
            {
                filterField.SetText(""); // SetText is silent: apply by hand
                ApplyFilter();
                filterField.Invalidate();
            }
            app.Focus(logTable); // Esc always lands you back on the log
            return true;

        default:
            return true;
    }
};

// ---- go -------------------------------------------------------------------------

LoadRepo();
app.Focus(logTable);

using (app)
{
    app.Run();
}

return 0;

// ---- intents (authoring API) -----------------------------------------------------
internal record RepoMsg(string Root, string Branch);
internal record LogMsg(IReadOnlyList<Git.Commit> Commits);
internal record FilesMsg(string[] Files, Dictionary<string, char> Status);
internal record DiffMsg(int Req, string Key, string Text);
internal record ToastMsg(string Text);
