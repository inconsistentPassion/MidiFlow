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
    private Storyboard? _fadeIn;
    private Storyboard? _fadeOut;
    private Storyboard? _settingsIn;
    private Storyboard? _settingsOut;
    private bool _uiVisible;
    private bool _settingsVisible;
    private bool _layoutReady;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        _vm.RenderFrameReady += OnRenderFrameReady;
        _vm.ShowMessage += msg => MessageBox.Show(msg, "PianoFlow", MessageBoxButton.OK, MessageBoxImage.Warning);

        _renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / 60)
        };
        _renderTimer.Tick += OnRenderTick;
    }

    private void SetupAnimations()
    {
        if (StatusBar == null || SettingsPanel == null) return;

        _fadeIn = new Storyboard();
        var fi = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
        Storyboard.SetTarget(fi, StatusBar);
        Storyboard.SetTargetProperty(fi, new PropertyPath(OpacityProperty));
        _fadeIn.Children.Add(fi);

        _fadeOut = new Storyboard();
        var fo = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        Storyboard.SetTarget(fo, StatusBar);
        Storyboard.SetTargetProperty(fo, new PropertyPath(OpacityProperty));
        _fadeOut.Children.Add(fo);

        _settingsIn = new Storyboard();
        var si = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
        Storyboard.SetTarget(si, SettingsPanel);
        Storyboard.SetTargetProperty(si, new PropertyPath(OpacityProperty));
        _settingsIn.Children.Add(si);

        _settingsOut = new Storyboard();
        var so = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        Storyboard.SetTarget(so, SettingsPanel);
        Storyboard.SetTargetProperty(so, new PropertyPath(OpacityProperty));
        _settingsOut.Children.Add(so);
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (!_layoutReady) return;
        try
        {
            _vm.UpdateFrame();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetupAnimations();
        RefreshRendererLayout();
        _layoutReady = true;

        // Start rendering
        _renderTimer.Start();

        // Populate MIDI devices
        RefreshMidiDevices();

        _uiVisible = false;
    }

    private void RefreshRendererLayout()
    {
        int w = (int)ActualWidth;
        int h = (int)ActualHeight;
        if (w <= 0 || h <= 0) return;

        _vm.Width = w;
        _vm.Height = h;

        int pianoH = (int)(h * _vm.PianoHeightPercent / 100.0);
        if (pianoH < 20) pianoH = (int)(h * 0.11);
        _vm.PianoHeight = pianoH;

        _vm.NoteRenderer.UpdateLayout(w, h, _vm.PianoHeight);
        RenderImage.Source = _vm.NoteRenderer.Bitmap;
    }

    private void OnRenderFrameReady()
    {
        if (_vm.Bitmap == null) return;
        RenderImage.Source = _vm.Bitmap;

        PlayStateText.Text = _vm.IsPlaying
            ? (_vm.IsPaused ? "⏸ Paused" : "▶ Playing")
            : "⏹ Stopped";
        TimeText.Text = _vm.TimeDisplay;
        MidiDeviceText.Text = _vm.MidiDeviceName != null ? $"🎹 {_vm.MidiDeviceName}" : "";
        StatusText.Text = _vm.StatusText ?? "";

        WelcomeOverlay.Visibility = _vm.FilePath != null ? Visibility.Collapsed : Visibility.Visible;
    }

    // --- F1 toggle for UI ---
    private void ToggleUI()
    {
        _uiVisible = !_uiVisible;

        if (_uiVisible)
        {
            StatusBar.Visibility = Visibility.Visible;
            _fadeIn?.Begin();
        }
        else
        {
            _fadeOut?.Begin();
            var d = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            d.Tick += (s, ev) =>
            {
                if (!_uiVisible) StatusBar.Visibility = Visibility.Collapsed;
                d.Stop();
            };
            d.Start();
        }
    }

    private void ToggleSettings()
    {
        _settingsVisible = !_settingsVisible;

        if (_settingsVisible)
        {
            SettingsPanel.Visibility = Visibility.Visible;
            _settingsIn?.Begin();
        }
        else
        {
            _settingsOut?.Begin();
            var d = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            d.Tick += (s, ev) =>
            {
                if (!_settingsVisible) SettingsPanel.Visibility = Visibility.Collapsed;
                d.Stop();
            };
            d.Start();
        }
    }

    private void RefreshMidiDevices()
    {
        MidiDeviceCombo.Items.Clear();
        var devices = _vm.GetMidiDevices();
        foreach (var d in devices)
        {
            MidiDeviceCombo.Items.Add(d.Name);
        }
        if (MidiDeviceCombo.Items.Count > 0)
            MidiDeviceCombo.SelectedIndex = 0;
    }

    // --- Keyboard Input ---
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F1:
                ToggleUI();
                e.Handled = true;
                break;

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
                DirFalling.IsChecked = _vm.Falling;
                DirRising.IsChecked = !_vm.Falling;
                e.Handled = true;
                break;

            case Key.Up:
                _vm.NoteSpeed += 50;
                SpeedSlider.Value = _vm.NoteSpeed;
                e.Handled = true;
                break;

            case Key.Down:
                _vm.NoteSpeed -= 50;
                SpeedSlider.Value = _vm.NoteSpeed;
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
                if (_settingsVisible)
                    ToggleSettings();
                else
                    Close();
                e.Handled = true;
                break;
        }
    }

    private void OnOpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "MIDI Files (*.mid;*.midi)|*.mid;*.midi|All Files (*.*)|*.*",
            Title = "Open MIDI File"
        };

        if (dlg.ShowDialog() == true)
            LoadMidiFile(dlg.FileName);
    }

    private void LoadMidiFile(string path)
    {
        if (_vm.LoadFile(path))
        {
            RefreshRendererLayout();
            _vm.Play();
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
                LoadMidiFile(files[0]);
        }
    }

    // --- Button handlers ---
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ToggleSettings();
    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => ToggleSettings();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MidiDeviceCombo_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int idx = MidiDeviceCombo.SelectedIndex;
        if (idx >= 0)
            _vm.ConnectMidiDevice(idx);
    }

    private void SpeedSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vm == null || SpeedLabel == null) return;
        _vm.NoteSpeed = e.NewValue;
        SpeedLabel.Text = $"{(int)_vm.NoteSpeed} px/s";
    }

    private void Direction_Changed(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _vm.Falling = DirFalling.IsChecked == true;
    }

    private void PianoHeightSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vm == null || !_layoutReady) return;
        _vm.PianoHeightPercent = (int)e.NewValue;
        PianoHeightLabel.Text = $"{(int)e.NewValue}%";
        RefreshRendererLayout();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
    }

    private void WindowedButton_Click(object sender, RoutedEventArgs e)
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowState = WindowState.Normal;
    }

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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _renderTimer.Stop();
        _vm.Dispose();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsLoaded)
            RefreshRendererLayout();
    }
}
