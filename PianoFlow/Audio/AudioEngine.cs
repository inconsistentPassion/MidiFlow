using MeltySynth;
using NAudio.Wave;

namespace PianoFlow.Audio;

/// <summary>
/// Audio synthesis engine using MeltySynth (SoundFont-based).
/// Renders MIDI notes to audio via a software synthesizer and outputs through NAudio.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private Synthesizer? _synth;
    private WaveOutEvent? _waveOut;
    private SynthWaveProvider? _waveProvider;
    private bool _disposed;

    private string? _soundFontPath;
    private int _sampleRate = 44100;
    private float _volume = 0.7f;

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
    /// Call this once at startup or when changing sound fonts.
    /// </summary>
    public void Initialize(string soundFontPath, int sampleRate = 44100)
    {
        Dispose();

        _sampleRate = sampleRate;
        _soundFontPath = soundFontPath;

        _synth = new Synthesizer(soundFontPath, _sampleRate);

        _waveProvider = new SynthWaveProvider(_synth, _sampleRate);

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 50,  // ~50ms latency - good balance
            NumberOfBuffers = 3,
            Volume = _volume
        };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();
    }

    /// <summary>Send a Note-On event to the synthesizer.</summary>
    public void NoteOn(int channel, int note, int velocity)
    {
        _synth?.NoteOn(channel, note, velocity);
    }

    /// <summary>Send a Note-Off event to the synthesizer.</summary>
    public void NoteOff(int channel, int note)
    {
        _synth?.NoteOff(channel, note);
    }

    /// <summary>Turn off all notes on all channels.</summary>
    public void AllNotesOff()
    {
        if (_synth == null) return;
        for (int ch = 0; ch < 16; ch++)
            _synth.AllNotesOff(ch);
    }

    /// <summary>Send a program change (instrument) on a channel.</summary>
    public void ProgramChange(int channel, int program)
    {
        _synth?.ProgramChange(channel, program);
    }

    /// <summary>Send a control change (e.g. sustain pedal, volume).</summary>
    public void ControlChange(int channel, int controller, int value)
    {
        _synth?.ControlChange(channel, controller, value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        _waveProvider = null;
        _synth = null;
    }
}

/// <summary>
/// NAudio IWaveProvider that pulls audio samples from a MeltySynth Synthesizer.
/// Runs on a background thread, calling synth.RenderInterleaved() each buffer.
/// </summary>
internal sealed class SynthWaveProvider : IWaveProvider
{
    private readonly Synthesizer _synth;
    private readonly WaveFormat _waveFormat;
    private readonly int _bufferSamples;
    private readonly float[] _renderBuffer;  // reused every Read call
    private readonly byte[] _byteBuffer;

    public SynthWaveProvider(Synthesizer synth, int sampleRate)
    {
        _synth = synth;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _bufferSamples = sampleRate / 60;  // ~16ms chunks
        _renderBuffer = new float[_bufferSamples * 2];  // stereo
        _byteBuffer = new byte[_bufferSamples * 2 * sizeof(float)];
    }

    public WaveFormat WaveFormat => _waveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int samplesNeeded = count / (sizeof(float) * 2);  // stereo float
        int samplesToRender = Math.Min(samplesNeeded, _bufferSamples);

        // Render directly into reusable buffer
        _synth.RenderInterleaved(_renderBuffer);

        // Copy to output
        int bytesToCopy = samplesToRender * sizeof(float) * 2;
        Buffer.BlockCopy(_renderBuffer, 0, buffer, offset, bytesToCopy);

        // Zero-fill if we rendered fewer samples than requested
        if (bytesToCopy < count)
            Array.Clear(buffer, offset + bytesToCopy, count - bytesToCopy);

        return count;
    }
}
