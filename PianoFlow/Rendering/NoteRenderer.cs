using System.Windows;
using System.Windows.Media;

namespace PianoFlow.Rendering;

/// <summary>
/// GPU-accelerated piano + note renderer using WPF DrawingVisual.
/// Replaces WriteableBitmap approach for 60fps performance.
/// </summary>
public class PianoFlowVisual : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    private List<VisualHit>? _keyHits; // for hit flash effect
    private double _lastHitTime;

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
    private static readonly SolidColorBrush[] ChannelBrushes;
    public static readonly Color[] ChannelColors;

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
    }

    // Key layout cache
    private int[] _whiteKeyX = Array.Empty<int>();
    private int _whiteKeyWidth;
    private int _blackKeyWidth;
    private int _pianoY;
    private int _pianoHeight;

    public int PianoHeight { get; set; } = 120;

    public PianoFlowVisual()
    {
        AddVisualChild(_visual);
        _keyHits = new List<VisualHit>();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    /// <summary>Hit flash effect data.</summary>
    public struct VisualHit
    {
        public int NoteIndex;
        public double Time;
    }

    /// <summary>Record a key hit for flash effect.</summary>
    public void RecordHit(int note)
    {
        _keyHits ??= new List<VisualHit>();
        _keyHits.Add(new VisualHit { NoteIndex = note - FirstNote, Time = 0 });
        _lastHitTime = 0;
    }

    /// <summary>Update key layout for given dimensions.</summary>
    public void UpdateLayout(int width, int height)
    {
        _pianoHeight = PianoHeight;
        _pianoY = height - _pianoHeight;
        ComputeKeyPositions(width);
    }

    private void ComputeKeyPositions(int width)
    {
        int whiteKeyCount = 0;
        for (int note = FirstNote; note <= LastNote; note++)
            if (!IsBlackKey[note]) whiteKeyCount++;

        _whiteKeyWidth = Math.Max(1, width / whiteKeyCount);
        _blackKeyWidth = Math.Max(1, (int)(_whiteKeyWidth * 0.6));

        var list = new List<int>();
        int wi = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (!IsBlackKey[note]) { list.Add(wi * _whiteKeyWidth); wi++; }
        }
        _whiteKeyX = list.ToArray();
    }

    /// <summary>Main render call. Draws notes + piano + particles.</summary>
    public void Render(IReadOnlyList<Models.MidiNote> notes, double currentTime,
        bool falling, double noteSpeed, bool[]? keyActive)
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

        // Draw piano
        DrawPiano(dc, keyActive, currentTime, w, h);

        // Draw separator
        dc.DrawRectangle(BrushCache.Separator, null, new Rect(0, _pianoY - 2, w, 2));
    }

    private void DrawNotes(DrawingContext dc, IReadOnlyList<Models.MidiNote> notes,
        double currentTime, bool falling, double noteSpeed, int w, int h)
    {
        double visibleDuration = _pianoY / noteSpeed;

        foreach (var note in notes)
        {
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
            if (noteIndex < 0 || noteIndex >= NoteCount) continue;

            int keyX = GetKeyX(noteIndex);
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

            // Alpha fade based on distance from hit point
            double distFromHit = Math.Abs(
                (falling ? note.OnTime : note.OffTime) - currentTime);
            byte alpha = (byte)Math.Max(80, 255 - (int)(distFromHit * 100));

            var color = ChannelColors[note.Channel % 16];
            var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            brush.Freeze();

            dc.DrawRectangle(brush, null, new Rect(keyX, yTop, keyW, yBottom - yTop));
        }
    }

    private void DrawPiano(DrawingContext dc, bool[]? keyActive, double currentTime, int w, int h)
    {
        // White keys
        int wi = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (IsBlackKey[note]) continue;
            int x = wi * _whiteKeyWidth;
            int ni = note - FirstNote;
            bool active = keyActive != null && ni < keyActive.Length && keyActive[ni];

            var brush = active ? BrushCache.WhiteKeyActive : BrushCache.WhiteKey;
            dc.DrawRectangle(brush, null, new Rect(x, _pianoY, _whiteKeyWidth - 1, _pianoHeight));

            // Hit flash
            if (active && _keyHits != null)
            {
                var hit = _keyHits.FirstOrDefault(k => k.NoteIndex == ni);
                if (hit.NoteIndex == ni)
                {
                    double fade = 1.0 - Math.Min(1.0, (currentTime - hit.Time) * 5);
                    if (fade > 0)
                    {
                        var flash = new SolidColorBrush(Color.FromArgb((byte)(fade * 180), 255, 136, 0));
                        flash.Freeze();
                        dc.DrawRectangle(flash, null, new Rect(x, _pianoY, _whiteKeyWidth - 1, _pianoHeight));
                    }
                }
            }
            wi++;
        }

        // Black keys
        wi = 0;
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (!IsBlackKey[note]) { wi++; continue; }
            int x = wi * _whiteKeyWidth - _blackKeyWidth / 2;
            int ni = note - FirstNote;
            bool active = keyActive != null && ni < keyActive.Length && keyActive[ni];

            var brush = active ? BrushCache.BlackKeyActive : BrushCache.BlackKey;
            int bkH = (int)(_pianoHeight * 0.65);
            dc.DrawRectangle(brush, null, new Rect(x, _pianoY, _blackKeyWidth, bkH));

            if (active && _keyHits != null)
            {
                var hit = _keyHits.FirstOrDefault(k => k.NoteIndex == ni);
                if (hit.NoteIndex == ni)
                {
                    double fade = 1.0 - Math.Min(1.0, (currentTime - hit.Time) * 5);
                    if (fade > 0)
                    {
                        var flash = new SolidColorBrush(Color.FromArgb((byte)(fade * 200), 255, 102, 0));
                        flash.Freeze();
                        dc.DrawRectangle(flash, null, new Rect(x, _pianoY, _blackKeyWidth, bkH));
                    }
                }
            }
        }
    }

    private int GetKeyX(int noteIndex)
    {
        int note = noteIndex + FirstNote;
        if (IsBlackKey[note])
        {
            int whiteBefore = 0;
            for (int n = FirstNote; n < note; n++)
                if (!IsBlackKey[n]) whiteBefore++;
            if (whiteBefore > 0 && whiteBefore < _whiteKeyX.Length)
                return _whiteKeyX[whiteBefore] - _blackKeyWidth / 2;
            return 0;
        }
        else
        {
            int wi = 0;
            for (int n = FirstNote; n < note; n++)
                if (!IsBlackKey[n]) wi++;
            return wi < _whiteKeyX.Length ? _whiteKeyX[wi] : 0;
        }
    }

    /// <summary>Get the X position center of a key (for particle spawn).</summary>
    public double GetKeyCenterX(int note)
    {
        int ni = note - FirstNote;
        if (ni < 0 || ni >= NoteCount) return 0;
        int x = GetKeyX(ni);
        int w = IsBlackKey[note] ? _blackKeyWidth : _whiteKeyWidth;
        return x + w / 2.0;
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
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }
}
