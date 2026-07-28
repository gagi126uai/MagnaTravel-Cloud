using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada): se lanza cuando la restauración NO puede
/// ejecutarse (frase que no coincide, contraseña incorrecta, archivo inválido/inexistente, modo desconocido,
/// tabla fuera de la lista permitida, o alguna tabla destino ya tenía datos). El mensaje YA viene en criollo.
/// </summary>
public sealed class SystemDataRestoreRefusedException : Exception
{
    public SystemDataRestoreRefusedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada, "el usuario tiene que poder volver atrás"):
/// orquesta la restauración de un backup de Postgres generado por <c>SystemDataWipeService</c> (u otro backup
/// del mismo directorio). Dos modos, deliberadamente distintos en alcance:
///
/// <list type="bullet">
///   <item><b><c>prueba</c></b>: restaura el backup COMPLETO a una base espejo separada
///   (<c>&lt;db&gt;_shadow</c>) y devuelve conteos para que el usuario verifique "¿esto es lo que
///   necesitaba?" — NUNCA toca la base viva.</item>
///   <item><b><c>real</c></b>: restaura SOLO las tablas de configuración (AFIP, políticas de aprobación, bot
///   de WhatsApp, ajustes generales — ver <c>TravelApi.Application.Constants.WipeGroups.ConfiguracionTables</c>),
///   data-only, y SOLO sobre tablas que estén vacías en la base viva.</item>
/// </list>
///
/// <para><b>Por qué el modo <c>real</c> NO es un restore total de la base viva</b>: un restore total mientras
/// la aplicación está corriendo necesita cortar TODAS las conexiones activas a esa base (incluida la que
/// atiende el propio pedido HTTP que dispara la restauración) y recrear el schema entero — el script existente
/// <c>scripts/ops/restore-db.sh --target primary</c> resuelve esto deteniendo los contenedores <c>api</c>/<c>worker</c>
/// ANTES de restaurar, algo que el proceso de la API no puede hacerse a sí mismo de forma segura. En cambio,
/// el caso de uso real más común — "recuperé de más al usar Empezar de cero y necesito la configuración de
/// vuelta" — se cubre por completo restaurando solo esas 5 tablas standalone (sin foreign keys entre sí) sobre
/// tablas vacías, sin necesitar cortar ninguna conexión.</para>
/// </summary>
public interface ISystemDataRestoreService
{
    /// <summary>Lista los backups disponibles para restaurar.</summary>
    Task<SystemDataBackupsResponse> ListBackupsAsync(CancellationToken ct);

    /// <summary>
    /// Valida un backup (sin restaurar nada). Tira <see cref="SystemDataRestoreRefusedException"/> si el
    /// nombre de archivo es inválido — ese rechazo también queda auditado (hallazgo menor de seguridad:
    /// intentos con nombres de archivo al azar son información de seguridad relevante).
    /// </summary>
    Task<SystemDataRestoreVerifyResponse> VerifyBackupAsync(string requesterUserId, string fileName, CancellationToken ct);

    /// <summary>
    /// Ejecuta la restauración real. Tira <see cref="SystemDataRestoreRefusedException"/> si la frase no
    /// coincide, la contraseña es incorrecta, el archivo/modo/tablas no son válidos, o (modo <c>real</c>)
    /// alguna tabla pedida ya tenía datos — en todos esos casos NO se restaura nada.
    /// </summary>
    Task<SystemDataRestoreResponse> ExecuteRestoreAsync(
        string requesterUserId,
        string password,
        string phrase,
        string fileName,
        string modo,
        IReadOnlyList<string>? tablas,
        CancellationToken ct);
}
