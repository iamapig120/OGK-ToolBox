using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Infrastructure.Audio;

public sealed class VgmstreamAudioPreviewService(string executablePath) : IAudioPreviewService, IDisposable
{
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, DecodeOperation> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Process> _processes = new();
    private bool _disposed;

    public bool IsAvailable => File.Exists(executablePath);

    public async Task<string> DecodeToWaveAsync(AudioReference audio, string cacheDirectory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable) throw new FileNotFoundException("vgmstream-cli is unavailable.", executablePath);
        var inputPath = !string.IsNullOrWhiteSpace(audio.AwbPath) && File.Exists(audio.AwbPath)
            ? audio.AwbPath
            : audio.AcbPath;
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            throw new FileNotFoundException("The selected audio source is unavailable.", inputPath);

        Directory.CreateDirectory(cacheDirectory);
        var info = new FileInfo(inputPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(inputPath)}|{info.LastWriteTimeUtc.Ticks}|{info.Length}")));
        var outputPath = Path.Combine(cacheDirectory, key + ".wav");
        if (IsValidWave(outputPath)) return outputPath;

        var createdCancellation = new CancellationTokenSource();
        var created = new DecodeOperation(createdCancellation, new Lazy<Task<string>>(
            () => DecodeCoreAsync(inputPath, outputPath, createdCancellation.Token),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var operation = _inflight.GetOrAdd(key, created);
        if (!ReferenceEquals(operation, created)) createdCancellation.Dispose();
        Interlocked.Increment(ref operation.Waiters);
        var task = operation.Task.Value;
        if (Interlocked.Exchange(ref operation.CleanupAttached, 1) == 0)
            _ = task.ContinueWith(_ =>
            {
                _inflight.TryRemove(new KeyValuePair<string, DecodeOperation>(key, operation));
                operation.Cancellation.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await task.WaitAsync(cancellationToken); }
        finally
        {
            if (Interlocked.Decrement(ref operation.Waiters) == 0 && !task.IsCompleted)
                operation.Cancellation.Cancel();
        }
    }

    private async Task<string> DecodeCoreAsync(string inputPath, string outputPath, CancellationToken operationToken)
    {
        var temporaryPath = outputPath + $".{Guid.NewGuid():N}.tmp.wav";
        using var timeout = new CancellationTokenSource(DecodeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken, timeout.Token);
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(temporaryPath);
        start.ArgumentList.Add(inputPath);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start vgmstream-cli.");
        _processes[process.Id] = process;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                Kill(process);
                if (timeout.IsCancellationRequested && !operationToken.IsCancellationRequested)
                    throw new TimeoutException($"Audio decoding exceeded {DecodeTimeout.TotalSeconds:0} seconds.");
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0 || !IsValidWave(temporaryPath))
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidDataException($"Audio decoding failed ({process.ExitCode}): {Limit(detail.Trim(), 8192)}");
            }
            File.Move(temporaryPath, outputPath, true);
            return outputPath;
        }
        finally
        {
            _processes.TryRemove(process.Id, out _);
            if (!process.HasExited) Kill(process);
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsValidWave(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 44) return false;
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == header.Length
            && header[..4].SequenceEqual("RIFF"u8)
            && header[8..].SequenceEqual("WAVE"u8);
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var operation in _inflight.Values) operation.Cancellation.Cancel();
        foreach (var process in _processes.Values) Kill(process);
        _processes.Clear();
    }

    private sealed class DecodeOperation(CancellationTokenSource cancellation, Lazy<Task<string>> task)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Lazy<Task<string>> Task { get; } = task;
        public int Waiters;
        public int CleanupAttached;
    }
}
