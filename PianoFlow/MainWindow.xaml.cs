using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using PianoFlow.ViewModels;

namespace PianoFlow;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _statusBarTimer;
    private Storyboard? _fadeIn;
    private Storyboard? _fadeOut;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.RenderFrameReady += OnRenderFrameReady;
        _vm.ShowMessage += msg => MessageBox.Show(msg, "PianoFlow", MessageBoxButton.OK, MessageBoxImage.Warning);

        // Render timer
        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 60) // 60 FPS
        };
        _renderTimer.Tick += (s, e) => _vm.UpdateFrame();

        // Status bar auto-hide timer
        _statusBarTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _statusBarTimer.Tick += (s, e) => FadeOutStatusBar();

        // Setup status bar animations
        SetupAnimations();
    }

    private void SetupAnimations()
    {
        _fadeIn = new Storyboard();
        var fadeInAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        Storyboard.SetTarget(fadeInAnim, StatusBar);
        Storyboard.SetTargetProperty(fadeInAnim, new PropertyPath(OpacityProperty));
        _fadeIn.Children.Add(fadeInAnim);

        _fadeOut = new Storyboard();
        var fadeOutAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500));
        Storyboard.SetTarget(fadeOutAnim, StatusBar);
        Storyboard.SetTargetProperty(fadeOutAnim, new PropertyPath(OpacityProperty));
        _fadeOut.Children.Add(fadeOutAnim);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize renderer with current window size
        UpdateViewLayout();

        // Show status bar initially
        FadeInStatusBar();

        // Try to connect to first MIDI device
        var devices = _vm.GetMidiDevices();
        if (devices.Count > 0)
        {
            _vm.ConnectMidiDevice(devices[0].Index);
        }
    }

    private void UpdateViewLayout()
    {
        int w = (int)ActualWidth;
        int h = (int)ActualHeight;
        if (w <= 0 || h <= 0) return;

        _vm.Width = w;
        _vm.Height = h;
        _vm.PianoHeight = (int)(h * 0.11); // ~11% of screen height

        // Update renderer layout
        _vm.NoteRenderer.UpdateLayout(w, h, _vm.PianoHeight);

        // Set the Image source
        RenderImage.Source = _vm.NoteRenderer.Bitmap;
    }

    private void OnRenderFrameReady()
    {
        RenderImage.Source = _vm.Bitmap;

        // Update UI text
        PlayStateText.Text = _vm.IsPlaying
            ? (_vm.IsPaused ? "⏸ Paused" : "▶ Playing")
            : "⏹ Stopped";
        TimeText.Text = _vm.TimeDisplay;
        MidiDeviceText.Text = _vm.MidiDeviceName != null ? $"🎹 {_vm.MidiDeviceName}" : "";
        StatusText.Text = _vm.StatusText ?? "";
    }

    // --- Keyboard Input ---
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _vm.TogglePause();
                e.Handled = true;
                break;

            case Key.R:
                _vm.Restart();
                e.Handled = true;
                break;

            case Key.F:
                _vm.FlipDirection();
                e.Handled = true;
                break;

            case Key.Up:
                _vm.NoteSpeed += 50;
                _vm.StatusText = $"Speed: {_vm.NoteSpeed:F0} px/s";
                e.Handled = true;
                break;

            case Key.Down:
                _vm.NoteSpeed -= 50;
                _vm.StatusText = $"Speed: {_vm.NoteSpeed:F0} px/s";
                e.Handled = true;
                break;

            case Key.E:
                OnExport();
                e.Handled = true;
                break;

            case Key.O:
                OnOpenFile();
                e.Handled = true;
                break;

            case Key.Q:
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    // --- File Open ---
    private void OnOpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "MIDI Files (*.mid;*.midi)|*.mid;*.midi|All Files (*.*)|*.*",
            Title = "Open MIDI File"
        };

        if (dlg.ShowDialog() == true)
        {
            LoadMidiFile(dlg.FileName);
        }
    }

    private void LoadMidiFile(string path)
    {
        if (_vm.LoadFile(path))
        {
            UpdateViewLayout();
            _vm.Play();
            FadeInStatusBar();
        }
    }

    // --- Drag & Drop ---
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                LoadMidiFile(files[0]);
            }
        }
    }

    // --- Video Export ---
    private void OnExport()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "MP4 Video (*.mp4)|*.mp4",
            Title = "Export Video",
            FileName = System.IO.Path.GetFileNameWithoutExtension(_vm.FilePath ?? "output") + ".mp4"
        };

        if (dlg.ShowDialog() == true)
        {
            _vm.StartExport(dlg.FileName);
            ExportOverlay.Visibility = Visibility.Visible;
        }
    }

    // --- Status Bar Auto-hide ---
    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        FadeInStatusBar();
        _statusBarTimer.Stop();
        _statusBarTimer.Start();
    }

    private void FadeInStatusBar()
    {
        _fadeOut?.Stop();
        _fadeIn?.Begin();
    }

    private void FadeOutStatusBar()
    {
        _fadeIn?.Stop();
        _fadeOut?.Begin();
        _statusBarTimer.Stop();
    }

    // --- Cleanup ---
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _renderTimer.Stop();
        _statusBarTimer.Stop();
        _vm.Dispose();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsLoaded)
        {
            UpdateViewLayout();
        }
    }
}
