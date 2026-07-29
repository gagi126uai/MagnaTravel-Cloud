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
/// Obra "Empezar de cero" (2026-07-27) + Parte A "Borrado selectivo por grupos" (2026-07-27, firmada): borra
/// SOLO los grupos de datos que el usuario elige (reservas y plata, clientes, operadores, tarifario, países y
/// destinos, posibles clientes, configuración — ver <see cref="WipeGroups"/>), dejando SIEMPRE intactos
/// usuarios/roles/permisos y la auditoría. Garantías:
///
/// <list type="number">
///   <item><b>A prueba de dedos</b>: exige escribir la frase exacta "BORRAR TODO" + la contraseña del usuario
///   que ejecuta (mismo mecanismo que el login, <c>UserManager.CheckPasswordAsync</c>).</item>
///   <item><b>Grupos coherentes ("tilda solo y avisa")</b>: si el caller pide un grupo sin alguno de sus
///   dependientes forzosos (ver <see cref="WipeGroups.ForcedDependencies"/>), se rechaza con 409 listando qué
///   falta — el front tilda solo los dependientes (belt), este chequeo es el cinturón (suspenders). Ver
///   <see cref="ValidateAndNormalizeGroups"/>.</item>
///   <item><b>Candado fiscal, chequeado DOS VECES, si el pedido puede tocar algo fiscal</b>: aplica cuando se
///   pide <c>reservasYPlata</c> (tiene <c>Invoices</c>) o <c>configuracion</c> (tiene <c>AfipSettings</c> —
///   hallazgo B5 de la revisión de seguridad: borrar la configuración de AFIP con comprobantes productivos
///   vivos tampoco puede pasar). Si ninguno de los dos está en el pedido, el candado no aplica (no tiene
///   sentido bloquear "borrar leads viejos" porque AFIP está en modo productivo). Si hay algún comprobante
///   emitido en el ambiente PRODUCTIVO de ARCA, o AFIP está configurado en PRODUCCIÓN ahora mismo, el borrado
///   se rechaza. Se evalúa antes del backup Y de nuevo como PRIMER statement dentro de la transacción de
///   borrado (cierra la ventana TOCTOU). Ver <see cref="RequiresFiscalLockCheck"/>/<see cref="EvaluateFiscalLockAsync"/>.</item>
///   <item><b>Ningún grupo deja huérfano a otro que no pediste</b>: varias tablas tienen foreign keys OPCIONALES
///   cruzando grupos (ej. un presupuesto "convertido en la reserva X", una reserva "originada en el lead Y").
///   Antes de truncar, esas foreign keys se DROPEAN temporalmente (no alcanza con poner la columna en NULL:
///   <c>TRUNCATE ... CASCADE</c> cascadea por la EXISTENCIA del constraint, no por el valor de los datos) y se
///   recrean después del TRUNCATE. Ver <see cref="DropCrossGroupForeignKeysAsync"/>/<see cref="ReattachForeignKeysAsync"/>.</item>
///   <item><b>Red de seguridad genérica fail-closed (B4)</b>: después de dropear las foreign keys CONOCIDAS,
///   se vuelve a consultar <c>information_schema</c> por CUALQUIER foreign key sin contemplar que cruce hacia
///   el conjunto a truncar. Si aparece alguna, se ABORTA el borrado entero (nada de cascadear en silencio) —
///   protege contra un mapa de desenganches desactualizado por una migración futura. Ver
///   <see cref="EnsureNoUnhandledCrossGroupForeignKeysAsync"/>. <see cref="FindForeignKeyAsync"/> también es
///   fail-closed: si una FK conocida no aparece (o aparece más de una vez), aborta en vez de seguir de largo.
///   La base real arrastra además tablas del esquema VIEJO que ya no están en el modelo de EF: se clasifican
///   aparte y se truncan solo si existen (ver <see cref="ReservasYPlataLegacyTables"/>).</item>
///   <item><b>Backup obligatorio ANTES de borrar</b>: si el backup (Postgres + copia verificada de MinIO, ver
///   <see cref="IWipeBackupPort"/>) falla, no se borra nada. Además, las comprobaciones caras que suelen
///   rechazar un pedido (el mapa de foreign keys) se ADELANTAN antes de generar el resguardo, ver
///   <see cref="RunForeignKeyPreflightAsync"/>. <b>OJO, no es una garantía absoluta</b> (corrección del
///   comentario, 2026-07-29): el adelanto REDUCE los resguardos huérfanos, no los elimina. Adentro de la
///   transacción siguen corriendo comprobaciones que pueden RECHAZAR el pedido con el resguardo ya generado
///   — el re-chequeo del candado fiscal (anti-TOCTOU) y las mismas comprobaciones de foreign keys, que son
///   las que mandan. Si alguna de esas rechaza, el borrado no toca un dato pero el archivo de resguardo ya
///   existe en el depósito. Es el precio de no negociar el invariante "el resguardo SIEMPRE existe antes de
///   tocar el primer dato".</item>
///   <item><b>Todo o nada</b>: el borrado real corre en SQL crudo dentro de UNA sola transacción.</item>
///   <item><b>Todo intento queda auditado</b>: éxito (<see cref="AuditActions.SystemDataWiped"/>) y rechazo
///   (<see cref="AuditActions.SystemDataWipeRejected"/>) quedan en el <c>AuditLog</c>, con el motivo en criollo
///   y JAMÁS la contraseña.</item>
/// </list>
/// </summary>
public class SystemDataWipeService : ISystemDataWipeService
{
    /// <summary>Frase que el ejecutor tiene que escribir EXACTA (comparación ordinal, sin trim silencioso).</summary>
    private const string RequiredPhrase = "BORRAR TODO";

    private const string FiscalLockMessage =
        "Hay comprobantes emitidos en modo productivo: no se puede borrar. Los comprobantes fiscales reales deben conservarse.";

    private const string AfipProductionModeMessage =
        "AFIP está en modo productivo: pasá a homologación antes de borrar datos. Los comprobantes reales no se tocan.";

    private readonly AppDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWipeBackupPort _backupPort;
    private readonly IAuditService _auditService;
    private readonly ILogger<SystemDataWipeService> _logger;

