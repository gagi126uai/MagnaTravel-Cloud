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
/// Obra "Empezar de cero" (2026-07-27): borra TODOS los datos de negocio cargados (reservas, clientes,
/// proveedores, pasajeros, facturas, catálogo, etc.) dejando SIEMPRE intactos usuarios/roles/permisos y la
/// auditoría. Pensado para dejar la máquina lista para operar desde cero (demo/pruebas -> producción real),
/// con las siguientes garantías:
///
/// <list type="number">
///   <item><b>A prueba de dedos</b>: exige escribir la frase exacta "BORRAR TODO" + la contraseña del usuario
///   que ejecuta (mismo mecanismo que el login, <c>UserManager.CheckPasswordAsync</c>).</item>
///   <item><b>Candado fiscal, chequeado DOS VECES</b>: si hay algún comprobante emitido en el ambiente
///   PRODUCTIVO de ARCA, O si AFIP está configurado en PRODUCCIÓN ahora mismo (haya o no CAE todavía), el
///   borrado se rechaza. Se evalúa antes del backup Y de nuevo como PRIMER statement dentro de la transacción
///   de borrado (cierra la ventana TOCTOU: una factura podría emitirse con CAE productivo justo entre el
///   primer chequeo y el TRUNCATE). Ver <see cref="EvaluateFiscalLockAsync"/>.</item>
///   <item><b>Backup obligatorio ANTES de borrar</b>: si el backup (Postgres + copia verificada de MinIO, ver
///   <see cref="IWipeBackupPort"/>) falla, no se borra nada. Los ORIGINALES de MinIO recién se borran DESPUÉS
///   de que la transacción de Postgres hizo commit (best-effort, ver <see cref="ExecuteWipeAsync"/>).</item>
///   <item><b>Todo o nada</b>: el borrado real corre en SQL crudo dentro de UNA sola transacción — o se borró
///   todo y quedó auditado, o no pasó nada. Nunca EF/<c>SaveChanges</c> para el borrado en sí: el interceptor
///   de auditoría automática de <c>AppDbContext</c> generaría miles de <c>AuditLog</c> (uno por fila borrada)
///   en vez de UN solo evento de negocio.</item>
///   <item><b>Configuración opcional</b>: la configuración de la agencia (AFIP, políticas de aprobación, bot
///   de WhatsApp) solo se borra si el ejecutor lo pide explícitamente (<c>incluirConfiguracion=true</c>). Las
///   reglas de comisión GENERALES (sin proveedor asociado) son un caso especial: <c>CommissionRules</c> tiene
///   FK física a <c>Suppliers</c> (que SIEMPRE se trunca), así que se capturan y se re-insertan cuando NO se
///   incluye la configuración — ver <see cref="CaptureGeneralCommissionRulesAsync"/>.</item>
///   <item><b>Todo intento queda auditado</b>: tanto el éxito (<see cref="AuditActions.SystemDataWiped"/>)
///   como cualquier rechazo (<see cref="AuditActions.SystemDataWipeRejected"/> — frase, contraseña, candado
///   fiscal o backup fallido) quedan en el <c>AuditLog</c>, con el motivo en criollo y JAMÁS la contraseña.</item>
/// </list>
/// </summary>
public class SystemDataWipeService : ISystemDataWipeService
{
    /// <summary>Frase que el ejecutor tiene que escribir EXACTA (comparación ordinal, sin trim silencioso).</summary>
    private const string RequiredPhrase = "BORRAR TODO";

    private const string FiscalLockMessage =
        "Hay comprobantes emitidos en modo productivo: no se puede borrar. Los comprobantes fiscales reales deben conservarse.";

    /// <summary>
    /// Hardening final (revision 2026-07-27, prescripto por seguridad): mensaje cuando AFIP esta configurado
    /// en modo PRODUCTIVO ahora mismo, exista o no una factura con CAE todavia. Ver
    /// <see cref="EvaluateFiscalLockAsync"/>.
    /// </summary>
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
        var counts = await CountAllAsync(ct);
        var (bloqueado, motivo) = await EvaluateFiscalLockAsync(ct);

