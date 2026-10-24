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
        bool saveLogs = true, analyzeOnly = false;
        var inputs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": output = args[++i]; break;
                case "--dvrescue": dvrescue = args[++i]; break;
                case "--ffmpeg": ffmpeg = args[++i]; break;
                case "--work": workDir = args[++i]; break;
                case "--no-logs": saveLogs = false; break;
                case "--analyze": analyzeOnly = true; break;
                default: inputs.Add(args[i]); break;
            }
        }
        if (inputs.Count == 0 || (output == null && !analyzeOnly))
        {
            Console.WriteLine("Usage: DVTapeMerger.exe --cli [--dvrescue exe] [--ffmpeg exe] [--work dir] [--no-logs] [--analyze] -o output.(dv|avi|mov|mkv) inputs...");
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
    }
}
