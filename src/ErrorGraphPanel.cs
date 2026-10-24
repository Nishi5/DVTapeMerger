namespace DVTapeMerger;

/// <summary>
/// Draws, for each input of a group (and for the merged result), rows showing the state of the video,
/// the audio and the time code of every frame, aligned on time code.
/// </summary>
public sealed class ErrorGraphPanel : Control
{
    sealed record Lane(string Label, int[] Tc, short[] Video, short[] Audio, TcState[] TcStates, int VideoBlocks, int AudioBlocks, bool IsResult);

    enum Kind { Video, Audio, TimeCode }

    readonly List<Lane> lanes = new();
    int tcStart, tcEnd, tcBase = 30;
    bool dropFrame;
    string? message = "グループを選択するとエラーの分布を表示します。";
    bool showVideo = true, showAudio = true, showTc = true;

    const int LabelWidth = 300;
    const int RowHeight = 15;
    const int RowGap = 2;
    const int LaneGap = 10;
    const int TopMargin = 28;

    static readonly Color MissingColor = Color.FromArgb(220, 220, 220);
    static readonly Color OkColor = Color.FromArgb(80, 170, 90);
    static readonly Color MinorColor = Color.FromArgb(230, 200, 40);
    static readonly Color MediumColor = Color.FromArgb(240, 130, 30);
    static readonly Color SevereColor = Color.FromArgb(210, 40, 40);
    static readonly Color DerivedColor = Color.FromArgb(120, 160, 230);
    static readonly Color NoTcColor = Color.FromArgb(150, 90, 200);

    public event Action<string>? HoverInfo;

