using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;

namespace QuickTrans.Services;

/// <summary>models 目录里的一个 GGUF 模型。</summary>
public sealed record ModelInfo(string FilePath, string DisplayName, long SizeBytes, string Quant)
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

    public string BaseDir { get; }
    public string ModelsDir { get; }
    public string EngineDir { get; private set; }

    public string? LoadedModelPath { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>最后一次被使用的时间，供闲置卸载策略判断。</summary>
    public DateTime? LastUsedAt { get; private set; }

    /// <summary>标记一次使用（加载成功或完成翻译）。</summary>
    public void MarkUsed() => LastUsedAt = DateTime.Now;

    /// <summary>闲置时长，未加载时返回 null。</summary>
    public TimeSpan? IdleFor => IsRunning && LastUsedAt.HasValue
        ? DateTime.Now - LastUsedAt.Value
        : null;
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

    /// <summary>扫描 models 目录。任何 .gguf 文件都会被列出来，方便用户自己扩充。</summary>
    public List<ModelInfo> ScanModels()
    {
        var list = new List<ModelInfo>();
        try
        {
            if (!Directory.Exists(ModelsDir)) return list;
            foreach (var f in Directory.GetFiles(ModelsDir, "*.gguf", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var fi = new FileInfo(f);
                    list.Add(new ModelInfo(f, Path.GetFileNameWithoutExtension(fi.Name), fi.Length, GuessQuant(fi.Name)));
                }
                catch { /* 单个文件读不到就跳过 */ }
            }
        }
        catch { /* 目录不可访问 */ }
        return list.OrderByDescending(m => m.SizeBytes).ToList();
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

    /// <summary>当前引擎目录是否具备 GPU 推理能力（有 CUDA 后端且检测到设备）。</summary>
    public bool SupportsGpu => _supportsGpuCache ??= DetectGpu(EngineDir);

    /// <summary>检测是否真的能用 GPU，用于界面提示。</summary>
    public static bool DetectGpu(string engineDir)
    {
        try
        {
            // 先看有没有 CUDA 后端文件，没有就不用费劲起进程了（CPU 版引擎走这条）
            var cudaBackend = Path.Combine(engineDir, "ggml-cuda.dll");
            if (!File.Exists(cudaBackend)) return false;

            var psi = new ProcessStartInfo(Path.Combine(engineDir, "llama-server.exe"), "--list-devices")
            {
                WorkingDirectory = engineDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;

            // 先等退出，超时直接杀掉，避免 --list-devices 挂起或异常时留下僵尸进程
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 可能已退出 */ }
                return false;
            }

            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            return output.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
                || output.Contains("ROCm", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>加载指定模型。会先卸掉正在跑的那个。</summary>
    public async Task<(bool Ok, string Message)> LoadAsync(string ggufPath, int gpuLayers, int ctx, CancellationToken ct = default)
    {
        if (!EngineReady)
            return (false, "引擎文件缺失，engine 目录下找不到 llama-server.exe");

        if (!File.Exists(ggufPath))
            return (false, "模型文件不存在");

        if (IsRunning && string.Equals(LoadedModelPath, ggufPath, StringComparison.OrdinalIgnoreCase))
            return (true, "已加载");

        Unload();
        _log.Clear();

        // 端口探测和实际使用之间有空窗，别的进程可能抢占；万一冲突就换端口重试。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await LaunchAndWaitAsync(ggufPath, gpuLayers, ctx, ct);
            if (result.Ok) return result;

            if (attempt < 2 && IsPortInUse(result.Message))
            {
                _port++;   // 从刚冲突的端口的下一个开始找
                continue;
            }

            return result;
        }

        return (false, "端口持续被占用，请稍后重试");
    }

    private static bool IsPortInUse(string message) =>
        message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || message.Contains("address in use", StringComparison.OrdinalIgnoreCase);

    private async Task<(bool Ok, string Message)> LaunchAndWaitAsync(string ggufPath, int gpuLayers, int ctx, CancellationToken ct)
    {
        _port = FindFreePort(_port);
        var threads = Math.Max(4, Environment.ProcessorCount / 2);
        // --reasoning off：Qwen3 这类模型默认会先输出一段思考过程，
        // 翻译场景不需要，关掉能让结果更干净也更快。对没有思考模式的模型无副作用。
        var args = $"-m \"{ggufPath}\" --port {_port} -ngl {gpuLayers} -c {ctx} --host 127.0.0.1 -t {threads} --no-webui --reasoning off";

        var psi = new ProcessStartInfo(Path.Combine(EngineDir, "llama-server.exe"), args)
        {
            WorkingDirectory = EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            psi.Environment["PATH"] = EngineDir + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        }
        catch { /* 环境变量操作失败时依赖同目录 dll 解析 */ }

        try
        {
            _proc = Process.Start(psi);
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
        try
        {
            for (var i = 0; i < 240; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(250, ct);

                if (_proc.HasExited)
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

    public void Dispose()
    {
        Unload();
        _http.Dispose();
    }
}
