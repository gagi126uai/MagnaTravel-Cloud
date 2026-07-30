using System.Text.RegularExpressions;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-052 (recomendación N1 de seguridad de la re-review): la regla ÚNICA de "qué nombre de archivo de resguardo
/// se acepta". La comparten el servicio (borde de la aplicación) y el puerto (borde del proceso externo), porque
/// tener dos definiciones distintas de "nombre seguro" es exactamente cómo se cuela un caso.
///
/// <para><b>Por qué lista blanca y no lista negra</b>: el nombre termina como argumento entrecomillado de
/// <c>pg_restore</c>. Enumerar lo prohibido (comillas, punto-punto, barras...) deja siempre algo afuera; enumerar
/// lo permitido (letras, números, punto, guion, guion bajo) no puede dejar pasar nada que sirva para cerrar el
/// entrecomillado y agregar flags al comando. Los nombres reales del sistema
/// (<c>wipe-20260727-223313.dump</c>, <c>pre-restore-20260728-090000.dump</c>) entran sin problema.</para>
/// </summary>
public static class SafeBackupFileNameRules
{
    /// <summary>
    /// Nombre "pelado" (sin carpetas) de un archivo de resguardo. <c>RegexOptions.Compiled</c> porque esto se
    /// evalúa una vez por archivo en cada listado.
    /// </summary>
    private static readonly Regex SafeFileNamePattern =
        new(@"^[A-Za-z0-9._-]+\.dump$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSafe(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        // Path.GetFileName sigue chequeándose además del patrón: el patrón ya rechaza barras, pero esta línea deja
        // explícito (y a prueba de un cambio futuro del patrón) que acá NUNCA puede entrar una ruta.
        return Path.GetFileName(fileName) == fileName && SafeFileNamePattern.IsMatch(fileName);
    }
}
