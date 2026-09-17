using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using OGKToolBox.Application;
using OGKToolBox.Application.Abstractions;
using OGKToolBox.Application.Contracts;
using OGKToolBox.Core.Abstractions;
using OGKToolBox.Core.Models;
using OGKToolBox.Infrastructure;

const string sessionHeader = "X-OGK-Session";
var sessionToken = Environment.GetEnvironmentVariable("OGK_SESSION_TOKEN");
if (string.IsNullOrWhiteSpace(sessionToken) || sessionToken.Length < 32)
    throw new InvalidOperationException("OGK_SESSION_TOKEN must be provided by the Electron host.");
var instanceId = Environment.GetEnvironmentVariable("OGK_INSTANCE_ID") ?? Guid.NewGuid().ToString("N");
var tokenBytes = Encoding.UTF8.GetBytes(sessionToken);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("OGK_API_URL") ?? "http://127.0.0.1:0");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddOGKToolBoxInfrastructure(Path.Combine(AppContext.BaseDirectory, "tools", "vgmstream"));
builder.Services.AddOGKToolBoxApplication(SHA256.HashData(tokenBytes));
var app = builder.Build();

app.Use(async (context, next) =>
{
    if (!context.Request.Headers.TryGetValue(sessionHeader, out var supplied)
        || !FixedEquals(tokenBytes, Encoding.UTF8.GetBytes(supplied.ToString())))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Invalid application session"
        });
        return;
    }
    try { await next(); }
    catch (KeyNotFoundException exception) { await Problem(context, 404, "Item not found", exception.Message); }
    catch (ArgumentException exception) { await Problem(context, 400, "Invalid request", exception.Message); }
    catch (InvalidDataException exception) { await Problem(context, 422, "Content could not be processed", exception.Message); }
    catch (InvalidOperationException exception) { await Problem(context, 409, "Operation could not be completed", exception.Message); }
    catch (IOException) { await Problem(context, 409, "File operation failed", "The target changed or could not be updated safely."); }
    catch (UnauthorizedAccessException) { await Problem(context, 403, "Access denied", "The selected directory is not writable."); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Unhandled API error for {Path}", context.Request.Path);
        await Problem(context, 500, "Unexpected application error", "See the local OGKToolBox log for details.");
    }
});

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("OGKToolBox.Api", instanceId, true)));
app.MapPost("/api/session/shutdown", (IHostApplicationLifetime lifetime) =>
{
    _ = Task.Run(async () => { await Task.Delay(50); lifetime.StopApplication(); });
    return Results.Accepted();
});

app.MapPost("/api/installations", (RegisterInstallationRequest request, IInstallationRegistry registry) =>
{
    var session = registry.Register(request.RootPath);
    return Results.Ok(new InstallationResponse(session.Id, session.DisplayName));
});

app.MapPost("/api/installations/{installationId}/scans",
    async (string installationId, ILibraryApplicationService library, CancellationToken cancellationToken) =>
        Results.Accepted(value: await library.StartScanAsync(installationId, cancellationToken)));
app.MapGet("/api/scans/{jobId}", (string jobId, ILibraryApplicationService library) => Results.Ok(library.GetScan(jobId)));
app.MapDelete("/api/scans/{jobId}", (string jobId, ILibraryApplicationService library) =>
    library.CancelScan(jobId) ? Results.Accepted() : Results.NotFound());

app.MapGet("/api/installations/{installationId}/summary",
    async (string installationId, ILibraryApplicationService library, CancellationToken cancellationToken) =>
        Results.Ok(await library.GetSummaryAsync(installationId, cancellationToken)));
app.MapGet("/api/installations/{installationId}/option-packages",
    async (string installationId, IOptionPackageApplicationService options, CancellationToken cancellationToken) =>
        Results.Ok(await options.InspectAsync(installationId, cancellationToken)));
app.MapGet("/api/installations/{installationId}/library/facets",
    async (string installationId, ILibraryApplicationService library, CancellationToken cancellationToken) =>
        Results.Ok(await library.GetFacetsAsync(installationId, cancellationToken)));

app.MapGet("/api/installations/{installationId}/library/music",
    async (string installationId, HttpRequest request, ILibraryApplicationService library, IOpaqueIdService ids,
        CancellationToken cancellationToken) =>
    {
        var page = await library.QueryMusicAsync(installationId,
            Query(request, ["genre", "package"]), cancellationToken);
        return Results.Ok(new PageResponse<MusicResponse>(page.Items.Select(item => Music(item, installationId, ids)).ToArray(),
            page.Total, page.Offset, page.Limit));
    });
