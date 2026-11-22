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

    /// <summary>当前配置是否支持 Prompt 级功能（单词词典、润色、解释）。仅本地模型与 OpenAI 兼容接口支持。</summary>
    public static bool SupportsPromptModes(AppConfig cfg)
    {
        if (!string.Equals(cfg.Engine, "api", StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.Equals(cfg.ApiType, "baidu", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(cfg.ApiType, "youdao", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(cfg.ApiType, "google", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断选中文本是否为单个英文单词（不含空格和换行），用于触发词典模式。</summary>
    public static bool IsSingleEnglishWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.Length < 2 || s.Length > 32) return false;

        var hasLetter = false;
        var sepCount = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                hasLetter = true;
            }
            else if ((c == '-' || c == '\'') && i > 0 && i < s.Length - 1)
            {
                sepCount++;
                if (sepCount > 2) return false;
            }
            else
            {
                return false;
            }
        }
        return hasLetter;
    }

    /// <summary>Hy-MT2 官方推荐的默认翻译指令。</summary>
    public static string BuildPrompt(string text, string targetLanguage, string mode = "translate", bool wordDict = false)
    {
        var lang = MapLanguage(targetLanguage);

        if (string.Equals(mode, "polish", StringComparison.OrdinalIgnoreCase))
        {
            return "Rewrite and polish the following text to make it natural, clear, and idiomatic in its original language. " +
                   "Reply with the polished text only, without explanations or preamble.\n\n" +
                   text;
        }

        if (string.Equals(mode, "explain", StringComparison.OrdinalIgnoreCase))
        {
            return $"Explain the following text, term, or code snippet concisely in {lang}. " +
                   "Be direct and structured, without unnecessary preamble.\n\n" +
                   text;
        }

        if (wordDict && IsSingleEnglishWord(text))
        {
            var word = text.Trim();
            if (string.Equals(targetLanguage, "简体中文", StringComparison.Ordinal))
            {
                return $"参照下面的词典格式，解释英文单词 \"{word}\"（简体中文）。只输出结果，不要多余说明。\n\n" +
                       "示例（apple）：\n" +
                       "音标：/ˈæp.əl/\n" +
                       "词性：n.\n" +
                       "释义：苹果；苹果树\n" +
                       "例句：She ate an apple for breakfast.（她早餐吃了一个苹果。）\n\n" +
                       $"现在请解释 \"{word}\"：";
            }

            if (string.Equals(targetLanguage, "繁体中文", StringComparison.Ordinal)
                || string.Equals(targetLanguage, "繁體中文", StringComparison.Ordinal))
            {
                return $"參照下面的詞典格式，解釋英文單詞 \"{word}\"（繁體中文）。只輸出結果，不要多餘說明。\n\n" +
                       "範例（apple）：\n" +
                       "音標：/ˈæp.əl/\n" +
                       "詞性：n.\n" +
                       "釋義：蘋果；蘋果樹\n" +
                       "例句：She ate an apple for breakfast.（她早餐吃了一個蘋果。）\n\n" +
                       $"現在請解釋 \"{word}\"：";
            }

            return $"Follow the exact 4-line dictionary format below to explain the English word \"{word}\" in {lang}. " +
                   "Always include the IPA line. Reply with the entry only, no preamble.\n\n" +
                   "Example (apple):\n" +
                   "IPA: /ˈæp.əl/\n" +
                   "POS: n.\n" +
                   "Meaning: a round fruit with red or green skin\n" +
                   "Example: She ate an apple for breakfast.\n\n" +
                   $"Now explain \"{word}\":";
        }

        // 措辞刻意严格：Qwen3 这类通用模型偶尔会复述指令或先说一句"翻译结果如下"，
        // 这里把要求讲死，配合后处理把漏网的套话剥掉。
        return $"Translate the following text into {lang}. " +
               "Reply with the translation only. Do not repeat or explain these instructions, " +
               "and do not add any preamble or commentary.\n\n" +
               text;
    }

    /// <summary>sakura-1.5B 训练时使用的官方系统提示，缺了它容易出元话语和重复。</summary>
    private const string SakuraSystemPrompt =
        "你是一个轻小说翻译模型，可以流畅通顺地以日本轻小说的风格将日文翻译成简体中文，" +
        "并联系上下文正确使用人称代词，不擅自添加原文中没有的代词。";

    /// <summary>当前是不是一次真正的翻译请求（词典模式仍然走通用提示）。</summary>
    private static bool IsTranslateRequest(string mode, bool allowWordDict, string text) =>
        string.Equals(mode, "translate", StringComparison.OrdinalIgnoreCase)
        && !(allowWordDict && IsSingleEnglishWord(text));

    private static bool IsSakuraModel(string? modelName) =>
        !string.IsNullOrEmpty(modelName)
        && modelName.Contains("sakura", StringComparison.OrdinalIgnoreCase);

    /// <summary>只认 plamo-3-translate 系列，避免把普通 plamo 对话模型也套上翻译模板。</summary>
    private static bool IsPlamoTranslateModel(string? modelName) =>
        !string.IsNullOrEmpty(modelName)
        && modelName.Contains("plamo", StringComparison.OrdinalIgnoreCase)
        && modelName.Contains("translate", StringComparison.OrdinalIgnoreCase);

    private static string? PlamoTargetCode(string targetLanguage) => targetLanguage switch
    {
        "简体中文" => "zh",
        "繁体中文" or "繁體中文" => "zh-Hant",
        "英语" or "英文" => "en",
        "日语" or "日文" => "ja",
        "韩语" or "韩文" => "ko",
        _ => null,
    };

    private static string PlamoSourceCode(string text)
    {
        if (IsMostlyJapanese(text)) return "ja";
        if (IsMostlyChinese(text)) return "zh";
        return "en";
    }

    /// <summary>
    /// 构造发给模型的消息。只对 plamo-3-translate / sakura 的翻译模式做专属分支，
    /// 其他模型、润色、解释、词典模式仍使用原来的 BuildPrompt，行为不变。
    /// </summary>
    private static object[] BuildMessages(
        string text, string target, string mode, bool allowWordDict, string? modelName)
    {
        if (IsTranslateRequest(mode, allowWordDict, text))
        {
            // sakura 只会日译中；其他方向继续用通用提示，避免把它不擅长的方向也硬套模板。
            if (IsSakuraModel(modelName)
                && string.Equals(target, "简体中文", StringComparison.Ordinal)
                && IsMostlyJapanese(text))
            {
                return new object[]
                {
                    new { role = "system", content = SakuraSystemPrompt },
                    new { role = "user", content = "将下面的日文文本翻译成中文：" + text },
                };
            }

            if (IsPlamoTranslateModel(modelName))
            {
                var targetCode = PlamoTargetCode(target);
                if (targetCode != null)
                {
                    var sourceCode = PlamoSourceCode(text);
                    return new object[]
                    {
                        new
                        {
                            role = "system",
                            content = $"[plamo-translate]\ntarget_lang={targetCode}\nsource_lang={sourceCode}\n---",
                        },
                        new { role = "user", content = text },
                    };
                }
            }
        }

        return new object[] { new { role = "user", content = BuildPrompt(text, target, mode, allowWordDict) } };
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
    /// 判断文本主体是不是日语。用假名占比而不是“有没有假名”，避免中文里夹一个
    /// 日文产品名就被误判；同时能认出汉字很多、假名很少的技术日语。
    /// </summary>
    public static bool IsMostlyJapanese(string text)
    {
        var kana = 0;
        var han = 0;
        foreach (var c in text)
        {
            if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0xFF66 && c <= 0xFF9D)) kana++;
            else if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF)) han++;
        }

        var total = kana + han;
        return kana >= 2 && total >= 2 && kana * 100 >= total * 25;
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
        [EnumeratorCancellation] CancellationToken ct,
        string mode = "translate",
        bool allowWordDict = false)
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
                await foreach (var s in StreamOpenAiAsync(text, target, cfg, mode, allowWordDict, ct))
                    yield return s;
            }
        }
        else
        {
            await foreach (var s in StreamLlamaAsync(text, target, localBaseUrl, mode, allowWordDict, cfg.SelectedModel, ct))
                yield return s;
        }
    }

    private static async IAsyncEnumerable<string> StreamLlamaAsync(
        string text, string target, string baseUrl, string mode, bool allowWordDict, string? modelName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = BuildMessages(text, target, mode, allowWordDict, modelName);

        var payload = new
        {
            messages,
            stream = true,
            n_predict = 1024,
            temperature = 0.7,
            top_p = 0.6,
            top_k = 20,
            repeat_penalty = 1.05,
            cache_prompt = true,
        };

        // 翻译请求才做重复熔断；润色/解释/词典保持原有输出行为。
        var detectRepetition = IsTranslateRequest(mode, allowWordDict, text);

        await foreach (var s in PostAndReadAsync(
                           baseUrl.TrimEnd('/') + "/v1/chat/completions", payload, null, ct, detectRepetition))
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
        string text, string target, AppConfig cfg, string mode, bool allowWordDict,
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
            messages = new object[] { new { role = "user", content = BuildPrompt(text, target, mode, allowWordDict) } },
            stream = true,
            temperature = 0.3,
            max_tokens = 1024,
        };

        await foreach (var s in PostAndReadAsync(url, payload, cfg.ApiKey, ct))
            yield return s;
    }

    private static async IAsyncEnumerable<string> PostAndReadAsync(
        string url, object payload, string? apiKey,
        [EnumeratorCancellation] CancellationToken ct,
        bool detectRepetition = false)
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

        var guard = detectRepetition ? new RepetitionGuard() : null;
        var fullText = detectRepetition ? new StringBuilder() : null;

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
                if (!string.IsNullOrEmpty(piece))
                {
                    if (guard != null && fullText != null)
                    {
                        fullText.Append(piece);
                        yield return piece;

                        // 先让这一片正常显示，再判断是否进入“同一段话连续重复”的循环。
                        // 一旦命中就结束枚举，释放 HTTP 响应，llama-server 随连接关闭停止生成。
                        if (guard.ShouldStop(fullText)) yield break;
                    }
                    else
                    {
                        yield return piece;
                    }
                }
            }
        }
    }

    private static string Brief(string s) => s.Length > 240 ? s[..240] + "…" : s;

    /// <summary>
    /// 流式重复熔断器。只做内存里的字符比较，不改 temperature / n_predict / 模型参数，
    /// 也不改变正常译文的输出；检测到结尾同一段 12–96 字连续出现 3 次就判定为循环。
    /// </summary>
    private sealed class RepetitionGuard
    {
        private const int MinPeriod = 12;
        private const int MaxPeriod = 96;
        private const int Repeats = 3;

        private int _lastLength;

        public bool ShouldStop(StringBuilder text)
        {
            var len = text.Length;
            if (len < MinPeriod * Repeats) return false;

            // 每积累 8 个字符才检查一次，避免每个 token 都把整段扫一遍。
            if (len - _lastLength < 8) return false;
            _lastLength = len;

            var maxPeriod = Math.Min(MaxPeriod, len / Repeats);
            for (var period = MinPeriod; period <= maxPeriod; period++)
            {
                var last = len - period;
                var prev = last - period;
                var prev2 = prev - period;

                if (BlockEquals(text, prev2, prev, period)
                    && BlockEquals(text, prev, last, period))
                    return true;
            }

            return false;
        }

        private static bool BlockEquals(StringBuilder s, int a, int b, int len)
        {
            for (var i = 0; i < len; i++)
                if (s[a + i] != s[b + i]) return false;
            return true;
        }
    }
}
