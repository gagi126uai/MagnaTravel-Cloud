using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Obra "Restaurar desde la app" (2026-07-27, Parte B firmada): implementación de <see cref="ISystemDataRestoreService"/>.
/// Ver el diseño completo (por qué el modo <c>real</c> no es un restore total) en la interfaz.
/// </summary>
public class SystemDataRestoreService : ISystemDataRestoreService
{
    /// <summary>Frase distinta a la del wipe ("BORRAR TODO") a propósito: son dos operaciones distintas, no queremos que confundan los candados.</summary>
    private const string RequiredPhrase = "RESTAURAR TODO";

    /// <summary>
    /// Decisión firmada del dueño (2026-07-27, revisión de seguridad, punto 9): aplicando la regla YA FIRMADA
    /// "en PROD se factura solo en homologación" — restaurar un backup viejo de <c>AfipSettings</c> JAMÁS
    /// puede dejar el sistema habilitado para facturar en modo PRODUCTIVO sin que nadie se dé cuenta (el
    /// backup podría traer un certificado y token productivos vigentes). Por eso, cada vez que se restaura la
    /// tabla <c>AfipSettings</c> en la base viva (modo <c>real</c>), se fuerza <c>IsProduction = false</c>
    /// EN LA MISMA operación — el caso de uso real ("recuperé el certificado que borré de más") sigue
    /// funcionando sin riesgo fiscal; si el dueño de verdad necesita productivo, lo prende a mano después.
    /// </summary>
    private const string AfipHomologacionMessage =
        "La conexión con AFIP se restauró en modo homologación; si necesitás productivo, activalo a mano.";

    /// <summary>
    /// Trampa de framework (comentario didáctico, mismo motivo que <c>SystemDataWipeService.AuditJsonOptions</c>):
    /// el encoder default de <c>JsonSerializer</c> escapa letras acentuadas ("conexión" queda "conexión") -
    /// pensado para HTML, no para texto legible en una columna de auditoría. <c>UnicodeRanges.All</c> deja el
    /// español con tildes tal cual se escribió.
    /// </summary>
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    /// <summary>
    /// Motivo persistido en <c>IMaintenanceModeService</c> (y expuesto por <c>GET /api/system/status</c>)
    /// mientras dura una restauración TOTAL.
    /// </summary>
    private const string TotalRestoreMaintenanceReason = "Restauración total del sistema en curso.";

    /// <summary>Hallazgo B4 de seguridad (2026-07-28): "dos restauraciones a la vez se pisan".</summary>
    private const string ConcurrentRestoreMessage = "Ya hay una restauración en curso.";

    /// <summary>Hallazgo B6 de seguridad (2026-07-28, F-16): motivo obligatorio del modo total.</summary>
    private const int MinMotivoLength = 10;

    /// <summary>
    /// Candado fiscal (hallazgo B2 de seguridad, 2026-07-28): MISMA regla firmada que protege "Empezar de
    /// cero" (ver <see cref="FiscalLockEvaluator"/>), aplicada también a la restauración TOTAL — antes de esta
    /// obra, un restore total podía reemplazar la base entera, comprobantes fiscales productivos incluidos,
    /// sin ningún aviso. El verbo cambia ("restaurar" en vez de "borrar"); la consulta es la misma.
    /// </summary>
    private const string FiscalLockRestoreMessage =
        "Hay comprobantes emitidos en modo productivo: no se puede restaurar. Los comprobantes fiscales reales deben conservarse.";

    private const string AfipProductionModeRestoreMessage =
        "AFIP está en modo productivo: pasá a homologación antes de restaurar. Los comprobantes reales no se tocan.";

    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDatabaseRestorePort _restorePort;
    private readonly IWipeBackupPort _backupPort;
    private readonly IMaintenanceModeService _maintenanceMode;
    private readonly IAuditService _auditService;
    private readonly ILogger<SystemDataRestoreService> _logger;