app.MapGet("/api/installations/{installationId}/library/cards",
    async (string installationId, HttpRequest request, ILibraryApplicationService library, CancellationToken cancellationToken) =>
    {
        var page = await library.QueryCardsAsync(installationId,
            Query(request, ["character", "rarity", "attribute", "package"]), cancellationToken);
        return Results.Ok(new PageResponse<CardResponse>(page.Items.Select(Card).ToArray(), page.Total, page.Offset, page.Limit));
    });
app.MapGet("/api/installations/{installationId}/library/characters",
    async (string installationId, HttpRequest request, ILibraryApplicationService library, CancellationToken cancellationToken) =>
    {
        var page = await library.QueryCharactersAsync(installationId, Query(request, ["package"]), cancellationToken);
        return Results.Ok(new PageResponse<CharacterResponse>(page.Items.Select(Character).ToArray(), page.Total, page.Offset, page.Limit));
    });
app.MapGet("/api/installations/{installationId}/library/resources",
    async (string installationId, HttpRequest request, ILibraryApplicationService library, CancellationToken cancellationToken) =>
    {
        var page = await library.QueryResourcesAsync(installationId,
            Query(request, ["kind", "package", "effective"]), cancellationToken);
        return Results.Ok(new PageResponse<ResourceResponse>(page.Items.Select(Resource).ToArray(), page.Total, page.Offset, page.Limit));
    });
app.MapGet("/api/installations/{installationId}/library/diagnostics",
    async (string installationId, HttpRequest request, IInstallationRegistry registry,
        ILibraryApplicationService library, CancellationToken cancellationToken) =>
    {
        var page = await library.QueryDiagnosticsAsync(installationId, Query(request, ["severity", "code"]), cancellationToken);
        var root = registry.GetRequired(installationId).Installation.RootPath;
        return Results.Ok(new PageResponse<DiagnosticResponse>(page.Items.Select(item => Diagnostic(item, root)).ToArray(),
            page.Total, page.Offset, page.Limit));
    });

app.MapGet("/api/installations/{installationId}/resources/{resourceId}/thumbnail",
    async (string installationId, string resourceId, int? maxSize, IResourceApplicationService resources,
        CancellationToken cancellationToken) => ThumbnailResult(await resources.ReadThumbnailAsync(installationId,
            resourceId, ThumbnailSize(maxSize), cancellationToken)));
app.MapGet("/api/installations/{installationId}/music/{musicId}/thumbnail",
    async (string installationId, string musicId, int? maxSize, IResourceApplicationService resources,
        CancellationToken cancellationToken) => ThumbnailResult(await resources.ReadMusicJacketAsync(installationId,
            musicId, ThumbnailSize(maxSize), cancellationToken)));
app.MapGet("/api/installations/{installationId}/cards/{cardId}/thumbnail",
    async (string installationId, string cardId, string? visual, int? maxSize, IResourceApplicationService resources,
        CancellationToken cancellationToken) => ThumbnailResult(await resources.ReadCardImageAsync(installationId, cardId,
            visual ?? "card", ThumbnailSize(maxSize), cancellationToken)));
app.MapGet("/api/installations/{installationId}/charts/{chartId}/preview",
    async (string installationId, string chartId, IResourceApplicationService resources, CancellationToken cancellationToken) =>
        Results.Ok(await resources.BuildChartPreviewAsync(installationId, chartId, cancellationToken)));
app.MapGet("/api/installations/{installationId}/music/{musicId}/audio",
    async (string installationId, string musicId, IResourceApplicationService resources, CancellationToken cancellationToken) =>
        Results.File(await resources.DecodeMusicAsync(installationId, musicId, cancellationToken), "audio/wav", enableRangeProcessing: true));
app.MapGet("/api/installations/{installationId}/chart-effect-textures",
    async (string installationId, IResourceApplicationService resources, CancellationToken cancellationToken) =>
        Results.Ok((await resources.ReadChartEffectTexturesAsync(installationId, cancellationToken)).Select(image =>
            new ImagePayload(image.Name, image.Width, image.Height, Convert.ToBase64String(image.PngBytes)))));
app.MapGet("/api/installations/{installationId}/characters/{modelId:int}/expressions",
    async (string installationId, int modelId, IResourceApplicationService resources, CancellationToken cancellationToken) =>
        Results.Ok(await resources.ListExpressionsAsync(installationId, modelId, cancellationToken)));
