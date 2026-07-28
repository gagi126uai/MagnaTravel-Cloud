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

    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IDatabaseRestorePort _restorePort;
    private readonly IAuditService _auditService;
    private readonly ILogger<SystemDataRestoreService> _logger;

    public SystemDataRestoreService(
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        IDatabaseRestorePort restorePort,
        IAuditService auditService,
        ILogger<SystemDataRestoreService> logger)
    {
        _context = context;
        _userManager = userManager;
        _restorePort = restorePort;
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
        CancellationToken ct)
    {
        try
        {
            return await ExecuteRestoreCoreAsync(requesterUserId, password, phrase, fileName, modo, tablas, ct);
        }
        catch (SystemDataRestoreRefusedException ex)
        {
            await AuditRejectionAsync(ex.Message, requesterUserId, ct);
            throw;
        }
    }

    private async Task AuditRejectionAsync(string motivo, string requesterUserId, CancellationToken ct)
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

    private async Task<SystemDataRestoreResponse> ExecuteRestoreCoreAsync(
        string requesterUserId,
        string password,
        string phrase,
        string fileName,
        string modo,
        IReadOnlyList<string>? tablas,
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
            throw new SystemDataRestoreRefusedException("El modo de restauración tiene que ser 'prueba' o 'real'.");
        }

        if (modo == RestoreModes.Prueba)
        {
            return await ExecuteShadowRestoreAsync(requester, fileName, ct);
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
