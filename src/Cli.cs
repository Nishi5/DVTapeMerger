using System.Runtime.InteropServices;

namespace DVTapeMerger;

/// <summary>
/// Command line mode: DVTapeMerger.exe --cli [options] -o output.(dv|avi|mov|mkv) input files or folders...
/// </summary>
static class Cli
{
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    public static int Run(string[] args)
    {
        AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var settings = AppSettings.Load();
        string? output = null, dvrescue = settings.DvrescuePath, ffmpeg = settings.FfmpegPath, workDir = settings.WorkDir;
        bool saveLogs = true, analyzeOnly = false, useDvrescue = settings.UseDvrescue;
        var inputs = new List<string>();
        bool badArgs = false;
        for (int i = 0; i < args.Length && !badArgs; i++)
        {
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "-o": output = Value(); badArgs = output == null; break;
                case "--dvrescue": dvrescue = Value(); badArgs = dvrescue == null; break;
                case "--ffmpeg": ffmpeg = Value(); badArgs = ffmpeg == null; break;
                case "--work": workDir = Value(); badArgs = workDir == null; break;
                case "--no-logs": saveLogs = false; break;
                case "--analyze": analyzeOnly = true; break;
                case "--engine":
                    var engineName = Value();
                    badArgs = engineName is not ("builtin" or "dvrescue");
                    useDvrescue = engineName == "dvrescue";
                    break;
                default: inputs.Add(args[i]); break;
            }
        }
        if (badArgs || inputs.Count == 0 || (output == null && !analyzeOnly))
        {
            Console.WriteLine("Usage: DVTapeMerger.exe --cli [--dvrescue exe] [--ffmpeg exe] [--work dir] [--no-logs] [--analyze] [--engine builtin|dvrescue] -o output.(dv|avi|mov|mkv) inputs...");
            return 2;
        }

        var files = inputs
            .SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase) : new[] { p }.AsEnumerable())
            .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".dv" or ".avi" or ".dif")
            .Select(p => new SourceFile { Path = Path.GetFullPath(p) })
            .ToList();

        var engine = new MergeEngine
        {
            WorkDir = string.IsNullOrWhiteSpace(workDir) ? Path.Combine(Path.GetTempPath(), "DVTapeMerger") : workDir,
            Dvrescue = ToolRunner.FindTool(dvrescue, "dvrescue.exe"),
            Ffmpeg = ToolRunner.FindTool(ffmpeg, "ffmpeg.exe"),
            UseDvrescue = useDvrescue,
            Log = Console.WriteLine,
            Progress = (_, _) => { },
        };

        try
        {
            engine.AnalyzeAsync(files, CancellationToken.None).GetAwaiter().GetResult();
            var groups = Grouping.BuildGroups(files);
            foreach (var g in groups)
            {
                var a = g.Runs[0].Source.Analysis!;
                bool df = a.Frames.FirstOrDefault(f => f.Tc >= 0).DropFrame;
                Console.WriteLine($"Group {g.Index}: TC {DvParser.FramesToTimeCode(g.TcStart, a.TcBase, df)} - {DvParser.FramesToTimeCode(g.TcEnd, a.TcBase, df)}");
                foreach (var r in g.Runs)
                    Console.WriteLine($"    {r.Label}: {r.FrameCount} frames, error {r.ErrorRatio:P3}");
            }
            if (analyzeOnly)
                return 0;

            var format = Path.GetExtension(output!).ToLowerInvariant() switch
            {
                ".avi" => OutputFormat.Avi,
                ".mov" => OutputFormat.Mov,
                ".mkv" => OutputFormat.Mkv,
                _ => OutputFormat.Dv,
            };
            engine.MergeAsync(groups, Path.GetFullPath(output!), format, saveLogs, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("Error: " + e.Message);
            return 1;
        }
        finally
        {
            // DV streams extracted from AVI files are only a cache
            try { MergeEngine.ClearCache(engine.WorkDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
