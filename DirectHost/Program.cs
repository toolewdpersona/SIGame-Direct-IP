using System.Diagnostics;
using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using SIGame.DirectHost;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Native launcher uses its log directory as cwd; assets stay beside the server.
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
var port = builder.Configuration.GetValue<int?>("port") ?? 51234;
if (port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
var instanceToken = builder.Configuration["instance-token"] ?? Guid.NewGuid().ToString("N");
using var versionStream = typeof(Program).Assembly.GetManifestResourceStream("mod-version.json")!;
using var versionDocument = System.Text.Json.JsonDocument.Parse(versionStream);
var modVersion = versionDocument.RootElement.GetProperty("version").GetString();
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = DirectGames.MaxPackageBytes + 1048576;
    o.Limits.MaxConcurrentConnections = 256;
    o.Limits.MaxConcurrentUpgradedConnections = 256;
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = DirectGames.MaxPackageBytes);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 131072)
    .AddJsonProtocol(o => { o.PayloadSerializerOptions.PropertyNamingPolicy = null; o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()); });
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.SetIsOriginAllowed(AllowedOrigin)
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<DirectGames>();
builder.Services.AddSingleton<DirectAvatars>();
builder.Services.AddSingleton<DirectLibraries>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("avatar-uploads", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Method == "POST" && context.Request.Path == "/direct/avatars"
            ? RateLimitPartition.GetConcurrencyLimiter("avatars", _ => new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("other"));
});
builder.Services.AddSingleton<DirectMetrics>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DirectMetrics>());
var app = builder.Build();
app.Use(async (context, next) =>
{
    // CORS alone does not prevent simple cross-origin POSTs against a loopback service.
    var origin = context.Request.Headers.Origin.ToString();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (origin.Length > 0 && !AllowedOrigin(origin) &&
        !string.Equals(origin, Origin(context), StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 403;
        return;
    }
    await next(context);
});
app.UseCors();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Revalidate after the host updates the mod, including non-hashed config.js.
        context.Context.Response.Headers.CacheControl = "no-cache";
        context.Context.Response.Headers.XContentTypeOptions = "nosniff";
    }
});
// Gameplay never calls an upstream game/account/content/statistics service.
// Public package libraries are accessed only when the local host explicitly selects them.
app.MapGet("/direct/libraries/{library}/{**path}", async (string library, string path, HttpContext context, DirectLibraries libraries) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    try
    {
        var json = await libraries.CatalogueAsync(library, path, context.Request.QueryString.Value ?? "", context.RequestAborted);
        return Results.Bytes(json, "application/json; charset=utf-8");
    }
    catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
    {
        return Results.Problem("Не удалось загрузить библиотеку вопросов: " + error.Message, statusCode: 502);
    }
});
app.MapPost("/direct/library-package", async (LibraryPackageRequest selection, HttpContext context, DirectLibraries libraries, DirectGames games) =>
{
    if (!IsLocal(context)) return Results.StatusCode(403);
    try { return Results.Ok(await libraries.ImportAsync(selection, games, context.RequestAborted)); }
    catch (Exception error) when (error is ArgumentException or HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or
        System.Xml.XmlException or InvalidOperationException or OperationCanceledException)
    {
        return Results.Problem("Не удалось загрузить пакет: " + error.Message, statusCode: 400);
    }
});
app.MapGet("/direct/health", (HttpContext context) => Results.Json(new
{
    mode = "sigame-direct",
    instanceToken = IsLocal(context) ? instanceToken : null,
    addresses = IsLocal(context) ? System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(address => address.ToString()).Distinct().ToArray() : [],
    version = modVersion
}));
app.MapPost("/direct/stop", (HttpContext context) =>
{
    if (!IsLocal(context) || context.Request.Headers["X-Instance-Token"] != instanceToken)
        return Results.StatusCode(403);
    context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
    return Results.Ok();
});
app.MapGet("/api/v1/info/host", (HttpContext context) => new
{
    name = "Прямое подключение", host = context.Request.Host.Host, siHosts = new Dictionary<string, string>(),
    port, contentPublicBaseUrls = new[] { Origin(context) + "/direct/media/" }, license = "",
    maxPackageSizeMb = 512, contentInfos = Array.Empty<object>(), storageInfos = Array.Empty<object>()
});
app.MapGet("/api/v1/info/bots", (HttpContext context) => DirectGames.Bots(context.Request.Headers.AcceptLanguage.ToString()));
app.MapPost("/direct/avatars", async (HttpContext context, DirectAvatars avatars) =>
{
    var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = DirectAvatars.MaxBytes + 65536;
    try
    {
        var form = await context.Request.ReadFormAsync(new FormOptions
        {
            MultipartBodyLengthLimit = DirectAvatars.MaxBytes,
            ValueLengthLimit = 1024,
            ValueCountLimit = 4
        }, context.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file == null || form.Files.Count != 1) return Results.BadRequest(new { error = "Выберите одну аватарку." });
        return Results.Ok(new { uri = await avatars.UploadAsync(file, context.RequestAborted) });
    }
    catch (Exception error) when (error is InvalidDataException or BadHttpRequestException)
    {
        return Results.BadRequest(new { error = error.Message });
    }
}).DisableAntiforgery().RequireRateLimiting("avatar-uploads");
app.MapGet("/direct/avatars/{id}", (string id, HttpContext context, DirectAvatars avatars) =>
{
    var avatar = avatars.Get(id);
    if (avatar == null) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    context.Response.Headers.ContentSecurityPolicy = DirectSecurity.AvatarPolicy;
    return Results.File(avatar.Path, avatar.ContentType, enableRangeProcessing: true);
});
app.MapPost("/direct/packages", async (HttpContext context, DirectGames games) =>
{
    if (!IsLocal(context)) return Results.Problem("Пакет может загрузить только компьютер хоста.", statusCode: 403);
    try
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file == null || form.Files.Count != 1 || file.Length is <= 0 or > DirectGames.MaxPackageBytes)
            return Results.BadRequest(new { error = "Выберите пакет .siq размером до 512 МБ." });
        var descriptor = await games.UploadAsync(file, context.RequestAborted);
        return Results.Ok(descriptor);
    }
    catch (Exception error) when (error is InvalidDataException or IOException or System.Xml.XmlException or ArgumentException)
    {
        return Results.BadRequest(new { error = "Некорректный пакет: " + error.Message });
    }
}).DisableAntiforgery();
app.MapPost("/api/v1/games", async (HttpContext context, RunRequest request, DirectGames games) =>
{
    if (!IsLocal(context)) return Results.Problem("Игру может создать только компьютер хоста.", statusCode: 403);
    try { return Results.Ok(await games.CreateAsync(request, Origin(context))); }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Cannot create direct game");
        return Results.BadRequest(new { isSuccess = false, hostUri = "", gameId = 0, isHost = false, errorType = 11, message = error.Message });
    }
});
app.MapGet("/direct/media/{packageId}/{category}/{fileName}", (string packageId, string category, string fileName, HttpContext context, DirectGames games) =>
{
    var path = games.MediaPath(packageId, category, fileName);
    if (path == null) return Results.NotFound();
    var provider = new FileExtensionContentTypeProvider();
    provider.TryGetContentType(path, out var type);
    // Applies even if a player opens package HTML/SVG directly, outside our iframe.
    context.Response.Headers.ContentSecurityPolicy = DirectSecurity.PackagePolicy;
    return Results.File(path, type ?? "application/octet-stream", enableRangeProcessing: true);
});
app.MapHub<LobbyHub>("/sionline");
app.MapHub<GameHub>("/sihost");
var parentPid = builder.Configuration.GetValue<int?>("parent-pid");
if (parentPid.HasValue)
{
    _ = Task.Run(async () =>
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid.Value);
            await parent.WaitForExitAsync(app.Lifetime.ApplicationStopping);
            app.Lifetime.StopApplication();
        }
        catch (ArgumentException) { app.Lifetime.StopApplication(); }
        catch (OperationCanceledException) { }
    });
}
await app.RunAsync();

static bool IsLocal(HttpContext context)
{
    var origin = context.Request.Headers.Origin.ToString();
    // Serving the web client must not broaden access to local management endpoints.
    return context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address) &&
        DirectSecurity.LoopbackHost(context.Request.Host.Host) &&
        (origin.Length == 0 || AllowedOrigin(origin) || string.Equals(origin, Origin(context), StringComparison.OrdinalIgnoreCase));
}
static string Origin(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}";
static bool AllowedOrigin(string origin) => DirectSecurity.NativeOrigin(origin);
