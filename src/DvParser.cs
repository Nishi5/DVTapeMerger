namespace DVTapeMerger;

public enum TcState : byte
{
    /// <summary>No usable time code.</summary>
    None,
    /// <summary>Time code read from the frame and consistent with its neighbors.</summary>
    Read,
    /// <summary>Time code missing or damaged in the frame, derived from the neighboring frames.</summary>
    Derived,
}

/// <summary>Per-frame information read directly from the DIF stream.</summary>
public struct FrameInfo
{
    /// <summary>Time code as an absolute frame number, or -1 when absent.</summary>
    public int Tc;
    /// <summary>Time code as read from the frame (before repair), or -1 when absent.</summary>
    public int RawTc;
    /// <summary>Recording date/time (DateTime ticks), or 0 when absent.</summary>
    public long Rdt;
    /// <summary>Number of video DIF blocks flagged as erroneous (STA != 0 or broken block ID).</summary>
    public short Errors;
    /// <summary>Number of audio DIF blocks containing error samples.</summary>
    public short AudioErrors;
    /// <summary>Number of subcode packs agreeing on the time code (subcode health).</summary>
    public byte TcVotes;
    public TcState TcState;
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
    public long AudioErrorBlockCount => Frames.Sum(f => (long)f.AudioErrors);
    public double ErrorBlockRatio => Frames.Length == 0 ? 0 : (double)ErrorBlockCount / ((long)Frames.Length * VideoBlocksPerFrame);
    public int TcMissingCount => Frames.Count(f => f.RawTc < 0);
}

/// <summary>
/// Minimal reader for raw DV25 (IEC 61834 / SMPTE 314M DV25) DIF streams.
/// Only the subcode/VAUX packs needed for alignment and the block status are decoded.
/// </summary>
public static class DvParser
{
    public const int BlockSize = 80;
    public const int BlocksPerSequence = 150;
    public const int SequenceSize = BlockSize * BlocksPerSequence;

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

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        long count = fs.Length / frameSize;
        var frames = new FrameInfo[count];
        var buf = new byte[frameSize];
        var parser = new FrameParser(format.isPal);

        for (long i = 0; i < count; i++)
        {
            if ((i & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)i / count);
            }
            fs.ReadExactly(buf, 0, frameSize);
            frames[i] = parser.Parse(buf);
        }
        progress?.Report(1);

        return new DvAnalysis { Path = path, FrameSize = frameSize, IsPal = format.isPal, Frames = frames };
    }

    // ----------------------------------------------------------------------------------------
    // Block layout helpers (per DIF sequence: H0, SC0, SC1, VA0-VA2, then 9 x (1 audio + 15 video))
    // ----------------------------------------------------------------------------------------

    public static bool IsAudioBlock(int blockInSequence) => blockInSequence >= 6 && (blockInSequence - 6) % 16 == 0;
    public static bool IsVideoBlock(int blockInSequence) => blockInSequence >= 6 && (blockInSequence - 6) % 16 != 0;

    public static bool VideoBlockOk(byte[] f, int o) => (f[o] & 0xE0) == 0x80 && (f[o + 3] >> 4) == 0;

    /// <summary>Audio quantization of the frame from the AAUX source pack (true = 12 bit), or null when not found.</summary>
    public static bool? Audio12Bit(byte[] f, int sequences)
    {
        for (int seq = 0; seq < sequences; seq++)
            for (int a = 0; a < 9; a++)
            {
                int o = seq * SequenceSize + (6 + a * 16) * BlockSize;
                if ((f[o] & 0xE0) == 0x60 && f[o + 3] == 0x50)
                {
                    int qu = f[o + 7] & 0x07;
                    if (qu == 0) return false;
                    if (qu == 1) return true;
                }
            }
        return null;
    }

    /// <summary>True when the audio block is damaged (wrong block ID or error code samples).</summary>
    public static bool AudioBlockBad(byte[] f, int o, bool? is12Bit)
    {
        if ((f[o] & 0xE0) != 0x60)
            return true;
        if (is12Bit == true)
        {
            for (int i = o + 8; i + 2 < o + BlockSize; i += 3)
                if ((f[i] == 0x80 && (f[i + 2] >> 4) == 0) || (f[i + 1] == 0x80 && (f[i + 2] & 0x0F) == 0))
                    return true;
        }
        else if (is12Bit == false)
        {
            for (int i = o + 8; i + 1 < o + BlockSize; i += 2)
                if (f[i] == 0x80 && f[i + 1] == 0x00)
                    return true;
        }
        return false;
    }

    /// <summary>Reads the per-frame information of one DIF frame.</summary>
    public sealed class FrameParser(bool isPal)
    {
        readonly int sequences = isPal ? 12 : 10;
        readonly List<(int tc, bool df)> tcVotes = new(32);
        readonly List<(int y, int m, int d)> dateVotes = new(32);
        readonly List<(int h, int m, int s)> timeVotes = new(32);

        public FrameInfo Parse(byte[] buf)
        {
            tcVotes.Clear();
            dateVotes.Clear();
            timeVotes.Clear();
            int errors = 0, audioErrors = 0;
            var audio12 = Audio12Bit(buf, sequences);

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
                        ReadPack(buf, blk + 3 + s * 8 + 3, isPal, tcVotes, dateVotes, timeVotes);
                }

                // VAUX blocks (3 per sequence): 15 packs of 5 bytes
                for (int va = 3; va <= 5; va++)
                {
                    int blk = seqBase + va * BlockSize;
                    if ((buf[blk] & 0xE0) != 0x40)
                        continue;
                    for (int p = 0; p < 15; p++)
                        ReadPack(buf, blk + 3 + p * 5, isPal, null, dateVotes, timeVotes);
                }

                for (int b = 6; b < BlocksPerSequence; b++)
                {
                    int blk = seqBase + b * BlockSize;
                    if (IsAudioBlock(b))
                    {
                        if (AudioBlockBad(buf, blk, audio12))
                            audioErrors++;
                    }
                    else if (!VideoBlockOk(buf, blk))
                    {
                        errors++;
                    }
                }
            }

            var f = new FrameInfo { Errors = (short)errors, AudioErrors = (short)audioErrors, Tc = -1, RawTc = -1 };
            if (tcVotes.Count > 0)
            {
                var best = Majority(tcVotes, out int votes);
                f.Tc = f.RawTc = best.tc;
                f.DropFrame = best.df;
                f.TcVotes = (byte)Math.Min(votes, 255);
                f.TcState = TcState.Read;
            }
            if (dateVotes.Count > 0 && timeVotes.Count > 0)
            {
                var d = Majority(dateVotes, out _);
                var t = Majority(timeVotes, out _);
                try { f.Rdt = new DateTime(d.y, d.m, d.d, t.h, t.m, t.s).Ticks; }
                catch (ArgumentOutOfRangeException) { f.Rdt = 0; }
            }
            return f;
        }
    }

    static T Majority<T>(List<T> votes, out int bestCount) where T : notnull
    {
        T best = votes[0];
        bestCount = 0;
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
