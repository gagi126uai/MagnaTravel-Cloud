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
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada, "el usuario tiene que poder volver atrás") +
/// Parte C "Restaurar TOTAL" (2026-07-28, firmada por el dueño): orquesta la restauración de un backup de
/// Postgres generado por <c>SystemDataWipeService</c> (u otro backup del mismo directorio). Tres modos,
/// deliberadamente distintos en alcance:
///
/// <list type="bullet">
///   <item><b><c>prueba</c></b>: restaura el backup COMPLETO a una base espejo separada
///   (<c>&lt;db&gt;_shadow</c>) y devuelve conteos para que el usuario verifique "¿esto es lo que
///   necesitaba?" — NUNCA toca la base viva.</item>
///   <item><b><c>real</c></b>: restaura SOLO las tablas de configuración (AFIP, políticas de aprobación, bot
///   de WhatsApp, ajustes generales — ver <c>TravelApi.Application.Constants.WipeGroups.ConfiguracionTables</c>),
///   data-only, y SOLO sobre tablas que estén vacías en la base viva. Pensado para "recuperé de más al usar
///   Empezar de cero y necesito la configuración de vuelta", SIN cortar ninguna conexión ni interrumpir el
///   servicio (esas tablas no tienen foreign keys entre sí ni con el resto del sistema).</item>
///   <item><b><c>total</c></b> (Parte C): reemplaza TODA la base viva por la foto del backup — la operación
///   más invasiva de las tres. Con backup previo OBLIGATORIO del estado que se pisa (el "deshacer del
///   deshacer"), modo mantenimiento activo mientras dura (el sistema responde 503 a casi todo
///   <c>/api/**</c> — ver <c>TravelApi.Middleware.MaintenanceModeMiddleware</c>), corte de conexiones activas,
///   y <c>pg_restore --clean --if-exists --single-transaction</c> (si algo falla a mitad de camino, Postgres
///   hace ROLLBACK automático: la base queda exactamente como estaba). Antes de esta obra, un restore total NO
///   podía hacerse desde dentro del proceso de la API sin detener los contenedores primero (ver
///   <c>scripts/ops/restore-db.sh --target primary</c>); esta obra lo resuelve activando mantenimiento ANTES
///   de cortar conexiones, en vez de depender de que un operador humano pare los contenedores a mano.</item>
/// </list>
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
    /// coincide, la contraseña es incorrecta, el archivo/modo/tablas no son válidos, (modo <c>real</c>)
    /// alguna tabla pedida ya tenía datos, o (modo <c>total</c>) el motivo falta/es muy corto, hay una
    /// restauración ya en curso, el esquema del backup es incompatible, o hay comprobantes fiscales reales en
    /// juego — en todos esos casos NO se restaura nada.
    /// </summary>
    /// <param name="motivo">
    /// Obligatorio (mínimo 10 caracteres) SOLO para <see cref="RestoreModes.Total"/> — por qué se ejecuta la
    /// restauración más destructiva del sistema. Ignorado en los otros modos.
    /// </param>
    Task<SystemDataRestoreResponse> ExecuteRestoreAsync(
        string requesterUserId,
        string password,
        string phrase,
        string fileName,
        string modo,
        IReadOnlyList<string>? tablas,
        string? motivo,
        CancellationToken ct);
}
