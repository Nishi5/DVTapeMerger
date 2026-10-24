namespace DVTapeMerger;

public static class Grouping
{
    /// <summary>A forward time code jump larger than this (in frames) inside one file starts a new run.</summary>
    const int MaxForwardJump = 30 * 30;
    /// <summary>Tolerance when comparing recording date/time ranges of runs.</summary>
    static readonly long RdtTolerance = TimeSpan.FromSeconds(5).Ticks;

    /// <summary>Half width (in frames) of the window used to vote on the time code offset.</summary>
    const int OffsetWindow = 15;

    /// <summary>
    /// Validates time codes against the frame position and fills missing or damaged ones.
    /// Within a continuous recording, (time code - frame position) is constant; it only changes where
    /// the capture dropped or repeated frames. The offset of each frame is decided by a vote over the
    /// neighboring frames, weighted by how many subcode packs agreed on each read time code, so that
    /// isolated wrong readings (e.g. a subcode still carrying the previous frame's time code) are ignored.
    /// </summary>
    public static void RepairTimeCodes(FrameInfo[] frames)
    {
        int n = frames.Length;
        var offset = new int?[n];
        var votes = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            votes.Clear();
            for (int j = Math.Max(0, i - OffsetWindow); j <= Math.Min(n - 1, i + OffsetWindow); j++)
            {
                if (frames[j].RawTc < 0)
                    continue;
                int d = frames[j].RawTc - j;
                votes[d] = votes.GetValueOrDefault(d) + Math.Max(1, (int)frames[j].TcVotes);
            }
            if (votes.Count > 0)
                offset[i] = votes.MaxBy(kv => kv.Value).Key;
        }

        // Place each change of offset where the readings around it agree best
        for (int c = 1; c < n; c++)
        {
            if (offset[c - 1] is not int d0 || offset[c] is not int d1 || d0 == d1)
                continue;
            int from = Math.Max(1, c - OffsetWindow), to = Math.Min(n - 1, c + OffsetWindow);
            int lo = Math.Max(0, c - 2 * OffsetWindow), hi = Math.Min(n, c + 2 * OffsetWindow);
            int bestB = c, bestScore = int.MinValue;
            for (int bnd = from; bnd <= to; bnd++)
            {
                int score = 0;
                for (int j = lo; j < hi; j++)
                {
                    if (frames[j].RawTc < 0)
                        continue;
                    int d = frames[j].RawTc - j;
                    int w = Math.Max(1, (int)frames[j].TcVotes);
                    if (j < bnd && d == d0 || j >= bnd && d == d1)
                        score += w;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestB = bnd;
                }
            }
            for (int j = from - 1; j <= to; j++)
                if (offset[j].HasValue && (offset[j] == d0 || offset[j] == d1))
                    offset[j] = j < bestB ? d0 : d1;
            c = Math.Max(c, to);
        }

        bool dropFrame = frames.FirstOrDefault(f => f.RawTc >= 0).DropFrame;
        for (int i = 0; i < n; i++)
        {
            ref var f = ref frames[i];
            if (offset[i] is int d && i + d >= 0)
            {
                f.Tc = i + d;
                f.TcState = f.RawTc == f.Tc ? TcState.Read : TcState.Derived;
                f.DropFrame = dropFrame;
            }
            else
            {
                f.Tc = -1;
                f.TcState = TcState.None;
            }
        }
    }

    public static void BuildRuns(SourceFile file)
    {
        file.Runs.Clear();
        var frames = file.Analysis!.Frames;
        RepairTimeCodes(frames);
        int start = 0;
        int lastTc = -1;
        for (int i = 0; i < frames.Length; i++)
        {
            int tc = frames[i].Tc;
            if (tc < 0)
                continue;
            if (lastTc >= 0 && (tc < lastTc || tc - lastTc > MaxForwardJump))
            {
                file.Runs.Add(MakeRun(file, start, i));
                start = i;
            }
            lastTc = tc;
        }
        if (frames.Length > start)
            file.Runs.Add(MakeRun(file, start, frames.Length));
    }

    static Run MakeRun(SourceFile file, int start, int end)
    {
        var frames = file.Analysis!.Frames;
        int tcStart = -1, tcEnd = -1;
        long rdtMin = 0, rdtMax = 0;
        for (int i = start; i < end; i++)
        {
            var f = frames[i];
            if (f.Tc >= 0)
            {
                if (tcStart < 0)
                    tcStart = f.Tc;
                tcEnd = f.Tc;
            }
            if (f.Rdt != 0)
            {
                if (rdtMin == 0 || f.Rdt < rdtMin)
                    rdtMin = f.Rdt;
                if (f.Rdt > rdtMax)
                    rdtMax = f.Rdt;
            }
        }
        return new Run { Source = file, StartFrame = start, EndFrame = end, TcStart = tcStart, TcEnd = tcEnd, RdtMin = rdtMin, RdtMax = rdtMax };
    }

    static bool Overlaps(Run a, Run b)
    {
        if (!a.HasTc || !b.HasTc)
            return false;
        if (a.Source.Analysis!.IsPal != b.Source.Analysis!.IsPal)
            return false;
        if (a.TcStart > b.TcEnd || b.TcStart > a.TcEnd)
            return false;
        if (a.HasRdt && b.HasRdt && (a.RdtMin - RdtTolerance > b.RdtMax || b.RdtMin - RdtTolerance > a.RdtMax))
            return false;
        return true;
    }

    /// <summary>Groups runs whose time code (and recording date) ranges overlap, then orders the groups chronologically.</summary>
    public static List<MergeGroup> BuildGroups(IEnumerable<SourceFile> files)
    {
        var runs = files.Where(f => f.Analysis != null).SelectMany(f => f.Runs).ToList();
        var parent = Enumerable.Range(0, runs.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);

        for (int i = 0; i < runs.Count; i++)
            for (int j = i + 1; j < runs.Count; j++)
                if (Overlaps(runs[i], runs[j]))
                    parent[Find(i)] = Find(j);

        var groups = runs
            .Select((r, i) => (r, root: Find(i)))
            .GroupBy(x => x.root)
            .Select(g =>
            {
                var group = new MergeGroup();
                // The input with the fewest errors goes first: dvrescue prefers earlier inputs when frames are equal.
                group.Runs.AddRange(g.Select(x => x.r).OrderBy(r => r.ErrorRatio));
                return group;
            })
            .OrderBy(g => g.RdtMin == 0 ? long.MaxValue : g.RdtMin)
            .ThenBy(g => g.TcStart)
            .ToList();

        for (int i = 0; i < groups.Count; i++)
            groups[i].Index = i + 1;
        return groups;
    }
}
