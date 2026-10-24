namespace DVTapeMerger;

/// <summary>
/// Draws one lane per input (and one for the merged result) of a group, aligned on time code,
/// colored by the ratio of erroneous video blocks.
/// </summary>
public sealed class ErrorGraphPanel : Control
{
    sealed record Lane(string Label, int[] Tc, short[] Errors, int BlocksPerFrame, bool IsResult);

    readonly List<Lane> lanes = new();
    int tcStart, tcEnd, tcBase = 30;
    bool dropFrame;
    string? message = "グループを選択するとエラーの分布を表示します。";

    const int LabelWidth = 280;
    const int LaneHeight = 22;
    const int LaneGap = 6;
    const int TopMargin = 28;

    static readonly Color MissingColor = Color.FromArgb(220, 220, 220);
    static readonly Color OkColor = Color.FromArgb(80, 170, 90);

    public event Action<string>? HoverInfo;

    public ErrorGraphPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = SystemColors.Window;
    }

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
            lanes.Add(MakeLane(run.Label, a.Frames.AsSpan(run.StartFrame, run.FrameCount), a.VideoBlocksPerFrame, false));
        }
        if (group.Result != null)
            lanes.Add(MakeLane("結合後", group.Result.Frames, group.Result.VideoBlocksPerFrame, true));
        Invalidate();
    }

    static Lane MakeLane(string label, ReadOnlySpan<FrameInfo> frames, int blocks, bool isResult)
    {
        var tc = new int[frames.Length];
        var err = new short[frames.Length];
        int last = -1;
        for (int i = 0; i < frames.Length; i++)
        {
            int t = frames[i].Tc >= 0 ? frames[i].Tc : (last >= 0 ? last + 1 : -1);
            tc[i] = t;
            last = t;
            err[i] = frames[i].Errors;
        }
        return new Lane(label, tc, err, blocks, isResult);
    }

    int GraphWidth => Math.Max(10, ClientSize.Width - LabelWidth - 12);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        using var font = new Font(Font.FontFamily, 9f);
        using var bold = new Font(font, FontStyle.Bold);
        if (message != null || lanes.Count == 0)
        {
            TextRenderer.DrawText(g, message ?? "", font, new Point(8, 8), SystemColors.GrayText);
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

        for (int li = 0; li < lanes.Count; li++)
        {
            var lane = lanes[li];
            int y = TopMargin + li * (LaneHeight + LaneGap);
            if (lane.IsResult)
            {
                using var sep = new Pen(SystemColors.ControlDark) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
                g.DrawLine(sep, 4, y - LaneGap / 2, x0 + width, y - LaneGap / 2);
            }

            var labelRect = new Rectangle(4, y, LabelWidth - 10, LaneHeight);
            TextRenderer.DrawText(g, lane.Label, lane.IsResult ? bold : font, labelRect, SystemColors.ControlText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            var buckets = new double[width];
            Array.Fill(buckets, -1);
            for (int i = 0; i < lane.Tc.Length; i++)
            {
                if (lane.Tc[i] < 0)
                    continue;
                // A frame covers [tc, tc + 1) on the axis, which may span several pixels when zoomed in
                int xFrom = Math.Max(0, (int)((lane.Tc[i] - tcStart) / span * width));
                int xTo = Math.Min(width - 1, Math.Max(xFrom, (int)Math.Ceiling((lane.Tc[i] + 1 - tcStart) / span * width) - 1));
                double ratio = (double)lane.Errors[i] / lane.BlocksPerFrame;
                for (int x = xFrom; x <= xTo; x++)
                    if (ratio > buckets[x])
                        buckets[x] = ratio;
            }

            using (var missing = new SolidBrush(MissingColor))
                g.FillRectangle(missing, x0, y, width, LaneHeight);
            for (int x = 0; x < width; x++)
            {
                if (buckets[x] < 0)
                    continue;
                using var pen = new Pen(ColorFor(buckets[x]));
                g.DrawLine(pen, x0 + x, y, x0 + x, y + LaneHeight - 1);
            }
            g.DrawRectangle(SystemPens.ControlDark, x0, y, width - 1, LaneHeight - 1);
        }

        // Legend
        int ly = TopMargin + lanes.Count * (LaneHeight + LaneGap) + 6;
        int lx = x0;
        foreach (var (color, text) in new[] { (OkColor, "エラーなし"), (ColorFor(0.002), "軽微 (<1%)"), (ColorFor(0.03), "中 (1-5%)"), (ColorFor(0.2), "重度 (>5%)"), (MissingColor, "フレームなし") })
        {
            using var b = new SolidBrush(color);
            g.FillRectangle(b, lx, ly + 3, 14, 12);
            g.DrawRectangle(SystemPens.ControlDark, lx, ly + 3, 14, 12);
            TextRenderer.DrawText(g, text, font, new Point(lx + 18, ly + 1), SystemColors.ControlText);
            lx += 18 + TextRenderer.MeasureText(text, font).Width + 14;
        }
    }

    static Color ColorFor(double ratio)
    {
        if (ratio <= 0)
            return OkColor;
        if (ratio < 0.01)
            return Color.FromArgb(230, 200, 40);
        if (ratio < 0.05)
            return Color.FromArgb(240, 130, 30);
        return Color.FromArgb(210, 40, 40);
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
            int worst = -1;
            for (int i = 0; i < lane.Tc.Length; i++)
                if (lane.Tc[i] >= tcFrom && lane.Tc[i] <= tcTo && lane.Errors[i] > worst)
                    worst = lane.Errors[i];
            parts.Add($"{lane.Label}: {(worst < 0 ? "なし" : worst + " ブロック")}");
        }
        HoverInfo?.Invoke(string.Join("  |  ", parts));
    }
}