    public SystemDataRestoreService(
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        IDatabaseRestorePort restorePort,
        IWipeBackupPort backupPort,
        IMaintenanceModeService maintenanceMode,
        IAuditService auditService,
        ILogger<SystemDataRestoreService> logger)
    {
        _context = context;
        _userManager = userManager;
        _restorePort = restorePort;
        _backupPort = backupPort;
        _maintenanceMode = maintenanceMode;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<SystemDataBackupsResponse> ListBackupsAsync(CancellationToken ct)
    {
        var backups = await _restorePort.ListBackupsAsync(ct);
        return new SystemDataBackupsResponse
        {
            Backups = backups
                .Select(b => new BackupFileSummaryDto
                {
                    Archivo = b.FileName,
                    FechaUtc = b.LastWriteTimeUtc,
                    TamanioBytes = b.SizeBytes,
                })
                .ToList(),
        };
    }

    public async Task<SystemDataRestoreVerifyResponse> VerifyBackupAsync(string requesterUserId, string fileName, CancellationToken ct)
    {
        // Hallazgo menor de seguridad: un intento de verificación con un archivo invalido tambien queda
        // auditado (mismo criterio que ExecuteRestoreAsync) — no es destructivo, pero es informacion de
        // seguridad relevante (¿alguien esta probando nombres de archivo al azar?).
        try
        {
            return await VerifyBackupCoreAsync(fileName, ct);
        }
        catch (SystemDataRestoreRefusedException ex)
        {
            await AuditRejectionAsync(ex.Message, requesterUserId, ct);
            throw;
        }
    }

    private async Task<SystemDataRestoreVerifyResponse> VerifyBackupCoreAsync(string fileName, CancellationToken ct)
    {
        if (!IsSafeFileName(fileName))
        {
            throw new SystemDataRestoreRefusedException("Nombre de archivo inválido.");
        }

        var result = await _restorePort.VerifyBackupAsync(fileName, ct);
        if (!result.Success)
        {
            _logger.LogWarning("Restaurar: verificacion de backup {Archivo} fallo. Motivo interno: {Motivo}", fileName, result.ErrorMessage);
        }

        return new SystemDataRestoreVerifyResponse
        {
            Valido = result.Success,
            // El motivo tecnico (stderr de pg_restore) NUNCA llega al usuario: si el archivo no es valido,
            // alcanza con decirle que no se pudo leer.
            Motivo = result.Success ? null : "No se pudo leer el archivo. Puede estar dañado o no ser un backup válido.",
            CantidadTablas = result.TableCount,
            TieneTablasClave = result.HasKeyTables,
        };
    }

    public async Task<SystemDataRestoreResponse> ExecuteRestoreAsync(
        string requesterUserId,
        string password,
        string phrase,
        string fileName,
        string modo,
        IReadOnlyList<string>? tablas,
        string? motivo,
        CancellationToken ct)
    {
        try
        {
            return await ExecuteRestoreCoreAsync(requesterUserId, password, phrase, fileName, modo, tablas, motivo, ct);
        }
        catch (SystemDataRestoreRefusedException ex)
        {
            await AuditRejectionAsync(ex.Message, requesterUserId, ct);
            throw;
        }
    }

    /// <summary>
    /// Hallazgo B6 de seguridad (2026-07-28, "auditoría frágil"): best-effort A PROPÓSITO. Un rechazo YA es un
    /// camino de excepción — si además el intento de auditarlo fallara sin este try/catch, el error real
    /// (frase incorrecta, candado fiscal, etc.) quedaría tapado por un 500 genérico. El intento de auditar
    /// SIEMPRE se loguea (haya salido bien o mal), nunca se pierde en silencio.
    /// </summary>
    private async Task AuditRejectionAsync(string motivo, string requesterUserId, CancellationToken ct)
    {
        try
        {
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.SystemDataRestoreRejected,
                entityName: AuditActions.SystemDataRestoreEntityName,
                entityId: DateTime.UtcNow.ToString("O"),
                details: motivo,
                userId: requesterUserId,
                userName: null,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Restaurar: no se pudo auditar un RECHAZO (motivo real del rechazo, en este log: {Motivo}). UserId={UserId}.",
                motivo, requesterUserId);
        }
    }

    private async Task<SystemDataRestoreResponse> ExecuteRestoreCoreAsync(
        string requesterUserId,
        string password,
        string phrase,
        string fileName,
        string modo,
        IReadOnlyList<string>? tablas,
        string? motivo,
        CancellationToken ct)
    {
        // 1) Frase EXACTA.
        if (!string.Equals(phrase, RequiredPhrase, StringComparison.Ordinal))
        {
            throw new SystemDataRestoreRefusedException("La frase no coincide. Escribí exactamente: RESTAURAR TODO.");
        }

        // 2) Contraseña del usuario que ejecuta.
        var requester = await _userManager.FindByIdAsync(requesterUserId);
        if (requester is null || !await _userManager.CheckPasswordAsync(requester, password))
        {
            throw new SystemDataRestoreRefusedException("La contraseña no es correcta.");
        }

        // 3) Archivo: nombre seguro (defensa en profundidad, el puerto tambien valida).
        if (!IsSafeFileName(fileName))
        {
            throw new SystemDataRestoreRefusedException("Nombre de archivo inválido.");
        }

        // 4) Modo valido.
        if (!RestoreModes.All.Contains(modo, StringComparer.Ordinal))
        {
            throw new SystemDataRestoreRefusedException("El modo de restauración tiene que ser 'prueba', 'real' o 'total'.");
        }

        // 5) Candado de concurrencia (hallazgo B4 de seguridad, "dos restauraciones a la vez se pisan"),
        // APLICA A LOS 3 MODOS: si ya hay una restauración TOTAL en curso (el único modo que activa el flag
        // de mantenimiento), ningún otro pedido de restauración —de NINGÚN modo— puede arrancar mientras tanto.
        // Este chequeo es la version BARATA (no atomica) - la version ATOMICA que de verdad resuelve la
        // carrera entre dos pedidos de modo total simultaneos vive en ExecuteTotalRestoreAsync (TryActivate).
        if (_maintenanceMode.IsActive)
        {
            throw new SystemDataRestoreRefusedException(ConcurrentRestoreMessage);
        }

        if (modo == RestoreModes.Prueba)
        {
            return await ExecuteShadowRestoreAsync(requester, fileName, ct);
        }

        if (modo == RestoreModes.Total)
        {
            return await ExecuteTotalRestoreAsync(requester, fileName, motivo, ct);
        }

        return await ExecuteLiveTableRestoreAsync(requester, fileName, tablas, ct);
    }

