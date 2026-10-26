namespace DVTapeMerger;

public sealed class SourceFile
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    /// <summary>AVI / QuickTime captures: the DV stream is taken out of the container with ffmpeg.</summary>
    public bool InContainer => Extension is ".avi" or ".mov";
    public string Kind => InContainer ? Extension[1..].ToUpperInvariant() : "DV";
    string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

    public static bool IsSupported(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".dv" or ".dif" or ".avi" or ".mov";

    /// <summary>Raw DIF stream used for analysis and merging (the file itself, or a stream extracted from AVI / MOV).</summary>
    public string? RawPath { get; set; }
    public DvAnalysis? Analysis { get; set; }
    public string? Error { get; set; }
    public List<Run> Runs { get; } = new();
}

/// <summary>A span of frames inside one source file with continuously increasing time code.</summary>
public sealed class Run
{
    public required SourceFile Source { get; init; }
    public required int StartFrame { get; init; }
    public required int EndFrame { get; init; } // exclusive
    public int TcStart { get; init; } = -1;
    public int TcEnd { get; init; } = -1;
    public long RdtMin { get; init; }
    public long RdtMax { get; init; }

    public int FrameCount => EndFrame - StartFrame;
    public bool CoversWholeFile => StartFrame == 0 && EndFrame == Source.Analysis!.Frames.Length;
    public bool HasTc => TcStart >= 0;
    public bool HasRdt => RdtMin != 0;

    public long ErrorBlocks
    {
        get
        {
            long sum = 0;
            var frames = Source.Analysis!.Frames;
            for (int i = StartFrame; i < EndFrame; i++)
                sum += frames[i].Errors;
            return sum;
        }
    }

    public int ErrorFrames => CountFrames(f => f.Errors > 0);
    public int AudioErrorFrames => CountFrames(f => f.AudioErrors > 0);

    int CountFrames(Func<FrameInfo, bool> predicate)
    {
        int n = 0;
        var frames = Source.Analysis!.Frames;
        for (int i = StartFrame; i < EndFrame; i++)
            if (predicate(frames[i]))
                n++;
        return n;
    }

    public double ErrorRatio => FrameCount == 0 ? 0 : (double)ErrorBlocks / ((long)FrameCount * Source.Analysis!.VideoBlocksPerFrame);

    public string Label => CoversWholeFile ? Source.FileName : $"{Source.FileName} [{StartFrame}-{EndFrame - 1}]";
}

/// <summary>A set of runs (from any capture) covering the same part of the tape.</summary>
public sealed class MergeGroup
{
    public int Index { get; set; }
    public List<Run> Runs { get; } = new();

    public int TcStart => Runs.Where(r => r.HasTc).Select(r => r.TcStart).DefaultIfEmpty(-1).Min();
    public int TcEnd => Runs.Where(r => r.HasTc).Select(r => r.TcEnd).DefaultIfEmpty(-1).Max();
    public long RdtMin => Runs.Where(r => r.HasRdt).Select(r => r.RdtMin).DefaultIfEmpty(0).Min();

    // Filled after merging
    public string? OutputPath { get; set; }
    public DvAnalysis? Result { get; set; }
    public string? MergeLogPath { get; set; }
}
