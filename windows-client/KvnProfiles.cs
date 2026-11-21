using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VPNSL.Windows;

/// <summary>Public, non-secret profile metadata. User keys are never serialized here.</summary>
public sealed class KvnProfileEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "AWG";
    public string SourcePath { get; set; } = ""; // AWG path only; no copy of secret contents.

    public override string ToString() => Name;
}

internal sealed record CsqttImportedProfile(string Name, string Endpoint, string VkHashes, string Password);

/// <summary>
/// The Android KVN "kvn-csqtt" v1 JSON format. Import stores the *complete*
/// payload encrypted with user-scoped DPAPI, never plaintext in settings.json.
/// </summary>
internal static class KvnProfileStore
{
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9 _-]{0,31}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex HashRegex = new(@"^[A-Za-z0-9_=:/.+\-]{16,512}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex HostRegex = new(@"^(\[[0-9a-fA-F:]+\]|[A-Za-z0-9.-]+):([0-9]{1,5})$",
        RegexOptions.CultureInvariant);

    private static string ProfileFolder => Path.Combine(SettingsStore.DirectoryPath, "profiles");
    private static string ProfilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidOperationException("Неверный идентификатор профиля.");
        return Path.Combine(ProfileFolder, id + ".dpapi");
    }

    public static CsqttImportedProfile ParseCsqtt(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) is < 40 or > 8192)
            throw new FormatException("Недопустимый размер CSQTT-профиля (40–8192 байт).");

        using var document = JsonDocument.Parse(json);
        var element = document.RootElement;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("format", out var format) ||
            format.GetString() != "kvn-csqtt" ||
            !element.TryGetProperty("version", out var version) ||
            version.GetInt32() != 1)
            throw new FormatException("Требуется файл КВН CSQTT версии 1.");

        string Required(string key)
        {
            if (!element.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.String)
                throw new FormatException("В профиле отсутствует поле " + key);
            return prop.GetString() ?? "";
        }

        var name = Required("name").Trim();
        var endpoint = Required("endpoint").Trim();
        var password = Required("password");

        if (!NameRegex.IsMatch(name))
            throw new FormatException("Имя профиля: латиница/цифры, до 32 символов.");
        var match = HostRegex.Match(endpoint);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, out var port) || port is < 1 or > 65535)
            throw new FormatException("В профиле неверный адрес CSQTT (host:port).");
        if (password.Length is < 10 or > 128 || !password.All(char.IsLetterOrDigit))
            throw new FormatException("Недопустимый пароль CSQTT.");

        if (!element.TryGetProperty("vk_hashes", out var hashes) ||
            hashes.ValueKind != JsonValueKind.Array ||
            hashes.GetArrayLength() is < 1 or > 6)
            throw new FormatException("В профиле должно быть от 1 до 6 VK-хешей.");
        var values = hashes.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() ?? "" : "").ToList();
        if (values.Any(value => !HashRegex.IsMatch(value)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new FormatException("Некорректные или повторяющиеся VK-хеши.");
        return new CsqttImportedProfile(name, endpoint, string.Join(",", values), password);
    }

    public static KvnProfileEntry ImportCsqtt(string filename)
    {
        if (!string.Equals(Path.GetExtension(filename), ".kvn", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Для CSQTT выберите файл .kvn.");
        var bytes = File.ReadAllBytes(filename);
        if (bytes.Length is < 40 or > 8192) throw new FormatException("Неверный размер .kvn.");

        // This is JSON as produced by Android KVN; reject corrupted UTF-8.
        var json = new UTF8Encoding(false, true).GetString(bytes);
        var profile = ParseCsqtt(json);
        var entry = new KvnProfileEntry { Transport = "CSQTT", Name = profile.Name };
        Directory.CreateDirectory(ProfileFolder);
        var secured = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var filepath = ProfilePath(entry.Id);
        try { File.WriteAllBytes(filepath, secured); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return entry;
    }

    public static CsqttImportedProfile LoadCsqtt(KvnProfileEntry entry)
    {
        if (entry.Transport != "CSQTT") throw new ArgumentException("Expected CSQTT profile.");
        var data = File.ReadAllBytes(ProfilePath(entry.Id));
        var plaintext = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        try { return ParseCsqtt(new UTF8Encoding(false, true).GetString(plaintext)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static void RemoveCsqtt(KvnProfileEntry entry)
    {
        if (entry.Transport != "CSQTT") return;
        var filename = ProfilePath(entry.Id);
        if (File.Exists(filename)) File.Delete(filename);
    }
}
