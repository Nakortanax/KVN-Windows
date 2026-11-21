using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace VPNSL.Windows;

public enum RouteTarget
{
    MOBILE,
    VPNSL,
}

public sealed class RouteProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Маршрут";
    public bool Enabled { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RouteTarget Target { get; set; } = RouteTarget.VPNSL;
    public List<string> Routes { get; set; } = [];

    [JsonIgnore]
    public string TargetDisplay => Target == RouteTarget.VPNSL ? "Через VPNSL" : "Напрямую";

    [JsonIgnore]
    public string RouteCountDisplay => Routes.Count switch
    {
        1 => "1 маршрут",
        >= 2 and <= 4 => $"{Routes.Count} маршрута",
        _ => $"{Routes.Count} маршрутов",
    };

    [JsonIgnore]
    public string EnabledDisplay => Enabled ? "Включено" : "Выключено";
}

public sealed class AppSettings
{
    public string KvnTransport { get; set; } = "CSQTT";
    public string KvnAwgProfilePath { get; set; } = "";
    public string KvnSelectedProfileId { get; set; } = "";
    public List<KvnProfileEntry> KvnProfiles { get; set; } = [];
    public string Peer { get; set; } = "";
    [JsonIgnore] // DPAPI encrypted separately.
    public string Password { get; set; } = "";
    [JsonIgnore] // DPAPI encrypted separately.
    public string VkHashes { get; set; } = "";
    public string VkHashMode { get; set; } = "manual";
    public int Workers { get; set; } = 18;
    public string TurnHost { get; set; } = "";
    public string TurnPort { get; set; } = "";
    public string Sni { get; set; } = "";
    public string Obfs { get; set; } = "audio";
    public string TurnTransport { get; set; } = "udp";
    public string VkAuthMode { get; set; } = "vkcalls";
    public string CaptchaMode { get; set; } = "auto";
    public string Fingerprint { get; set; } = "chrome";
    public string ClientIds { get; set; } = "8202606,6287487";
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    public string SshHost { get; set; } = "";
    public string SshUser { get; set; } = "root";
    [JsonIgnore] // DPAPI encrypted separately.
    public string SshPassword { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public int ServerPeerPort { get; set; } = 46000;
    public int ServerWebPort { get; set; } = 46002;
    [JsonIgnore] // DPAPI encrypted separately.
    public string ServerMainPassword { get; set; } = "";
    public string ServerWebLogin { get; set; } = "";
    [JsonIgnore] // DPAPI encrypted separately.
    public string ServerWebPassword { get; set; } = "";
    public bool ServerDockerInstall { get; set; }
    public string SshPrivateKeyPath { get; set; } = "";
    [JsonIgnore] // DPAPI encrypted separately.
    public string SshPrivateKeyPassphrase { get; set; } = "";

    public List<RouteProfile> Routes { get; set; } = [];
}

public static class SettingsStore
{
    private sealed class Secrets
    {
        public string Password { get; set; } = "";
        public string VkHashes { get; set; } = "";
        public string SshPassword { get; set; } = "";
        public string SshPrivateKeyPassphrase { get; set; } = "";
        public string ServerMainPassword { get; set; } = "";
        public string ServerWebPassword { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KVN");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    private static string SecretsPath => Path.Combine(DirectoryPath, "credentials.dpapi");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var raw = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(raw, JsonOptions) ?? new AppSettings();
            if (string.IsNullOrWhiteSpace(loaded.DeviceId)) loaded.DeviceId = Guid.NewGuid().ToString("N");
            loaded.Routes ??= [];
            loaded.KvnProfiles ??= [];
            loaded.Routes.RemoveAll(p => string.Equals(p.Id, "__VPNSL_FULL_TUNNEL__", StringComparison.Ordinal));

            if (File.Exists(SecretsPath))
            {
                var clear = ProtectedData.Unprotect(
                    File.ReadAllBytes(SecretsPath), null, DataProtectionScope.CurrentUser);
                try { ApplySecrets(loaded, JsonSerializer.Deserialize<Secrets>(clear)); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            else
            {
                // One-time migration of older KVN lab settings from plaintext.
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                string Old(string key) => root.TryGetProperty(key, out var item) &&
                    item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
                ApplySecrets(loaded, new Secrets
                {
                    Password = Old("Password"), VkHashes = Old("VkHashes"),
                    SshPassword = Old("SshPassword"),
                    SshPrivateKeyPassphrase = Old("SshPrivateKeyPassphrase"),
                    ServerMainPassword = Old("ServerMainPassword"),
                    ServerWebPassword = Old("ServerWebPassword"),
                });
                Save(loaded);
            }
            return loaded;
        }
        catch
        {
            // Bad/missing DPAPI material is never treated as plaintext.
            return new AppSettings();
        }
    }

    private static void ApplySecrets(AppSettings settings, Secrets? value)
    {
        if (value is null) return;
        settings.Password = value.Password;
        settings.VkHashes = value.VkHashes;
        settings.SshPassword = value.SshPassword;
        settings.SshPrivateKeyPassphrase = value.SshPrivateKeyPassphrase;
        settings.ServerMainPassword = value.ServerMainPassword;
        settings.ServerWebPassword = value.ServerWebPassword;
    }

    public static void Save(AppSettings settings)
    {
        settings.Routes ??= [];
        settings.KvnProfiles ??= [];
        settings.Routes.RemoveAll(p => string.Equals(p.Id, "__VPNSL_FULL_TUNNEL__", StringComparison.Ordinal));
        Directory.CreateDirectory(DirectoryPath);
        var credentials = JsonSerializer.SerializeToUtf8Bytes(new Secrets
        {
            Password = settings.Password, VkHashes = settings.VkHashes,
            SshPassword = settings.SshPassword,
            SshPrivateKeyPassphrase = settings.SshPrivateKeyPassphrase,
            ServerMainPassword = settings.ServerMainPassword,
            ServerWebPassword = settings.ServerWebPassword,
        });
        try
        {
            var secured = ProtectedData.Protect(credentials, null, DataProtectionScope.CurrentUser);
            var tempSecret = SecretsPath + ".tmp";
            File.WriteAllBytes(tempSecret, secured);
            File.Move(tempSecret, SecretsPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(credentials); }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, FilePath, true);
    }
}
