using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPNSL.Windows;

/// <summary>Pure local tests. Never creates a tunnel, route or user configuration.</summary>
internal static class KvnSelfTest
{
    public static int ValidateWfp()
    {
        try
        {
            using (KvnIpv6Guard.Enable())
            {
                Console.WriteLine("KVN_WFP_FILTERS_ACTIVE");
            }
            Console.WriteLine("KVN_WFP_FILTERS_REMOVED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("KVN_WFP_FAILED: " + ex.Message);
            return 14;
        }
    }

    public static int Run()
    {
        try
        {
            // ABI selftest only, does not install WFP filters or touch routes.
            KvnIpv6Guard.CheckNativeLayout();
            const string sample = """
                {
                  "format": "kvn-csqtt",
                  "version": 1,
                  "name": "TestOnly",
                  "endpoint": "example.test:46222",
                  "vk_hashes": ["TESTONLYhash12345678901234567890"],
                  "password": "TestPassword12345"
                }
                """;
            var profile = KvnProfileStore.ParseCsqtt(sample);
            if (profile.Name != "TestOnly" || profile.Endpoint != "example.test:46222" ||
                profile.VkHashes != "TESTONLYhash12345678901234567890" ||
                profile.Password != "TestPassword12345")
                throw new InvalidOperationException("CSQTT profile parser mismatch.");

            var bad = sample.Replace("example.test:46222", "example.test:99999");
            try
            {
                KvnProfileStore.ParseCsqtt(bad);
                throw new InvalidOperationException("Invalid CSQTT port was accepted.");
            }
            catch (FormatException) { /* Expected invalid profile */ }

            // Check the current-user Windows DPAPI round trip without writing
            // anything to the user's settings or profiles.
            var secret = Encoding.UTF8.GetBytes(sample);
            try
            {
                var encrypted = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
                if (encrypted.AsSpan().IndexOf(Encoding.UTF8.GetBytes("TestPassword12345")) >= 0)
                    throw new InvalidOperationException("DPAPI blob contains plaintext.");
                var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                try
                {
                    if (!secret.SequenceEqual(clear))
                        throw new InvalidOperationException("DPAPI round-trip mismatch.");
                }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            finally { CryptographicOperations.ZeroMemory(secret); }

            var serialized = JsonSerializer.Serialize(new AppSettings
            {
                Password = "SENSITIVE_TEST_PASSWORD_123456",
                VkHashes = "SENSITIVE_TEST_VK_HASH_123456",
                SshPassword = "SENSITIVE_TEST_SSH_PASSWORD",
                ServerWebPassword = "SENSITIVE_TEST_SERVER_PASSWORD",
            });
            if (serialized.Contains("SENSITIVE_TEST", StringComparison.Ordinal))
                throw new InvalidOperationException("A secret was serialized into settings.json.");

            Console.WriteLine("KVN_PROFILE_SELFTEST_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("KVN_PROFILE_SELFTEST_FAILED: " + ex.GetType().Name);
            return 12;
        }
    }
}
