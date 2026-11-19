using QuickTrans.Models;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace QuickTrans.Services;

/// <summary>
/// 朗读服务，只用系统自带的语音（SAPI / OneCore）。
/// 完全离线，不依赖网络，MediaPlayer 有线程亲和性，必须在 UI 线程创建。
/// </summary>
public sealed class SpeechService : IDisposable
{
    private SpeechSynthesizer? _synth;
    private MediaPlayer? _player;
    private MediaSource? _source;
    private SpeechSynthesisStream? _stream;
    private readonly object _gate = new();

    // 每次 Stop/新的朗读都会递增。合成是异步的，合成期间用户点了停止或关了浮窗，
    // 合成回来后要能识别出“这次朗读已作废”，不能再开始播放。
    private int _version;
    private volatile bool _synthesizing;

    /// <summary>朗读结束（播放完或手动停止）时触发，供界面恢复按钮状态。可能在非 UI 线程触发。</summary>
    public event Action? Stopped;

    public bool IsSpeaking =>
        _synthesizing || _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    private void EnsurePlayer()
    {
        if (_player != null) return;
        _player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
        _player.MediaEnded += (_, _) =>
        {
            // 播放完立刻释放音频流，别等到下次朗读；放到线程池里做，避免在播放器自己的回调里改 Source
            var v = _version;
            Task.Run(() =>
            {
                if (v != _version) return;   // 期间已经开始了新的朗读，不要动它的资源
                ReleaseMedia();
                Stopped?.Invoke();
            });
        };
        _player.MediaFailed += (_, _) =>
        {
            Task.Run(() =>
            {
                ReleaseMedia();
                Stopped?.Invoke();
            });
        };
    }

    /// <summary>
    /// 朗读一段文本。lang 传 "zh" 或 "en"，用来挑对应语言的音色。
    /// </summary>
    public async Task SpeakAsync(string text, string lang, AppConfig cfg, CancellationToken ct = default)
    {
        // 这里的停止只是为新朗读腾位置，不通知界面——否则刚点亮的喇叭按钮会被立刻熄灭
        var version = StopCore();

        if (string.IsNullOrWhiteSpace(text))
        {
            Stopped?.Invoke();
            return;
        }

        _synth ??= new SpeechSynthesizer();

        var configured = string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase)
            ? cfg.TtsVoiceZh
            : cfg.TtsVoiceEn;

        // 优先用用户选的音色；找不到就按语言挑一个；再不行用系统默认
        var voice = SpeechSynthesizer.AllVoices
                        .FirstOrDefault(v => string.Equals(v.DisplayName, configured, StringComparison.OrdinalIgnoreCase))
                    ?? SpeechSynthesizer.AllVoices
                        .FirstOrDefault(v => v.Language.StartsWith(lang, StringComparison.OrdinalIgnoreCase))
                    ?? SpeechSynthesizer.DefaultVoice;

        if (voice == null)
            throw new InvalidOperationException("系统里没有可用的语音包，请在 Windows 设置的「语音」里添加");

        _synth.Voice = voice;

        // SpeakingRate：1.0 是常速，范围约 0.5–3.0
        _synth.Options.SpeakingRate = Math.Clamp(1.0 + cfg.TtsRatePercent / 100.0, 0.5, 3.0);

        SpeechSynthesisStream stream;
        _synthesizing = true;
        try
        {
            stream = await _synth.SynthesizeTextToStreamAsync(text);
        }
        finally
        {
            _synthesizing = false;
        }

        // 合成期间被停止（或被新的朗读取代）：丢掉这段音频，不播放
        if (version != _version)
        {
            try { stream.Dispose(); } catch { }
            return;
        }

        EnsurePlayer();
        lock (_gate)
        {
            _stream = stream;
            _source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player!.Source = _source;
        }
        _player!.Play();
    }

    /// <summary>停止朗读并通知界面。</summary>
    public void Stop()
    {
        StopCore();
        Stopped?.Invoke();
    }

    /// <summary>停止播放并释放音频资源，返回新的版本号。</summary>
    private int StopCore()
    {
        var v = Interlocked.Increment(ref _version);
        try
        {
            _player?.Pause();
            // 清掉源，彻底停止：只 Pause 的话下次 Play 会从暂停点继续，
            // 而且关闭窗口后 MediaPlayer 可能还在后台持有音频资源。
            ReleaseMedia();
        }
        catch { /* 播放器还没创建 */ }
        return v;
    }

    private void ReleaseMedia()
    {
        lock (_gate)
        {
            try { if (_player != null) _player.Source = null; } catch { }
            try { _source?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            _source = null;
            _stream = null;
        }
    }
    /// <summary>系统里的全部语音，供设置页按语言分组展示。</summary>
    public static IReadOnlyList<(string Name, string Language)> GetSystemVoices() =>
        SpeechSynthesizer.AllVoices
            .Select(v => (v.DisplayName, v.Language))
            .OrderBy(v => v.Language)
            .ThenBy(v => v.DisplayName)
            .ToList();

    public void Dispose()
    {
        Interlocked.Increment(ref _version);
        ReleaseMedia();
        try { _player?.Dispose(); } catch { }
        try { _synth?.Dispose(); } catch { }
        _player = null;
        _synth = null;
    }
}
