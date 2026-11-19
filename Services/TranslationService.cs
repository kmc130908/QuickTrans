using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using QuickTrans.Models;

namespace QuickTrans.Services;

/// <summary>
/// 翻译调用层。本地走 llama-server 的 OpenAI 兼容接口，在线走用户自定义的兼容接口，
/// 两条路返回格式一致，界面只认流式片段。
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>百度/有道/谷歌这类一次性返回的接口的单次请求超时。</summary>
    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(20);

    private static string MapLanguage(string name) => name switch
    {
        "简体中文" => "Simplified Chinese",
        "繁体中文" or "繁體中文" => "Traditional Chinese",
        "英语" or "英文" => "English",
        "日语" or "日文" => "Japanese",
        "韩语" or "韩文" => "Korean",
        "法语" => "French",
        "德语" => "German",
        "西班牙语" => "Spanish",
        "俄语" => "Russian",
        "葡萄牙语" => "Portuguese",
        "意大利语" => "Italian",
        "阿拉伯语" => "Arabic",
        "泰语" => "Thai",
        "越南语" => "Vietnamese",
        _ => name,
    };

    /// <summary>Hy-MT2 官方推荐的默认翻译指令。</summary>
    public static string BuildPrompt(string text, string targetLanguage)
    {
        var lang = MapLanguage(targetLanguage);
        // 措辞刻意严格：Qwen3 这类通用模型偶尔会复述指令或先说一句"翻译结果如下"，
        // 这里把要求讲死，配合后处理把漏网的套话剥掉。
        return $"Translate the following text into {lang}. " +
               "Reply with the translation only. Do not repeat or explain these instructions, " +
               "and do not add any preamble or commentary.\n\n" +
               text;
    }

    /// <summary>判断文本主体是不是中文，用于自动决定翻译方向。</summary>
    public static bool IsMostlyChinese(string text)
    {
        var han = 0;
        var other = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            if (c >= 0x4E00 && c <= 0x9FFF) han++;
            else other++;
        }
        return han > 0 && han >= other;
    }

    /// <summary>
    /// 决定目标语言。手动指定优先；否则中文译成英语，其他语言译成默认目标语言，
    /// 并且避免出现「译成同一种语言」这种无意义的结果。
    /// </summary>
    public static string ResolveTarget(string sourceText, AppConfig cfg, string? overrideTarget = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideTarget)) return overrideTarget!;

        if (!cfg.AutoDirection) return cfg.TargetLanguage;

        var isChinese = IsMostlyChinese(sourceText);
        var target = cfg.TargetLanguage;

        if (isChinese && string.Equals(target, "简体中文", StringComparison.Ordinal))
            return "英语";

        if (!isChinese && string.Equals(target, "英语", StringComparison.Ordinal))
            return "简体中文";

        return target;
    }

    public static async IAsyncEnumerable<string> TranslateAsync(
        string text,
        AppConfig cfg,
        string localBaseUrl,
        string target,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.Equals(cfg.Engine, "api", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(cfg.ApiType, "baidu", StringComparison.OrdinalIgnoreCase))
            {
                var translated = await TranslateBaiduAsync(text, target, cfg, ct);
                yield return translated;
            }
            else if (string.Equals(cfg.ApiType, "youdao", StringComparison.OrdinalIgnoreCase))
            {
                var translated = await TranslateYoudaoAsync(text, target, cfg, ct);
                yield return translated;
            }
            else if (string.Equals(cfg.ApiType, "google", StringComparison.OrdinalIgnoreCase))
            {
                var translated = await TranslateGoogleAsync(text, target, ct);
                yield return translated;
            }
            else
            {
                await foreach (var s in StreamOpenAiAsync(text, target, cfg, ct))
                    yield return s;
            }
        }
        else
        {
            await foreach (var s in StreamLlamaAsync(text, target, localBaseUrl, ct))
                yield return s;
        }
    }

    private static async IAsyncEnumerable<string> StreamLlamaAsync(
        string text, string target, string baseUrl,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new
        {
            messages = new object[] { new { role = "user", content = BuildPrompt(text, target) } },
            stream = true,
            n_predict = 1024,
            temperature = 0.7,
            top_p = 0.6,
            top_k = 20,
            repeat_penalty = 1.05,
            cache_prompt = true,
        };

        await foreach (var s in PostAndReadAsync(baseUrl.TrimEnd('/') + "/v1/chat/completions", payload, null, ct))
            yield return s;
    }

    /// <summary>
    /// 百度翻译开放平台。注意它跟 OpenAI 协议完全不兼容：
    /// 要 appid + 密钥做 MD5 签名，语言码也不一样（zh/jp/kor…），所以单独实现。
    /// 密钥栏按「appid:密钥」的格式填写。
    /// </summary>
    private static async Task<string> TranslateBaiduAsync(
        string text, string target, AppConfig cfg, CancellationToken ct)
    {
        var raw = (cfg.ApiKey ?? "").Trim();
        var sep = raw.IndexOf(':');
        if (sep <= 0 || sep == raw.Length - 1)
            throw new InvalidOperationException(
                "百度翻译需要把密钥填成「appid:密钥」的格式，\n例如 20240101000000000:AbCdEfGhIjKlMnOp");

        var appId = raw[..sep].Trim();
        var secret = raw[(sep + 1)..].Trim();
        var salt = Random.Shared.Next(100000, 999999).ToString();
        var to = MapBaiduLang(target);
        var sign = Md5Hex(appId + text + salt + secret);

        // 用 POST 而不是 GET：中文 URL 编码后每字膨胀到 9 个字符，
        // 一段 300 字的文本就能把 URL 撑到 2700+，超过 IIS 默认的 2048 上限返回 414。
        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = "auto",
            ["to"] = to,
            ["appid"] = appId,
            ["salt"] = salt,
            ["sign"] = sign,
        };

        // 复用共享的 HttpClient（每次 new 会留下一批 Socket/连接池对象），超时改成按请求控制
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OnlineTimeout);
        using var content = new FormUrlEncodedContent(form);
        var json = await (await Http.PostAsync(
                "https://fanyi-api.baidu.com/api/trans/vip/translate", content, timeout.Token))
            .Content.ReadAsStringAsync(timeout.Token);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error_code", out var codeEl))
        {
            var code = codeEl.ValueKind == JsonValueKind.String ? codeEl.GetString() : codeEl.ToString();
            var msg = root.TryGetProperty("error_msg", out var msgEl) ? msgEl.GetString() : "";
            throw new InvalidOperationException($"百度翻译返回错误 {code}：{msg}");
        }

        if (!root.TryGetProperty("trans_result", out var results))
            throw new InvalidOperationException("百度翻译没有返回译文，原始响应：" + Truncate(json, 200));

        var sb = new StringBuilder();
        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("dst", out var dst))
                sb.AppendLine(dst.GetString());
        }

        var output = sb.ToString().Trim();
        if (output.Length == 0) throw new InvalidOperationException("百度翻译返回了空译文");
        return output;
    }

    /// <summary>
    /// 有道智云。密钥栏按「appKey:appSecret」填。
    /// 签名算法：SHA256(appKey + 截断后的输入 + salt + curtime + appSecret)。
    /// </summary>
    private static async Task<string> TranslateYoudaoAsync(
        string text, string target, AppConfig cfg, CancellationToken ct)
    {
        var raw = (cfg.ApiKey ?? "").Trim();
        var sep = raw.IndexOf(':');
        if (sep <= 0 || sep == raw.Length - 1)
            throw new InvalidOperationException(
                "有道翻译需要把密钥填成「appKey:appSecret」的格式");

        var appKey = raw[..sep].Trim();
        var appSecret = raw[(sep + 1)..].Trim();

        var salt = Guid.NewGuid().ToString();
        var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // 有道规定：长度超过 20 的文本要按「前10 + 总长 + 后10」截断后再签名。
        // 用 Rune（Unicode 标量值）而不是 UTF-16 code unit 切，否则 emoji 会被
        // 从代理对中间切断，产生无效序列导致签名错误。
        var runes = text.EnumerateRunes().ToArray();
        var input = runes.Length <= 20
            ? text
            : string.Concat(runes.Take(10)) + runes.Length + string.Concat(runes.TakeLast(10));

        var sign = Sha256Hex(appKey + input + salt + curtime + appSecret);

        var form = new Dictionary<string, string>
        {
            ["q"] = text,
            ["from"] = "auto",
            ["to"] = MapYoudaoLang(target),
            ["appKey"] = appKey,
            ["salt"] = salt,
            ["sign"] = sign,
            ["signType"] = "v3",
            ["curtime"] = curtime,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OnlineTimeout);
        using var content = new FormUrlEncodedContent(form);
        var json = await (await Http.PostAsync("https://openapi.youdao.com/api", content, timeout.Token))
            .Content.ReadAsStringAsync(timeout.Token);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.TryGetProperty("errorCode", out var codeEl) ? codeEl.GetString() : "";
        if (!string.Equals(code, "0", StringComparison.Ordinal))
        {
            var hint = code switch
            {
                "108" => "（appKey 无效）",
                "202" => "（签名校验失败，检查 appSecret）",
                "206" => "（时间戳无效，检查系统时间）",
                "401" => "（账户余额不足）",
                "411" => "（访问频率受限）",
                _ => "",
            };
            throw new InvalidOperationException($"有道翻译返回错误码 {code}{hint}");
        }

        if (root.TryGetProperty("translation", out var arr) && arr.GetArrayLength() > 0)
        {
            var sb = new StringBuilder();
            foreach (var item in arr.EnumerateArray())
                sb.AppendLine(item.GetString());
            return sb.ToString().Trim();
        }

        throw new InvalidOperationException("有道翻译没有返回译文");
    }

    /// <summary>
    /// 谷歌翻译的免费网页端点（client=gtx），不需要 key。
    /// 返回的是嵌套数组，译文在 [0][*][0] 里逐段拼接。
    /// 注意国内访问需要代理。
    /// </summary>
    private static async Task<string> TranslateGoogleAsync(
        string text, string target, CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single" +
                  $"?client=gtx&sl=auto&tl={MapGoogleLang(target)}&dt=t" +
                  $"&q={Uri.EscapeDataString(text)}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OnlineTimeout);
        var json = await Http.GetStringAsync(url, timeout.Token);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidOperationException("谷歌翻译返回格式异常");

        var sb = new StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
        {
            if (seg.ValueKind != JsonValueKind.Array || seg.GetArrayLength() == 0) continue;
            var piece = seg[0].GetString();
            if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
        }

        var output = sb.ToString().Trim();
        if (output.Length == 0) throw new InvalidOperationException("谷歌翻译返回了空译文");
        return output;
    }

    private static string Sha256Hex(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string MapYoudaoLang(string target) => target switch
    {
        "简体中文" => "zh-CHS",
        "繁体中文" => "zh-CHT",
        "英语" => "en",
        "日语" => "ja",
        "韩语" => "ko",
        "法语" => "fr",
        "德语" => "de",
        "西班牙语" => "es",
        "俄语" => "ru",
        "葡萄牙语" => "pt",
        "意大利语" => "it",
        "阿拉伯语" => "ar",
        "泰语" => "th",
        "越南语" => "vi",
        _ => "zh-CHS",
    };

    private static string MapGoogleLang(string target) => target switch
    {
        "简体中文" => "zh-CN",
        "繁体中文" => "zh-TW",
        "英语" => "en",
        "日语" => "ja",
        "韩语" => "ko",
        "法语" => "fr",
        "德语" => "de",
        "西班牙语" => "es",
        "俄语" => "ru",
        "葡萄牙语" => "pt",
        "意大利语" => "it",
        "阿拉伯语" => "ar",
        "泰语" => "th",
        "越南语" => "vi",
        _ => "zh-CN",
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static string Md5Hex(string input)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string MapBaiduLang(string target) => target switch
    {
        "简体中文" => "zh",
        "繁体中文" => "cht",
        "英语" => "en",
        "日语" => "jp",
        "韩语" => "kor",
        "法语" => "fra",
        "德语" => "de",
        "西班牙语" => "spa",
        "俄语" => "ru",
        "葡萄牙语" => "pt",
        "意大利语" => "it",
        "阿拉伯语" => "ara",
        "泰语" => "th",
        "越南语" => "vie",
        _ => "zh",
    };

    private static async IAsyncEnumerable<string> StreamOpenAiAsync(
        string text, string target, AppConfig cfg,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl))
            throw new InvalidOperationException("还没配置在线接口地址。打开设置，填写 Base URL、密钥和模型名。");

        var baseUrl = cfg.ApiBaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/chat/completions";

        var payload = new
        {
            model = cfg.ApiModel,
            messages = new object[] { new { role = "user", content = BuildPrompt(text, target) } },
            stream = true,
            temperature = 0.3,
            max_tokens = 1024,
        };

        await foreach (var s in PostAndReadAsync(url, payload, cfg.ApiKey, ct))
            yield return s;
    }

    private static async IAsyncEnumerable<string> PostAndReadAsync(
        string url, object payload, string? apiKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"接口返回 {(int)resp.StatusCode}：{Brief(body)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            using (doc)
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new HttpRequestException("服务端错误：" + err.ToString());

                if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) continue;
                if (!choices[0].TryGetProperty("delta", out var delta)) continue;
                if (!delta.TryGetProperty("content", out var content)) continue;

                var piece = content.GetString();
                if (!string.IsNullOrEmpty(piece)) yield return piece;
            }
        }
    }

    private static string Brief(string s) => s.Length > 240 ? s[..240] + "…" : s;
}
