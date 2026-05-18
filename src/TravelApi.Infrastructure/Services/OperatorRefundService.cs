using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.2.2 v3 §6.3 (2026-05-18): orquesta los ingresos fisicos que la agencia
/// recibe del operador (T2 del flujo) y los reparte N:M contra los
/// <see cref="BookingCancellation"/> via <see cref="OperatorRefundAllocation"/>.
///
/// <para>
/// <b>Patron de transacciones</b>: cada metodo abre una unidad de trabajo que
/// commitea con un solo <c>SaveChangesAsync</c> al final (HC1 plan v3). Los
/// services internos invocados (BookingCancellationService, ClientCreditService)
/// solo hacen <c>Add()</c> en memoria — el commit lo manejamos nosotros para
/// mantener atomicidad real entre allocation, deductions, manual cash movement
/// y client credit entry.
/// </para>
///
/// <para>
/// <b>Concurrencia N:M</b>: cuando dos cashiers allocate paralelos contra el
/// mismo refund, el cap se valida con un CHECK SQL Postgres
/// (<c>chk_OperatorRefundsReceived_allocated_not_exceeds</c>). Si supera, EF
/// recibe <c>SqlState 23514</c> y el <see cref="BusinessInvariantInterceptor"/>
/// lo traduce a <see cref="BusinessInvariantViolationException"/> con codigo
/// INV-084. El service ademas implementa retry xmin limitado: la primera
/// transaccion que detecta concurrencia recarga el refund + BC y reintenta.
/// </para>
///
/// <para>
/// <b>Matriz fiscal Mono/RI</b>: el modulo se comporta distinto segun la
/// condicion fiscal cristalizada en el <see cref="FiscalSnapshot"/> del BC:
/// <list type="bullet">
///   <item>INV-105: Supplier Monotributo + deducciones tipo retencion AR
///     (kinds 10..39) → rechazar (no esta inscripto en el regimen).</item>
///   <item>INV-115: Agency Monotributo + deducciones tipo retencion AR →
///     rechazar (no genera credito fiscal IVA).</item>
/// </list>
/// </para>
/// </summary>
public class OperatorRefundService : IOperatorRefundService
{
    private const int MaxConcurrencyRetries = 3;

    private readonly AppDbContext _db;
    private readonly IBookingCancellationService _bcService;
    private readonly IClientCreditService _clientCreditService;
    private readonly IAuditService _auditService;
    private readonly IOperationalFinanceSettingsService _settings;
    private readonly ILogger<OperatorRefundService> _logger;

    public OperatorRefundService(
        AppDbContext db,
        IBookingCancellationService bcService,
        IClientCreditService clientCreditService,
        IAuditService auditService,
        IOperationalFinanceSettingsService settings,
        ILogger<OperatorRefundService> logger)
    {
        _db = db;
        _bcService = bcService;
        _clientCreditService = clientCreditService;
        _auditService = auditService;
        _settings = settings;
        _logger = logger;
    }

    // =========================================================================
    // RecordReceivedAsync
    // =========================================================================

    public async Task<OperatorRefundReceivedDto> RecordReceivedAsync(
        RecordOperatorRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        if (request.ReceivedAmount <= 0m)
        {
            // Defensivo aunque el DataAnnotation ya valida — el service tambien
            // puede ser invocado desde background jobs que no pasan por validator.
            throw new ArgumentException("ReceivedAmount debe ser > 0.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
        {
            throw new ArgumentException("Currency es ISO 4217 (3 chars).", nameof(request));
        }

        // 1) Resolver Supplier (Include para que ManualCashMovementBuilder
        //    pueda armar la descripcion "Devolucion del operador {Name}").
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.PublicId == request.SupplierPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"Supplier {request.SupplierPublicId} no encontrado.");

        // 2) Crear el aggregate del ingreso.
        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id,
            Supplier = supplier, // navigation seteada explicitamente para que el builder NO tire
            ReceivedAt = request.ReceivedAt.ToUniversalTime(),
            ReceivedAmount = ReservationEconomicPolicy.RoundCurrency(request.ReceivedAmount),
            AllocatedAmount = 0m,
            Method = string.IsNullOrWhiteSpace(request.Method) ? "Transfer" : request.Method!,
            Reference = request.Reference,
            Currency = request.Currency.ToUpperInvariant(),
            // ExchangeRateAtReceipt: en MVP se setea = 1 si la moneda es ARS y no
            // viene en el request. Una FC futura agrega FetchedAt + fuente.
            ExchangeRateAtReceipt = 1m,
            ReceivedByUserId = userId,
            ReceivedByUserName = userName ?? string.Empty,
        };
        _db.OperatorRefundReceived.Add(refund);

        // 3) Crear el ManualCashMovement Income asociado al ingreso fisico
        //    (cierra INV-CONT-09: el deposito se ve en el Libro de Caja).
        //    El builder valida que Supplier no sea null y ReceivedAmount > 0.
        //
        //    Trainee/junior — bug fix 2026-05-18:
        //    El builder setea la NAVIGATION property `OperatorRefundReceived = refund`
        //    (no el FK escalar refund.Id). Eso es clave porque en este punto el
        //    refund acaba de hacer Add() y todavia NO se persistio: refund.Id == 0
        //    hasta que el SaveChangesAsync final corra. Si el builder seteara el
        //    FK escalar a 0, Postgres recibiria una FK invalida y rompe el INSERT
        //    con 23503. EF resuelve esto al guardar en orden topologico cuando
        //    seteamos la navigation property: inserta primero el refund, obtiene
        //    el Id real, y despues inserta el movement con la FK correcta.
        var movement = ManualCashMovementBuilder.BuildIncomeForRefund(refund, userId);
        // Sobrescribimos la Description si vino un Notes en el request — util
        // para que el cashier deje contexto operativo en caja.
        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            // Mantenemos el formato base + un sufijo con las notas (recorta a 500).
            var withNotes = $"{movement.Description} — {request.Notes!.Trim()}";
            movement.Description = withNotes.Length > 500 ? withNotes[..500] : withNotes;
        }
        _db.ManualCashMovements.Add(movement);

