using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace VPNSL.Windows;

/// <summary>
/// Controls only a KVN-owned AmneziaWG tunnel service. Never modifies the
/// official AmneziaWG manager or an already installed tunnel service.
/// The bundled amneziawg.exe must contain our full-tunnel IPv6 WFP guard.
/// </summary>
internal sealed class KvnAwgTunnel
{
    private const string GuardMarker = "Home VPN: WFP full-tunnel IPv4 and IPv6 leak protection enabled";
    private string? _ownedServiceName;
    private string? _ownedProfileCopy;
    public bool IsActive => _ownedServiceName is not null;

    public async Task ConnectAsync(string sourceProfile, CancellationToken cancellation = default)
    {
        if (IsActive)
            throw new InvalidOperationException("Туннель КВН уже запущен.");

        var source = Path.GetFullPath(sourceProfile);
        if (!string.Equals(Path.GetExtension(source), ".conf", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(source))
            throw new InvalidOperationException("Выберите действующий профиль AWG в формате .conf.");

        var profile = await File.ReadAllBytesAsync(source, cancellation);
        if (profile.Length is < 100 or > 65536)
            throw new InvalidOperationException("Недопустимый размер файла конфигурации AWG.");
        ValidateFullTunnelProfile(Encoding.UTF8.GetString(profile));

        var awgExe = Path.Combine(AppContext.BaseDirectory, "amneziawg.exe");
        if (!File.Exists(awgExe) || !ContainsGuardMarker(awgExe))
            throw new InvalidOperationException(
                "В сборке отсутствует проверенный движок AWG с WFP-защитой IPv6. Подключение запрещено.");

        // Never install over the user's normal AmneziaWG service or a stale
        // KVN service left running after a crash.
        var activeServices = await RunPowerShellAsync(
            "Get-Service -Name 'AmneziaWGTunnel$*' -ErrorAction SilentlyContinue | " +
            "Where-Object {$_.Status -ne 'Stopped'} | Select-Object -ExpandProperty Name",
            cancellation);
        if (!string.IsNullOrWhiteSpace(activeServices))
            throw new InvalidOperationException(
                "В Windows уже работает туннель AmneziaWG. Отключите его перед запуском КВН.");

        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).Substring(0, 12);
        var tunnelName = "KVN-" + id;
        var serviceName = "AmneziaWGTunnel$" + tunnelName;
        var existingService = await QueryServiceStatusAsync(serviceName, cancellation);
        if (existingService != "MISSING")
            throw new InvalidOperationException(
                "Уже существует служба этого профиля. КВН не будет перезаписывать её: " + serviceName);

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KVN", "Profiles");
        Directory.CreateDirectory(directory);
        var copied = Path.Combine(directory, tunnelName + ".conf");
        await File.WriteAllBytesAsync(copied, profile, cancellation);
        var attemptedInstall = false;
        try
        {
            attemptedInstall = true;
            await RunExecutableAsync(awgExe, ["/installtunnelservice", copied], cancellation);

            // The upstream command installs an AUTOMATIC service.
            // KVN makes it demand-start so a reboot cannot silently start
            // a VPN which was only selected for the current application run.
            await RunExecutableAsync("sc.exe", ["config", serviceName, "start=", "demand"], cancellation);

            for (var i = 0; i < 20; i++)
            {
                if (await QueryServiceStatusAsync(serviceName, cancellation) == "RUNNING")
                {
                    _ownedServiceName = serviceName;
                    _ownedProfileCopy = copied;
                    return;
                }
                await Task.Delay(500, cancellation);
            }
            throw new InvalidOperationException("Служба AWG не перешла в состояние Running.");
        }
        catch
        {
            if (attemptedInstall &&
                await QueryServiceStatusAsync(serviceName, CancellationToken.None) != "MISSING")
            {
                try { await RunExecutableAsync(awgExe, ["/uninstalltunnelservice", tunnelName], CancellationToken.None); }
                catch { /* Never remove someone else's service. Stale KVN service is detected on next connect. */ }
            }
            // Retain config when a service still refers to it.
            if (await QueryServiceStatusAsync(serviceName, CancellationToken.None) == "MISSING")
                File.Delete(copied);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var service = _ownedServiceName;
        if (service is null) return;
        var tunnelName = service["AmneziaWGTunnel$".Length..];
        var awgExe = Path.Combine(AppContext.BaseDirectory, "amneziawg.exe");
        await RunExecutableAsync(awgExe, ["/uninstalltunnelservice", tunnelName], CancellationToken.None);

        // Don't claim disconnected while the service is still running.
        for (var i = 0; i < 20; i++)
        {
            if (await QueryServiceStatusAsync(service, CancellationToken.None) == "MISSING")
            {
                _ownedServiceName = null;
                if (_ownedProfileCopy is not null)
                {
                    File.Delete(_ownedProfileCopy);
                    _ownedProfileCopy = null;
                }
                return;
            }
            await Task.Delay(300);
        }
        throw new InvalidOperationException("Windows ещё не удалила службу AWG. Проверьте службы вручную.");
    }

    private static void ValidateFullTunnelProfile(string content)
    {
        var section = "";
        var hasPrivateKey = false;
        var hasEndpoint = false;
        var hasFullIpv4 = false;
        foreach (var entry in content.Split('\n'))
        {
            var line = entry.Split('#', 2)[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator < 1) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase) &&
                key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase))
                hasPrivateKey |= value.Length > 20;
            if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
                    hasEndpoint |= value.Contains(':');
                if (key.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase))
                    hasFullIpv4 |= value.Split(',').Any(x => x.Trim() == "0.0.0.0/0");
            }
        }
        if (!hasPrivateKey || !hasEndpoint || !hasFullIpv4)
            throw new InvalidOperationException(
                "Профиль должен содержать PrivateKey, Endpoint и AllowedIPs = 0.0.0.0/0. " +
                "Другие режимы пока не прошли проверку IPv6 и не запускаются.");
    }

    private static bool ContainsGuardMarker(string file)
    {
        // Build-integrity preflight, not a substitute for signature validation.
        var binary = File.ReadAllBytes(file);
        return binary.AsSpan().IndexOf(Encoding.UTF8.GetBytes(GuardMarker)) >= 0;
    }

    private static async Task<string> QueryServiceStatusAsync(string service, CancellationToken cancellation)
    {
        var cmd = "$x=Get-Service -Name '" + service + "' -ErrorAction SilentlyContinue; " +
                  "if($null -eq $x){'MISSING'}else{$x.Status.ToString().ToUpperInvariant()}";
        return (await RunPowerShellAsync(cmd, cancellation)).Trim();
    }

    private static Task<string> RunPowerShellAsync(string script, CancellationToken cancellation) =>
        RunExecutableAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script], cancellation);

    private static async Task<string> RunExecutableAsync(
        string executable, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ??
            throw new InvalidOperationException("Не удалось запустить " + Path.GetFileName(executable));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw;
        }
        var output = await stdout;
        await stderr; // Deliberately never log private profile paths or configuration.
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                Path.GetFileName(executable) + " завершился с кодом " + process.ExitCode);
        return output.Trim();
    }
}
