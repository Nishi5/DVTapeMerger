using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DVTapeMerger;

public enum OutputFormat { Dv, Avi, Mov, Mkv }

public sealed record Chapter(long StartFrame, long EndFrame, DateTime? Rdt);

public sealed class MergeEngine
{
    public required string WorkDir { get; init; }
    public string? Dvrescue { get; init; }
    /// <summary>Use dvrescue for the merge instead of the built-in block merger.</summary>
    public bool UseDvrescue { get; init; }
    public string? Ffmpeg { get; init; }
    public required Action<string> Log { get; init; }
    public required Action<double, string> Progress { get; init; }

    string CacheDir => Path.Combine(WorkDir, "cache");

    // ----------------------------------------------------------------------------------------
    // Analysis
    // ----------------------------------------------------------------------------------------

    public async Task AnalyzeAsync(IList<SourceFile> files, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (file.Analysis != null)
                continue;
            file.Error = null;
            double baseProgress = (double)i / files.Count;
            double share = 1.0 / files.Count;
            try
            {
                if (file.InContainer)
                {
                    Progress(baseProgress, $"{file.Kind}からDVストリームを取り出し中 ({i + 1}/{files.Count}): {file.FileName}");
                    file.RawPath = await ExtractDvStreamAsync(file.Path, ct);
                }
                else
                {
                    file.RawPath = file.Path;
                }

                Progress(baseProgress, $"解析中 ({i + 1}/{files.Count}): {file.FileName}");
                var progress = new Progress<double>(p => Progress(baseProgress + share * p, $"解析中 ({i + 1}/{files.Count}): {file.FileName}"));
                var raw = file.RawPath;
                file.Analysis = await Task.Run(() => DvParser.Analyze(raw, progress, ct), ct);
                Grouping.BuildRuns(file);
                Log($"解析: {file.FileName}  {file.Analysis.Frames.Length} フレーム, 映像エラーフレーム {file.Analysis.ErrorFrameCount}, 音声エラーフレーム {file.Analysis.AudioErrorFrameCount}, TC読取不可 {file.Analysis.TcMissingCount}, 区間 {file.Runs.Count}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                file.Error = file.InContainer && e is InvalidDataException
                    ? "映像がDV形式ではありません（DV以外の形式に変換されたファイルは使えません）"
                    : e.Message;
                file.Analysis = null;
                Log($"エラー: {file.FileName}: {file.Error}");
            }
        }
        Progress(1, "解析完了");
    }

    async Task<string> ExtractDvStreamAsync(string sourcePath, CancellationToken ct)
    {
        if (Ffmpeg == null)
            throw new InvalidOperationException("AVI・MOVの読み込みにはffmpegが必要です。設定でffmpeg.exeの場所を指定してください。");

        var info = new FileInfo(sourcePath);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..16];
        var target = Path.Combine(CacheDir, key + ".dv");
        if (File.Exists(target))
            return target;

