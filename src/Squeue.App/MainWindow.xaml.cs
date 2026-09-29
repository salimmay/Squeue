using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Squeue.Core.FileSystem;
using Squeue.Core.Jobs;
using Squeue.ViewModels;

namespace Squeue.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IFileSystem _fs = new WindowsFileSystem();

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private const int WM_DEVICECHANGE = 0x0219;

    /// Refreshes the drive pills when a drive or card is added or removed.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE) Dispatcher.BeginInvoke(() => _viewModel.RefreshDrivesCommand.Execute(null));
        return IntPtr.Zero;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        // Let Explorer finish the drop before showing a dialog.
        Dispatcher.BeginInvoke(() => ChooseDestinationAndPlan(paths));
    }

    private void OnAddFolders(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose folders to copy", Multiselect = true };
        if (dialog.ShowDialog(this) == true) ChooseDestinationAndPlan(dialog.FolderNames);
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose files to copy", Multiselect = true };
        if (dialog.ShowDialog(this) == true) ChooseDestinationAndPlan(dialog.FileNames);
    }

    private async void ChooseDestinationAndPlan(IReadOnlyList<string> sources)
    {
        var dialog = new OpenFolderDialog { Title = "Copy to" };
        if (dialog.ShowDialog(this) != true) return;
        string destination = dialog.FolderName;
        try
        {
            // Listing a big card can take a moment; keep the window responsive.
            var plan = await Task.Run(() => JobPlanner.Plan(_fs, sources, destination));
            _viewModel.ProposePlan(plan);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Squeue", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