app.MapGet("/api/installations/{installationId}/expressions/{expressionId}",
    async (string installationId, string expressionId, IResourceApplicationService resources, CancellationToken cancellationToken) =>
    {
        var image = await resources.ReadExpressionAsync(installationId, expressionId, cancellationToken);
        return image is null ? Results.NotFound() : Results.File(image.PngBytes, "image/png");
    });

app.MapGet("/api/installations/{installationId}/configuration",
    async (string installationId, IConfigurationApplicationService configuration, CancellationToken cancellationToken) =>
        Results.Ok(Configuration(await configuration.InspectAsync(installationId, cancellationToken))));
app.MapGet("/api/installations/{installationId}/configuration/io-dlls",
    async (string installationId, IConfigurationApplicationService configuration, CancellationToken cancellationToken) =>
    {
        var catalog = await configuration.ListIoDllsAsync(installationId, cancellationToken);
        return Results.Ok(new { directory = catalog.DirectoryName, dlls = catalog.Dlls });
    });
app.MapPost("/api/installations/{installationId}/configuration/{kind}/preview",
    async (string installationId, string kind, ConfigurationPreviewRequest request,
        IConfigurationApplicationService configuration, CancellationToken cancellationToken) =>
    {
        if (!Enum.TryParse<GameConfigurationFileKind>(kind, true, out var parsed))
            return Results.BadRequest(new { error = "Unknown configuration file kind." });
        return Results.Ok(await configuration.PreviewAsync(installationId, parsed, request.BaselineHash,
            request.Edits, cancellationToken));
    });
app.MapPost("/api/installations/{installationId}/configuration/save",
    async (string installationId, ConfigurationSaveRequest request, IConfigurationApplicationService configuration,
        CancellationToken cancellationToken) => Results.Ok(await configuration.SaveAsync(installationId,
            request.PreviewToken, cancellationToken)));
app.MapGet("/api/installations/{installationId}/configuration/{kind}/backups",
    async (string installationId, string kind, IConfigurationApplicationService configuration,
        CancellationToken cancellationToken) =>
    {
        if (!Enum.TryParse<GameConfigurationFileKind>(kind, true, out var parsed))
            return Results.BadRequest(new { error = "Unknown configuration file kind." });
        return Results.Ok(await configuration.ListBackupsAsync(installationId, parsed, cancellationToken));
    });
app.MapPost("/api/installations/{installationId}/configuration/backups/{backupId}/restore",
    async (string installationId, string backupId, IConfigurationApplicationService configuration,
        CancellationToken cancellationToken) => Results.Ok(await configuration.RestoreAsync(installationId,
            backupId, cancellationToken)));
app.MapPost("/api/installations/{installationId}/mods/{modId}/state",
    async (string installationId, string modId, ModStateRequest request,
        IConfigurationApplicationService configuration, CancellationToken cancellationToken) =>
        Results.Ok(await configuration.ToggleModAsync(installationId, modId, request.Enabled, cancellationToken)));

app.MapGet("/api/installations/{installationId}/exports/{kind}/{itemId}",
    async (string installationId, string kind, string itemId, IResourceApplicationService resources,
        CancellationToken cancellationToken) =>
    {
        var content = await resources.ExportAsync(installationId, kind, itemId == "_" ? string.Empty : itemId,
            cancellationToken);
        return Results.Stream(content.Content, content.ContentType, content.FileName);
    });

await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
    .Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException("The local API did not bind to loopback.");
Console.Out.WriteLine("OGK_READY " + JsonSerializer.Serialize(new { address, instanceId }));
Console.Out.Flush();
_ = MonitorParentAsync(app.Lifetime, app.Lifetime.ApplicationStopping);
await app.WaitForShutdownAsync();

static async Task MonitorParentAsync(IHostApplicationLifetime lifetime, CancellationToken stopping)
{
    if (!int.TryParse(Environment.GetEnvironmentVariable("OGK_PARENT_PID"), out var parentId)) return;
    DateTime expectedStartTime;
    try
    {
        using var initial = Process.GetProcessById(parentId);
        expectedStartTime = initial.StartTime.ToUniversalTime();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    { lifetime.StopApplication(); return; }
    while (!stopping.IsCancellationRequested)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.HasExited || parent.StartTime.ToUniversalTime() != expectedStartTime)
            {
                lifetime.StopApplication();
                return;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { lifetime.StopApplication(); return; }
        try { await Task.Delay(TimeSpan.FromSeconds(2), stopping); }
        catch (OperationCanceledException) { return; }
    }
}

