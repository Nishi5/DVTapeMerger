namespace DVTapeMerger;

/// <summary>Per-frame information read directly from the DIF stream.</summary>
public struct FrameInfo
{
    /// <summary>Time code as an absolute frame number, or -1 when absent.</summary>
    public int Tc;
    /// <summary>Recording date/time (DateTime ticks), or 0 when absent.</summary>
    public long Rdt;
    /// <summary>Number of video DIF blocks flagged as erroneous (STA != 0 or broken block ID).</summary>
    public short Errors;
    public bool DropFrame;
}

public sealed class DvAnalysis
{
    public required string Path { get; init; }
    public required int FrameSize { get; init; }
    public required bool IsPal { get; init; }
    public required FrameInfo[] Frames { get; init; }

    public int VideoBlocksPerFrame => IsPal ? 12 * 135 : 10 * 135;
    public double FrameRate => IsPal ? 25.0 : 30000.0 / 1001.0;
    public int TcBase => IsPal ? 25 : 30;

    public int ErrorFrameCount => Frames.Count(f => f.Errors > 0);
    public long ErrorBlockCount => Frames.Sum(f => (long)f.Errors);
    public double ErrorBlockRatio => Frames.Length == 0 ? 0 : (double)ErrorBlockCount / ((long)Frames.Length * VideoBlocksPerFrame);
}

/// <summary>
/// Minimal reader for raw DV25 (IEC 61834 / SMPTE 314M DV25) DIF streams.
/// Only the subcode/VAUX packs needed for alignment and the video block status are decoded.
/// </summary>
public static class DvParser
{
    const int BlockSize = 80;
    const int BlocksPerSequence = 150;
    const int SequenceSize = BlockSize * BlocksPerSequence;

    public static (int frameSize, bool isPal)? DetectFormat(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[BlockSize];
        if (fs.Read(buf, 0, BlockSize) != BlockSize)
            return null;
        // Header block: SCT=0, Dseq=0, DBN=0
        if ((buf[0] & 0xE0) != 0 || (buf[1] & 0xF0) != 0 || buf[2] != 0)
            return null;
        bool isPal = (buf[3] & 0x80) != 0;
        return (SequenceSize * (isPal ? 12 : 10), isPal);
    }

    public static DvAnalysis Analyze(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var format = DetectFormat(path) ?? throw new InvalidDataException("DVストリームとして認識できません（先頭がDIFヘッダーではありません）: " + System.IO.Path.GetFileName(path));
        int frameSize = format.frameSize;
        int sequences = frameSize / SequenceSize;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        long count = fs.Length / frameSize;
        var frames = new FrameInfo[count];
        var buf = new byte[frameSize];
        var tcVotes = new List<(int tc, bool df)>(32);
        var dateVotes = new List<(int y, int m, int d)>(32);
        var timeVotes = new List<(int h, int m, int s)>(32);

        for (long i = 0; i < count; i++)
        {
            if ((i & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)i / count);
            }
            fs.ReadExactly(buf, 0, frameSize);
            tcVotes.Clear();
            dateVotes.Clear();
            timeVotes.Clear();
            int errors = 0;

            for (int seq = 0; seq < sequences; seq++)
            {
                int seqBase = seq * SequenceSize;

                // Subcode blocks (2 per sequence): 6 sync blocks of [ID0 ID1 FF PC0..PC4]
                for (int sc = 1; sc <= 2; sc++)
                {
                    int blk = seqBase + sc * BlockSize;
                    if ((buf[blk] & 0xE0) != 0x20)
                        continue;
                    for (int s = 0; s < 6; s++)
                        ReadPack(buf, blk + 3 + s * 8 + 3, format.isPal, tcVotes, dateVotes, timeVotes);
                }

                // VAUX blocks (3 per sequence): 15 packs of 5 bytes
                for (int va = 3; va <= 5; va++)
                {
                    int blk = seqBase + va * BlockSize;
                    if ((buf[blk] & 0xE0) != 0x40)
                        continue;
                    for (int p = 0; p < 15; p++)
                        ReadPack(buf, blk + 3 + p * 5, format.isPal, null, dateVotes, timeVotes);
                }

                // Audio and video blocks: 9 x (1 audio + 15 video)
                for (int b = 6; b < BlocksPerSequence; b++)
                {
                    if ((b - 6) % 16 == 0)
                        continue;
                    int blk = seqBase + b * BlockSize;
                    if ((buf[blk] & 0xE0) != 0x80 || (buf[blk + 3] >> 4) != 0)
                        errors++;
                }
            }

            ref var f = ref frames[i];
            f.Errors = (short)errors;
            f.Tc = -1;
            if (tcVotes.Count > 0)
            {
                var best = Majority(tcVotes);
                f.Tc = best.tc;
                f.DropFrame = best.df;
            }
            if (dateVotes.Count > 0 && timeVotes.Count > 0)
            {
                var d = Majority(dateVotes);
                var t = Majority(timeVotes);
                try { f.Rdt = new DateTime(d.y, d.m, d.d, t.h, t.m, t.s).Ticks; }
                catch (ArgumentOutOfRangeException) { f.Rdt = 0; }
            }
        }
        progress?.Report(1);

