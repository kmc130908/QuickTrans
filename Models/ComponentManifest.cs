namespace QuickTrans.Models;

/// <summary>可下载组件清单。程序内置一份兜底，也可以从远端 URL 拉取最新版。</summary>
public sealed class ComponentManifest
{
    public string Version { get; set; } = "1";
    public List<ComponentItem> Components { get; set; } = new();
}

/// <summary>一个可下载的组件（引擎或模型）。</summary>
public sealed class ComponentItem
{
    /// <summary>唯一标识，如 engine-cuda / model-qwen3-06b。</summary>
    public string Id { get; set; } = "";

    /// <summary>界面显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话说明。</summary>
    public string Description { get; set; } = "";

    /// <summary>类别：engine 或 model。</summary>
    public string Kind { get; set; } = "model";

    /// <summary>下载体积，用于界面提示。</summary>
    public long SizeBytes { get; set; }

    /// <summary>主下载地址。</summary>
    public string Url { get; set; } = "";

    /// <summary>备用地址，为空则不用。下载时优先试这个，失败再回落主地址。</summary>
    public string MirrorUrl { get; set; } = "";

    /// <summary>下载完成后校验用，为空则跳过校验。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>解压目标子目录（相对于软件根目录），如 engine、models。</summary>
    public string Target { get; set; } = "";

    /// <summary>压缩格式：zip 或 none（none 表示下载的就是最终文件）。</summary>
    public string ArchiveKind { get; set; } = "zip";

    /// <summary>装好后应存在的文件名，用于判断是否已安装。</summary>
    public string CheckFile { get; set; } = "";

    /// <summary>模型文件名匹配前缀：只要 models 目录（或外部模型）中存在同系列 .gguf（如不同量化 Q4_K_M/Q8_0），也识别为已安装。</summary>
    public string[] MatchPrefixes { get; set; } = Array.Empty<string>();

    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB"
        : $"{SizeBytes / 1024.0 / 1024:F0} MB";
}

/// <summary>
/// 内置组件清单。
///
/// 模型来源策略：
/// - Qwen3 系列用 ModelScope（阿里官方，国内直达，实测 3.2MB/s，支持断点续传）。
///   官方只发布了 Q8_0 量化，没有 Q4_K_M，所以这里也用 Q8_0。
/// - Hy-MT2 的 Q4_K_M 是本地自行量化的，官方没有这个版本，必须自己传到 GitHub Release。
/// - GPU 引擎必须自己传到 GitHub Release（Inno 打包产物）。
/// </summary>
public static class DefaultManifest
{
    // 官方引擎（v2.0.0 Vulkan 通用版）与 Hy-MT2 模型发布地址
    private const string RepoBase =
        "https://github.com/kmc130908/QuickTrans/releases/download/v1.1.0";
    private const string EngineRepoBase =
        "https://github.com/kmc130908/QuickTrans/releases/download/v2.0.0";

    // 魔搭社区 ModelScope（国内直连高速），选用 Q4_K_M 量化比 Q8_0 缩小约 40% 且划词质量几乎无损
    private const string Qwen06 =
        "https://modelscope.cn/models/unsloth/Qwen3-0.6B-GGUF/resolve/master/Qwen3-0.6B-Q4_K_M.gguf";
    private const string Qwen17 =
        "https://modelscope.cn/models/unsloth/Qwen3-1.7B-GGUF/resolve/master/Qwen3-1.7B-Q4_K_M.gguf";

    public static ComponentManifest Create() => new()
    {
        Version = "2",
        Components = new List<ComponentItem>
        {
            new()
            {
                Id = "engine-vulkan",
                Name = "Vulkan 通用 GPU 加速引擎",
                Description = "支持 NVIDIA / AMD / Intel 全系显卡加速（仅 31MB，并自带 CPU 兜底）。不装只能用 CPU，速度慢 3–4 倍。",
                Kind = "engine",
                SizeBytes = 32_840_864L,
                Url = $"{EngineRepoBase}/engine-vulkan.zip",
                MirrorUrl = $"https://ghfast.top/{EngineRepoBase}/engine-vulkan.zip",
                Sha256 = "4C9AA655405F5A5FA5498968F70969F8CB410EA8EFDCE37A9D8BA7C7EE9FB982",
                Target = "engine",
                ArchiveKind = "zip",
                CheckFile = "ggml-vulkan.dll",
            },
            new()
            {
                Id = "model-qwen3-06b",
                Name = "Qwen3-0.6B（378MB，最轻快）",
                Description = "Q4_K_M 精简量化，魔搭国内直连。体积最小、速度最快，适合轻度划词看大意。",
                Kind = "model",
                SizeBytes = 396_705_472L,
                Url = Qwen06,
                Target = "models",
                ArchiveKind = "none",
                CheckFile = "Qwen3-0.6B-Q4_K_M.gguf",
                MatchPrefixes = new[] { "Qwen3-0.6B" },
            },
            new()
            {
                Id = "model-qwen3-17b",
                Name = "Qwen3-1.7B（1.03GB，全能推荐）",
                Description = "Q4_K_M 量化，魔搭国内直连。兼顾翻译、词典、润色与代码解释，综合能力强。",
                Kind = "model",
                SizeBytes = 1_107_409_472L,
                Url = Qwen17,
                Target = "models",
                ArchiveKind = "none",
                CheckFile = "Qwen3-1.7B-Q4_K_M.gguf",
                MatchPrefixes = new[] { "Qwen3-1.7B" },
            },
            new()
            {
                Id = "model-hymt2-18b",
                Name = "Hy-MT2-1.8B（1.06GB）",
                Description = "专用翻译模型，质量最稳，无繁体风险。官方无此量化版，由本项目自行量化发布。",
                Kind = "model",
                SizeBytes = 1_133_076_480L,
                Url = $"{RepoBase}/Hy-MT2-1.8B-Q4_K_M.gguf",
                MirrorUrl = $"https://ghfast.top/{RepoBase}/Hy-MT2-1.8B-Q4_K_M.gguf",
                Sha256 = "DC5F44FCF1FA496EE7AD725982C0C8C553A4DE00259B53AF84C4B89FB0C06699",
                Target = "models",
                ArchiveKind = "none",
                CheckFile = "Hy-MT2-1.8B-Q4_K_M.gguf",
                MatchPrefixes = new[] { "Hy-MT2-1.8B", "HY-MT1.5-1.8B", "HY-MT-1.8B" },
            },
        },
    };
}
