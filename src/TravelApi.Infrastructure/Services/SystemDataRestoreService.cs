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

    /// <summary>
    /// ADR-052 (D2/D6): los DOS rechazos NUEVOS del gate de versión, en criollo y SEPARADOS a propósito (menor M1
    /// de la re-review). Antes había un solo texto que, para el caso "le falta una migración del medio", mentía
    /// diciendo "es de una versión anterior". T-5: ni un id de migración, ni "esquema", ni nombres de base.
    /// </summary>
    private const string NewerBackupRejectionMessage =
        "Ese resguardo es de una versión MÁS NUEVA del sistema que la que está instalada: no se puede usar acá. " +
        "Avisá al equipo técnico.";

    /// <summary>Ver <see cref="NewerBackupRejectionMessage"/>.</summary>
    private const string HistoryGapRejectionMessage =
        "Ese resguardo tiene un salto en su historial: le falta una parte del medio, así que el sistema no puede " +
        "completarlo solo. No se tocó nada. Avisá al equipo técnico.";

    /// <summary>ADR-052 (D2.1): la base viva quedó a mitad de una actualización, así que no hay referencia confiable para comparar.</summary>
    private const string LivePendingMigrationsRejectionMessage =
        "El sistema quedó a mitad de una actualización, así que no se puede restaurar desde acá. No se tocó nada. " +
        "Avisá al equipo técnico.";

    /// <summary>ADR-052 (D1.5): el servidor no permite crear/renombrar bases con el usuario actual. Fail-closed, sin jerga.</summary>
    private const string InsufficientPrivilegesRejectionMessage =
        "Este servidor no permite hacer una restauración total desde la aplicación. No se tocó nada. Avisá al equipo técnico.";

    /// <summary>ADR-052 (D4.4): el intento falló DESPUÉS de tocar la base y el sistema volvió solo a como estaba.</summary>
    private const string RolledBackRejectionMessage =
        "No se pudo actualizar el sistema con ese resguardo. Quedó todo como estaba antes de intentarlo.";

    /// <summary>ADR-052 (D4.5): DOBLE FALLO — el único caso que deja el sistema frenado a propósito.</summary>
    private const string DoubleFailureRejectionMessage =
        "No se pudo actualizar el sistema con ese resguardo y tampoco se pudo volver atrás sola. Por seguridad, el " +
        "sistema queda en mantenimiento. Avisá URGENTE al equipo técnico.";

    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDatabaseRestorePort _restorePort;
    private readonly IWipeBackupPort _backupPort;
    private readonly ISchemaUpdatePort _schemaUpdatePort;
    private readonly IMaintenanceModeService _maintenanceMode;
    private readonly IAuditService _auditService;
    private readonly ILogger<SystemDataRestoreService> _logger;

    public SystemDataRestoreService(
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        IDatabaseRestorePort restorePort,
        IWipeBackupPort backupPort,
        ISchemaUpdatePort schemaUpdatePort,
        IMaintenanceModeService maintenanceMode,
        IAuditService auditService,
        ILogger<SystemDataRestoreService> logger)
    {
        _context = context;
        _userManager = userManager;
        _restorePort = restorePort;
        _backupPort = backupPort;
        _schemaUpdatePort = schemaUpdatePort;
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
                    // ADR-052 (D5): marca INFORMATIVA. La pantalla avisa con ella; nunca decide con ella.
                    VersionResguardo = b.VersionState,
                    // Rediseño 2026-07-30 (§7 punto 1): frase en criollo ya armada por el motor.
                    PorQueSeGuardo = b.OriginLabel,
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
            await AuditRejectionAsync(ex, requesterUserId, ct);
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
            await AuditRejectionAsync(ex, requesterUserId, ct);
            throw;
        }
    }

    /// <summary>
    /// Hallazgo B6 de seguridad (2026-07-28, "auditoría frágil"): best-effort A PROPÓSITO. Un rechazo YA es un
    /// camino de excepción — si además el intento de auditarlo fallara sin este try/catch, el error real
    /// (frase incorrecta, candado fiscal, etc.) quedaría tapado por un 500 genérico. El intento de auditar
    /// SIEMPRE se loguea (haya salido bien o mal), nunca se pierde en silencio.
    ///
    /// <para><b>ADR-052 (D7)</b>: cuando el rechazo llega después de una VUELTA ATRÁS exitosa, el detalle pasa a
    /// ser un JSON con <c>volvioAtras: true</c> además del texto. En los demás rechazos se sigue guardando el
    /// texto pelado (mismo formato que antes de esta obra, para no romper la lectura del historial existente).</para>
    /// </summary>
    private async Task AuditRejectionAsync(SystemDataRestoreRefusedException refusal, string requesterUserId, CancellationToken ct)
    {
        // ADR-052 (D7): el DOBLE FALLO y la vuelta atrás tienen cada uno su propio dato buscable, además del
        // texto. El resto de los rechazos sigue guardando el texto pelado (mismo formato que antes de esta obra).
        string details;
        if (refusal.DoubleFailure)
        {
            details = JsonSerializer.Serialize(new { motivo = refusal.Message, dobleFallo = true }, AuditJsonOptions);
        }
        else if (refusal.RolledBack)
        {
            details = JsonSerializer.Serialize(new { motivo = refusal.Message, volvioAtras = true }, AuditJsonOptions);
        }
        else
        {
            details = refusal.Message;
        }

        // Si el rechazo llegó DESPUÉS de tocar la base, dejar constancia no puede depender de que el pedido HTTP
        // siga vivo — a esa altura casi siempre está cortado (el proxy corta a los 60s, o el admin cerró la
        // pestaña). Sin esto, justo el PEOR desenlace (doble fallo) era el que se quedaba sin registro.
        var auditCt = refusal.HappenedAfterTouchingTheDatabase ? CancellationToken.None : ct;

        try
        {
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.SystemDataRestoreRejected,
                entityName: AuditActions.SystemDataRestoreEntityName,
                entityId: DateTime.UtcNow.ToString("O"),
                details: details,
                userId: requesterUserId,
                userName: null,
                auditCt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Restaurar: no se pudo auditar un RECHAZO (motivo real del rechazo, en este log: {Motivo}). UserId={UserId}.",
                refusal.Message, requesterUserId);
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
    /// (Documentación histórica de la obra anterior, conservada porque explica POR QUÉ existe cada candado; la
    /// secuencia vigente es la de ADR-052, ver el comentario del método.)
    ///
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
    ///   (con ADR-052, <c>RestoreIntoNewDatabaseAsync</c>), con timeout PROPIO (hallazgo B1).</item>
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
    ///
    /// <para><b>ADR-052 (2026-07-29, rev. 2, firmada) — SECUENCIA VIGENTE</b>, con el número de paso del ADR:
    /// <list type="number">
    ///   <item><b>0</b> Motivo ≥10 (F-16) → candado de concurrencia atómico.</item>
    ///   <item><b>1</b> Gate de versión (5 veredictos): igual / anterior / más nueva / con agujero / base viva a
    ///   medio actualizar. Los dos rechazos nuevos tienen textos DISTINTOS y en criollo (T-5).</item>
    ///   <item><b>2</b> Candado fiscal (misma regla que "Empezar de cero").</item>
    ///   <item><b>3</b> Assert de privilegios (incluye PROPIEDAD de la base) + limpieza de sobras. Va ANTES de
    ///   pagar el <c>pg_restore</c> y el resguardo previo (condición C1 de la re-review).</item>
    ///   <item><b>4</b> Restaurar el resguardo en una base NUEVA al costado. Si falla: NADA se tocó.</item>
    ///   <item><b>5</b> Resguardo previo obligatorio del estado actual (se toma DESPUÉS del paso 4: si el
    ///   resguardo elegido está corrupto, no se pagan minutos de mantenimiento para nada).</item>
    ///   <item><b>6</b> INTERCAMBIO DE NOMBRES. Desde acá, cualquier fallo vuelve atrás.</item>
    ///   <item><b>7</b> Actualizar el esquema si el resguardo era de una versión anterior (bootstrappers →
    ///   migraciones → backfills, sin tragarse fallos).</item>
    ///   <item><b>8</b> AFIP a homologación (ahora DESPUÉS de migrar y DENTRO del sobre de vuelta atrás).</item>
    ///   <item><b>9</b> Reponer archivos de MinIO — best-effort, FUERA del sobre de vuelta atrás (desvío
    ///   declarado y aceptado: la reposición es aditiva y no tiene rollback propio; tirar abajo una base entera
    ///   porque no volvieron unos vouchers sería peor que el aviso honesto). La auditoría lleva
    ///   <c>archivosRepuestos</c> como DATO (condición C2), no solo dentro del mensaje.</item>
    ///   <item><b>10</b> Auditoría → dropear la copia vieja → mantenimiento OFF.</item>
    /// </list></para>
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

        // Sentinela B1: se pone en true SOLO si el sistema NO puede reabrirse solo (doble fallo de la vuelta
        // atrás, o AFIP sin confirmar) — en ese caso el finally de abajo NO desactiva el mantenimiento.
        var maintenanceOutcomeUnknown = false;
        string? backupPrevioFileName = null;

        // Se llena SOLO cuando el intercambio de nombres ya se hizo: es el "estamos del otro lado del puente".
        // Cualquier excepción INESPERADA a partir de ese punto tiene que volver atrás igual (ver el catch de
        // abajo) — el ADR pide que TODO lo que falle entre el intercambio y el final vuelva atrás, no solo los
        // fallos que el código enumera paso por paso.
        string? swappedPreviousDatabaseName = null;

        _logger.LogWarning(
            "Restaurar TOTAL: INICIADO por {UserId} ({UserName}). Archivo={Archivo}. Motivo={Motivo}.",
            requester.Id, requester.UserName, fileName, motivoNormalizado);

        // ADR-052 (D4): vuelta atrás. Es una función LOCAL a propósito: necesita poder marcar
        // "maintenanceOutcomeUnknown" (el sentinela que decide si el finally reabre el sistema o lo deja
        // frenado), y esa decisión no puede vivir en otro lado. SIEMPRE termina lanzando.
        async Task RollbackAndThrowAsync(string pasoQueFallo, string? internalError, string previousDatabaseName)
        {
            _logger.LogCritical(
                "Restaurar TOTAL: falló {Paso} DESPUÉS del intercambio de nombres. Se vuelve atrás. UserId={UserId}, " +
                "Archivo={Archivo}, BackupPrevio={BackupPrevio}, BaseAnterior={BaseAnterior}. Motivo interno: {Error}",
                pasoQueFallo, requester.Id, fileName, backupPrevioFileName, previousDatabaseName, internalError);

            // BLOQUEANTE de seguridad (re-review): la vuelta atrás puede TIRAR (no solo devolver "no pude"): se
            // conecta a Postgres, y una conexión que no abre es una excepción, no un false. Sin este try/catch, esa
            // excepción salía por arriba, el "finally" apagaba el mantenimiento y el sistema se reabría con la base
            // a medio actualizar y SIN el forzado de AFIP a homologación. Una excepción se trata IDÉNTICO a
            // "no pude volver atrás": doble fallo, mantenimiento sostenido.
            DatabaseSwapRollbackResult rollback;
            try
            {
                _maintenanceMode.Touch();
                rollback = await _restorePort.RollbackSwapAsync(previousDatabaseName, CancellationToken.None);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogCritical(rollbackEx,
                    "Restaurar TOTAL: la vuelta atrás TIRÓ una excepción. Se trata como doble fallo (el sistema queda en mantenimiento).");
                rollback = new DatabaseSwapRollbackResult(false, rollbackEx.Message);
            }

            if (rollback.Success)
            {
                // El sistema quedó EXACTAMENTE como antes del intento (mismos datos, mismo esquema): es seguro
                // reabrirlo, así que el finally desactiva el mantenimiento y el rechazo se audita con
                // volvioAtras=true.
                throw new SystemDataRestoreRefusedException(RolledBackRejectionMessage, rolledBack: true);
            }

            // DOBLE FALLO (D4.5): el ÚNICO caso que deja el sistema frenado a propósito. Ni la auto-expiración lo
            // reabre: sale solo a mano por el runbook (docs/db-operations.md).
            maintenanceOutcomeUnknown = true;
            _maintenanceMode.SuppressAutoExpiry(
                "Restauración total: la actualización falló y la vuelta atrás no se pudo completar. Requiere intervención manual.");
            _logger.LogCritical(
                "Restaurar TOTAL: DOBLE FALLO. UserId={UserId}, Archivo={Archivo}, BackupPrevio={BackupPrevio}, " +
                "BaseAnterior={BaseAnterior}. El sistema QUEDA en mantenimiento. Motivo interno de la vuelta atrás: {Error}",
                requester.Id, fileName, backupPrevioFileName, previousDatabaseName, rollback.ErrorMessage);
            throw new SystemDataRestoreRefusedException(DoubleFailureRejectionMessage, doubleFailure: true);
        }

        try
        {
            // 1) Gate de versión (ADR-052 D2), fail-closed, ANTES de tocar la base.
            var schemaCheck = await _restorePort.CheckSchemaCompatibilityAsync(fileName, ct);
            var needsSchemaUpdate = EnsureBackupVersionIsUsable(schemaCheck, fileName);

            // 2) Candado fiscal (hallazgo B2), MISMA regla que "Empezar de cero".
            var fiscalLockReason = await FiscalLockEvaluator.EvaluateAsync(_context, ct);
            switch (fiscalLockReason)
            {
                case FiscalLockEvaluator.Reason.LiveProductionInvoice:
                    throw new SystemDataRestoreRefusedException(FiscalLockRestoreMessage);
                case FiscalLockEvaluator.Reason.AfipInProductionMode:
                    throw new SystemDataRestoreRefusedException(AfipProductionModeRestoreMessage);
            }

            // 3) Assert de privilegios (ADR-052 D1.5 + C1): va ANTES de pagar el pg_restore y el resguardo
            // previo, porque de nada sirve descubrir 15 minutos después que el usuario no puede renombrar bases.
            var privileges = await _restorePort.CheckDatabaseManagementPrivilegesAsync(ct);
            if (!privileges.CanManage)
            {
                _logger.LogError(
                    "Restaurar TOTAL: el usuario de Postgres no puede administrar bases (crear/renombrar). Motivo interno: {Error}",
                    privileges.ErrorMessage);
                throw new SystemDataRestoreRefusedException(InsufficientPrivilegesRejectionMessage);
            }

            // 3b) Limpieza de sobras de intentos anteriores (D1.6). Best-effort dentro del puerto: no puede
            // frenar una restauración que recién arranca.
            await _restorePort.CleanupLeftoverRestoreDatabasesAsync(ct);

            // 4) Restaurar el resguardo en una base NUEVA al costado. Si esto falla, la base viva NUNCA se tocó.
            _maintenanceMode.Touch();
            _maintenanceMode.SetStep(RestoreProgressSteps.Datos);
            var newDatabaseRestore = await _restorePort.RestoreIntoNewDatabaseAsync(fileName, ct);
            if (!newDatabaseRestore.Success || string.IsNullOrWhiteSpace(newDatabaseRestore.NewDatabaseName))
            {
                _logger.LogError(
                    "Restaurar TOTAL: falló restaurar el resguardo en una base nueva (desenlace {Outcome}). La base viva NO se tocó. Motivo interno: {Error}",
                    newDatabaseRestore.Outcome, newDatabaseRestore.ErrorMessage);
                // Mejora real de ADR-052 sobre el diseño anterior: acá el sistema se puede reabrir tranquilo
                // (mantenimiento OFF por el finally) porque lo único afectado fue una base descartable. Antes,
                // el mismo fallo tocaba la base viva antes de saber si el resguardo servía.
                throw new SystemDataRestoreRefusedException(
                    "No se pudo leer ese resguardo completo, así que no se restauró nada: el sistema quedó intacto. " +
                    "Probá con otro resguardo o avisá al equipo técnico.");
            }

            var newDatabaseName = newDatabaseRestore.NewDatabaseName;

            // 5) Resguardo previo OBLIGATORIO del estado ACTUAL — el "deshacer el deshacer". Se toma DESPUÉS de
            // que el resguardo elegido demostró que se puede restaurar (cambio deliberado de ADR-052 D1.9).
            var timestamp = DateTime.UtcNow;
            var backupFileName = BuildPreviousStateBackupFileName(timestamp);
            var minioBackupPrefix = BuildPreviousStateBackupMinioPrefix(timestamp);

            _maintenanceMode.SetStep(RestoreProgressSteps.Resguardo);
            var backupResult = await _backupPort.CreateBackupAsync(backupFileName, minioBackupPrefix, ct);
            if (!backupResult.Success)
            {
                _logger.LogError(
                    "Restaurar TOTAL: el resguardo previo del estado actual falló, se aborta sin tocar nada. Motivo interno: {Error}",
                    backupResult.ErrorMessage);
                await _restorePort.DropDatabaseAsync(newDatabaseName, CancellationToken.None);
                throw new SystemDataRestoreRefusedException(
                    "No se pudo generar el resguardo de tu estado actual. No se restauró nada. Volvé a intentarlo o avisá al equipo técnico.");
            }

            backupPrevioFileName = backupResult.BackupFileName ?? backupFileName;
            _logger.LogWarning(
                "Restaurar TOTAL: resguardo previo generado ({BackupPrevio}). Arrancando el intercambio de nombres.",
                backupPrevioFileName);

            // 6) INTERCAMBIO DE NOMBRES (D1.4). Desde acá en adelante la base viva YA cambió: todo usa
            // CancellationToken.None (B-N1) y todo fallo pasa por la vuelta atrás.
            _maintenanceMode.Touch();

            // El puerto tiene su propia red (devuelve un resultado con el nombre de la base anterior incluso si algo
            // explota adentro). Este try/catch es el cinturón de acá: si AUN ASÍ tirara, no puede salir un 500 con
            // detalle técnico — se rechaza limpio y auditado. Es seguro tratarlo como "no se intercambió nada":
            // el único tramo del puerto que puede tirar antes de devolver resultado corre ANTES de cualquier rename.
            DatabaseSwapResult swap;
            try
            {
                swap = await _restorePort.SwapRestoredDatabaseIntoLiveAsync(newDatabaseName, CancellationToken.None);
            }
            catch (Exception swapEx)
            {
                _logger.LogCritical(swapEx,
                    "Restaurar TOTAL: el intercambio de nombres TIRÓ una excepción. Se rechaza sin haber cambiado la base viva. " +
                    "UserId={UserId}, Archivo={Archivo}, BackupPrevio={BackupPrevio}.",
                    requester.Id, fileName, backupPrevioFileName);
                throw new SystemDataRestoreRefusedException(
                    "No se pudo poner en marcha ese resguardo, así que no se cambió nada: el sistema quedó como estaba. " +
                    "Volvé a intentarlo o avisá al equipo técnico.");
            }

            if (!swap.Success)
            {
                // Ojo: el intercambio pudo haber quedado a mitad de camino. La vuelta atrás reconcilia POR ESTADO
                // y es idempotente (condición C1), así que llamarla acá es seguro incluso si el intercambio no
                // llegó a hacer nada: en ese caso no toca NADA y devuelve éxito.
                await RollbackAndThrowAsync(
                    pasoQueFallo: "el intercambio de nombres",
                    internalError: swap.ErrorMessage,
                    previousDatabaseName: swap.PreviousDatabaseName);
            }

            swappedPreviousDatabaseName = swap.PreviousDatabaseName;

            // Desde el intercambio hasta el final, TODO lo que queda es acomodar el sistema ya cambiado
            // (actualizarlo si hacía falta, dejar AFIP en homologación, reponer archivos, auditar): un solo
            // paso para el usuario, "poniendo el sistema al día".
            _maintenanceMode.SetStep(RestoreProgressSteps.Actualizacion);

            // 7) Actualizar el esquema si el resguardo era de una versión anterior (D3/B4).
            var migracionesAplicadas = 0;
            if (needsSchemaUpdate)
            {
                _maintenanceMode.Touch();
                var schemaUpdate = await _schemaUpdatePort.UpdateAsync(SchemaUpdatePolicy.Restore, CancellationToken.None);
                if (!schemaUpdate.Success)
                {
                    await RollbackAndThrowAsync(
                        pasoQueFallo: "la actualización del sistema",
                        internalError: schemaUpdate.ErrorMessage,
                        previousDatabaseName: swap.PreviousDatabaseName);
                }

                migracionesAplicadas = schemaUpdate.MigrationsApplied;
                _logger.LogWarning(
                    "Restaurar TOTAL: esquema actualizado. Migraciones aplicadas={Migraciones}.", migracionesAplicadas);
            }

            // 8) Forzar AFIP a homologación (B3), ahora DESPUÉS de migrar (contra el esquema ya al día) y DENTRO
            // del sobre de vuelta atrás: si no se puede confirmar, ya hay a dónde volver — antes era un
            // desenlace incierto sin salida.
            //
            // Antes del UPDATE, un calentamiento explícito de la conexión (recomendación aceptada en la re-review):
            // el intercambio mató todas las conexiones y vació el pool, así que ESTA es la primera consulta contra
            // la base nueva. Un parpadeo justo acá haría volver atrás un restore que salió PERFECTO, y eso sería
            // tirar el trabajo a la basura por nada.
            await WarmUpDatabaseConnectionAsync();

            try
            {
                await _context.Database.ExecuteSqlRawAsync(
                    """UPDATE "AfipSettings" SET "IsProduction" = false;""", CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "Restaurar TOTAL: no se pudo confirmar que AFIP quedó en homologación después del intercambio. Se vuelve atrás.");
                await RollbackAndThrowAsync(
                    pasoQueFallo: "la verificación de la conexión con AFIP",
                    internalError: ex.Message,
                    previousDatabaseName: swap.PreviousDatabaseName);
            }

            // 9) Reponer archivos de MinIO (B5): best-effort y FUERA del sobre de vuelta atrás (desvío declarado
            // en el ADR y aceptado en la re-review). La condición C2 pide que el resultado quede como DATO en la
            // auditoría, no solo dentro del mensaje al usuario.
            var (archivosMensaje, archivosRepuestos) = await TryRestoreMinioObjectsAsync(fileName, CancellationToken.None);

            _logger.LogWarning(
                "Restaurar TOTAL: EXITOSO. UserId={UserId}, Archivo={Archivo}, BackupPrevio={BackupPrevio}, " +
                "EsquemaActualizado={EsquemaActualizado}, MigracionesAplicadas={Migraciones}, ArchivosRepuestos={ArchivosRepuestos}.",
                requester.Id, fileName, backupPrevioFileName, needsSchemaUpdate, migracionesAplicadas, archivosRepuestos);

            // 10a) ANTES de dropear la copia anterior: verificar que el resguardo previo se pueda leer
            // (recomendación aceptada en la re-review). Cuesta segundos y es el ÚNICO cinturón contra "me
            // equivoqué de resguardo": si el archivo saliera ilegible, la copia anterior de la base es lo único
            // que queda, así que NO se dropea.
            var resguardoPrevioVerificado = await VerifyPreviousStateBackupAsync(backupPrevioFileName);

            // 10b) Auditoría DESPUÉS del restore, BEST-EFFORT (B6): un restore YA exitoso no puede convertirse en
            // un 500 para el usuario porque falló el INSERT del AuditLog.
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
                        // ADR-052 (D7): números y booleanos, nunca ids de migración ni nombres de base (T-5).
                        esquemaActualizado = needsSchemaUpdate,
                        migracionesAplicadas = migracionesAplicadas,
                        // Auditabilidad (hallazgo B1 de la revisión de riesgo de datos, 2026-07-30): el gate de
                        // versión tolera filas de historial que el sistema no conoce cuando son VIEJAS. Que el
                        // gate haya aflojado —y cuánto— tiene que quedar registrado; el número alcanza, los ids
                        // se quedan en el log interno (T-5).
                        historialHuerfanasToleradas = schemaCheck.ToleratedOrphanMigrationsCount,
                        archivosRepuestos = archivosRepuestos,
                        resguardoPrevioVerificado = resguardoPrevioVerificado,
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

            // La copia vieja se dropea recién ACÁ, con todo lo demás ya confirmado (D1.6) y SOLO si el resguardo
            // previo quedó verificado. Si el drop falla es basura en disco, NUNCA una pérdida de datos: no puede
            // convertir un restore exitoso en error (y por eso tampoco tiene que caer en la red de seguridad que
            // vuelve atrás, más abajo).
            if (resguardoPrevioVerificado)
            {
                try
                {
                    await _restorePort.DropDatabaseAsync(swap.PreviousDatabaseName, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Restaurar TOTAL: la restauración fue exitosa pero no se pudo dropear la copia anterior ({BaseAnterior}). " +
                        "Queda ocupando disco hasta la limpieza del próximo intento.",
                        swap.PreviousDatabaseName);
                }
            }
            else
            {
                _logger.LogCritical(
                    "Restaurar TOTAL: el resguardo previo ({BackupPrevio}) NO se pudo verificar, así que la copia anterior de la " +
                    "base ({BaseAnterior}) se CONSERVA como único respaldo. OJO: la limpieza de sobras del próximo intento la " +
                    "dropea — ver el runbook en docs/db-operations.md.",
                    backupPrevioFileName, swap.PreviousDatabaseName);
            }

            return new SystemDataRestoreResponse
            {
                Modo = RestoreModes.Total,
                Mensaje =
                    "Se restauró todo el sistema a la foto del resguardo elegido. Lo que se cargó después se perdió. " +
                    "Las sesiones y las contraseñas también volvieron a como estaban en ese momento. " +
                    (needsSchemaUpdate
                        ? "Como el resguardo era más viejo, el sistema se actualizó solo después de restaurarlo. "
                        : string.Empty) +
                    archivosMensaje,
            };
        }
        catch (Exception ex) when (ex is not SystemDataRestoreRefusedException && swappedPreviousDatabaseName is not null)
        {
            // Red de seguridad: una excepción que el código NO previó, ya del otro lado del intercambio. Sin este
            // catch, el finally apagaría el mantenimiento y dejaría el sistema abierto con la base restaurada a
            // medio terminar — justo lo que la vuelta atrás existe para evitar.
            await RollbackAndThrowAsync(
                pasoQueFallo: "un paso posterior al intercambio de nombres",
                internalError: ex.Message,
                previousDatabaseName: swappedPreviousDatabaseName);
            throw; // inalcanzable: RollbackAndThrowAsync siempre lanza. El compilador igual lo pide.
        }
        finally
        {
            // Desactivar modo mantenimiento SIEMPRE, EXCEPTO si el sistema no puede reabrirse solo (doble fallo
            // de la vuelta atrás): ahí tiene que seguir frenado hasta que el equipo técnico lo revise.
            if (!maintenanceOutcomeUnknown)
            {
                _maintenanceMode.Deactivate();
                _logger.LogWarning("Restaurar TOTAL: modo mantenimiento DESACTIVADO.");
            }
        }
    }

    /// <summary>
    /// ADR-052 (recomendación aceptada en la re-review): abre y descarta una conexión contra la base que acaba de
    /// quedar viva, con reintentos cortos, ANTES de la primera consulta que importa.
    ///
    /// <para><b>Por qué existe</b>: el intercambio de nombres mata todas las conexiones y vacía el pool. La primera
    /// consulta después de eso es la que paga el reconectar, y si justo ahí hay un parpadeo, el sistema volvería
    /// atrás un restore que salió PERFECTO. Este calentamiento absorbe ese parpadeo. Si igual no se puede conectar,
    /// NO se rechaza acá: se sigue y decide el paso siguiente (que ya sabe volver atrás) — así hay un solo lugar
    /// que decide, no dos.</para>
    /// </summary>
    private async Task WarmUpDatabaseConnectionAsync()
    {
        const int intentos = 3;
        for (var intento = 1; intento <= intentos; intento++)
        {
            try
            {
                if (await _context.Database.CanConnectAsync(CancellationToken.None))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Restaurar TOTAL: la conexión a la base recién intercambiada falló en el intento {Intento}/{Intentos} del calentamiento.",
                    intento, intentos);
            }

            if (intento < intentos)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
        }

        _logger.LogError(
            "Restaurar TOTAL: no se pudo confirmar la conexión a la base recién intercambiada después de {Intentos} intentos. " +
            "Se sigue igual: el paso siguiente decide (y sabe volver atrás).",
            intentos);
    }

    /// <summary>
    /// ADR-052 (recomendación aceptada en la re-review): verifica que el resguardo previo (el "deshacer el
    /// deshacer") se pueda LEER, antes de dropear la copia anterior de la base. Cuesta segundos y es el único
    /// cinturón contra "me equivoqué de resguardo". Best-effort: si la verificación misma explota, se considera NO
    /// verificado (fail-closed: se conserva la copia anterior).
    /// </summary>
    private async Task<bool> VerifyPreviousStateBackupAsync(string? backupPrevioFileName)
    {
        if (string.IsNullOrWhiteSpace(backupPrevioFileName) || !IsSafeFileName(backupPrevioFileName))
        {
            return false;
        }

        try
        {
            var verify = await _restorePort.VerifyBackupAsync(backupPrevioFileName, CancellationToken.None);
            if (verify.Success)
            {
                return true;
            }

            _logger.LogError(
                "Restaurar TOTAL: el resguardo previo ({BackupPrevio}) NO se pudo verificar. Motivo interno: {Error}",
                backupPrevioFileName, verify.ErrorMessage);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Restaurar TOTAL: falló la verificación del resguardo previo ({BackupPrevio}).", backupPrevioFileName);
            return false;
        }
    }

    /// <summary>
    /// ADR-052 (D2/D6): traduce el veredicto del gate a "se puede seguir o se rechaza", y si se puede seguir dice
    /// si HAY QUE ACTUALIZAR el esquema después de restaurar. Los textos son en criollo y SEPARADOS por caso
    /// (T-5 + menor M1): "versión más nueva" y "historial con agujero" son problemas distintos y un solo texto
    /// para los dos mentiría en uno de ellos.
    /// </summary>
    /// <returns><c>true</c> si el resguardo es de una versión anterior y el sistema tiene que actualizarse solo.</returns>
    private bool EnsureBackupVersionIsUsable(SchemaCompatibilityResult schemaCheck, string fileName)
    {
        switch (schemaCheck.Verdict)
        {
            case RestoreSchemaVerdict.Identical:
                return false;

            case RestoreSchemaVerdict.SubsetNeedsUpdate:
                _logger.LogWarning(
                    "Restaurar TOTAL: el resguardo {Archivo} es de una versión anterior; después de restaurarlo se van a aplicar {Faltantes} migración(es).",
                    fileName, schemaCheck.MissingMigrationsCount);
                return true;

            case RestoreSchemaVerdict.NewerThanSystem:
                _logger.LogError("Restaurar TOTAL: RECHAZADO — el resguardo {Archivo} es de una versión más nueva que el sistema.", fileName);
                throw new SystemDataRestoreRefusedException(NewerBackupRejectionMessage);

            case RestoreSchemaVerdict.HistoryGap:
                _logger.LogError("Restaurar TOTAL: RECHAZADO — el historial del resguardo {Archivo} tiene un salto en el medio.", fileName);
                throw new SystemDataRestoreRefusedException(HistoryGapRejectionMessage);

            case RestoreSchemaVerdict.LiveHasPendingMigrations:
                _logger.LogError("Restaurar TOTAL: RECHAZADO — el sistema quedó a mitad de una actualización (hay migraciones pendientes).");
                throw new SystemDataRestoreRefusedException(LivePendingMigrationsRejectionMessage);

            case RestoreSchemaVerdict.DumpHistoryEmpty:
            default:
                _logger.LogError(
                    "Restaurar TOTAL: RECHAZADO — no se pudo determinar la versión del resguardo {Archivo} (veredicto {Verdict}). Motivo interno: {Error}",
                    fileName, schemaCheck.Verdict, schemaCheck.ErrorMessage);
                throw new SystemDataRestoreRefusedException(
                    "No se pudo determinar de qué versión del sistema es ese resguardo, así que no se restauró nada. Avisá al equipo técnico.");
        }
    }

    /// <summary>
    /// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B5 de seguridad, "los archivos no vuelven"): si
    /// <paramref name="fileName"/> sigue el esquema de nombre propio de esta obra (<c>pre-restore-&lt;ts&gt;.dump</c>),
    /// intenta reponer los archivos de MinIO que se respaldaron junto con esa foto de la base. Si no se puede
    /// determinar el resguardo (backup de otro origen, ej. el sidecar diario de <c>docker-compose.yml</c>, o
    /// un backup viejo de "Empezar de cero"), o si la reposición falla, el mensaje AVISA CLARO — nunca se
    /// sugiere falsamente que los archivos volvieron.
    ///
    /// <para><b>ADR-052, condición C2 de la re-review</b>: además del mensaje, devuelve un BOOLEANO
    /// (<c>archivosRepuestos</c>) que va a la auditoría como dato. Sin eso, "los archivos no volvieron" quedaba
    /// solo dentro de un texto y era imposible de buscar después. Cómo re-correr la reposición sin restaurar de
    /// nuevo: <c>docs/db-operations.md</c>.</para>
    /// </summary>
    private async Task<(string Mensaje, bool ArchivosRepuestos)> TryRestoreMinioObjectsAsync(string fileName, CancellationToken ct)
    {
        var minioPrefix = TryDeriveMinioBackupPrefix(fileName);
        if (minioPrefix is null)
        {
            return ("Los archivos subidos (vouchers, adjuntos) no se recuperan con esta restauración: no se pudo determinar su resguardo.", false);
        }

        int restoredCount;
        try
        {
            restoredCount = await _backupPort.RestoreObjectsFromBackupPrefixAsync(minioPrefix, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restaurar TOTAL: fallo reponiendo los archivos de MinIO desde {Prefix}.", minioPrefix);
            return ("Los archivos subidos (vouchers, adjuntos) no se pudieron recuperar con esta restauración.", false);
        }

        return restoredCount > 0
            ? ("Los archivos subidos (vouchers, adjuntos) se repusieron desde el resguardo de ese momento.", true)
            : ("Los archivos subidos (vouchers, adjuntos) no se recuperaron con esta restauración: no se encontró un resguardo de archivos para ese momento.", false);
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
    ///
    /// <para><b>Rediseño 2026-07-30</b>: el prefijo sale de <see cref="BackupOriginRules"/>, que es el MISMO
    /// lugar del que lo lee la columna "Por qué se guardó" de la pantalla. Es <c>internal</c> para que el test
    /// pueda cerrar el círculo (el nombre que el motor ESCRIBE tiene que ser el que la columna SABE leer).</para>
    /// </summary>
    internal static string BuildPreviousStateBackupFileName(DateTime utcNow) =>
        $"{BackupOriginRules.PreRestoreFileNamePrefix}{utcNow:yyyyMMdd-HHmmss}.dump";

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
    /// ADR-052 (recomendación N1 de seguridad de la re-review): LISTA BLANCA de nombres de archivo, no solo
    /// "sin separadores de carpeta y termina en .dump".
    ///
    /// <para><b>Por qué importa</b>: el nombre viaja hasta la línea de comandos de <c>pg_restore</c>
    /// (entrecomillado). Un nombre con una comilla doble adentro podría cerrar el entrecomillado y meter FLAGS
    /// propios en el comando. Con la lista blanca, lo único que existe son letras, números, punto, guion y guion
    /// bajo — ninguno de esos caracteres puede escapar de las comillas.</para>
    /// </summary>
    internal static bool IsSafeFileName(string fileName) => SafeBackupFileNameRules.IsSafe(fileName);
}
