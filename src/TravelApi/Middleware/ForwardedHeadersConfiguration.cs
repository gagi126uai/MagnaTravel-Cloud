using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace TravelApi.Middleware;

/// <summary>
/// Configuracion COMPARTIDA de <see cref="ForwardedHeadersOptions"/> entre <c>Program.cs</c> y los
/// tests que verifican la semantica real de deteccion de IP detras de los dos nginx (el del HOST del
/// VPS, fuera de este repo, y el del contenedor "web"). Vive en un solo lugar a proposito: asi un test
/// nunca puede "aprobar" una configuracion que el arranque real dejo de usar.
/// </summary>
public static class ForwardedHeadersConfiguration
{
    /// <summary>
    /// Hallazgo 2026-08-06 (revision de seguridad, bloqueante B1): "api" jamas recibe trafico que no
    /// venga de DENTRO de la red de docker-compose (no publica su puerto al host, ver docker-compose.yml
    /// -"expose", nunca "ports"-). El primer intento de arreglo confiaba en CUALQUIER peer inmediato
    /// (KnownNetworks/KnownProxies vacios) — eso deja el header X-Forwarded-For en manos de quien lo
    /// mande: los dos nginx de la cadena (el del host del VPS y el del contenedor "web", ver
    /// src/TravelWeb/nginx.conf:13,43) APPENDEAN la IP del cliente al FINAL del header en vez de
    /// reemplazarlo, asi que un atacante puede escribir un prefijo INVENTADO al PRINCIPIO del header y,
    /// si confiamos en todo el recorrido, ese prefijo termina tomandose como la IP real -> cualquier
    /// limite por IP (incluido el freno de fuerza bruta de /auth/login) se saltea rotando ese prefijo en
    /// cada pedido.
    ///
    /// FIX: en vez de confiar en TODOS, confiamos SOLO en las redes PRIVADAS (donde viven los dos nginx
    /// y los contenedores de docker-compose: loopback + los tres rangos RFC1918). El algoritmo de
    /// <see cref="Microsoft.AspNetCore.Builder.ForwardedHeadersExtensions"/> lee el header de DERECHA a
    /// IZQUIERDA (el salto mas reciente primero) y se DETIENE en la primera IP que NO esta en esta
    /// lista de confianza — es decir, se detiene justo en la IP PUBLICA del cliente real, porque esa
    /// nunca puede ser una IP privada. El prefijo inventado que el atacante haya puesto mas a la
    /// izquierda (antes de la IP real) NUNCA se alcanza.
    ///
    /// ForwardLimit=2 (NUNCA null): son dos saltos reales confirmados en la topologia de produccion
    /// (nginx del host del VPS -> contenedor "web" -> "api"). Ponerlo en null (procesar TODA la cadena
    /// sin tope) es innecesario con la lista de redes confiables ya funcionando como freno, y deja de
    /// ser una segunda linea de defensa si el dia de mañana algo agrega mas saltos privados de los
    /// esperados (un balanceador nuevo, por ejemplo): mejor fallar mostrando una IP intermedia de la
    /// red privada que arriesgarse a recorrer una cadena mas larga de lo previsto.
    ///
    /// RESIDUAL (documentado a proposito, no es un "no se hizo"): esto depende de que ambos nginx
    /// sigan APPENDEANDO (no reemplazando) el header, y de que ningun peer con IP dentro de estos
    /// rangos privados pueda conectarse a "api" salvo los contenedores propios de docker-compose. El
    /// candado DEFINITIVO -no este, que es defensa en profundidad- es que "api" nunca publique su
    /// puerto al host.
    /// </summary>
    public static ForwardedHeadersOptions Build()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 2
        };

        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var network in TrustedPrivateNetworks())
        {
            options.KnownNetworks.Add(network);
        }

        // KnownProxies es para direcciones EXACTAS (no rangos). Mismo criterio que el default de
        // ASP.NET Core: la variante IPv6 de loopback no se expresa comoda como IPNetwork acá.
        options.KnownProxies.Add(IPAddress.IPv6Loopback);

        return options;
    }

    /// <summary>
    /// Rangos privados (RFC 1918) + loopback IPv4: donde viven los nginx de la cadena y los
    /// contenedores de docker-compose. Cualquier IP FUERA de estos rangos es, por definicion, una IP
    /// publica -> no puede ser un salto interno nuestro, tiene que ser el cliente real (o alguien
    /// intentando hacerse pasar por uno, pero eso ya lo frena el algoritmo de arriba).
    /// </summary>
    // Nombre completo a proposito: .NET 8 agrego System.Net.IPNetwork, que colisiona con el
    // tipo propio de ASP.NET Core (Microsoft.AspNetCore.HttpOverrides.IPNetwork, el que
    // realmente espera ForwardedHeadersOptions.KnownNetworks). Sin calificar, el compilador
    // no puede elegir entre los dos.
    private static IEnumerable<Microsoft.AspNetCore.HttpOverrides.IPNetwork> TrustedPrivateNetworks()
    {
        yield return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("127.0.0.0"), 8);
        yield return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("10.0.0.0"), 8);
        yield return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("172.16.0.0"), 12);
        yield return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("192.168.0.0"), 16);
    }
}
