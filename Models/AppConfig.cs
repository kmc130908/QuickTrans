using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickTrans.Models;

/// <summary>配置保存在 exe 同目录 config.json，整个软件目录可以直接搬走。</summary>
public sealed class AppConfig
{
    /// <summary>全局热键，例如 "Ctrl+Q"、"Alt+T"。</summary>
    public string Hotkey { get; set; } = "Ctrl+Q";

    /// <summary>截图 OCR 热键。</summary>
    public string OcrHotkey { get; set; } = "Alt+S";

    /// <summary>翻译通道：local = 本地模型，api = 自定义在线接口。</summary>
    public string Engine { get; set; } = "local";

    /// <summary>当前选中的本地模型文件名（models 目录下的 .gguf）。</summary>
    public string SelectedModel { get; set; } = "";

    /// <summary>卸载到 GPU 的层数，999 表示全部。</summary>
    public int GpuLayers { get; set; } = 999;

    /// <summary>是否用 GPU 推理。关闭后纯 CPU 跑，不占显存但慢很多。</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>上下文长度。划词场景 4096 足够，开大会多吃显存。</summary>
    public int ContextSize { get; set; } = 4096;

    /// <summary>模型卸载策略：never = 常驻，onPopupClose = 浮窗关闭即卸载，afterMinutes = 闲置 N 分钟后卸载。</summary>
    public string UnloadPolicy { get; set; } = "never";

    /// <summary>闲置卸载的分钟数，0.5 为最小单位。</summary>
    public double UnloadMinutes { get; set; } = 5.0;

    /// <summary>自定义接口的 Base URL，例如 https://api.deepseek.com/v1</summary>
    /// <summary>在线接口类型：openai / baidu / youdao / google。</summary>
    public string ApiType { get; set; } = "openai";

    public string ApiBaseUrl { get; set; } = "";

    /// <summary>保存的多套接口配置，切换时把选中的一套写进上面的字段。</summary>
    public List<ApiProfile> ApiProfiles { get; set; } = new();

    /// <summary>当前选中的配置名，空表示还没保存过任何一套。</summary>
    public string ActiveProfile { get; set; } = "";

    public string ApiKey { get; set; } = "";

    public string ApiModel { get; set; } = "";

    /// <summary>默认目标语言。</summary>
    public string TargetLanguage { get; set; } = "简体中文";

    /// <summary>开启后：选中中文译成英语，选中其他语言译成 TargetLanguage。</summary>
    public bool AutoDirection { get; set; } = true;

    /// <summary>浮窗上快速切换的语言按钮。</summary>
    public List<string> QuickLanguages { get; set; } = new() { "简体中文", "英语", "日语", "韩语" };

    public double PopupWidth { get; set; } = 440;

    /// <summary>切到其它窗口后是否自动关闭浮窗。</summary>
    public bool PopupAutoClose { get; set; } = true;

    /// <summary>切走窗口后自动关闭前等待的秒数。0 表示切走立即关闭。</summary>
    public int PopupAutoCloseSeconds { get; set; } = 5;

    /// <summary>切走窗口后，鼠标仍停在浮窗上时是否暂停倒计时（true=悬停暂停，false=严格按秒数关）。</summary>
    public bool PopupAutoClosePauseOnHover { get; set; } = true;

    /// <summary>定时自动关闭：不管切不切窗口，浮窗显示指定秒数后自动关闭。</summary>
    public bool PopupTimedCloseEnabled { get; set; } = false;

    /// <summary>定时自动关闭的秒数。</summary>
    public int PopupTimedCloseSeconds { get; set; } = 30;

    /// <summary>托盘图标样式，1-4 对应 assets 里的四套图标。</summary>
    public int TrayIconStyle { get; set; } = 1;

    /// <summary>是否已提示过"缺组件去下载"，避免每次启动都弹窗。</summary>
    public bool ComponentHintShown { get; set; }

    /// <summary>手动启动（非开机自启）时是否自动弹出设置页，让用户确认软件已启动。</summary>
    public bool ShowSettingsOnStartup { get; set; } = true;

    /// <summary>是否已看过首次使用引导。</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>是否随系统启动。</summary>
    public bool AutoStart { get; set; }

    // ---------- 朗读 ----------

    /// <summary>是否在浮窗上显示朗读按钮。</summary>
    public bool TtsEnabled { get; set; } = true;

    /// <summary>中文音色（系统语音的显示名，如 Microsoft Huihui）。</summary>
    public string TtsVoiceZh { get; set; } = "Microsoft Huihui";

    /// <summary>英文音色。</summary>
    public string TtsVoiceEn { get; set; } = "Microsoft Zira";

    /// <summary>语速百分比，-50 到 100，0 为常速。</summary>
    public int TtsRatePercent { get; set; }

    /// <summary>翻窗关闭时是否停止朗读。</summary>
    public bool TtsStopOnClose { get; set; } = true;

    /// <summary>使用哪套引擎目录：gpu 对应 engine，cpu 对应 engine-cpu。</summary>
    public string EngineVariant { get; set; } = "gpu";

    /// <summary>浮窗不透明度，1.0 为完全不透明。</summary>
    public double PopupOpacity { get; set; } = 1.0;

    /// <summary>取词方式：auto / uia / clipboard。</summary>
    public string GrabMode { get; set; } = "auto";

    /// <summary>启动时预加载上次用的模型。</summary>
    public bool PrewarmModel { get; set; } = true;

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg != null)
                {
                    if (cfg.QuickLanguages == null || cfg.QuickLanguages.Count == 0)
                        cfg.QuickLanguages = new List<string> { "简体中文", "英语", "日语", "韩语" };
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时用默认值启动，不把用户挡在门外
        }

        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 目录只读等情况下静默失败
        }
    }

    /// <summary>深拷贝一份配置，用于"点取消时恢复到改动前"。</summary>
    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
    }

    /// <summary>把另一份配置的字段值全部覆盖到当前实例上（保持引用不变）。</summary>
    public void CopyFrom(AppConfig source)
    {
        foreach (var prop in typeof(AppConfig).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            try { prop.SetValue(this, prop.GetValue(source)); }
            catch { /* 个别只读计算属性跳过 */ }
        }
    }
}

/// <summary>一套在线接口配置。ApiKey 对百度是「appid:密钥」，对有道是「appKey:appSecret」。</summary>
public sealed class ApiProfile
{
    public string Name { get; set; } = "新接口";
    public string Type { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    public ApiProfile Clone() => new()
    {
        Name = Name,
        Type = Type,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
    };
}