static bool FixedEquals(byte[] expected, byte[] supplied) => expected.Length == supplied.Length
    && CryptographicOperations.FixedTimeEquals(expected, supplied);

static async Task Problem(HttpContext context, int status, string title, string detail)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Detail = detail });
}

static LibraryQuery Query(HttpRequest request, IReadOnlyCollection<string> allowedFilters)
{
    var offset = int.TryParse(request.Query["offset"], out var parsedOffset) ? parsedOffset : 0;
    var limit = int.TryParse(request.Query["limit"], out var parsedLimit) ? parsedLimit : 50;
    var filters = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var key in allowedFilters)
    {
        if (!request.Query.TryGetValue(key, out var supplied)) continue;
        var values = supplied.SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => NormalizeFilter(key, value.Trim())).Where(value => value.Length > 0).ToArray();
        if (values.Length > 0) filters[key] = values;
    }
    return new(offset, limit, request.Query["q"].ToString(), request.Query["sort"].ToString(),
        request.Query["direction"].ToString().Equals("desc", StringComparison.OrdinalIgnoreCase), filters);
}

static string NormalizeFilter(string key, string value)
{
    if (key.Equals("kind", StringComparison.OrdinalIgnoreCase)
        && Enum.TryParse<ResourceKind>(value, true, out var kind)) return ((int)kind).ToString(CultureInfo.InvariantCulture);
    if (key.Equals("severity", StringComparison.OrdinalIgnoreCase)
        && Enum.TryParse<DiagnosticSeverity>(value, true, out var severity)) return ((int)severity).ToString(CultureInfo.InvariantCulture);
    if (key.Equals("effective", StringComparison.OrdinalIgnoreCase)
        && bool.TryParse(value, out var effective)) return effective ? "1" : "0";
    return value;
}

static MusicResponse Music(IndexedItem<Music> indexed, string installationId, IOpaqueIdService ids)
{
    var item = indexed.Value;
    return new(indexed.Id, item.Id, item.DataName, item.Title, item.Artist, item.Genre, item.VersionName,
        item.ReleaseDate, item.Charts.Select(chart => new ChartResponse(
            ids.Create("chart", installationId, item.Id.ToString(CultureInfo.InvariantCulture),
                chart.Difficulty.ToString(CultureInfo.InvariantCulture)), chart.Difficulty, chart.DifficultyName,
            chart.LevelConstant, chart.Creator, chart.MainBpm, chart.TotalNotes, chart.TapCount, chart.HoldCount,
            chart.FlickCount, chart.BellCount, chart.Exists)).ToArray(), item.Jacket is not null, item.Audio is not null,
        item.Origin.PackageId);
}

static CardResponse Card(IndexedItem<Card> indexed)
{
    var item = indexed.Value;
    return new(indexed.Id, item.Id, item.DataName, item.Name, item.CharacterId, item.CharacterName, item.NickName,
        item.Rarity, item.Attribute, item.Image is not null, item.CharacterImage is not null,
        item.FullIllustration is not null, item.Icon is not null, item.Origin.PackageId);
}

static CharacterResponse Character(IndexedItem<Character> indexed)
{
    var item = indexed.Value;
    return new(indexed.Id, item.Id, item.DataName, item.Name, item.ModelId, item.GraphicCardId, item.FlavorText,
        item.Origin.PackageId);
}

static ResourceResponse Resource(IndexedItem<GameResource> indexed)
{
    var item = indexed.Value;
    return new(indexed.Id, item.Key, item.Kind, item.Size, item.Origin.PackageId, item.Origin.LoadOrder,
        item.Origin.IsEffective);
}

static DiagnosticResponse Diagnostic(IndexedItem<LibraryDiagnostic> indexed, string root)
{
    var item = indexed.Value;
    var source = item.SourcePath is null ? null : Path.GetRelativePath(root, item.SourcePath);
    if (source is not null && (Path.IsPathRooted(source) || source.StartsWith("..", StringComparison.Ordinal)))
        source = Path.GetFileName(item.SourcePath);
    return new(indexed.Id, item.Severity, item.Code, item.Message, source);
}

