using System.Collections.Concurrent;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;

namespace OGKToolBox.Application.Services;

public sealed class LibraryApplicationService(
    IInstallationRegistry installations,
    IOpaqueIdService ids,
    ILibraryScanner scanner,
    ILibraryIndex index,
    ILocalFileSystem files) : ILibraryApplicationService
{
    private readonly ConcurrentDictionary<string, ScanJob> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<LibrarySnapshot?>>> _snapshots = new(StringComparer.Ordinal);

    public Task<ScanJobStatus> StartScanAsync(string installationId, CancellationToken cancellationToken)
    {
        var installation = installations.GetRequired(installationId);
        var active = _jobs.Values.FirstOrDefault(job => job.InstallationId == installationId &&
            job.State is ScanJobState.Pending or ScanJobState.Running);
        if (active is not null) return Task.FromResult(active.Snapshot());

        var job = new ScanJob(Guid.NewGuid().ToString("N"), installationId, installation.Installation.RootPath);
        _jobs[job.Id] = job;
        _ = RunScanAsync(job, installation.Installation);
        return Task.FromResult(job.Snapshot());
    }

    public ScanJobStatus GetScan(string jobId) => _jobs.TryGetValue(jobId, out var job)
        ? job.Snapshot()
        : throw new KeyNotFoundException("The scan job is unavailable.");

    public bool CancelScan(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        job.Cancellation.Cancel();
        return true;
    }

    public async Task<LibrarySummary> GetSummaryAsync(string installationId, CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        
        // 优先使用快速计数，避免加载完整快照
        var counts = await index.GetCountsAsync(session.Installation, cancellationToken);
        if (counts is null) 
            return new(installationId, 0, 0, 0, 0, 0, "Unknown", null);
        
        var lastScan = files.GetLastWriteTimeUtc(session.Installation.IndexPath);
        
        // ICF controls the AM Daemon version value; Option changes only affect its release suffix.
        var sourceCurrent = await index.IsSourceCurrentAsync(session.Installation, cancellationToken);
        var icfCurrent = await index.IsAmfsCurrentAsync(session.Installation, cancellationToken);
        
        if (!sourceCurrent || !icfCurrent)
        {
            // Option 或 AMFS 有变化，需要加载快照更新
            var snapshot = await LoadSnapshotAsync(session, cancellationToken);
            if (snapshot is not null)
            {
                // Option 变化：重新扫描 Option 获取新的版本字母
                if (!sourceCurrent)
                {
                    snapshot = await scanner.ScanOptionAsync(session.Installation, snapshot, null, cancellationToken);
                }
                
                _snapshots[session.Id] = CompletedSnapshot(snapshot);
                
                return new(installationId, snapshot.EffectiveMusic.Count, snapshot.Cards.Count, 
                    snapshot.Characters.Count, snapshot.Resources.Count,
                    snapshot.Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error),
                    (snapshot.GameVersion ?? GameVersionInfo.FromDataPackages(snapshot.Packages)).Display, lastScan);
            }
        }
        
        // 使用快速计数返回摘要
        var (musicCount, cardCount, characterCount, resourceCount, diagnosticCount) = counts.Value;
        
        // 从缓存读取版本号（不加载完整快照）
        string gameVersion = "Unknown";
        try
        {
            var snapshot = await LoadSnapshotAsync(session, cancellationToken);
            if (snapshot?.GameVersion is not null)
            {
                gameVersion = snapshot.GameVersion.Display;
            }
            else if (snapshot is not null)
            {
                // 如果缓存中没有版本信息，使用数据包版本
                gameVersion = GameVersionInfo.FromDataPackages(snapshot.Packages).Display;
            }
        }
        catch
        {
            // 读取失败不影响返回
        }
        
        return new(installationId, musicCount, cardCount, characterCount, resourceCount, diagnosticCount,
            gameVersion, lastScan);
    }

    public async Task<LibraryPage<Music>> QueryMusicAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken) =>
        Map(await index.QueryMusicAsync(installations.GetRequired(installationId).Installation, query, cancellationToken),
            item => ids.Create("music", installationId, item.Id));

    public async Task<LibraryPage<Card>> QueryCardsAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken) =>
        Map(await index.QueryCardsAsync(installations.GetRequired(installationId).Installation, query, cancellationToken),
            item => ids.Create("card", installationId, item.Id));

    public async Task<LibraryPage<Character>> QueryCharactersAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken) =>
        Map(await index.QueryCharactersAsync(installations.GetRequired(installationId).Installation, query, cancellationToken),
            item => ids.Create("character", installationId, item.Id));

    public async Task<LibraryPage<GameResource>> QueryResourcesAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken) =>
        Map(await index.QueryResourcesAsync(installations.GetRequired(installationId).Installation, query, cancellationToken),
            item => ids.Create("resource", installationId, item.Id));

    public async Task<LibraryPage<LibraryDiagnostic>> QueryDiagnosticsAsync(string installationId, LibraryQuery query, CancellationToken cancellationToken) =>
        Map(await index.QueryDiagnosticsAsync(installations.GetRequired(installationId).Installation, query, cancellationToken),
            item => ids.Create("diagnostic", installationId, item.Id));

    public async Task<LibraryFacets> GetFacetsAsync(string installationId, CancellationToken cancellationToken)
    {
        var session = installations.GetRequired(installationId);
        var snapshot = await LoadSnapshotAsync(session, cancellationToken) ?? LibrarySnapshot.Empty(session.Installation);
        return new(
            Facets(snapshot.EffectiveMusic.Select(item => item.Genre)),
            Facets(snapshot.Packages.Select(item => item.Id)),
            Facets(snapshot.Cards.Select(item => item.Rarity)),
            Facets(snapshot.Cards.Select(item => item.Attribute)),
            Facets(snapshot.Resources.Select(item => item.Kind.ToString())),
            Facets(snapshot.Diagnostics.Select(item => item.Severity.ToString())));
    }

    public async Task<Music> GetMusicAsync(string installationId, string musicId, CancellationToken cancellationToken)
    {
        var logical = RequireId(ids.Read(musicId, "music"), installationId);
        var session = installations.GetRequired(installationId);
        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        return snapshot?.EffectiveMusic.FirstOrDefault(item => item.Id.ToString() == logical)
            ?? throw new KeyNotFoundException("The selected music item is unavailable.");
    }

    public async Task<GameResource> GetResourceAsync(string installationId, string resourceId, CancellationToken cancellationToken)
    {
        var logical = RequireId(ids.Read(resourceId, "resource"), installationId);
        var separator = logical.IndexOf('\u001f');
        if (separator < 0) throw new KeyNotFoundException("The selected resource is unavailable.");
        var key = logical[..separator];
        var package = logical[(separator + 1)..];
        var session = installations.GetRequired(installationId);
        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        return snapshot?.Resources.FirstOrDefault(item => item.Key == key && item.Origin.PackageId == package)
            ?? throw new KeyNotFoundException("The selected resource is unavailable.");
    }

    public async Task<Card> GetCardAsync(string installationId, string cardId, CancellationToken cancellationToken)
    {
        var logical = RequireId(ids.Read(cardId, "card"), installationId);
        var session = installations.GetRequired(installationId);
        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        return snapshot?.Cards.FirstOrDefault(item => item.Id.ToString() == logical)
            ?? throw new KeyNotFoundException("The selected card is unavailable.");
    }

    public async Task<Character> GetCharacterAsync(string installationId, string characterId,
        CancellationToken cancellationToken)
    {
        var logical = RequireId(ids.Read(characterId, "character"), installationId);
        var session = installations.GetRequired(installationId);
        var snapshot = await LoadSnapshotAsync(session, cancellationToken);
        return snapshot?.Characters.FirstOrDefault(item => item.Id.ToString() == logical)
            ?? throw new KeyNotFoundException("The selected character is unavailable.");
    }

    private async Task RunScanAsync(ScanJob job, GameInstallation installation)
    {
        job.State = ScanJobState.Running;
        try
        {
            var progress = new Progress<ScanProgress>(value => job.Update(value));
            var snapshot = await scanner.ScanAsync(installation, progress, job.Cancellation.Token);
            
            _snapshots[job.InstallationId] = CompletedSnapshot(snapshot);
            job.State = ScanJobState.Completed;
        }
        catch (OperationCanceledException)
        {
            job.State = ScanJobState.Cancelled;
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.State = ScanJobState.Failed;
        }
        finally { job.FinishedAt = DateTimeOffset.UtcNow; }
    }

    private static string RequireId(IReadOnlyList<string> values, string installationId)
    {
        if (values.Count != 2 || values[0] != installationId)
            throw new KeyNotFoundException("The requested item does not belong to this installation.");
        return values[1];
    }

    private async Task<LibrarySnapshot?> LoadSnapshotAsync(
        InstallationSession session,
        CancellationToken cancellationToken)
    {
        var lazy = _snapshots.GetOrAdd(session.Id, _ => new Lazy<Task<LibrarySnapshot?>>(
            () => scanner.LoadCachedAsync(session.Installation, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            if (lazy.IsValueCreated && lazy.Value.IsFaulted)
                _snapshots.TryRemove(new KeyValuePair<string, Lazy<Task<LibrarySnapshot?>>>(session.Id, lazy));
            throw;
        }
    }

    private static Lazy<Task<LibrarySnapshot?>> CompletedSnapshot(LibrarySnapshot snapshot) =>
        new(() => Task.FromResult<LibrarySnapshot?>(snapshot), LazyThreadSafetyMode.ExecutionAndPublication);


    private static LibraryPage<T> Map<T>(LibraryPage<T> page, Func<IndexedItem<T>, string> createId) =>
        new(page.Items.Select(item => new IndexedItem<T>(createId(item), item.Value)).ToArray(),
            page.Total, page.Offset, page.Limit);

    private static IReadOnlyList<FacetValue> Facets(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
        .Select(group => new FacetValue(group.Key, group.Count()))
        .OrderByDescending(item => item.Count).ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed class ScanJob(string id, string installationId, string rootPath)
    {
        private readonly object _gate = new();
        public string Id { get; } = id;
        public string InstallationId { get; } = installationId;
        public string RootPath { get; } = rootPath;
        public CancellationTokenSource Cancellation { get; } = new();
        public ScanJobState State { get; set; } = ScanJobState.Pending;
        public string Phase { get; private set; } = "Pending";
        public int Completed { get; private set; }
        public int Total { get; private set; }
        public string? CurrentItem { get; private set; }
        public int OverallCompleted { get; private set; }
        public int OverallTotal { get; private set; }
        public string? Error { get; set; }
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }

        public void Update(ScanProgress progress)
        {
            lock (_gate)
            {
                Phase = progress.Phase;
                Completed = progress.Completed;
                Total = progress.Total;
                CurrentItem = progress.CurrentPath is null
                    ? null
                    : Path.GetRelativePath(RootPath, progress.CurrentPath);
                OverallCompleted = progress.OverallCompleted;
                OverallTotal = progress.OverallTotal;
            }
        }

        public ScanJobStatus Snapshot()
        {
            lock (_gate) return new(Id, InstallationId, State, Phase, Completed, Total, CurrentItem,
                Error, CreatedAt, FinishedAt, OverallCompleted, OverallTotal);
        }
    }
}