        return new SystemDataWipePreviewResponse
        {
            Conteos = counts,
            Bloqueado = bloqueado,
            MotivoBloqueo = motivo,
        };
    }

    public async Task<SystemDataWipeResponse> ExecuteWipeAsync(
        string requesterUserId,
        string password,
        string phrase,
        bool incluirConfiguracion,
        CancellationToken ct)
    {
        // Fix menor #6 (revision 2026-07-27): CUALQUIER rechazo (frase, contraseña, candado fiscal, backup
        // fallido) queda auditado con el motivo — nunca la contraseña. Un solo try/catch envolvente cubre
        // TODOS los caminos de rechazo, incluido el re-chequeo del candado fiscal DENTRO de la transaccion
        // (para ese momento la transaccion ya se deshizo sola via "await using" antes de llegar aca).
        try
        {
            return await ExecuteWipeCoreAsync(requesterUserId, password, phrase, incluirConfiguracion, ct);
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
        bool incluirConfiguracion,
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

        // 3) Candado fiscal (chequeo #1, fuera de la transaccion): si hay algún comprobante real, no seguimos.
        var (bloqueado, motivo) = await EvaluateFiscalLockAsync(ct);
        if (bloqueado)
        {
            throw new SystemDataWipeRefusedException(motivo!);
        }

        // Conteos ANTES de borrar: son los que se reportan como "lo que se borró".
        var counts = await CountAllAsync(ct);

        // 4) Backup OBLIGATORIO. Si falla, no se toca un solo dato (ni Postgres ni MinIO: el paso de MinIO
        // solo COPIA, ver PgDumpAndMinioWipeBackupPort — los originales siguen intactos pase lo que pase aca).
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

        // 5) Borrado real: SQL crudo, UNA sola transaccion. CreateExecutionStrategy() envuelve el reintento
        // transitorio de Npgsql (igual patron que AuthService.LoginAsync/RefreshAsync); acá además garantiza
        // que si algo falla a mitad de camino, Postgres deshace TODO (nada de borrado parcial).
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(ct);

            // Fix bloqueante #3 (revision 2026-07-27): re-chequeo del candado fiscal como PRIMER statement
            // DENTRO de la transaccion. Cierra la ventana TOCTOU: entre el chequeo #1 (arriba) y este punto
            // pudo haber corrido un ProcessInvoiceJob que le puso CAE productivo a una factura. Si esto tira,
            // "await using" deshace la transaccion (rollback) ANTES de que la excepcion llegue al catch de
            // ExecuteWipeAsync (que audita el rechazo fuera de cualquier transaccion abierta).
            var (bloqueadoEnTx, motivoEnTx) = await EvaluateFiscalLockAsync(ct);
            if (bloqueadoEnTx)
            {
                throw new SystemDataWipeRefusedException(motivoEnTx!);
            }

            // Fix bloqueante #2 (revision 2026-07-27): capturamos las reglas de comision GENERALES (sin
            // proveedor) ANTES del TRUNCATE. Suppliers cascadea FISICAMENTE sobre CommissionRules (FK real,
            // aunque SupplierId sea nullable) — sin esto, las reglas generales (que son CONFIGURACION) morian
            // igual aunque el tilde estuviera apagado. Las de un proveedor especifico SI mueren con el
            // proveedor (correcto en ambos casos).
            var generalCommissionRules = incluirConfiguracion
                ? null
                : await CaptureGeneralCommissionRulesAsync(ct);

            await TruncateBusinessTablesAsync(ct);
            await DeleteBankAccountsAsync(includeAgency: incluirConfiguracion, ct);

            if (incluirConfiguracion)
            {
                await TruncateConfigurationTablesAsync(ct);
                await ReseedApprovalPoliciesAsync(ct);
            }
            else if (generalCommissionRules is { Count: > 0 })
            {
                await RestoreGeneralCommissionRulesAsync(generalCommissionRules, ct);
            }

            await InsertWipeAuditLogAsync(requester, counts, backupResult, backupFileName, minioPrefix, incluirConfiguracion, ct);

            await transaction.CommitAsync(ct);
        });

        _logger.LogWarning(
            "Empezar de cero ejecutado por {UserId} ({UserName}). IncluirConfiguracion={IncluirConfiguracion}. Backup={BackupFile}",
            requester.Id, requester.UserName, incluirConfiguracion, backupResult.BackupFileName ?? backupFileName);

        // Fix bloqueante #1 (revision 2026-07-27): recien ACA, con el commit YA confirmado, borramos los
        // objetos ORIGINALES de MinIO (la copia de backup bajo el prefijo ya esta verificada desde el paso 4).
        // Best-effort a proposito: si esto falla, el wipe YA fue exitoso (Postgres + backup de MinIO existen)
        // - un objeto que quedo sin borrar es basura inofensiva, nunca una perdida de dato. Si en cambio la
        // transaccion de arriba hubiera fallado, este bloque NUNCA se ejecuta y los originales de MinIO
        // siguen intactos - "no se borro nada" vuelve a ser literalmente cierto.
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
            ConfiguracionBorrada = incluirConfiguracion,
        };
    }

    /// <summary>
    /// Candado fiscal: un comprobante emitido en el ambiente PRODUCTIVO de ARCA jamás se puede borrar, aunque
    /// el dueño reconfigure AFIP a homologación después. Tres capas (de más precisa a más conservadora):
    /// <list type="bullet">
    ///   <item>La correcta (no proxy): <see cref="Domain.Entities.Invoice.WasIssuedInProduction"/>, congelada
    ///   AL MOMENTO de conseguir el CAE (ver <c>AfipService.ProcessInvoiceJob</c>). Los históricos previos a
    ///   esta columna quedaron backfillados a <c>FALSE</c> (ver migración
    ///   <c>Adr051_DataWipe_BackfillWasIssuedInProductionForHistoricInvoices</c> — regla firmada del dueño: en
    ///   PROD solo se factura en homologación, nunca en modo productivo).</item>
    ///   <item>Hardening final (revisión 2026-07-27, prescripto por seguridad): si AFIP está configurado en
    ///   PRODUCCIÓN ahora mismo, se rechaza el wipe SIEMPRE — haya o no una factura con CAE todavía. No alcanza
    ///   con "no hay comprobantes reales hoy": si el ambiente es productivo, cualquier operación posterior
    ///   (incluso una emisión en curso) podría generar uno antes de que el dueño se dé cuenta. Es más simple y
    ///   más seguro exigir pasar a homologación primero, en vez de perseguir cada ventana de carrera con una
    ///   consulta de CAE puntual.</item>
    ///   <item>Cinturón y tiradores residual para el caso en que este método se invoque contra una base SIN
    ///   fila de <c>AfipSettings</c> (AFIP nunca configurado): sin fila no hay forma de saber el ambiente, así
    ///   que se sigue confiando en <see cref="Domain.Entities.Invoice.WasIssuedInProduction"/> (capa 1).</item>
    /// </list>
    /// Se llama DOS VECES por cada wipe (antes del backup y de nuevo como primer statement de la transacción)
    /// para cerrar la ventana TOCTOU — ver <see cref="ExecuteWipeCoreAsync"/>.
    /// </summary>
    private async Task<(bool Bloqueado, string? Motivo)> EvaluateFiscalLockAsync(CancellationToken ct)
    {
        var hasInvoiceMarkedProduction = await _context.Invoices.AsNoTracking()
            .AnyAsync(invoice => invoice.WasIssuedInProduction == true, ct);
        if (hasInvoiceMarkedProduction)
        {
            return (true, FiscalLockMessage);
        }

        var afipSettings = await _context.AfipSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (afipSettings is { IsProduction: true })
        {
            // Hardening final: ya no se condiciona a "existe algun CAE" — el ambiente productivo por si solo
            // basta para frenar el wipe. Mensaje distinto al candado por comprobante: acá el problema es la
            // CONFIGURACION (arreglable por el dueño reconfigurando AFIP), no un dato que haya que conservar.
            return (true, AfipProductionModeMessage);
        }

        return (false, null);
    }

    /// <summary>
    /// Conteos usando los DbSet tipados (LINQ), NO SQL crudo: así el preview corre igual contra Postgres real
    /// y contra InMemory (tests unitarios). Nota sobre <c>Cobros</c>: <c>Payment</c> tiene un query filter
    /// global (<c>!IsDeleted</c>), así que este conteo muestra pagos ACTIVOS — un pago soft-deleted ya se
    /// considera "borrado" para el usuario aunque el TRUNCATE de más abajo también le pegue a esa fila física.
    /// </summary>
    private async Task<SystemDataWipeCounts> CountAllAsync(CancellationToken ct)
    {
        return new SystemDataWipeCounts
        {
            Reservas = await _context.Reservas.CountAsync(ct),
            Clientes = await _context.Customers.CountAsync(ct),
            Operadores = await _context.Suppliers.CountAsync(ct),
            Pasajeros = await _context.Passengers.CountAsync(ct),
            Facturas = await _context.Invoices.CountAsync(ct),
            Cobros = await _context.Payments.CountAsync(ct),
            MovimientosCaja = await _context.CashLedgerEntries.CountAsync(ct),
            Archivos = await _context.ReservaAttachments.CountAsync(ct),
            PaisesYDestinos = await _context.Countries.CountAsync(ct) + await _context.Destinations.CountAsync(ct),
            Tarifario = await _context.Rates.CountAsync(ct),
            PosiblesClientes = await _context.Leads.CountAsync(ct),
        };
    }

    /// <summary>
    /// Snapshot MINIMO de una regla de comision GENERAL (sin proveedor) capturado antes del TRUNCATE, para
    /// poder re-insertarla si el tilde de configuracion esta apagado. NO guarda <c>Id</c> a proposito: la
    /// restauracion es un INSERT nuevo (fila nueva con Id propio), no una restauracion identica byte a byte.
    /// </summary>
    private sealed record CapturedCommissionRule(
        string? ServiceType,
        decimal CommissionPercent,
        int Priority,
        bool IsActive,
        DateTime CreatedAt,
        string? Description);

    /// <summary>
    /// Fix bloqueante #2: lee (sin trackear) las reglas de comision que NO estan atadas a un proveedor
    /// especifico (<c>SupplierId IS NULL</c>) — son configuracion agencia-wide, no datos de negocio por
    /// proveedor. Se llama DENTRO de la transaccion, ANTES del TRUNCATE.
    /// </summary>
    private async Task<List<CapturedCommissionRule>> CaptureGeneralCommissionRulesAsync(CancellationToken ct)
    {
        return await _context.CommissionRules.AsNoTracking()
            .Where(rule => rule.SupplierId == null)
            .Select(rule => new CapturedCommissionRule(
                rule.ServiceType, rule.CommissionPercent, rule.Priority, rule.IsActive, rule.CreatedAt, rule.Description))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Fix bloqueante #2: re-inserta (SQL crudo, dentro de la misma transaccion) las reglas de comision
    /// generales capturadas ANTES del TRUNCATE. Solo se llama cuando <c>incluirConfiguracion=false</c> — con
    /// el tilde puesto, CommissionRules es parte de "borrar TODA la configuracion" y no se restaura nada.
    /// </summary>
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

    /// <summary>
    /// TRUNCATE de TODAS las tablas de NEGOCIO y CATÁLOGO ("todo es todo": incluye países/destinos/tarifario,
    /// no solo reservas/clientes). <c>RESTART IDENTITY CASCADE</c> en un único statement: Postgres lo procesa
    /// atómicamente y el CASCADE arrastra cualquier tabla hija con FK hacia estas (aunque no esté en la lista),
    /// así que listar los padres alcanza. Lo que NUNCA aparece acá: AspNet* (usuarios/roles/permisos),
    /// RolePermissions, AuditLogs, tablas de Hangfire — esas sobreviven SIEMPRE, no tienen FK ENTRANTE desde
    /// ninguna tabla de este listado (el CASCADE nunca las toca).
    ///
    /// <para><b>OJO con <c>CommissionRules</c></b> (fix bloqueante #2): aunque conceptualmente es CONFIGURACION,
    /// vive ACA (no en <see cref="TruncateConfigurationTablesAsync"/>) porque tiene FK FISICA a
    /// <c>Suppliers</c> — el CASCADE la arrastraría de todos modos aunque no estuviera listada. Por eso se
    /// captura y re-inserta la porcion "general" ANTES de este TRUNCATE (ver
    /// <see cref="CaptureGeneralCommissionRulesAsync"/>/<see cref="RestoreGeneralCommissionRulesAsync"/>)
    /// cuando <c>incluirConfiguracion=false</c>.</para>
    /// </summary>
    private async Task TruncateBusinessTablesAsync(CancellationToken ct)
    {
        await _context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE
                "PartialCreditNoteReconciliationReceipts",
                "PartialCreditNoteReconciliations",
                "ClientCreditWithdrawals",
                "ClientCreditEntries",
                "SupplierCreditApplications",
                "SupplierCreditEntries",
                "DeductionLines",
                "OperatorRefundAllocations",
                "OperatorRefundsReceived",
                "BookingCancellationDebitNoteAnnulments",
                "BookingCancellationCreditNotes",
                "BookingCancellationLineTreasuryFxAdjustments",
                "BookingCancellationLineOperatorCharges",
                "BookingCancellationLines",
                "BookingCancellations",
                "ApprovalRequests",
                "CashLedgerEntries",
                "ArcaIdempotencyKeys",
                "ManualCashMovements",
                "PaymentReceipts",
                "VoucherAuditEntries",
                "VoucherPassengerAssignments",
                "Vouchers",
                "PassengerServiceAssignments",
                "ReservaEditAuthorizationChanges",
                "ReservaEditAuthorizations",
                "ReservaStatusChangeLogs",
                "ReservaAttachments",
                "ReservaPendingChanges",
                "CommissionAccruals",
                "CommissionRules",
                "InvoiceTribute",
                "InvoiceItem",
                "Invoices",
                "WhatsAppDeliveries",
                "MessageDeliveries",
                "QuoteItems",
                "Quotes",
                "LeadActivities",
                "Leads",
                "UpcomingStartAlertDismissals",
                "Notifications",
                "SupplierInvoicePaymentApplicationReversals",
                "SupplierInvoicePaymentApplications",
                "SupplierInvoiceLines",
                "SupplierInvoices",
                "SupplierPayments",
                "RateSupplierSales",
                "Rates",
                "CatalogPackageDepartures",
                "CatalogPackages",
                "HotelBookings",
                "TransferBookings",
                "PackageBookings",
                "AssistanceBookings",
                "CustomerCreditLimitByCurrency",
                "SupplierBalanceByCurrency",
                "ReservaMoneyByCurrency",
                "FlightSegments",
                "Payments",
                "Passengers",
                "Reservations",
                "TravelFiles",
                "Customers",
                "Suppliers",
                "Countries",
                "DestinationDepartures",
                "Destinations",
                "BnaExchangeRateSnapshots",
                "BusinessSequences",
                "RefreshTokens",
                "OutboxMessage",
                "OutboxState",
                "InboxState"
            RESTART IDENTITY CASCADE;
            """, ct);
    }

    /// <summary>
    /// TRUNCATE del grupo CONFIGURACIÓN — solo corre si <c>incluirConfiguracion=true</c>. Sin el tilde, estas
    /// 5 tablas quedan intactas (limpieza normal deja la agencia lista para seguir operando con su
    /// configuración de siempre). <c>CommissionRules</c> NO aparece acá a propósito: ya se truncó como parte
    /// del grupo de negocio (ver <see cref="TruncateBusinessTablesAsync"/>) porque el CASCADE de
    /// <c>Suppliers</c> la arrastra de todos modos; las reglas generales se restauran aparte cuando el tilde
    /// está apagado.
    /// </summary>
    private async Task TruncateConfigurationTablesAsync(CancellationToken ct)
    {
        await _context.Database.ExecuteSqlRawAsync("""
            TRUNCATE TABLE
                "AgencySettings",
                "AfipSettings",
                "OperationalFinanceSettings",
                "ApprovalPolicies",
                "WhatsAppBotConfigs"
            RESTART IDENTITY CASCADE;
            """, ct);
    }

    /// <summary>
    /// <c>BankAccounts</c> es polimórfica (Agencia/Cliente/Proveedor, ver <c>BankAccountOwnerType</c>) y
    /// <c>OwnerId</c> es FK LÓGICA (no física) — el CASCADE del TRUNCATE de negocio NO la toca. Por eso se
    /// borra aparte con DELETE, filtrando por dueño: las de Cliente/Proveedor son SIEMPRE negocio; la de la
    /// Agencia (OwnerType=0) es CONFIGURACIÓN y solo se borra con el tilde.
    /// </summary>
    private async Task DeleteBankAccountsAsync(bool includeAgency, CancellationToken ct)
    {
        await _context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"BankAccounts\" WHERE \"OwnerType\" IN (1, 2);", ct);

        if (includeAgency)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"BankAccounts\" WHERE \"OwnerType\" = 0;", ct);
        }
    }

    /// <summary>
    /// <c>ApprovalPolicies</c> NO tiene un "GetOrCreate" perezoso como <c>AgencySettings</c>/<c>AfipSettings</c>
    /// (ver <c>ApprovalPolicyService.RequiresApprovalAsync</c>: una fila ausente cae a un fallback GENÉRICO
    /// <c>true</c>, no al default de FÁBRICA de cada tipo). Sin este re-seed, truncar la tabla cambiaría
    /// SILENCIOSAMENTE el comportamiento de <c>PaymentDeadlineOverride</c>/<c>ReservationTransfer</c> (nacieron
    /// en <c>FALSE</c>; el fallback genérico es <c>TRUE</c>, o sea "ahora sí pide aprobación"). Estos 7 INSERT
    /// son EXACTAMENTE los mismos valores que sembraron las migraciones <c>AddApprovalPolicies</c> y
    /// <c>FC1_3_6_SeedPartialCreditNoteApprovalPolicy</c> — el sistema vuelve a los defaults de fábrica reales,
    /// no a un fallback genérico.
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
    /// El AuditLog del wipe se inserta con SQL crudo DENTRO de la misma transacción que el borrado (nunca via
    /// <c>IAuditService.LogBusinessEventAsync</c>, que hace su propio <c>SaveChanges</c> fuera de esta
    /// transacción): o se borró todo y quedó auditado, o no pasó nada — commit único.
    ///
    /// <para>Fix menor #6 (revision 2026-07-27): el JSON de <c>Changes</c> queda con TODOS los campos como
    /// ESCALARES (nada de objetos anidados) — la pantalla de auditoría del front pinta <c>[object Object]</c>
    /// cuando un valor de <c>Changes</c> es un objeto en vez de string/numero/booleano.</para>
    /// </summary>
    private async Task InsertWipeAuditLogAsync(
        ApplicationUser requester,
        SystemDataWipeCounts counts,
        WipeBackupResult backupResult,
        string backupFileName,
        string minioPrefix,
        bool incluirConfiguracion,
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
            incluirConfiguracion,
        });

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
