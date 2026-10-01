using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Squeue");
            string journalPath = Path.Combine(appData, "state.db");
            var settingsStore = new JsonSettingsStore(Path.Combine(appData, "settings.json"));
            _runner = new JobRunner(new WindowsFileSystem(), journalPath,
                new JobRunnerOptions { HistoryDays = settingsStore.Load().HistoryDays });
            var viewModel = new MainViewModel(_runner, new SystemDrives(), action => Dispatcher.BeginInvoke(action),
                settingsStore, OpenFolder);
            viewModel.QueueFinished += () => OnQueueFinished(viewModel);
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

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"{path} isn't available. The drive may not be connected.", "Squeue", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OnQueueFinished(MainViewModel viewModel)
    {
        switch (viewModel.Settings.Current.WhenDone)
        {
            case WhenDone.Sound:
                SystemSounds.Asterisk.Play();
                break;
            case WhenDone.Flash when MainWindow is { } window:
                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = new WindowInteropHelper(window).Handle,
                    dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG, // flash until the window comes to the foreground
                };
                FlashWindowEx(ref info);
                break;
        }
    }

    private const uint FLASHW_TRAY = 2;
    private const uint FLASHW_TIMERNOFG = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    protected override void OnExit(ExitEventArgs e)
    {
        // Stops the current file safely; unfinished jobs carry on at the next launch.
        _runner?.Dispose();
        if (_runner is not null) _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