static ConfigurationResponse Configuration(ConfigurationView view) => new(view.InstallationId, view.HookVersion,
    view.Files.Select(file => new ConfigurationFileResponse(file.Kind, file.DisplayName, file.Exists, file.EncodingName,
        file.NewLineName, file.LastWriteTime, file.Size, file.ContentHash,
        file.Entries.Select(entry => new ConfigurationEntryResponse(entry.Section, entry.Key,
            entry.Value, entry.DisplayValue, entry.ValueKind, entry.LineNumber, entry.Description,
            entry.IsSensitive, entry.IsKnown, entry.Locator, entry.IsPresent, entry.DefaultValue,
            entry.RequiredModsOrEmpty)).ToArray())).ToArray(),
    view.Mods.Select(mod => new InstalledModResponse(mod.Id, mod.Name, mod.FileName, mod.Kind, mod.IsEnabled,
        mod.Version, mod.LastWriteTime, mod.Size, mod.ChineseName, mod.Description,
        mod.ConfigurationEntries.Select(entry => new ConfigurationEntryResponse(entry.Section, entry.Key,
            entry.Value, entry.DisplayValue, entry.ValueKind, entry.LineNumber, entry.Description,
            entry.IsSensitive, entry.IsKnown, entry.Locator, entry.IsPresent, entry.DefaultValue,
            entry.RequiredModsOrEmpty)).ToArray())).ToArray(),
    view.Diagnostics.Select(item => new DiagnosticResponse(string.Empty, item.Severity, item.Code,
        item.Message, item.SourcePath is null ? null : Path.GetFileName(item.SourcePath))).ToArray());

static int ThumbnailSize(int? maxSize) => Math.Clamp(maxSize ?? 640, 64, 2048);

static IResult ThumbnailResult(ThumbnailImage? image) => image is null
    ? Results.NotFound()
    : Results.File(image.Bytes, image.ContentType);

public sealed record HealthResponse(string Name, string InstanceId, bool Ready);
public sealed record RegisterInstallationRequest(string RootPath);
public sealed record InstallationResponse(string InstallationId, string DisplayName);
public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit);
public sealed record MusicResponse(string Id, int NumericId, string DataName, string Title, string Artist, string Genre,
    string VersionName, DateTime? ReleaseDate, IReadOnlyList<ChartResponse> Charts, bool HasJacket, bool HasAudio,
    string PackageId);
public sealed record ChartResponse(string Id, int Difficulty, string DifficultyName, decimal LevelConstant,
    string Creator, decimal MainBpm, int TotalNotes, int TapCount, int HoldCount, int FlickCount, int BellCount, bool Exists);
public sealed record CardResponse(string Id, int NumericId, string DataName, string Name, int CharacterId,
    string CharacterName, string NickName, string Rarity, string Attribute, bool HasImage, bool HasCharacterImage,
    bool HasFullIllustration, bool HasIcon, string PackageId);
public sealed record CharacterResponse(string Id, int NumericId, string DataName, string Name, int ModelId,
    int GraphicCardId, string FlavorText, string PackageId);
public sealed record ResourceResponse(string Id, string Key, ResourceKind Kind, long Size, string PackageId,
    int LoadOrder, bool IsEffective);
public sealed record DiagnosticResponse(string Id, DiagnosticSeverity Severity, string Code, string Message, string? Source);
public sealed record ConfigurationResponse(string InstallationId, string HookVersion,
    IReadOnlyList<ConfigurationFileResponse> Files, IReadOnlyList<InstalledModResponse> Mods,
    IReadOnlyList<DiagnosticResponse> Diagnostics);
public sealed record InstalledModResponse(string Id, string Name, string FileName, InstalledModKind Kind,
    bool IsEnabled, string Version, DateTimeOffset LastWriteTime, long Size, string ChineseName, string Description,
    IReadOnlyList<ConfigurationEntryResponse> ConfigurationEntries);
public sealed record ConfigurationFileResponse(GameConfigurationFileKind Kind, string DisplayName, bool Exists,
    string EncodingName, string NewLineName, DateTimeOffset? LastWriteTime, long Size, string ContentHash,
    IReadOnlyList<ConfigurationEntryResponse> Entries);
public sealed record ConfigurationEntryResponse(string Section, string Key, string Value, string DisplayValue, ConfigurationValueKind ValueKind,
    int LineNumber, string Description, bool IsSensitive, bool IsKnown, string Locator, bool IsPresent,
    string DefaultValue, IReadOnlyList<string> RequiredMods);
public sealed record ConfigurationPreviewRequest(string BaselineHash, IReadOnlyList<ConfigurationEdit> Edits);
public sealed record ConfigurationSaveRequest(string PreviewToken);
public sealed record ModStateRequest(bool Enabled);
public sealed record ImagePayload(string Name, int Width, int Height, string PngBase64);