    private async Task<SystemDataRestoreResponse> ExecuteShadowRestoreAsync(
        ApplicationUser requester, string fileName, CancellationToken ct)
    {
        var shadowResult = await _restorePort.RestoreToShadowDatabaseAsync(fileName, ct);
        if (!shadowResult.Success)
        {
            _logger.LogError("Restaurar (modo prueba): fallo restaurando la base sombra. Motivo interno: {Error}", shadowResult.ErrorMessage);
            throw new SystemDataRestoreRefusedException(
                "No se pudo restaurar el backup a la base de prueba. Puede estar dañado o el equipo técnico necesita revisarlo.");
        }

        SystemDataWipeCounts? counts = null;
        string? advertencia = null;
        try
        {
            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(shadowResult.ShadowConnectionString);
            await using var shadowContext = new AppDbContext(optionsBuilder.Options);
            counts = await SystemDataWipeService.CountAllAsync(shadowContext, ct);
        }
        catch (Exception ex)
        {
            // Un backup de una version MUY anterior del sistema puede tener un esquema distinto al actual
            // (columnas/tablas que cambiaron desde entonces). No es un error del usuario: se informa como
            // advertencia, nunca como el detalle tecnico crudo de Postgres/EF.
            _logger.LogWarning(ex, "Restaurar (modo prueba): no se pudieron calcular todos los conteos de la base sombra (posible backup de una version anterior).");
            advertencia = "No se pudieron calcular todos los conteos: el backup podría ser de una versión anterior del sistema.";
        }
        finally
        {
            // Hallazgo menor de seguridad: la base sombra es una copia COMPLETA de produccion (con datos de
            // pasajeros/clientes reales) que solo hacia falta un instante para leer estos conteos. Se borra
            // SIEMPRE (haya salido bien o mal el conteo) para no dejar una copia de datos sensibles dando
            // vueltas para siempre. Best-effort: si esto falla, no debe tapar el resultado ya calculado.
            await _restorePort.DropShadowDatabaseAsync(ct);
        }

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.SystemDataRestored,
            entityName: AuditActions.SystemDataRestoreEntityName,
            entityId: DateTime.UtcNow.ToString("O"),
            details: JsonSerializer.Serialize(new { modo = RestoreModes.Prueba, archivo = fileName }, AuditJsonOptions),
            userId: requester.Id,
            userName: string.IsNullOrWhiteSpace(requester.FullName) ? requester.Email : requester.FullName,
            ct);

