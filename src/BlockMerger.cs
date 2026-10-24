namespace DVTapeMerger;

/// <summary>
/// Built-in merge: for each time code of a group, the frames of all inputs carrying that time code
/// are combined block by block. Only frames with the same (read or derived) time code are mixed,
/// so a block can never come from a neighboring frame.
/// </summary>
public static class BlockMerger
{
    sealed class Candidate
    {
        public required Run Run { get; init; }
        public required int RunOrder { get; init; }
        public required int FrameIndex { get; init; }
        public FrameInfo Info => Run.Source.Analysis!.Frames[FrameIndex];
        public byte[]? Data;
        public DvParser.AudioFormat? Audio;
    }

    /// <summary>True when the intact video blocks of both frames have the same payload (or too few to tell).</summary>
    static bool SamePicture(byte[] a, byte[] b, int sequences)
    {
        int both = 0, equal = 0;
        for (int seq = 0; seq < sequences; seq++)
            for (int blk = 6; blk < DvParser.BlocksPerSequence; blk++)
            {
                if (!DvParser.IsVideoBlock(blk))
                    continue;
                int o = seq * DvParser.SequenceSize + blk * DvParser.BlockSize;
                if (!DvParser.VideoBlockOk(a, o) || !DvParser.VideoBlockOk(b, o))
                    continue;
                both++;
                if (a.AsSpan(o + 3, DvParser.BlockSize - 3).SequenceEqual(b.AsSpan(o + 3, DvParser.BlockSize - 3)))
                    equal++;
            }
        return both < 20 || equal * 10 >= both * 8;
    }

    /// <summary>Writes the time code into the time code packs of the subcode. Returns true when a pack was changed.</summary>
    static bool RewriteTimeCode(byte[] frame, int sequences, int tc, int fps, bool dropFrame)
    {
        var text = DvParser.FramesToTimeCode(tc, fps, dropFrame); // HH:MM:SS;FF
        int hh = int.Parse(text[..2]), mm = int.Parse(text[3..5]), ss = int.Parse(text[6..8]), ff = int.Parse(text[9..11]);
        static byte Bcd(int v) => (byte)((v / 10) << 4 | (v % 10));
        bool changed = false;
        for (int seq = 0; seq < sequences; seq++)
            for (int sc = 1; sc <= 2; sc++)
            {
                int blk = seq * DvParser.SequenceSize + sc * DvParser.BlockSize;
                if ((frame[blk] & 0xE0) != 0x20)
                    continue;
                for (int s = 0; s < 6; s++)
                {
                    int o = blk + 3 + s * 8 + 3;
                    if (frame[o] != 0x13)
                        continue;
                    // Keep the flag bits (color frame, drop frame, binary group flags) unless the pack is empty
                    bool empty = frame[o + 1] == 0xFF && frame[o + 2] == 0xFF;
                    byte p1 = (byte)((empty ? (dropFrame ? 0x40 : 0) : frame[o + 1] & 0xC0) | Bcd(ff));
                    byte p2 = (byte)((empty ? 0 : frame[o + 2] & 0x80) | Bcd(ss));
                    byte p3 = (byte)((empty ? 0 : frame[o + 3] & 0x80) | Bcd(mm));
                    byte p4 = (byte)((empty ? 0 : frame[o + 4] & 0xC0) | Bcd(hh));
                    if (frame[o + 1] != p1 || frame[o + 2] != p2 || frame[o + 3] != p3 || frame[o + 4] != p4)
                    {
                        frame[o + 1] = p1;
                        frame[o + 2] = p2;
                        frame[o + 3] = p3;
                        frame[o + 4] = p4;
                        changed = true;
                    }
                }
            }
        return changed;
    }

    static void SetArb(byte[] frame, int blockOffset, int arb) => frame[blockOffset] = (byte)((frame[blockOffset] & 0xF0) | arb);

    public sealed record Stats(int Frames, int FramesRepaired, int FramesMetaRepaired, int FramesDropped, int DerivedTcUsed, int CandidatesRejected);

