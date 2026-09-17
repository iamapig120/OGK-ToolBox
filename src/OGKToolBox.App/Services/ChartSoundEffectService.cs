using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OGKToolBox.Core.Models;

namespace OGKToolBox.App.Services;

public sealed class ChartSoundEffectService : IDisposable
{
    private static readonly IReadOnlyDictionary<ChartPreviewSoundKind, string> FileByKind =
        new Dictionary<ChartPreviewSoundKind, string>
        {
            [ChartPreviewSoundKind.Tap] = "se_tap.wav",
            [ChartPreviewSoundKind.CriticalTap] = "se_extap.wav",
            [ChartPreviewSoundKind.Wall] = "se_wall.wav",
            [ChartPreviewSoundKind.CriticalWall] = "se_exwall.wav",
            [ChartPreviewSoundKind.HoldLoop] = "se_hold.wav",
            [ChartPreviewSoundKind.HoldEnd] = "se_hold_end.wav",
            [ChartPreviewSoundKind.Flick] = "se_flick.wav",
            [ChartPreviewSoundKind.CriticalFlick] = "se_cr_flick.wav",
            [ChartPreviewSoundKind.Bell] = "se_bell.wav",
            [ChartPreviewSoundKind.BeamNotice] = "se_beam_notice.wav",
            [ChartPreviewSoundKind.BeamShot] = "se_beam_shot.wav"
        };

    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private readonly object _gate = new();
    private readonly Dictionary<ChartPreviewSoundKind, CachedSound> _sounds = [];
    private readonly Dictionary<string, FadeInOutSampleProvider> _loops = [];
    private readonly HashSet<FadeInOutSampleProvider> _voices = [];
    private MixingSampleProvider? _mixer;
    private WaveOutEvent? _output;
    private bool _disposed;

    public bool IsReady => _sounds.Count == FileByKind.Count && _output is not null;

    public Task<bool> PrepareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_output is not null) return Task.FromResult(IsReady);

            var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "chart");
            foreach (var pair in FileByKind)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(assetsPath, pair.Value);
                if (File.Exists(path)) _sounds[pair.Key] = LoadSound(path);
            }

            if (_sounds.Count == 0) return Task.FromResult(false);
            _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            _output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 3 };
            _output.Init(_mixer);
            _output.Play();
            return Task.FromResult(IsReady);
        }
    }

    public void Play(ChartPreviewSoundKind kind)
    {
        lock (_gate)
        {
            if (_mixer is null || !_sounds.TryGetValue(kind, out var sound)) return;
            var source = new CachedSoundSampleProvider(sound, loopStartFrame: null, VolumeFor(kind));
            var voice = new FadeInOutSampleProvider(source, true);
            voice.BeginFadeIn(3);
            _voices.Add(voice);
            _mixer.AddMixerInput(voice);
            ScheduleRemoval(voice, sound.Duration + TimeSpan.FromMilliseconds(20));
        }
    }

    public void StartLoop(string voiceId, ChartPreviewSoundKind kind)
    {
        lock (_gate)
        {
            StopLoopCore(voiceId);
            if (_mixer is null || !_sounds.TryGetValue(kind, out var sound)) return;
            var loopStartFrame = Math.Clamp(
                (int)Math.Round(LoopStartFor(kind).TotalSeconds * MixFormat.SampleRate), 0, sound.FrameCount - 1);
            var source = new CachedSoundSampleProvider(sound, loopStartFrame, VolumeFor(kind));
            var voice = new FadeInOutSampleProvider(source, true);
            voice.BeginFadeIn(8);
            _loops[voiceId] = voice;
            _voices.Add(voice);
            _mixer.AddMixerInput(voice);
        }
    }

    public void StopLoop(string voiceId)
    {
        lock (_gate) StopLoopCore(voiceId);
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var voice in _voices.ToArray())
            {
                voice.BeginFadeOut(8);
                ScheduleRemoval(voice, TimeSpan.FromMilliseconds(12));
            }
            _loops.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _mixer = null;
            _sounds.Clear();
            _voices.Clear();
            _loops.Clear();
        }
    }

    private void StopLoopCore(string voiceId)
    {
        if (!_loops.Remove(voiceId, out var voice)) return;
        voice.BeginFadeOut(8);
        ScheduleRemoval(voice, TimeSpan.FromMilliseconds(12));
    }

    private async void ScheduleRemoval(FadeInOutSampleProvider voice, TimeSpan delay)
    {
        await Task.Delay(delay);
        lock (_gate)
        {
            if (!_voices.Remove(voice)) return;
            _mixer?.RemoveMixerInput(voice);
        }
    }

    private static CachedSound LoadSound(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels == 1) provider = new MonoToStereoSampleProvider(provider);
        else if (provider.WaveFormat.Channels != 2)
            throw new InvalidDataException($"不支持 {provider.WaveFormat.Channels} 声道的谱面音效：{path}");
        if (provider.WaveFormat.SampleRate != MixFormat.SampleRate)
            provider = new WdlResamplingSampleProvider(provider, MixFormat.SampleRate);

        var samples = new List<float>();
        var buffer = new float[MixFormat.SampleRate * MixFormat.Channels];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        return new CachedSound(samples.ToArray());
    }

    private static float VolumeFor(ChartPreviewSoundKind kind) => kind switch
    {
        ChartPreviewSoundKind.BeamNotice => .30f,
        ChartPreviewSoundKind.BeamShot => .36f,
        ChartPreviewSoundKind.HoldLoop => .34f,
        _ => .52f
    };

    private static TimeSpan LoopStartFor(ChartPreviewSoundKind kind) => kind switch
    {
        ChartPreviewSoundKind.BeamNotice => TimeSpan.FromSeconds(.861),
        ChartPreviewSoundKind.BeamShot => TimeSpan.FromSeconds(1.957),
        ChartPreviewSoundKind.HoldLoop => TimeSpan.Zero,
        _ => TimeSpan.Zero
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record CachedSound(float[] Samples)
    {
        public int FrameCount => Samples.Length / MixFormat.Channels;
        public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)MixFormat.SampleRate);
    }

    private sealed class CachedSoundSampleProvider(
        CachedSound sound, int? loopStartFrame, float volume) : ISampleProvider
    {
        private int _sampleIndex;
        private readonly int? _loopStartSample = loopStartFrame * MixFormat.Channels;
        public WaveFormat WaveFormat => MixFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count)
            {
                var remaining = sound.Samples.Length - _sampleIndex;
                if (remaining <= 0)
                {
                    if (_loopStartSample is null) break;
                    _sampleIndex = _loopStartSample.Value;
                    remaining = sound.Samples.Length - _sampleIndex;
                }
                var copy = Math.Min(count - written, remaining);
                for (var index = 0; index < copy; index++)
                    buffer[offset + written + index] = sound.Samples[_sampleIndex + index] * volume;
                _sampleIndex += copy;
                written += copy;
            }
            return written;
        }
    }
}
