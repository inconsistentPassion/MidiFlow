using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private readonly NoteRenderer _noteRenderer = new();
    private readonly ParticleSystem _particles = new();
    private readonly VideoExporter _exporter = new();

    // --- Playback ---
    private double _currentTime;
    private bool _isPlaying;
    private bool _isPaused;
    private double _noteSpeed = 300; // pixels per second
    private bool _falling = true; // true = falling (top→bottom), false = rising

    // --- Piano state ---
    private bool[] _keyActive = new bool[NoteRenderer.NoteCount];
    private double[] _keyFadeTime = new double[NoteRenderer.NoteCount];

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
    public WriteableBitmap? Bitmap => _noteRenderer.Bitmap;
    public NoteRenderer NoteRenderer => _noteRenderer;

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

    // --- Constructor ---
    public MainViewModel()
    {
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

            // Reset piano state
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
        if (!IsPlaying)
        {
            Play();
        }
        else if (IsPaused)
        {
            Play();
        }
        else
        {
            Pause();
        }
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

            // Check if playback ended
            if (_midiData != null && CurrentTime >= _midiData.TotalSeconds + 1)
            {
                IsPlaying = false;
                IsPaused = false;
                StatusText = "Finished";
            }
        }

        // Update live note key states
        var allNotes = GetAllVisibleNotes();

        // Reset key states
        Array.Clear(_keyActive);
        foreach (var note in allNotes)
        {
            int idx = note.Note - NoteRenderer.FirstNote;
            if (idx >= 0 && idx < _keyActive.Length)
            {
                if (note.OnTime <= CurrentTime && note.OffTime > CurrentTime)
                {
                    _keyActive[idx] = true;
                }
            }
        }

        // Update particles
        _particles.Update(1.0 / _fps);

        // Render
        _noteRenderer.Render(allNotes, CurrentTime, Falling, _noteSpeed,
            null, _keyActive, _keyFadeTime);

        // Render particles on top (unsafe block needed)
        var bitmap = _noteRenderer.Bitmap;
        if (bitmap != null)
        {
            bitmap.Lock();
            unsafe
            {
                var buffer = (uint*)bitmap.BackBuffer;
                int stride = bitmap.BackBufferStride / 4;
                _particles.Render(buffer, stride, _width, _height);
            }
            bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, _width, _height));
            bitmap.Unlock();
        }

        RenderFrameReady?.Invoke();

        // Export frame if exporting
        if (IsExporting && bitmap != null)
        {
            _exporter.WriteFrame(bitmap);
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

        // Merge file notes with live notes
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
            OffTime = CurrentTime + 0.5, // default duration, will be updated on NoteOff
            IsLive = true,
        };
        _liveNotes.Add(midiNote);

        // Activate key
        int idx = note - NoteRenderer.FirstNote;
        if (idx >= 0 && idx < _keyActive.Length)
        {
            _keyActive[idx] = true;
            _keyFadeTime[idx] = CurrentTime;
        }

        // Spawn particles
        var color = GetChannelColor(channel);
        double keyX = GetKeyXPosition(note);
        _particles.Emit(keyX, _height - _pianoHeight, color);
    }

    private void OnLiveNoteOff(int note, int channel)
    {
        // Find the most recent live note with this note/channel and set its off time
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

        int idx = note - NoteRenderer.FirstNote;
        if (idx >= 0 && idx < _keyActive.Length)
        {
            _keyActive[idx] = false;
        }
    }

    private uint GetChannelColor(int channel)
    {
        uint[] colors = {
            0xFF4FC3F7, 0xFFFF7043, 0xFF66BB6A, 0xFFFFCA28,
            0xFFAB47BC, 0xFFEF5350, 0xFF26C6DA, 0xFF8D6E63,
            0xFF78909C, 0xFFD4E157, 0xFFFF8A65, 0xFFAED581,
            0xFF7986CB, 0xFFF06292, 0xFF4DB6AC, 0xFFFFD54F,
        };
        return colors[channel % 16];
    }

    private double GetKeyXPosition(int note)
    {
        // Approximate: count white keys before this note
        bool[] isBlack = { false, true, false, true, false, false, true, false, true, false, true, false };
        int whiteCount = 0;
        for (int n = NoteRenderer.FirstNote; n < note; n++)
        {
            if (!isBlack[n % 12]) whiteCount++;
        }
        int totalWhite = 0;
        for (int n = NoteRenderer.FirstNote; n <= NoteRenderer.LastNote; n++)
        {
            if (!isBlack[n % 12]) totalWhite++;
        }
        double whiteWidth = _width / (double)totalWhite;
        return whiteCount * whiteWidth + whiteWidth / 2;
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

    // --- Cleanup ---
    public void Dispose()
    {
        _midiDevice.Dispose();
        _exporter.Dispose();
    }

    // --- Helpers ---
    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