    public static async Task<Stats> MergeAsync(MergeGroup group, string outputPath, Action<double>? progress, CancellationToken ct)
    {
        var a0 = group.Runs[0].Source.Analysis!;
        int frameSize = a0.FrameSize;
        int sequences = frameSize / DvParser.SequenceSize;
        int videoBlocks = a0.VideoBlocksPerFrame;

        // Time code -> candidate frames
        var byTc = new SortedDictionary<int, List<Candidate>>();
        for (int r = 0; r < group.Runs.Count; r++)
        {
            var run = group.Runs[r];
            var frames = run.Source.Analysis!.Frames;
            for (int i = run.StartFrame; i < run.EndFrame; i++)
            {
                int tc = frames[i].Tc;
                if (tc < 0)
                    continue;
                if (!byTc.TryGetValue(tc, out var list))
                    byTc[tc] = list = new();
                list.Add(new Candidate { Run = run, RunOrder = r, FrameIndex = i });
            }
        }

        var streams = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        int written = 0, repaired = 0, metaRepaired = 0, dropped = 0, derived = 0, rejected = 0, done = 0;
        try
        {
            foreach (var run in group.Runs)
                if (!streams.ContainsKey(run.Source.RawPath!))
                    streams[run.Source.RawPath!] = new FileStream(run.Source.RawPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var outFrame = new byte[frameSize];

            foreach (var (tc, all) in byTc)
            {
                if ((++done & 0xFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke((double)done / byTc.Count);
                }

                // Frames broken everywhere and without any readable time code are noise (e.g. after the end of a recording)
                if (all.All(c => c.Info.Errors >= videoBlocks * 9 / 10 && c.Info.TcState != TcState.Read))
                {
                    dropped++;
                    continue;
                }

                foreach (var c in all)
                {
                    var fs = streams[c.Run.Source.RawPath!];
                    c.Data = new byte[frameSize];
                    fs.Position = (long)c.FrameIndex * frameSize;
                    await fs.ReadExactlyAsync(c.Data, ct);
                    c.Audio = DvParser.AudioFormat.Read(c.Data, a0.IsPal);
                }

                // Captures of the same tape frame carry identical block payloads: candidates showing a
                // different picture (wrong time code) are put aside, keeping the group backed by the most
                // reliable time code readings.
                var clusters = new List<List<Candidate>>();
                foreach (var c in all)
                {
                    var cluster = clusters.FirstOrDefault(k => SamePicture(k[0].Data!, c.Data!, sequences));
                    if (cluster != null)
                        cluster.Add(c);
                    else
                        clusters.Add([c]);
                }
                var chosen = clusters
                    .OrderByDescending(k => k.Sum(c => c.Info.TcState == TcState.Read ? Math.Max(1, (int)c.Info.TcVotes) : 0))
                    .ThenByDescending(k => k.Count)
                    .ThenBy(k => k.Min(c => c.Info.Errors))
                    .First();
                rejected += all.Count - chosen.Count;

                // A repeated frame inside one input gives several candidates; keep the best of each input
                var candidates = chosen
                    .GroupBy(c => c.RunOrder)
                    .Select(g => g.OrderBy(c => c.Info.Errors + c.Info.AudioErrors).First())
                    .OrderBy(c => c.Info.Errors + c.Info.AudioErrors)
                    .ThenBy(c => c.Info.TcState == TcState.Read ? 0 : 1)
                    .ThenBy(c => c.RunOrder)
                    .ToList();

                var baseFrame = candidates[0];
                Buffer.BlockCopy(baseFrame.Data!, 0, outFrame, 0, frameSize);
                // The low nibble of the first ID byte is a per-frame sequence number set by the playback device;
                // it differs between captures, so blocks taken from other inputs get the one of the base frame.
                int arb = baseFrame.Data![0] & 0x0F;
                if (baseFrame.Info.TcState != TcState.Read)
                    derived++;
                bool blocksFixed = false, metaFixed = false;

                if (candidates.Count > 1)
                {
                    // Subcode and VAUX (time code, recording date...): from the input whose subcode is the most intact
                    // (frames whose subcode carries this very time code first, then any frame having time code packs,
                    // whose value is rewritten below)
                    var meta = candidates
                        .OrderByDescending(c => c.Info.TcState == TcState.Read ? 1000 + c.Info.TcVotes : c.Info.RawTc >= 0 ? c.Info.TcVotes : -1)
                        .ThenBy(c => c.Info.Rdt == 0 ? 1 : 0)
                        .First();
                    if (meta != baseFrame)
                    {
                        for (int seq = 0; seq < sequences; seq++)
                        {
                            int o = seq * DvParser.SequenceSize + DvParser.BlockSize;
                            Buffer.BlockCopy(meta.Data!, o, outFrame, o, 5 * DvParser.BlockSize);
                            for (int b = 0; b < 5; b++)
                                SetArb(outFrame, o + b * DvParser.BlockSize, arb);
                        }
                        metaFixed = true;
                    }

                    // Damaged audio/video blocks: replaced by the same block of another input when it is intact there
                    for (int seq = 0; seq < sequences; seq++)
                    {
                        for (int b = 6; b < DvParser.BlocksPerSequence; b++)
                        {
                            int o = seq * DvParser.SequenceSize + b * DvParser.BlockSize;
                            bool audio = DvParser.IsAudioBlock(b);
                            if (audio ? !DvParser.AudioBlockBad(outFrame, seq, (b - 6) / 16, baseFrame.Audio) : DvParser.VideoBlockOk(outFrame, o))
                                continue;
                            for (int k = 1; k < candidates.Count; k++)
                            {
                                var d = candidates[k].Data!;
                                if (audio ? !DvParser.AudioBlockBad(d, seq, (b - 6) / 16, candidates[k].Audio ?? baseFrame.Audio) : DvParser.VideoBlockOk(d, o))
                                {
                                    Buffer.BlockCopy(d, o, outFrame, o, DvParser.BlockSize);
                                    SetArb(outFrame, o, arb);
                                    blocksFixed = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                // When no input carries this time code intact in its subcode, the time code packs present in
                // the output are rewritten with the time code established from the neighboring frames.
                if (!candidates.Any(c => c.Info.TcState == TcState.Read) && RewriteTimeCode(outFrame, sequences, tc, a0.TcBase, baseFrame.Info.DropFrame))
                    metaFixed = true;

                if (blocksFixed)
                    repaired++;
                if (metaFixed)
                    metaRepaired++;
                await output.WriteAsync(outFrame, ct);
                written++;
                foreach (var c in all)
                    c.Data = null;
            }
        }
        finally
        {
            foreach (var s in streams.Values)
                s.Dispose();
        }
        progress?.Invoke(1);
        return new Stats(written, repaired, metaRepaired, dropped, derived, rejected);
    }
}
