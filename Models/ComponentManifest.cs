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
    // 自己上传的引擎和 Hy-MT2 模型放在这里，替换用户名即可
    private const string RepoBase =
        "https://github.com/kmc130908/QuickTrans/releases/download/v1.1.0";

    // 阿里 ModelScope 的官方仓库，国内直连
    private const string Qwen06 =
        "https://modelscope.cn/models/Qwen/Qwen3-0.6B-GGUF/resolve/master/Qwen3-0.6B-Q8_0.gguf";
    private const string Qwen17 =
        "https://modelscope.cn/models/Qwen/Qwen3-1.7B-GGUF/resolve/master/Qwen3-1.7B-Q8_0.gguf";

    public static ComponentManifest Create() => new()
    {
        Version = "1",
        Components = new List<ComponentItem>
        {
            new()
            {
                Id = "engine-cuda",
                Name = "GPU 加速引擎",
                Description = "带 CUDA 后端，NVIDIA 显卡可用。不装只能用 CPU，速度慢 3-4 倍。",
                Kind = "engine",
                SizeBytes = 744L * 1024 * 1024,
                Url = $"{RepoBase}/engine-cuda.zip",
                MirrorUrl = $"https://ghfast.top/{RepoBase}/engine-cuda.zip",
                Target = "engine",
                ArchiveKind = "zip",
                CheckFile = "llama-server.exe",
            },
            new()
            {
                Id = "model-qwen3-06b",
                Name = "Qwen3-0.6B（609MB，推荐）",
                Description = "最小的可用模型，阿里官方源国内直连。速度最快，适合看大意。",
                Kind = "model",
                SizeBytes = 609L * 1024 * 1024,
                Url = Qwen06,
                Target = "models",
                ArchiveKind = "none",
                CheckFile = "Qwen3-0.6B-Q8_0.gguf",
            },
            new()
            {
                Id = "model-qwen3-17b",
                Name = "Qwen3-1.7B（1.71GB）",
                Description = "质量明显好于 0.6B，同样走阿里官方源。体积较大，CPU 推理也慢一些。",
                Kind = "model",
                SizeBytes = 1749L * 1024 * 1024,
                Url = Qwen17,
                Target = "models",
                ArchiveKind = "none",
                CheckFile = "Qwen3-1.7B-Q8_0.gguf",
            },
            new()
            {
                Id = "model-hymt2-18b",
                Name = "Hy-MT2-1.8B（1.06GB）",
                Description = "专用翻译模型，质量最稳，无繁体风险。官方无此量化版，由本项目自行量化发布。",
                Kind = "model",
                SizeBytes = 1060L * 1024 * 1024,
                Url = $"{RepoBase}/Hy-MT2-1.8B-Q4_K_M.gguf",
                MirrorUrl = $"https://ghfast.top/{RepoBase}/Hy-MT2-1.8B-Q4_K_M.gguf",
                Target = "models",
                ArchiveKind = "none",
                CheckFile = "Hy-MT2-1.8B-Q4_K_M.gguf",
            },
        },
    };
}
