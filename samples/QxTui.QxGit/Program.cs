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
// A FileWatcher keeps the views live when the repo changes under us —
// from another terminal, an editor, or qxgit itself.
//
//   worktree tree ── select a file ──► diff pane (diff HEAD / --cached /
//                    clean file     ─► code view)
//                    s               ── stage/unstage the whole selection
//                    ctrl/shift-click─ multi-select (folders = subtrees)
//   commit table  ── select a row  ──► diff pane (show <hash>)
//                   Enter/dbl-click ─► full-diff overlay window
//   p / P / F     ── pull / push / fetch        c ── commit (prompt)
//   b             ── branches: search name/author/email, enter switches
//                    (auto-stash incl. untracked, linked to the branch you
//                    leave; arriving on a branch with a linked stash pops it)
//                    or creates the typed name when nothing matches
//   m ── merge picker · w ── worktrees (add/remove)
//   / filter · t tree · a all/changed · r refresh · q quit · Tab focus
// ---------------------------------------------------------------------------

if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
{
    Console.Error.WriteLine("qxgit: POSIX only (the terminal layer is).");
    return 1;
}

string startDir = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
const string LogFormat = "%H%x09%an%x09%ad%x09%s";
const string BranchFormat =
    "%(HEAD)%09%(refname:short)%09%(objectname:short)%09%(committerdate:short)" +
    "%09%(authorname)%09%(authoremail)%09%(subject)";

// ---- state (frame-thread only; effects return messages, never touch this) --

Git.Commit[] commits = [];
Git.Commit[] filtered = [];
Git.Branch[] branches = [];
Git.Worktree[] worktrees = [];
string[] diffLines = [];
List<Token[]>? codeTokens = null; // parallel highlight spans for code views
string codeLang = "";
int diffReq = 0;          // monotonic; stale DiffMsg results are dropped
string repoRoot = startDir;
Dictionary<string, Git.FileStatus> fileStatus = new();
string[] lastFiles = [];      // cached FilesMsg payload for tree rebuilds
bool showAllFiles = false;    // default: the STATUS view (changed files only)
Modal? diffModal = null;  // the open full-diff window (its close chip needs it)
FileWatcher? watcher = null; // live refresh once the repo root is known

var branch = new Signal<string>("…");
var busy = new Signal<string>("");
var diffTitle = new Signal<string>("select a commit or file");

var terminal = new PosixTerminal();
var app = new App(terminal, new LayoutNode(), targetFps: 120);
app.DesktopNotifyTitle = "qxgit";

// ---- widgets ----------------------------------------------------------------