    public SystemDataWipeService(
        AppDbContext context,
        UserManager<ApplicationUser> userManager,
        IWipeBackupPort backupPort,
        IAuditService auditService,
        ILogger<SystemDataWipeService> logger)
    {
        _context = context;
        _userManager = userManager;
        _backupPort = backupPort;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<SystemDataWipePreviewResponse> GetPreviewAsync(CancellationToken ct)
    {
        var counts = await CountAllAsync(_context, ct);
        var (bloqueado, motivo) = await EvaluateFiscalLockAsync(ct);

        return new SystemDataWipePreviewResponse
        {
            Conteos = counts,
            Bloqueado = bloqueado,
            MotivoBloqueo = motivo,
            Dependencias = WipeGroups.ForcedDependencies.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }

    public async Task<SystemDataWipeResponse> ExecuteWipeAsync(
        string requesterUserId,
        string password,
        string phrase,
        IReadOnlyList<string> grupos,
        CancellationToken ct)
    {
        // Cualquier rechazo (frase, contraseña, grupos incoherentes, candado fiscal, backup fallido) queda
        // auditado con el motivo — nunca la contraseña. Un solo try/catch envolvente cubre TODOS los caminos.
        try
        {
            return await ExecuteWipeCoreAsync(requesterUserId, password, phrase, grupos, ct);
        }
        catch (SystemDataWipeRefusedException ex)
        {
            await _auditService.LogBusinessEventAsync(
                action: AuditActions.SystemDataWipeRejected,
                entityName: AuditActions.SystemDataWipeEntityName,
                entityId: DateTime.UtcNow.ToString("O"),
                details: ex.Message,
                userId: requesterUserId,
                userName: null,
                ct);
            throw;
        }
    }

    private async Task<SystemDataWipeResponse> ExecuteWipeCoreAsync(
        string requesterUserId,
        string password,
        string phrase,
        IReadOnlyList<string> grupos,
        CancellationToken ct)
    {
        // 1) Frase EXACTA. Ordinal (case-sensitive): el usuario tiene que escribir "BORRAR TODO" a la letra.
        if (!string.Equals(phrase, RequiredPhrase, StringComparison.Ordinal))
        {
            throw new SystemDataWipeRefusedException("La frase no coincide. Escribí exactamente: BORRAR TODO.");
        }

        // 2) Contraseña del usuario que ejecuta (mismo patrón que el login: AuthService.CheckPasswordAsync).
        var requester = await _userManager.FindByIdAsync(requesterUserId);
        if (requester is null || !await _userManager.CheckPasswordAsync(requester, password))
        {
            throw new SystemDataWipeRefusedException("La contraseña no es correcta.");
        }

        // 3) Grupos: validos, sin repetidos, y coherentes con las dependencias forzosas (regla "tilda solo y
        // avisa" — el front ya tilda los dependientes; esto es el cinturón que rechaza un pedido incompleto).
        var gruposResueltos = ValidateAndNormalizeGroups(grupos);

        // 4) Candado fiscal (chequeo #1, fuera de la transaccion): aplica si el grupo pedido puede tocar un
        // comprobante — "reservasYPlata" (contiene Invoices) O "configuracion" (contiene AfipSettings: borrar
        // la config de AFIP con comprobantes productivos vivos tampoco puede pasar, hallazgo B5 de la revision
        // de seguridad). Borrar solo tarifario/paises/leads nunca toca nada fiscal, no hay motivo para
        // bloquearlo por el estado de AFIP.
        if (RequiresFiscalLockCheck(gruposResueltos))
        {
            var (bloqueado, motivo) = await EvaluateFiscalLockAsync(ct);
            if (bloqueado)
            {
                throw new SystemDataWipeRefusedException(motivo!);
            }
        }

        // Conteos ANTES de borrar, recortados a SOLO los grupos pedidos (hallazgo N8 de la revision: informar
        // conteos de grupos que ni se tocaron seria "informar de mas" — el usuario podria creer que se borro
        // algo que en realidad seguia intacto).
        var counts = ScopeCountsToSelectedGroups(await CountAllAsync(_context, ct), gruposResueltos);

        // 5) ULTIMA validacion que puede RECHAZAR el pedido: el mapa de foreign keys (solo lectura). Va ANTES
        // del backup a proposito (fix 2026-07-28): antes, un pedido que la red fail-closed iba a rechazar igual
        // generaba un archivo de resguardo, y cada intento fallido dejaba un resguardo huerfano en el deposito.
        // Ver RunForeignKeyPreflightAsync: no toca ni un dato, y las mismas comprobaciones se repiten adentro
        // de la transaccion (esas son las que mandan).
        await RunForeignKeyPreflightAsync(gruposResueltos, ct);

        // 6) Backup OBLIGATORIO. Si falla, no se toca un solo dato.
        var timestamp = DateTime.UtcNow;
        var backupFileName = BuildBackupFileName(timestamp);
        var minioPrefix = BuildMinioPrefix(timestamp);
        var backupResult = await _backupPort.CreateBackupAsync(backupFileName, minioPrefix, ct);
        if (!backupResult.Success)
        {
            _logger.LogError(
                "Empezar de cero: el backup previo fallo, se aborta el borrado sin tocar datos. Motivo interno: {Error}",
                backupResult.ErrorMessage);
            throw new SystemDataWipeRefusedException(
                "No se pudo generar el backup previo. No se borró nada. Volvé a intentarlo o avisá al equipo técnico.");
        }

        // 7) Borrado real: SQL crudo, UNA sola transaccion.
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);

            // Re-chequeo del candado fiscal como PRIMER statement DENTRO de la transaccion (cierra la ventana
            // TOCTOU), mismo criterio de scoping que el chequeo #1.
            if (RequiresFiscalLockCheck(gruposResueltos))
            {
                var (bloqueadoEnTx, motivoEnTx) = await EvaluateFiscalLockAsync(ct);
                if (bloqueadoEnTx)
                {
                    throw new SystemDataWipeRefusedException(motivoEnTx!);
                }
            }

            // Mismo conjunto que ya validó RunForeignKeyPreflightAsync antes del backup (incluye los casos
            // especiales CommissionRules/RateSupplierSales y las tablas legacy que existan en esta base).
            var tablesToDelete = await BuildTablesToTruncateAsync(gruposResueltos, ct);

            // Rastro de auditoria de las tablas LEGACY (2026-07-29): se resuelven en tiempo de ejecucion
            // (existen o no segun la base), asi que sin este dato NO queda ningun registro de que se vacio.
            // Ver InsertWipeAuditLogAsync.
            var legacyTablesTruncated = ResolveLegacyTablesTruncated(tablesToDelete, gruposResueltos);

            var incluyeConfiguracion = gruposResueltos.Contains(WipeGroups.Configuracion);
            var incluyeOperadores = gruposResueltos.Contains(WipeGroups.Operadores);

            // CommissionRules es un caso especial (ver comentario de la constante): vive con Suppliers (FK
            // fisica) pero conceptualmente es CONFIGURACION. Se captura la porcion GENERAL (sin proveedor)
            // ANTES del truncate si no se va a restaurar (solo cuando "configuracion" no fue pedido).
            List<CapturedCommissionRule>? generalCommissionRules = null;
            if (incluyeOperadores && !incluyeConfiguracion)
            {
                generalCommissionRules = await CaptureGeneralCommissionRulesAsync(ct);
            }

            // Desenganchar referencias cruzadas OPCIONALES antes del TRUNCATE (ver el comentario largo del
            // metodo: hay que DROPEAR la foreign key, no alcanza con poner la columna en NULL — Postgres
            // cascadea por la EXISTENCIA del constraint, no por el valor actual de los datos).
            var droppedForeignKeys = await DropCrossGroupForeignKeysAsync(gruposResueltos, ct);

            // Red de seguridad generica fail-closed (hallazgo B4): si despues de dropear TODAS las FK
            // conocidas todavia queda alguna foreign key sin contemplar apuntando hacia el conjunto a
            // truncar, aborta ANTES de tocar un solo dato en vez de dejar que CASCADE se coma un grupo de mas.
            //
            // Se pasa el conjunto vacio de "foreign keys conocidas" porque para este punto ya fueron dropeadas
            // de verdad (a diferencia del preflight, que corre con todas puestas). Ver
            // BuildTableSetForForeignKeyCheck para por que las 5 tablas de "configuracion" se suman aparte.
            await EnsureNoUnhandledCrossGroupForeignKeysAsync(
                BuildTableSetForForeignKeyCheck(tablesToDelete, gruposResueltos),
                new HashSet<string>(StringComparer.Ordinal),
                ct);

            await TruncateTablesAsync(tablesToDelete, ct);
            await DeleteBankAccountsAsync(gruposResueltos, ct);

            // Repone las foreign keys dropeadas ANTES de truncar, ya con las columnas del lado sobreviviente
            // en NULL (nunca quedan apuntando a una fila que se acaba de borrar).
            await ReattachForeignKeysAsync(droppedForeignKeys, ct);

            if (incluyeConfiguracion)
            {
                await TruncateConfigurationTablesAsync(ct);
                await ReseedApprovalPoliciesAsync(ct);
            }
            else if (generalCommissionRules is { Count: > 0 })
            {
                await RestoreGeneralCommissionRulesAsync(generalCommissionRules, ct);
            }

            await InsertWipeAuditLogAsync(
                requester, counts, backupResult, backupFileName, minioPrefix, gruposResueltos, legacyTablesTruncated, ct);

            await transaction.CommitAsync(ct);
        });

        _logger.LogWarning(
            "Empezar de cero ejecutado por {UserId} ({UserName}). Grupos={Grupos}. Backup={BackupFile}",
            requester.Id, requester.UserName, string.Join(",", gruposResueltos), backupResult.BackupFileName ?? backupFileName);

        // Recien ACA, con el commit YA confirmado, borramos los objetos ORIGINALES de MinIO. Best-effort a
        // proposito (ver IWipeBackupPort.RemoveOriginalObjectsAsync).
        try
        {
            await _backupPort.RemoveOriginalObjectsAsync(backupResult, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Empezar de cero: fallo post-commit limpiando objetos originales de MinIO (basura inofensiva, el wipe ya fue exitoso).");
        }

        return new SystemDataWipeResponse
        {
            Borrado = counts,
            BackupArchivo = backupResult.BackupFileName ?? backupFileName,
            GruposBorrados = gruposResueltos.ToList(),
        };
    }

    /// <summary>
    /// Valida que <paramref name="grupos"/> sea una lista no vacia de nombres validos (ver
    /// <see cref="WipeGroups.All"/>), sin repetidos, y que incluya TODOS los dependientes forzosos de cada
    /// grupo pedido (<see cref="WipeGroups.ForcedDependencies"/>). Regla firmada "tilda solo y avisa": el
    /// front ya tilda los dependientes solo — esto es el cinturon que rechaza un pedido incompleto en vez de
    /// completarlo en silencio (si el front tiene un bug y no tilda el dependiente, mejor un 409 explicito que
    /// un borrado parcial que sorprenda al usuario).
    ///
    /// <para><b>Hallazgo bloqueante de data-exposure (ronda de revisión)</b>: ambos mensajes de rechazo usan
    /// <see cref="WipeGroups.GrupoLabels"/> (nombres de NEGOCIO en criollo) — NUNCA la clave interna cruda
    /// ("reservasYPlata", "posiblesClientes", etc, vocabulario de programador). El caso de "grupo desconocido"
    /// además evita hasta nombrar el token que mandó el caller (podría ser cualquier string arbitrario): el
    /// mensaje es genérico y el detalle técnico completo queda SOLO en el log del servidor.</para>
    /// </summary>
    private HashSet<string> ValidateAndNormalizeGroups(IReadOnlyList<string> grupos)
    {
        if (grupos is null || grupos.Count == 0)
        {
            throw new SystemDataWipeRefusedException("Elegí al menos un grupo de datos para borrar.");
        }

        var normalizados = new HashSet<string>(grupos, StringComparer.Ordinal);

        var invalidos = normalizados.Where(g => !WipeGroups.IsValid(g)).ToList();
        if (invalidos.Count > 0)
        {
            // El token invalido (podria ser cualquier string arbitrario, hasta con datos pegados por error)
            // NUNCA se le muestra al usuario - el detalle completo queda en el log del servidor.
            _logger.LogWarning(
                "Empezar de cero: se pidieron grupos desconocidos: {Invalidos}.", string.Join(", ", invalidos));
            throw new SystemDataWipeRefusedException(
                "Alguno de los grupos elegidos ya no existe. Actualizá la pantalla y probá de nuevo.");
        }

        var faltantes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var grupo in normalizados)
        {
            foreach (var dependiente in WipeGroups.ForcedDependencies[grupo])
            {
                if (!normalizados.Contains(dependiente))
                {
                    faltantes.Add(dependiente);
                }
            }
        }

