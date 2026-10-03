using System.IO;
using System.Net;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MySmdb;

/// <summary>
/// Túneles SSH de las conexiones MySQL que los usan. Se abre uno por servidor SSH y destino la primera vez
/// que hace falta, lo comparten pestañas y explorador, y se cierra al desconectar o al salir.
/// La huella del servidor SSH se guarda la primera vez; si después cambia, se rechaza la conexión.
/// </summary>
public static class SshTunnels
{
    private sealed record Tunnel(SshClient Client, ForwardedPortLocal Forward, uint LocalPort, string? Fingerprint);

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Tunnel> Open = new();

    private static string Key(ConnectionProfile p) => $"{p.SshUser}@{p.SshHost}:{p.SshPort}>{p.Host}:{p.Port}";

    /// <summary>
    /// Puerto local (127.0.0.1) que lleva a la base a través del túnel. Abre el túnel si no lo está.
    /// Bloquea mientras conecta: se llama fuera del hilo de la interfaz.
    /// </summary>
    public static uint LocalPort(ConnectionProfile profile)
    {
        lock (Lock)
        {
            string key = Key(profile);
            if (Open.TryGetValue(key, out var tunnel))
            {
                if (tunnel.Client.IsConnected && tunnel.Forward.IsStarted)
                {
                    // Otra copia de la conexión (p. ej. "Probar" y luego "Conectar") hereda la huella aceptada.
                    profile.SshHostKey ??= tunnel.Fingerprint;
                    return tunnel.LocalPort;
                }
                Dispose(tunnel);   // se cayó (red, reinicio del servidor...): se vuelve a abrir
                Open.Remove(key);
            }

            bool hostKeyChanged = false;
            var client = new SshClient(BuildConnectionInfo(profile)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            client.HostKeyReceived += (_, e) =>
            {
                string fingerprint = e.FingerPrintSHA256;
                if (string.IsNullOrEmpty(profile.SshHostKey))
                {
                    profile.SshHostKey = fingerprint;   // primera conexión: se confía y se recuerda
                    e.CanTrust = true;
                }
                else
                {
                    e.CanTrust = profile.SshHostKey == fingerprint;
                    hostKeyChanged = !e.CanTrust;
                }
            };

            try
            {
                client.Connect();
            }
            catch (Exception) when (hostKeyChanged)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    $"La huella del servidor SSH {profile.SshHost} cambió desde la última conexión. Puede ser una reinstalación del " +
                    "servidor o un intento de interceptar la conexión. Si sabes que el cambio es legítimo, en la pantalla de conexión " +
                    "desmarca y vuelve a marcar «Conectar mediante un túnel SSH» y conecta: se aceptará la huella nueva.");
            }
            catch (SshAuthenticationException)
            {
                client.Dispose();
                throw new InvalidOperationException($"El servidor SSH {profile.SshHost} rechazó el usuario, la contraseña o la clave.");
            }
            catch
            {
                client.Dispose();
                throw;
            }

            uint port = FreeLocalPort();
            var forward = new ForwardedPortLocal("127.0.0.1", port, profile.Host, profile.Port);
            client.AddForwardedPort(forward);
            forward.Start();
            Open[key] = new Tunnel(client, forward, port, profile.SshHostKey);
            return port;
        }
    }

    private static ConnectionInfo BuildConnectionInfo(ConnectionProfile profile)
    {
        string host = profile.SshHost ?? throw new InvalidOperationException("Falta el servidor SSH.");
        string user = profile.SshUser ?? throw new InvalidOperationException("Falta el usuario SSH.");

        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(profile.SshKeyFile))
        {
            var key = string.IsNullOrEmpty(profile.SshPassphrase)
                ? new PrivateKeyFile(profile.SshKeyFile)
                : new PrivateKeyFile(profile.SshKeyFile, profile.SshPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        if (!string.IsNullOrEmpty(profile.SshPassword))
        {
            methods.Add(new PasswordAuthenticationMethod(user, profile.SshPassword));
            // Algunos servidores piden la contraseña como "keyboard-interactive".
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = profile.SshPassword;
            };
            methods.Add(interactive);
        }
        return new ConnectionInfo(host, (int)profile.SshPort, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /// <summary>Un puerto libre de 127.0.0.1, elegido por el sistema.</summary>
    private static uint FreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        uint port = (uint)((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void Close(ConnectionProfile profile)
    {
        if (!profile.UseSsh) return;
        lock (Lock)
        {
            string key = Key(profile);
            if (Open.Remove(key, out var tunnel)) Dispose(tunnel);
        }
    }

    public static void CloseAll()
    {
        lock (Lock)
        {
            foreach (var tunnel in Open.Values) Dispose(tunnel);
            Open.Clear();
        }
    }

    private static void Dispose(Tunnel tunnel)
    {
        try { tunnel.Forward.Stop(); } catch { }
        try { tunnel.Client.Dispose(); } catch { }
    }
}
