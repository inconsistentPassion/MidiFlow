using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PianoFlow.Audio;
using PianoFlow.Export;
using PianoFlow.Models;
using PianoFlow.Rendering;

namespace PianoFlow.ViewModels;

/// <summary>
/// Main ViewModel: orchestrates MIDI loading, playback, audio synthesis, rendering, and export.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // --- State ---
    private MidiFileData? _midiData;
    private readonly MidiDeviceInput _midiDevice = new();
    private readonly PianoFlowVisual _pianoVisual;
    private readonly ParticleSystem _particles = new();
    private readonly VideoExporter _exporter = new();
    private readonly AudioEngine _audio = new();

    // --- Playback ---
    private double _currentTime;
    private bool _isPlaying;
    private bool _isPaused;
    private double _noteSpeed = 300;
    private bool _falling = true;

    // --- Piano state ---
    private bool[] _keyActive = new bool[PianoFlowVisual.NoteCount];

    // --- Live notes ---
    private readonly List<MidiNote> _liveNotes = new();
    private DateTime _playbackStartTime;

    // --- Pre-allocated merged note list (avoids per-frame allocation) ---
    private List<MidiNote> _mergedNotes = new();
    private bool _mergedDirty = true;

    // --- Audio sync state ---
    private HashSet<(int ch, int note)> _activeAudioNotes = new();

    // --- Export ---
    private bool _isExporting;
    private int _exportFrame;
    private int _exportTotalFrames;
    private RenderTargetBitmap? _exportRtb;  // reused across frames

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
    public AudioEngine Audio => _audio;

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

    // --- SoundFont Loading ---
    public bool LoadSoundFont(string sf2Path)
    {
        try
        {
            _audio.Initialize(sf2Path);
            StatusText = $"SoundFont loaded: {System.IO.Path.GetFileName(sf2Path)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"SoundFont error: {ex.Message}";
            ShowMessage?.Invoke($"Failed to load SoundFont:\n{ex.Message}");
            return false;
        }
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
            _liveNotes.Clear();
            _mergedDirty = true;
            _activeAudioNotes.Clear();
            _audio.AllNotesOff();

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
        _audio.AllNotesOff();
        _activeAudioNotes.Clear();
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
        _liveNotes.Clear();
        _mergedDirty = true;
        _particles.Clear();
        _activeAudioNotes.Clear();
        _audio.AllNotesOff();
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

        double prevTime = _currentTime;

        // Update playback time
        if (IsPlaying && !IsPaused)
        {
            CurrentTime = (DateTime.Now - _playbackStartTime).TotalSeconds;

            if (_midiData != null && CurrentTime >= _midiData.TotalSeconds + 1)
            {
                IsPlaying = false;
                IsPaused = false;
                _audio.AllNotesOff();
                _activeAudioNotes.Clear();
                StatusText = "Finished";
            }
        }

        // --- Audio synthesis: schedule note events between prevTime and currentTime ---
        if (_audio.IsReady && _midiData != null && IsPlaying && !IsPaused)
        {
            SyncAudio(prevTime, _currentTime);
        }

        // Get merged notes (reuses list, avoids allocation)
        var allNotes = GetMergedNotes();

        // Update key states
        UpdateKeyStates(allNotes);

        // Update particles
        _particles.Update(1.0 / _fps);

        // Render via WPF DrawingVisual (GPU accelerated)
        _pianoVisual.Render(allNotes, _currentTime, Falling, _noteSpeed, _keyActive, _particles);

        RenderFrameReady?.Invoke();

        // Export frame if exporting
        if (IsExporting)
        {
            ExportFrame();
        }
    }

    /// <summary>
    /// Sync audio: fire NoteOn/NoteOff for notes between prevTime and currentTime.
    /// Uses binary search to find notes in the time range — robust against frame skips and seeks.
    /// </summary>
    private void SyncAudio(double prevTime, double currentTime)
    {
        if (_midiData == null) return;
        var notes = _midiData.Notes;
        int count = notes.Count;
        if (count == 0) return;

        double windowStart = prevTime;
        double windowEnd = currentTime;

        // Binary search for first note with OnTime >= windowStart
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (notes[mid].OnTime < windowStart) lo = mid + 1;
            else hi = mid;
        }

        // Play all notes that start in [windowStart, windowEnd]
        for (int i = lo; i < count; i++)
        {
            var note = notes[i];
            if (note.OnTime > windowEnd) break;  // sorted by OnTime, so we're done
            if (note.OnTime >= windowStart && note.OnTime <= windowEnd)
            {
                _audio.NoteOn(note.Channel, note.Note, note.Velocity);
                _activeAudioNotes.Add((note.Channel, note.Note));
            }
        }

        // Turn off notes that have ended
        if (_activeAudioNotes.Count > 0)
        {
            var toRemove = new List<(int ch, int note)>();
            foreach (var (ch, n) in _activeAudioNotes)
            {
                // Check if this note's OffTime has passed
                // Scan from the start position backward/forward to find matching note
                bool shouldOff = false;
                for (int i = Math.Max(0, lo - 100); i < Math.Min(count, lo + 200); i++)
                {
                    if (notes[i].Channel == ch && notes[i].Note == n && notes[i].OffTime <= currentTime)
                    {
                        shouldOff = true;
                        break;
                    }
                }
                if (shouldOff)
                {
                    _audio.NoteOff(ch, n);
                    toRemove.Add((ch, n));
                }
            }
            foreach (var key in toRemove)
                _activeAudioNotes.Remove(key);
        }
    }

    /// <summary>Get merged notes, reusing the pre-allocated list.</summary>
    private IReadOnlyList<MidiNote> GetMergedNotes()
    {
        if (_midiData == null) return _liveNotes;
        if (_liveNotes.Count == 0) return _midiData.Notes;

        if (_mergedDirty)
        {
            _mergedNotes.Clear();
            _mergedNotes.AddRange(_midiData.Notes);
            _mergedNotes.AddRange(_liveNotes);
            _mergedNotes.Sort((a, b) => a.OnTime.CompareTo(b.OnTime));
            _mergedDirty = false;
        }
        else
        {
            // Just append new live notes and re-sort if needed
            // For simplicity, rebuild when live notes change
            _mergedNotes.Clear();
            _mergedNotes.AddRange(_midiData.Notes);
            _mergedNotes.AddRange(_liveNotes);
            _mergedNotes.Sort((a, b) => a.OnTime.CompareTo(b.OnTime));
        }

        return _mergedNotes;
    }

    /// <summary>Update key active states - optimized to avoid per-note branch.</summary>
    private void UpdateKeyStates(IReadOnlyList<MidiNote> allNotes)
    {
        Array.Clear(_keyActive);
        int count = allNotes.Count;
        for (int i = 0; i < count; i++)
        {
            var note = allNotes[i];
            if (note.OnTime <= _currentTime && note.OffTime > _currentTime)
            {
                int idx = note.Note - PianoFlowVisual.FirstNote;
                if ((uint)idx < PianoFlowVisual.NoteCount)
                    _keyActive[idx] = true;
            }
        }
    }

    private void ExportFrame()
    {
        // Reuse the same RenderTargetBitmap across frames
        if (_exportRtb == null || _exportRtb.PixelWidth != _width || _exportRtb.PixelHeight != _height)
        {
            _exportRtb?.Freeze(); // freeze old one
            _exportRtb = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
        }

        _exportRtb.Render(_pianoVisual);
        _exporter.WriteFrame(_exportRtb);
        _exportFrame++;

        if (_exportFrame >= _exportTotalFrames)
        {
            _exporter.FinishExport(_exportTotalFrames);
            _exportRtb = null;
            IsExporting = false;
            StatusText = "Export complete!";
        }
    }

    // --- Live MIDI Input ---
    private void OnLiveNoteOn(int note, int velocity, int channel)
    {
        var midiNote = new MidiNote
        {
            Note = note,
            Velocity = velocity,
            Channel = channel,
            OnTime = _currentTime,
            OffTime = _currentTime + 0.5,
            IsLive = true,
        };
        _liveNotes.Add(midiNote);
        _mergedDirty = true;

        int idx = note - PianoFlowVisual.FirstNote;
        if ((uint)idx < PianoFlowVisual.NoteCount)
            _keyActive[idx] = true;

        // Play sound through synth
        if (_audio.IsReady)
            _audio.NoteOn(channel, note, velocity);

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
                n.OffTime = _currentTime;
                _liveNotes[i] = n;
                break;
            }
        }
        _mergedDirty = true;

        int idx = note - PianoFlowVisual.FirstNote;
        if ((uint)idx < PianoFlowVisual.NoteCount)
            _keyActive[idx] = false;

        // Stop sound
        if (_audio.IsReady)
            _audio.NoteOff(channel, note);
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
        _exportRtb = null;
        IsExporting = false;
        StatusText = "Export cancelled";
    }

    public void Dispose()
    {
        _midiDevice.Dispose();
        _exporter.Dispose();
        _audio.Dispose();
    }

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
