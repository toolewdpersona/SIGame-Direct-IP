using System.Security.Cryptography;

namespace SIGame.DirectHost;

/// <summary>Session-only images shared by the direct host; no external content service.</summary>
public sealed class DirectAvatars : IDisposable
{
    public const int MaxBytes = 15 * 1024 * 1024;
    public const long MaxSessionBytes = 512L * 1024 * 1024;
    public const int MaxSessionFiles = 128;
    private long _storedBytes;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SIGame.DirectAvatars", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, Avatar> _images = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public DirectAvatars() => Directory.CreateDirectory(_root);

    public async Task<string> UploadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxBytes) throw new InvalidDataException("Выберите картинку или видео размером до 15MB.");
        await using var input = file.OpenReadStream();
        var temporary = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        try
        {
            string id;
            string type;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                long length = 0;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    length += read;
                    if (length > MaxBytes) throw new InvalidDataException("Аватарка больше 15MB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                output.Position = 0;
                var header = new byte[64];
                var count = await output.ReadAsync(header, cancellationToken);
                // Use actual bytes, never an untrusted filename or supplied content type.
                type = count >= 8 && header.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
                    : count >= 3 && header.AsSpan().StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
                    : count >= 13 && (header.AsSpan(0, 6).SequenceEqual("GIF87a"u8) ||
                        header.AsSpan(0, 6).SequenceEqual("GIF89a"u8)) ? "image/gif"
                    : count >= 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8) ? "video/mp4"
                    : count >= 8 && header.AsSpan().StartsWith(new byte[] { 26, 69, 223, 163 }) &&
                        System.Text.Encoding.ASCII.GetString(header, 0, count).Contains("webm", StringComparison.Ordinal) ? "video/webm"
                    : throw new InvalidDataException("Выберите PNG, JPEG, GIF, MP4 или WebM.");
                output.Position = 0;
                id = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken)).ToLowerInvariant() +
                    (type switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "video/mp4" => ".mp4", _ => ".webm" });
            }
            lock (_gate)
            {
                if (!_images.ContainsKey(id))
                {
                    var length = new FileInfo(temporary).Length;
                    if (_images.Count >= MaxSessionFiles || _storedBytes + length > MaxSessionBytes)
                        throw new InvalidDataException("Хранилище аватарок этого сеанса заполнено. Перезапустите хост.");
                    var path = Path.Combine(_root, id);
                    File.Move(temporary, path);
                    _images.Add(id, new Avatar(path, type));
                    _storedBytes += length;
                }
            }
            return "/direct/avatars/" + id;
        }
        finally { File.Delete(temporary); }
    }

    public Avatar? Get(string id)
    {
        lock (_gate) return _images.GetValueOrDefault(id);
    }

    public string NormalizeUri(string value)
    {
        if (value.Length == 0) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 || !uri.AbsolutePath.StartsWith("/direct/avatars/", StringComparison.Ordinal))
            throw new InvalidDataException("Сначала загрузите аватарку на этот хост.");
        var id = uri.AbsolutePath["/direct/avatars/".Length..];
        if (Get(id) == null) throw new InvalidDataException("Аватарка не найдена на этом хосте.");
        // Each recipient gets the address it used to connect, including peers outside the LAN.
        return LocalFileShare.Placeholder + "/direct/avatars/" + id;
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SIGame.DirectAvatars")) + Path.DirectorySeparatorChar;
        var ownedRoot = Path.GetFullPath(_root);
        if (ownedRoot.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(ownedRoot))
            Directory.Delete(ownedRoot, true);
    }

    public sealed record Avatar(string Path, string ContentType);
}
