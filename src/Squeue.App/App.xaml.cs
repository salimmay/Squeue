using System.IO;
using System.Windows;
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.ViewModels;

namespace Squeue.App;

public partial class App : Application
{
    private JobRunner? _runner;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(initiallyOwned: true, $"Squeue.SingleInstance.{Environment.UserName}", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Squeue is already running.", "Squeue", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        try
        {
            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(args.Exception.Message, "Squeue", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            string journalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Squeue", "state.db");
            _runner = new JobRunner(new WindowsFileSystem(), journalPath);
            var viewModel = new MainViewModel(_runner, new SystemDrives(), action => Dispatcher.BeginInvoke(action));
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
            _runner.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Squeue", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Stops the current file safely; unfinished jobs carry on at the next launch.
        _runner?.Dispose();
        if (_runner is not null) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
