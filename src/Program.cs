namespace DVTapeMerger;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--cli")
        {
            Environment.Exit(Cli.Run(args[1..]));
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