        var part = target + ".part";
        // Stream copy of the video track: DV video packets in AVI and QuickTime are complete DIF frames,
        // so the raw output is the original DIF data, byte for byte.
        int code = await ToolRunner.RunAsync(Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath, "-map", "0:v:0", "-c", "copy", "-f", "rawvideo", part],
            Log, ct);
        if (code != 0 || !File.Exists(part))
        {
            TryDelete(part);
            throw new InvalidOperationException($"ffmpegでの読み込みに失敗しました (終了コード {code})");
        }
        File.Move(part, target, true);
        return target;
    }

    /// <summary>Deletes the DV streams extracted from AVI / MOV files (only the files this tool created).</summary>
    public static void ClearCache(string workDir)
    {
        var dir = Path.Combine(workDir, "cache");
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            int dot = name.IndexOf('.');
            if (dot == 16 && name[..16].All(Uri.IsHexDigit) && (name[16..] is ".dv" or ".dv.part"))
                TryDelete(file);
        }
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// True when the audio sampling frequency or quantization changes within the inputs
    /// (formats seen on fewer frames than one second are ignored as damaged packs).
    /// </summary>
    public static bool HasMixedAudioFormats(IEnumerable<MergeGroup> groups) => groups
        .SelectMany(g => g.Runs)
        .SelectMany(r => r.Source.Analysis!.Frames.Skip(r.StartFrame).Take(r.FrameCount))
        .Where(f => f.AudioCode != 0)
        .GroupBy(f => f.AudioCode)
        .Count(g => g.Count() >= 30) > 1;

    // ----------------------------------------------------------------------------------------
    // Merge
    // ----------------------------------------------------------------------------------------

    public async Task MergeAsync(IList<MergeGroup> groups, string outputPath, OutputFormat format, bool saveLogs, CancellationToken ct)
    {
        if (UseDvrescue && Dvrescue == null)
            throw new InvalidOperationException("dvrescue.exe が見つかりません。設定で場所を指定してください。");
        if (format != OutputFormat.Dv && Ffmpeg == null)
            throw new InvalidOperationException(".dv 以外の形式で出力するにはffmpegが必要です。");
        if (groups.Count == 0)
            throw new InvalidOperationException("結合するデータがありません。");
        if (groups.Select(g => g.Runs[0].Source.Analysis!.IsPal).Distinct().Count() > 1)
            throw new InvalidOperationException("NTSCとPALのファイルが混在しています。同じ方式のファイルだけを追加してください。");
        if (groups.SelectMany(g => g.Runs).Any(r => string.Equals(r.Source.Path, Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("出力ファイルが入力ファイルに含まれています。別の出力ファイル名を指定してください。");
        if (format != OutputFormat.Dv && HasMixedAudioFormats(groups))
            Log("警告: 途中で音声の形式（サンプリング周波数・ビット数）が変わっています。.dv 以外の形式では音声が正しく出力されない場合があります。");

        var jobDir = Path.Combine(WorkDir, "job-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(jobDir);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (UseDvrescue && !ToolRunner.IsAscii(jobDir))
            Log("警告: 作業フォルダーのパスに英数字以外が含まれています。dvrescueが失敗する場合は設定で変更してください。");

        try
        {
            long totalFrames = groups.Sum(g => (long)g.Runs.Max(r => r.FrameCount));
            long doneFrames = 0;

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                await MergeGroupAsync(group, jobDir, totalFrames, doneFrames, ct);
                doneFrames += group.Runs.Max(r => r.FrameCount);
            }

            var chapters = BuildChapters(groups);
            Progress(0.95, "出力ファイルを作成中...");
            if (format == OutputFormat.Dv)
                await ConcatDvAsync(groups, outputPath, ct);
            else
                await RemuxAsync(groups, chapters, outputPath, format, jobDir, ct);

            if (saveLogs)
                WriteLogs(groups, chapters, outputPath);

            Log($"完了: {outputPath}");
            Progress(1, "完了");
        }
        finally
        {
            try { Directory.Delete(jobDir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("作業ファイルを削除できませんでした: " + jobDir); }
        }
    }

    async Task MergeGroupAsync(MergeGroup group, string jobDir, long totalFrames, long doneFrames, CancellationToken ct)
    {
        string status = $"グループ {group.Index} を処理中 (入力 {group.Runs.Count} 件)";
        Progress((double)doneFrames / totalFrames * 0.95, status);

        if (group.Runs.Count == 1)
        {
            // Nothing to merge with: the frames are used as they are.
            var run = group.Runs[0];
            var path = run.CoversWholeFile ? run.Source.RawPath! : await WriteRunAsync(run, Path.Combine(jobDir, $"g{group.Index:0000}_in0.dv"), ct);
            group.OutputPath = path;
            group.Result = Slice(run.Source.Analysis!, run.StartFrame, run.EndFrame, path);
            group.MergeLogPath = null;
            Log($"グループ {group.Index}: 入力が1件のためそのまま使用 ({run.Label})");
            return;
        }

        Log($"グループ {group.Index}: " + string.Join(" + ", group.Runs.Select(r => r.Label)));
        if (UseDvrescue)
            await MergeWithDvrescueAsync(group, jobDir, totalFrames, doneFrames, status, ct);
        else
        {
            var path = Path.Combine(jobDir, $"g{group.Index:0000}.dv");
            long groupFrames = group.Runs.Max(r => r.FrameCount);
            var stats = await Task.Run(() => BlockMerger.MergeAsync(group, path,
                p => Progress((doneFrames + p * groupFrames) / totalFrames * 0.95, status), ct), ct);
            group.OutputPath = path;
            group.MergeLogPath = null;
            Log($"グループ {group.Index}: {stats.Frames} フレーム出力, ブロック修復 {stats.FramesRepaired} フレーム, サブコード(タイムコード等)修復 {stats.FramesMetaRepaired} フレーム, 破損のため除外 {stats.FramesDropped} フレーム, 絵が一致せず除外した候補 {stats.CandidatesRejected}");
            if (stats.FramesWithoutTc > 0)
                Log($"グループ {group.Index}: 警告: タイムコードが決まらず位置を特定できない入力フレーム {stats.FramesWithoutTc} 個は使用していません");
        }

        group.Result = await Task.Run(() => DvParser.Analyze(group.OutputPath!, null, ct), ct);
        Grouping.RepairTimeCodes(group.Result.Frames);
        Log($"グループ {group.Index}: 映像エラーフレーム {string.Join(", ", group.Runs.Select(r => r.ErrorFrames))} → 結合後 {group.Result.ErrorFrameCount}, " +
            $"音声エラーフレーム {string.Join(", ", group.Runs.Select(r => r.AudioErrorFrames))} → 結合後 {group.Result.AudioErrorFrameCount}");
    }

    async Task MergeWithDvrescueAsync(MergeGroup group, string jobDir, long totalFrames, long doneFrames, string status, CancellationToken ct)
    {
        var inputs = new List<string>();
        for (int k = 0; k < group.Runs.Count; k++)
        {
            var run = group.Runs[k];
            string name = $"g{group.Index:0000}_in{k}";
            if (run.CoversWholeFile)
                inputs.Add(ToolRunner.MakeAsciiPath(run.Source.RawPath!, jobDir, name));
            else
                inputs.Add(await WriteRunAsync(run, Path.Combine(jobDir, name + ".dv"), ct));
        }

        var output = Path.Combine(jobDir, $"g{group.Index:0000}.dv");
        var csv = Path.Combine(jobDir, $"g{group.Index:0000}_merge.csv");
        var args = new List<string>(inputs) { "--merge", output, "--merge-log", csv, "--csv" };

        long expected = (long)group.Runs.Max(r => r.FrameCount) * group.Runs[0].Source.Analysis!.FrameSize;
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var poll = Task.Run(async () =>
        {
            while (!pollCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, pollCts.Token);
                    long size = File.Exists(output) ? new FileInfo(output).Length : 0;
                    double p = Math.Min(1.0, (double)size / expected);
                    Progress((doneFrames + p * (expected / group.Runs[0].Source.Analysis!.FrameSize)) / totalFrames * 0.95, status);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { }
            }
        });

        int code;
        try
        {
            code = await ToolRunner.RunAsync(Dvrescue!, args, Log, ct, logStdout: false);
        }
        finally
        {
            pollCts.Cancel();
            await poll;
        }
        if (code != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException($"dvrescueでの結合に失敗しました (グループ {group.Index}, 終了コード {code})");

        // Inputs written for this group are no longer needed
        foreach (var input in inputs.Where(p => p.StartsWith(jobDir, StringComparison.OrdinalIgnoreCase)))
            TryDelete(input);

        group.OutputPath = output;
        group.MergeLogPath = csv;
    }

    static async Task<string> WriteRunAsync(Run run, string path, CancellationToken ct)
    {
        int frameSize = run.Source.Analysis!.FrameSize;
        await using var src = new FileStream(run.Source.RawPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        src.Position = (long)run.StartFrame * frameSize;
        await CopyBytesAsync(src, dst, (long)run.FrameCount * frameSize, ct);
        return path;
    }

    static async Task CopyBytesAsync(Stream src, Stream dst, long count, CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        while (count > 0)
        {
            int n = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), ct);
            if (n == 0)
                break;
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            count -= n;
        }
    }

    static DvAnalysis Slice(DvAnalysis a, int start, int end, string path) => new()
    {
        Path = path,
        FrameSize = a.FrameSize,
        IsPal = a.IsPal,
        Frames = a.Frames[start..end],
    };

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ----------------------------------------------------------------------------------------
    // Output
    // ----------------------------------------------------------------------------------------

    /// <summary>A chapter starts at each recording start, detected by a jump of the recording date/time.</summary>
    static List<Chapter> BuildChapters(IList<MergeGroup> groups)
    {
        var chapters = new List<Chapter>();
        long pos = 0;
        long lastRdt = 0;
        long chapterStart = 0;
        DateTime? chapterRdt = null;
        bool first = true;
        long threshold = TimeSpan.FromSeconds(3).Ticks;
        foreach (var g in groups)
        {
            foreach (var f in g.Result!.Frames)
            {
                if (f.Rdt != 0)
                {
                    if (first)
                    {
                        chapterRdt = new DateTime(f.Rdt);
                        first = false;
                    }
                    else if (lastRdt != 0 && (f.Rdt < lastRdt || f.Rdt - lastRdt > threshold))
                    {
                        chapters.Add(new Chapter(chapterStart, pos, chapterRdt));
                        chapterStart = pos;
                        chapterRdt = new DateTime(f.Rdt);
                    }
                    lastRdt = f.Rdt;
                }
                pos++;
            }
        }
        if (pos > chapterStart)
            chapters.Add(new Chapter(chapterStart, pos, chapterRdt));
        return chapters;
    }

    async Task ConcatDvAsync(IList<MergeGroup> groups, string outputPath, CancellationToken ct)
    {
        var part = outputPath + ".part";
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            foreach (var g in groups)
            {
                await using var src = new FileStream(g.OutputPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                await src.CopyToAsync(dst, 1 << 20, ct);
            }
        }
        File.Move(part, outputPath, true);
    }

    async Task RemuxAsync(IList<MergeGroup> groups, List<Chapter> chapters, string outputPath, OutputFormat format, string jobDir, CancellationToken ct)
    {
        var first = groups[0].Result!;
        var list = Path.Combine(jobDir, "concat.txt");
        File.WriteAllLines(list, groups.Select(g => "file '" + g.OutputPath!.Replace("\\", "/").Replace("'", "'\\''") + "'"), new UTF8Encoding(false));

        var meta = Path.Combine(jobDir, "metadata.txt");
        var sb = new StringBuilder(";FFMETADATA1\n");
        var timeBase = first.IsPal ? "1/25" : "1001/30000";
        if (format != OutputFormat.Avi)
        {
            foreach (var c in chapters)
            {
                sb.Append("[CHAPTER]\n");
                sb.Append($"TIMEBASE={timeBase}\nSTART={c.StartFrame}\nEND={c.EndFrame}\n");
                sb.Append("title=").Append(EscapeMeta(c.Rdt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "(撮影日時なし)")).Append('\n');
            }
        }
        File.WriteAllText(meta, sb.ToString(), new UTF8Encoding(false));

        var part = Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + ".part" + Path.GetExtension(outputPath));
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "concat", "-safe", "0", "-i", list,
            "-f", "ffmetadata", "-i", meta,
            "-map", "0:v", "-map", "0:a?", "-map_metadata", "1", "-map_chapters", "1",
            "-c", "copy",
        };

        // Container-level metadata from the DV stream (the DIF frames themselves keep all of their original packs)
        var firstRdt = groups.SelectMany(g => g.Result!.Frames).Select(f => f.Rdt).FirstOrDefault(r => r != 0);
        if (firstRdt != 0)
        {
            var local = DateTime.SpecifyKind(new DateTime(firstRdt), DateTimeKind.Local);
            args.AddRange(["-metadata", "creation_time=" + local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.000000Z", CultureInfo.InvariantCulture)]);
        }
        var firstTc = first.Frames.FirstOrDefault(f => f.Tc >= 0);
        if (format != OutputFormat.Avi && first.Frames.Any(f => f.Tc >= 0))
            args.AddRange(["-timecode", DvParser.FramesToTimeCode(firstTc.Tc, first.TcBase, firstTc.DropFrame)]);

        args.AddRange(["-f", format switch { OutputFormat.Avi => "avi", OutputFormat.Mov => "mov", _ => "matroska" }, part]);

        int code = await ToolRunner.RunAsync(Ffmpeg!, args, Log, ct);
        if (code != 0 || !File.Exists(part))
            throw new InvalidOperationException($"ffmpegでの出力に失敗しました (終了コード {code})");
        File.Move(part, outputPath, true);
    }

    static string EscapeMeta(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    void WriteLogs(IList<MergeGroup> groups, List<Chapter> chapters, string outputPath)
    {
        var logDir = Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + "_logs");
        Directory.CreateDirectory(logDir);
        var fps = groups[0].Result!.FrameRate;
        var inv = CultureInfo.InvariantCulture;

        var report = new StringBuilder();
        report.AppendLine("DVTapeMerger 結合レポート");
        report.AppendLine("出力: " + Path.GetFileName(outputPath));
        report.AppendLine();
        long totalFrames = 0, totalErrorFrames = 0, totalAudioErrorFrames = 0;
        foreach (var g in groups)
        {
            var r = g.Result!;
            totalFrames += r.Frames.Length;
            totalErrorFrames += r.ErrorFrameCount;
            totalAudioErrorFrames += r.AudioErrorFrameCount;
            report.AppendLine($"グループ {g.Index}: TC {DvParser.FramesToTimeCode(g.TcStart, r.TcBase, r.Frames.FirstOrDefault().DropFrame)} - {DvParser.FramesToTimeCode(g.TcEnd, r.TcBase, r.Frames.FirstOrDefault().DropFrame)}, {r.Frames.Length} フレーム");
            foreach (var run in g.Runs)
                report.AppendLine($"    入力: {run.Label}  {run.FrameCount} フレーム, 映像エラーフレーム {run.ErrorFrames} (ブロック率 {run.ErrorRatio.ToString("P3", inv)}), 音声エラーフレーム {run.AudioErrorFrames}");
            report.AppendLine($"    結合後: 映像エラーフレーム {r.ErrorFrameCount} (ブロック率 {r.ErrorBlockRatio.ToString("P3", inv)}), 音声エラーフレーム {r.AudioErrorFrameCount}");
            if (g.MergeLogPath != null && File.Exists(g.MergeLogPath))
                File.Copy(g.MergeLogPath, Path.Combine(logDir, Path.GetFileName(g.MergeLogPath)), true);
        }
        report.AppendLine();
        report.AppendLine($"合計: {totalFrames} フレーム, 映像エラーが残るフレーム {totalErrorFrames}, 音声エラーが残るフレーム {totalAudioErrorFrames}");
        File.WriteAllText(Path.Combine(logDir, "report.txt"), report.ToString(), new UTF8Encoding(true));

        var scenes = new StringBuilder("No,Start,End,RecordingDateTime\n");
        for (int i = 0; i < chapters.Count; i++)
        {
            var c = chapters[i];
            scenes.Append(inv, $"{i + 1},{TimeSpan.FromSeconds(c.StartFrame / fps):hh\\:mm\\:ss\\.fff},{TimeSpan.FromSeconds(c.EndFrame / fps):hh\\:mm\\:ss\\.fff},{c.Rdt?.ToString("yyyy-MM-dd HH:mm:ss", inv)}\n");
        }
        File.WriteAllText(Path.Combine(logDir, "scenes.csv"), scenes.ToString(), new UTF8Encoding(true));
        Log("ログを保存しました: " + logDir);
    }
}
