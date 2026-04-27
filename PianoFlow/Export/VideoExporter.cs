using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace PianoFlow.Export;

/// <summary>
/// Exports rendered frames to MP4 via FFmpeg subprocess pipe.
/// Captures frames as raw RGB24, encodes with libx264.
/// </summary>
public class VideoExporter : IDisposable
{
    private Process? _ffmpeg;
    private bool _isExporting;
    private byte[]? _rgbBuffer;

    public bool IsExporting => _isExporting;


    /// <summary>Export completed (outputPath).</summary>
    public event Action<string>? Completed;

    /// <summary>Export failed (errorMessage).</summary>
    public event Action<string>? Failed;

    /// <summary>Start exporting video.</summary>
    /// <param name="outputPath">Output MP4 file path.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="fps">Target frames per second.</param>
    /// <param name="crf">Quality (0-51, lower = better, default 18).</param>
    /// <param name="ffmpegPath">Path to ffmpeg executable.</param>
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
            _rgbBuffer = new byte[width * height * 3];
            _isExporting = true;
        }
        catch (Exception ex)
        {
            Failed?.Invoke($"FFmpeg error: {ex.Message}");
        }
    }

    /// <summary>Write a frame to the FFmpeg pipe.
    /// The bitmap must be in BGRA32 format.</summary>
    public unsafe void WriteFrame(WriteableBitmap bitmap)
    {
        if (_ffmpeg == null || !_isExporting || _ffmpeg.StandardInput.BaseStream == null)
            return;

        try
        {
            bitmap.Lock();

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = bitmap.BackBufferStride;
            var src = (byte*)bitmap.BackBuffer;

            // Convert BGRA to RGB24
            int bufferSize = width * height * 3;
            if (_rgbBuffer == null || _rgbBuffer.Length != bufferSize)
            {
                _rgbBuffer = new byte[bufferSize];
            }

            int idx = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = y * stride + x * 4;
                    _rgbBuffer[idx + 2] = src[offset];     // B -> R
                    _rgbBuffer[idx + 1] = src[offset + 1]; // G -> G
                    _rgbBuffer[idx] = src[offset + 2];     // R -> B
                    // skip alpha
                    idx += 3;
                }
            }

            bitmap.Unlock();

            _ffmpeg.StandardInput.BaseStream.Write(_rgbBuffer, 0, _rgbBuffer.Length);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WriteFrame error: {ex.Message}");
        }
    }

    /// <summary>Finish export and close FFmpeg.</summary>
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

    /// <summary>Cancel the current export.</summary>
    public void Cancel()
    {
        if (_ffmpeg != null)
        {
            try
            {
                _ffmpeg.Kill();
            }
            catch { }
            _isExporting = false;
            _ffmpeg.Dispose();
            _ffmpeg = null;
        }
    }

    public void Dispose()
    {
        Cancel();
    }
}
