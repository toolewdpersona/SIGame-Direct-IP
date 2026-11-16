using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SIGame.DirectHost;

/// <summary>On-demand catalogue access. Only the local host can call these routes.</summary>
public sealed class DirectLibraries : IDisposable
{
    private const int MaxJsonBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = ConnectPublicAsync
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Uri LibraryBase(string library) => library switch
    {
        "default" => new("https://vladimirkhil.com/sistorage/"),
        "sibrowser" => new("https://www.sibrowser.ru/sistorage/"),
        _ => throw new ArgumentException("Неизвестная библиотека вопросов.")
    };

    public static Uri CatalogueUri(string library, string path, string query)
    {
        var validPath = Regex.IsMatch(path,
            @"^api/(v1/(facets/(languages|authors|tags|publishers|restrictions)|packages/(search)?)|v2/facets/tags)$",
            RegexOptions.CultureInvariant);
        if (!validPath || query.Length > 4096) throw new ArgumentException("Некорректный запрос к библиотеке.");
        return new Uri(LibraryBase(library), path + query);
    }

    public async Task<byte[]> CatalogueAsync(string library, string path, string query, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogueUri(library, path, query));
        return await JsonAsync(request, timeout.Token);
    }

    /// <summary>Resolves a catalogue item, downloads it with bounded redirects/bytes, then uses the ordinary SIQ validator.</summary>
    public async Task<PackageDescriptor> ImportAsync(LibraryPackageRequest selection, DirectGames games, CancellationToken cancellationToken)
    {
        if (!await _downloads.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("Уже загружаются два пакета. Дождитесь завершения.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var token = timeout.Token;
            using var metadataRequest = await PackageRequestAsync(selection, token);
            using var metadata = JsonDocument.Parse(await JsonAsync(metadataRequest, token));
            if (!metadata.RootElement.TryGetProperty("directContentUri", out var content) ||
                !Uri.TryCreate(content.GetString(), UriKind.Absolute, out var uri))
                throw new InvalidDataException("Библиотека не вернула ссылку на пакет.");
            if (uri.Host == "vk.com") uri = new Uri(uri.AbsoluteUri + (uri.Query.Length > 0 ? "&" : "?") + "dl=1");

            using var response = await DownloadAsync(uri, token);
            var length = response.Content.Headers.ContentLength;
            if (length is > DirectGames.MaxPackageBytes) throw new InvalidDataException("Пакет превышает 512 МБ.");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("Хранилище не отдало файл. Скачайте пакет вручную и выберите «Файл с вопросами».");

            var temporaryFile = Path.Combine(Path.GetTempPath(), "sigame-library-" + Guid.NewGuid().ToString("N") + ".siq");
            await using var file = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await using (var stream = await response.Content.ReadAsStreamAsync(token))
                await CopyLimitedAsync(stream, file, DirectGames.MaxPackageBytes, token);
            file.Position = 0;
            return await games.UploadAsync(new FormFile(file, 0, file.Length, "file", "library.siq"), token);
        }
        finally { _downloads.Release(); }
    }

    private async Task<HttpRequestMessage> PackageRequestAsync(LibraryPackageRequest selection, CancellationToken token)
    {
        if (selection.Library == "random")
        {
            using var languagesRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(LibraryBase("default"), "api/v1/facets/languages"));
            using var languages = JsonDocument.Parse(await JsonAsync(languagesRequest, token));
            var culture = DirectGames.Culture(selection.Language);
            var language = languages.RootElement.EnumerateArray().FirstOrDefault(l => l.GetProperty("code").GetString() == culture);
            var body = new Dictionary<string, object>
            {
                ["restrictionIds"] = new[] { -1 }, ["tagIds"] = new[] { -1 },
                ["roundCount"] = 2, ["tableThemeCount"] = 6, ["themeListThemeCount"] = 4, ["baseQuestionPrice"] = 100
            };
            if (language.ValueKind != JsonValueKind.Undefined) body["languageId"] = language.GetProperty("id").GetInt32();
            return new HttpRequestMessage(HttpMethod.Post, new Uri(LibraryBase("default"), "api/v1/packages/random"))
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }

        var valid = selection.Library == "default" ? Guid.TryParseExact(selection.Id, "D", out _) :
            selection.Library == "sibrowser" && Regex.IsMatch(selection.Id ?? "", @"^[0-9]{1,12}$", RegexOptions.CultureInvariant);
        if (!valid) throw new ArgumentException("Некорректный идентификатор пакета.");
        return new HttpRequestMessage(HttpMethod.Get, new Uri(LibraryBase(selection.Library), "api/v1/packages/" + selection.Id));
    }

    private async Task<byte[]> JsonAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxJsonBytes) throw new InvalidDataException("Слишком большой ответ библиотеки.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, MaxJsonBytes, token);
        var bytes = buffer.ToArray();
        using var json = JsonDocument.Parse(bytes);
        return bytes;
    }

    public static bool IsDownloadUri(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.Host == "vladimirkhil.com") return Regex.IsMatch(uri.AbsolutePath,
            @"^/sistorage/(packages|temp)/[0-9a-fA-F-]{36}\.siq$", RegexOptions.CultureInvariant);
        if (uri.Host == "vk.com") return Regex.IsMatch(uri.AbsolutePath, @"^/doc-?[0-9]+_[0-9]+$", RegexOptions.CultureInvariant);
        return uri.Host.EndsWith(".userapi.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/", StringComparison.Ordinal);
    }

    private async Task<HttpResponseMessage> DownloadAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!IsDownloadUri(uri)) throw new InvalidDataException("Библиотека вернула неподдерживаемый адрес пакета. Скачайте .siq вручную.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                response.Dispose();
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Хранилище пакетов вернуло HTTP {status}.");
        }
        throw new InvalidDataException("Слишком много перенаправлений при загрузке пакета.");
    }

    public static async Task CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("Превышен допустимый размер загружаемых данных.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    // Pin each socket to a checked public address: DNS changes and redirects cannot reach the host's LAN.
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) && !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 2)) &&
            !(b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        foreach (var address in addresses.Where(IsPublicAddress))
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); if (token.IsCancellationRequested) throw; }
        }
        throw new HttpRequestException("Не удалось подключиться к публичному хранилищу пакетов.");
    }

    public void Dispose() { _http.Dispose(); _downloads.Dispose(); }
}

public sealed record LibraryPackageRequest(string Library, string? Id = null, string? Language = null);