var tree = new TreeView(new TreeNode("(loading…)"))
{
    Tooltip = "worktree · click selects · ctrl-click toggles · shift-click range · s stages the selection",
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

static int TokFg(TokenKind k, Theme th) => k switch
{
    TokenKind.Comment => th.Get(ThemeRole.Faint),
    TokenKind.String => th.Get(ThemeRole.Success),
    TokenKind.Keyword => th.Get(ThemeRole.Accent),
    TokenKind.Number => th.Get(ThemeRole.Warning),
    TokenKind.Type => th.Get(ThemeRole.Title),
    TokenKind.Directive => th.Get(ThemeRole.Accent),
    _ => th.Get(ThemeRole.Text),
};

/// <summary>Paint one line: highlighted segments where tokens exist,
/// plain text in the gaps (and for token-less views like diffs).</summary>
static void PaintCodeLine(CellBuffer buffer, RenderClip clip, int x, int y,
    string line, Token[]? toks, Theme theme)
{
    int pos = 0, cx = x;
    if (toks is not null)
    {
        foreach (var t in toks)
        {
            if (t.Start > pos)
            {
                var gap = line[pos..Math.Min(t.Start, line.Length)];
                buffer.WriteTextClipped(clip, cx, y, gap, fg: theme.Get(ThemeRole.Text));
                cx += gap.Length;
            }
            int end = Math.Min(t.Start + t.Length, line.Length);
            if (end <= t.Start) continue;
            var seg = line[t.Start..end];
            buffer.WriteTextClipped(clip, cx, y, seg, fg: TokFg(t.Kind, theme));
            cx += seg.Length;
            pos = end;
        }
    }
    if (pos < line.Length)
        buffer.WriteTextClipped(clip, cx, y, line[pos..], fg: theme.Get(ThemeRole.Text));
}

// one renderer for diff AND code views, shared by pane and overlay
static int DiffFg(string line, Theme theme)
{
    if (line.StartsWith("+++") || line.StartsWith("---")) return theme.Get(ThemeRole.Faint);
    if (line.StartsWith('+')) return theme.Get(ThemeRole.Success);
    if (line.StartsWith('-')) return theme.Get(ThemeRole.Danger);
    if (line.StartsWith("@@")) return theme.Get(ThemeRole.Accent);
    if (line.StartsWith("diff ") || line.StartsWith("index ")) return theme.Get(ThemeRole.Dim);
    if (line.StartsWith("commit ") || line.StartsWith("Author:") || line.StartsWith("Date:"))
        return theme.Get(ThemeRole.Title);
    var t = line.TrimStart();
    if (t.StartsWith("//") || t.StartsWith('#')) return theme.Get(ThemeRole.Dim);
    return theme.Get(ThemeRole.Text);
}

var diffList = new ListView(() => diffLines.Length)
{
    RenderRow = (buffer, clip, rect, i, selected) =>
    {
        var theme = app.Theme;
        if (selected)
            buffer.FillRect(rect.X, rect.Y, rect.W, rect.H,
                new Cell { Char = ' ', Fg = -1, Bg = theme.Get(ThemeRole.PanelBg) });
        if (codeTokens is not null && i < codeTokens.Count)
            PaintCodeLine(buffer, clip, rect.X, rect.Y, diffLines[i], codeTokens[i], theme);
        else
            buffer.WriteTextClipped(clip, rect.X, rect.Y, diffLines[i], fg: DiffFg(diffLines[i], theme));
    },
};

var hud = new Label(() =>
    $" qxgit · {repoRoot} [{branch.Value}]{(busy.Value.Length > 0 ? "  " + busy.Value : "")}",
    fg: ThemeRole.Title, bg: ThemeRole.Faint);


// the worktree panel owns its collapse state; the </> chip and 't' both
// flip IsCollapsed (opt-in per component: Collapsible defaults false)
var filesPanel = new Panel(() => showAllFiles ? "worktree · all" : "worktree · changed")
{
    Collapsible = true,
};
tree.Node.Grow = 1f;
filesPanel.AddChild(tree);
filesPanel.Node.Width = 36;
// long paths get room by dragging SIDEWAYS only — height stays owned by
// the row's cross-axis stretch (a pinned height would freeze on resize)
filesPanel.Resizable = true;
filesPanel.ResizeAxes = ResizeAxis.Horizontal;

void ToggleTree() => filesPanel.IsCollapsed = !filesPanel.IsCollapsed;
filesPanel.CollapseChanged += b =>
{
    // focus must not stay trapped in an invisible pane
    if (b && ReferenceEquals(app.Focused, tree)) app.Focus(logTable);
};

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

// layout: tree + diff share the top row; the commit log is the
// full-width band underneath (swapped per user preference)
var diffPanel = new Panel(() => "diff · " + diffTitle.Value);
diffList.Node.Grow = 1f;
diffPanel.AddChild(diffList);

var mainRow = new Container();
mainRow.Node.Direction = FlexDirection.Row;
mainRow.Node.Gap = 1;
mainRow.Node.Grow = 3f;
mainRow.AddChild(filesPanel);
mainRow.AddChild(diffPanel);
diffPanel.Node.Grow = 1f;

var page = new Container();
page.Node.Direction = FlexDirection.Column;
page.AddChild(hud);
hud.Node.Height = 1;
page.AddChild(mainRow);
commitsPanel.Node.Grow = 2f;
page.AddChild(commitsPanel);


// bottom bar: live view-state readout + one chip whose menu opens
// UPWARD and holds every action (replaces the old two-line key help)
var statusBar = new Label(() =>
    $" {tree.SelectedNodes.Count} selected · sort: " +
    (logTable.SortColumn < 0 ? "none" :
        $"{new[] { "hash", "date", "author", "subject" }[logTable.SortColumn]}" +
        (logTable.SortDescending ? " ▼" : " ▲")) +
    $" · filter: {(filterField.Text.Length > 0 ? filterField.Text : "-")} · live: fs-watch",
    fg: ThemeRole.Dim, bg: ThemeRole.Faint);

void OpenActionMenu(Widget anchor)
{
    const int Items = 13;
    int h = Items + 2;
    // anchor above the chip: the box grows upward from the bottom bar
    int y = Math.Max(0, anchor.Rect.Y - h);
    app.PushOverlay(new ContextMenuPopup(
    [
        new MenuItem("pull",              Act: () => GitOp("pull", "pull"), Hint: "p"),
        new MenuItem("push",              Act: () => GitOp("push", "push"), Hint: "P"),
        new MenuItem("fetch",             Act: () => GitOp("fetch", "fetch"), Hint: "F"),
        new MenuItem("commit…",           Act: OpenCommitPrompt, Hint: "c"),
        new MenuItem("branches…",         Act: OpenBranches, Hint: "b"),
        new MenuItem("merge…",            Act: OpenMerge, Hint: "m"),
        new MenuItem("worktrees…",        Act: OpenWorktrees, Hint: "w"),
        new MenuItem("stage selection",   Act: StageSelected, Hint: "s",
            Enabled: tree.SelectedNodes.Any(n => n.Tag is string)),
        new MenuItem("toggle tree panel", Act: ToggleTree, Hint: "t"),
        new MenuItem("all/changed files", Act: () => { showAllFiles = !showAllFiles; RebuildTree(); }, Hint: "a"),
        new MenuItem("refresh",           Act: RefreshAll, Hint: "r"),
        new MenuItem("clear filter+sort", Act: () =>
        {
            filterField.SetText("");
            filterField.Invalidate();
            logTable.SetSort(-1, false);
            ApplyFilter();
        }, Hint: "Esc"),
        new MenuItem("quit",              Act: app.Quit, Hint: "q"),
    ], anchor.Rect.X, y));
}

Button? menuBtn = null;
menuBtn = new Button(() => " menu ▴ ", () => OpenActionMenu(menuBtn!),
    style: ButtonStyle.Outline)
{
    Tooltip = "every action — the menu opens upward",
};
menuBtn.Node.Width = 10;

var bottomBar = new Container();
bottomBar.Node.Direction = FlexDirection.Row;
bottomBar.Node.Height = 1;
bottomBar.AddChild(statusBar);
statusBar.Node.Grow = 1f;
bottomBar.AddChild(menuBtn);
page.AddChild(bottomBar);

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

/// <summary>Clean files have no diff — show them as CODE instead of an
/// empty pane: read the working file straight off disk.</summary>
void LoadCode(string path)
{
    int req = ++diffReq;
    diffTitle.Set($"{path} (code)");
    codeLang = SyntaxHighlighter.LanguageFor(path);
    string full = Path.Combine(repoRoot, path);
    app.Effect(async ct =>
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(full, ct);
            if (lines.Length > 3000)
                lines = [.. lines.Take(3000), $"… ({lines.Length - 3000} more lines)"];
            return new DiffMsg(req, $"{path} (code)", string.Join('\n', lines) + "\n");
        }
        catch (Exception ex)
        {
            return new DiffMsg(req, $"{path} (code)", $"(unreadable: {ex.Message})\n");
        }
    });
}

