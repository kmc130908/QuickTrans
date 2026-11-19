using System.IO;
using System.IO.Pipes;
using System.Text;

namespace QuickTrans.Services;

/// <summary>
/// 主进程与设置子进程之间的轻量本地通信（Named Pipe）。
/// 把设置页放到独立子进程运行：关掉设置页后进程直接退出，系统注入的输入法（IME）、
/// D3D11/显卡驱动和语音库占用的几十 MB 内存会立即完全归还给操作系统。
/// </summary>
public sealed class AppIpcServer : IDisposable
{
    public const string PipeName = "QuickTrans_Ipc_v1";

    private readonly Func<string, Task<string>> _handler;
    private readonly CancellationTokenSource _cts = new();

    public AppIpcServer(Func<string, Task<string>> handler)
    {
        _handler = handler;
        _ = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token);

                var conn = server;
                server = null; // 交给后台任务释放
                _ = Task.Run(() => HandleClientAsync(conn));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                try { await Task.Delay(200, _cts.Token); } catch { break; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await using (pipe)
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(_cts.Token);
                if (string.IsNullOrEmpty(line)) return;

                var reply = await _handler(line);
                // 单行协议：把返回内容里的换行转义，避免截断
                await writer.WriteLineAsync(Encode(reply));
            }
        }
        catch { /* 客户端提前断开或主进程正在退出 */ }
    }

    public static string Encode(string s) =>
        s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    public static string Decode(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 'r' => '\r', '\\' => '\\', _ => s[i] });
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}

public static class AppIpcClient
{
    /// <summary>向正在运行的主进程发一条指令并等待回复。主进程不存在或超时返回 null。</summary>
    public static async Task<string?> SendAsync(string command, int connectTimeoutMs = 1500, CancellationToken ct = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", AppIpcServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(connectTimeoutMs, ct);

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            await writer.WriteLineAsync(command);
            var reply = await reader.ReadLineAsync(ct);
            return reply == null ? null : AppIpcServer.Decode(reply);
        }
        catch
        {
            return null;
        }
    }
}