    public ErrorGraphPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = SystemColors.Window;
    }

    public void SetKinds(bool video, bool audio, bool timeCode)
    {
        showVideo = video;
        showAudio = audio;
        showTc = timeCode;
        LayoutChanged?.Invoke();
        Invalidate();
    }

    /// <summary>Height needed to draw all lanes and the legend.</summary>
    public int RequiredHeight
    {
        get
        {
            int kinds = Math.Max(1, Kinds.Count);
            return lanes.Count == 0 ? 60 : TopMargin + lanes.Count * (LaneHeight(kinds) + LaneGap) + 44;
        }
    }

    public event Action? LayoutChanged;

    public void ShowGroup(MergeGroup? group)
    {
        lanes.Clear();
        message = null;
        if (group == null)
        {
            message = "グループを選択するとエラーの分布を表示します。";
            Invalidate();
            return;
        }
        tcStart = group.TcStart;
        tcEnd = group.TcEnd;
        if (tcStart < 0)
        {
            message = "タイムコードがないため表示できません。";
            Invalidate();
            return;
        }
        var a0 = group.Runs[0].Source.Analysis!;
        tcBase = a0.TcBase;
        dropFrame = a0.Frames.FirstOrDefault(f => f.Tc >= 0).DropFrame;

        foreach (var run in group.Runs)
        {
            var a = run.Source.Analysis!;
            lanes.Add(MakeLane(run.Label, a.Frames.AsSpan(run.StartFrame, run.FrameCount), a, false));
        }
        if (group.Result != null)
            lanes.Add(MakeLane("結合後", group.Result.Frames, group.Result, true));
        LayoutChanged?.Invoke();
        Invalidate();
    }

    static Lane MakeLane(string label, ReadOnlySpan<FrameInfo> frames, DvAnalysis a, bool isResult)
    {
        var tc = new int[frames.Length];
        var video = new short[frames.Length];
        var audio = new short[frames.Length];
        var states = new TcState[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            tc[i] = frames[i].Tc;
            video[i] = frames[i].Errors;
            audio[i] = frames[i].AudioErrors;
            states[i] = frames[i].TcState;
        }
        return new Lane(label, tc, video, audio, states, a.VideoBlocksPerFrame, a.AudioBlocksPerFrame, isResult);
    }

    List<Kind> Kinds
    {
        get
        {
            var kinds = new List<Kind>();
            if (showVideo) kinds.Add(Kind.Video);
            if (showAudio) kinds.Add(Kind.Audio);
            if (showTc) kinds.Add(Kind.TimeCode);
            return kinds;
        }
    }

    int LaneHeight(int kinds) => kinds * RowHeight + (kinds - 1) * RowGap;
    int GraphWidth => Math.Max(10, ClientSize.Width - LabelWidth - 12);

    /// <summary>Severity of a frame for a kind: -1 missing, 0 ok, 1 minor, 2 medium, 3 severe (time code: 1 corrected, 3 unknown).</summary>
    static int Severity(Lane lane, int i, Kind kind)
    {
        switch (kind)
        {
            case Kind.Video:
            {
                double r = (double)lane.Video[i] / lane.VideoBlocks;
                return r <= 0 ? 0 : r < 0.01 ? 1 : r < 0.05 ? 2 : 3;
            }
            case Kind.Audio:
            {
                int n = lane.Audio[i];
                double r = (double)n / lane.AudioBlocks;
                return n == 0 ? 0 : r < 0.05 ? 1 : r < 0.25 ? 2 : 3;
            }
            default:
                return lane.TcStates[i] switch { TcState.Read => 0, TcState.Derived => 1, _ => 3 };
        }
    }

    static Color ColorFor(Kind kind, int severity) => severity switch
    {
        < 0 => MissingColor,
        0 => OkColor,
        1 => kind == Kind.TimeCode ? DerivedColor : MinorColor,
        2 => MediumColor,
        _ => kind == Kind.TimeCode ? NoTcColor : SevereColor,
    };

    static string KindName(Kind kind) => kind switch { Kind.Video => "映像", Kind.Audio => "音声", _ => "TC" };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var font = new Font(Font.FontFamily, 9f);
        using var small = new Font(Font.FontFamily, 7.5f);
        using var bold = new Font(font, FontStyle.Bold);
        var kinds = Kinds;
        if (message != null || lanes.Count == 0 || kinds.Count == 0)
        {
            TextRenderer.DrawText(g, message ?? (kinds.Count == 0 ? "表示する項目を選んでください。" : ""), font, new Point(8, 8), SystemColors.GrayText);
            return;
        }

        int width = GraphWidth;
        int x0 = LabelWidth;
        double span = Math.Max(1, tcEnd - tcStart + 1);

        // Time code axis
        using (var pen = new Pen(SystemColors.ControlDark))
        {
            for (int t = 0; t <= 4; t++)
            {
                int x = x0 + (int)(width * t / 4.0);
                g.DrawLine(pen, x, TopMargin - 6, x, TopMargin - 2);
                var tc = tcStart + (int)(span * t / 4.0);
                var text = DvParser.FramesToTimeCode(Math.Min(tc, tcEnd), tcBase, dropFrame);
                var size = TextRenderer.MeasureText(text, font);
                int tx = Math.Clamp(x - size.Width / 2, x0, x0 + width - size.Width);
                TextRenderer.DrawText(g, text, font, new Point(tx, 4), SystemColors.ControlText);
            }
        }

        int laneHeight = LaneHeight(kinds.Count);
        var severity = new int[width];
        for (int li = 0; li < lanes.Count; li++)
        {
            var lane = lanes[li];
            int y = TopMargin + li * (laneHeight + LaneGap);
            if (lane.IsResult)
            {
                using var sep = new Pen(SystemColors.ControlDark) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                g.DrawLine(sep, 4, y - LaneGap / 2, x0 + width, y - LaneGap / 2);
            }

            var labelRect = new Rectangle(4, y, LabelWidth - 44, laneHeight);
            TextRenderer.DrawText(g, lane.Label, lane.IsResult ? bold : font, labelRect, SystemColors.ControlText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            for (int ki = 0; ki < kinds.Count; ki++)
            {
                var kind = kinds[ki];
                int ry = y + ki * (RowHeight + RowGap);
                TextRenderer.DrawText(g, KindName(kind), small, new Rectangle(LabelWidth - 38, ry, 34, RowHeight), SystemColors.GrayText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);

                Array.Fill(severity, -1);
                for (int i = 0; i < lane.Tc.Length; i++)
                {
                    if (lane.Tc[i] < 0)
                        continue;
                    // A frame covers [tc, tc + 1) on the axis, which may span several pixels when zoomed in
                    int xFrom = Math.Max(0, (int)((lane.Tc[i] - tcStart) / span * width));
                    int xTo = Math.Min(width - 1, Math.Max(xFrom, (int)Math.Ceiling((lane.Tc[i] + 1 - tcStart) / span * width) - 1));
                    int s = Severity(lane, i, kind);
                    for (int x = xFrom; x <= xTo; x++)
                        if (s > severity[x])
                            severity[x] = s;
                }

                using (var missing = new SolidBrush(MissingColor))
                    g.FillRectangle(missing, x0, ry, width, RowHeight);
                for (int x = 0; x < width; x++)
                {
                    if (severity[x] < 0)
                        continue;
                    using var pen = new Pen(ColorFor(kind, severity[x]));
                    g.DrawLine(pen, x0 + x, ry, x0 + x, ry + RowHeight - 1);
                }
                g.DrawRectangle(SystemPens.ControlDark, x0, ry, width - 1, RowHeight - 1);
            }
        }

        // Legend
        int ly = TopMargin + lanes.Count * (laneHeight + LaneGap) + 2;
        var items = new List<(Color, string)> { (OkColor, "正常"), (MinorColor, "軽微"), (MediumColor, "中"), (SevereColor, "重度"), (MissingColor, "フレームなし") };
        if (showTc)
            items.AddRange([(DerivedColor, "TC補正"), (NoTcColor, "TC不明")]);
        DrawLegend(g, font, x0, ly, items);
        TextRenderer.DrawText(g, "映像: 壊れたブロックの割合 軽微<1% 中<5%　音声: 軽微<5% 中<25%　TC補正: 読めない・誤ったタイムコードを前後から補ったもの",
            small, new Point(x0, ly + 20), SystemColors.GrayText);
    }

    static void DrawLegend(Graphics g, Font font, int x, int y, List<(Color color, string text)> items)
    {
        foreach (var (color, text) in items)
        {
            using var b = new SolidBrush(color);
            g.FillRectangle(b, x, y + 3, 14, 12);
            g.DrawRectangle(SystemPens.ControlDark, x, y + 3, 14, 12);
            TextRenderer.DrawText(g, text, font, new Point(x + 18, y + 1), SystemColors.ControlText);
            x += 18 + TextRenderer.MeasureText(text, font).Width + 12;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (lanes.Count == 0 || message != null || e.X < LabelWidth || e.X >= LabelWidth + GraphWidth)
            return;
        double span = Math.Max(1, tcEnd - tcStart + 1);
        int tcFrom = tcStart + (int)((e.X - LabelWidth) / (double)GraphWidth * span);
        int tcTo = Math.Max(tcFrom, tcStart + (int)((e.X - LabelWidth + 1) / (double)GraphWidth * span) - 1);
        var parts = new List<string> { "TC " + DvParser.FramesToTimeCode(tcFrom, tcBase, dropFrame) };
        foreach (var lane in lanes)
        {
            int video = -1, audio = -1, tcSeverity = -1;
            for (int i = 0; i < lane.Tc.Length; i++)
            {
                if (lane.Tc[i] < tcFrom || lane.Tc[i] > tcTo)
                    continue;
                video = Math.Max(video, lane.Video[i]);
                audio = Math.Max(audio, lane.Audio[i]);
                tcSeverity = Math.Max(tcSeverity, Severity(lane, i, Kind.TimeCode));
            }
            parts.Add(video >= 0
                ? $"{lane.Label}: 映像 {video} / 音声 {audio} ブロック, TC {(tcSeverity == 0 ? "読取" : tcSeverity == 1 ? "補正" : "不明")}"
                : $"{lane.Label}: フレームなし");
        }
        HoverInfo?.Invoke(string.Join("  |  ", parts));
    }
}
