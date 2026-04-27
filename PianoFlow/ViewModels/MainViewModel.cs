using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PianoFlow.Export;
using PianoFlow.Models;
using PianoFlow.Rendering;

namespace PianoFlow.ViewModels;

/// <summary>
/// Main ViewModel: orchestrates MIDI loading, playback, rendering, and export.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // --- State ---
    private MidiFileData? _midiData;
    private readonly MidiDeviceInput _midiDevice = new();
    private readonly PianoFlowVisual _pianoVisual;
    private readonly ParticleSystem _particles = new();
    private readonly VideoExporter _exporter = new();

    // --- Playback ---
    private double _currentTime;
    private bool _isPlaying;
    private bool _isPaused;
    private double _noteSpeed = 300;
    private bool _falling = true;

    // --- Piano state ---
    private bool[] _keyActive = new bool[PianoFlowVisual.NoteCount];
    private double[] _keyFadeTime = new double[PianoFlowVisual.NoteCount];

    // --- Live notes ---
    private readonly List<MidiNote> _liveNotes = new();
    private DateTime _playbackStartTime;

    // --- Export ---
    private bool _isExporting;
    private int _exportFrame;
    private int _exportTotalFrames;

    // --- Config ---
    private int _width = 1920;
    private int _height = 1080;
    private int _pianoHeight = 120;
    private int _fps = 60;
    private string? _filePath;
    private string? _statusText;
    private string? _midiDeviceName;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? RenderFrameReady;
    public event Action<string>? ShowMessage;

    // --- Properties ---
    public PianoFlowVisual PianoVisual => _pianoVisual;

    public bool IsPlaying
    {
        get => _isPlaying;
        set { _isPlaying = value; OnPropertyChanged(nameof(IsPlaying)); }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set { _isPaused = value; OnPropertyChanged(nameof(IsPaused)); }
    }

    public bool IsExporting
    {
        get => _isExporting;
        set { _isExporting = value; OnPropertyChanged(nameof(IsExporting)); }
    }

    public bool Falling
    {
        get => _falling;
        set { _falling = value; OnPropertyChanged(nameof(Falling)); }
    }

    public double NoteSpeed
    {
        get => _noteSpeed;
        set { _noteSpeed = Math.Max(50, Math.Min(2000, value)); OnPropertyChanged(nameof(NoteSpeed)); }
    }

    public double CurrentTime
    {
        get => _currentTime;
        set { _currentTime = value; OnPropertyChanged(nameof(CurrentTime)); OnPropertyChanged(nameof(TimeDisplay)); }
    }

    public double TotalSeconds => _midiData?.TotalSeconds ?? 0;

    public string TimeDisplay =>
        $"{FormatTime(_currentTime)} / {FormatTime(TotalSeconds)}";

    public string? StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
    }

    public string? MidiDeviceName
    {
        get => _midiDeviceName;
        set { _midiDeviceName = value; OnPropertyChanged(nameof(MidiDeviceName)); }
    }

    public string? FilePath
    {
        get => _filePath;
        set { _filePath = value; OnPropertyChanged(nameof(FilePath)); }
    }

    public int Width
    {
        get => _width;
        set { _width = value; OnPropertyChanged(nameof(Width)); }
    }

    public int Height
    {
        get => _height;
        set { _height = value; OnPropertyChanged(nameof(Height)); }
    }

    public int PianoHeight
    {
        get => _pianoHeight;
        set { _pianoHeight = value; OnPropertyChanged(nameof(PianoHeight)); }
    }

    public int PianoHeightPercent
    {
        get => _height > 0 ? (int)(_pianoHeight * 100.0 / _height) : 11;
        set
        {
            PianoHeight = (int)(_height * value / 100.0);
            OnPropertyChanged(nameof(PianoHeightPercent));
        }
    }

    // --- Constructor ---
    public MainViewModel(PianoFlowVisual pianoVisual)
    {
        _pianoVisual = pianoVisual;
        _midiDevice.NoteOn += OnLiveNoteOn;
        _midiDevice.NoteOff += OnLiveNoteOff;
    }

    // --- MIDI File Loading ---
    public bool LoadFile(string path)
    {
        try
        {
            StatusText = "Loading...";
            _midiData = MidiFileParser.Parse(path);
            _filePath = path;
            IsPlaying = false;
            IsPaused = false;
            CurrentTime = 0;

            Array.Clear(_keyActive);
            Array.Clear(_keyFadeTime);
            _liveNotes.Clear();

            StatusText = $"Loaded: {System.IO.Path.GetFileName(path)} " +
                         $"({_midiData.Notes.Count} notes, {FormatTime(_midiData.TotalSeconds)})";

            OnPropertyChanged(nameof(TotalSeconds));
            OnPropertyChanged(nameof(TimeDisplay));
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            ShowMessage?.Invoke($"Failed to load MIDI file:\n{ex.Message}");
            return false;
        }
    }

    // --- Playback Control ---
    public void Play()
    {
        if (_midiData == null) return;
        _playbackStartTime = DateTime.Now - TimeSpan.FromSeconds(_currentTime);
        IsPlaying = true;
        IsPaused = false;
        StatusText = "Playing";
    }

    public void Pause()
    {
        IsPaused = true;
        StatusText = "Paused";
    }

    public void TogglePause()
    {
        if (!IsPlaying) Play();
        else if (IsPaused) Play();
        else Pause();
    }

    public void Restart()
    {
        CurrentTime = 0;
        Array.Clear(_keyActive);
        Array.Clear(_keyFadeTime);
        _liveNotes.Clear();
        _particles.Clear();
        if (IsPlaying) Play();
    }

    public void FlipDirection()
    {
        Falling = !Falling;
        StatusText = Falling ? "Direction: Falling ↓" : "Direction: Rising ↑";
    }

    // --- MIDI Device ---
    public List<(int Index, string Name)> GetMidiDevices() => MidiDeviceInput.GetDevices();

    public void ConnectMidiDevice(int index)
    {
        try
        {
            _midiDevice.Open(index);
            MidiDeviceName = _midiDevice.DeviceName;
            StatusText = $"Connected: {MidiDeviceName}";
        }
        catch (Exception ex)
        {
            StatusText = $"MIDI Error: {ex.Message}";
        }
    }

    public void DisconnectMidiDevice()
    {
        _midiDevice.Close();
        MidiDeviceName = null;
        StatusText = "MIDI disconnected";
    }

    // --- Frame Update ---
    public void UpdateFrame()
    {
        if (_midiData == null && _liveNotes.Count == 0) return;

        // Update playback time
        if (IsPlaying && !IsPaused)
        {
            CurrentTime = (DateTime.Now - _playbackStartTime).TotalSeconds;

            if (_midiData != null && CurrentTime >= _midiData.TotalSeconds + 1)
            {
                IsPlaying = false;
                IsPaused = false;
                StatusText = "Finished";
            }
        }

        // Update key states
        var allNotes = GetAllVisibleNotes();
        Array.Clear(_keyActive);
        foreach (var note in allNotes)
        {
            int idx = note.Note - PianoFlowVisual.FirstNote;
            if (idx >= 0 && idx < _keyActive.Length)
            {
                if (note.OnTime <= CurrentTime && note.OffTime > CurrentTime)
                    _keyActive[idx] = true;
            }
        }

        // Update particles
        _particles.Update(1.0 / _fps);

        // Render via WPF DrawingVisual (GPU accelerated)
        _pianoVisual.Render(allNotes, CurrentTime, Falling, _noteSpeed, _keyActive);

        RenderFrameReady?.Invoke();

        // Export frame if exporting
        if (IsExporting)
        {
            // For export, render to a RenderTargetBitmap
            var rtb = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(_pianoVisual);
            _exporter.WriteFrame(rtb);
            _exportFrame++;
            if (_exportFrame >= _exportTotalFrames)
            {
                _exporter.FinishExport(_exportTotalFrames);
                IsExporting = false;
                StatusText = "Export complete!";
            }
        }
    }

    private IReadOnlyList<MidiNote> GetAllVisibleNotes()
    {
        if (_midiData == null) return _liveNotes;
        if (_liveNotes.Count == 0) return _midiData.Notes;

        var merged = new List<MidiNote>(_midiData.Notes);
        merged.AddRange(_liveNotes);
        merged.Sort((a, b) => a.OnTime.CompareTo(b.OnTime));
        return merged;
    }

    // --- Live MIDI Input ---
    private void OnLiveNoteOn(int note, int velocity, int channel)
    {
        var midiNote = new MidiNote
        {
            Note = note,
            Velocity = velocity,
            Channel = channel,
            OnTime = CurrentTime,
            OffTime = CurrentTime + 0.5,
            IsLive = true,
        };
        _liveNotes.Add(midiNote);

        int idx = note - PianoFlowVisual.FirstNote;
        if (idx >= 0 && idx < _keyActive.Length)
        {
            _keyActive[idx] = true;
            _keyFadeTime[idx] = CurrentTime;
        }

        // Record hit for flash effect
        _pianoVisual.RecordHit(note);

        // Spawn particles
        var color = PianoFlowVisual.ChannelColors[channel % 16];
        double keyX = _pianoVisual.GetKeyCenterX(note);
        _particles.Emit(keyX, _pianoVisual.PianoTopY, color);
    }

    private void OnLiveNoteOff(int note, int channel)
    {
        for (int i = _liveNotes.Count - 1; i >= 0; i--)
        {
            if (_liveNotes[i].Note == note && _liveNotes[i].Channel == channel && _liveNotes[i].IsLive)
            {
                var n = _liveNotes[i];
                n.OffTime = CurrentTime;
                _liveNotes[i] = n;
                break;
            }
        }

        int idx = note - PianoFlowVisual.FirstNote;
        if (idx >= 0 && idx < _keyActive.Length)
            _keyActive[idx] = false;
    }

    // --- Video Export ---
    public void StartExport(string outputPath)
    {
        if (_midiData == null)
        {
            ShowMessage?.Invoke("No MIDI file loaded.");
            return;
        }

        int fps = 30;
        _exportTotalFrames = (int)(_midiData.TotalSeconds * fps);
        _exportFrame = 0;

        _exporter.StartExport(outputPath, _width, _height, fps, 18);
        if (_exporter.IsExporting)
        {
            IsExporting = true;
            CurrentTime = 0;
            IsPlaying = true;
            IsPaused = false;
            _playbackStartTime = DateTime.Now;
            StatusText = $"Exporting... ({_exportTotalFrames} frames)";
        }
    }

    public void CancelExport()
    {
        _exporter.Cancel();
        IsExporting = false;
        StatusText = "Export cancelled";
    }

    public void Dispose()
    {
        _midiDevice.Dispose();
        _exporter.Dispose();
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
