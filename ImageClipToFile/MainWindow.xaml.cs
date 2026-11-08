using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ImageClipToFile;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private const int WM_CLIPBOARDUPDATE = 0x031D;

    public static readonly RoutedCommand SavePending = new();
    public static readonly RoutedCommand DiscardPending = new();
    public static readonly RoutedCommand ToggleCapture = new();
    public static readonly RoutedCommand ToggleReview = new();
    public static readonly RoutedCommand ToggleTopmost = new();
    public static readonly RoutedCommand OpenFolder = new();
    public static readonly RoutedCommand ShowAbout = new();

    private readonly AppSettings _settings = AppSettings.Load();
    private HwndSource? _hwndSource;
    private bool _updatePending;

    // The clip waiting for a decision in review mode, already encoded as PNG
    private byte[]? _pendingPng;
    private int _savedCount;

    // Some apps raise several clipboard updates for one copy; skip repeats of the same image
    private string? _lastHash;
    private DateTime _lastHashAt;

    public MainWindow()
    {
        InitializeComponent();

        if (_settings.Left is double left && _settings.Top is double top &&
            left >= SystemParameters.VirtualScreenLeft && top >= SystemParameters.VirtualScreenTop &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        Width = Math.Max(_settings.Width, MinWidth);
        Height = Math.Max(_settings.Height, MinHeight);

        FolderBox.Text = _settings.SaveDirectory;
        CaptureToggle.IsChecked = _settings.Capture;
        ReviewToggle.IsChecked = _settings.ReviewBeforeSaving;
        TopmostToggle.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        UpdateStatus();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this);
        _hwndSource.AddHook(WndProc);
        AddClipboardFormatListener(_hwndSource.Handle);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_pendingPng != null)
        {
            var answer = MessageBox.Show(this, "The clip you are reviewing hasn't been saved. Save it before closing?",
                "ImageClipToFile", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !SavePendingClip()))
            {
                e.Cancel = true;
                return;
            }
        }

        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = Width;
            _settings.Height = Height;
        }
        else
        {
            _settings.Left = RestoreBounds.Left;
            _settings.Top = RestoreBounds.Top;
            _settings.Width = RestoreBounds.Width;
            _settings.Height = RestoreBounds.Height;
        }
        _settings.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource != null)
        {
            RemoveClipboardFormatListener(_hwndSource.Handle);
            _hwndSource.RemoveHook(WndProc);
        }
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Read the clipboard outside the message handler, and only once per burst of updates
        if (msg == WM_CLIPBOARDUPDATE && CaptureToggle.IsChecked == true && !_updatePending)
        {
            _updatePending = true;
            Dispatcher.BeginInvoke(OnClipboardChanged, System.Windows.Threading.DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        _updatePending = false;
        if (CaptureToggle.IsChecked != true)
            return;

        var image = ClipboardImage.TryGet();
        if (image == null)
            return;

        var png = ClipboardImage.EncodePng(image);
        var hash = Convert.ToHexString(SHA256.HashData(png));
        if (hash == _lastHash && DateTime.UtcNow - _lastHashAt < TimeSpan.FromSeconds(2))
            return;
        _lastHash = hash;
        _lastHashAt = DateTime.UtcNow;

        ShowImage(image);

        if (ReviewToggle.IsChecked == true)
        {
            // A new clip replaces the one under review
            _pendingPng = png;
            UpdatePendingUi();
            StatusText.Text = $"New clip ({image.PixelWidth} × {image.PixelHeight}). Save it or discard it; the next clip replaces it.";
        }
        else
        {
            _pendingPng = null;
            UpdatePendingUi();
            if (WritePng(png) is { } path)
                StatusText.Text = $"Saved {Path.GetFileName(path)} ({image.PixelWidth} × {image.PixelHeight}) at {DateTime.Now:HH:mm:ss}";
        }
    }

    private void ShowImage(BitmapSource image)
    {
        Preview.Source = image;
        Placeholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Writes the PNG to the save folder with a time-stamped name. Returns the path, or null on failure.</summary>
    private string? WritePng(byte[] png)
    {
        try
        {
            var folder = _settings.SaveDirectory;
            Directory.CreateDirectory(folder);

            var name = $"imageclip_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            var path = Path.Combine(folder, name + ".png");
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(folder, $"{name}_{n}.png");

            File.WriteAllBytes(path, png);
            _savedCount++;
            Title = $"ImageClipToFile – {_savedCount} saved";
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StatusText.Text = $"Could not save the clip: {ex.Message}";
            return null;
        }
    }

    private bool SavePendingClip()
    {
        if (_pendingPng == null)
            return true;
        if (WritePng(_pendingPng) is not { } path)
            return false;

        _pendingPng = null;
        UpdatePendingUi();
        StatusText.Text = $"Saved {Path.GetFileName(path)} at {DateTime.Now:HH:mm:ss}";
        return true;
    }

    private void UpdatePendingUi()
    {
        var pending = _pendingPng != null;
        PendingActions.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        PendingBadge.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateStatus()
    {
        StatusText.Text = CaptureToggle.IsChecked != true
            ? "Capture is paused. Images on the clipboard are ignored."
            : ReviewToggle.IsChecked == true
                ? "Waiting for an image. Each clip is shown for you to save or discard."
                : "Waiting for an image. Each clip is saved automatically.";
    }

    // --- Commands and handlers ---

    private void HasPending_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _pendingPng != null;

    private void SavePending_Executed(object sender, ExecutedRoutedEventArgs e) => SavePendingClip();

    private void DiscardPending_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _pendingPng = null;
        Preview.Source = null;
        Placeholder.Visibility = Visibility.Visible;
        UpdatePendingUi();
        StatusText.Text = "Clip discarded.";
    }

    private void ToggleCapture_Executed(object sender, ExecutedRoutedEventArgs e) => Flip(CaptureToggle);
    private void ToggleReview_Executed(object sender, ExecutedRoutedEventArgs e) => Flip(ReviewToggle);
    private void ToggleTopmost_Executed(object sender, ExecutedRoutedEventArgs e) => Flip(TopmostToggle);

    private void Flip(ToggleButton toggle)
    {
        toggle.IsChecked = toggle.IsChecked != true;
        ApplyToggles();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => ApplyToggles();

    private void ApplyToggles()
    {
        _settings.Capture = CaptureToggle.IsChecked == true;
        _settings.ReviewBeforeSaving = ReviewToggle.IsChecked == true;
        _settings.AlwaysOnTop = TopmostToggle.IsChecked == true;
        Topmost = _settings.AlwaysOnTop;
        _settings.Save();
        UpdateStatus();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder to save image clips in",
            InitialDirectory = Directory.Exists(_settings.SaveDirectory) ? _settings.SaveDirectory : null,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _settings.SaveDirectory = dialog.FolderName;
        FolderBox.Text = dialog.FolderName;
        _settings.Save();
        StatusText.Text = $"Clips will be saved in {dialog.FolderName}";
    }

    private void ShowAbout_Executed(object sender, ExecutedRoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenSaveFolder();
    private void OpenFolder_Executed(object sender, ExecutedRoutedEventArgs e) => OpenSaveFolder();

    private void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not open the folder: {ex.Message}";
        }
    }
}