        if (faltantes.Count > 0)
        {
            var faltantesEnCriollo = faltantes.Select(ToGrupoLabel);
            throw new SystemDataWipeRefusedException(
                $"Para borrar los grupos elegidos también hace falta incluir: {string.Join(", ", faltantesEnCriollo)}.");
        }

        return normalizados;
    }

    /// <summary>Traduce una clave interna de grupo ("reservasYPlata") a su nombre de negocio ("Reservas y su plata") — ver <see cref="WipeGroups.GrupoLabels"/>.</summary>
    private static string ToGrupoLabel(string grupo) =>
        WipeGroups.GrupoLabels.TryGetValue(grupo, out var label) ? label : grupo;

    /// <summary>
    /// El candado fiscal aplica si el pedido puede tocar algo fiscal: "reservasYPlata" (tiene <c>Invoices</c>)
    /// o "configuracion" (tiene <c>AfipSettings</c> — hallazgo B5: borrar la configuración de AFIP con
    /// comprobantes productivos vivos tampoco puede pasar, aunque no se toque ninguna reserva).
    /// </summary>
    private static bool RequiresFiscalLockCheck(HashSet<string> grupos) =>
        grupos.Contains(WipeGroups.ReservasYPlata) || grupos.Contains(WipeGroups.Configuracion);

    /// <summary>
    /// Recorta un <see cref="SystemDataWipeCounts"/> completo a SOLO los campos de los grupos pedidos (el
    /// resto queda en 0). Hallazgo N8: informar conteos de grupos que no se tocaron sería "informar de más".
    /// </summary>
    private static SystemDataWipeCounts ScopeCountsToSelectedGroups(SystemDataWipeCounts full, HashSet<string> grupos)
    {
        var scoped = new SystemDataWipeCounts();

        if (grupos.Contains(WipeGroups.ReservasYPlata))
        {
            scoped.Reservas = full.Reservas;
            scoped.Pasajeros = full.Pasajeros;
            scoped.Facturas = full.Facturas;
            scoped.Cobros = full.Cobros;
            scoped.MovimientosCaja = full.MovimientosCaja;
            scoped.Archivos = full.Archivos;
        }

        if (grupos.Contains(WipeGroups.Clientes))
        {
            scoped.Clientes = full.Clientes;
        }

        if (grupos.Contains(WipeGroups.Operadores))
        {
            scoped.Operadores = full.Operadores;
        }

        if (grupos.Contains(WipeGroups.Tarifario))
        {
            scoped.Tarifario = full.Tarifario;
        }

        if (grupos.Contains(WipeGroups.PaisesYDestinos))
        {
            scoped.PaisesYDestinos = full.PaisesYDestinos;
        }

        if (grupos.Contains(WipeGroups.PosiblesClientes))
        {
            scoped.PosiblesClientes = full.PosiblesClientes;
        }

        return scoped;
    }

    /// <summary>
    /// Candado fiscal: ver el comentario completo en la clase. Se llama DOS VECES por cada wipe cuyo pedido
    /// pueda tocar algo fiscal (antes del backup y de nuevo como primer statement de la transacción). Ver
    /// <see cref="RequiresFiscalLockCheck"/> para saber cuándo aplica.
    /// </summary>
    private async Task<(bool Bloqueado, string? Motivo)> EvaluateFiscalLockAsync(CancellationToken ct)
    {
        // (2026-07-28) La consulta en si vive en FiscalLockEvaluator, COMPARTIDA con SystemDataRestoreService
        // (modo total) - ver el comentario XML de esa clase sobre por que se comparte la consulta pero no el
        // mensaje (cada operacion usa su propio verbo: "borrar" aca, "restaurar" alla).
        var reason = await FiscalLockEvaluator.EvaluateAsync(_context, ct);
        return reason switch
        {
            FiscalLockEvaluator.Reason.LiveProductionInvoice => (true, FiscalLockMessage),
            FiscalLockEvaluator.Reason.AfipInProductionMode => (true, AfipProductionModeMessage),
            _ => (false, null),
        };
    }

    /// <summary>
    /// Conteos usando los DbSet tipados (LINQ), NO SQL crudo: asi corre igual contra Postgres real y contra
    /// InMemory (tests unitarios), y se puede reusar contra CUALQUIER <see cref="AppDbContext"/> — incluido
    /// uno apuntando a la base SOMBRA de una restauracion de prueba (Parte B, ver <c>SystemDataRestoreService</c>).
    /// </summary>
    internal static async Task<SystemDataWipeCounts> CountAllAsync(AppDbContext context, CancellationToken ct)
    {
        return new SystemDataWipeCounts
        {
            Reservas = await context.Reservas.CountAsync(ct),
            Clientes = await context.Customers.CountAsync(ct),
            Operadores = await context.Suppliers.CountAsync(ct),
            Pasajeros = await context.Passengers.CountAsync(ct),
            Facturas = await context.Invoices.CountAsync(ct),
            Cobros = await context.Payments.CountAsync(ct),
            MovimientosCaja = await context.CashLedgerEntries.CountAsync(ct),
            Archivos = await context.ReservaAttachments.CountAsync(ct),
            PaisesYDestinos = await context.Countries.CountAsync(ct) + await context.Destinations.CountAsync(ct),
            Tarifario = await context.Rates.CountAsync(ct),
            PosiblesClientes = await context.Leads.CountAsync(ct),
        };
    }

    private sealed record CapturedCommissionRule(
        string? ServiceType,
        decimal CommissionPercent,
        int Priority,
        bool IsActive,
        DateTime CreatedAt,
        string? Description);

    private async Task<List<CapturedCommissionRule>> CaptureGeneralCommissionRulesAsync(CancellationToken ct)
    {
        return await _context.CommissionRules.AsNoTracking()
            .Where(rule => rule.SupplierId == null)
            .Select(rule => new CapturedCommissionRule(
                rule.ServiceType, rule.CommissionPercent, rule.Priority, rule.IsActive, rule.CreatedAt, rule.Description))
            .ToListAsync(ct);
    }

    private async Task RestoreGeneralCommissionRulesAsync(List<CapturedCommissionRule> rules, CancellationToken ct)
    {
        foreach (var rule in rules)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "CommissionRules"
                    ("SupplierId", "ServiceType", "CommissionPercent", "Priority", "IsActive", "CreatedAt", "Description")
                VALUES
                    (NULL, {rule.ServiceType}, {rule.CommissionPercent}, {rule.Priority}, {rule.IsActive}, {rule.CreatedAt}, {rule.Description});
                """, ct);
        }
    }

    // ===== Tablas por grupo =====
    //
    // Cada tabla de negocio/catalogo vive en EXACTAMENTE UNO de estos 6 grupos (CommissionRules y
    // RateSupplierSales son casos especiales, ver arriba). El test de integracion
    // "InformationSchemaTables_CoincideExactamenteConListaBlancaMasSupervivientes" garantiza que TODA tabla
    // real de la base este clasificada en alguno de estos grupos, en configuracion, o en la lista de
    // supervivientes (AspNetUsers, AuditLogs, etc.) — una tabla nueva sin clasificar rompe ese test.

    internal const string CommissionRulesTable = "CommissionRules";
    internal const string RateSupplierSalesTable = "RateSupplierSales";

    /// <summary>
    /// Tablas propias del grupo "reservas y plata". Expuestas <c>internal</c> (con
    /// <c>InternalsVisibleTo("TravelApi.Tests")</c>) para que el test guardián
    /// "InformationSchemaTables_CoincideExactamenteConListaBlancaMasSupervivientes" DERIVE la lista esperada de
    /// ACÁ en vez de mantener una copia paralela que se puede desincronizar en silencio (N1).
    ///
    /// <para><b>Decisión firmada del dueño (2026-07-27, revisión de seguridad B3)</b>: "la plata del operador
    /// ligada a reservas se va CON las reservas; la ficha del operador queda". Por eso
    /// <c>SupplierInvoices</c>/<c>SupplierInvoiceLines</c>/<c>SupplierPayments</c>/sus aplicaciones y reversas, y
    /// el saldo materializado <c>SupplierBalanceByCurrency</c> viven ACÁ (no en <see cref="OperadoresTables"/>):
    /// borrar solo "reservasYPlata" (sin "operadores") también borra toda esa plata, dejando la ficha del
    /// proveedor (<c>Suppliers</c>) intacta pero SIN saldo huérfano — <c>SupplierBalanceByCurrency</c> es una
    /// PROYECCIÓN calculada de esa plata (ver su propio comentario XML), así que dejarla viva sin la plata que
    /// la respalda sería un saldo mentiroso.</para>
    ///
    /// <para><b>SupplierInvoiceLine.ReservaId es NOT NULL</b> (no es una FK opcional, no se puede desenganchar):
    /// por eso tiene que vivir en el MISMO grupo que <c>TravelFiles</c> siempre.</para>
    /// </summary>
    internal static readonly string[] ReservasYPlataTables =
    {
        "PartialCreditNoteReconciliationReceipts", "PartialCreditNoteReconciliations", "ClientCreditWithdrawals",
        "ClientCreditEntries", "SupplierCreditApplications", "SupplierCreditEntries", "DeductionLines",
        "OperatorRefundAllocations", "OperatorRefundsReceived", "BookingCancellationDebitNoteAnnulments",
        "BookingCancellationCreditNotes", "BookingCancellationLineTreasuryFxAdjustments",
        "BookingCancellationLineOperatorCharges", "BookingCancellationLines", "BookingCancellations",
        "ApprovalRequests", "CashLedgerEntries", "ArcaIdempotencyKeys", "ManualCashMovements", "PaymentReceipts",
        "VoucherAuditEntries", "VoucherPassengerAssignments", "Vouchers", "PassengerServiceAssignments",
        "ReservaEditAuthorizationChanges", "ReservaEditAuthorizations", "ReservaStatusChangeLogs",
        "ReservaAttachments", "ReservaPendingChanges", "CommissionAccruals", "InvoiceTribute", "InvoiceItem",
        "Invoices", "WhatsAppDeliveries", "MessageDeliveries", "UpcomingStartAlertDismissals", "Notifications",
        "HotelBookings", "TransferBookings", "PackageBookings", "AssistanceBookings", "ReservaMoneyByCurrency",
        "FlightSegments", "Payments", "Passengers", "Reservations", "TravelFiles", "BnaExchangeRateSnapshots",
        "BusinessSequences", "RefreshTokens", "OutboxMessage", "OutboxState", "InboxState",
        // Decision firmada B3 (ver comentario de la clase): plata del operador ligada a reservas.
        "SupplierInvoicePaymentApplicationReversals", "SupplierInvoicePaymentApplications",
        "SupplierInvoiceLines", "SupplierInvoices", "SupplierPayments", "SupplierBalanceByCurrency",
    };

    internal static readonly string[] ClientesTables = { "Customers", "CustomerCreditLimitByCurrency" };

    /// <summary>
    /// Decision firmada B3 (ver <see cref="ReservasYPlataTables"/>): "operadores" conserva SOLO la ficha del
    /// proveedor (datos de contacto/fiscales) — la plata que ese proveedor movió con reservas se fue con
    /// "reservasYPlata" (que "operadores" siempre arrastra, ver <see cref="WipeGroups.ForcedDependencies"/>).
    /// </summary>
    internal static readonly string[] OperadoresTables = { "Suppliers" };

    internal static readonly string[] TarifarioTables = { "Rates", "CatalogPackageDepartures", "CatalogPackages" };

    internal static readonly string[] PaisesYDestinosTables = { "Countries", "DestinationDepartures", "Destinations" };

    internal static readonly string[] PosiblesClientesTables = { "QuoteItems", "Quotes", "LeadActivities", "Leads" };

    // ===== Tablas LEGACY (fix del bug de PROD del 2026-07-28) =====
    //
    // POR QUE EXISTE ESTA SECCION: el borrado por grupos abortó en PROD (fail-closed, la red funcionó bien)
    // porque había una foreign key "sin contemplar" desde "CupoAssignments" hacia "Reservations". El mapa
    // estaba incompleto, y NINGUN test lo podia detectar: la base de los tests de integración se construye con
    // "EnsureCreated" a partir del MODELO DE EF ACTUAL, mientras que PROD arrastra tablas del esquema VIEJO
    // (anterior al "retail pivot" de enero) que se sacaron del modelo pero que ninguna migración llegó a
    // dropear. Esas tablas siguen existiendo en la base real, vacías o con datos muertos, y con sus foreign
    // keys puestas — por eso frenan al TRUNCATE.
    //
    // COMO SE ARMO LA LISTA: se recorrió el historial COMPLETO de migraciones (Up) reconstruyendo qué tablas
    // quedan creadas, y se restó el conjunto de tablas del modelo de EF actual. De esas sobrantes, las que
    // tienen una foreign key apuntando hacia alguna tabla de los grupos (o que son claramente datos del grupo)
    // se clasifican acá; el resto queda vivo (ver LegacyTablesThatStayAlive).
    //
    // TRAMPA IMPORTANTE: estas tablas NO existen en toda base (una base creada desde el modelo actual no las
    // tiene). Por eso NO se truncan a ciegas — ResolveExistingLegacyTablesAsync pregunta primero a
    // information_schema cuáles existen de verdad, y solo esas entran al TRUNCATE. Truncar una tabla
    // inexistente tiraría "relation does not exist" y rompería el borrado en cualquier base sana.

    /// <summary>
    /// Tablas legacy que se van CON "reservas y su plata". Criterio (mismo que el resto del archivo): el dato
    /// ligado a una reserva o a su plata se va con las reservas; los catálogos quedan.
    ///
    /// <list type="bullet">
    ///   <item><c>CupoAssignments</c>: asignación de un lugar del cupo a una reserva. Sin la reserva no
    ///   significa nada (FK <c>ReservationId</c> -&gt; <c>Reservations</c>). El bloque de cupos en sí
    ///   (<c>Cupos</c>) es catálogo y queda vivo.</item>
    ///   <item><c>BspReconciliationEntries</c> / <c>BspNormalizedRecords</c> / <c>BspImportRawRecords</c> /
    ///   <c>BspImportBatches</c>: conciliación BSP (la plata de los aéreos contra la cámara de compensación).
    ///   La entrada de conciliación apunta a la reserva; un lote importado sin sus conciliaciones es un dato
    ///   incompleto, así que se va el circuito entero.</item>
    ///   <item><c>TreasuryApplications</c> / <c>TreasuryReceipts</c>: recibos de tesorería viejos imputados a
    ///   reservas (<c>TreasuryApplications.ReservationId</c>). Es plata de reservas.</item>
    ///   <item><c>InvoiceItems</c> / <c>InvoiceTributes</c> (en PLURAL): nombres viejos de los renglones de
    ///   factura, con FK hacia <c>Invoices</c>. El modelo actual usa los nombres en singular
    ///   (<c>InvoiceItem</c>/<c>InvoiceTribute</c>, ya clasificados arriba).</item>
    /// </list>
    ///
    /// <para><b>Por qué NO están "Reservas" ni "Servicios" (sacadas el 2026-07-29, hallazgo N2 de la revisión
    /// de seguridad)</b>: eran los nombres viejos de <c>TravelFiles</c> y <c>Reservations</c>, y se habían
    /// agregado "por las dudas". Verificado contra la base real de producción ese día: NO existen — el
    /// renombre se hizo IN PLACE (la misma tabla cambió de nombre), así que no quedó ninguna tabla vieja
    /// colgando. Y en una instalación nueva tampoco pueden existir (nacen ya con el nombre actual). Eran
    /// además las dos entradas MÁS riesgosas de esta lista por tener nombres genéricos en castellano: si
    /// alguna vez alguien crea una tabla propia llamada "Reservas" o "Servicios" para otra cosa, este borrado
    /// se la llevaría puesta sin que nadie lo pidiera. Truncar una tabla que no existe no rompe nada acá
    /// (<see cref="ResolveExistingLegacyTablesAsync"/> filtra por <c>information_schema</c>), pero el riesgo
    /// de un falso positivo por nombre no compensa: si algún día aparece una base con el esquema pre-renombre,
    /// se vuelven a agregar con evidencia.</para>
    /// </summary>
    internal static readonly string[] ReservasYPlataLegacyTables =
    {
        "CupoAssignments",
        "BspReconciliationEntries", "BspNormalizedRecords", "BspImportRawRecords", "BspImportBatches",
        "TreasuryApplications", "TreasuryReceipts",
        "InvoiceItems", "InvoiceTributes",
    };

    /// <summary>
    /// Tablas legacy que se van con "posibles clientes": <c>QuoteVersions</c> son las versiones de un
    /// presupuesto viejo (FK <c>QuoteId</c> -&gt; <c>Quotes</c>), no tienen sentido sin el presupuesto.
    /// </summary>
    internal static readonly string[] PosiblesClientesLegacyTables = { "QuoteVersions" };

    /// <summary>
    /// Tablas legacy que QUEDAN VIVAS a propósito. Ninguna tiene foreign keys hacia las tablas que se truncan,
    /// así que no frenan ni cascadean nada; están acá para que quede escrito que se miraron y se decidió no
    /// tocarlas (y para que el test guardián las acepte sin marcarlas como "sin clasificar").
    ///
    /// <list type="bullet">
    ///   <item><c>Cupos</c>: el bloque de lugares en sí es CATALOGO (decisión firmada 2026-07-28), como el
    ///   tarifario: no se va con las reservas. <b>Efecto conocido y aceptado</b>: <c>Cupos</c> tiene una
    ///   columna <c>Reserved</c> (contador de lugares ya tomados) que la aplicación vieja mantenía a mano al
    ///   crear/borrar filas de <c>CupoAssignments</c>. Al truncar las asignaciones sin tocar el bloque, ese
    ///   contador puede quedar en un número viejo (dice "hay 8 tomados" cuando ya no hay ninguna asignación).
    ///   NO se corrige a propósito: el módulo de cupos está muerto (fuera del modelo de EF desde el "retail
    ///   pivot") y en producción sus tablas están VACÍAS (verificado el 2026-07-29), así que no hay ningún
    ///   contador real que quede mintiendo. Si algún día el módulo revive, hay que recalcular
    ///   <c>Reserved</c> después del borrado.</item>
    ///   <item><c>Agencies</c>: la referencia <c>AspNetUsers.AgencyId</c> -&gt; <c>Agencies</c> sale de un
    ///   usuario, y los usuarios NUNCA se borran — así que borrar la agencia dejaría usuarios huérfanos.</item>
    ///   <item><c>Tariffs</c> / <c>TariffValidities</c>: tarifario anterior al pivot (hoy el tarifario vivo es
    ///   <c>Rates</c>). Sin FK hacia nada vivo.</item>
    ///   <item><c>AccountingEntries</c> / <c>AccountingLines</c>: asientos del módulo contable que se dio de
    ///   baja. Sin FK hacia nada vivo. PENDIENTE DE DEFINICION: si algún día se decide que estos asientos son
    ///   "plata de reservas", pasarlos a <see cref="ReservasYPlataLegacyTables"/>.</item>
    ///   <item><c>_repair_*</c>: fotos de resguardo que sacaron las migraciones de reparación antes de tocar
    ///   datos. Son la red de seguridad de esas reparaciones: NUNCA se borran (por eso se aceptan por prefijo,
    ///   ver <see cref="LegacyRepairBackupTablePrefix"/>).</item>
    /// </list>
    /// </summary>
    internal static readonly string[] LegacyTablesThatStayAlive =
    {
        "Cupos", "Agencies", "Tariffs", "TariffValidities", "AccountingEntries", "AccountingLines",
    };

    /// <summary>
    /// Prefijo de las tablas de resguardo que dejan las migraciones de reparación (ver
    /// <see cref="LegacyTablesThatStayAlive"/>).
    ///
    /// <para><b>Aclaración (2026-07-29): esta constante NO protege nada por sí sola.</b> El servicio de
    /// borrado ni siquiera la consulta — las tablas <c>_repair_*</c> sobreviven simplemente porque no están
    /// en ninguna lista a truncar (acá se borra por lista blanca explícita, nunca "todo lo que haya"). El
    /// único que la usa es el test guardián
    /// <c>InformationSchemaTables_CoincideExactamenteConListaBlancaMasSupervivientes</c>, para aceptar por
    /// prefijo esas fotos de resguardo sin tener que listarlas una por una (van naciendo con cada migración
    /// de reparación). Está acá y no en el test para que el prefijo viva junto a la documentación de las
    /// tablas legacy.</para>
    /// </summary>
    internal const string LegacyRepairBackupTablePrefix = "_repair_";

    /// <summary>Tablas legacy que se truncan, agrupadas igual que las vivas. Fuente única para el servicio y para el test guardián.</summary>
    private static IEnumerable<string> LegacyTablesForGroups(HashSet<string> grupos)
    {
        if (grupos.Contains(WipeGroups.ReservasYPlata))
        {
            foreach (var tabla in ReservasYPlataLegacyTables) yield return tabla;
        }

        if (grupos.Contains(WipeGroups.PosiblesClientes))
        {
            foreach (var tabla in PosiblesClientesLegacyTables) yield return tabla;
        }
    }

    /// <summary>
    /// De todo el conjunto que se va a truncar, devuelve SOLO las tablas legacy (ordenadas), para dejarlas
    /// asentadas en la auditoría. Es una intersección en memoria — no vuelve a consultar la base — entre lo
    /// que efectivamente se trunca y el catálogo de tablas legacy de los grupos pedidos.
    /// </summary>
    private static List<string> ResolveLegacyTablesTruncated(HashSet<string> tablesToDelete, HashSet<string> grupos)
    {
        return LegacyTablesForGroups(grupos)
            .Where(tablesToDelete.Contains)
            .OrderBy(tabla => tabla, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Devuelve, de las tablas legacy que corresponden a los grupos pedidos, SOLO las que existen de verdad en
    /// esta base (consulta a <c>information_schema.tables</c>). En una base creada desde el modelo de EF actual
    /// (los tests, o una instalación nueva) el resultado es vacío y el borrado se comporta exactamente como
    /// antes; en PROD, que arrastra el esquema viejo, devuelve las que quedaron colgadas.
    /// </summary>
    private async Task<List<string>> ResolveExistingLegacyTablesAsync(HashSet<string> grupos, CancellationToken ct)
    {
        // Mismo motivo que en RunForeignKeyPreflightAsync: el proveedor InMemory de los tests unitarios no
        // puede consultar information_schema. En Postgres (produccion) siempre corre.
        var candidatas = LegacyTablesForGroups(grupos).ToList();
        if (candidatas.Count == 0 || !_context.Database.IsRelational())
        {
            return new List<string>();
        }

        var listaTablas = string.Join(", ", candidatas.Select(t => $"'{t}'"));

        // EF1002: "listaTablas" se arma con los arrays estaticos de este archivo, nunca con un string del caller.
#pragma warning disable EF1002
        return await _context.Database.SqlQueryRaw<string>($"""
            SELECT table_name AS "Value"
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
              AND table_name IN ({listaTablas})
            """).ToListAsync(ct);
#pragma warning restore EF1002
    }

    /// <summary>Arma el conjunto final de tablas a truncar segun los grupos pedidos (sin CommissionRules ni RateSupplierSales, se agregan aparte).</summary>
    private static HashSet<string> BuildTableSetForGroups(HashSet<string> grupos)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        if (grupos.Contains(WipeGroups.ReservasYPlata)) tables.UnionWith(ReservasYPlataTables);
        if (grupos.Contains(WipeGroups.Clientes)) tables.UnionWith(ClientesTables);
        if (grupos.Contains(WipeGroups.Operadores)) tables.UnionWith(OperadoresTables);
        if (grupos.Contains(WipeGroups.Tarifario)) tables.UnionWith(TarifarioTables);
        if (grupos.Contains(WipeGroups.PaisesYDestinos)) tables.UnionWith(PaisesYDestinosTables);
        if (grupos.Contains(WipeGroups.PosiblesClientes)) tables.UnionWith(PosiblesClientesTables);
        // "configuracion" NO agrega tablas aca: TruncateConfigurationTablesAsync las maneja aparte porque
        // ademas necesitan el reseed de ApprovalPolicies (no alcanza con un TRUNCATE liso).
        return tables;
    }

    /// <summary>
    /// Conjunto COMPLETO de tablas que va a truncar el borrado: las de los grupos pedidos + los dos casos
    /// especiales (<c>CommissionRules</c> y <c>RateSupplierSales</c>) + las tablas legacy que existan de verdad
    /// en esta base. Se usa DOS veces con el mismo resultado: en la validación previa al backup y adentro de la
    /// transacción — así lo que se valida antes de generar el resguardo es exactamente lo que se va a truncar.
    /// </summary>
    private async Task<HashSet<string>> BuildTablesToTruncateAsync(HashSet<string> grupos, CancellationToken ct)
    {
        var tables = BuildTableSetForGroups(grupos);

        var incluyeConfiguracion = grupos.Contains(WipeGroups.Configuracion);
        var incluyeOperadores = grupos.Contains(WipeGroups.Operadores);

        // CommissionRules es un caso especial (ver comentario de la constante): vive con Suppliers (FK fisica)
        // pero conceptualmente es CONFIGURACION.
        if (incluyeOperadores || incluyeConfiguracion)
        {
            tables.Add(CommissionRulesTable);
        }

        // RateSupplierSales es una cache de estadisticas de venta (Rate<->Supplier) sin valor de negocio
        // propio: sus dos FK (RateId, SupplierId) son OBLIGATORIAS (no nullable), asi que no se puede
        // "desenganchar" — muere entera apenas CUALQUIERA de sus dos padres muere.
        if (incluyeOperadores || grupos.Contains(WipeGroups.Tarifario))
        {
            tables.Add(RateSupplierSalesTable);
        }

        tables.UnionWith(await ResolveExistingLegacyTablesAsync(grupos, ct));

        return tables;
    }

    /// <summary>
    /// Conjunto de tablas contra el que se corre la red de seguridad de foreign keys. Es el conjunto a truncar
    /// MAS las 5 tablas de "configuracion" cuando ese grupo fue pedido: esas se truncan aparte (necesitan el
    /// reseed de ApprovalPolicies) y por eso no están en <see cref="BuildTablesToTruncateAsync"/>, pero la red
    /// igual tiene que cubrirlas.
    /// </summary>
    private static HashSet<string> BuildTableSetForForeignKeyCheck(HashSet<string> tablesToDelete, HashSet<string> grupos)
    {
        var tablas = new HashSet<string>(tablesToDelete, StringComparer.Ordinal);
        if (grupos.Contains(WipeGroups.Configuracion))
        {
            tablas.UnionWith(WipeGroups.ConfiguracionTables);
        }

        return tablas;
    }

    /// <summary>
    /// Metadata de una foreign key dropeada temporalmente para poder truncar un grupo sin arrastrar otro.
    /// Se descubre TODO por consulta a <c>information_schema</c> (constraint name, tabla/columna referenciada,
    /// regla de borrado) — nunca se hardcodea el nombre del constraint, que EF genera con su propia
    /// convención y puede no coincidir con lo que uno esperaría a simple vista.
    /// </summary>
    private sealed record DroppedForeignKey(
        string ChildTable, string ChildColumn, string ConstraintName, string ReferencedTable, string ReferencedColumn,
        string DeleteRule, string UpdateRule);

    /// <summary>Mensaje generico fail-closed: se usa tanto cuando una FK conocida desaparecio del modelo como cuando aparece una NO contemplada (ver <see cref="EnsureNoUnhandledCrossGroupForeignKeysAsync"/>).</summary>
    private const string UnhandledForeignKeyMessage =
        "Hay datos relacionados que este borrado no sabe manejar todavía; avisá al equipo técnico.";

    /// <summary>
    /// Dropea (temporalmente, DENTRO de la misma transacción) las foreign keys OPCIONALES que cruzan de un
    /// grupo a otro, ANTES del TRUNCATE, y deja en NULL la columna del lado que sobrevive.
    ///
    /// <para><b>Por qué no alcanza con un simple <c>UPDATE ... SET columna = NULL</c></b> (lección aprendida
    /// escribiendo esta obra): <c>TRUNCATE ... CASCADE</c> de Postgres cascadea según la EXISTENCIA del
    /// constraint de foreign key, no según el VALOR actual de los datos. Aunque la columna esté en NULL para
    /// todas las filas, si el constraint sigue ahí, <c>TRUNCATE</c> igual vacía la tabla completa. Por eso hay
    /// que DROPEAR el constraint (no solo los datos) antes de truncar, y volver a crearlo EXACTAMENTE igual
    /// después (ver <see cref="ReattachForeignKeysAsync"/>) — el modelo de EF queda intacto al terminar.</para>
    ///
    /// <para>Cada bloque de abajo documenta la foreign key real que motiva el desenganche (columna, tabla
    /// origen y destino) verificada contra el modelo de EF (<c>AppDbContext</c>) — no de memoria. Todas las
    /// columnas de esta lista son NULLABLE por diseño (si alguna dejara de serlo, la migración que la cambie
    /// tiene que revisar este método).</para>
    /// </summary>
    private async Task<List<DroppedForeignKey>> DropCrossGroupForeignKeysAsync(HashSet<string> grupos, CancellationToken ct)
    {
        var dropped = new List<DroppedForeignKey>();
        foreach (var (tabla, columna) in BuildCrossGroupForeignKeyCandidates(grupos))
        {
            // Fail-closed (hallazgo bloqueante de seguridad): si la FK esperada no aparece (o aparece mas de
            // una vez, algo que no debería pasar nunca para una columna simple), NO seguimos de largo — eso
            // seria fail-OPEN y dejaria que el CASCADE se coma un grupo que nadie pidio. Mejor abortar todo el
            // borrado con un aviso de "avisá al equipo técnico" que arriesgar un borrado de mas.
            var fk = await FindForeignKeyAsync(tabla, columna, ct);

            // EF1002: "fk" sale de FindForeignKeyAsync, que solo devuelve constraints que EXISTEN de verdad
            // en information_schema para las columnas de la lista candidatos de arriba (todas hardcodeadas
            // en este archivo) - nunca es un string libre que mande el caller.
#pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync(
                $"""ALTER TABLE "{fk.ChildTable}" DROP CONSTRAINT "{fk.ConstraintName}";""", ct);
            await _context.Database.ExecuteSqlRawAsync(
                $"""UPDATE "{fk.ChildTable}" SET "{fk.ChildColumn}" = NULL WHERE "{fk.ChildColumn}" IS NOT NULL;""", ct);
#pragma warning restore EF1002
            dropped.Add(fk);
        }

        return dropped;
    }

    /// <summary>
    /// Mapa (PURO, sin tocar la base) de las foreign keys OPCIONALES que hay que desenganchar segun los grupos
    /// pedidos. Está separado del método que las dropea para poder VALIDARLO antes de generar el resguardo (ver
    /// <see cref="RunForeignKeyPreflightAsync"/>): la validación necesita saber qué foreign keys son "conocidas"
    /// sin todavía modificar nada.
    /// </summary>
    private static List<(string Table, string Column)> BuildCrossGroupForeignKeyCandidates(HashSet<string> grupos)
    {
        var incluyeReservas = grupos.Contains(WipeGroups.ReservasYPlata);
        var incluyeClientes = grupos.Contains(WipeGroups.Clientes);
        var incluyeOperadores = grupos.Contains(WipeGroups.Operadores);
        var incluyeTarifario = grupos.Contains(WipeGroups.Tarifario);
        var incluyePosiblesClientes = grupos.Contains(WipeGroups.PosiblesClientes);

        var candidatos = new List<(string Table, string Column)>();

        // clientes SIN posiblesClientes: Quote.CustomerId y Lead.ConvertedCustomerId apuntan a Customers.
        if (incluyeClientes && !incluyePosiblesClientes)
        {
            candidatos.Add(("Quotes", "CustomerId"));
            candidatos.Add(("Leads", "ConvertedCustomerId"));
        }

        // reservasYPlata SIN posiblesClientes: Quote.ConvertedReservaId (columna real "ConvertedFileId") apunta a TravelFiles.
        if (incluyeReservas && !incluyePosiblesClientes)
        {
            candidatos.Add(("Quotes", "ConvertedFileId"));
        }

        // posiblesClientes SIN reservasYPlata: Reserva.SourceQuoteId/SourceLeadId apuntan a Quotes/Leads.
        if (incluyePosiblesClientes && !incluyeReservas)
        {
            candidatos.Add(("TravelFiles", "SourceQuoteId"));
            candidatos.Add(("TravelFiles", "SourceLeadId"));
        }

        // operadores SIN tarifario: Rate.SupplierId apunta a Suppliers.
        if (incluyeOperadores && !incluyeTarifario)
        {
            candidatos.Add(("Rates", "SupplierId"));
        }

        // operadores SIN posiblesClientes: QuoteItem.SupplierId apunta a Suppliers.
        if (incluyeOperadores && !incluyePosiblesClientes)
        {
            candidatos.Add(("QuoteItems", "SupplierId"));
        }

        // tarifario SIN posiblesClientes: QuoteItem.RateId apunta a Rates.
        if (incluyeTarifario && !incluyePosiblesClientes)
        {
            candidatos.Add(("QuoteItems", "RateId"));
        }

        // tarifario SIN reservasYPlata: TODOS los servicios tipados referencian Rates via RateId.
        if (incluyeTarifario && !incluyeReservas)
        {
            candidatos.Add(("Reservations", "RateId"));
            candidatos.Add(("HotelBookings", "RateId"));
            candidatos.Add(("TransferBookings", "RateId"));
            candidatos.Add(("PackageBookings", "RateId"));
            candidatos.Add(("AssistanceBookings", "RateId"));
            candidatos.Add(("FlightSegments", "RateId"));
        }

        // reservasYPlata SIN tarifario: Rate.CreatedFromReservaId apunta a TravelFiles (marca informativa de
        // "en que reserva nacio este producto de catalogo" — ADR-017 F1.1). Hallazgo B1 de la revision de
        // seguridad: sin este desenganche, borrar solo reservas se llevaba puesto TODO el tarifario por
        // CASCADE (y en cadena, QuoteItems/RateSupplierSales que dependen de Rates).
        if (incluyeReservas && !incluyeTarifario)
        {
            candidatos.Add(("Rates", "CreatedFromReservaId"));
        }

        return candidatos;
    }

    /// <summary>
    /// Busca en <c>information_schema</c> el constraint de foreign key definido sobre
    /// <paramref name="childColumn"/> de <paramref name="childTable"/>: nombre del constraint, tabla/columna
    /// REFERENCIADA y reglas de borrado/actualización (<c>delete_rule</c>/<c>update_rule</c>, que Postgres
    /// devuelve como el mismo texto SQL que hace falta para recrearlas: "SET NULL", "CASCADE", "RESTRICT", "NO
    /// ACTION"). <b>Fail-closed</b> (hallazgo bloqueante de seguridad): si no aparece EXACTAMENTE una fila (ni
    /// cero, ni más de una — una columna simple nunca debería tener más de una FK), TIRA
    /// <see cref="SystemDataWipeRefusedException"/> en vez de seguir de largo. Antes esto hacía <c>continue</c>
    /// silencioso (fail-open) si no encontraba nada, lo que dejaba el CASCADE libre para comerse un grupo
    /// entero sin que nadie se enterara.
    /// </summary>
    private async Task<DroppedForeignKey> FindForeignKeyAsync(string childTable, string childColumn, CancellationToken ct)
    {
        // EF1002: childTable/childColumn vienen SIEMPRE de la lista "candidatos" hardcodeada en
        // DropCrossGroupForeignKeysAsync, nunca de un string libre del caller.
#pragma warning disable EF1002
        var filas = await _context.Database.SqlQueryRaw<string>($"""
            SELECT tc.constraint_name || '|' || ccu.table_name || '|' || ccu.column_name || '|' || rc.delete_rule || '|' || rc.update_rule AS "Value"
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema
            JOIN information_schema.referential_constraints rc
              ON tc.constraint_name = rc.constraint_name AND tc.table_schema = rc.constraint_schema
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
              AND tc.table_name = '{childTable}' AND kcu.column_name = '{childColumn}'
            """).ToListAsync(ct);
#pragma warning restore EF1002

        if (filas.Count != 1)
        {
            _logger.LogError(
                "Empezar de cero: se esperaba EXACTAMENTE una foreign key para {ChildTable}.{ChildColumn}, se encontraron {Cantidad}.",
                childTable, childColumn, filas.Count);
            throw new SystemDataWipeRefusedException(UnhandledForeignKeyMessage);
        }

        var partes = filas[0].Split('|');
        return new DroppedForeignKey(childTable, childColumn, partes[0], partes[1], partes[2], partes[3], partes[4]);
    }

    /// <summary>
    /// Red de seguridad GENÉRICA fail-closed (hallazgo bloqueante B4 de la revisión de seguridad): después de
    /// dropear las foreign keys CONOCIDAS (<see cref="DropCrossGroupForeignKeysAsync"/>), vuelve a consultar
    /// <c>information_schema</c> por CUALQUIER foreign key que todavía apunte hacia una tabla que se va a
    /// truncar, DESDE una tabla que NO se va a truncar. Si aparece alguna, significa que el mapa de
    /// desenganches de este archivo quedó desactualizado (por ejemplo, una migración futura agrega una FK
    /// nueva cruzando grupos) — en vez de dejar que <c>TRUNCATE ... CASCADE</c> se coma esa tabla en silencio,
    /// se ABORTA todo el borrado con un aviso de "avisá al equipo técnico". Esta es la protección de último
    /// recurso: aunque el mapa manual de <see cref="DropCrossGroupForeignKeysAsync"/> tenga un agujero, este
    /// método lo detecta ANTES de tocar un solo dato.
    ///
    /// <para><paramref name="foreignKeysConocidas"/> son las foreign keys que el mapa SI conoce y que
    /// <see cref="DropCrossGroupForeignKeysAsync"/> va a dropear (formato <c>"Tabla.Columna"</c>). Sirven para
    /// poder correr esta misma red ANTES de la transacción, cuando esas foreign keys todavía están puestas: sin
    /// esa exclusión, la red se quejaría de las que ella misma sabe manejar. Adentro de la transacción se llama
    /// con el conjunto vacío, porque para ese momento ya fueron dropeadas de verdad.</para>
    /// </summary>
    private async Task EnsureNoUnhandledCrossGroupForeignKeysAsync(
        HashSet<string> tablesToDelete, IReadOnlySet<string> foreignKeysConocidas, CancellationToken ct)
    {
        if (tablesToDelete.Count == 0)
        {
            return;
        }

        var listaTablas = string.Join(", ", tablesToDelete.Select(t => $"'{t}'"));

        // EF1002: "listaTablas" se arma con los mismos strings estaticos que TruncateTablesAsync ya usa
        // (tablesToDelete sale de los arrays hardcodeados de este archivo) - nunca un string libre del caller.
#pragma warning disable EF1002
        var filasSinManejar = await _context.Database.SqlQueryRaw<string>($"""
            SELECT tc.table_name || '.' || kcu.column_name || ' -> ' || ccu.table_name AS "Value"
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
              AND ccu.table_name IN ({listaTablas})
              AND tc.table_name NOT IN ({listaTablas})
            """).ToListAsync(ct);
#pragma warning restore EF1002

        if (foreignKeysConocidas.Count > 0)
        {
            // Cada fila viene con el formato "Tabla.Columna -> TablaDestino"; la clave para comparar con el
            // mapa conocido es la parte de la izquierda.
            filasSinManejar = filasSinManejar
                .Where(fila => !foreignKeysConocidas.Contains(fila.Split(" -> ")[0]))
                .ToList();
        }

        if (filasSinManejar.Count > 0)
        {
            // El detalle tecnico (nombres de tabla/columna reales) SOLO va al log — nunca al usuario (T-5).
            _logger.LogError(
                "Empezar de cero: hay foreign keys SIN CONTEMPLAR cruzando hacia el conjunto a truncar: {Detalle}. Se aborta el borrado sin tocar datos.",
                string.Join(" | ", filasSinManejar));
            throw new SystemDataWipeRefusedException(UnhandledForeignKeyMessage);
        }
    }

    /// <summary>
    /// Validación PREVIA AL RESGUARDO (fix del bug "cada intento rechazado deja un resguardo huérfano",
    /// 2026-07-28). Corre en modo SOLO LECTURA las dos comprobaciones de foreign keys que pueden rechazar el
    /// borrado, para que un pedido que va a ser rechazado no llegue nunca a generar un archivo de resguardo:
    ///
    /// <list type="number">
    ///   <item>que TODAS las foreign keys del mapa conocido existan exactamente una vez
    ///   (<see cref="FindForeignKeyAsync"/>, fail-closed);</item>
    ///   <item>que no haya NINGUNA foreign key sin contemplar cruzando hacia el conjunto a truncar
    ///   (<see cref="EnsureNoUnhandledCrossGroupForeignKeysAsync"/>, la red genérica fail-closed).</item>
    /// </list>
    ///
    /// <para><b>Por qué las MISMAS comprobaciones siguen adentro de la transacción</b>: esto es un adelanto,
    /// no un reemplazo. Entre esta validación y el borrado real pasa el resguardo (que tarda), y en el medio
    /// alguien podría correr una migración que agregue una foreign key nueva. La comprobación de adentro de la
    /// transacción es la que manda; ésta solo evita ensuciar el depósito de resguardos con intentos que ya se
    /// sabe que van a fallar.</para>
    ///
    /// <para><b>Invariante que NO se negocia</b>: el resguardo SIEMPRE existe antes de tocar el primer dato.
    /// Este método no borra ni modifica nada — solo lee <c>information_schema</c>.</para>
    /// </summary>
    private async Task RunForeignKeyPreflightAsync(HashSet<string> grupos, CancellationToken ct)
    {
        // Trampa de framework (comentario didactico): este chequeo consulta information_schema, que es una
        // tabla propia de una base SQL. El proveedor "InMemory" que usan los tests unitarios no puede ejecutar
        // SQL de ningun tipo, asi que ahi no corre. En produccion el proveedor SIEMPRE es Postgres (Npgsql),
        // que si es relacional — la validacion nunca se saltea de verdad.
        if (!_context.Database.IsRelational())
        {
            return;
        }

        var tablesToDelete = await BuildTablesToTruncateAsync(grupos, ct);

        var foreignKeysConocidas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tabla, columna) in BuildCrossGroupForeignKeyCandidates(grupos))
        {
            await FindForeignKeyAsync(tabla, columna, ct); // fail-closed: tira si no existe o está duplicada
            foreignKeysConocidas.Add($"{tabla}.{columna}");
        }

        await EnsureNoUnhandledCrossGroupForeignKeysAsync(
            BuildTableSetForForeignKeyCheck(tablesToDelete, grupos), foreignKeysConocidas, ct);
    }

    /// <summary>
    /// Recrea, DENTRO de la misma transacción, las foreign keys dropeadas por
    /// <see cref="DropCrossGroupForeignKeysAsync"/> — con el MISMO nombre, misma columna, mismo destino y
    /// misma regla de borrado que tenían antes. Se llama DESPUES del TRUNCATE (que para entonces ya truncó la
    /// tabla referenciada) y de que la columna del lado sobreviviente quedó en NULL, así que la foreign key
    /// nueva nunca encuentra una fila inválida al recrearse.
    /// </summary>
    private async Task ReattachForeignKeysAsync(List<DroppedForeignKey> dropped, CancellationToken ct)
    {
        foreach (var fk in dropped)
        {
            // EF1002: "fk" viene de FindForeignKeyAsync (consulta a information_schema), nunca de un string
            // libre del caller.
#pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE "{fk.ChildTable}" ADD CONSTRAINT "{fk.ConstraintName}"
                FOREIGN KEY ("{fk.ChildColumn}") REFERENCES "{fk.ReferencedTable}" ("{fk.ReferencedColumn}")
                ON DELETE {fk.DeleteRule} ON UPDATE {fk.UpdateRule};
                """, ct);
