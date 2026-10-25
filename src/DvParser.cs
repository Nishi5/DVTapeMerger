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
    /// <summary>Audio format of the frame: 0 unknown, else 1 + sampling frequency code * 2 + (12 bit ? 1 : 0).</summary>
    public byte AudioCode;
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
    public int AudioErrorFrameCount => Frames.Count(f => f.AudioErrors > 0);
    public int AudioBlocksPerFrame => IsPal ? 12 * 9 : 10 * 9;
    public double AudioErrorBlockRatio => Frames.Length == 0 ? 0 : (double)AudioErrorBlockCount / ((long)Frames.Length * AudioBlocksPerFrame);
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

    // Audio sample positions (IEC 61834-2 shuffling): sample "of" of an audio block = table[seq][block] + n * stride,
    // where "of" counts the samples of a channel pair interleaved (even: first channel, odd: second channel).
    static readonly byte[,] Shuffle525 =
    {
        { 0, 30, 60, 20, 50, 80, 10, 40, 70 }, { 6, 36, 66, 26, 56, 86, 16, 46, 76 }, { 12, 42, 72, 2, 32, 62, 22, 52, 82 },
        { 18, 48, 78, 8, 38, 68, 28, 58, 88 }, { 24, 54, 84, 14, 44, 74, 4, 34, 64 },
        { 1, 31, 61, 21, 51, 81, 11, 41, 71 }, { 7, 37, 67, 27, 57, 87, 17, 47, 77 }, { 13, 43, 73, 3, 33, 63, 23, 53, 83 },
        { 19, 49, 79, 9, 39, 69, 29, 59, 89 }, { 25, 55, 85, 15, 45, 75, 5, 35, 65 },
    };
    static readonly byte[,] Shuffle625 =
    {
        { 0, 36, 72, 26, 62, 98, 16, 52, 88 }, { 6, 42, 78, 32, 68, 104, 22, 58, 94 }, { 12, 48, 84, 2, 38, 74, 28, 64, 100 },
        { 18, 54, 90, 8, 44, 80, 34, 70, 106 }, { 24, 60, 96, 14, 50, 86, 4, 40, 76 }, { 30, 66, 102, 20, 56, 92, 10, 46, 82 },
        { 1, 37, 73, 27, 63, 99, 17, 53, 89 }, { 7, 43, 79, 33, 69, 105, 23, 59, 95 }, { 13, 49, 85, 3, 39, 75, 29, 65, 101 },
        { 19, 55, 91, 9, 45, 81, 35, 71, 107 }, { 25, 61, 97, 15, 51, 87, 5, 41, 77 }, { 31, 67, 103, 21, 57, 93, 11, 47, 83 },
    };

    /// <summary>Audio format of one frame, from its AAUX source packs.</summary>
    public sealed class AudioFormat
    {
        public bool IsPal;
        public bool Is12Bit;
        /// <summary>Sampling frequency code: 0 48 kHz, 1 44.1 kHz, 2 32 kHz.</summary>
        public int Smp;
        /// <summary>Samples per channel in this frame.</summary>
        public int Samples;
        /// <summary>Whether each channel pair is recorded (12 bit: first/second half of the DIF sequences).</summary>
        public bool[] PairUsed = [true, true];

        public static AudioFormat? Read(byte[] f, bool isPal)
        {
            int sequences = isPal ? 12 : 10;
            int half = sequences / 2;
            AudioFormat? fmt = null;
            var found = new bool[2];
            for (int seq = 0; seq < sequences; seq++)
                for (int a = 0; a < 9; a++)
                {
                    int o = seq * SequenceSize + (6 + a * 16) * BlockSize;
                    if ((f[o] & 0xE0) != 0x60 || f[o + 3] != 0x50)
                        continue;
                    int pairIndex = seq / half;
                    if (found[pairIndex])
                        continue;
                    int qu = f[o + 7] & 0x07, smp = (f[o + 7] >> 3) & 0x07, afSize = f[o + 4] & 0x3F;
                    if (qu > 1 || smp > 2)
                        continue;
                    found[pairIndex] = true;
                    if (fmt == null)
                    {
                        int min = isPal ? smp switch { 0 => 1896, 1 => 1742, _ => 1264 } : smp switch { 0 => 1580, 1 => 1452, _ => 1053 };
                        fmt = new AudioFormat { IsPal = isPal, Is12Bit = qu == 1, Smp = smp, Samples = min + afSize };
                    }
                    fmt.PairUsed[pairIndex] = (f[o + 5] & 0x0F) != 0x0F; // AUDIO MODE 0xF: no information (not recorded)
                }
            if (fmt != null && !fmt.Is12Bit)
                fmt.PairUsed[1] = fmt.PairUsed[0]; // 16 bit: one channel pair spread over all sequences
            return fmt;
        }
    }

    /// <summary>
    /// True when the audio block is damaged: wrong block ID, or an error code sample (0x800 in 12 bit, 0x8000 in 16 bit)
    /// at a position actually holding a sample of a recorded channel. Unused positions and unrecorded channels are ignored.
    /// </summary>
    public static bool AudioBlockBad(byte[] f, int seq, int audioBlock, AudioFormat? fmt)
    {
        int o = seq * SequenceSize + (6 + audioBlock * 16) * BlockSize;
        if (fmt == null)
            return (f[o] & 0xE0) != 0x60;
        int sequences = fmt.IsPal ? 12 : 10;
        int half = sequences / 2;
        if (!fmt.PairUsed[fmt.Is12Bit ? seq / half : 0])
            return false;
        if ((f[o] & 0xE0) != 0x60)
            return true;
        var table = fmt.IsPal ? Shuffle625 : Shuffle525;
        int stride = sequences * 9;
        int limit = fmt.Samples * 2;
        if (fmt.Is12Bit)
        {
            int start = table[seq % half, audioBlock];
            for (int n = 0; n < 24; n++)
            {
                if (start + n * stride >= limit)
                    continue;
                int i = o + 8 + n * 3;
                if ((f[i] == 0x80 && (f[i + 2] >> 4) == 0) || (f[i + 1] == 0x80 && (f[i + 2] & 0x0F) == 0))
                    return true;
            }
        }
        else
        {
            int start = table[seq, audioBlock];
            for (int n = 0; n < 36; n++)
            {
                if (start + n * stride >= limit)
                    continue;
                int i = o + 8 + n * 2;
                if (f[i] == 0x80 && f[i + 1] == 0x00)
                    return true;
            }
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
            var audio = AudioFormat.Read(buf, isPal);

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
                        if (AudioBlockBad(buf, seq, (b - 6) / 16, audio))
                            audioErrors++;
                    }
                    else if (!VideoBlockOk(buf, blk))
                    {
                        errors++;
                    }
                }
            }

            var f = new FrameInfo { Errors = (short)errors, AudioErrors = (short)audioErrors, Tc = -1, RawTc = -1 };
            if (audio != null)
                f.AudioCode = (byte)(1 + audio.Smp * 2 + (audio.Is12Bit ? 1 : 0));
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
