using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickTrans.Services;

/// <summary>models 目录或用户手动添加的外部路径里的一个 GGUF 模型。</summary>
public sealed record ModelInfo(
    string FilePath,
    string DisplayName,
    long SizeBytes,
    string Quant,
    bool IsCustomPath = false)
{
    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB"
        : $"{SizeBytes / 1024.0 / 1024:F0} MB";
}

/// <summary>
/// 管理本地的 llama-server 进程。模型就是 models 目录下的裸 .gguf 文件，
/// 用户自己丢进去、自己删掉，软件只负责扫描和加载。
/// </summary>
public sealed class EngineManager : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly StringBuilder _log = new();
    private Process? _proc;
    private int _port = 8771;

    // 同一时刻只允许一个加载流程：预热、热键翻译、设置页“使用”可能同时触发加载，
    // 没有这把锁时后来者会看到“进程已启动”就误以为模型已就绪，直接请求导致失败。
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile bool _loading;
    private volatile bool _ready;

    public string BaseDir { get; }
    public string ModelsDir { get; }
    public string EngineDir { get; private set; }

    public string? LoadedModelPath { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>进程已起来并且健康检查通过，可以直接发请求。</summary>
    public bool IsReady => _ready && IsRunning;

    /// <summary>最后一次被使用的时间，供闲置卸载策略判断。</summary>
    public DateTime? LastUsedAt { get; private set; }

    // 正在翻译的“租约”：当前时间还没超过这个时间点，就认为引擎在忙，闲置卸载不动它。
    // 用时间戳而不是计数器，是为了子进程被强杀、没来得及发结束通知时也能自动过期，
    // 不会把闲置卸载永久卡死。
    private long _busyUntilTick;

    /// <summary>标记一次使用（加载成功或完成翻译）。</summary>
    public void MarkUsed() => LastUsedAt = DateTime.Now;

    /// <summary>开始一段翻译：在租约到期前禁止闲置卸载。</summary>
    public void BeginTranslateLease(int seconds = 900)
    {
        var until = Environment.TickCount64 + Math.Max(10, seconds) * 1000L;
        var prev = Volatile.Read(ref _busyUntilTick);
        if (until > prev) Volatile.Write(ref _busyUntilTick, until);
        MarkUsed();
    }

    /// <summary>本轮翻译结束，解除忙碌租约。</summary>
    public void EndTranslateLease() => Volatile.Write(ref _busyUntilTick, 0);

    /// <summary>闲置时长，未加载或正在翻译时返回 null。</summary>
    public TimeSpan? IdleFor
    {
        get
        {
            if (!IsRunning || _loading || !LastUsedAt.HasValue) return null;
            if (Environment.TickCount64 < Volatile.Read(ref _busyUntilTick)) return null;
            return DateTime.Now - LastUsedAt.Value;
        }
    }
    public int Port => _port;
    public string BaseUrl => $"http://127.0.0.1:{_port}";
    public string LogText => _log.ToString();

    public EngineManager(string baseDir, string engineVariant = "gpu")
    {
        BaseDir = baseDir;
        ModelsDir = Path.Combine(baseDir, "models");
        EngineDir = ResolveEngineDir(baseDir, engineVariant);
        EngineVariant = ResolveVariant(baseDir, engineVariant);
        try { Directory.CreateDirectory(ModelsDir); } catch { /* 只读目录不致命 */ }
    }

    /// <summary>当前使用的引擎版本：gpu 或 cpu。</summary>
    public string EngineVariant { get; private set; }

    /// <summary>把版本名映射成实际目录，目录不存在时退回 engine。</summary>
    private static string ResolveEngineDir(string baseDir, string variant)
    {
        var cpuDir = Path.Combine(baseDir, "engine-cpu");
        if (string.Equals(variant, "cpu", StringComparison.OrdinalIgnoreCase) && Directory.Exists(cpuDir))
            return cpuDir;

        return Path.Combine(baseDir, "engine");
    }

    private static string ResolveVariant(string baseDir, string variant)
    {
        var cpuDir = Path.Combine(baseDir, "engine-cpu");
        return string.Equals(variant, "cpu", StringComparison.OrdinalIgnoreCase) && Directory.Exists(cpuDir)
            ? "cpu"
            : "gpu";
    }

    /// <summary>两套引擎目录各自是否存在，供设置页展示可选项。</summary>
    public static (bool Gpu, bool Cpu) DetectVariants(string baseDir) =>
        (Directory.Exists(Path.Combine(baseDir, "engine")),
         Directory.Exists(Path.Combine(baseDir, "engine-cpu")));

    /// <summary>切换到另一套引擎。调用方需要先保证模型已卸载。</summary>
    public bool SwitchEngine(string variant)
    {
        var resolved = ResolveVariant(BaseDir, variant);
        var dir = ResolveEngineDir(BaseDir, resolved);

        if (string.Equals(dir, EngineDir, StringComparison.OrdinalIgnoreCase)) return false;

        EngineVariant = resolved;
        EngineDir = dir;
        _supportsGpuCache = null;   // 换了目录，GPU 能力要重新判定
        return true;
    }

    /// <summary>扫描 models 目录并合并用户手动添加的外部 .gguf 路径，方便自由管理电脑任意位置的模型。</summary>
    public List<ModelInfo> ScanModels(IEnumerable<string>? extraPaths = null)
    {
        var list = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Directory.Exists(ModelsDir))
            {
                foreach (var f in Directory.GetFiles(ModelsDir, "*.gguf", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var full = Path.GetFullPath(f);
                        if (!seen.Add(full)) continue;
                        var fi = new FileInfo(full);
                        list.Add(new ModelInfo(full, Path.GetFileNameWithoutExtension(fi.Name), fi.Length, GuessQuant(fi.Name), IsCustomPath: false));
                    }
                    catch { /* 单个文件读不到就跳过 */ }
                }
            }
        }
        catch { /* 目录不可访问 */ }

        if (extraPaths != null)
        {
            foreach (var raw in extraPaths)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var full = Path.GetFullPath(raw.Trim());
                    if (!full.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(full)) continue;
                    if (!seen.Add(full)) continue;
                    var fi = new FileInfo(full);
                    list.Add(new ModelInfo(full, Path.GetFileNameWithoutExtension(fi.Name), fi.Length, GuessQuant(fi.Name), IsCustomPath: true));
                }
                catch { }
            }
        }

        return list.OrderByDescending(m => m.SizeBytes).ToList();
    }

    /// <summary>
    /// 按需扫描本机常见本地大模型目录（LM Studio / GPT4All / Jan / llama.cpp / ModelScope / HuggingFace / 下载目录）中的有效 .gguf 文件。
    /// 纯按需执行，零后台轮询与常驻占用。
    /// </summary>
    public static List<string> DiscoverCommonModels(string excludeModelsDir, IEnumerable<string>? existingPaths = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new (string Dir, int MaxDepth)[]
        {
            (Path.Combine(home, ".lmstudio", "models"), 4),
            (Path.Combine(home, ".cache", "lm-studio", "models"), 4),
            (Path.Combine(localApp, "nomic.ai", "GPT4All"), 2),
            (Path.Combine(home, "jan", "models"), 3),
            (Path.Combine(localApp, "llama.cpp"), 2),
            (Path.Combine(home, ".cache", "modelscope", "hub"), 4),
            (Path.Combine(home, ".cache", "huggingface", "hub"), 4),
            (Path.Combine(home, "Downloads"), 1),
        };

        var excludePrefix = "";
        try { excludePrefix = Path.GetFullPath(excludeModelsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
        catch { }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingPaths != null)
        {
            foreach (var p in existingPaths)
            {
                try { if (!string.IsNullOrWhiteSpace(p)) known.Add(Path.GetFullPath(p)); } catch { }
            }
        }

        var found = new List<string>();
        void ScanDir(string dir, int depth)
        {
            if (depth < 0 || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var full = Path.GetFullPath(f);
                        if (!string.IsNullOrEmpty(excludePrefix) && full.StartsWith(excludePrefix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!known.Add(full)) continue;
                        if (ValidateGgufHeader(full).Ok)
                            found.Add(full);
                    }
                    catch { }
                }
            }
            catch { }

            if (depth == 0) return;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        var attr = File.GetAttributes(sub);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue; // 跳过符号链接避免死循环
                        ScanDir(sub, depth - 1);
                    }
                    catch { }
                }
            }
            catch { }
        }

        foreach (var (dir, depth) in roots)
            ScanDir(dir, depth);

        return found;
    }

    private static string GuessQuant(string fileName)
    {
        var n = fileName.ToUpperInvariant();
        if (n.Contains("1.25")) return "1.25-bit";
        if (n.Contains("2BIT") || n.Contains("2-BIT") || n.Contains("Q2_K")) return "2-bit";
        foreach (var q in new[] { "Q8_0", "Q6_K", "Q5_K_M", "Q5_K_S", "Q4_K_M", "Q4_K_S", "Q4_0", "Q3_K_M" })
            if (n.Contains(q)) return q;
        return "未知";
    }

    /// <summary>识别需要腾讯专用内核的极限量化格式，这类文件通用 llama.cpp 加载不了。</summary>
    private static bool IsExoticQuant(string path)
    {
        var n = Path.GetFileName(path).ToUpperInvariant();
        return n.Contains("2BIT") || n.Contains("2-BIT") || n.Contains("1.25") || n.Contains("STQ");
    }

    /// <summary>引擎目录是否完整。</summary>
    public bool EngineReady => File.Exists(Path.Combine(EngineDir, "llama-server.exe"));

    private bool? _supportsGpuCache;

    /// <summary>当前引擎目录是否具备 GPU 推理能力（有 GPU 后端且检测到设备）。</summary>
    public bool SupportsGpu => _supportsGpuCache ??= DetectGpu(EngineDir);

    private static readonly Dictionary<string, (long Stamp, bool Ok, string Backend, string Device)> GpuProbeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] GpuBackendDlls = { "ggml-cuda.dll", "ggml-vulkan.dll", "ggml-rocm.dll" };

    /// <summary>查找引擎目录下的 GPU 后端动态库（CUDA / Vulkan / ROCm），找不到返回 null。</summary>
    private static string? FindGpuBackendDll(string engineDir)
    {
        foreach (var name in GpuBackendDlls)
        {
            var path = Path.Combine(engineDir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>指定引擎目录是否带有 GPU 后端文件（CUDA / Vulkan / ROCm）。</summary>
    public static bool HasGpuBackend(string engineDir) => FindGpuBackendDll(engineDir) != null;

    /// <summary>返回引擎目录内置的 GPU 后端类型：CUDA / Vulkan / ROCm，纯 CPU 版返回空串。</summary>
    public static string GetGpuBackendKind(string engineDir)
    {
        var dll = FindGpuBackendDll(engineDir);
        if (dll == null) return "";
        var name = Path.GetFileName(dll);
        if (name.Contains("cuda", StringComparison.OrdinalIgnoreCase)) return "CUDA";
        if (name.Contains("vulkan", StringComparison.OrdinalIgnoreCase)) return "Vulkan";
        if (name.Contains("rocm", StringComparison.OrdinalIgnoreCase)) return "ROCm";
        return "GPU";
    }

    /// <summary>检测是否真的能用 GPU，用于界面提示。</summary>
    public static bool DetectGpu(string engineDir) => DetectGpuInfo(engineDir).Ok;

    /// <summary>
    /// 检测 GPU 能力并返回主设备名称（如 Vulkan / NVIDIA GeForce RTX 4070 Laptop GPU）。
    /// 结果按“引擎目录 + GPU 后端文件版本”缓存，避免每次打开设置页都拉起 llama-server。
    /// </summary>
    public static (bool Ok, string Backend, string DeviceName) DetectGpuInfo(string engineDir)
    {
        try
        {
            // 先看有没有 GPU 后端文件（CUDA/Vulkan/ROCm），没有就不用费劲起进程了（纯 CPU 版走这条）
            var gpuBackend = FindGpuBackendDll(engineDir);
            if (gpuBackend == null) return (false, "", "");

            var backendKind = GetGpuBackendKind(engineDir);
            var fi = new FileInfo(gpuBackend);
            var stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length;
            lock (GpuProbeCache)
            {
                if (GpuProbeCache.TryGetValue(engineDir, out var hit) && hit.Stamp == stamp)
                    return (hit.Ok, hit.Backend, hit.Device);
            }

            var psi = new ProcessStartInfo(Path.Combine(engineDir, "llama-server.exe"), "--list-devices")
            {
                WorkingDirectory = engineDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, backendKind, "");

            // 先等退出，超时直接杀掉，避免 --list-devices 挂起或异常时留下僵尸进程
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 可能已退出 */ }
                return (false, backendKind, "");
            }

            var output = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
            var device = ParsePrimaryGpuDevice(output, out var detectedBackend);
            var ok = !string.IsNullOrEmpty(detectedBackend);
            var finalBackend = ok ? detectedBackend : backendKind;

            lock (GpuProbeCache) GpuProbeCache[engineDir] = (stamp, ok, finalBackend, device);
            return (ok, finalBackend, device);
        }
        catch
        {
            return (false, "", "");
        }
    }

    private static string ParsePrimaryGpuDevice(string output, out string backend)
    {
        backend = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var tag in new[] { "CUDA", "Vulkan", "ROCm" })
            {
                if (!line.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) continue;
                var colon = line.IndexOf(':');
                if (colon < 0) continue;

                backend = tag;
                var name = line[(colon + 1)..].Trim();
                // 去掉末尾的 "(7948 MiB, 7180 MiB free)" 显存标注，界面展示更清爽
                var paren = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (paren > 0 && name.EndsWith(")", StringComparison.Ordinal))
                    name = name[..paren].Trim();
                return name;
            }
        }
        return "";
    }

    /// <summary>
    /// 基础 GGUF 文件头校验（前 4 字节必须为 "GGUF" 且体积合理）。
    /// 不限制用户自行放置的第三方模型，仅防止损坏/截断/误下成网页的文件导致引擎崩溃。
    /// </summary>
    public static (bool Ok, string Reason) ValidateGgufHeader(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return (false, "模型文件不存在");
            var fi = new FileInfo(filePath);
            if (fi.Length < 64 * 1024)
                return (false, "模型文件体积过小，可能未下载完整或已损坏。");

            Span<byte> magic = stackalloc byte[4];
            using var fs = File.OpenRead(filePath);
            if (fs.Read(magic) < 4
                || magic[0] != (byte)'G'
                || magic[1] != (byte)'G'
                || magic[2] != (byte)'U'
                || magic[3] != (byte)'F')
            {
                return (false, "模型文件格式无效（缺少 GGUF 文件头），可能下载损坏或不是有效的 .gguf 模型。");
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "无法读取模型文件：" + ex.Message);
        }
    }

    /// <summary>加载指定模型。会先卸掉正在跑的那个。</summary>
    public async Task<(bool Ok, string Message)> LoadAsync(string ggufPath, int gpuLayers, int ctx, CancellationToken ct = default)
    {
        if (!EngineReady)
            return (false, "引擎文件缺失，engine 目录下找不到 llama-server.exe");

        var check = ValidateGgufHeader(ggufPath);
        if (!check.Ok)
            return (false, check.Reason);

        // 串行化加载：别的调用方正在加载时在这里排队，等它加载完直接复用结果
        await _loadGate.WaitAsync(ct);
        try
        {
            if (IsReady && string.Equals(LoadedModelPath, ggufPath, StringComparison.OrdinalIgnoreCase))
                return (true, "已加载");

            Unload();
            _log.Clear();

            // 加载期间也算“在用”：否则闲置计时用的是上次使用时间，
            // 闲置卸载后重新加载时，定时器会把还没加载完的进程当成闲置进程杀掉。
            _loading = true;
            MarkUsed();

            // 附加参数：-np 1 只开一个槽位（翻译不需要并发），--cache-ram 0 关闭提示词的内存缓存
            // （实测每次翻译会多留约 20MB，连续使用会一路涨到数百 MB，且对划词翻译没有收益）。
            // 万一用户换了不认识这两个参数的老版引擎，自动去掉后重试一次。
            var useExtra = true;

            // 端口探测和实际使用之间有空窗，别的进程可能抢占；万一冲突就换端口重试。
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var result = await LaunchAndWaitAsync(ggufPath, gpuLayers, ctx, useExtra, ct);
                if (result.Ok) return result;

                if (useExtra && IsUnknownArgument(result.Message))
                {
                    useExtra = false;
                    continue;
                }

                if (attempt < 3 && IsPortInUse(result.Message))
                {
                    _port++;   // 从刚冲突的端口的下一个开始找
                    continue;
                }

                return result;
            }

            return (false, "端口持续被占用，请稍后重试");
        }
        finally
        {
            _loading = false;
            _loadGate.Release();
        }
    }

    private static bool IsUnknownArgument(string message) =>
        message.Contains("invalid argument", StringComparison.OrdinalIgnoreCase)
        || message.Contains("unknown argument", StringComparison.OrdinalIgnoreCase)
        || message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase);

    private static bool IsPortInUse(string message) =>
        message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || message.Contains("address in use", StringComparison.OrdinalIgnoreCase);

    private async Task<(bool Ok, string Message)> LaunchAndWaitAsync(
        string ggufPath, int gpuLayers, int ctx, bool useExtraArgs, CancellationToken ct)
    {
        _port = FindFreePort(_port);
        // 线程数降到物理核数的一半（最少 1）：CPU 推理占满核心时会把 UI 线程
        // 挤掉，导致翻译中浮窗拖不动、按钮点不了、切 CPU 引擎无响应。
        // 配合 Idle 优先级，推理只在空闲时跑，交互全程流畅。
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        // --reasoning off：Qwen3 这类模型默认会先输出一段思考过程，
        // 翻译场景不需要，关掉能让结果更干净也更快。对没有思考模式的模型无副作用。
        var args = $"-m \"{ggufPath}\" --port {_port} -ngl {gpuLayers} -c {ctx} --host 127.0.0.1 -t {threads} --no-webui --reasoning off";
        if (useExtraArgs)
        {
            args += " -np 1 --cache-ram 0";
            // GPU 模式：-sm none 只用主 GPU，避免双显卡笔记本把模型拆到核显拖慢 3 倍；
            // CPU 模式：-dev none 禁用 GPU 后端初始化，实现 Vulkan/CUDA 引擎下 CPU 推理 0MB 显存占用。
            args += gpuLayers > 0 ? " -sm none" : " -dev none";
        }

        var psi = new ProcessStartInfo(Path.Combine(EngineDir, "llama-server.exe"), args)
        {
            WorkingDirectory = EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // CPU 推理时 llama-server 会用 -t 线程数占满核心，把 UI 线程和鼠标
        // 消息处理全挤掉，表现就是「翻译中浮窗拖不动、设置按钮点不了」。
        // 这里让它以低于正常的优先级运行：翻译慢一点点，但界面始终流畅。
        // 对 GPU 引擎无副作用——GPU 推理主要吃显卡，几乎不占用 CPU。
        try
        {
            psi.Environment["LLAMA_SERVER_PRIORITY_LOW"] = "1";
        }
        catch { /* 环境变量操作失败时忽略 */ }

        try
        {
            psi.Environment["PATH"] = EngineDir + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        }
        catch { /* 环境变量操作失败时依赖同目录 dll 解析 */ }

        try
        {
            _proc = Process.Start(psi);

            if (_proc == null)
            {
                AppendLog("error: Process.Start 返回 null");
                return (false, "启动 llama-server 失败");
            }

            // 把 llama-server 优先级降到 Idle（最低）：
            // 本地 CPU 推理会用满所有核心，把 UI 线程和鼠标消息处理全挤掉，
            // 表现就是「翻译中浮窗拖不动、设置按钮点不了、切 CPU 引擎直接无响应」。
            // Idle 优先级让系统只在完全空闲时才跑推理，界面永远流畅。
            // GPU 推理主要吃显卡，几乎不受影响。
            try { _proc.PriorityClass = ProcessPriorityClass.Idle; } catch { }

            // 把 llama-server 绑到 Job 对象：Job 句柄随主进程释放时，
            // 系统会直接杀掉里面的所有进程。这样即使主进程被任务管理器
            // 强杀、崩溃，也不会留下占着显存的孤儿进程。
            if (_proc != null)
            {
                try { AssignProcessToJobObject(JobHandle, _proc.Handle); }
                catch { /* 绑定失败不影响启动，还有启动时的孤儿清理兜底 */ }
            }
            if (_proc == null) return (false, "无法启动推理引擎");

            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            LoadedModelPath = ggufPath;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return (false, "启动失败：" + ex.Message);
        }

        // 等待模型加载完成（大模型可能要十几秒）
        var proc = _proc;
        try
        {
            for (var i = 0; i < 240; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(250, ct);

                // 等待期间被别处卸载了（切换引擎、退出等）：不要再对着空引用或别的进程轮询
                if (proc == null || !ReferenceEquals(_proc, proc))
                {
                    LastError = "加载被中断";
                    return (false, LastError);
                }

                if (proc.HasExited)
                {
                    var tail = Tail(600);
                    var msg = IsExoticQuant(ggufPath)
                        ? "这个模型用的是腾讯 AngelSlim 的极限量化内核，需要专用推理引擎，通用的 llama.cpp 加载不了。\n\n请换用同目录下的 Q4_K_M 版本，速度和质量都更好。"
                        : "引擎异常退出：" + tail;
                    LastError = msg;
                    Unload();
                    return (false, LastError);
                }

                if (await HealthAsync(ct))
                {
                    _ready = true;
                    MarkUsed();
                    return (true, "加载完成");
                }
            }

            LastError = "加载超时";
            Unload();
            return (false, LastError);
        }
        catch
        {
            // 取消或任何异常都要把已启动的引擎杀掉，否则残留进程一直占着显存
            Unload();
            throw;
        }
    }

    private async Task<bool> HealthAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/health", ct);
            if (!resp.IsSuccessStatusCode) return false;
            var text = await resp.Content.ReadAsStringAsync(ct);
            return text.Contains("ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Unload()
    {
        _ready = false;
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                if (!_proc.WaitForExit(3000))
                {
                    // 3 秒还没退，属于极端情况（进程卡在内核态）。记一笔，
                    // 引用照常置空，残留进程会占着旧端口，新进程会另选端口。
                    AppendLog("warning: llama-server 未在 3 秒内退出");
                }
            }
        }
        catch { /* 进程可能已经没了 */ }
        finally
        {
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            LoadedModelPath = null;
        }
    }

    private static int FindFreePort(int start)
    {
        for (var p = start; p < start + 40; p++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { /* 被占用，试下一个 */ }
        }
        return start;
    }

    private void AppendLog(string line)
    {
        lock (_log)
        {
            _log.AppendLine(line);
            if (_log.Length > 24000) _log.Remove(0, _log.Length - 16000);
        }
    }

    private string Tail(int n)
    {
        var s = LogText;
        return s.Length <= n ? s : s[^n..];
    }

    // ---- Job Object：保证 llama-server 随主进程一起退出 ----
    //
    // 不用 Job 的话，主进程被强杀（任务管理器结束任务、崩溃）时 Unload 跑不到，
    // 子进程就成了孤儿，一直占着显存。绑进 Job 后由系统负责回收。

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly IntPtr JobHandle = CreateKillOnCloseJob();

    private static IntPtr CreateKillOnCloseJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return job;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Unload();
        _http.Dispose();
    }

    /// <summary>
    /// 清理上次异常退出遗留的 llama-server。
    ///
    /// 主进程被强杀（任务管理器结束任务、崩溃、开发时 taskkill）时，
    /// OnExit 不会执行，Unload 也就没机会跑，子进程会一直占着显存。
    /// 这里在启动时扫一遍：只杀本软件 engine / engine-cpu 目录下的进程，
    /// 不动用户自己跑的其它 llama.cpp 服务。
    /// </summary>
    public static int KillOrphanServers()
    {
        var killed = 0;

        try
        {
            var roots = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "engine"),
                Path.Combine(AppContext.BaseDirectory, "engine-cpu"),
            };

            foreach (var proc in Process.GetProcessesByName("llama-server"))
            {
                try
                {
                    var exePath = proc.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(exePath)) continue;

                    var mine = roots.Any(r =>
                        exePath.StartsWith(r, StringComparison.OrdinalIgnoreCase));
                    if (!mine) continue;

                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                    killed++;
                }
                catch
                {
                    // 进程可能在枚举过程中自己退了，或者没有权限读 MainModule
                }
                finally
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }

        return killed;
    }
}
