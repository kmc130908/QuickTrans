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

    public bool IsSpeaking => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    private void EnsurePlayer()
    {
        if (_player != null) return;
        _player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech };
    }

    /// <summary>
    /// 朗读一段文本。lang 传 "zh" 或 "en"，用来挑对应语言的音色。
    /// </summary>
    public async Task SpeakAsync(string text, string lang, AppConfig cfg, CancellationToken ct = default)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(text)) return;

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

        var stream = await _synth.SynthesizeTextToStreamAsync(text);

        EnsurePlayer();
        _player!.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Play();
    }

    public void Stop()
    {
        try { _player?.Pause(); } catch { /* 播放器还没创建 */ }
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
        try { _player?.Dispose(); } catch { }
        try { _synth?.Dispose(); } catch { }
        _player = null;
        _synth = null;
    }
}