#pragma warning restore EF1002
        }
    }

    /// <summary>
    /// TRUNCATE de las tablas resueltas en un UNICO statement (atomico) con <c>RESTART IDENTITY CASCADE</c>.
    /// Para este punto, <see cref="DropCrossGroupForeignKeysAsync"/> ya dropeó toda foreign key opcional hacia
    /// afuera del conjunto, asi que el CASCADE de Postgres ya no tiene forma de alcanzar una tabla de un grupo
    /// no pedido (esas tablas ya no tienen ningún constraint que las conecte a las que se van a truncar).
    /// </summary>
    private async Task TruncateTablesAsync(HashSet<string> tables, CancellationToken ct)
    {
        if (tables.Count == 0)
        {
            return;
        }

        var quotedTables = string.Join(", ", tables.Select(t => $"\"{t}\""));
        // EF1002: el analizador avisa "nunca interpoles directo en SQL crudo" porque asume que el valor puede
        // venir de un usuario. Aca NO es asi: "tables" sale SIEMPRE de los arrays estaticos hardcodeados de
        // este archivo (ReservasYPlataTables, ClientesTables, etc.) - nunca de un string que mande el caller.
#pragma warning disable EF1002
        await _context.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE {quotedTables} RESTART IDENTITY CASCADE;", ct);
#pragma warning restore EF1002
    }

    /// <summary>
    /// <c>BankAccounts</c> es polimórfica (Agencia/Cliente/Proveedor) y <c>OwnerId</c> es FK LÓGICA (no
    /// física) — el CASCADE del TRUNCATE de negocio NO la toca, se borra aparte por DELETE segun que grupos
    /// se pidieron: las de Cliente mueren con "clientes", las de Proveedor con "operadores", la de la Agencia
    /// solo con "configuracion".
    /// </summary>
    private async Task DeleteBankAccountsAsync(HashSet<string> grupos, CancellationToken ct)
    {
        if (grupos.Contains(WipeGroups.Clientes))
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"BankAccounts\" WHERE \"OwnerType\" = 1;", ct); // Customer
        }

        if (grupos.Contains(WipeGroups.Operadores))
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"BankAccounts\" WHERE \"OwnerType\" = 2;", ct); // Supplier
        }

        if (grupos.Contains(WipeGroups.Configuracion))
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"BankAccounts\" WHERE \"OwnerType\" = 0;", ct); // Agency
        }
    }

    private async Task TruncateConfigurationTablesAsync(CancellationToken ct)
    {
        var quotedTables = string.Join(", ", WipeGroups.ConfiguracionTables.Select(t => $"\"{t}\""));
        // EF1002: mismo motivo que TruncateTablesAsync — WipeGroups.ConfiguracionTables es un array estatico.
#pragma warning disable EF1002
        await _context.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE {quotedTables} RESTART IDENTITY CASCADE;", ct);
#pragma warning restore EF1002
    }

    /// <summary>
    /// <c>ApprovalPolicies</c> NO tiene un "GetOrCreate" perezoso: sin este re-seed, truncar la tabla
    /// cambiaria SILENCIOSAMENTE el comportamiento de <c>PaymentDeadlineOverride</c>/<c>ReservationTransfer</c>
    /// (nacieron en <c>FALSE</c>; el fallback generico de <c>ApprovalPolicyService</c> es <c>TRUE</c>). Estos 7
    /// INSERT son EXACTAMENTE los mismos valores que sembraron las migraciones <c>AddApprovalPolicies</c> y
    /// <c>FC1_3_6_SeedPartialCreditNoteApprovalPolicy</c>.
    /// </summary>
    private async Task ReseedApprovalPoliciesAsync(CancellationToken ct)
    {
        await _context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "ApprovalPolicies" ("RequestType", "RequiresApproval", "ExpirationDaysOverride", "UpdatedAt")
            VALUES
                ('InvoiceAnnulment', TRUE, NULL, NOW()),
                ('ReservationCancellationWithPayment', TRUE, NULL, NOW()),
                ('DiscountAboveThreshold', TRUE, NULL, NOW()),
                ('FrozenEntityMutation', TRUE, NULL, NOW()),
                ('PaymentDeadlineOverride', FALSE, NULL, NOW()),
                ('ReservationTransfer', FALSE, NULL, NOW()),
                ('PartialCreditNoteApproval', TRUE, 5, NOW());
            """, ct);
    }

    /// <summary>
    /// Trampa de framework (comentario didáctico): <c>JsonSerializer.Serialize</c> por default usa el encoder
    /// "seguro para HTML", que escapa CUALQUIER letra acentuada o con diéresis como <c>ó</c> en vez de
    /// dejarla literal — pensado para texto que se va a incrustar en una página HTML, no para JSON que se
    /// guarda en una columna de auditoría y lo lee un humano. Sin este encoder relajado, "Configuración"
    /// quedaría guardado como "Configuración" — técnicamente el mismo texto, pero ilegible a simple vista
    /// para cualquiera que abra la tabla de auditoría directo en la base. <c>UnicodeRanges.All</c> le dice
    /// "no escapes ningún caracter Unicode conocido", así el español con tildes/ñ queda tal cual se escribió.
    /// </summary>
    private static readonly JsonSerializerOptions AuditJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    /// <summary>
    /// El AuditLog del wipe se inserta con SQL crudo DENTRO de la misma transacción que el borrado: o se
    /// borró todo y quedó auditado, o no pasó nada — commit único. El JSON de <c>Changes</c> queda con todos
    /// los campos como ESCALARES (nada de objetos/arrays anidados: la pantalla de auditoría del front pinta
    /// <c>[object Object]</c> cuando un valor de <c>Changes</c> no es string/numero/booleano) — por eso los
    /// grupos borrados viajan como un string separado por comas, no como un array JSON.
    ///
    /// <para><b>Por qué la auditoría SÍ guarda nombres técnicos de tablas legacy (2026-07-29)</b>: las tablas
    /// del esquema viejo no tienen conteo propio en <see cref="SystemDataWipeCounts"/> (no están en el modelo
    /// de EF) y se resuelven en tiempo de ejecución según qué exista en esta base — sin este campo, vaciarlas
    /// no dejaría NINGÚN rastro de una destrucción irreversible. Es la única excepción, y es admisible porque
    /// la auditoría es admin-only (deuda ya existente en esta pantalla) y sirve para investigar un incidente,
    /// no para explicarle nada al usuario. La respuesta de la API (<see cref="SystemDataWipeResponse"/>) NO
    /// lleva este dato: ahí sigue valiendo T-5 al pie de la letra.</para>
    /// </summary>
    private async Task InsertWipeAuditLogAsync(
        ApplicationUser requester,
        SystemDataWipeCounts counts,
        WipeBackupResult backupResult,
        string backupFileName,
        string minioPrefix,
        HashSet<string> grupos,
        IReadOnlyList<string> legacyTablesTruncated,
        CancellationToken ct)
    {
        var changes = JsonSerializer.Serialize(new
        {
            reservasBorradas = counts.Reservas,
            clientesBorrados = counts.Clientes,
            operadoresBorrados = counts.Operadores,
            pasajerosBorrados = counts.Pasajeros,
            facturasBorradas = counts.Facturas,
            cobrosBorrados = counts.Cobros,
            movimientosCajaBorrados = counts.MovimientosCaja,
            archivosBorrados = counts.Archivos,
            paisesYDestinosBorrados = counts.PaisesYDestinos,
            tarifarioBorrado = counts.Tarifario,
            posiblesClientesBorrados = counts.PosiblesClientes,
            backupArchivo = backupResult.BackupFileName ?? backupFileName,
            backupMinioPrefijo = backupResult.MinioPrefix ?? minioPrefix,
            // Hallazgo bloqueante de data-exposure: nombres de NEGOCIO en el audit log, nunca las claves
            // internas crudas (ver WipeGroups.GrupoLabels) - la auditoria la puede leer un no-programador.
            gruposBorrados = string.Join(", ", grupos.OrderBy(g => g, StringComparer.Ordinal).Select(ToGrupoLabel)),
            // Escalar (string separado por comas) igual que gruposBorrados: la pantalla de auditoria no sabe
            // pintar arrays. Vacio = esta base no tenia ninguna tabla del esquema viejo (caso normal).
            tablasDelEsquemaViejoVaciadas = legacyTablesTruncated.Count == 0
                ? "ninguna"
                : string.Join(", ", legacyTablesTruncated),
        }, AuditJsonOptions);

        var now = DateTime.UtcNow;
        var userName = string.IsNullOrWhiteSpace(requester.FullName) ? requester.Email : requester.FullName;

        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AuditLogs" ("UserId", "UserName", "Action", "EntityName", "EntityId", "Timestamp", "Changes", "Category")
            VALUES ({requester.Id}, {userName}, {AuditActions.SystemDataWiped}, {AuditActions.SystemDataWipeEntityName},
                    {now.ToString("O")}, {now}, {changes}, {"Business"});
            """, ct);
    }

    private static string BuildBackupFileName(DateTime utcNow) => $"wipe-{utcNow:yyyyMMdd-HHmmss}.dump";

    private static string BuildMinioPrefix(DateTime utcNow) => $"wipe-backup-{utcNow:yyyyMMdd-HHmmss}/";
}
