using MeltySynth;
using NAudio.Wave;

namespace PianoFlow.Audio;

/// <summary>
/// Audio synthesis engine using MeltySynth (SoundFont-based).
/// Thread-safe: uses lock since MeltySynth's Synthesizer is not thread-safe,
/// and NAudio calls Read() from a background thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private Synthesizer? _synth;
    private WaveOutEvent? _waveOut;
    private SynthWaveProvider? _waveProvider;
    private readonly object _synthLock = new();
    private bool _disposed;

    private string? _soundFontPath;
    private int _sampleRate = 44100;
    private float _volume = 1.0f;

    public bool IsReady => _synth != null && _waveOut != null;
    public string? SoundFontPath => _soundFontPath;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_waveOut != null)
                _waveOut.Volume = _volume;
        }
    }

    /// <summary>
    /// Load a SoundFont and start the audio output pipeline.
    /// </summary>
    public void Initialize(string soundFontPath, int sampleRate = 44100)
    {
        Dispose();

        _sampleRate = sampleRate;
        _soundFontPath = soundFontPath;

        lock (_synthLock)
        {
            _synth = new Synthesizer(soundFontPath, _sampleRate);
        }

        _waveProvider = new SynthWaveProvider(this, _sampleRate);

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,  // 100ms latency - stable, no underruns
            NumberOfBuffers = 4,   // extra buffer to prevent gaps
            Volume = _volume
        };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();
    }

    /// <summary>Send a Note-On event to the synthesizer (thread-safe).</summary>
    public void NoteOn(int channel, int note, int velocity)
    {
        lock (_synthLock)
        {
            _synth?.NoteOn(channel, note, velocity);
        }
    }

    /// <summary>Send a Note-Off event to the synthesizer (thread-safe).</summary>
    public void NoteOff(int channel, int note)
    {
        lock (_synthLock)
        {
            _synth?.NoteOff(channel, note);
        }
    }

    /// <summary>Turn off all notes on all channels (thread-safe).</summary>
    public void AllNotesOff()
    {
        lock (_synthLock)
        {
            if (_synth == null) return;
            for (int ch = 0; ch < 16; ch++)
                _synth.ProcessMidiMessage(ch, 0xB0, 123, 0); // CC 123 = All Notes Off
        }
    }

    /// <summary>Render audio samples into a buffer (called by SynthWaveProvider on audio thread).</summary>
    internal void RenderSamples(float[] left, float[] right)
    {
        lock (_synthLock)
        {
            _synth?.Render(left, right);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _waveProvider = null;

        lock (_synthLock)
        {
            _synth = null;
        }
    }
}

/// <summary>
/// NAudio IWaveProvider that pulls audio samples from a MeltySynth Synthesizer.
/// Uses unsafe pointer operations for fast interleaving (no per-sample BlockCopy).
/// </summary>
internal sealed unsafe class SynthWaveProvider : IWaveProvider
{
    private readonly AudioEngine _engine;
    private readonly WaveFormat _waveFormat;
    private readonly int _bufferSamples;
    private readonly float[] _left;
    private readonly float[] _right;

    public SynthWaveProvider(AudioEngine engine, int sampleRate)
    {
        _engine = engine;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _bufferSamples = sampleRate / 50;  // 20ms chunks (bigger = fewer calls, more stable)
        _left = new float[_bufferSamples];
        _right = new float[_bufferSamples];
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesNeeded = count / 8;  // stereo float = 8 bytes per sample pair
        int samplesToRender = Math.Min(samplesNeeded, _bufferSamples);
        if (samplesToRender <= 0) return count;

        // Render via engine (thread-safe)
        _engine.RenderSamples(_left, _right);

        // Fast interleave using unsafe pointers
        fixed (float* pL = _left, pR = _right)
        fixed (byte* pBuf = buffer)
        {
            float* dst = (float*)(pBuf + offset);
            float* l = pL;
            float* r = pR;
            float* end = l + samplesToRender;

            while (l < end)
            {
                *dst++ = *l++;
                *dst++ = *r++;
            }
        }

        // Zero-fill remainder if needed
        int bytesWritten = samplesToRender * 8;
        if (bytesWritten < count)
            Array.Clear(buffer, offset + bytesWritten, count - bytesWritten);

        return count;
    }
}
