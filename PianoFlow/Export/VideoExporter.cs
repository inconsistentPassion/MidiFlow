using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PianoFlow.Export;

/// <summary>
/// Exports rendered frames to MP4 via FFmpeg subprocess pipe.
/// Accepts RenderTargetBitmap (from WPF visual rendering).
/// </summary>
public class VideoExporter : IDisposable
{
    private Process? _ffmpeg;
    private bool _isExporting;

    public bool IsExporting => _isExporting;

    public event Action<string>? Completed;
    public event Action<string>? Failed;

    public void StartExport(string outputPath, int width, int height,
        int fps = 30, int crf = 18, string ffmpegPath = "ffmpeg")
    {
        if (_isExporting) return;

        string args = $"-y -f rawvideo -vcodec rawvideo -pix_fmt rgb24 " +
                      $"-s {width}x{height} -r {fps} -i - " +
                      $"-c:v libx264 -crf {crf} -preset fast -pix_fmt yuv420p " +
                      $"\"{outputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            _ffmpeg = Process.Start(psi);
            if (_ffmpeg == null)
            {
                Failed?.Invoke("Failed to start FFmpeg process.");
                return;
            }
            _isExporting = true;
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"FFmpeg error: {ex.Message}");
        }
    }

    /// <summary>Write a frame. Accepts any BitmapSource (RenderTargetBitmap, etc).</summary>
    public void WriteFrame(BitmapSource bitmap)
    {
        if (_ffmpeg == null || !_isExporting) return;

        try
        {
            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;

            // Convert to RGB24
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Rgb24, null, 0);
            byte[] rgb = new byte[w * h * 3];
            converted.CopyPixels(rgb, w * 3, 0);

            _ffmpeg.StandardInput.BaseStream.Write(rgb, 0, rgb.Length);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WriteFrame error: {ex.Message}");
        }
    }

    public void FinishExport(int totalFrames)
    {
        if (_ffmpeg == null) return;

        try
        {
            _ffmpeg.StandardInput.Close();
            _ffmpeg.WaitForExit(30000);
            Completed?.Invoke("Export complete.");
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"Export finish error: {ex.Message}");
        }
        finally
        {
            _isExporting = false;
            _ffmpeg?.Dispose();
            _ffmpeg = null;
        }
    }

    public void Cancel()
    {
        if (_ffmpeg != null)
        {
            try { _ffmpeg.Kill(); } catch { }
            _isExporting = false;
            _ffmpeg.Dispose();
            _ffmpeg = null;
        }
    }

    public void Dispose() => Cancel();
}
