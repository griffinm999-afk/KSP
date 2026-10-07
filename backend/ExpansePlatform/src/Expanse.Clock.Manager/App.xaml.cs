using System;
using System.Windows;
namespace Expanse.Clock.Manager;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? viewPipe = null;
        string? commandPipe = null;
        string? colonyPipe = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] is not ("--view-pipe" or "--command-pipe" or "--colony-pipe")) continue;
            var flag = e.Args[i];
            if ((flag == "--view-pipe" ? viewPipe : flag == "--command-pipe" ? commandPipe : colonyPipe) is not null || i + 1 >= e.Args.Length || string.IsNullOrWhiteSpace(e.Args[i + 1]) || e.Args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                MessageBox.Show("Usage: Expanse.Clock.Manager [--view-pipe <name>] [--command-pipe <name>] [--colony-pipe <name>]", "Expanse Foundations", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2); return;
            }
            if (flag == "--view-pipe") viewPipe = e.Args[++i]; else if (flag == "--command-pipe") commandPipe = e.Args[++i]; else colonyPipe = e.Args[++i];
        }
        new MainWindow(viewPipe, commandPipe, colonyPipe).Show();
    }
}
