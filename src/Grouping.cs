namespace DVTapeMerger;

public static class Grouping
{
    /// <summary>A forward time code jump larger than this (in frames) inside one file starts a new run.</summary>
    const int MaxForwardJump = 30 * 30;
    /// <summary>Tolerance when comparing recording date/time ranges of runs.</summary>
    static readonly long RdtTolerance = TimeSpan.FromSeconds(5).Ticks;

    public static void BuildRuns(SourceFile file)
    {
        file.Runs.Clear();
        var frames = file.Analysis!.Frames;
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