        return new DvAnalysis { Path = path, FrameSize = frameSize, IsPal = format.isPal, Frames = frames };
    }

    static T Majority<T>(List<T> votes) where T : notnull
    {
        if (votes.Count == 1)
            return votes[0];
        T best = votes[0];
        int bestCount = 0;
        for (int i = 0; i < votes.Count; i++)
        {
            int c = 0;
            for (int j = 0; j < votes.Count; j++)
                if (EqualityComparer<T>.Default.Equals(votes[i], votes[j]))
                    c++;
            if (c > bestCount)
            {
                bestCount = c;
                best = votes[i];
            }
        }
        return best;
    }

    static bool Bcd(int value, int tensMask, out int result)
    {
        int units = value & 0x0F;
        int tens = (value >> 4) & tensMask;
        result = tens * 10 + units;
        return units <= 9;
    }

    static void ReadPack(byte[] b, int o, bool isPal, List<(int, bool)>? tc, List<(int, int, int)> date, List<(int, int, int)> time)
    {
        switch (b[o])
        {
            case 0x13: // Time code
                if (tc == null || (b[o + 1] & b[o + 2] & b[o + 3] & b[o + 4]) == 0xFF)
                    return;
                if (Bcd(b[o + 1], 0x3, out int ff) && Bcd(b[o + 2], 0x7, out int ss) && Bcd(b[o + 3], 0x7, out int mm) && Bcd(b[o + 4], 0x3, out int hh)
                    && ff < (isPal ? 25 : 30) && ss < 60 && mm < 60 && hh < 24)
                {
                    bool df = !isPal && (b[o + 1] & 0x40) != 0;
                    tc.Add((TimeCodeToFrames(hh, mm, ss, ff, isPal ? 25 : 30, df), df));
                }
                return;
            case 0x62: // Recording date
                if (Bcd(b[o + 2], 0x3, out int day) && Bcd(b[o + 3], 0x1, out int month) && Bcd(b[o + 4], 0xF, out int year)
                    && day >= 1 && day <= 31 && month >= 1 && month <= 12)
                    date.Add((year < 75 ? 2000 + year : 1900 + year, month, day));
                return;
            case 0x63: // Recording time
                if (Bcd(b[o + 2], 0x7, out int s) && Bcd(b[o + 3], 0x7, out int m) && Bcd(b[o + 4], 0x3, out int h)
                    && s < 60 && m < 60 && h < 24)
                    time.Add((h, m, s));
                return;
        }
    }

    public static int TimeCodeToFrames(int hh, int mm, int ss, int ff, int fps, bool dropFrame)
    {
        int frames = ((hh * 60 + mm) * 60 + ss) * fps + ff;
        if (dropFrame)
        {
            int totalMinutes = hh * 60 + mm;
            frames -= 2 * (totalMinutes - totalMinutes / 10);
        }
        return frames;
    }

    public static string FramesToTimeCode(int frames, int fps, bool dropFrame)
    {
        if (frames < 0)
            return "--:--:--:--";
        if (dropFrame)
        {
            // Inverse of the drop-frame numbering (2 frame numbers skipped every minute except each 10th)
            const int framesPer10Min = 17982;
            const int framesPerMin = 1798;
            int d = frames / framesPer10Min;
            int m = frames % framesPer10Min;
            frames += 18 * d + (m > 2 ? 2 * ((m - 2) / framesPerMin) : 0);
        }
        int f = frames % fps;
        int totalSeconds = frames / fps;
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}{(dropFrame ? ';' : ':')}{f:00}";
    }
}
