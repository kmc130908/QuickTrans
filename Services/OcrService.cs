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

        byte[] png;
        try
        {
            png = CaptureRegionAsPng(x, y, width, height);
        }
        catch (Exception ex)
        {
            return (false, "", "截屏失败：" + ex.Message);
        }

        try
        {
            using var ras = new InMemoryRandomAccessStream();

            using (var output = ras.GetOutputStreamAt(0))
            {
                using var writer = new DataWriter(output);
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await output.FlushAsync();
            }

            ras.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var bitmap = await decoder.GetSoftwareBitmapAsync();

            var result = await engine.RecognizeAsync(bitmap);

            var lines = result.Lines
                .Select(l => l.Text.Trim())
                .Where(t => t.Length > 0);

            var text = string.Join(Environment.NewLine, lines).Trim();
            return text.Length == 0
                ? (false, "", "没有识别到文字，换个区域或放大一点再试")
                : (true, text, "");
        }
        catch (Exception ex)
        {
            return (false, "", "识别出错：" + ex.Message);
        }
    }

    /// <summary>抓屏并放大，小字放大后识别率明显更高。</summary>
    private static byte[] CaptureRegionAsPng(int x, int y, int width, int height)
    {
        using var src = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        const int scale = 2;
        using var big = new Bitmap(width * scale, height * scale, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, big.Width, big.Height);
        }

        using var ms = new MemoryStream();
        big.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
