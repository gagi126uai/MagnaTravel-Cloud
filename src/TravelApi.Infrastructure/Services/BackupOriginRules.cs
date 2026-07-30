using TravelApi.Application.DTOs;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Rediseño de la pantalla de resguardos (2026-07-30, firmado por el dueño, §7 punto 1): traduce el ORIGEN de
/// un resguardo a la frase en criollo que el usuario ve en la columna "Por qué se guardó".
///
/// <para><b>De dónde sale el origen</b>: del PREFIJO con el que el motor bautiza cada archivo al crearlo
/// (<c>wipe-&lt;fecha&gt;.dump</c> para "Empezar de cero", <c>pre-restore-&lt;fecha&gt;.dump</c> para el
/// resguardo que se toma antes de "Restaurar todo"). No hay ningún registro en la base que diga de dónde salió
/// cada archivo — el nombre ES el único dato disponible, y por eso los constructores de nombre de AMBOS
/// servicios usan las constantes de acá (así, si alguien renombra un prefijo, la columna no empieza a mentir en
/// silencio).</para>
///
/// <para><b>T-5 / P-17</b>: el prefijo y el nombre del archivo son detalle INTERNO. Lo único que viaja al front
/// es la frase ya traducida. Cualquier archivo cuyo origen no se pueda determinar (por ejemplo el resguardo
/// diario que genera el sidecar de <c>docker-compose.yml</c>, o uno copiado a mano al volumen) cae en
/// <see cref="BackupOriginLabels.Manual"/> — nunca se adivina un origen que no consta.</para>
/// </summary>
public static class BackupOriginRules
{
    /// <summary>Prefijo de los resguardos que genera "Empezar de cero" (ver <c>SystemDataWipeService</c>).</summary>
    public const string WipeFileNamePrefix = "wipe-";

    /// <summary>Prefijo del resguardo previo que genera "Restaurar todo" (ver <c>SystemDataRestoreService</c>).</summary>
    public const string PreRestoreFileNamePrefix = "pre-restore-";

    /// <summary>
    /// Devuelve la frase en criollo del origen de <paramref name="fileName"/>. Comparación <c>Ordinal</c> (no
    /// "ignorando mayúsculas") a propósito: los nombres los escribe el motor SIEMPRE en minúsculas, así que una
    /// coincidencia relajada solo serviría para etiquetar mal un archivo que alguien dejó a mano.
    /// </summary>
    public static string DescribeOrigin(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BackupOriginLabels.Manual;
        }

        if (fileName.StartsWith(PreRestoreFileNamePrefix, StringComparison.Ordinal))
        {
            return BackupOriginLabels.BeforeRestore;
        }

        if (fileName.StartsWith(WipeFileNamePrefix, StringComparison.Ordinal))
        {
            return BackupOriginLabels.AfterWipe;
        }

        return BackupOriginLabels.Manual;
    }
}
