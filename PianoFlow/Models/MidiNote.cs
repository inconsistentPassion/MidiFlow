namespace PianoFlow.Models;

/// <summary>
/// Represents a single MIDI note with timing information.
/// </summary>
public struct MidiNote
{
    /// <summary>MIDI note number (0-127). A0=21, C8=108.</summary>
    public int Note;

    /// <summary>Velocity (0-127).</summary>
    public int Velocity;

    /// <summary>MIDI channel (0-15).</summary>
    public int Channel;

    /// <summary>Time in seconds when note starts.</summary>
    public double OnTime;

    /// <summary>Time in seconds when note ends.</summary>
    public double OffTime;

    /// <summary>Duration in seconds.</summary>
    public double Duration => OffTime - OnTime;

    /// <summary>Track index (for multi-track files).</summary>
    public int Track;

    /// <summary>Whether this note came from a live MIDI device.</summary>
    public bool IsLive;

    public override string ToString() =>
        $"Note={Note} Ch={Channel} Vel={Velocity} On={OnTime:F3}s Off={OffTime:F3}s";
}