        // 4) Audit. Lo armamos con todo el contexto fiscal del ingreso para que
        //    el contador pueda reconstruir el evento sin ir a la tabla principal.
        //    EntityId: usamos refund.PublicId (Guid asignado por default en el
        //    field de la entidad). El Id int todavia es 0 — solo lo tendremos
        //    despues del SaveChangesAsync final. AuditService persiste su propia
        //    fila inmediatamente (Repository.AddAsync llama SaveChanges), por
        //    eso necesitamos un identificador estable AHORA, no despues.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundReceivedRegistered,
            entityName: AuditActions.OperatorRefundReceivedEntityName,
            entityId: refund.PublicId.ToString(),
            details: JsonSerializer.Serialize(new
            {
                refundPublicId = refund.PublicId,
                supplierId = supplier.Id,
                supplierPublicId = supplier.PublicId,
                supplierName = supplier.Name,
                refund.ReceivedAmount,
                refund.Currency,
                refund.Method,
                refund.Reference,
                refund.ReceivedAt,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 5) SaveChanges unico: refund + movement + audit log se persisten juntos.
        await _db.SaveChangesAsync(ct);

        // 6) Mapeo de salida.
        return MapRefund(refund);
    }

    // =========================================================================
    // AllocateAsync (concurrencia N:M con retry xmin)
    // =========================================================================

    public async Task<OperatorRefundAllocationDto> AllocateAsync(
        Guid refundPublicId,
        AllocateRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        // Validaciones tempranas del payload (defensivo).
        if (request.GrossAmount <= 0m)
        {
            throw new ArgumentException("GrossAmount debe ser > 0.", nameof(request));
        }
        if (request.Deductions is null)
        {
            throw new ArgumentException("Deductions no puede ser null (usar lista vacia).", nameof(request));
        }
        foreach (var dl in request.Deductions)
        {
            if (dl.Amount <= 0m)
            {
                throw new ArgumentException("Cada deduccion debe tener Amount > 0.", nameof(request));
            }
        }

        // Retry loop ante concurrencia xmin. Cada iteracion abre un Detach
        // implicito porque cargamos las entidades de nuevo desde la BD.
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryAllocateOnceAsync(refundPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Otra tx modifico el refund (o el BC) entre nuestro Load y
                // nuestro Save. Loggeamos a debug + clear ChangeTracker para
                // forzar reload, y reintentamos. La tercera vez relanzamos:
                // un cliente legitimo se rinde con un 409.
                _logger.LogWarning(ex,
                    "AllocateAsync concurrency conflict on attempt {Attempt}/{Max} for refund {RefundPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, refundPublicId);

                // Soltamos lo trackeado para que el siguiente Load no pelee con
                // las entidades stale del intento previo. ChangeTracker.Clear
                // es net8/EF8 — equivalente al viejo Detach manual.
                _db.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries - 1)
                {
                    throw;
                }

                // Backoff exponencial corto: 100ms, 400ms. Sin jitter porque
                // los conflicts genuinos son raros y un par de retries ordenados
                // alcanzan.
                var delayMs = (int)Math.Pow(4, attempt) * 100;
                await Task.Delay(delayMs, ct);
            }
        }