void LoadCommitDiff(Git.Commit cm) =>
    LoadDiff($"{cm.Hash[..8]} · {cm.Subject}",
        "show", "--patch", "--date=short",
        "--format=commit %H%nAuthor: %an <%ae>%nDate:   %ad%n%n    %s%n",
        cm.Hash);

void LoadFileDiff(string path, Git.FileStatus fs)
{
    if (!fs.Changed)
        LoadCode(path);                            // clean file → code view
    else if (fs.Worktree == '?')
        // untracked: diff against nothing (exits 1 with the answer on stdout)
        LoadDiff($"? {path}", "diff", "--no-index", "--", "/dev/null", path);
    else if (fs.Worktree != ' ')
        LoadDiff(path, "diff", "HEAD", "--", path);
    else
        LoadDiff($"{path} (staged)", "diff", "--cached", "--", path);
}

void ApplyFilter()
{
    string f = filterField.Text.Trim();
    IEnumerable<Git.Commit> q = f.Length == 0
        ? commits
        : commits.Where(c =>
            c.Subject.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            c.Author.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            c.Hash.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    // header double-click sort: the table owns the state, we own the order
    if (logTable.SortColumn >= 0)
    {
        Func<Git.Commit, string> key = logTable.SortColumn switch
        {
            0 => c => c.Hash,
            1 => c => c.Date,
            2 => c => c.Author,
            _ => c => c.Subject,
        };
        q = logTable.SortDescending
            ? q.OrderByDescending(key, StringComparer.Ordinal)
            : q.OrderBy(key, StringComparer.Ordinal);
    }
    filtered = [.. q];
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

void LoadBranches()
{
    string cwd = repoRoot;
    app.Effect(async ct =>
    {
        var (code, sout, _) = await Git.RunAsync(cwd, ct,
            "for-each-ref", $"--format={BranchFormat}", "refs/heads", "refs/remotes");
        return code == 0 ? new BranchesMsg(Git.ParseBranches(sout)) : null;
    });
}

void LoadWorktrees()
{
    string cwd = repoRoot;
    app.Effect(async ct =>
    {
        var (code, sout, _) = await Git.RunAsync(cwd, ct, "worktree", "list", "--porcelain");
        return code == 0 ? new WorktreesMsg(Git.ParseWorktrees(sout)) : null;
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

void RefreshAll()
{
    LoadLog();
    LoadFiles();
    LoadBranches();
    LoadWorktrees();
}

/// <summary>One funnel for every mutating git op: busy flag in, effect
/// out, GitOpMsg back — toast (in-TUI AND desktop) + full refresh on
/// success. Nothing else in this file spawns git directly.</summary>
void GitOp(string what, params string[] gitArgs)
{
    busy.Set(what + "…");
    string cwd = repoRoot;
    app.Effect(async ct =>
    {
        var (code, sout, serr) = await Git.RunAsync(cwd, ct, gitArgs);
        string tail = (sout.Length > 0 ? sout : serr).Trim();
        int nl = tail.LastIndexOf('\n');
        return new GitOpMsg(what, code, nl >= 0 ? tail[(nl + 1)..] : tail);
    });
}

/// <summary>Sequential multi-command op (delete local THEN push
/// --delete): stops at the first failure, reports the failing line.</summary>
void GitOpMany(string what, params string[][] cmds)
{
    busy.Set(what + "…");
    string cwd = repoRoot;
    app.Effect(async ct =>
    {
        var log = new List<string>();
        foreach (var c in cmds)
        {
            var (code, sout, serr) = await Git.RunAsync(cwd, ct, c);
            if (code != 0)
            {
                string tail = (serr.Length > 0 ? serr : sout).Trim();
                int nl = tail.LastIndexOf('\n');
                return new GitOpMsg(what, 1, nl >= 0 ? tail[(nl + 1)..] : tail);
            }
            log.Add($"{c[0]} {c[1]}");
        }
        return new GitOpMsg(what, 0, string.Join(" · ", log));
    });
}

void RebuildTree()
{
    // carry the selection across the rebuild: same tags, new node objects
    var oldTags = tree.SelectedNodes.Select(n => n.Tag as string)
        .Where(t => t is not null).Select(t => t!).ToHashSet();
    tree.Root = Git.BuildTree(Path.GetFileName(repoRoot), lastFiles, fileStatus,
        changedOnly: !showAllFiles);
    if (oldTags.Count == 0) return;
    var byTag = new Dictionary<string, TreeNode>();
    void Index(TreeNode n)
    {
        if (n.Tag is string t) byTag[t] = n;
        foreach (var c in n.Children) Index(c);
    }
    Index(tree.Root);
    var nodes = oldTags.Select(t => byTag.TryGetValue(t, out var n) ? n : null)
        .Where(n => n is not null).Select(n => n!).ToList();
    if (nodes.Count > 0) tree.SelectNodes(nodes);
}

void OpenDiffOverlay()
{
    var lines = diffLines; // freeze: the pane may reload underneath the modal
    var toks = codeTokens;
    var body = new ListView(() => lines.Length)
    {
        RenderRow = (buffer, clip, rect, i, _) =>
        {
            if (toks is not null && i < toks.Count)
                PaintCodeLine(buffer, clip, rect.X, rect.Y, lines[i], toks[i], app.Theme);
            else
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

// ---- pickers (framework Picker/Prompt as overlays) ----------------------------

/// <summary>Pure filter behind the branch picker: branch name, author
/// name OR author email of the pointed-at commit, case-insensitive. The
/// picker's commit callback maps its index through the SAME function, so
/// what you see is what Enter selects.</summary>
Git.Branch[] BranchMatches(string q) =>
    q.Trim().Length == 0
        ? branches
        : [.. branches.Where(b =>
            b.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            b.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            b.Email.Contains(q, StringComparison.OrdinalIgnoreCase))];

string[] BranchLines(string q) =>
    [.. BranchMatches(q).Select(b =>
        $"{(b.Current ? "* " : "  ")}{b.Name}  {b.Hash}  {b.Author} {b.Email}  {b.Subject}")];

/// <summary>Switch branches with per-branch stash memory: a dirty
/// worktree (untracked included) is stashed under a marker naming the
/// branch we LEAVE; after the checkout lands, a stash marked for the
/// branch we ARRIVED on is popped. Coming back to a branch restores
/// exactly the desk you left on it. Creating a branch carries changes
/// over instead (same HEAD — stashing would be churn).</summary>
void SwitchToBranch(string name, bool create = false)
{
    string from = branch.Value;
    bool dirty = fileStatus.Values.Any(st => st.Changed);
    app.Effect(async ct =>
    {
        var log = new List<string>();
        if (dirty && !create)
        {
            var (sc, _, se) = await Git.RunAsync(repoRoot, ct,
                "stash", "push", "--include-untracked", "-m", $"qxgit:{from}");
            if (sc != 0)
                return new GitOpMsg($"switch {name}", 1, $"stash failed: {se.Trim()}");
            log.Add($"stashed {from}");
        }
        var (cc, _, ce) = await Git.RunAsync(repoRoot, ct,
            create ? ["checkout", "-b", name] : ["checkout", name]);
        if (cc != 0)
            return new GitOpMsg($"switch {name}", 1, ce.Trim().Split('\n')[0]);
        log.Add(create ? $"created {name}" : $"on {name}");

        var (lc, lo, _) = await Git.RunAsync(repoRoot, ct, "stash", "list");
        int idx = lc == 0 ? Git.ParseStashIndex(lo, name) : -1;
        if (idx >= 0)
        {
            var (pc, _, pe) = await Git.RunAsync(repoRoot, ct, "stash", "pop", $"stash@{{{idx}}}");
            if (pc != 0)
                return new GitOpMsg($"switch {name}", 1,
                    string.Join(" · ", log) + " · stash pop CONFLICT: " + pe.Trim().Split('\n')[0]);
            log.Add($"restored stash of {name}");
        }
        return new GitOpMsg($"switch {name}", 0, string.Join(" · ", log));
    });
}

/// <summary>Delete dialog: the alternatives, each labelled with the exact
/// command it runs. The remote option appears only when a remote
/// counterpart (origin/&lt;name&gt;) exists — and says so.</summary>
void OpenBranchDelete(Git.Branch b)
{
    if (b.Current)
    {
        app.Toast($"cannot delete the current branch ({b.Name})", ToastKind.Warning);
        return;
    }
    var options = new List<(string Label, Action Act)>();
    int slash = b.Name.IndexOf('/');
    if (slash > 0)
    {
        // a remote-tracking ref (origin/feat): you can delete the branch
        // ON the remote (the one you created by pushing), or just drop
        // your local tracking copy
        string remoteName = b.Name[..slash];
        string remoteBranch = b.Name[(slash + 1)..];
        options.Add(($"delete REMOTE branch   git push {remoteName} --delete {remoteBranch}",
            () => GitOp($"delete remote {remoteBranch}",
                "push", remoteName, "--delete", remoteBranch)));
        options.Add(($"remove tracking ref    git branch -dr {b.Name}",
            () => GitOp($"prune {b.Name}", "branch", "-dr", b.Name)));
    }
    else
    {
        var remote = branches.FirstOrDefault(x => x.Name == "origin/" + b.Name);
        string? remoteName = remote?.Name.Split('/')[0];
        options.Add(($"delete local         git branch -d {b.Name}",
            () => GitOp($"delete {b.Name}", "branch", "-d", b.Name)));
        options.Add(($"FORCE delete local   git branch -D {b.Name}",
            () => GitOp($"force delete {b.Name}", "branch", "-D", b.Name)));
        if (remoteName is not null)
        {
            options.Add(($"delete local + remote  git push {remoteName} --delete {b.Name}",
                () => GitOpMany($"delete {b.Name} + remote",
                    ["branch", "-d", b.Name],
                    ["push", remoteName, "--delete", b.Name])));
        }
    }
    var labels = options.Select(o => o.Label).ToArray();
    app.PushOverlay(new Picker(
        $"delete {b.Name} · enter runs it · esc cancels",
        () => labels,
        (i, _) =>
        {
            if (i >= options.Count) return;
            app.PopOverlay();
            options[i].Act();
        }));
}

void OpenBranches()
{
    LoadBranches();
    app.PushOverlay(new Picker(
        "branches · type: name/author/email · enter: switch/create · ^d: delete",
        () => BranchLines(""),
        (i, q) =>
        {
            var m = BranchMatches(q);
            if (i >= m.Length) return;
            app.PopOverlay();
            var b = m[i];
            if (b.Current) app.Toast($"already on {b.Name}", ToastKind.Info);
            else SwitchToBranch(b.Name);
        },
        (c, i, q) =>
        {
            var m = BranchMatches(q);
            if (c == '\x04' && i < m.Length)   // ctrl-d: delete dialog
                OpenBranchDelete(m[i]);
        },
        search: BranchLines,
        onCreate: name =>
        {
            app.PopOverlay();
            SwitchToBranch(name, create: true);
        }));
}

void OpenMerge()
{
    LoadBranches();
    app.PushOverlay(new Picker(
        $"merge into {branch.Value} · type to search · enter merges",
        () => MergeLines(""),
        (i, q) =>
        {
            var others = MergeMatches(q);
            if (i >= others.Length) return;
            app.PopOverlay();
            GitOp($"merge {others[i].Name}", "merge", others[i].Name);
        },
        search: MergeLines));
}

Git.Branch[] MergeMatches(string q) =>
    [.. branches.Where(b => !b.Current)
        .Where(b => q.Trim().Length == 0 ||
                    b.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    b.Author.Contains(q, StringComparison.OrdinalIgnoreCase))];

string[] MergeLines(string q) =>
    [.. MergeMatches(q).Select(b => $"{b.Name}  {b.Hash}  {b.Author}  {b.Subject}")];

void OpenWorktrees()
{
    LoadWorktrees();
    app.PushOverlay(new Picker(
        "worktrees · enter shows path · a add · x remove",
        () => [.. worktrees.Select(w => $"{w.Path}  [{w.Branch}]")],
        (i, _) =>
        {
            if (i < worktrees.Length)
                app.Toast($"open with: qxgit {worktrees[i].Path}", ToastKind.Info, desktop: true);
        },
        (c, i, _) =>
        {
            if (c == 'a')
                app.PushOverlay(new Prompt("add worktree", "/path [branch]", spec =>
                {
                    string[] parts = spec.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) return;
                    GitOp("worktree add", parts.Length > 1
                        ? ["worktree", "add", parts[0], parts[1]]
                        : ["worktree", "add", parts[0]]);
                }));
            else if (c == 'x' && i < worktrees.Length)
                GitOp($"worktree remove {worktrees[i].Path}",
                    "worktree", "remove", "--force", worktrees[i].Path);
        }));
}

void OpenCommitPrompt() =>
    app.PushOverlay(new Prompt("commit message (staged changes)", "subject line…",
        msg => GitOp("commit", "commit", "-m", msg)));

/// <summary>Stage/unstage the whole multi-selection (ctrl/shift-click in
/// the tree; folders include their subtrees): any worktree-side change
/// (untracked included) stages via `add`; a selection that is only
/// index-side unstages via `restore --staged`. One git call per set.</summary>
/// <summary>Cursor on a directory: the diff pane becomes a staging
/// preview — every file the current selection (folders expanded) would
/// stage, with its porcelain markers. No git call: pure state.</summary>
void ShowStagePreview(TreeNode dir)
{
    var nodes = tree.SelectedNodes.Where(x => x.Tag is string).ToList();
    diffReq++; // any in-flight diff load is now stale
    diffTitle.Set($"{dir.Label.Trim()} · {nodes.Count} file(s) in selection");
    diffLines = nodes.Count == 0
        ? ["(nothing to stage in selection)"]
        : [.. nodes.Select(x =>
        {
            var p = (string)x.Tag!;
            var fs = fileStatus.TryGetValue(p, out var st) ? st : new Git.FileStatus(' ', ' ');
            return $"{fs.Index}{fs.Worktree} {p}";
        })];
    diffList.ScrollTo(0);
    diffList.Invalidate();
}

bool stageOpPending = false; // clear the tree selection when THIS op lands

void StageSelected()
{
    var paths = tree.SelectedNodes
        .Select(n => n.Tag as string)
        .Where(p => p is not null)
        .Select(p => p!)
        .ToList();
    if (paths.Count == 0)
    {
        app.Toast("nothing selected", ToastKind.Info);
        return;
    }
    Git.FileStatus Of(string p) =>
        fileStatus.TryGetValue(p, out var st) ? st : new Git.FileStatus(' ', ' ');
    var dirty = paths.Where(p => Of(p).Worktree != ' ').ToList();
    if (dirty.Count > 0)
    {
        stageOpPending = true; // the selection is consumed by the op
        GitOp($"stage {dirty.Count} file{(dirty.Count == 1 ? "" : "s")}", ["add", "--", .. dirty]);
        return;
    }
    var staged = paths.Where(p => Of(p).Index != ' ').ToList();
    if (staged.Count == 0)
    {
        app.Toast("nothing to stage", ToastKind.Info);
        return;
    }
    stageOpPending = true;
    GitOp($"unstage {staged.Count} file{(staged.Count == 1 ? "" : "s")}",
        ["restore", "--staged", "--", .. staged]);
}

// ---- intents: the ONE place results become state ------------------------------

app.OnMessage<RepoMsg>(m =>
{
    repoRoot = m.Root;
    branch.Set(m.Branch);
    watcher?.Dispose();
    // ignore .git churn except HEAD/refs: `git status` rewrites .git/index,
    // which would re-trigger the watcher that triggered the status — a
    // perpetual refresh loop that rebuilt the tree (and cleared its
    // selection) twice a second
    watcher = new FileWatcher(app, repoRoot, () => new WatchMsg(),
        include: p => !p.Contains("/.git/", StringComparison.Ordinal)
                      || p.EndsWith("/HEAD", StringComparison.Ordinal)
                      || p.Contains("/.git/refs/", StringComparison.Ordinal));
    RefreshAll();
});

app.OnMessage<WatchMsg>(_ => RefreshAll()); // repo changed under us: stay live

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

app.OnMessage<BranchesMsg>(m =>
{
    branches = [.. m.Branches];
    // the HUD branch AND the stash-link source of truth: without this a
    // switch stamped the stash with the branch we were on at startup
    var cur = branches.FirstOrDefault(b => b.Current);
    if (cur is not null) branch.Set(cur.Name);
});
app.OnMessage<WorktreesMsg>(m => worktrees = [.. m.Worktrees]);

app.OnMessage<GitOpMsg>(m =>
{
    busy.Set("");
    bool ok = m.Code == 0;
    app.Toast($"{m.What}: {(ok ? m.Line : m.Line.Length > 0 ? m.Line : "failed")}",
        ok ? ToastKind.Success : ToastKind.Error, desktop: true);
    if (ok)
    {
        // a consumed selection must not keep shouting its markers after
        // the op ate it — clear BEFORE the refresh rebuilds the tree
        if (stageOpPending)
        {
            stageOpPending = false;
            tree.ClearSelection();
        }
        RefreshAll();
    }
});

app.OnMessage<DiffMsg>(m =>
{
    if (m.Req != diffReq) return; // a newer request is in flight — drop the stale one
    busy.Set("");
    diffLines = m.Text.Split('\n');
    if (diffLines.Length > 0 && diffLines[^1].Length == 0)
        diffLines = diffLines[..^1]; // trailing newline artifact
    // code views get highlight spans (computed once, off the render path);
    // diffs keep their +/- semantics instead
    codeTokens = m.Key.EndsWith("(code)", StringComparison.Ordinal)
        ? SyntaxHighlighter.HighlightAll(diffLines, codeLang)
        : null;
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
        LoadFileDiff(path, fileStatus.TryGetValue(path, out var st) ? st : new Git.FileStatus(' ', ' '));
    else
        ShowStagePreview(n); // a directory cursor: show WHAT would be staged
};
tree.Activated += n => { if (n.Tag is not null) OpenDiffOverlay(); };

filterField.Changed += _ => ApplyFilter();
logTable.SortChanged += (_, _) => ApplyFilter();

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
            RefreshAll();
            return true;

        case Key.Char when ev.Char is 't':
            ToggleTree();
            return true;

        case Key.Char when ev.Char is 'a':
            showAllFiles = !showAllFiles; // status view ↔ full file browser
            RebuildTree();
            return true;

        case Key.Char when ev.Char is 's':
            StageSelected();
            return true;

        case Key.Char when ev.Char is 'c':
            OpenCommitPrompt();
            return true;

        case Key.Char when ev.Char is 'b':
            OpenBranches();
            return true;

        case Key.Char when ev.Char is 'm':
            OpenMerge();
            return true;

        case Key.Char when ev.Char is 'w':
            OpenWorktrees();
            return true;

        case Key.Char when ev.Char is 'p':
            GitOp("pull", "pull");
            return true;

        case Key.Char when ev.Char is 'P':
            GitOp("push", "push");
            return true;

        case Key.Char when ev.Char is 'F':
            GitOp("fetch", "fetch");
            return true;

        case Key.Escape:
            // Esc is the "reset my view" key: filter, sort, then focus
            if (filterField.Text.Length > 0)
            {
                filterField.SetText(""); // SetText is silent: apply by hand
                filterField.Invalidate();
            }
            logTable.SetSort(-1, false); // no-op (no event) when unsorted
            ApplyFilter();
            app.Focus(logTable);
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

watcher?.Dispose();
return 0;

// ---- intents (authoring api) -----------------------------------------------------
internal record RepoMsg(string Root, string Branch);
internal record LogMsg(IReadOnlyList<Git.Commit> Commits);
internal record FilesMsg(string[] Files, Dictionary<string, Git.FileStatus> Status);
internal record BranchesMsg(IReadOnlyList<Git.Branch> Branches);
internal record WorktreesMsg(IReadOnlyList<Git.Worktree> Worktrees);
internal record DiffMsg(int Req, string Key, string Text);
internal record GitOpMsg(string What, int Code, string Line);
internal record WatchMsg;
internal record ToastMsg(string Text);
