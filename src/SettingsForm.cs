namespace DVTapeMerger;

public sealed class SettingsForm : Form
{
    readonly TextBox dvrescueBox = new() { Width = 420 };
    readonly TextBox ffmpegBox = new() { Width = 420 };
    readonly TextBox workDirBox = new() { Width = 420 };
    readonly CheckBox useDvrescueBox = new() { Text = "結合処理に dvrescue を使う（通常はオフ。内蔵の結合処理の方がタイムコードの破損に強い）", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };

    public SettingsForm(AppSettings settings)
    {
        Text = "設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = SystemFonts.MessageBoxFont;

        dvrescueBox.Text = settings.DvrescuePath;
        ffmpegBox.Text = settings.FfmpegPath;
        workDirBox.Text = settings.WorkDir;
        useDvrescueBox.Checked = settings.UseDvrescue;

        var table = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        AddRow(table, "dvrescue.exe:", dvrescueBox, () => BrowseFile(dvrescueBox, "dvrescue|dvrescue.exe|実行ファイル|*.exe"));
        AddRow(table, "ffmpeg.exe:", ffmpegBox, () => BrowseFile(ffmpegBox, "ffmpeg|ffmpeg.exe|実行ファイル|*.exe"));
        AddRow(table, "作業フォルダー:", workDirBox, () => BrowseFolder(workDirBox));

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Text = "空欄の場合はこのアプリと同じフォルダー、PATHの順に探します。\n作業フォルダーにはAVIから取り出したDVデータと結合途中のファイルが置かれます（キャプチャファイルと同程度の空き容量が必要です）。英数字のみのパスを推奨します。",
            Margin = new Padding(3, 8, 3, 8),
        };
        table.Controls.Add(note, 0, 3);
        table.SetColumnSpan(note, 3);
        table.Controls.Add(useDvrescueBox, 0, 4);
        table.SetColumnSpan(useDvrescueBox, 3);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        table.Controls.Add(buttons, 0, 5);
        table.SetColumnSpan(buttons, 3);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(table);

        ok.Click += (_, _) =>
        {
            settings.DvrescuePath = dvrescueBox.Text.Trim();
            settings.FfmpegPath = ffmpegBox.Text.Trim();
            settings.WorkDir = workDirBox.Text.Trim();
            settings.UseDvrescue = useDvrescueBox.Checked;
        };
    }

    static void AddRow(TableLayoutPanel table, string label, TextBox box, Action browse)
    {
        int row = table.RowCount++;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }, 0, row);
        table.Controls.Add(box, 1, row);
        var button = new Button { Text = "参照...", AutoSize = true };
        button.Click += (_, _) => browse();
        table.Controls.Add(button, 2, row);
    }

    void BrowseFile(TextBox box, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter, FileName = box.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            box.Text = dlg.FileName;
    }

    void BrowseFolder(TextBox box)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = box.Text };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            box.Text = dlg.SelectedPath;
    }
}
