namespace TravelApi.Application.Interfaces;

/// <summary>
/// Obra "Empezar de cero" (2026-07-27): resultado de intentar generar (o limpiar) el backup previo al
/// borrado masivo. <see cref="Success"/> en <c>false</c> significa "no se borra nada" — el caller
/// (<c>SystemDataWipeService</c>) NUNCA arranca la transacción de borrado si el backup no se pudo generar.
///
/// <para><b>Fix bloqueante #1 (revisión 2026-07-27)</b>: <see cref="CopiedObjectKeys"/> guarda las claves
/// ORIGINALES (no las de destino) de los objetos de MinIO que <c>CreateBackupAsync</c> copió y verificó con
/// éxito al prefijo de backup. <c>SystemDataWipeService</c> usa esta lista recién DESPUÉS de que la
/// transacción de borrado hizo commit, para borrar los originales — nunca antes.</para>
/// </summary>
public sealed record WipeBackupResult(
    bool Success,
    string? BackupFileName,
    string? MinioPrefix,
    string? ErrorMessage,
    IReadOnlyList<string>? CopiedObjectKeys = null);

/// <summary>
/// Puerto (patrón hexagonal, mismo espíritu que <see cref="IFileStoragePort"/>) para el backup OBLIGATORIO
/// que se genera justo antes de un borrado masivo de datos ("Empezar de cero"). La implementación real hace
/// dos cosas: un <c>pg_dump -Fc</c> completo de Postgres a un archivo en disco (validado con
/// <c>pg_restore --list</c>, no solo tamaño &gt; 0), y COPIA (nunca mueve) todos los objetos del bucket de
/// MinIO a un prefijo de backup, verificando cada copia con <c>StatObject</c>. Separado en un puerto para
/// poder testear <c>SystemDataWipeService</c> sin depender de un Postgres/MinIO reales corriendo (se inyecta
/// un fake en los tests unitarios; el puerto real se prueba por construcción en integración/producción).
/// </summary>
public interface IWipeBackupPort
{
    /// <summary>
    /// Genera el backup completo (Postgres + COPIA verificada de MinIO — los originales de MinIO NO se tocan
    /// acá). Si CUALQUIERA de los dos pasos falla, el resultado viene con
    /// <see cref="WipeBackupResult.Success"/>=false y el caller aborta el borrado sin tocar datos.
    /// </summary>
    /// <param name="backupFileName">Nombre del archivo del dump de Postgres (sin path), ej. "wipe-20260727-153000.dump".</param>
    /// <param name="minioPrefix">Prefijo destino dentro del bucket de MinIO, ej. "wipe-backup-20260727-153000/".</param>
    Task<WipeBackupResult> CreateBackupAsync(string backupFileName, string minioPrefix, CancellationToken ct);

    /// <summary>
    /// Fix bloqueante #1 (revisión 2026-07-27): borra los objetos ORIGINALES de MinIO que
    /// <see cref="WipeBackupResult.CopiedObjectKeys"/> confirma que ya tienen una copia verificada en el
    /// prefijo de backup. Se llama SOLO DESPUÉS de que la transacción de borrado hizo commit exitosamente —
    /// nunca antes. Es <b>best-effort</b>: si un objeto no se puede borrar, la implementación loguea y sigue
    /// (nunca tira) — el wipe YA fue exitoso en ese punto (Postgres + backup de MinIO existen), así que un
    /// fallo acá es basura inofensiva (un objeto que debería haberse borrado pero cuya copia de backup
    /// también existe), NUNCA una pérdida de dato.
    /// </summary>
    Task RemoveOriginalObjectsAsync(WipeBackupResult backupResult, CancellationToken ct);
}
