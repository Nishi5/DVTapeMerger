using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DVTapeMerger;

public static class ToolRunner
{
    /// <summary>Runs an external tool, forwarding its output lines to <paramref name="log"/>. Returns the exit code.</summary>
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string>? log, CancellationToken ct, bool logStdout = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        log?.Invoke("> " + Path.GetFileName(exe) + " " + string.Join(" ", psi.ArgumentList.Select(Quote)));
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null && logStdout) log?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        return p.ExitCode;
    }

    static string Quote(string s) => s.Contains(' ') || s.Contains(';') ? "\"" + s + "\"" : s;

    public static string? FindTool(string? configured, string exeName)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        var local = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(local))
            return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static bool IsAscii(string s) => s.All(c => c < 0x80);

    /// <summary>
    /// dvrescue reads its arguments in the ANSI code page, so a path with characters outside of it
    /// is exposed through a hard link (or a copy) with an ASCII name inside the work directory.
    /// </summary>
    public static string MakeAsciiPath(string path, string workDir, string name)
    {
        if (IsAscii(path))
            return path;
        var link = Path.Combine(workDir, name + Path.GetExtension(path));
        if (File.Exists(link))
            File.Delete(link);
        if (!CreateHardLink(link, path, IntPtr.Zero))
            File.Copy(path, link);
        return link;
    }
}