        // Unreachable (el loop o retorna o relanza), pero el compilador lo necesita.
        throw new InvalidOperationException("AllocateAsync retry loop exhausted sin resultado.");
    }

    private async Task<OperatorRefundAllocationDto> TryAllocateOnceAsync(
        Guid refundPublicId,
        AllocateRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 1) Cargar refund + supplier (Include para el matrix fiscal del operador).
        var refund = await _db.OperatorRefundReceived
            .Include(r => r.Supplier)
            .FirstOrDefaultAsync(r => r.PublicId == refundPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"OperatorRefundReceived {refundPublicId} no encontrado.");

        // 2) Cargar BC target con su Reserva (necesaria para que el callback de
        //    BookingCancellationService pueda actualizar el Status de la Reserva).
        var bc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .Include(b => b.Supplier)
            .FirstOrDefaultAsync(b => b.PublicId == request.BookingCancellationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"BookingCancellation {request.BookingCancellationPublicId} no encontrado.");

        // 3) Estado valido: solo BCs post-CAE pueden recibir allocations.
        var validStates = new[]
        {
            BookingCancellationStatus.AwaitingOperatorRefund,
            BookingCancellationStatus.ClientCreditApplied,
        };
        if (!validStates.Contains(bc.Status))
        {
            throw new BusinessInvariantViolationException(
                $"No se puede allocate refund sobre BC en {bc.Status}. " +
                "Requiere AwaitingOperatorRefund o ClientCreditApplied.",
                invariantCode: "INV-093");
        }

        // 4) Validar coherencia de moneda: el ingreso del operador y la moneda
        //    del FiscalSnapshot del BC tienen que coincidir. Si no, hay un error
        //    operativo (cargo el wrong refund) y no podemos hacer asientos.
        if (!string.Equals(
                refund.Currency,
                bc.FiscalSnapshot?.CurrencyAtEvent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessInvariantViolationException(
                $"La moneda del refund ({refund.Currency}) no coincide con la del " +
                $"FiscalSnapshot del BC ({bc.FiscalSnapshot?.CurrencyAtEvent ?? "<vacio>"}). " +
                "Revisa el ingreso correcto del operador.",
                invariantCode: "INV-118");
        }

        // 4-bis) INV-126: el operador del ingreso (refund.SupplierId) y el operador
        //        de la cancelacion (bc.SupplierId) tienen que ser el MISMO. Sin
        //        este check un cashier podria allocate por error un ingreso de
        //        Despegar contra una cancelacion cuyo operador era Avantrip,
        //        contaminando el cap del refund y los reportes contables por
        //        operador. La validacion vive aca (no en BD) porque es runtime
        //        cross-aggregate — un CHECK SQL acoplaria dos tablas.
        if (refund.SupplierId != bc.SupplierId)
        {
            throw new BusinessInvariantViolationException(
                "El proveedor del reintegro no coincide con el proveedor de la cancelacion.",
                invariantCode: "INV-126");
        }

        // 5) Validar matriz fiscal Mono/RI usando el FiscalSnapshot cristalizado.
        //    Hacemos PRIMERO la validacion porque rechaza antes de tocar BD:
        //    un usuario equivocado se entera con HTTP 409 sin pasar por retry.
        ValidateFiscalMatrix(
            agencyTaxConditionAtEvent: bc.FiscalSnapshot?.AgencyTaxConditionAtEvent,
            supplierTaxConditionAtEvent: bc.FiscalSnapshot?.SupplierTaxConditionAtEvent,
            deductions: request.Deductions);

        // 6) Calcular NetAmount + total deducciones. INV-112 (CHECK SQL) valida
        //    NetAmount >= 0 y GrossAmount >= NetAmount, pero validamos antes
        //    para devolver un mensaje claro.
        var totalDeductions = request.Deductions.Sum(d => d.Amount);
        var netAmount = ReservationEconomicPolicy.RoundCurrency(request.GrossAmount - totalDeductions);
        if (netAmount < 0m)
        {
            throw new BusinessInvariantViolationException(
                $"El monto neto quedaria negativo: gross={request.GrossAmount}, deducciones={totalDeductions}. " +
                "Las deducciones no pueden superar al gross.",
                invariantCode: "INV-112");
        }

        // 7) Crear la allocation. El CHECK SQL del cap se evalua al SaveChanges
        //    final (cuando refund.AllocatedAmount tenga el valor incrementado).
        var allocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            Refund = refund,
            BookingCancellationId = bc.Id,
            BookingCancellation = bc,
            GrossAmount = ReservationEconomicPolicy.RoundCurrency(request.GrossAmount),
            NetAmount = netAmount,
            IsVoided = false,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = userId,
        };

        // 8) Crear DeductionLines (1:N con la allocation).
        foreach (var dlReq in request.Deductions)
        {
            var line = new DeductionLine
            {
                Kind = dlReq.Kind,
                Amount = ReservationEconomicPolicy.RoundCurrency(dlReq.Amount),
                CertificateNumber = dlReq.CertificateNumber,
                CertificateDate = dlReq.CertificateDate,
                CertificatePdfUrl = dlReq.CertificatePdfUrl,
                Jurisdiction = dlReq.Jurisdiction,
                ForeignCountryCode = dlReq.ForeignCountryCode,
                Description = dlReq.Description,
                SupportingDocumentRef = dlReq.SupportingDocumentRef,
                JustificationComment = dlReq.JustificationComment,
                MissingFiscalSupport = dlReq.MissingFiscalSupport,
                Comment = dlReq.Comment,
                RequiresAccountingReview = dlReq.RequiresAccountingReview,
                Allocation = allocation,
            };
            allocation.Deductions.Add(line);
        }

        _db.OperatorRefundAllocations.Add(allocation);

        // 9) Incrementar el cap del refund con el NETO (NO el gross).
        //    Por que NetAmount y no GrossAmount: el contrato de
        //    OperatorRefundReceived.AllocatedAmount es "SUM(allocations.NetAmount
        //    WHERE NOT IsVoided)" (ver doc de la propiedad + ADR-002 §2.5 +
        //    plan tactico FC1.2 v3 §6.3 paso 4). Las deducciones del operador
        //    (penalidades, comisiones, retenciones que se queda el operador)
        //    NO entran a la caja de la agencia, por lo tanto NO consumen el cap
        //    del ingreso fisico. Si pusieramos GrossAmount el cap se consumiria
        //    de mas y la agencia "perderia" capacidad de asignar a otros BC.
        //    El CHECK SQL chk_OperatorRefundsReceived_allocated_not_exceeds
        //    valida AllocatedAmount <= ReceivedAmount al SaveChanges final.
        refund.AllocatedAmount += allocation.NetAmount;

        // 10) Actualizar el denormalizado del BC: ReceivedRefundAmount = SUM
        //     allocations.NetAmount activas. Lo incrementamos en memoria; un
        //     test invariante periodico (CancellationFlowE2E) lo reconcilia
        //     contra la suma real para detectar drift (HC del plan).
        bc.ReceivedRefundAmount += allocation.NetAmount;

        // 11) Notificar al BookingCancellationService que hubo allocation.
        //     Si era la primera activa, transiciona el BC a ClientCreditApplied.
        //     IMPORTANTE: el callback NO commitea (HC1) — solo modifica el Status
        //     en memoria. El SaveChanges unico viene al final.
        await _bcService.OnAllocationRecordedAsync(bc.Id, allocation.NetAmount, ct);

        // 12) Crear el ClientCreditEntry: el cliente recibe NetAmount como
        //     saldo a favor. RemainingBalance arranca = NetAmount; FC1.2.3
        //     gestionara retiros.
        //
        //     Guard defensivo: si las deducciones consumen el gross completo
        //     (netAmount == 0), no tiene sentido crear un entry con saldo 0.
        //     ClientCreditService.CreateEntryAsync tira ArgumentException
        //     internamente, pero validamos aca para fallar ANTES del Add() del
        //     entry — asi la transaccion envolvente no queda con cambios parciales
        //     (allocation + bc.Status modificado) que el rollback tiene que limpiar.
        //     Bajo el patron HC1 (no SaveChanges intermedio) el rollback se hace
        //     solo via ChangeTracker.Clear() del retry, pero un fail temprano es
        //     menos costoso y mas legible.
        if (allocation.NetAmount <= 0m)
        {
            throw new ArgumentException(
                "Las deducciones igualaron o superaron al GrossAmount: el cliente recibiria saldo cero. " +
                "Revisar los montos del refund o las deducciones.",
                nameof(request));
        }

        // Pasamos la entidad y NO el Id, porque al momento de esta llamada
        // allocation.Id == 0 (todavia no se persistio — HC1 plan v3: un solo
        // SaveChanges al final). EF8 resuelve la FK al hacer SaveChanges en
        // orden topologico: primero inserta la allocation (obtiene Id real), y
        // despues el entry con esa FK ya resuelta. Setear el Id en el entry
        // ahora dejaria 0 escrito en Postgres y rompe la FK.
        await _clientCreditService.CreateEntryAsync(
            bookingCancellationId: bc.Id,
            operatorRefundAllocation: allocation,
            customerId: bc.CustomerId,
            netAmount: allocation.NetAmount,
            currency: refund.Currency,
            userId: userId,
            userName: userName,
            ct: ct);

        // 13) Audit con metadata completa.
        //     EntityId: usamos el PublicId (Guid asignado en el constructor de
        //     la entidad, default = Guid.NewGuid()). NO podemos usar el Id int
        //     porque todavia es 0 hasta el SaveChanges final — el audit log se
        //     persiste antes (AuditService.LogBusinessEventAsync hace su propio
        //     SaveChanges interno). El PublicId es estable desde el momento del
        //     Add() y permite trazar la fila aun antes de que la BD le asigne Id.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundAllocated,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            entityId: allocation.PublicId.ToString(),
            details: JsonSerializer.Serialize(new
            {
                allocationPublicId = allocation.PublicId,
                refundPublicId = refund.PublicId,
                bcPublicId = bc.PublicId,
                allocation.GrossAmount,
                allocation.NetAmount,
                refundAllocatedAmountAfter = refund.AllocatedAmount,
                deductions = request.Deductions.Select(d => new
                {
                    d.Kind,
                    d.Amount,
                    d.Description,
                }).ToList(),
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 14) SaveChanges UNICO al final. EF persiste:
        //       - allocation (+ deductions cascade)
        //       - bc.Status update + bc.ReceivedRefundAmount
        //       - refund.AllocatedAmount update (xmin check)
        //       - client credit entry
        //       - manual cash movement (si aplicara — aca no, ya se hizo en
        //         RecordReceivedAsync)
        //       - audit log
        await _db.SaveChangesAsync(ct);

        return MapAllocation(allocation);
    }

    /// <summary>
    /// Valida la matriz Agencia × Operador × Deducciones segun el snapshot
    /// fiscal capturado al confirmar el BC. Las reglas vivien aca porque son
    /// runtime (no se pueden expresar como CHECK SQL sin acoplar dos tablas).
    /// </summary>
    private static void ValidateFiscalMatrix(
        string? agencyTaxConditionAtEvent,
        string? supplierTaxConditionAtEvent,
        IReadOnlyList<DeductionLineRequest> deductions)
    {
        var agencyCanonical = TaxConditionNormalizer.Normalize(agencyTaxConditionAtEvent);
        var supplierCanonical = TaxConditionNormalizer.Normalize(supplierTaxConditionAtEvent);

        // INV-118: si el snapshot quedo incoherente algo se rompio en Confirm.
        if (agencyCanonical == TaxConditionCanonical.Unknown ||
            supplierCanonical == TaxConditionCanonical.Unknown)
        {
            throw new BusinessInvariantViolationException(
                "FiscalSnapshot del BC incoherente (Agency o Supplier tax condition desconocidos). " +
                "Imposible aplicar matriz fiscal de retenciones.",
                invariantCode: "INV-118");
        }

        // Kinds 10..39 = retenciones impositivas AR (IVA + Ganancias + IIBB).
        // Forzamos rechazo cuando Agency o Supplier es Monotributo.
        foreach (var d in deductions)
        {
            var kindNumber = (int)d.Kind;
            var isArgentineWithholding = kindNumber >= 10 && kindNumber <= 39;

            if (!isArgentineWithholding) continue;

            if (supplierCanonical == TaxConditionCanonical.Monotributista)
            {
                // INV-105: operador Mono no esta inscripto en el regimen — registrar
                // una retencion suya seria invent un credito fiscal inexistente.
                throw new BusinessInvariantViolationException(
                    $"El operador es Monotributo: no se pueden registrar deducciones del tipo {d.Kind} (retencion AR). " +
                    "Usar AdministrativeFee/BankingCost/Other con justificacion.",
                    invariantCode: "INV-105");
            }

            if (agencyCanonical == TaxConditionCanonical.Monotributista)
            {
                // INV-115: agencia Mono NO genera credito fiscal IVA, asentar una
                // retencion seria "regalarsela" al operador sin contrapartida.
                throw new BusinessInvariantViolationException(
                    $"La agencia es Monotributo: no se pueden registrar deducciones del tipo {d.Kind} (retencion AR). " +
                    "El Monotributo no genera credito fiscal IVA.",
                    invariantCode: "INV-115");
            }
        }
    }

    // =========================================================================
    // VoidAllocationAsync
    // =========================================================================

    public async Task<OperatorRefundAllocationDto> VoidAllocationAsync(
        Guid allocationPublicId,
        VoidAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 20)
        {
            throw new ArgumentException("Reason debe tener al menos 20 chars.", nameof(request));
        }

        // Retry envolvente: si el cap-update del refund pelea con otra tx.
        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryVoidOnceAsync(allocationPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "VoidAllocationAsync concurrency conflict on attempt {Attempt}/{Max} for allocation {AllocationPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, allocationPublicId);

                _db.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries - 1)
                {
                    throw;
                }

                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("VoidAllocationAsync retry loop exhausted.");
    }

    private async Task<OperatorRefundAllocationDto> TryVoidOnceAsync(
        Guid allocationPublicId,
        VoidAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 1) Cargar allocation + refund + bc + entries + withdrawals
        //    (Include profundo porque validamos que NO se haya consumido el
        //    saldo del cliente antes de permitir void).
        var allocation = await _db.OperatorRefundAllocations
            .Include(a => a.Refund)
            .Include(a => a.BookingCancellation)
            .Include(a => a.Deductions)
            .FirstOrDefaultAsync(a => a.PublicId == allocationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"OperatorRefundAllocation {allocationPublicId} no encontrada.");

        // 2) Idempotencia + double-void: si ya esta voided rechazamos con 409.
        //    A diferencia del Force ARCA (que es no-op), aca el cliente esta
        //    intentando una operacion que no tiene efecto y conviene avisar.
        if (allocation.IsVoided)
        {
            throw new BusinessInvariantViolationException(
                "La allocation ya esta anulada. No se puede anular dos veces.",
                invariantCode: "INV-093");
        }

        // 3) Buscar ClientCreditEntry asociado (por OperatorRefundAllocationId).
        //    Si tiene withdrawals consumidos (kind != KeptAsCredit), rechazamos:
        //    no podemos "deshacer" plata que ya salio de caja sin un Reversal
        //    explicito (FC1.2.3).
        var creditEntry = await _db.ClientCreditEntries
            .Include(c => c.Withdrawals)
            .FirstOrDefaultAsync(c => c.OperatorRefundAllocationId == allocation.Id, ct);

        if (creditEntry != null)
        {
            var hasConsumedWithdrawals = creditEntry.Withdrawals
                .Any(w => w.Kind != WithdrawalKind.KeptAsCredit);
            if (hasConsumedWithdrawals)
            {
                throw new InvalidOperationException(
                    "La allocation tiene retiros consumidos por el cliente. " +
                    "Iniciar un ClientRefundReversal antes de anular la allocation.");
            }

            // El entry queda con balance cero + IsFullyConsumed = true (asi no
            // aparece como saldo activo). NO eliminamos la fila: audit trail.
            creditEntry.RemainingBalance = 0m;
            creditEntry.IsFullyConsumed = true;
        }

        // 4) Marcar soft-void + metadata humana del actor.
        allocation.IsVoided = true;
        allocation.VoidedAt = DateTime.UtcNow;
        allocation.VoidedByUserId = userId;
        allocation.VoidedReason = request.Reason.Trim();

        // 5) Liberar el cap del refund con el NETO (NO el gross).
        //    Espejo de paso 9 de AllocateAsync: el cap se incrementa con
        //    NetAmount, asi que tambien se libera con NetAmount. Si decrementaramos
        //    con GrossAmount, el AllocatedAmount podria quedar negativo (rompiendo
        //    el CHECK SQL: AllocatedAmount >= 0) cuando la allocation tenia
        //    deducciones.
        allocation.Refund.AllocatedAmount -= allocation.NetAmount;

        // 6) Ajustar el denormalizado del BC.
        allocation.BookingCancellation.ReceivedRefundAmount -= allocation.NetAmount;

        // 7) Notificar al BC service para que evalue revertir el Status si
        //    no quedan mas allocations activas.
        await _bcService.OnAllocationVoidedAsync(
            allocation.BookingCancellationId,
            allocation.NetAmount,
            ct);

        // 8) Audit con metadata previa al cambio (capturada en serialize-time).
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundAllocationVoided,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            entityId: allocation.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                allocationPublicId = allocation.PublicId,
                refundPublicId = allocation.Refund.PublicId,
                bcPublicId = allocation.BookingCancellation.PublicId,
                allocation.GrossAmount,
                allocation.NetAmount,
                reason = allocation.VoidedReason,
                voidedByUserId = userId,
                refundAllocatedAmountAfter = allocation.Refund.AllocatedAmount,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        // 9) SaveChanges unico.
        await _db.SaveChangesAsync(ct);

        return MapAllocation(allocation);
    }

    // =========================================================================
    // ReassociateAllocationAsync
    // =========================================================================

    public async Task<OperatorRefundAllocationDto> ReassociateAllocationAsync(
        Guid allocationPublicId,
        ReassociateAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        await EnsureFeatureFlagOnAsync(ct);

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 20)
        {
            throw new ArgumentException("Reason debe tener al menos 20 chars.", nameof(request));
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            try
            {
                return await TryReassociateOnceAsync(allocationPublicId, request, userId, userName, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex,
                    "ReassociateAllocationAsync concurrency conflict on attempt {Attempt}/{Max} for allocation {AllocationPublicId}. Retrying.",
                    attempt + 1, MaxConcurrencyRetries, allocationPublicId);

                _db.ChangeTracker.Clear();

                if (attempt == MaxConcurrencyRetries - 1)
                {
                    throw;
                }

                await Task.Delay((int)Math.Pow(4, attempt) * 100, ct);
            }
        }

        throw new InvalidOperationException("ReassociateAllocationAsync retry loop exhausted.");
    }

    private async Task<OperatorRefundAllocationDto> TryReassociateOnceAsync(
        Guid allocationPublicId,
        ReassociateAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct)
    {
        // 1) Cargar allocation vieja + refund + bc viejo.
        var oldAllocation = await _db.OperatorRefundAllocations
            .Include(a => a.Refund).ThenInclude(r => r.Supplier)
            .Include(a => a.BookingCancellation).ThenInclude(b => b.Reserva)
            .Include(a => a.Deductions)
            .FirstOrDefaultAsync(a => a.PublicId == allocationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"OperatorRefundAllocation {allocationPublicId} no encontrada.");

        if (oldAllocation.IsVoided)
        {
            throw new BusinessInvariantViolationException(
                "La allocation ya esta anulada. No se puede reasociar.",
                invariantCode: "INV-093");
        }

        // 2) Validar que el ClientCreditEntry asociado no tenga withdrawals
        //    consumidos (mismo guard que VoidAllocation: no se puede mover
        //    plata que ya salio de caja).
        var oldCreditEntry = await _db.ClientCreditEntries
            .Include(c => c.Withdrawals)
            .FirstOrDefaultAsync(c => c.OperatorRefundAllocationId == oldAllocation.Id, ct);
        if (oldCreditEntry != null)
        {
            var hasConsumed = oldCreditEntry.Withdrawals.Any(w => w.Kind != WithdrawalKind.KeptAsCredit);
            if (hasConsumed)
            {
                throw new InvalidOperationException(
                    "La allocation tiene retiros consumidos por el cliente. " +
                    "No se puede reasociar — iniciar un ClientRefundReversal primero.");
            }
        }

        // 3) Cargar BC destino con su FiscalSnapshot completo.
        var newBc = await _db.BookingCancellations
            .Include(b => b.Reserva)
            .FirstOrDefaultAsync(b => b.PublicId == request.NewBookingCancellationPublicId, ct)
            ?? throw new KeyNotFoundException(
                $"BookingCancellation destino {request.NewBookingCancellationPublicId} no encontrado.");

        var validStates = new[]
        {
            BookingCancellationStatus.AwaitingOperatorRefund,
            BookingCancellationStatus.ClientCreditApplied,
        };
        if (!validStates.Contains(newBc.Status))
        {
            throw new BusinessInvariantViolationException(
                $"No se puede reasociar a BC destino en {newBc.Status}. " +
                "Requiere AwaitingOperatorRefund o ClientCreditApplied.",
                invariantCode: "INV-093");
        }

        // 4) Validar moneda del refund con el snapshot del BC nuevo (puede ser
        //    distinto al viejo BC si vienen de FiscalSnapshots diferentes).
        if (!string.Equals(
                oldAllocation.Refund.Currency,
                newBc.FiscalSnapshot?.CurrencyAtEvent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessInvariantViolationException(
                $"La moneda del refund ({oldAllocation.Refund.Currency}) no coincide con la del " +
                $"BC destino ({newBc.FiscalSnapshot?.CurrencyAtEvent ?? "<vacio>"}).",
                invariantCode: "INV-118");
        }

        // 5) Recomputar matriz fiscal con el snapshot del BC destino.
        //    Las deducciones existentes podrian no ser validas en el nuevo
        //    contexto (Agency/Supplier distintos). Las re-mapeamos desde la
        //    allocation original.
        var deductionsAsRequest = oldAllocation.Deductions
            .Select(d => new DeductionLineRequest(
                Kind: d.Kind,
                Amount: d.Amount,
                Description: d.Description,
                CertificateNumber: d.CertificateNumber,
                CertificateDate: d.CertificateDate,
                CertificatePdfUrl: d.CertificatePdfUrl,
                Jurisdiction: d.Jurisdiction,
                ForeignCountryCode: d.ForeignCountryCode,
                SupportingDocumentRef: d.SupportingDocumentRef,
                JustificationComment: d.JustificationComment,
                MissingFiscalSupport: d.MissingFiscalSupport,
                Comment: d.Comment,
                RequiresAccountingReview: d.RequiresAccountingReview))
            .ToList();
        ValidateFiscalMatrix(
            agencyTaxConditionAtEvent: newBc.FiscalSnapshot?.AgencyTaxConditionAtEvent,
            supplierTaxConditionAtEvent: newBc.FiscalSnapshot?.SupplierTaxConditionAtEvent,
            deductions: deductionsAsRequest);

        // 6) Soft-void de la vieja + ajustes contables del BC viejo y refund.
        var oldBcId = oldAllocation.BookingCancellationId;
        oldAllocation.IsVoided = true;
        oldAllocation.VoidedAt = DateTime.UtcNow;
        oldAllocation.VoidedByUserId = userId;
        oldAllocation.VoidedReason = $"Reassociate: {request.Reason.Trim()}";
        // Liberar cap del refund con NetAmount (espejo de AllocateAsync).
        // Ver explicacion en paso 9 de TryAllocateOnceAsync: el cap acumula netos,
        // por lo tanto se libera con neto.
        oldAllocation.Refund.AllocatedAmount -= oldAllocation.NetAmount;
        oldAllocation.BookingCancellation.ReceivedRefundAmount -= oldAllocation.NetAmount;

        if (oldCreditEntry != null)
        {
            oldCreditEntry.RemainingBalance = 0m;
            oldCreditEntry.IsFullyConsumed = true;
        }

        await _bcService.OnAllocationVoidedAsync(oldBcId, oldAllocation.NetAmount, ct);

        // 7) Crear allocation nueva apuntando al BC nuevo. Misma estructura
        //    economica que la vieja, snapshot fiscal recalculado del destino.
        var newAllocation = new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = oldAllocation.OperatorRefundReceivedId,
            Refund = oldAllocation.Refund,
            BookingCancellationId = newBc.Id,
            BookingCancellation = newBc,
            GrossAmount = oldAllocation.GrossAmount,
            NetAmount = oldAllocation.NetAmount,
            IsVoided = false,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = userId,
            VoidsAllocationId = oldAllocation.Id, // FK retro: trazabilidad
        };

        foreach (var d in oldAllocation.Deductions)
        {
            newAllocation.Deductions.Add(new DeductionLine
            {
                Kind = d.Kind,
                Amount = d.Amount,
                CertificateNumber = d.CertificateNumber,
                CertificateDate = d.CertificateDate,
                CertificatePdfUrl = d.CertificatePdfUrl,
                Jurisdiction = d.Jurisdiction,
                ForeignCountryCode = d.ForeignCountryCode,
                Description = d.Description,
                SupportingDocumentRef = d.SupportingDocumentRef,
                JustificationComment = d.JustificationComment,
                MissingFiscalSupport = d.MissingFiscalSupport,
                Comment = d.Comment,
                RequiresAccountingReview = d.RequiresAccountingReview,
                Allocation = newAllocation,
            });
        }

        _db.OperatorRefundAllocations.Add(newAllocation);
        // Incrementar cap del refund con NetAmount de la NUEVA allocation
        // (mismo principio que AllocateAsync paso 9: cap acumula netos).
        oldAllocation.Refund.AllocatedAmount += newAllocation.NetAmount;
        newBc.ReceivedRefundAmount += newAllocation.NetAmount;

        // 8) Crear ClientCreditEntry nuevo para el cliente del BC nuevo.
        //    Pasamos la entidad y no el Id por el mismo motivo que en AllocateAsync:
        //    newAllocation.Id == 0 hasta el SaveChanges final, asi que setear el
        //    Id ahora rompe la FK. La navigation property hace que EF resuelva la
        //    FK al persistir en orden topologico.
        await _clientCreditService.CreateEntryAsync(
            bookingCancellationId: newBc.Id,
            operatorRefundAllocation: newAllocation,
            customerId: newBc.CustomerId,
            netAmount: newAllocation.NetAmount,
            currency: oldAllocation.Refund.Currency,
            userId: userId,
            userName: userName,
            ct: ct);

        // 9) Notificar al BC service nuevo.
        await _bcService.OnAllocationRecordedAsync(newBc.Id, newAllocation.NetAmount, ct);

        // 10) Audit UNICO "Reassociated" (no doble Void+Allocated) para que la
        //     historia se lea como evento atomico.
        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OperatorRefundAllocationReassociated,
            entityName: AuditActions.OperatorRefundAllocationEntityName,
            entityId: oldAllocation.Id.ToString(),
            details: JsonSerializer.Serialize(new
            {
                oldAllocationPublicId = oldAllocation.PublicId,
                newAllocationPublicId = newAllocation.PublicId,
                refundPublicId = oldAllocation.Refund.PublicId,
                fromBcId = oldBcId,
                fromBcPublicId = oldAllocation.BookingCancellation.PublicId,
                toBcId = newBc.Id,
                toBcPublicId = newBc.PublicId,
                netAmount = newAllocation.NetAmount,
                grossAmount = newAllocation.GrossAmount,
                reason = request.Reason.Trim(),
                actorUserId = userId,
            }),
            userId: userId,
            userName: userName,
            ct: ct);

        await _db.SaveChangesAsync(ct);

        return MapAllocation(newAllocation);
    }

    // =========================================================================
    // Queries
    // =========================================================================

    public async Task<OperatorRefundReceivedDto?> GetByPublicIdAsync(
        Guid publicId,
        CancellationToken ct)
    {
        var refund = await _db.OperatorRefundReceived
            .AsNoTracking()
            .Include(r => r.Supplier)
            .Include(r => r.Allocations).ThenInclude(a => a.BookingCancellation)
            .Include(r => r.Allocations).ThenInclude(a => a.Deductions)
            .FirstOrDefaultAsync(r => r.PublicId == publicId, ct);

        return refund is null ? null : MapRefund(refund);
    }

    // =========================================================================
    // Helpers privados
    // =========================================================================

    private async Task EnsureFeatureFlagOnAsync(CancellationToken ct)
    {
        var settings = await _settings.GetEntityAsync(ct);
        if (!settings.EnableNewCancellationFlow)
        {
            throw new InvalidOperationException(
                "El modulo de cancelacion/refund no esta habilitado en este ambiente " +
                "(EnableNewCancellationFlow=false).");
        }
    }

    private static OperatorRefundReceivedDto MapRefund(OperatorRefundReceived refund)
    {
        return new OperatorRefundReceivedDto
        {
            PublicId = refund.PublicId,
            SupplierPublicId = refund.Supplier?.PublicId ?? Guid.Empty,
            SupplierName = refund.Supplier?.Name ?? string.Empty,
            ReceivedAmount = refund.ReceivedAmount,
            AllocatedAmount = refund.AllocatedAmount,
            RemainingCap = refund.ReceivedAmount - refund.AllocatedAmount,
            Currency = refund.Currency,
            ReceivedAt = refund.ReceivedAt,
            Method = refund.Method,
            Reference = refund.Reference,
            ReceivedByUserId = refund.ReceivedByUserId,
            ReceivedByUserName = refund.ReceivedByUserName,
            Allocations = refund.Allocations?
                .Select(a => MapAllocation(a, refundPublicIdFallback: refund.PublicId))
                .ToList() ?? new List<OperatorRefundAllocationDto>(),
        };
    }

    private static OperatorRefundAllocationDto MapAllocation(
        OperatorRefundAllocation a,
        Guid? refundPublicIdFallback = null)
    {
        return new OperatorRefundAllocationDto
        {
            PublicId = a.PublicId,
            RefundPublicId = a.Refund?.PublicId ?? refundPublicIdFallback ?? Guid.Empty,
            BookingCancellationPublicId = a.BookingCancellation?.PublicId ?? Guid.Empty,
            GrossAmount = a.GrossAmount,
            NetAmount = a.NetAmount,
            IsVoided = a.IsVoided,
            VoidedAt = a.VoidedAt,
            VoidedByUserId = a.VoidedByUserId,
            VoidedReason = a.VoidedReason,
            CreatedAt = a.CreatedAt,
            CreatedByUserId = a.CreatedByUserId,
            Deductions = a.Deductions?.Select(MapDeduction).ToList() ?? new List<DeductionLineDto>(),
        };
    }

    private static DeductionLineDto MapDeduction(DeductionLine d)
    {
        return new DeductionLineDto
        {
            PublicId = d.PublicId,
            Kind = d.Kind,
            Amount = d.Amount,
            Description = d.Description,
            CertificateNumber = d.CertificateNumber,
            CertificateDate = d.CertificateDate,
            CertificatePdfUrl = d.CertificatePdfUrl,
            Jurisdiction = d.Jurisdiction,
            ForeignCountryCode = d.ForeignCountryCode,
            SupportingDocumentRef = d.SupportingDocumentRef,
            JustificationComment = d.JustificationComment,
            MissingFiscalSupport = d.MissingFiscalSupport,
            Comment = d.Comment,
            RequiresAccountingReview = d.RequiresAccountingReview,
        };
    }
}
