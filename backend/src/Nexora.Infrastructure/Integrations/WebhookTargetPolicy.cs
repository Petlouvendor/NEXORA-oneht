using System.Net;
using System.Net.Sockets;

namespace Nexora.Infrastructure.Integrations;

/// <summary>
/// Protection SSRF des webhooks sortants (analyse exhaustive 2026-10, §6.5). Une URL saisie par un client ne doit pas
/// faire appeler par le serveur une adresse interne : boucle locale, réseau privé, métadonnées du nuage
/// (169.254.169.254), adresses réservées. Le contrôle se fait à l'enregistrement (refus clair) ET à la connexion, après
/// résolution DNS — un nom public qui se résout ensuite vers une adresse interne (rebinding) est bloqué aussi.
/// Une installation sur site qui doit appeler un système du réseau local active
/// <c>Integrations:AllowPrivateWebhookTargets</c>.
/// </summary>
public static class WebhookTargetPolicy
{
    /// <summary>Hôte de simulation (bac à sable §42) : jamais appelé sur le réseau.</summary>
    public const string SimulationHost = "sim.webhook.local";

    /// <summary>Contrôle de l'URL avant enregistrement ; rend le motif du refus, ou null.</summary>
    public static string? Validate(string? url, bool allowPrivate)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "URL cible absolue http(s) requise.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "L'URL cible ne doit pas contenir d'identifiants.";
        if (string.Equals(uri.Host, SimulationHost, StringComparison.OrdinalIgnoreCase) || allowPrivate)
            return null;
        if (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return "Adresse interne refusée : la cible d'un webhook doit être joignable sur Internet.";
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !IsPublic(ip))
            return "Adresse interne refusée : la cible d'un webhook doit être joignable sur Internet.";
        return null;
    }

    /// <summary>Adresse publique routable ? (faux pour boucle locale, privé, lien local, partagé, réservé, multidiffusion).</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                    // 0.0.0.0/8
                || b[0] == 10                                     // 10.0.0.0/8
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)     // 100.64.0.0/10 (CGNAT)
                || b[0] == 127                                    // 127.0.0.0/8
                || (b[0] == 169 && b[1] == 254)                   // 169.254.0.0/16 (lien local, métadonnées du nuage)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)      // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)        // 192.0.0.0/24
                || (b[0] == 192 && b[1] == 168)                   // 192.168.0.0/16
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))    // 198.18.0.0/15
                || b[0] >= 224);                                  // multidiffusion, réservé, diffusion
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return false;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;              // fc00::/7 (adresses locales uniques)
            return true;
        }
        return false;
    }

    /// <summary>
    /// Gestionnaire HTTP qui refuse, au moment de la connexion, toute adresse résolue non publique, et ne suit pas les
    /// redirections (une redirection vers une adresse interne contournerait le contrôle).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(a => allowPrivate || IsPublic(a))
                ?? throw new HttpRequestException(
                    $"Adresse interne refusée pour {context.DnsEndPoint.Host} : la cible d'un webhook doit être joignable sur Internet.");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
