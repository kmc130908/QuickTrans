using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace QuickTrans.Services;

/// <summary>
/// 截图 OCR，用 Windows 自带的识别引擎：不需要额外模型，不占显存，
/// 只有真正识别时才加载语言包（约 100ms），之后进程内缓存复用。
/// </summary>
public static class OcrService
{
    private static OcrEngine? _engine;
    private static readonly object Gate = new();

    /// <summary>系统当前可用的 OCR 语言。</summary>
    public static IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages
                    .Select(l => l.LanguageTag)
                    .ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    private static OcrEngine? ResolveEngine()
    {
        lock (Gate)
        {
            if (_engine != null) return _engine;

            foreach (var tag in new[] { "zh-Hans-CN", "zh-Hans", "zh-CN", "en-US" })
            {
                try
                {
                    var lang = new Language(tag);
                    if (!OcrEngine.IsLanguageSupported(lang)) continue;
                    _engine = OcrEngine.TryCreateFromLanguage(lang);
                    if (_engine != null) return _engine;
                }
                catch { /* 某个语言标签不合法，继续试下一个 */ }
            }

            try { _engine = OcrEngine.TryCreateFromUserProfileLanguages(); }
            catch { /* 用户配置也拿不到 */ }

            return _engine;
        }
    }

    /// <summary>识别屏幕上指定区域，返回识别到的文字。</summary>
    public static async Task<(bool Ok, string Text, string Error)> RecognizeRegionAsync(
        int x, int y, int width, int height)
    {
        if (width < 4 || height < 4)
            return (false, "", "选区太小");

        var engine = ResolveEngine();
        if (engine == null)
            return (false, "", "系统没有可用的 OCR 语言包。到「设置 → 时间和语言 → 语言」里给中文安装「光学字符识别」组件。");

        SoftwareBitmap bitmap;
        try
        {
            bitmap = CaptureRegion(x, y, width, height, (int)OcrEngine.MaxImageDimension);
        }
        catch (Exception ex)
        {
            return (false, "", "截屏失败：" + ex.Message);
        }

        try
        {
            using (bitmap)
            {
                var result = await engine.RecognizeAsync(bitmap);

                var lines = result.Lines
                    .Select(l => l.Text.Trim())
                    .Where(t => t.Length > 0);

                var text = string.Join(Environment.NewLine, lines).Trim();
                return text.Length == 0
                    ? (false, "", "没有识别到文字，换个区域或放大一点再试")
                    : (true, text, "");
            }
        }
        catch (Exception ex)
        {
            return (false, "", "识别出错：" + ex.Message);
        }
    }

    /// <summary>小选区才放大的像素上限（约 250 万像素，相当于 1600×1560）。</summary>
    private const long UpscalePixelLimit = 2_500_000;

    /// <summary>
    /// 抓屏并直接生成 SoftwareBitmap：不再经过“放大位图 → PNG 编码 → 内存流 → 解码”的链路，
    /// 同一张图不会在内存里同时存在好几份。小字选区放大 2 倍识别率更高；
    /// 大选区放大收益很小却会把内存涨 4 倍，保持原尺寸。
    /// </summary>
    private static SoftwareBitmap CaptureRegion(int x, int y, int width, int height, int maxDimension)
    {
        using var src = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        var scale = (long)width * height <= UpscalePixelLimit ? 2 : 1;
        if (maxDimension > 0)
        {
            while (scale > 1 && Math.Max(width, height) * scale > maxDimension) scale--;
        }

        if (scale == 1)
            return ToSoftwareBitmap(src);

        using var big = new Bitmap(width * scale, height * scale, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, big.Width, big.Height);
        }

        return ToSoftwareBitmap(big);
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bmp.Width * 4;
            var buffer = new byte[rowBytes * bmp.Height];

            // Stride 对 32bpp 恒等于 width*4，这里仍按行拷贝以防万一
            for (var row = 0; row < bmp.Height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    IntPtr.Add(data.Scan0, row * data.Stride), buffer, row * rowBytes, rowBytes);
            }

            // 屏幕抓图的 alpha 通道没有意义，统一置为不透明，避免被当成透明像素
            for (var i = 3; i < buffer.Length; i += 4) buffer[i] = 0xFF;

            return SoftwareBitmap.CreateCopyFromBuffer(
                System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(buffer),
                BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}