        return new SystemDataRestoreResponse
        {
            Modo = RestoreModes.Prueba,
            Conteos = counts,
            Advertencia = advertencia,
        };
    }

    private async Task<SystemDataRestoreResponse> ExecuteLiveTableRestoreAsync(
        ApplicationUser requester, string fileName, IReadOnlyList<string>? tablas, CancellationToken ct)
    {
        if (tablas is null || tablas.Count == 0)
        {
            throw new SystemDataRestoreRefusedException("Elegí al menos una tabla de configuración para restaurar.");
        }

        var tablasNormalizadas = new HashSet<string>(tablas, StringComparer.Ordinal);
        var fueraDeLista = tablasNormalizadas.Where(t => !WipeGroups.ConfiguracionTables.Contains(t, StringComparer.Ordinal)).ToList();
        if (fueraDeLista.Count > 0)
        {
            throw new SystemDataRestoreRefusedException(
                "Solo se pueden restaurar tablas de configuración sobre la base viva.");
        }

        // Hallazgo bloqueante de usabilidad (punto 11 de la revision): NO se rechaza todo el pedido si UNA
        // tabla ya tiene datos. El puerto restaura tabla-por-tabla y SALTEA (nunca sobrescribe) las que ya
        // tenian filas — asi el caso de uso real ("recupere de mas, devolveme mi configuracion") funciona
        // aunque, por ejemplo, ApprovalPolicies ya haya vuelto a sus defaults de fabrica por el reseed del wipe.
        var result = await _restorePort.RestoreTablesIntoLiveDatabaseAsync(fileName, tablasNormalizadas.ToList(), ct);

        // Candado fiscal de restauracion (punto 9, decision firmada del dueño) — hallazgo BLOQUEANTE B-N1
        // (ronda de revision): esto tiene que correr ANTES de decidir si tirar por falla, no despues. El
        // puerto restaura TABLA POR TABLA y aborta en la primera que falla: si AfipSettings es la 2da de 5 y
        // la 4ta falla, "result.Success" da false pero "AfipSettings" YA quedo repuesta en result.RestoredTables
        // — sin este orden, el sistema podia quedar habilitado para facturar en modo PRODUCTIVO real (CAE)
        // con el candado fiscal del wipe bloqueado despues por su propia culpa, y nadie se enteraria porque el
        // codigo nunca llegaba a este UPDATE en el camino de error. El UPDATE es IDEMPOTENTE (une fila o
        // ninguna): correrlo siempre que "AfipSettings" este en RestoredTables es seguro, haya o no fallado
        // algo despues.
        var seRestauroAfip = result.RestoredTables.Contains("AfipSettings", StringComparer.Ordinal);
        if (seRestauroAfip)
        {
            await _context.Database.ExecuteSqlRawAsync(
                """UPDATE "AfipSettings" SET "IsProduction" = false;""", ct);
        }

        if (!result.Success)
        {
            _logger.LogError("Restaurar (modo real): fallo restaurando tablas de configuracion. Motivo interno: {Error}", result.ErrorMessage);
            throw new SystemDataRestoreRefusedException(
                BuildPartialFailureMessage(result.RestoredTables, seRestauroAfip));
        }

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.SystemDataRestored,
            entityName: AuditActions.SystemDataRestoreEntityName,
            entityId: DateTime.UtcNow.ToString("O"),
            // T-5: nada de nombres de tabla crudos, ni siquiera en el audit log — se usan las mismas etiquetas
            // de negocio que ve el usuario (WipeGroups.ConfiguracionTableLabels).
            details: JsonSerializer.Serialize(new
            {
                modo = RestoreModes.Real,
                archivo = fileName,
                repuesto = result.RestoredTables.Select(ToBusinessLabel).ToArray(),
                salteadoPorTenerDatos = result.SkippedNonEmptyTables.Select(ToBusinessLabel).ToArray(),
                afipForzadoAHomologacion = seRestauroAfip,
            }, AuditJsonOptions),
            userId: requester.Id,
            userName: string.IsNullOrWhiteSpace(requester.FullName) ? requester.Email : requester.FullName,
            ct);

        return new SystemDataRestoreResponse
        {
            Modo = RestoreModes.Real,
            TablasRestauradas = result.RestoredTables.Select(ToBusinessLabel).ToList(),
            TablasSalteadas = result.SkippedNonEmptyTables.Select(ToBusinessLabel).ToList(),
            Mensaje = BuildLiveRestoreMessage(result.RestoredTables, result.SkippedNonEmptyTables, seRestauroAfip),
        };
    }

    /// <summary>
    /// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño) + ronda de hardening de seguridad/funcional
    /// del mismo día: reemplaza TODA la base viva por la foto de <paramref name="fileName"/>. Pasos, EN ORDEN
    /// (cada uno con su propio log estructurado — hallazgo B6, "que el rastro quede aunque falle la
    /// auditoría en la base"):
    ///
    /// <list type="number">
    ///   <item><b>0) Motivo obligatorio</b> (hallazgo B6/F-16): mínimo 10 caracteres. La operación más
    ///   destructiva del sistema exige que quien la ejecuta escriba POR QUÉ.</item>
    ///   <item><b>0.5) Candado de concurrencia ATÓMICO</b> (hallazgo B4): <see cref="IMaintenanceModeService.TryActivate"/>
    ///   activa el mantenimiento y devuelve si ESTA llamada ganó la carrera — si perdió, se rechaza sin haber
    ///   tocado nada todavía (ni el guard de esquema, ni el candado fiscal, ni el backup).</item>
    ///   <item><b>1) Guard de compatibilidad de esquema</b> (hallazgo B7): fail-closed contra <c>__EFMigrationsHistory</c>.</item>
    ///   <item><b>2) Candado fiscal</b> (hallazgo B2): MISMA regla que "Empezar de cero" (<see cref="FiscalLockEvaluator"/>).</item>
    ///   <item><b>a) Backup previo OBLIGATORIO del estado ACTUAL</b> — el "deshacer el deshacer".</item>
    ///   <item><b>c+d) Cortar conexiones y <c>pg_restore</c></b> — delegado ENTERO al puerto
    ///   (<see cref="IDatabaseRestorePort.RestoreTotalAsync"/>), con timeout PROPIO (hallazgo B1).</item>
    ///   <item><b>b3) Forzar AFIP a homologación</b> (hallazgo B3): MISMO criterio que el modo <c>real</c> — un
    ///   resguardo viejo puede traer <c>AfipSettings.IsProduction=true</c> y dejar el sistema emitiendo CAE
    ///   real en silencio.</item>
    ///   <item><b>b5) Reponer archivos de MinIO</b> (hallazgo B5): si se puede determinar el resguardo de
    ///   archivos correspondiente a este backup.</item>
    ///   <item><b>e) Desactivar modo mantenimiento</b> — SIEMPRE, EXCEPTO si el desenlace del <c>pg_restore</c>
    ///   quedó incierto (hallazgo B1: <see cref="TotalRestoreOutcome.UnknownMayStillBeRunning"/>) — ahí el
    ///   sistema TIENE que seguir en mantenimiento.</item>
    ///   <item><b>f) Auditoría DESPUÉS del restore, best-effort</b> (hallazgo B6): si el <c>INSERT</c> del
    ///   <c>AuditLog</c> falla, se loguea pero NUNCA convierte un restore YA exitoso en un 500 al usuario.</item>
    /// </list>
    ///
    /// <para><b>Hallazgo BLOQUEANTE B-N1 (ronda de hardening, 2026-07-28)</b>: desde que <c>pg_restore</c>
    /// termina con éxito, TODO lo que sigue (forzar AFIP, reponer MinIO, auditar) usa
    /// <see cref="CancellationToken.None"/> — nunca el <c>ct</c> del pedido HTTP. Cancelar el PEDIDO (cerrar la
    /// pestaña, que el proxy corte la conexión) no puede cancelar la CONSECUENCIA de una restauración que ya
    /// reemplazó la base viva.</para>
    ///
    /// <para><b>Hallazgo B-N2 (ronda de hardening, 2026-07-28)</b>: <see cref="IMaintenanceModeService.Touch"/>
    /// renueva el reloj de auto-expiración justo antes del <c>pg_restore</c> (para que los pasos previos no
    /// consuman ese presupuesto); <see cref="IMaintenanceModeService.SuppressAutoExpiry"/> exime a una sesión
    /// de la auto-expiración cuando el desenlace queda incierto (timeout del <c>pg_restore</c>, o el UPDATE de
    /// AFIP no se pudo confirmar) — esos casos SOLO se resuelven con intervención manual.</para>
    /// </summary>
    private async Task<SystemDataRestoreResponse> ExecuteTotalRestoreAsync(
        ApplicationUser requester, string fileName, string? motivo, CancellationToken ct)
    {
        // 0) Motivo obligatorio (hallazgo B6/F-16), ANTES de tocar cualquier candado o recurso.
        var motivoNormalizado = motivo?.Trim();
        if (string.IsNullOrEmpty(motivoNormalizado) || motivoNormalizado.Length < MinMotivoLength)
        {
            throw new SystemDataRestoreRefusedException(
                $"Para restaurar TOTAL tenés que escribir un motivo de al menos {MinMotivoLength} caracteres.");
        }

        // 0.5) Candado de concurrencia ATOMICO (hallazgo B4): si perdimos la carrera contra otro pedido de
        // modo total que se activó justo entre el chequeo barato de ExecuteRestoreCoreAsync y este punto, NO
        // seguimos de largo.
        if (!_maintenanceMode.TryActivate(TotalRestoreMaintenanceReason))
        {
            throw new SystemDataRestoreRefusedException(ConcurrentRestoreMessage);
        }

        // Sentinela B1: se pone en true SOLO si el desenlace del pg_restore queda INCIERTO — en ese caso el
        // finally de abajo NO desactiva el mantenimiento (el sistema tiene que seguir "tapiado" hasta que el
        // equipo tecnico confirme el estado real de la base, o hasta la auto-expiracion de
        // FileMaintenanceModeService como ultimo recurso).
        var maintenanceOutcomeUnknown = false;
        string? backupPrevioFileName = null;

        _logger.LogWarning(
            "Restaurar TOTAL: INICIADO por {UserId} ({UserName}). Archivo={Archivo}. Motivo={Motivo}.",
            requester.Id, requester.UserName, fileName, motivoNormalizado);

        try
        {
            // 1) Guard de compatibilidad de esquema (hallazgo B7), fail-closed, ANTES de tocar la base.
            var schemaCheck = await _restorePort.CheckSchemaCompatibilityAsync(fileName, ct);
            if (!schemaCheck.Compatible)
            {
                _logger.LogError(
                    "Restaurar TOTAL: guard de compatibilidad de esquema RECHAZO el archivo. Motivo interno: {Error}",
                    schemaCheck.ErrorMessage);
                throw new SystemDataRestoreRefusedException(
                    "Ese resguardo es de una versión anterior del sistema; no se puede restaurar sobre la versión actual. Avisá al equipo técnico.");
            }

            // 2) Candado fiscal (hallazgo B2), MISMA regla que "Empezar de cero".
            var fiscalLockReason = await FiscalLockEvaluator.EvaluateAsync(_context, ct);
            switch (fiscalLockReason)
            {
                case FiscalLockEvaluator.Reason.LiveProductionInvoice:
                    throw new SystemDataRestoreRefusedException(FiscalLockRestoreMessage);
                case FiscalLockEvaluator.Reason.AfipInProductionMode:
                    throw new SystemDataRestoreRefusedException(AfipProductionModeRestoreMessage);
            }

            // a) Backup previo OBLIGATORIO del estado ACTUAL — el "deshacer el deshacer". Prefijo PROPIO
            // "pre-restore-" (hallazgo menor, revisión funcional: "era indistinguible de un Empezar de cero en
            // la lista") — PgDumpAndMinioWipeBackupPort igual lo reconoce como backup existente (no lo re-copia
            // en una operación futura, ver KnownBackupPrefixMarkers).
            var timestamp = DateTime.UtcNow;
            var backupFileName = BuildPreviousStateBackupFileName(timestamp);
            var minioBackupPrefix = BuildPreviousStateBackupMinioPrefix(timestamp);

            var backupResult = await _backupPort.CreateBackupAsync(backupFileName, minioBackupPrefix, ct);
            if (!backupResult.Success)
            {
                _logger.LogError(
                    "Restaurar TOTAL: el backup previo del estado actual fallo, se aborta sin tocar nada. Motivo interno: {Error}",
                    backupResult.ErrorMessage);
                throw new SystemDataRestoreRefusedException(
                    "No se pudo generar el resguardo de tu estado actual. No se restauró nada. Volvé a intentarlo o avisá al equipo técnico.");
            }

            backupPrevioFileName = backupResult.BackupFileName ?? backupFileName;
            _logger.LogWarning(
                "Restaurar TOTAL: backup previo generado ({BackupPrevio}). Cortando conexiones y arrancando pg_restore.",
                backupPrevioFileName);

            // B-N2(c): renovar el reloj de mantenimiento justo ANTES de arrancar el pg_restore real — desde
            // este punto, el presupuesto de auto-expiración (Maintenance:MaxDurationMinutes) mide el tiempo
            // DESDE ACÁ (acotado por el timeout propio del pg_restore), no desde el arranque de TODA la
            // operación (que ya consumió tiempo en el guard de esquema + candado fiscal + backup previo, cada
            // uno con su PROPIO timeout — ver el comentario de clase de FileMaintenanceModeService).
            _maintenanceMode.Touch();

            // c+d) Cortar conexiones + pg_restore --clean --if-exists --single-transaction, con timeout PROPIO
            // (hallazgo B1 — ver el comentario XML del puerto).
            var restoreResult = await _restorePort.RestoreTotalAsync(fileName, ct);

            if (restoreResult.Outcome == TotalRestoreOutcome.UnknownMayStillBeRunning)
            {
                maintenanceOutcomeUnknown = true;
                // Hallazgo B-N2(a): un desenlace incierto NUNCA puede auto-expirar solo — sería repetir
                // exactamente el error que el hallazgo B1 vino a corregir (reabrir el sistema sin certeza de
                // que sea seguro). La única salida es manual (ver docs/db-operations.md).
                _maintenanceMode.SuppressAutoExpiry(
                    "Restauración total: desenlace incierto tras el timeout de pg_restore. Requiere confirmación manual.");
                _logger.LogCritical(
                    "Restaurar TOTAL: DESENLACE INCIERTO tras timeout de pg_restore. UserId={UserId}, Archivo={Archivo}, " +
                    "BackupPrevio={BackupPrevio}. El sistema QUEDA en mantenimiento hasta confirmar el estado real de la base.",
                    requester.Id, fileName, backupPrevioFileName);
                throw new SystemDataRestoreRefusedException(
                    "No se pudo confirmar que la restauración terminó a tiempo. Por seguridad, el sistema sigue en mantenimiento. Avisá urgente al equipo técnico.");
            }

            if (!restoreResult.Success)
            {
                _logger.LogError(
                    "Restaurar TOTAL: fallo el pg_restore total. Motivo interno: {Error}", restoreResult.ErrorMessage);
                // Hallazgo de data-exposure (2026-07-28): NUNCA interpolar el nombre de archivo del resguardo
                // previo en un mensaje de error crudo — el nombre queda disponible para el usuario en el
                // flujo normal de "Volver atrás" (misma pantalla que lista los resguardos), no hace falta
                // repetirlo acá con jerga tecnica de nombre de archivo.
                throw new SystemDataRestoreRefusedException(
                    "No se pudo completar la restauración total. El sistema quedó exactamente como estaba antes de " +
                    "intentarlo (la operación se deshizo sola). Avisá al equipo técnico. Tu estado de antes de este " +
                    "intento igual quedó a salvo: vas a poder elegirlo desde \"Volver atrás\".");
            }

            // ============================================================================================
            // A PARTIR DE ACÁ: restoreResult.Success == true — la base viva YA se reemplazó exitosamente.
            //
            // Hallazgo BLOQUEANTE B-N1 de seguridad (2026-07-28, "gravísimo"): TODO lo que sigue usa
            // CancellationToken.None, NUNCA el "ct" del pedido HTTP. Si el pedido se cancela DESPUÉS de que
            // el pg_restore terminó bien (el admin cierra la pestaña, el proxy corta la conexión — pasa
            // AdminDangerController.Restore le pasa RequestAborted como "ct"), estos pasos NO pueden abortarse
            // a mitad de camino: dejarían el AFIP forzado a medias, los archivos de MinIO sin reponer, sin
            // auditoría, y —lo peor— el "finally" de abajo desactivaría el mantenimiento IGUAL (porque
            // "maintenanceOutcomeUnknown" seguiría en false), reabriendo el sistema con AFIP potencialmente en
            // modo PRODUCTIVO. Una vez que la base YA se reemplazó, cancelar el PEDIDO no puede cancelar la
            // CONSECUENCIA de esa restauración.
            // ============================================================================================

            // b3) Forzar AFIP a homologacion (hallazgo B3) — MISMO criterio que el modo real: un resguardo
            // viejo puede traer un AfipSettings productivo y dejar el sistema facturando CAE real en silencio.
            // Si esto no se puede CONFIRMAR, no hay forma segura de saber si el sistema quedaria facturando en
            // modo productivo real - se trata como desenlace incierto (igual que B1), nunca se sale de
            // mantenimiento sin esa confirmacion.
            try
            {
                await _context.Database.ExecuteSqlRawAsync(
                    """UPDATE "AfipSettings" SET "IsProduction" = false;""", CancellationToken.None);
            }
            catch (Exception ex)
            {
                maintenanceOutcomeUnknown = true;
                _maintenanceMode.SuppressAutoExpiry(
                    "Restauración total: la base ya se reemplazó pero no se pudo confirmar que AFIP quedó en homologación. Requiere confirmación manual.");
                _logger.LogCritical(ex,
                    "Restaurar TOTAL: la base YA se reemplazo pero el UPDATE que fuerza AFIP a homologacion FALLO. " +
                    "UserId={UserId}, Archivo={Archivo}, BackupPrevio={BackupPrevio}. El sistema QUEDA en mantenimiento " +
                    "hasta confirmar a mano el estado real de AfipSettings.",
                    requester.Id, fileName, backupPrevioFileName);
                throw new SystemDataRestoreRefusedException(
                    "La base ya se restauró, pero no se pudo confirmar que AFIP quedó en modo homologación. Por seguridad, el sistema sigue en mantenimiento. Avisá urgente al equipo técnico.");
            }

            // b5) Reponer archivos de MinIO (hallazgo B5) si se puede determinar el resguardo correspondiente.
            // CancellationToken.None (B-N1): best-effort, pero tiene que intentarse completo aunque el pedido
            // original ya se haya cancelado.
            var archivosMensaje = await TryRestoreMinioObjectsAsync(fileName, CancellationToken.None);

            _logger.LogWarning(
                "Restaurar TOTAL: EXITOSO. UserId={UserId}, Archivo={Archivo}, BackupPrevio={BackupPrevio}.",
                requester.Id, fileName, backupPrevioFileName);

            // f) Auditoria DESPUES del restore, BEST-EFFORT (hallazgo B6) — ver el comentario completo de
            // AuditActions.SystemDataTotallyRestored sobre por que no puede escribirse antes ni durante, y por
            // que un fallo ACA no puede convertir un restore YA exitoso en un 500 para el usuario.
            // CancellationToken.None (B-N1): mismo criterio que arriba.
            try
            {
                await _auditService.LogBusinessEventAsync(
                    action: AuditActions.SystemDataTotallyRestored,
                    entityName: AuditActions.SystemDataRestoreEntityName,
                    entityId: DateTime.UtcNow.ToString("O"),
                    details: JsonSerializer.Serialize(new
                    {
                        modo = RestoreModes.Total,
                        archivo = fileName,
                        backupPrevio = backupPrevioFileName,
                        motivo = motivoNormalizado,
                        afipForzadoAHomologacion = true,
                    }, AuditJsonOptions),
                    userId: requester.Id,
                    userName: string.IsNullOrWhiteSpace(requester.FullName) ? requester.Email : requester.FullName,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Restaurar TOTAL: la restauracion fue EXITOSA pero NO se pudo escribir el AuditLog (queda constancia SOLO en este log). " +
                    "UserId={UserId}, Archivo={Archivo}, BackupPrevio={BackupPrevio}.",
                    requester.Id, fileName, backupPrevioFileName);
            }

            return new SystemDataRestoreResponse
            {
                Modo = RestoreModes.Total,
                RestauradoDe = fileName,
                BackupPrevio = backupPrevioFileName,
                Mensaje =
                    "Se restauró todo el sistema a la foto del resguardo elegido. Lo que se cargó después se perdió. " +
                    "Las sesiones y las contraseñas también volvieron a como estaban en ese momento. " +
                    archivosMensaje,
            };
        }
        finally
        {
            // e) Desactivar modo mantenimiento SIEMPRE, EXCEPTO si el desenlace quedo incierto (hallazgo B1) —
            // ahi el sistema tiene que seguir en mantenimiento hasta que se confirme el estado real de la base.
            if (!maintenanceOutcomeUnknown)
            {
                _maintenanceMode.Deactivate();
                _logger.LogWarning("Restaurar TOTAL: modo mantenimiento DESACTIVADO.");
            }
        }
    }

    /// <summary>
    /// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B5 de seguridad, "los archivos no vuelven"): si
    /// <paramref name="fileName"/> sigue el esquema de nombre propio de esta obra (<c>pre-restore-&lt;ts&gt;.dump</c>),
    /// intenta reponer los archivos de MinIO que se respaldaron junto con esa foto de la base. Si no se puede
    /// determinar el resguardo (backup de otro origen, ej. el sidecar diario de <c>docker-compose.yml</c>, o
    /// un backup viejo de "Empezar de cero"), o si la reposición falla, el mensaje AVISA CLARO — nunca se
    /// sugiere falsamente que los archivos volvieron.
    /// </summary>
    private async Task<string> TryRestoreMinioObjectsAsync(string fileName, CancellationToken ct)
    {
        var minioPrefix = TryDeriveMinioBackupPrefix(fileName);
        if (minioPrefix is null)
        {
            return "Los archivos subidos (vouchers, adjuntos) no se recuperan con esta restauración: no se pudo determinar su resguardo.";
        }

        int restoredCount;
        try
        {
            restoredCount = await _backupPort.RestoreObjectsFromBackupPrefixAsync(minioPrefix, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar TOTAL: fallo reponiendo los archivos de MinIO desde {Prefix}.", minioPrefix);
            return "Los archivos subidos (vouchers, adjuntos) no se pudieron recuperar con esta restauración.";
        }

        return restoredCount > 0
            ? "Los archivos subidos (vouchers, adjuntos) se repusieron desde el resguardo de ese momento."
            : "Los archivos subidos (vouchers, adjuntos) no se recuperaron con esta restauración: no se encontró un resguardo de archivos para ese momento.";
    }

    /// <summary>
    /// Deriva el prefijo de MinIO correspondiente a un nombre de archivo de backup, SOLO si sigue alguno de
    /// los DOS esquemas de nombre que esta obra reconoce (el propio de la restauración total y el heredado de
    /// "Empezar de cero" — un admin puede elegir restaurar TOTAL desde cualquiera de los dos). Cualquier otro
    /// nombre (backup manual, sidecar diario/semanal de <c>docker-compose.yml</c>) devuelve <c>null</c> — ESE
    /// backup nunca tuvo un resguardo de MinIO asociado, así que no hay nada que derivar.
    /// </summary>
    private static string? TryDeriveMinioBackupPrefix(string fileName)
    {
        const string dumpSuffix = ".dump";
        if (!fileName.EndsWith(dumpSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        (string FilePrefix, string BackupPrefix)[] knownSchemes =
        {
            ("pre-restore-", "pre-restore-backup-"),
            ("wipe-", "wipe-backup-"),
        };

        foreach (var (filePrefix, backupPrefix) in knownSchemes)
        {
            if (!fileName.StartsWith(filePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var timestamp = fileName[filePrefix.Length..^dumpSuffix.Length];
            // Formato esperado exacto: yyyyMMdd-HHmmss (15 caracteres, guion en la posicion 8).
            if (timestamp.Length == 15 && timestamp[8] == '-')
            {
                return $"{backupPrefix}{timestamp}/";
            }
        }

        return null;
    }

    /// <summary>
    /// Prefijo PROPIO de esta obra (hallazgo menor, revisión funcional: "el resguardo previo de un restore
    /// total era indistinguible de un Empezar de cero en la lista de backups") — antes usaba el mismo esquema
    /// "wipe-" que <c>SystemDataWipeService.BuildBackupFileName</c>.
    /// </summary>
    private static string BuildPreviousStateBackupFileName(DateTime utcNow) => $"pre-restore-{utcNow:yyyyMMdd-HHmmss}.dump";

    /// <summary>Ver <see cref="BuildPreviousStateBackupFileName"/>.</summary>
    private static string BuildPreviousStateBackupMinioPrefix(DateTime utcNow) => $"pre-restore-backup-{utcNow:yyyyMMdd-HHmmss}/";

    /// <summary>
    /// Arma el resumen en criollo de qué se repuso y qué se salteó (punto 11), siempre con nombres de NEGOCIO
    /// (T-5) — nunca "AgencySettings"/"AfipSettings" crudos.
    /// </summary>
    private static string BuildLiveRestoreMessage(
        IReadOnlyList<string> restauradas, IReadOnlyList<string> salteadas, bool afipForzadoAHomologacion)
    {
        var partes = new List<string>();

        if (restauradas.Count > 0)
        {
            var etiquetas = restauradas.Select(ToBusinessLabel);
            partes.Add($"Se repuso: {CapitalizeFirst(string.Join(", ", etiquetas))}.");
        }

        if (salteadas.Count > 0)
        {
            var etiquetas = salteadas.Select(ToBusinessLabel);
            var verbo = salteadas.Count == 1 ? "ya tenía datos" : "ya tenían datos";
            partes.Add($"{CapitalizeFirst(string.Join(", ", etiquetas))} {verbo}, así que no se {(salteadas.Count == 1 ? "tocó" : "tocaron")}.");
        }

        if (restauradas.Count == 0 && salteadas.Count == 0)
        {
            partes.Add("No había nada para restaurar.");
        }

        if (afipForzadoAHomologacion)
        {
            partes.Add(AfipHomologacionMessage);
        }

        return string.Join(" ", partes);
    }

    /// <summary>
    /// Arma el mensaje de rechazo cuando el puerto restauró ALGUNAS tablas antes de fallar en otra (punto 1
    /// de la ronda de revisión: "sumá al audit del rechazo qué alcanzó a reponerse"). Este mensaje es tanto lo
    /// que ve el usuario como lo que queda en el audit log de rechazo (mismo <c>ex.Message</c>, ver
    /// <see cref="AuditRejectionAsync"/>) — siempre en nombres de NEGOCIO (T-5).
    /// </summary>
    private static string BuildPartialFailureMessage(IReadOnlyList<string> restauradasAntesDeFallar, bool afipForzadoAHomologacion)
    {
        var mensaje = "No se pudo completar la restauración.";

        if (restauradasAntesDeFallar.Count > 0)
        {
            var etiquetas = restauradasAntesDeFallar.Select(ToBusinessLabel);
            mensaje += $" Ya alcanzó a reponerse: {CapitalizeFirst(string.Join(", ", etiquetas))}.";
        }

        if (afipForzadoAHomologacion)
        {
            mensaje += " " + AfipHomologacionMessage;
        }

        mensaje += " Avisá al equipo técnico.";
        return mensaje;
    }

    private static string ToBusinessLabel(string tableName) =>
        WipeGroups.ConfiguracionTableLabels.TryGetValue(tableName, out var label) ? label : "datos de configuración";

    private static string CapitalizeFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// Path traversal + consistencia de extensión: mismo criterio que el puerto (defensa en profundidad en el
    /// borde del servicio) — el nombre tiene que terminar en ".dump", igual que <c>ListBackupsAsync</c> filtra.
    /// </summary>
    private static bool IsSafeFileName(string fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName)
            && Path.GetFileName(fileName) == fileName
            && fileName.EndsWith(".dump", StringComparison.Ordinal);
    }
}
