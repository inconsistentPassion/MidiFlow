using System.Windows;
using System.Windows.Media;

namespace PianoFlow.Rendering;

/// <summary>
/// GPU-accelerated piano + note renderer using WPF DrawingVisual.
/// Performance-optimized: pre-cached brushes, precomputed key positions, zero per-frame allocations.
/// </summary>
public class PianoFlowVisual : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    // Key range: A0 (21) to C8 (108)
    public const int FirstNote = 21;
    public const int LastNote = 108;
    public const int NoteCount = LastNote - FirstNote + 1;

    private static readonly bool[] IsBlackKey =
        Enumerable.Range(0, 128).Select(n =>
        {
            int[] blacks = { 1, 3, 6, 8, 10 };
            return blacks.Contains(n % 12);
        }).ToArray();

    // 16 channel colors
    public static readonly Color[] ChannelColors;
    private static readonly SolidColorBrush[] ChannelBrushes;

    // --- Pre-cached note brushes: [channel, alphaBucket] ---
    // Alpha quantized to 16 levels
    private const int AlphaBuckets = 16;
    private static readonly SolidColorBrush[][] NoteBrushCache;
    private static readonly SolidColorBrush[] HitFlashWhite;
    private static readonly SolidColorBrush[] HitFlashBlack;

    static PianoFlowVisual()
    {
        ChannelColors = new Color[]
        {
            Color.FromRgb(79, 195, 247),   // 0  light blue
            Color.FromRgb(255, 112, 67),   // 1  deep orange
            Color.FromRgb(102, 187, 106),  // 2  green
            Color.FromRgb(255, 202, 40),   // 3  amber
            Color.FromRgb(171, 71, 188),   // 4  purple
            Color.FromRgb(239, 83, 80),    // 5  red
            Color.FromRgb(38, 198, 218),   // 6  cyan
            Color.FromRgb(141, 110, 99),   // 7  brown
            Color.FromRgb(120, 144, 156),  // 8  blue grey
            Color.FromRgb(212, 225, 87),   // 9  lime
            Color.FromRgb(255, 138, 101),  // 10 deep orange light
            Color.FromRgb(174, 213, 129),  // 11 light green
            Color.FromRgb(121, 134, 203),  // 12 indigo
            Color.FromRgb(240, 98, 146),   // 13 pink
            Color.FromRgb(77, 182, 172),   // 14 teal
            Color.FromRgb(255, 213, 79),   // 15 yellow
        };
        ChannelBrushes = ChannelColors.Select(c => new SolidColorBrush(c)).ToArray();
        foreach (var b in ChannelBrushes) b.Freeze();

        // Pre-build alpha-bucketed brushes for each channel
        NoteBrushCache = new SolidColorBrush[16][];
        for (int ch = 0; ch < 16; ch++)
        {
            NoteBrushCache[ch] = new SolidColorBrush[AlphaBuckets];
            var baseColor = ChannelColors[ch];
            for (int a = 0; a < AlphaBuckets; a++)
            {
                int alphaValue = (int)(a * 255.0 / (AlphaBuckets - 1));
                alphaValue = Math.Max(80, alphaValue);
                var brush = new SolidColorBrush(
                    Color.FromArgb((byte)alphaValue, baseColor.R, baseColor.G, baseColor.B));
                brush.Freeze();
                NoteBrushCache[ch][a] = brush;
            }
        }

        // Pre-build hit flash brushes (20 fade levels)
        HitFlashWhite = new SolidColorBrush[20];
        HitFlashBlack = new SolidColorBrush[20];
        for (int i = 0; i < 20; i++)
        {
            double fade = i / 19.0;
            HitFlashWhite[i] = MakeBrush((byte)(fade * 180), 255, 136, 0);
            HitFlashBlack[i] = MakeBrush((byte)(fade * 200), 255, 102, 0);
        }
    }

    private static SolidColorBrush GetCachedNoteBrush(int channel, double alpha01)
    {
        int bucket = (int)(Math.Clamp(alpha01, 0, 1) * (AlphaBuckets - 1) + 0.5);
        return NoteBrushCache[channel % 16][bucket];
    }

    // --- Precomputed key positions (O(1) lookup) ---
    private int[] _keyX = new int[NoteCount];       // X position for each note
    private int[] _blackKeyX = new int[NoteCount];   // X position for black keys (precomputed)
    private int _whiteKeyWidth;
    private int _blackKeyWidth;
    private int _pianoY;
    private int _pianoHeight;
    private int _blackKeyVisualHeight;

    public int PianoHeight { get; set; } = 120;

    // --- Hit flash tracking (per-note) ---
    private double[] _hitTime = new double[NoteCount]; // time of last hit per note index

    private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public PianoFlowVisual()
    {
        AddVisualChild(_visual);
        Array.Fill(_hitTime, -999.0);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    /// <summary>Record a key hit for flash effect.</summary>
    public void RecordHit(int note)
    {
        int idx = note - FirstNote;
        if (idx >= 0 && idx < NoteCount)
        {
            _hitTime[idx] = 0; // will be set to currentTime on next render
            _hitCount++;
        }
    }

    /// <summary>Update key layout for given dimensions.</summary>
    public void UpdateLayout(int width, int height)
    {
        _pianoHeight = PianoHeight;
        _pianoY = height - _pianoHeight;
        _blackKeyVisualHeight = (int)(_pianoHeight * 0.65);
        ComputeKeyPositions(width);
    }

    private void ComputeKeyPositions(int width)
    {
        int whiteKeyCount = 0;
        for (int note = FirstNote; note <= LastNote; note++)
            if (!IsBlackKey[note]) whiteKeyCount++;

        _whiteKeyWidth = Math.Max(1, width / whiteKeyCount);
        _blackKeyWidth = Math.Max(1, (int)(_whiteKeyWidth * 0.6));

        // Precompute X for ALL notes (both white and black) - O(1) lookup later
        int wi = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            int idx = note - FirstNote;
            if (!IsBlackKey[note])
            {
                _keyX[idx] = wi * _whiteKeyWidth;
                wi++;
            }
            else
            {
                // Black key is centered between adjacent white keys
                int x = wi * _whiteKeyWidth - _blackKeyWidth / 2;
                _keyX[idx] = x;
            }
        }
    }

    /// <summary>Main render call. Draws notes + particles + piano.</summary>
    public void Render(IReadOnlyList<Models.MidiNote> notes, double currentTime,
        bool falling, double noteSpeed, bool[]? keyActive, ParticleSystem? particles = null)
    {
        int w = (int)ActualWidth;
        int h = (int)ActualHeight;
        if (w <= 0 || h <= 0) return;

        _pianoY = h - _pianoHeight;

        using var dc = _visual.RenderOpen();

        // Background
        dc.DrawRectangle(BrushCache.Background, null, new Rect(0, 0, w, h));

        // Draw notes
        DrawNotes(dc, notes, currentTime, falling, noteSpeed, w, h);

        // Draw particles (between notes and piano)
        particles?.Draw(dc);

        // Draw piano
        DrawPiano(dc, keyActive, currentTime, w, h);

        // Draw separator
        dc.DrawRectangle(BrushCache.Separator, null, new Rect(0, _pianoY - 2, w, 2));
    }

    private void DrawNotes(DrawingContext dc, IReadOnlyList<Models.MidiNote> notes,
        double currentTime, bool falling, double noteSpeed, int w, int h)
    {
        double visibleDuration = _pianoY / noteSpeed;

        for (int i = 0; i < notes.Count; i++)
        {
            var note = notes[i];

            // Visibility culling
            if (falling)
            {
                if (note.OffTime < currentTime - 0.1 || note.OnTime > currentTime + visibleDuration)
                    continue;
            }
            else
            {
                if (note.OnTime > currentTime + 0.1 || note.OffTime < currentTime - visibleDuration)
                    continue;
            }

            int noteIndex = note.Note - FirstNote;
            if ((uint)noteIndex >= NoteCount) continue;

            // O(1) key position lookup
            int keyX = _keyX[noteIndex];
            int keyW = IsBlackKey[note.Note] ? _blackKeyWidth : _whiteKeyWidth;

            int yTop, yBottom;
            if (falling)
            {
                yTop = _pianoY - (int)((note.OffTime - currentTime) * noteSpeed);
                yBottom = _pianoY - (int)((note.OnTime - currentTime) * noteSpeed);
            }
            else
            {
                yTop = _pianoY - (int)((note.OnTime - currentTime) * noteSpeed);
                yBottom = _pianoY - (int)((note.OffTime - currentTime) * noteSpeed);
            }

            yTop = Math.Max(0, yTop);
            yBottom = Math.Min(_pianoY, yBottom);
            if (yTop >= yBottom) continue;

            // Alpha from distance - use cached brush (zero allocation)
            double distFromHit = Math.Abs(
                (falling ? note.OnTime : note.OffTime) - currentTime);
            double alpha = Math.Max(80.0 / 255.0, 1.0 - distFromHit * 100.0 / 255.0);

            var brush = GetCachedNoteBrush(note.Channel, alpha);
            dc.DrawRectangle(brush, null, new Rect(keyX, yTop, keyW, yBottom - yTop));
        }
    }

    private void DrawPiano(DrawingContext dc, bool[]? keyActive, double currentTime, int w, int h)
    {
        for (int note = FirstNote; note <= LastNote; note++)
        {
            int ni = note - FirstNote;
            bool active = keyActive != null && ni < keyActive.Length && keyActive[ni];
            bool black = IsBlackKey[note];
            int x = _keyX[ni];

            if (black)
            {
                var brush = active ? BrushCache.BlackKeyActive : BrushCache.BlackKey;
                dc.DrawRectangle(brush, null, new Rect(x, _pianoY, _blackKeyWidth, _blackKeyVisualHeight));

                // Hit flash
                if (active)
                {
                    double elapsed = currentTime - _hitTime[ni];
                    if (elapsed >= 0 && elapsed < 0.3)
                    {
                        int fi = (int)((1.0 - elapsed / 0.3) * 19 + 0.5);
                        dc.DrawRectangle(HitFlashBlack[fi], null,
                            new Rect(x, _pianoY, _blackKeyWidth, _blackKeyVisualHeight));
                    }
                }
            }
            else
            {
                var brush = active ? BrushCache.WhiteKeyActive : BrushCache.WhiteKey;
                dc.DrawRectangle(brush, null, new Rect(x, _pianoY, _whiteKeyWidth - 1, _pianoHeight));

                // Hit flash
                if (active)
                {
                    double elapsed = currentTime - _hitTime[ni];
                    if (elapsed >= 0 && elapsed < 0.3)
                    {
                        int fi = (int)((1.0 - elapsed / 0.3) * 19 + 0.5);
                        dc.DrawRectangle(HitFlashWhite[fi], null,
                            new Rect(x, _pianoY, _whiteKeyWidth - 1, _pianoHeight));
                    }
                }
            }
        }
    }

    /// <summary>Get the X position center of a key (for particle spawn).</summary>
    public double GetKeyCenterX(int note)
    {
        int ni = note - FirstNote;
        if ((uint)ni >= NoteCount) return 0;
        int w = IsBlackKey[note] ? _blackKeyWidth : _whiteKeyWidth;
        return _keyX[ni] + w / 2.0;
    }

    /// <summary>Get the Y position of the piano top.</summary>
    public int PianoTopY => _pianoY;
}

/// <summary>Pre-frozen brushes for common colors.</summary>
public static class BrushCache
{
    public static readonly SolidColorBrush Background;
    public static readonly SolidColorBrush Separator;
    public static readonly SolidColorBrush WhiteKey;
    public static readonly SolidColorBrush WhiteKeyActive;
    public static readonly SolidColorBrush BlackKey;
    public static readonly SolidColorBrush BlackKeyActive;

    static BrushCache()
    {
        Background = Make(10, 14, 26);
        Separator = Make(51, 51, 102);
        WhiteKey = Make(224, 224, 224);
        WhiteKeyActive = Make(255, 255, 255);
        BlackKey = Make(26, 26, 46);
        BlackKeyActive = Make(58, 58, 94);
    }

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
