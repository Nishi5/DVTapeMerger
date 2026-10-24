using System.Globalization;

namespace DVTapeMerger;

public sealed class MainForm : Form
{
    readonly AppSettings settings = AppSettings.Load();
    readonly List<SourceFile> files = new();
    List<MergeGroup> groups = new();
    CancellationTokenSource? cts;

    readonly ListView fileList = new() { View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, Dock = DockStyle.Fill, AllowDrop = true };
    readonly ListView groupList = new() { View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, MultiSelect = false, Dock = DockStyle.Fill };
    readonly ErrorGraphPanel graph = new() { Dock = DockStyle.Fill };
    readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f) };
    readonly TextBox outputBox = new() { Width = 380 };
    readonly ComboBox formatBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly CheckBox saveLogsBox = new() { Text = "レポートとログを保存", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    readonly Button analyzeButton = new() { Text = "解析(&A)", AutoSize = true };
    readonly Button mergeButton = new() { Text = "結合を実行(&M)", AutoSize = true };
    readonly Button cancelButton = new() { Text = "中止", AutoSize = true, Enabled = false };
    readonly ToolStripStatusLabel statusLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "ファイルを追加してください。" };
    readonly ToolStripProgressBar progressBar = new() { Width = 220, Maximum = 1000 };
    readonly Label summaryLabel = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4) };
    readonly List<Control> busyDisabled = new();

    static readonly string[] FormatNames = ["DV (.dv)", "AVI Type-2 (.avi)", "QuickTime (.mov)", "Matroska (.mkv)"];
    static readonly string[] FormatExtensions = [".dv", ".avi", ".mov", ".mkv"];

    public MainForm(string[] initialPaths)
    {
        Text = "DVTapeMerger";
        Font = SystemFonts.MessageBoxFont;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Size = new Size(Math.Min(1280, area.Width * 9 / 10), Math.Min(900, area.Height * 9 / 10));
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterScreen;

        // Menu
        var menu = new MenuStrip { RenderMode = ToolStripRenderMode.System };
        var fileMenu = new ToolStripMenuItem("ファイル(&F)");
        fileMenu.DropDownItems.Add("ファイルを追加(&A)...", null, (_, _) => AddFilesDialog());
        fileMenu.DropDownItems.Add("フォルダーを追加(&D)...", null, (_, _) => AddFolderDialog());
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("終了(&X)", null, (_, _) => Close());
        var toolMenu = new ToolStripMenuItem("ツール(&T)");
        toolMenu.DropDownItems.Add("設定(&S)...", null, (_, _) => ShowSettings());
        var helpMenu = new ToolStripMenuItem("ヘルプ(&H)");
        helpMenu.DropDownItems.Add("バージョン情報(&A)...", null, (_, _) => ShowAbout());
        menu.Items.AddRange([fileMenu, toolMenu, helpMenu]);
        MainMenuStrip = menu;

        // File buttons
        var fileButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 4, 4, 0) };
        var addFiles = new Button { Text = "ファイルを追加...", AutoSize = true };
        var addFolder = new Button { Text = "フォルダーを追加...", AutoSize = true };
        var remove = new Button { Text = "選択を削除", AutoSize = true };
        var clear = new Button { Text = "すべてクリア", AutoSize = true };
        addFiles.Click += (_, _) => AddFilesDialog();
        addFolder.Click += (_, _) => AddFolderDialog();
        remove.Click += (_, _) => RemoveSelected();
        clear.Click += (_, _) => { files.Clear(); ResetGroups(); RefreshFileList(); };
        fileButtons.Controls.AddRange([addFiles, addFolder, remove, clear,
            new Label { Text = "同じテープを複数回キャプチャしたファイル（分割されたもの）をすべて追加してください。ドラッグ＆ドロップもできます。", AutoSize = true, Margin = new Padding(10, 8, 3, 3), ForeColor = SystemColors.GrayText }]);
        busyDisabled.AddRange([addFiles, addFolder, remove, clear]);

        foreach (var (name, width) in new[] { ("ファイル名", 230), ("形式", 50), ("フレーム数", 75), ("長さ", 75), ("TC開始", 90), ("TC終了", 90), ("撮影開始", 135), ("エラーフレーム", 90), ("エラー率", 75), ("区間", 45), ("状態", 120) })
            fileList.Columns.Add(name, width);
        fileList.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        fileList.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths); };
        fileList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        var filePanel = new Panel { Dock = DockStyle.Fill };
        filePanel.Controls.Add(fileList);
        filePanel.Controls.Add(fileButtons);

        // Groups + graph
        foreach (var (name, width) in new[] { ("No", 40), ("撮影開始", 135), ("TC開始", 90), ("TC終了", 90), ("長さ", 75), ("入力数", 55), ("最良入力のエラー率", 120), ("結合後のエラー率", 120), ("結合後のエラーフレーム", 140) })
            groupList.Columns.Add(name, width);
        groupList.SelectedIndexChanged += (_, _) => graph.ShowGroup(groupList.SelectedItems.Count > 0 ? (MergeGroup)groupList.SelectedItems[0].Tag! : null);
        graph.HoverInfo += text => statusLabel.Text = text;

        var groupSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        groupSplit.Panel1.Controls.Add(groupList);
        groupSplit.Panel1.Controls.Add(summaryLabel);
        groupSplit.Panel2.Controls.Add(graph);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var groupTab = new TabPage("結合グループとエラー分布");
        groupTab.Controls.Add(groupSplit);
        var logTab = new TabPage("ログ");
        logTab.Controls.Add(logBox);
        tabs.TabPages.AddRange([groupTab, logTab]);

        var mainSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        mainSplit.Panel1.Controls.Add(filePanel);
        mainSplit.Panel2.Controls.Add(tabs);

        // Output
        var outputPanel = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(4) };
        var row1 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var browse = new Button { Text = "参照...", AutoSize = true };
        browse.Click += (_, _) => BrowseOutput();
        formatBox.Items.AddRange(FormatNames);
        formatBox.SelectedIndex = Math.Clamp(settings.OutputFormat, 0, FormatNames.Length - 1);
        formatBox.SelectedIndexChanged += (_, _) => FixOutputExtension();
        row1.Controls.AddRange([new Label { Text = "出力ファイル:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, outputBox, browse,
            new Label { Text = "形式:", AutoSize = true, Margin = new Padding(12, 7, 3, 3) }, formatBox, saveLogsBox]);
        var row2 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        row2.Controls.AddRange([cancelButton, mergeButton, analyzeButton]);
        outputPanel.Controls.Add(row1);
        outputPanel.Controls.Add(row2);
        busyDisabled.AddRange([outputBox, browse, formatBox, saveLogsBox, analyzeButton, mergeButton]);

        outputBox.Text = settings.LastOutput;
        saveLogsBox.Checked = settings.SaveLogs;
        analyzeButton.Click += async (_, _) => await AnalyzeAsync();
        mergeButton.Click += async (_, _) => await MergeAsync();
        cancelButton.Click += (_, _) => cts?.Cancel();

        var status = new StatusStrip { RenderMode = ToolStripRenderMode.System };
        status.Items.AddRange([statusLabel, progressBar]);

        Controls.Add(mainSplit);
        Controls.Add(outputPanel);
        Controls.Add(menu);
        Controls.Add(status);

        Load += (_, _) =>
        {
            mainSplit.SplitterDistance = 230;
            groupSplit.SplitterDistance = 190;
        };
        FormClosing += OnClosing;

        // Files dropped on the executable
        if (initialPaths.Length > 0)
            Shown += (_, _) => AddPaths(initialPaths);
    }

    // ----------------------------------------------------------------------------------------

    string WorkDir => string.IsNullOrWhiteSpace(settings.WorkDir) ? Path.Combine(Path.GetTempPath(), "DVTapeMerger") : settings.WorkDir;

    MergeEngine CreateEngine() => new()
    {
        WorkDir = WorkDir,
        Dvrescue = ToolRunner.FindTool(settings.DvrescuePath, "dvrescue.exe"),
        Ffmpeg = ToolRunner.FindTool(settings.FfmpegPath, "ffmpeg.exe"),
        Log = AppendLog,
        Progress = (p, text) => BeginInvoke(() =>
        {
            progressBar.Value = (int)Math.Clamp(p * 1000, 0, 1000);
            statusLabel.Text = text;
        }),
    };

    void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }
        logBox.AppendText(DateTime.Now.ToString("HH:mm:ss ", CultureInfo.InvariantCulture) + line + Environment.NewLine);
    }

    void SetBusy(bool busy)
    {
        foreach (var c in busyDisabled)
            c.Enabled = !busy;
        cancelButton.Enabled = busy;
        fileList.AllowDrop = !busy;
        UseWaitCursor = busy;
    }

    void AddFilesDialog()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Filter = "DVキャプチャ (*.dv;*.avi)|*.dv;*.avi;*.dif|すべてのファイル|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            AddPaths(dlg.FileNames);
    }

    void AddFolderDialog()
    {
        using var dlg = new FolderBrowserDialog { Description = "キャプチャファイルが入ったフォルダー（サブフォルダーも含めて追加します）" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            AddPaths([dlg.SelectedPath]);
    }

    void AddPaths(IEnumerable<string> paths)
    {
        if (cts != null)
            return;
        var existing = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            IEnumerable<string> candidates = Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(IsSupported).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                : IsSupported(p) ? [p] : [];
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (existing.Add(full))
                    files.Add(new SourceFile { Path = full });
            }
        }
        if (string.IsNullOrWhiteSpace(outputBox.Text) && files.Count > 0)
            outputBox.Text = Path.Combine(Path.GetDirectoryName(files[0].Path)!, "merged" + FormatExtensions[formatBox.SelectedIndex]);
        ResetGroups();
        RefreshFileList();
    }

    static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".dv" or ".avi" or ".dif";
    }

    void RemoveSelected()
    {
        if (cts != null)
            return;
        foreach (ListViewItem item in fileList.SelectedItems)
            files.Remove((SourceFile)item.Tag!);
        ResetGroups();
        RefreshFileList();
    }

    void ResetGroups()
    {
        groups = new();
        RefreshGroupList();
    }

    void RefreshFileList()
    {
        fileList.BeginUpdate();
        fileList.Items.Clear();
        foreach (var f in files)
        {
            var a = f.Analysis;
            string[] cols;
            if (a == null)
            {
                cols = [f.FileName, f.IsAvi ? "AVI" : "DV", "", "", "", "", "", "", "", "", f.Error != null ? "エラー: " + f.Error : "未解析"];
            }
            else
            {
                var firstTc = a.Frames.FirstOrDefault(x => x.Tc >= 0);
                var lastTc = a.Frames.LastOrDefault(x => x.Tc >= 0);
                var rdt = a.Frames.FirstOrDefault(x => x.Rdt != 0).Rdt;
                cols = [f.FileName, f.IsAvi ? "AVI" : "DV", a.Frames.Length.ToString("N0"), FormatDuration(a.Frames.Length / a.FrameRate),
                    firstTc.Tc >= 0 ? DvParser.FramesToTimeCode(firstTc.Tc, a.TcBase, firstTc.DropFrame) : "なし",
                    lastTc.Tc >= 0 ? DvParser.FramesToTimeCode(lastTc.Tc, a.TcBase, lastTc.DropFrame) : "なし",
                    rdt != 0 ? new DateTime(rdt).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "なし",
                    a.ErrorFrameCount.ToString("N0"), a.ErrorBlockRatio.ToString("P3"), f.Runs.Count.ToString(), "解析済み" + (a.IsPal ? " (PAL)" : " (NTSC)")];
            }
            var item = new ListViewItem(cols) { Tag = f, ToolTipText = f.Path };
            if (f.Error != null)
                item.ForeColor = Color.Firebrick;
            fileList.Items.Add(item);
        }
        fileList.ShowItemToolTips = true;
        FitColumns(fileList);
        fileList.EndUpdate();
        int analyzed = files.Count(f => f.Analysis != null);
        statusLabel.Text = $"{files.Count} ファイル（解析済み {analyzed}）";
    }

    void RefreshGroupList()
    {
        groupList.BeginUpdate();
        groupList.Items.Clear();
        foreach (var g in groups)
        {
            var a = g.Runs[0].Source.Analysis!;
            bool df = a.Frames.FirstOrDefault(x => x.Tc >= 0).DropFrame;
            int frames = g.Runs.Max(r => r.FrameCount);
            var item = new ListViewItem([
                g.Index.ToString(),
                g.RdtMin != 0 ? new DateTime(g.RdtMin).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "なし",
                DvParser.FramesToTimeCode(g.TcStart, a.TcBase, df),
                DvParser.FramesToTimeCode(g.TcEnd, a.TcBase, df),
                FormatDuration((g.Result?.Frames.Length ?? frames) / a.FrameRate),
                g.Runs.Count.ToString(),
                g.Runs.Min(r => r.ErrorRatio).ToString("P3"),
                g.Result != null ? g.Result.ErrorBlockRatio.ToString("P3") : "",
                g.Result != null ? g.Result.ErrorFrameCount.ToString("N0") : "",
            ]) { Tag = g };
            if (g.Runs.Count == 1)
                item.ForeColor = SystemColors.GrayText;
            groupList.Items.Add(item);
        }
        FitColumns(groupList);
        groupList.EndUpdate();
        graph.ShowGroup(null);

        if (groups.Count == 0)
        {
            summaryLabel.Text = "解析すると、テープ上の同じ部分を含むファイルがグループにまとめられます。";
            return;
        }
        int single = groups.Count(g => g.Runs.Count == 1);
        var text = $"{groups.Count} グループ（うち入力が1件だけのもの {single}）。グループを選ぶと下にエラー分布を表示します。";
        if (groups.All(g => g.Result != null))
        {
            long frames = groups.Sum(g => (long)g.Result!.Frames.Length);
            long errFrames = groups.Sum(g => (long)g.Result!.ErrorFrameCount);
            text += $"  結合後: 全 {frames:N0} フレーム中、エラーが残るフレーム {errFrames:N0}";
        }
        summaryLabel.Text = text;
        if (groupList.Items.Count > 0)
            groupList.Items[0].Selected = true;
    }

    static void FitColumns(ListView list)
    {
        foreach (ColumnHeader column in list.Columns)
        {
            column.AutoResize(ColumnHeaderAutoResizeStyle.HeaderSize);
            int header = column.Width;
            if (list.Items.Count == 0)
                continue;
            column.AutoResize(ColumnHeaderAutoResizeStyle.ColumnContent);
            column.Width = Math.Min(Math.Max(column.Width, header), 320);
        }
    }

    static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    void BrowseOutput()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = string.Join("|", FormatNames.Select((n, i) => $"{n}|*{FormatExtensions[i]}")),
            FilterIndex = formatBox.SelectedIndex + 1,
            FileName = Path.GetFileName(outputBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(outputBox.Text)) ? Path.GetDirectoryName(outputBox.Text) : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            formatBox.SelectedIndex = dlg.FilterIndex - 1;
            outputBox.Text = dlg.FileName;
            FixOutputExtension();
        }
    }

    void FixOutputExtension()
    {
        if (!string.IsNullOrWhiteSpace(outputBox.Text))
            outputBox.Text = Path.ChangeExtension(outputBox.Text, FormatExtensions[formatBox.SelectedIndex]);
    }

    async Task<bool> AnalyzeAsync()
    {
        if (files.Count == 0)
        {
            MessageBox.Show(this, "ファイルを追加してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        var engine = CreateEngine();
        if (files.Any(f => f.IsAvi && f.Analysis == null) && engine.Ffmpeg == null)
        {
            MessageBox.Show(this, "AVIファイルの読み込みにはffmpeg.exeが必要です。[ツール]→[設定]で場所を指定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await engine.AnalyzeAsync(files, cts.Token);
            groups = Grouping.BuildGroups(files);
            AppendLog($"グループ分け: {groups.Count} グループ");
            return files.Any(f => f.Analysis != null);
        }
        catch (OperationCanceledException)
        {
            AppendLog("中止しました。");
            statusLabel.Text = "中止しました。";
            return false;
        }
        finally
        {
            cts.Dispose();
            cts = null;
            SetBusy(false);
            RefreshFileList();
            RefreshGroupList();
        }
    }

    async Task MergeAsync()
    {
        if (files.Count == 0 || files.Any(f => f.Analysis == null && f.Error == null) || groups.Count == 0)
        {
            if (!await AnalyzeAsync())
                return;
        }
        if (files.Any(f => f.Error != null)
            && MessageBox.Show(this, "解析できなかったファイルがあります。それらを除いて結合しますか？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        var output = outputBox.Text.Trim();
        if (output.Length == 0 || !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(output))))
        {
            MessageBox.Show(this, "出力ファイルの場所を指定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        output = Path.GetFullPath(output);
        if (files.Any(f => string.Equals(f.Path, output, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "出力ファイルが入力ファイルと同じです。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (File.Exists(output) && MessageBox.Show(this, $"{Path.GetFileName(output)} は既に存在します。上書きしますか？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var engine = CreateEngine();
        if (engine.Dvrescue == null)
        {
            MessageBox.Show(this, "dvrescue.exe が見つかりません。[ツール]→[設定]で場所を指定してください。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var format = (OutputFormat)formatBox.SelectedIndex;
        if (format != OutputFormat.Dv && engine.Ffmpeg == null)
        {
            MessageBox.Show(this, ".dv以外の形式で出力するにはffmpeg.exeが必要です。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!CheckFreeSpace(output))
            return;

        settings.LastOutput = output;
        settings.OutputFormat = formatBox.SelectedIndex;
        settings.SaveLogs = saveLogsBox.Checked;
        settings.Save();

        foreach (var g in groups)
        {
            g.Result = null;
            g.OutputPath = null;
        }

        cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            await engine.MergeAsync(groups, output, format, saveLogsBox.Checked, cts.Token);
            System.Media.SystemSounds.Asterisk.Play();
            statusLabel.Text = "完了: " + output;
        }
        catch (OperationCanceledException)
        {
            AppendLog("中止しました。");
            statusLabel.Text = "中止しました。";
        }
        catch (Exception e)
        {
            AppendLog("エラー: " + e.Message);
            MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            cts.Dispose();
            cts = null;
            SetBusy(false);
            if (groups.Any(g => g.Result == null))
                foreach (var g in groups)
                    g.Result = null;
            RefreshGroupList();
        }
    }

    bool CheckFreeSpace(string output)
    {
        try
        {
            long frameSize = groups[0].Runs[0].Source.Analysis!.FrameSize;
            long outputBytes = groups.Sum(g => (long)g.Runs.Max(r => r.FrameCount)) * frameSize;
            Directory.CreateDirectory(WorkDir);
            var workDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(WorkDir))!);
            var outDrive = new DriveInfo(Path.GetPathRoot(output)!);
            long needWork = groups.Where(g => g.Runs.Count > 1).Sum(g => (long)g.Runs.Max(r => r.FrameCount)) * frameSize;
            long needOut = outputBytes;
            if (string.Equals(workDrive.Name, outDrive.Name, StringComparison.OrdinalIgnoreCase))
                needWork += needOut;
            string? problem = null;
            if (workDrive.AvailableFreeSpace < needWork)
                problem = $"作業フォルダーのドライブ ({workDrive.Name}) の空き容量が不足する可能性があります。\n必要: 約 {needWork / 1e9:F1} GB / 空き: {workDrive.AvailableFreeSpace / 1e9:F1} GB";
            else if (outDrive.AvailableFreeSpace < needOut)
                problem = $"出力先ドライブ ({outDrive.Name}) の空き容量が不足する可能性があります。\n必要: 約 {needOut / 1e9:F1} GB / 空き: {outDrive.AvailableFreeSpace / 1e9:F1} GB";
            if (problem != null)
                return MessageBox.Show(this, problem + "\n\n続行しますか？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        return true;
    }

    void ShowSettings()
    {
        using var dlg = new SettingsForm(settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
            settings.Save();
    }

    void ShowAbout()
    {
        MessageBox.Show(this,
            "DVTapeMerger " + Application.ProductVersion.Split('+')[0] + "\n\n" +
            "同じDVテープを複数回キャプチャしたファイルから、良好なフレームを選んで1本にまとめます。\n\n" +
            "フレーム単位の結合処理には外部ツール dvrescue (BSD 3-Clause License, MIPoPS) を、\n" +
            "AVIの読み込みとコンテナ出力には外部ツール FFmpeg を使用します。\n" +
            "これらのツールは本ソフトウェアには含まれていません。",
            "バージョン情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (cts != null)
        {
            if (MessageBox.Show(this, "処理中です。中止して終了しますか？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            cts.Cancel();
        }
        settings.LastOutput = outputBox.Text.Trim();
        settings.OutputFormat = formatBox.SelectedIndex;
        settings.SaveLogs = saveLogsBox.Checked;
        settings.Save();

        // DV streams extracted from AVI files are only a cache
        try
        {
            var cache = Path.Combine(WorkDir, "cache");
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
