using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TravelApi.Infrastructure.Ai;

/// <summary>Veredicto de revisar una direccion antes de usarla.</summary>
public enum AiEndpointVerdict
{
    /// <summary>La direccion sirve: es https, esta completa y apunta a internet.</summary>
    Ok = 0,

    /// <summary>Vacia, mal escrita, sin https, o con usuario/clave adentro de la direccion.</summary>
    Malformed = 1,

    /// <summary>Apunta a la red interna del servidor (o a un nombre que resuelve ahi).</summary>
    PrivateOrInternal = 2,

    /// <summary>El nombre no se pudo resolver: no existe o no hay salida a internet.</summary>
    Unresolvable = 3,
}

/// <summary>
/// Revisa que la "direccion" que carga el Admin sea una direccion legitima de internet ANTES de
/// que el servidor le pegue.
///
/// <para><b>Por que esto existe (agujero real, no ceremonia)</b>: el endpoint "Probar conexion"
/// hace que el SERVIDOR abra una conexion a una direccion que escribe el usuario. Sin control, ese
/// boton se convierte en una sonda para espiar la red interna del servidor: bases de datos,
/// paneles internos, o el servicio de metadatos de la nube (169.254.169.254), que en muchos
/// proveedores entrega credenciales de la maquina. Es el clasico SSRF.</para>
///
/// <para><b>La regla</b>: solo <c>https</c>, direccion absoluta, sin usuario:clave adentro, y el
/// nombre tiene que resolver a direcciones PUBLICAS. Si cualquiera de las direcciones a las que
/// resuelve es interna (loopback, red privada, link-local, metadatos, etc.), se rechaza entera.</para>
///
/// <para><b>Limitacion conocida (declarada, no tapada)</b>: entre que revisamos el nombre y que el
/// cliente HTTP se conecta, un servidor de nombres hostil podria contestar distinto ("DNS
/// rebinding"). Cerrar eso del todo exige conectarse a mano a la IP ya verificada. No se hace hoy:
/// el atacante tendria que ser un Admin de la propia agencia (el unico que puede tocar esta
/// pantalla), y el resultado que obtendria es un codigo de cinco valores, sin cuerpo de respuesta.</para>
/// </summary>
public sealed class AiEndpointGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHostAddresses;

    /// <param name="resolveHostAddresses">
    /// Como resolver un nombre a direcciones. Se puede reemplazar en los tests para no depender de
    /// que la maquina que corre la suite tenga internet.
    /// </param>
    public AiEndpointGuard(Func<string, CancellationToken, Task<IPAddress[]>>? resolveHostAddresses = null)
    {
        _resolveHostAddresses = resolveHostAddresses ?? DefaultResolveAsync;
    }

    private static Task<IPAddress[]> DefaultResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// Revisa la direccion. La parte de formato (https, absoluta) es inmediata; la resolucion del
    /// nombre solo se intenta si el formato ya paso.
    /// </summary>
    public async Task<AiEndpointVerdict> CheckAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return AiEndpointVerdict.Malformed;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return AiEndpointVerdict.Malformed;
        }

        // Solo https: una direccion en http mandaria la clave del proveedor en claro por la red,
        // y ademas es el disfraz mas comun para apuntar a un servicio interno.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return AiEndpointVerdict.Malformed;
        }

        // "https://usuario:clave@host" mete credenciales en la direccion y confunde a los parsers
        // (truco clasico para que una revision superficial lea mal cual es el host real).
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return AiEndpointVerdict.Malformed;
        }

        // Truco de .NET: para una direccion IPv6, Uri.Host devuelve el host CON los corchetes
        // ("[::1]"), y asi no lo puede leer IPAddress.TryParse. Hay que sacarselos.
        var host = uri.HostNameType == UriHostNameType.IPv6
            ? uri.Host.Trim('[', ']')
            : uri.Host;

        // Si el host YA es una direccion numerica, no hay nada que resolver: se revisa directo.
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            return IsInternalAddress(literalAddress)
                ? AiEndpointVerdict.PrivateOrInternal
                : AiEndpointVerdict.Ok;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await _resolveHostAddresses(host, cancellationToken);
        }
        catch (SocketException)
        {
            // El nombre no existe o no hay salida a internet. Para el usuario es lo mismo:
            // "esa direccion no responde".
            return AiEndpointVerdict.Unresolvable;
        }
        catch (ArgumentException)
        {
            // Host con formato que el resolvedor rechaza (por ejemplo, demasiado largo).
            return AiEndpointVerdict.Malformed;
        }

        if (addresses.Length == 0)
        {
            return AiEndpointVerdict.Unresolvable;
        }

        // Alcanza con que UNA de las direcciones sea interna para rechazar: un nombre que resuelve
        // a varias podria estar mezclando una publica de fachada con una interna real.
        if (addresses.Any(IsInternalAddress))
        {
            return AiEndpointVerdict.PrivateOrInternal;
        }

        return AiEndpointVerdict.Ok;
    }

    /// <summary>
    /// ¿Esta direccion pertenece a la propia maquina o a la red interna? Cubre las dos familias
    /// (IPv4 e IPv6), incluidas las IPv4 "disfrazadas" de IPv6 (::ffff:10.0.0.1).
    /// </summary>
    public static bool IsInternalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsInternalAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsInternalIpv4(address);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsInternalIpv6(address);
        }

        // Familia rara (IPX y compania): no es internet, no se usa.
        return true;
    }

    private static bool IsInternalIpv4(IPAddress address)
    {
        var octets = address.GetAddressBytes();

        // 0.0.0.0/8 — "esta red". Apunta a la propia maquina en muchos sistemas.
        if (octets[0] == 0) return true;

        // 10.0.0.0/8 — red privada.
        if (octets[0] == 10) return true;

        // 127.0.0.0/8 — la propia maquina (IsLoopback ya cubre 127.0.0.1, esto cubre el resto).
        if (octets[0] == 127) return true;

        // 169.254.0.0/16 — link-local. Aca vive el servicio de METADATOS de la nube
        // (169.254.169.254), que en varios proveedores entrega credenciales de la maquina.
        if (octets[0] == 169 && octets[1] == 254) return true;

        // 172.16.0.0/12 — red privada.
        if (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) return true;

        // 192.168.0.0/16 — red privada (la de cualquier oficina).
        if (octets[0] == 192 && octets[1] == 168) return true;

        // 100.64.0.0/10 — red compartida del proveedor de internet (CGNAT).
        if (octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127) return true;

        // 192.0.0.0/24 y 192.0.2.0/24 — reservadas por IANA / documentacion.
        if (octets[0] == 192 && octets[1] == 0 && (octets[2] == 0 || octets[2] == 2)) return true;

        // 198.18.0.0/15 — pruebas de rendimiento entre redes.
        if (octets[0] == 198 && (octets[1] == 18 || octets[1] == 19)) return true;

        // 198.51.100.0/24 y 203.0.113.0/24 — documentacion.
        if (octets[0] == 198 && octets[1] == 51 && octets[2] == 100) return true;
        if (octets[0] == 203 && octets[1] == 0 && octets[2] == 113) return true;

        // 224.0.0.0/4 multicast y 240.0.0.0/4 reservada (incluye 255.255.255.255).
        if (octets[0] >= 224) return true;

        return false;
    }

    private static bool IsInternalIpv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        // fc00::/7 — direcciones unicas locales (el equivalente IPv6 de la red privada).
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return true;
        }

        // :: (sin especificar) — no apunta a ningun lado util y algunos sistemas la tratan como local.
        if (address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        return false;
    }
}
