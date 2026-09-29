using System.IO;
using System.Windows;
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.ViewModels;

namespace Squeue.App;

public partial class App : Application
{
    private JobRunner? _runner;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string journalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Squeue", "state.db");
        _runner = new JobRunner(new WindowsFileSystem(), journalPath);
        var viewModel = new MainViewModel(_runner, new SystemDrives(), action => Dispatcher.BeginInvoke(action));
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
        _runner.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stops the current file safely; unfinished jobs carry on at the next launch.
        _runner?.Dispose();
        base.OnExit(e);
    }
}
