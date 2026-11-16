using System.Net;
using System.IO.Compression;
using System.Xml;

namespace SIGame.DirectHost;

internal static class DirectSecurity
{
    public const string PackagePolicy = "sandbox allow-scripts allow-presentation; object-src 'none'; base-uri 'none'; form-action 'none'; connect-src 'none'";
    public const string AvatarPolicy = "sandbox; default-src 'none'; frame-ancestors 'none'";

    public static bool NativeOrigin(string origin) => origin is "http://tauri.localhost" or "https://tauri.localhost";

    public static bool LoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    // Check metadata before the upstream extractor. XML must not expand entities or fetch external resources.
    public static void CheckPackage(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("Слишком много файлов в пакете.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p == "..") || !names.Add(name))
                throw new InvalidDataException("Некорректные пути файлов в пакете.");
            total = checked(total + entry.Length);
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Распакованный пакет больше 2 ГБ.");
            if (name is not ("content.xml" or "Texts/authors.xml" or "Texts/sources.xml")) continue;
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Описание пакета больше 16 МБ.");
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024
            });
            while (reader.Read()) { }
        }
    }
}
