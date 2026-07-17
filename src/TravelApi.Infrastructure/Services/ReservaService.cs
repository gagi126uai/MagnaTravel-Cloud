using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Linq;
using System.Security.Claims;
using TravelApi.Application.Constants;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services.Reservations;
using TravelApi.Infrastructure.Time;

namespace TravelApi.Infrastructure.Services;

public class ReservaService : IReservaService
{
    private readonly AppDbContext _context;
    private readonly IMapper _mapper;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ReservaService> _logger;
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    // ADR-020 F3: motor de estados automatico. Opcional (default null) para no romper los tests
    // unitarios que construyen ReservaService a mano; en runtime lo inyecta DI.
    private readonly ReservaAutoStateService? _autoStateService;
    // ADR-031 v2.1: auditoria del alta/baja de asignaciones pasajero<->servicio (determinan el SET del
    // servicio: a quien se le exige nombre/documento y quien aparece en el voucher). Opcional para no
    // romper los ctores de tests; si es null, la operacion funciona igual, solo no se registra el evento.
    private readonly IAuditService? _auditService;
    // H3 (2026-06-24): para saber si la reserva tiene una multa del operador pendiente de confirmar y asi alimentar
    // la capacidad CanConfirmOperatorPenalty. Opcional (default null) para no romper los ctores de tests unitarios;
    // si es null (modo test sin cancelaciones), la capacidad queda en false (el boton no se ofrece, comportamiento
    // seguro). En runtime lo inyecta DI. No hay ciclo: BookingCancellationService NO depende de IReservaService.
    private readonly IBookingCancellationService? _cancellationService;

    /// <summary>
    /// cbteTipo de las Notas de Credito de AFIP (3=A, 8=B, 13=C, 53=M). Se usa para
    /// EXCLUIR las NC del guard fiscal de cancelacion: una NC no es una "factura viva".
    ///
    /// Espejo del mismo conjunto en <c>MutationGuards.LiveInvoiceCreditNoteTypes</c> y
    /// en <c>InvoiceComprobanteHelpers.IsCreditNote</c>. Se replica inline porque EF Core
    /// no traduce el helper a SQL. Mantener los tres sincronizados si cambia la lista.
    /// </summary>
    private static readonly int[] CreditNoteComprobanteTypes = { 3, 8, 13, 53 };

    public ReservaService(
        AppDbContext context,
        IMapper mapper,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        UserManager<ApplicationUser> userManager,
        ILogger<ReservaService> logger,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        ReservaAutoStateService? autoStateService = null,
        IAuditService? auditService = null,
        IBookingCancellationService? cancellationService = null)
    {
        _context = context;
        _mapper = mapper;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _userManager = userManager;
        _logger = logger;
        // B1.15 Fase 2a: estos dos son opcionales para no romper tests unitarios
        // que instancian ReservaService directamente con el ctor de 5 args.
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _autoStateService = autoStateService;
        _auditService = auditService;
        _cancellationService = cancellationService;
    }

    /// <summary>
    /// ADR-031 v2.1: resuelve el actor (userId, userName) del HttpContext para la auditoria de
    /// asignaciones. En tests sin HttpContext devuelve (null, null) — el evento, si se loguea, queda
    /// con autor desconocido (aceptable, no rompe).
    /// </summary>
    private (string? userId, string? userName) ResolveAuditActor()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        if (user is null) return (null, null);
        var userId = user.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        var userName = user.FindFirstValue(System.Security.Claims.ClaimTypes.Name) ?? user.Identity?.Name;
        return (string.IsNullOrWhiteSpace(userId) ? null : userId,
                string.IsNullOrWhiteSpace(userName) ? null : userName);
    }

    /// <summary>
    /// B1.15 Fase 2a: id del usuario actual desde el HttpContext, o null si no
    /// hay HttpContext (tests unitarios). Centralizado para los chequeos de
    /// view_all/cobranzas.see_cost/cancel_with_payment/etc.
    /// </summary>
    private string? GetCurrentUserIdOrNull()
        => _httpContextAccessor?.HttpContext?.User?.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);

    /// <summary>
    /// B1.15 Fase 2a: chequea un permiso para el user actual. Devuelve false si
    /// no hay user resoluble o no hay resolver inyectado (modo test).
    /// </summary>
    private async Task<bool> CurrentUserHasPermissionAsync(string permission, CancellationToken ct)
    {
        if (_permissionResolver is null) return false;
        var userId = GetCurrentUserIdOrNull();
        if (string.IsNullOrEmpty(userId)) return false;
        var perms = await _permissionResolver.GetPermissionsAsync(userId, ct);
        return perms.Contains(permission);
    }

    /// <summary>
    /// B1.15 Fase 2a: chequea un permiso para un user explicito. Util cuando el
    /// controller pasa el actor por parametro (ej: UpdateStatusAsync con
    /// validacion de cancel/cancel_with_payment).
    /// </summary>
    private async Task<bool> UserHasPermissionAsync(string? userId, string permission, CancellationToken ct)
    {
        if (_permissionResolver is null || string.IsNullOrEmpty(userId)) return false;
        var perms = await _permissionResolver.GetPermissionsAsync(userId, ct);
        return perms.Contains(permission);
    }

    public async Task<ReservaDto> GetReservaByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
        return await GetReservaByIdAsync(id);
    }

    /// <summary>
    /// "Estado de Cuenta" de la reserva como LIBRO MAYOR (extracto estilo banco). Read-model DERIVADO: arma
    /// una linea por cada comprobante/cobro VIVO, calcula el saldo corriente por moneda y lo devuelve.
    ///
    /// <para>La cuenta (orden, signos, saldo corriente) vive en <see cref="ReservaAccountStatementBuilder"/>
    /// (puro, probado sin EF). Aca solo cargamos los mismos Includes de comprobantes+cobros que usa el
    /// detalle y traducimos cada entidad VIVA a una linea plana, reusando los clasificadores canonicos.</para>
    ///
    /// <para><b>SEGURIDAD</b>: el extracto es venta/cobranza PURA. Ningun campo de costo ni margen entra en
    /// las lineas, por eso este metodo NO llama a <c>ApplyCostMaskingAsync</c>: no hay nada que enmascarar.</para>
    /// </summary>
    public async Task<ReservaAccountStatementDto> GetAccountStatementAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);

        // Mismos Includes que el detalle para Invoices+Payments (con el Receipt para el nº de recibo). AsNoTracking:
        // es solo lectura. No cargamos servicios/pasajeros: el extracto solo mira comprobantes y cobros.
        var file = await _context.Reservas
            .AsNoTracking()
            .Include(f => f.Invoices)
            .Include(f => f.Payments).ThenInclude(p => p.Receipt)
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (file == null)
        {
            throw new KeyNotFoundException($"File with ID {id} not found locally");
        }

        var inputLines = new List<AccountStatementInputLine>();
        AddInvoiceLines(file, inputLines);
        AddPaymentLines(file, inputLines);

        var statement = ReservaAccountStatementBuilder.Build(inputLines);

        return new ReservaAccountStatementDto
        {
            ReservaPublicId = file.PublicId,
            Currencies = statement.Currencies
                .Select(block => new AccountStatementCurrencyBlockDto
                {
                    Currency = block.Currency,
                    ClosingBalance = block.ClosingBalance,
                    Lines = block.Lines
                        .Select(line => new AccountStatementLineDto
                        {
                            Date = line.Date,
                            Kind = line.Kind,
                            Description = line.Description,
                            DocumentRef = line.DocumentRef,
                            Currency = line.Currency,
                            Charge = line.Charge,
                            Credit = line.Credit,
                            RunningBalance = line.RunningBalance,
                            SourcePublicId = line.SourcePublicId,
                        })
                        .ToList(),
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Traduce las facturas/ND/NC de la reserva a lineas del extracto. Cuenta todo comprobante con CAE
    /// aprobado, AUNQUE este anulado (misma regla unica que el cuadre: <c>CountsInNetBilled</c>). Factura/ND
    /// son CARGO (suman la deuda); NC es ABONO (la resta). Tipo desconocido (dato sucio) se omite.
    ///
    /// <para>FIX doble conteo (extracto): la factura ANULADA (AnnulmentStatus=Succeeded) DEBE seguir
    /// mostrandose como cargo, y su Nota de Credito como abono. Si saltaramos la factura Succeeded (lo que
    /// hacia antes), el extracto mostraba solo la NC suelta (un abono sin su cargo) y el saldo corriente del
    /// libro mayor quedaba incoherente. Mostrando AMBAS lineas, el saldo cierra: factura 80k cargo, NC 80k
    /// abono -> saldo de esa anulacion = 0.</para>
    ///
    /// <para>La fecha de cada linea es la de EMISION FISCAL (<c>Invoice.IssuedAt</c>, la fecha que ARCA aprobo
    /// el CAE), con fallback a <c>CreatedAt</c> para comprobantes legacy sin IssuedAt: asi el orden cronologico
    /// del libro mayor respeta la fecha del comprobante AFIP y no el timestamp de fila.</para>
    /// </summary>
    private static void AddInvoiceLines(Reserva file, List<AccountStatementInputLine> lines)
    {
        if (file.Invoices == null) return;

        foreach (var invoice in file.Invoices)
        {
            // Regla unica del cuadre: cuenta si el CAE esta aprobado (aunque la factura este anulada). La
            // anulacion la refleja su Nota de Credito como abono; no se omite la factura para no descuadrar.
            if (!ReservaInvoicingCuadreCalculator.CountsInNetBilled(invoice.Resultado)) continue;

            var category = InvoiceComprobanteHelpers.Categorize(invoice.TipoComprobante);

            // Moneda del comprobante: Invoice.MonId viene en codigo ARCA ("PES"/"DOL"); el extracto agrupa por
            // ISO ("ARS"/"USD"). Hoy todo se factura en pesos, pero dejamos la traduccion correcta. Si el codigo
            // no se reconoce (dato legacy raro), cae a ARS (regla legacy de Monedas.Normalizar).
            string currency = Domain.Helpers.ArcaCurrencyMapper.ToIso(invoice.MonId) ?? Monedas.ARS;

            // Referencia legible del comprobante: "PuntoDeVenta-NumeroComprobante" (ej. "0001-00000123").
            string documentRef = $"{invoice.PuntoDeVenta:D4}-{invoice.NumeroComprobante:D8}";

            // Fecha del movimiento en el extracto = fecha de EMISION FISCAL del comprobante, no el timestamp
            // de fila. Invoice.IssuedAt es la fecha que ARCA aprobo el CAE (en la reconciliacion se parsea del
            // nodo <CbteFch> del comprobante AFIP), asi que es la fecha correcta para ORDENAR el libro mayor.
            // Fallback a CreatedAt para comprobantes legacy/historicos que se backfillearon sin IssuedAt (la
            // columna es nullable): preferimos la fiscal cuando existe y degradamos al timestamp de fila si no.
            DateTime movementDate = invoice.IssuedAt ?? invoice.CreatedAt;

            switch (category)
            {
                case InvoiceComprobanteCategory.Invoice:
                    lines.Add(new AccountStatementInputLine(
                        Date: movementDate,
                        Kind: AccountStatementLineKinds.Invoice,
                        Description: "Factura",
                        DocumentRef: documentRef,
                        Currency: currency,
                        Charge: invoice.ImporteTotal,
                        Credit: 0m,
                        // Origen = la factura: el front cruza con invoices[] para abrir su PDF.
                        SourcePublicId: invoice.PublicId));
                    break;

                case InvoiceComprobanteCategory.DebitNote:
                    lines.Add(new AccountStatementInputLine(
                        Date: movementDate,
                        Kind: AccountStatementLineKinds.DebitNote,
                        Description: "Nota de débito",
                        DocumentRef: documentRef,
                        Currency: currency,
                        Charge: invoice.ImporteTotal,
                        Credit: 0m,
                        // Origen = el comprobante (ND): mismo PublicId de Invoice.
                        SourcePublicId: invoice.PublicId));
                    break;

                case InvoiceComprobanteCategory.CreditNote:
                    lines.Add(new AccountStatementInputLine(
                        Date: movementDate,
                        Kind: AccountStatementLineKinds.CreditNote,
                        Description: "Nota de crédito",
                        DocumentRef: documentRef,
                        Currency: currency,
                        Charge: 0m,
                        Credit: invoice.ImporteTotal,
                        // Origen = el comprobante (NC): mismo PublicId de Invoice.
                        SourcePublicId: invoice.PublicId));
                    break;

                default:
                    // Tipo desconocido (dato sucio): no lo mostramos, igual que el cuadre no lo cuenta.
                    break;
            }
        }
    }

    /// <summary>
    /// ADR-037 / cuadre POR MONEDA (2026-06-22): carga <c>FacturadoNeto</c> y <c>DisponibleParaFacturar</c>
    /// en cada linea de <paramref name="porMoneda"/>. El facturado neto de cada moneda sale del calculator
    /// (facturas + ND - NC vivas, agrupadas por la moneda ISO del comprobante); el "falta facturar" se arma
    /// con la VENTA de esa misma moneda (TotalSale), mismo criterio que el escalar.
    ///
    /// <para>Bordes: una moneda con venta y sin facturas queda en FacturadoNeto 0 y DisponibleParaFacturar =
    /// su venta. Una factura en una moneda que NO tiene venta vendida en la reserva (cruce/sobrefacturacion
    /// raro) no tiene linea en PorMoneda — su facturado quedaria invisible. Para no esconderlo, esos casos se
    /// loggean como advertencia operativa (sin montos sensibles): es un dato que el contador querria ver.</para>
    /// </summary>
    private void PopulateFacturadoPorMoneda(Reserva file, List<ReservaMoneyLineDto> porMoneda)
    {
        if (file.Invoices == null || file.Invoices.Count == 0)
        {
            // Sin comprobantes: cada moneda queda facturado 0 y falta = su venta. El default del DTO ya es 0,
            // pero seteamos explicito el DisponibleParaFacturar para que coincida con la venta de la moneda.
            foreach (var line in porMoneda)
            {
                line.FacturadoNeto = 0m;
                line.DisponibleParaFacturar = line.TotalSale;
            }
            return;
        }

        // Armamos las lineas del calculator desde las facturas, traduciendo MonId (ARCA "PES"/"DOL") a ISO
        // ("ARS"/"USD"); MonId vacio o no reconocido -> ARS (regla legacy, mismo criterio que el extracto).
        var invoiceLines = file.Invoices.Select(invoice => new CuadreInvoiceLineByCurrency(
            Currency: Domain.Helpers.ArcaCurrencyMapper.ToIso(invoice.MonId) ?? Monedas.ARS,
            TipoComprobante: invoice.TipoComprobante,
            ImporteTotal: invoice.ImporteTotal,
            // Regla unica: cuenta el CAE aprobado aunque este anulado; la NC hace la resta (sin doble conteo).
            IsLive: ReservaInvoicingCuadreCalculator.CountsInNetBilled(invoice.Resultado)));

        var facturadoPorMoneda = ReservaInvoicingCuadreCalculator.CalculatePerCurrency(invoiceLines);

        // Cada moneda con venta toma su facturado (0 si no hay facturas en esa moneda) y calcula su falta.
        foreach (var line in porMoneda)
        {
            decimal facturado = facturadoPorMoneda.TryGetValue(line.Currency, out var neto) ? neto : 0m;
            line.FacturadoNeto = facturado;
            line.DisponibleParaFacturar = line.TotalSale - facturado;
        }

        // Borde raro: una moneda que SOLO aparece en facturas (sin venta vendida en esa moneda) no tiene
        // linea en PorMoneda, asi que su facturado no se mostraria. No inventamos una linea de venta; lo
        // dejamos registrado para que se note (es un cruce/sobrefacturacion que el contador deberia revisar).
        var monedasConVenta = porMoneda.Select(line => line.Currency).ToHashSet(System.StringComparer.Ordinal);
        foreach (var (currency, facturado) in facturadoPorMoneda)
        {
            if (facturado != 0m && !monedasConVenta.Contains(currency))
            {
                _logger.LogWarning(
                    "Reserva {ReservaId}: factura neta en moneda {Currency} sin venta en esa moneda (posible cruce o sobrefacturacion). No se expone en PorMoneda.",
                    file.Id, currency);
            }
        }
    }

    /// <summary>
    /// Traduce los cobros VIVOS de la reserva a lineas del extracto (siempre ABONO: bajan la deuda). VIVO =
    /// no cancelado y no soft-deleted (mismo filtro que <see cref="ReservaMoneyCalculator"/>).
    ///
    /// <para>El abono cae en la moneda a la que el pago se IMPUTA (<c>ImputedCurrency ?? Currency</c>) por su
    /// monto imputado (<c>ImputedAmount ?? Amount</c>), exactamente como lo computa ReservaMoneyCalculator —
    /// asi el saldo de cierre del bloque cuadra con PorMoneda[moneda].Balance. Si el cobro fue CRUZADO (entro
    /// en otra moneda), la descripcion lo aclara para que el extracto no confunda.</para>
    ///
    /// <para>Incluye los cobros PUENTE (AffectsCash=false: sobrepago a saldo a favor / saldo a favor aplicado):
    /// no movieron caja pero BAJAN la deuda, asi que el extracto DEBE mostrarlos o no cuadraria con el Balance.
    /// La descripcion los marca como "Saldo a favor aplicado".</para>
    /// </summary>
    private static void AddPaymentLines(Reserva file, List<AccountStatementInputLine> lines)
    {
        if (file.Payments == null) return;

        foreach (var payment in file.Payments)
        {
            // Mismo filtro de "pago vivo" del calculator: ni cancelado ni borrado.
            bool isLive = payment.Status != "Cancelled" && !payment.IsDeleted;
            if (!isLive) continue;

            // Moneda y monto imputados (lo que efectivamente baja del saldo de esa moneda). Coincide con la
            // forma en que ReservaMoneyCalculator imputa los pagos, para que los saldos cuadren.
            string imputedCurrency = Monedas.Normalizar(payment.ImputedCurrency ?? payment.Currency);
            decimal imputedAmount = payment.ImputedAmount ?? payment.Amount;

            string description = BuildPaymentDescription(payment, imputedCurrency);

            lines.Add(new AccountStatementInputLine(
                Date: payment.PaidAt,
                Kind: AccountStatementLineKinds.Payment,
                Description: description,
                DocumentRef: payment.Receipt?.ReceiptNumber,
                Currency: imputedCurrency,
                Charge: 0m,
                Credit: imputedAmount,
                // Origen = el cobro: el front cruza con payments[] para ver/emitir/anular su recibo.
                SourcePublicId: payment.PublicId));
        }
    }

    /// <summary>
    /// Arma el texto legible de un cobro para el extracto: nº de recibo si existe, si no el metodo. Para un
    /// cobro PUENTE (no movio caja) dice "Saldo a favor aplicado". Para un cobro CRUZADO (entro en otra moneda)
    /// aclara la moneda real que se recibio, para que no parezca que entro en la moneda del saldo.
    /// </summary>
    private static string BuildPaymentDescription(Payment payment, string imputedCurrency)
    {
        // Puente: no movio caja (sobrepago a saldo a favor / saldo a favor aplicado a esta reserva).
        if (!payment.AffectsCash)
        {
            return "Saldo a favor aplicado";
        }

        // Base: nº de recibo si lo hay; si no, el metodo de cobro (Efectivo/Transferencia/...).
        string baseText = !string.IsNullOrWhiteSpace(payment.Receipt?.ReceiptNumber)
            ? $"Cobro recibo {payment.Receipt!.ReceiptNumber}"
            : $"Cobro ({payment.Method})";

        // Cobro CRUZADO: entro en una moneda y se imputo a otra. Aclaramos la moneda REAL que se recibio
        // (Amount + Currency) para que la linea no confunda (el monto que se muestra es el imputado).
        string realCurrency = Monedas.Normalizar(payment.Currency);
        if (!string.Equals(realCurrency, imputedCurrency, StringComparison.Ordinal))
        {
            return $"{baseText} — recibido en {realCurrency}";
        }

        return baseText;
    }

    public async Task<ReservaDto> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId, CancellationToken cancellationToken)
    {
        var reserva = await CreateReservaAsync(request, createdByUserId);
        return await GetReservaByIdAsync(reserva.Id);
    }

    public async Task<ReservationServiceMutationResult> AddServiceAsync(string reservaPublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero. En una reserva de solo-lectura
        // (Closed/Lost/Cancelled/PendingOperatorRefund) no se agrega servicio, sin autorizacion que valga.
        await ReservaCapacityRules.EnsureServicesEditableByStateAsync(_context, reservaId, ct);
        // ADR-020 F4: agregar un servicio a una reserva confirmada requiere autorizacion (y ademas
        // dispara regresion a En gestion: la autorizacion es el paso previo consciente).
        await EnsureReservaEditableAsync(reservaId, ReservaEditAuthorizationOperations.ServiceAdded,
            entityType: "ServicioReserva", entityId: null, summary: request.ServiceType, ct: ct);
        var (reservation, warning) = await AddServiceAsync(reservaId, request, ct);

        var servicioDto = _mapper.Map<ServicioReservaDto>(reservation);

        // B1 (ADR-017 F1b): el costo REAL se resuelve/persiste server-side, asi que viaja
        // en la entidad mapeada. El body de respuesta del POST llega a un caller que puede
        // no tener cobranzas.see_cost; sin esto reabriria la fuga que el GET de detalle ya
        // cierra (asimetria response-mutacion vs response-detalle).
        await CostMasking.MaskGenericServiceAsync(servicioDto, _httpContextAccessor, _permissionResolver, ct);

        return new ReservationServiceMutationResult
        {
            Servicio = servicioDto,
            Warning = warning
        };
    }

    public async Task<ServicioReservaDto> UpdateServiceAsync(string servicePublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default)
    {
        var serviceId = await ResolveRequiredIdAsync<ServicioReserva>(servicePublicIdOrLegacyId, ct);
        await EnsureServiceEditableAsync(serviceId, ReservaEditAuthorizationOperations.ServiceEdited, request.ServiceType, ct);
        var service = await UpdateServiceAsync(serviceId, request, ct);

        var servicioDto = _mapper.Map<ServicioReservaDto>(service);

        // B1 (ADR-017 F1b): mismo motivo que en AddServiceAsync — el body del PUT no debe
        // revelar NetCost/Commission/Tax reales a un caller sin cobranzas.see_cost.
        await CostMasking.MaskGenericServiceAsync(servicioDto, _httpContextAccessor, _permissionResolver, ct);

        return servicioDto;
    }

    public async Task RemoveServiceAsync(string servicePublicIdOrLegacyId, CancellationToken ct = default)
    {
        var serviceId = await ResolveRequiredIdAsync<ServicioReserva>(servicePublicIdOrLegacyId, ct);
        await EnsureServiceEditableAsync(serviceId, ReservaEditAuthorizationOperations.ServiceDeleted, null, ct);
        await RemoveServiceAsync(serviceId, ct);
    }

    /// <summary>
    /// ADR-020 F4: aplica el candado a una operacion sobre un servicio generico, resolviendo
    /// primero la reserva duena. Si el servicio no esta vinculado a una reserva (caso raro),
    /// no hay candado que aplicar.
    /// </summary>
    private async Task EnsureServiceEditableAsync(int serviceId, string operation, string? summary, CancellationToken ct)
    {
        var reservaId = await _context.Servicios
            .Where(s => s.Id == serviceId)
            .Select(s => s.ReservaId)
            .FirstOrDefaultAsync(ct);
        if (reservaId is null) return;
        // ADR-035: candado por ESTADO primero (antes del candado de autorizacion). En una reserva de
        // solo-lectura (terminal) no se edita ni se borra el servicio generico, sin autorizacion que valga.
        await ReservaCapacityRules.EnsureServicesEditableByStateAsync(_context, reservaId.Value, ct);
        await EnsureReservaEditableAsync(reservaId.Value, operation,
            entityType: "ServicioReserva", entityId: serviceId, summary: summary, ct: ct);
    }

    /// <summary>
    /// ADR-020 F4: aplica el candado a una operacion sobre un pasajero, resolviendo la reserva duena.
    /// </summary>
    private async Task EnsurePassengerEditableAsync(int passengerId, string operation, CancellationToken ct)
    {
        var reservaId = await _context.Passengers
            .Where(p => p.Id == passengerId)
            .Select(p => (int?)p.ReservaId)
            .FirstOrDefaultAsync(ct);
        if (reservaId is null) return;
        await EnsureReservaEditableAsync(reservaId.Value, operation,
            entityType: "Passenger", entityId: passengerId, summary: null, ct: ct);
    }

    /// <summary>
    /// Decision 2026-06-17 (pasajeros bajo candado): true si el update CAMBIA un dato de IDENTIDAD que YA
    /// estaba cargado (nombre/tipo y numero de documento/fecha de nacimiento/nacionalidad/genero no vacio y
    /// distinto al nuevo, o un dato cargado que se limpia). COMPLETAR un campo de identidad que estaba VACIO
    /// NO cuenta como cambio (es completar, no alterar) y por eso no dispara el candado de estado. Los campos
    /// de contacto (email/telefono/notas) y el vencimiento de pasaporte NO son identidad: se editan libres
    /// (mismo criterio que el guard fiscal de UpdatePassengerAsync). Si el pasajero no existe, devuelve false
    /// y deja que el metodo interno tire el NotFound.
    /// </summary>
    private async Task<bool> PassengerEditChangesExistingIdentityAsync(int passengerId, Passenger incoming, CancellationToken ct)
    {
        var current = await _context.Passengers.AsNoTracking()
            .Where(p => p.Id == passengerId)
            .Select(p => new { p.FullName, p.DocumentType, p.DocumentNumber, p.BirthDate, p.Nationality, p.Gender })
            .FirstOrDefaultAsync(ct);
        if (current is null) return false;

        return ChangesExistingText(current.FullName, incoming.FullName)
            || ChangesExistingText(current.DocumentType, incoming.DocumentType)
            || ChangesExistingText(current.DocumentNumber, incoming.DocumentNumber)
            || ChangesExistingDate(current.BirthDate, incoming.BirthDate)
            || ChangesExistingText(current.Nationality, incoming.Nationality)
            || ChangesExistingText(current.Gender, incoming.Gender);
    }

    /// <summary>"Cambia un valor ya cargado" = el actual NO esta vacio y el nuevo es distinto (incluye
    /// limpiarlo). Si el actual esta vacio, completar (o dejar vacio) NO es cambio.</summary>
    private static bool ChangesExistingText(string? current, string? incoming)
        => !string.IsNullOrWhiteSpace(current)
           && !string.Equals(current ?? string.Empty, incoming ?? string.Empty, StringComparison.Ordinal);

    /// <summary>Idem para la fecha de nacimiento: comparada por DIA (Kind/hora no cuentan).</summary>
    private static bool ChangesExistingDate(DateTime? current, DateTime? incoming)
    {
        if (!current.HasValue) return false;                 // estaba vacia -> completar no es cambio
        if (!incoming.HasValue) return true;                 // tenia valor y lo limpian -> cambio
        return current.Value.Date != incoming.Value.Date;
    }

    public async Task<IEnumerable<PassengerDto>> GetPassengersAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetPassengersAsync(reservaId);
    }

    public async Task<PassengerDto> AddPassengerAsync(string reservaPublicIdOrLegacyId, PassengerUpsertRequest passenger, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        // ADR-020 F4 + decision 2026-06-17 (pasajeros bajo candado): AGREGAR un pasajero es COMPLETAR el
        // roster nominal que el propio sistema EXIGE para emitir (aereo=nombre+doc, asistencia=+nacimiento).
        // Bloquearlo por el candado de ESTADO dejaba un callejon sin salida (te pide el dato y no te deja
        // cargarlo). Ya NO pide autorizacion por estado. El candado FISCAL sigue: la capacidad por reserva
        // (declaredPax) acota el alta a los cupos declarados y el alta no altera comprobantes ya emitidos.
        return await AddPassengerAsync(reservaId, MapPassenger(passenger));
    }

    public async Task<PassengerDto> UpdatePassengerAsync(string passengerPublicIdOrLegacyId, PassengerUpsertRequest updated, CancellationToken ct = default)
    {
        var passengerId = await ResolveRequiredIdAsync<Passenger>(passengerPublicIdOrLegacyId, ct);
        var mapped = MapPassenger(updated);
        // ADR-020 F4 + decision 2026-06-17 (pasajeros bajo candado): COMPLETAR un dato de identidad que
        // estaba VACIO no pide autorizacion (es completar, lo que el sistema exige para emitir); CAMBIAR un
        // dato de identidad YA cargado (o limpiarlo) SI pide autorizacion por el candado de estado. El
        // candado FISCAL (voucher emitido / CAE) lo aplica aparte el UpdatePassengerAsync interno cuando
        // cambian datos personales, este o no este bajo candado de estado.
        if (await PassengerEditChangesExistingIdentityAsync(passengerId, mapped, ct))
        {
            await EnsurePassengerEditableAsync(passengerId, ReservaEditAuthorizationOperations.PassengerEdited, ct);
        }
        return await UpdatePassengerAsync(passengerId, mapped);
    }

    public async Task RemovePassengerAsync(string passengerPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var passengerId = await ResolveRequiredIdAsync<Passenger>(passengerPublicIdOrLegacyId, ct);
        await EnsurePassengerEditableAsync(passengerId, ReservaEditAuthorizationOperations.PassengerDeleted, ct);
        await RemovePassengerAsync(passengerId);
    }

    public async Task<ReservaDto> UpdatePassengerCountsAsync(string reservaPublicIdOrLegacyId, PassengerCountsRequest counts, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // ADR-020: las cantidades agregadas (AdultCount/...) solo se editan en las etapas comerciales
        // tempranas (Cotizacion / Presupuesto). Desde En gestion se cargan pasajeros nominales.
        if (reserva.Status != EstadoReserva.Quotation && reserva.Status != EstadoReserva.Budget)
            throw new InvalidOperationException("Las cantidades de pasajeros solo se pueden editar en Cotizacion o Presupuesto. Si ya pasó a En gestion, cargá los pasajeros nominales.");

        if (counts.AdultCount < 0 || counts.ChildCount < 0 || counts.InfantCount < 0)
            throw new ArgumentException("Las cantidades no pueden ser negativas.");

        // Coherencia: no permitir bajar la cantidad DECLARADA por debajo de los pasajeros
        // NOMINALES ya cargados. Si lo permitieramos, quedarian pasajeros "huerfanos" (mas
        // nominales que la cantidad declarada) y el gate de readiness (currentPax < declaredPax)
        // pasaria de forma enganosa, dejando que esos pasajeros se cuelen en vouchers/facturas.
        // NO borramos pasajeros automaticamente: perderia datos cargados sin confirmacion del usuario.
        var declaredTotal = counts.AdultCount + counts.ChildCount + counts.InfantCount;
        var loadedPassengers = await _context.Passengers.CountAsync(p => p.ReservaId == reservaId, ct);
        if (declaredTotal < loadedPassengers)
            throw new InvalidOperationException(
                $"Hay {loadedPassengers} pasajeros cargados en la reserva; quitá los que sobren antes de bajar la cantidad a {declaredTotal}.");

        reserva.AdultCount = counts.AdultCount;
        reserva.ChildCount = counts.ChildCount;
        reserva.InfantCount = counts.InfantCount;

        await _context.SaveChangesAsync(ct);
        return await GetReservaByIdAsync(reservaId);
    }

    public async Task<ReservaDto> UpdateDatesAsync(string reservaPublicIdOrLegacyId, UpdateReservaDatesRequest request, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // ADR-035 (2026-06-19): PRIMERA COMPUERTA — en una reserva CERRADA (Closed/Lost/Cancelled/
        // PendingOperatorRefund) las fechas/datos de cabecera son solo lectura DURA: ninguna autorizacion las
        // desbloquea. Corre ANTES del candado de autorizacion (EnsureReservaEditableAsync) y del guard fiscal
        // (factura/voucher), que quedan intactos para los estados vivos.
        await ReservaCapacityRules.EnsureReservaDataEditableByStateAsync(_context, reservaId, ct);

        // ADR-020 F4: cambiar fechas de una reserva confirmada requiere autorizacion (candado).
        await EnsureReservaEditableAsync(reservaId, ReservaEditAuthorizationOperations.ReservaDataEdited,
            entityType: "Reserva", entityId: reservaId, summary: "Fechas de la reserva", ct: ct);

        // B1.15 Fase 0' (CODE-03): cambiar fechas con factura AFIP viva o voucher
        // emitido rompe la coherencia con el periodo declarado en el comprobante.
        var blockReason = await MutationGuards.GetReservaDatesMutationBlockReasonAsync(_context, reservaId, ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateDatesAsync rejected. ReservaId={ReservaId}. Reason={Reason}",
                reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        // Permite editar StartDate/EndDate explicitamente. Pasar `clearXxxDate=true`
        // borra el valor; pasar la fecha en el campo lo setea; null sin clear no toca.
        // Las fechas se normalizan a Kind=Utc porque las columnas Postgres son
        // 'timestamp with time zone' y Npgsql exige Kind=Utc al persistir.
        if (request.ClearStartDate)
            reserva.StartDate = null;
        else if (request.StartDate.HasValue)
            reserva.StartDate = NormalizeUtcOrNull(request.StartDate);

        if (request.ClearEndDate)
            reserva.EndDate = null;
        else if (request.EndDate.HasValue)
            reserva.EndDate = NormalizeUtcOrNull(request.EndDate);

        if (reserva.StartDate.HasValue && reserva.EndDate.HasValue
            && reserva.EndDate.Value.Date < reserva.StartDate.Value.Date)
        {
            throw new ArgumentException("La fecha de regreso no puede ser anterior a la fecha de salida.");
        }

        await _context.SaveChangesAsync(ct);
        return await GetReservaByIdAsync(reservaId);
    }

    /// <summary>
    /// Normaliza un DateTime opcional a Kind=Utc para persistirlo en columnas
    /// 'timestamp with time zone' de Postgres. Tambien actua como guard contra
    /// inputs vacios serializados como DateTime.MinValue ("0001-01-01").
    /// </summary>
    private static DateTime? NormalizeUtcOrNull(DateTime? value)
    {
        if (!value.HasValue) return null;
        if (value.Value == DateTime.MinValue) return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            // Unspecified (caso tipico del binder JSON con "yyyy-mm-dd"): asumimos
            // que el operador eligio una fecha calendario en su zona, no un instante.
            // Tomamos solo la parte Date y la marcamos Utc para evitar offsets.
            _ => DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc)
        };
    }

    // ============= Phase 2.1 — Pasajero <-> Servicio =============

    public async Task<IReadOnlyList<PassengerServiceAssignmentDto>> GetAssignmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);

        var passengerIds = await _context.Passengers
            .AsNoTracking()
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (passengerIds.Count == 0) return Array.Empty<PassengerServiceAssignmentDto>();

        var assignments = await _context.PassengerServiceAssignments
            .AsNoTracking()
            .Include(a => a.Passenger)
            .Where(a => passengerIds.Contains(a.PassengerId))
            .OrderBy(a => a.ServiceType).ThenBy(a => a.ServiceId).ThenBy(a => a.Id)
            .ToListAsync(ct);

        var publicIdLookup = await BuildServicePublicIdLookupAsync(assignments, ct);

        return assignments.Select(a => MapAssignment(a, ResolveServicePublicId(publicIdLookup, a.ServiceType, a.ServiceId))).ToList();
    }

    /// <summary>
    /// Construye un lookup (serviceType, serviceId) -> publicId con 1 query por tipo presente.
    /// Ej: si hay assignments contra 3 hoteles y 2 transfers, hace 2 queries totales (no 5).
    /// </summary>
    private async Task<Dictionary<string, Dictionary<int, Guid>>> BuildServicePublicIdLookupAsync(
        IReadOnlyCollection<PassengerServiceAssignment> assignments,
        CancellationToken ct)
    {
        var byType = assignments
            .GroupBy(a => a.ServiceType)
            .ToDictionary(g => g.Key, g => g.Select(a => a.ServiceId).Distinct().ToList());

        var result = new Dictionary<string, Dictionary<int, Guid>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (serviceType, ids) in byType)
        {
            if (ids.Count == 0) continue;

            var lookup = serviceType switch
            {
                AssignmentServiceType.Hotel => await _context.HotelBookings.AsNoTracking()
                    .Where(b => ids.Contains(b.Id))
                    .Select(b => new { b.Id, b.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Transfer => await _context.TransferBookings.AsNoTracking()
                    .Where(b => ids.Contains(b.Id))
                    .Select(b => new { b.Id, b.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Package => await _context.PackageBookings.AsNoTracking()
                    .Where(b => ids.Contains(b.Id))
                    .Select(b => new { b.Id, b.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Flight => await _context.FlightSegments.AsNoTracking()
                    .Where(f => ids.Contains(f.Id))
                    .Select(f => new { f.Id, f.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Assistance => await _context.AssistanceBookings.AsNoTracking()
                    .Where(a => ids.Contains(a.Id))
                    .Select(a => new { a.Id, a.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Generic => await _context.Servicios.AsNoTracking()
                    .Where(s => ids.Contains(s.Id))
                    .Select(s => new { s.Id, s.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                _ => new Dictionary<int, Guid>()
            };

            result[serviceType] = lookup;
        }

        return result;
    }

    private static Guid? ResolveServicePublicId(
        Dictionary<string, Dictionary<int, Guid>> lookup,
        string serviceType,
        int serviceId)
    {
        if (!lookup.TryGetValue(serviceType, out var byId)) return null;
        return byId.TryGetValue(serviceId, out var publicId) ? publicId : (Guid?)null;
    }

    public async Task<PassengerServiceAssignmentDto> CreateAssignmentAsync(string reservaPublicIdOrLegacyId, CreatePassengerAssignmentRequest request, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);

        // ADR-036: el mapeo pasajero<->servicio (quien viaja en cada servicio / quien sale en el voucher) es
        // una edicion de pasajeros mas. En "En viaje" y en los terminales (Closed/Lost/Cancelled/
        // PendingOperatorRefund) la reserva es solo lectura DURA: misma compuerta por estado que el CRUD de
        // pasajeros, corre PRIMERO. Sin esto se podian agregar/quitar asignaciones en una reserva cerrada.
        await ReservaCapacityRules.EnsurePassengersEditableByStateAsync(_context, reservaId, ct);

        if (string.IsNullOrWhiteSpace(request.ServiceType) || !AssignmentServiceType.All.Contains(request.ServiceType))
            throw new ArgumentException($"ServiceType invalido. Valores aceptados: {string.Join(", ", AssignmentServiceType.All)}.");

        // Resolver passenger y validar que pertenezca a la Reserva
        var passengerId = await ResolveRequiredIdAsync<Passenger>(request.PassengerPublicIdOrLegacyId, ct);
        var passenger = await _context.Passengers
            .FirstOrDefaultAsync(p => p.Id == passengerId, ct)
            ?? throw new KeyNotFoundException("Pasajero no encontrado");
        if (passenger.ReservaId != reservaId)
            throw new InvalidOperationException("El pasajero no pertenece a esta reserva.");

        // Resolver el ServiceId segun tipo (cada tipo tiene su tabla)
        var serviceId = request.ServiceType switch
        {
            AssignmentServiceType.Hotel => await ResolveRequiredIdAsync<HotelBooking>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Transfer => await ResolveRequiredIdAsync<TransferBooking>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Package => await ResolveRequiredIdAsync<PackageBooking>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Flight => await ResolveRequiredIdAsync<FlightSegment>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Assistance => await ResolveRequiredIdAsync<AssistanceBooking>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Generic => await ResolveRequiredIdAsync<ServicioReserva>(request.ServicePublicIdOrLegacyId, ct),
            _ => throw new ArgumentException("ServiceType no soportado.")
        };

        // Validar que el servicio pertenezca a la Reserva (defensa en profundidad)
        var serviceBelongsToReserva = request.ServiceType switch
        {
            AssignmentServiceType.Hotel => await _context.HotelBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Transfer => await _context.TransferBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Package => await _context.PackageBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Flight => await _context.FlightSegments.AnyAsync(f => f.Id == serviceId && f.ReservaId == reservaId, ct),
            AssignmentServiceType.Assistance => await _context.AssistanceBookings.AnyAsync(a => a.Id == serviceId && a.ReservaId == reservaId, ct),
            AssignmentServiceType.Generic => await _context.Servicios.AnyAsync(s => s.Id == serviceId && s.ReservaId == reservaId, ct),
            _ => false
        };
        if (!serviceBelongsToReserva)
            throw new InvalidOperationException("El servicio no pertenece a esta reserva.");

        // Idempotencia: si ya existe la asignacion, devolver la existente.
        // El check de capacidad va DESPUES — sino una re-asignacion idempotente
        // bloquearia indebidamente cuando el servicio ya esta lleno con ESTE mismo pax.
        var existing = await _context.PassengerServiceAssignments
            .Include(a => a.Passenger)
            .FirstOrDefaultAsync(a => a.PassengerId == passengerId && a.ServiceType == request.ServiceType && a.ServiceId == serviceId, ct);
        if (existing != null)
        {
            var existingPublicId = await ResolveServicePublicIdAsync(request.ServiceType, serviceId, ct);
            return MapAssignment(existing, existingPublicId);
        }

        // Phase 2.3: bloquear si el servicio ya esta lleno (capacidad por servicio).
        // Solo aplica a Hotel/Transfer/Package — Flight/Generic no declaran capacidad.
        var serviceLabel = await BuildServiceLabelAsync(request.ServiceType, serviceId, ct);
        var fullBlockReason = await ReservaCapacityRules.GetServiceFullBlockReasonAsync(
            _context, request.ServiceType, serviceId, serviceLabel, ct);
        if (fullBlockReason != null) throw new InvalidOperationException(fullBlockReason);

        var assignment = new PassengerServiceAssignment
        {
            PassengerId = passengerId,
            ServiceType = request.ServiceType,
            ServiceId = serviceId,
            RoomNumber = request.RoomNumber,
            SeatNumber = request.SeatNumber?.Trim(),
            Notes = request.Notes?.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _context.PassengerServiceAssignments.Add(assignment);
        await _context.SaveChangesAsync(ct);

        // ADR-031 v2.1 (§6.5): auditar el alta. La asignacion determina el SET del servicio (a quien se
        // le exige nombre/documento al resolver y quien aparece en su voucher), por eso queda trazada
        // quien/cuando. details SIN numero de documento (misma regla que el gate); con los ids alcanza.
        await LogAssignmentAuditAsync(
            AuditActions.PassengerAssignedToService,
            assignment.Id, request.ServiceType, serviceId, passengerId, reservaId, ct);

        // Re-cargar con Passenger include para el mapeo
        var saved = await _context.PassengerServiceAssignments
            .Include(a => a.Passenger)
            .FirstAsync(a => a.Id == assignment.Id, ct);

        var servicePublicId = await ResolveServicePublicIdAsync(request.ServiceType, serviceId, ct);
        return MapAssignment(saved, servicePublicId);
    }

    private async Task<Guid?> ResolveServicePublicIdAsync(string serviceType, int serviceId, CancellationToken ct)
    {
        return serviceType switch
        {
            AssignmentServiceType.Hotel => await _context.HotelBookings.AsNoTracking()
                .Where(b => b.Id == serviceId).Select(b => (Guid?)b.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Transfer => await _context.TransferBookings.AsNoTracking()
                .Where(b => b.Id == serviceId).Select(b => (Guid?)b.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Package => await _context.PackageBookings.AsNoTracking()
                .Where(b => b.Id == serviceId).Select(b => (Guid?)b.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Flight => await _context.FlightSegments.AsNoTracking()
                .Where(f => f.Id == serviceId).Select(f => (Guid?)f.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Assistance => await _context.AssistanceBookings.AsNoTracking()
                .Where(a => a.Id == serviceId).Select(a => (Guid?)a.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Generic => await _context.Servicios.AsNoTracking()
                .Where(s => s.Id == serviceId).Select(s => (Guid?)s.PublicId).FirstOrDefaultAsync(ct),
            _ => null
        };
    }

    /// <summary>Construye un label legible del servicio para mensajes de error.</summary>
    private async Task<string> BuildServiceLabelAsync(string serviceType, int serviceId, CancellationToken ct)
    {
        return serviceType switch
        {
            AssignmentServiceType.Hotel => await _context.HotelBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Hotel {b.HotelName ?? "sin nombre"}")
                .FirstOrDefaultAsync(ct) ?? "Hotel",
            AssignmentServiceType.Transfer => await _context.TransferBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Transfer {b.VehicleType ?? ""}".Trim())
                .FirstOrDefaultAsync(ct) ?? "Transfer",
            AssignmentServiceType.Package => await _context.PackageBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Paquete {b.PackageName ?? "sin nombre"}")
                .FirstOrDefaultAsync(ct) ?? "Paquete",
            AssignmentServiceType.Assistance => await _context.AssistanceBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Asistencia {b.PlanType ?? "seguro"}")
                .FirstOrDefaultAsync(ct) ?? "Asistencia",
            _ => serviceType
        };
    }

    public async Task RemoveAssignmentAsync(string assignmentPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var assignmentId = await ResolveRequiredIdAsync<PassengerServiceAssignment>(assignmentPublicIdOrLegacyId, ct);
        var assignment = await _context.PassengerServiceAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment == null) throw new KeyNotFoundException("Asignacion no encontrada");

        // ADR-031 v2.1 (§6.5): capturamos los datos para el audit ANTES de borrar (luego ya no estan).
        // La reserva la inferimos del pasajero (la asignacion no la guarda directo).
        var reservaIdForAudit = await _context.Passengers
            .AsNoTracking()
            .Where(p => p.Id == assignment.PassengerId)
            .Select(p => (int?)p.ReservaId)
            .FirstOrDefaultAsync(ct) ?? 0;

        // ADR-036: quitar una asignacion es editar pasajeros. En "En viaje" y en los terminales la reserva es
        // solo lectura DURA -> mismo candado por estado. Reusamos el reservaId que ya inferimos del pasajero
        // para el audit; gateamos ANTES de borrar.
        await ReservaCapacityRules.EnsurePassengersEditableByStateAsync(_context, reservaIdForAudit, ct);

        var auditServiceType = assignment.ServiceType;
        var auditServiceId = assignment.ServiceId;
        var auditPassengerId = assignment.PassengerId;

        _context.PassengerServiceAssignments.Remove(assignment);
        await _context.SaveChangesAsync(ct);

        // Auditar la baja MANUAL (distinta de la baja por cascada al borrar el servicio, §4.3).
        await LogAssignmentAuditAsync(
            AuditActions.PassengerUnassignedFromService,
            assignmentId, auditServiceType, auditServiceId, auditPassengerId, reservaIdForAudit, ct);
    }

    /// <summary>
    /// ADR-031 v2.1 (§6.5): registra un evento de auditoria de asignacion (alta o baja manual). El
    /// <c>details</c> JSON lleva SOLO ids (serviceType/serviceId/passengerId/reservaId) — NUNCA el numero
    /// de documento. Best-effort: si no hay IAuditService inyectado (tests), no hace nada; la integridad
    /// de la asignacion no depende del audit.
    /// </summary>
    private async Task LogAssignmentAuditAsync(
        string action, int assignmentId, string serviceType, int serviceId, int passengerId, int reservaId, CancellationToken ct)
    {
        if (_auditService is null) return;

        var (userId, userName) = ResolveAuditActor();
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            serviceType,
            serviceId,
            passengerId,
            reservaId
        });
        await _auditService.LogBusinessEventAsync(
            action,
            AuditActions.PassengerServiceAssignmentEntityName,
            assignmentId.ToString(),
            details,
            userId ?? string.Empty,
            userName,
            ct);
    }

    private static PassengerServiceAssignmentDto MapAssignment(PassengerServiceAssignment a, Guid? servicePublicId = null)
    {
        return new PassengerServiceAssignmentDto
        {
            PublicId = a.PublicId,
            PassengerPublicId = a.Passenger?.PublicId ?? Guid.Empty,
            PassengerFullName = a.Passenger?.FullName ?? string.Empty,
            ServiceType = a.ServiceType,
            ServiceId = a.ServiceId,
            ServicePublicId = servicePublicId,
            RoomNumber = a.RoomNumber,
            SeatNumber = a.SeatNumber,
            Notes = a.Notes,
            CreatedAt = a.CreatedAt
        };
    }

    // ============= /Phase 2.1 =============

    public async Task<IEnumerable<PaymentDto>> GetReservaPaymentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetReservaPaymentsAsync(reservaId);
    }

    public async Task<PaymentDto> AddPaymentAsync(string reservaPublicIdOrLegacyId, ReservationPaymentUpsertRequest payment, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await AddPaymentAsync(reservaId, MapPayment(payment));
    }

    public async Task<PaymentDto> UpdatePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, ReservationPaymentUpsertRequest updatedPayment, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, ct);
        return await UpdatePaymentAsync(reservaId, paymentId, MapPayment(updatedPayment));
    }

    public async Task DeletePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, ct);
        await DeletePaymentAsync(reservaId, paymentId);
    }

    public async Task<ReservaDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, string? actorUserId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);

        // B1.15 Fase 2a (Decision 6): cancelacion exige reservas.cancel y, si la
        // reserva tiene cobros o facturas, ademas reservas.cancel_with_payment.
        // Admin bypass: si el user actual es Admin, ya pasa por el handler de
        // permisos arriba; este chequeo es para el caso del Vendedor.
        //
        // B1.15 Fase 2a (FIX 7 — fiscal critico): bloqueo simetrico al de
        // RevertStatusAsync. Una reserva con factura AFIP CAE vivo (no anulada
        // via NC aprobada) NO se puede cancelar. La cancelacion sin NC dejaria
        // un comprobante fiscal valido para una reserva inexistente. El usuario
        // debe ejecutar primero <c>POST /api/invoices/{id}/annul</c> y esperar
        // a que <c>AnnulmentStatus = Succeeded</c>. El controller traduce
        // InvalidOperationException a 400/409 segun camino actual.
        //
        // FIX 2026-05-30 (mismo criterio que MutationGuards): EXCLUIMOS las Notas de
        // Credito del conteo. Una NC tambien es una fila Invoice con su propio CAE y
        // AnnulmentStatus=None, pero NACE para anular/corregir una factura — nunca se
        // anula a si misma. Si la contaramos, tras emitir una NC TOTAL la reserva
        // quedaria bloqueada para siempre aunque la factura original ya este Succeeded.
        // Solo bloquea que quede una FACTURA viva; en NC parcial la factura original
        // sigue viva por el resto, asi que igual bloquea (decision del dueño).
        if (status == EstadoReserva.Cancelled)
        {
            // NOTA: el gate fiscal "sin factura CAE viva" se movio al camino compartido
            // (ApplyTransitionAsync) para que el overload int no lo saltee. Aca quedan solo los
            // chequeos de PERMISO (B1.15), que dependen del actor y del HttpContext.

            // Solo aplicamos validacion B1.15 si tenemos un actor concreto.
            // En tests unitarios sin HttpContext el actorUserId puede llegar null
            // (camino legacy); preservamos el comportamiento previo en ese caso.
            if (!string.IsNullOrEmpty(actorUserId))
            {
                var httpContextUser = _httpContextAccessor?.HttpContext?.User;
                var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
                if (!isAdmin)
                {
                    var hasCancel = await UserHasPermissionAsync(actorUserId, Permissions.ReservasCancel, ct);
                    if (!hasCancel)
                    {
                        throw new UnauthorizedAccessException("No tenes permiso para cancelar reservas.");
                    }

                    var hasPaymentsOrInvoices = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted, ct)
                        || await _context.Invoices.AnyAsync(i => i.ReservaId == id, ct);
                    if (hasPaymentsOrInvoices)
                    {
                        var hasCancelWithPayment = await UserHasPermissionAsync(actorUserId, Permissions.ReservasCancelWithPayment, ct);
                        if (!hasCancelWithPayment)
                        {
                            throw new UnauthorizedAccessException(
                                "Cancelar una reserva con cobros o facturas asociadas requiere autorizacion adicional.");
                        }
                    }
                }
            }
        }

        await UpdateStatusAsync(id, status, actorUserId);
        return await GetReservaByIdAsync(id);
    }

    /// <summary>
    /// "Anular reserva SIN factura" del flujo unificado: anular una reserva en firme SIN factura con CAE vivo,
    /// CON o SIN cobros. Pasa la reserva a Cancelled. Cubre dos casos del discriminador:
    ///   - DirectCancel (sin cobros):     baja directa, SIN generar saldo a favor ni Nota de Credito.
    ///   - PaymentsToCredit (con cobros): la plata cobrada queda como SALDO A FAVOR del cliente (reutilizable),
    ///     un <see cref="ClientCreditEntry"/> por moneda, sin emitir Nota de Credito (no hay factura que acreditar).
    /// Ver el contrato completo en <see cref="IReservaService.AnnulWithPaymentsToCreditAsync"/>.
    ///
    /// <para><b>Atomicidad (patron FC4)</b>: todo el efecto (cambio de estado a Cancelled, cancelacion de los
    /// servicios para que la reserva no quede con venta exigible, conversion de cobros a saldo a favor por
    /// moneda y el audit) entra en UNA sola transaccion. El audit se STAGEA con <c>StageBusinessEvent</c> para
    /// que viaje en el mismo commit. Invariante: o se anula la reserva Y la plata queda 100% como saldo a
    /// favor, o no se toca nada.</para>
    ///
    /// <para><b>Por que NO toca el camino formal con NC</b>: aca, por precondicion, NO hay factura con CAE vivo,
    /// asi que no hay nada que acreditar fiscalmente. Si la hubiera, este metodo RECHAZA y deriva a
    /// <c>BookingCancellationService</c> (camino (4)). No se tocan los guards fiscales ni AfipService/InvoiceService.</para>
    /// </summary>
    /// <summary>Largo minimo del motivo de la anulacion. Mismo criterio que el draft de cancelacion con NC y que
    /// RevertStatusAsync: una operacion que mueve plata a saldo a favor tiene que quedar JUSTIFICADA en el audit.</summary>
    private const int AnnulReasonMinLength = 10;

    public async Task<ReservaDto> AnnulWithPaymentsToCreditAsync(
        string publicIdOrLegacyId, string reason, string? actorUserId, string? actorUserName, CancellationToken ct = default)
    {
        // Validacion server-side del motivo (NO se confia en el front): obligatorio y >= 10 chars. Es plata que se
        // mueve a saldo a favor -> sin justificacion no se ejecuta. ArgumentException; el controller la mapea a 400.
        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length < AnnulReasonMinLength)
        {
            throw new ArgumentException(
                $"El motivo de la anulación es obligatorio y debe tener al menos {AnnulReasonMinLength} caracteres.");
        }

        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);

        // AUTHZ (mismo patron que UpdateStatusAsync, lineas ~1058-1082): permiso BASE reservas.cancel y, SOLO si
        // la reserva tiene cobros o facturas asociadas, ademas reservas.cancel_with_payment. Admin bypassa (ya
        // paso por el handler de permisos del controller; este chequeo cubre al Vendedor). Asi la baja DIRECTA
        // sin plata (DirectCancel) le alcanza a un Vendedor con reservas.cancel, mientras que convertir cobros a
        // saldo a favor (PaymentsToCredit) sigue exigiendo la autorizacion reforzada.
        //
        // En tests unitarios sin HttpContext/resolver el actorUserId puede llegar null o no haber resolver: en
        // ese caso el bloque queda inerte (mismo comportamiento que UpdateStatusAsync). El SERVICE igual valida
        // las precondiciones de negocio mas abajo.
        if (!string.IsNullOrEmpty(actorUserId))
        {
            var httpContextUser = _httpContextAccessor?.HttpContext?.User;
            var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
            if (!isAdmin)
            {
                var hasCancel = await UserHasPermissionAsync(actorUserId, Permissions.ReservasCancel, ct);
                if (!hasCancel)
                {
                    throw new UnauthorizedAccessException("No tenes permiso para anular reservas.");
                }

                var hasPaymentsOrInvoices = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted, ct)
                    || await _context.Invoices.AnyAsync(i => i.ReservaId == id, ct);
                if (hasPaymentsOrInvoices)
                {
                    var hasCancelWithPayment = await UserHasPermissionAsync(actorUserId, Permissions.ReservasCancelWithPayment, ct);
                    if (!hasCancelWithPayment)
                    {
                        throw new UnauthorizedAccessException(
                            "Anular una reserva con cobros o facturas asociadas requiere autorizacion adicional.");
                    }
                }
            }
        }

        // Efecto atomico. Patron FC4: solo contra provider RELACIONAL usamos transaccion envolvente
        // (InMemory en los tests no soporta transacciones -> ramificamos por IsRelational y corremos el mismo
        // cuerpo sin transaccion; la atomicidad real se valida en integracion Postgres).
        //
        // IMPORTANTE (idempotencia/concurrencia, 2026-06-26): la carga de la reserva Y las precondiciones van
        // DENTRO del delegado de la ExecutionStrategy, no afuera. Bajo Serializable, dos requests concurrentes
        // (doble clic) pasan ambos las precondiciones; el perdedor aborta con 40001 y la ExecutionStrategy
        // REEJECUTA el delegado. Si recargamos fresco adentro (y limpiamos el ChangeTracker), el reintento ve
        // la reserva ya anulada y hace no-op en vez de crear un SEGUNDO saldo a favor de la nada.
        if (_context.Database.IsRelational())
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await LoadValidateAndApplyAnnulWithPaymentsToCreditAsync(id, trimmedReason, actorUserId, actorUserName, ct);
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            await LoadValidateAndApplyAnnulWithPaymentsToCreditAsync(id, trimmedReason, actorUserId, actorUserName, ct);
        }

        return await GetReservaByIdAsync(id);
    }

    /// <summary>
    /// Carga fresca la reserva, re-valida TODAS las precondiciones DENTRO de la transaccion y aplica el efecto.
    /// Se invoca una vez por intento de la ExecutionStrategy: cada reintento empieza con la pizarra limpia
    /// (<c>ChangeTracker.Clear</c>) y recarga, asi nunca arrastra entidades <c>Added</c> (credito/puente/audit)
    /// de un intento previo que se aborto, ni reusa una instancia tracked stale.
    /// </summary>
    private async Task LoadValidateAndApplyAnnulWithPaymentsToCreditAsync(
        int id, string reason, string? actorUserId, string? actorUserName, CancellationToken ct)
    {
        // Pizarra limpia. En un reintento de la ExecutionStrategy el ChangeTracker aun tiene las entidades
        // Added del intento anterior (saldo a favor, puente, audit); sin esto, el proximo SaveChanges las
        // re-insertaria -> saldo a favor DUPLICADO. Clear() las suelta y recargamos todo fresco abajo.
        _context.ChangeTracker.Clear();

        // 1) Cargar la reserva con su grafo economico (pagos + servicios) tracked: vamos a mutarla. Mismos
        //    Includes que usa el calculador/persister de plata, para que TotalPaid por moneda cuadre.
        var reserva = await _context.Reservas
            .Include(r => r.Payments)
            .Include(r => r.Servicios)
            .Include(r => r.FlightSegments)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // 2) GUARD DE IDEMPOTENCIA (corre PRIMERO, antes que las demas precondiciones). Doble clic, retry de la
        //    ExecutionStrategy o ganador de una carrera Serializable: si la operacion YA se aplico, esto es un
        //    reintento y debe ser NO-OP (no re-validar estado ni duplicar nada). Dos señales de "ya aplicada":
        //      (a) existe el puente de "saldo a favor por anulacion" -> hubo conversion de cobros (PaymentsToCredit).
        //          Usamos el Method del puente — es UNICO de esta operacion; NO usamos SourceReservaId del credito
        //          porque el converter de SOBREPAGO tambien lo setea y daria falso positivo.
        //      (b) la reserva ya esta Cancelled -> cubre el caso DIRECTO sin cobros (DirectCancel), que NO deja
        //          puente (no hay plata que trasladar): sin esta señal, un segundo clic veria la reserva ya
        //          anulada, fallaria la precondicion de estado firme y devolveria un 409 confuso.
        var alreadyConverted = await _context.Payments.AnyAsync(
            p => p.ReservaId == id && p.Method == CancellationToClientCreditConverter.BridgeMethod, ct);
        var alreadyCancelled = string.Equals(reserva.Status, EstadoReserva.Cancelled, StringComparison.OrdinalIgnoreCase);
        if (alreadyConverted || alreadyCancelled)
            return;

        // 3) Precondicion de estado: solo aplica en venta firme NO terminal (InManagement / Confirmed). En
        //    pre-venta el camino es MarkLost/baja; en terminales no hay nada que anular. Usamos la lista de
        //    estados firmes pero EXCLUIMOS los terminales firmes (Closed) y los ya-anulados.
        var isAnnulableState =
            string.Equals(reserva.Status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reserva.Status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase);
        if (!isAnnulableState)
        {
            throw new InvalidOperationException(
                "Esta acción solo aplica a una reserva en firme (En gestión o Confirmada). " +
                "En este estado no se puede anular con saldo a favor.");
        }

        // 4) Precondicion fiscal: NO debe tener factura con CAE vivo. Si la tiene, hay que ANULAR por el camino
        //    formal (Nota de Credito), no por aca. Mismo criterio de "CAE vivo" que el guard de cancelacion
        //    (excluye NC: una NC nace para anular, no mantiene viva la reserva). Derivamos al camino (4).
        var hasLiveCae = await _context.Invoices.AnyAsync(
            i => i.ReservaId == id
                && !CreditNoteComprobanteTypes.Contains(i.TipoComprobante)
                && !string.IsNullOrEmpty(i.CAE)
                && i.AnnulmentStatus != AnnulmentStatus.Succeeded,
            ct);
        if (hasLiveCae)
        {
            throw new InvalidOperationException(
                "La reserva tiene factura emitida. Para deshacerla hay que anularla por el camino formal " +
                "(se emite Nota de Crédito).");
        }

        // 5) Plata viva: ¿hay al menos un cobro vivo? Antes esto RECHAZABA cuando NO habia (forzaba la baja
        //    simple por otro camino). Ahora este metodo cubre AMBOS casos del flujo unificado:
        //      - CON cobros (PaymentsToCredit): el converter traslada la plata a saldo a favor del cliente.
        //      - SIN cobros (DirectCancel):     el converter no genera ningun saldo a favor (TotalPaid=0 por
        //        moneda -> devuelve lista vacia) y la reserva se anula DIRECTO.
        //    En los dos el efecto atomico es el mismo (cancelar servicios + recalcular deuda del operador +
        //    pasar a Cancelled + auditar). Por eso ya NO rechazamos por "sin cobros".
        var hasLivePayments = reserva.Payments.Any(p => !p.IsDeleted);

        // 6) Pagador: solo es OBLIGATORIO si hay cobros vivos. Sin pagador no hay bolsillo de cliente al que
        //    acreditar la plata, asi que con cobros + PayerId null rechazamos ANTES de mutar nada (si no, la
        //    plata del cliente desapareceria). Sin cobros (baja directa) no se genera ningun saldo a favor, asi
        //    que PayerId null es perfectamente aceptable.
        if (hasLivePayments && reserva.PayerId is null)
        {
            throw new InvalidOperationException(
                "La reserva tiene cobros pero no tiene un cliente pagador asignado; no se puede generar saldo a favor.");
        }

        // 7) Precondicion de PLATA AL OPERADOR (R1, gemela de la cancelacion de UN servicio): si a algun servicio se le
        //    pago al operador y la reserva NO tiene factura que ancle el receivable "me tiene que devolver", anular
        //    cancelaria todos los servicios (caja del operador negativa) SIN dejar la linea que representa ese
        //    receivable -> el reconciler del saldo a favor mintearia ese negativo como credito GASTABLE. Bloqueamos
        //    ANTES de mutar nada. La guarda vive en BookingCancellationService (misma logica, alcance TOTAL) para no
        //    duplicar el calculo del RefundCap. Solo corre si el service esta cableado (en runtime siempre lo inyecta
        //    DI; los tests que no anulan con plata al operador no lo inyectan y la guarda queda inerte, comportamiento
        //    seguro). Lanza InvalidOperationException -> el controller la mapea a 409, igual que las demas precondiciones.
        if (_cancellationService is not null)
        {
            await _cancellationService.EnsureReservaAnnulHasReceivableAnchorAsync(reserva.Id, ct);
        }

        await ApplyAnnulWithPaymentsToCreditAsync(reserva, reason, actorUserId, actorUserName, ct);
    }

    /// <summary>
    /// Cuerpo comun del caso (3): cancela servicios, traslada los cobros a saldo a favor por moneda, pasa la
    /// reserva a Cancelled, stagea el audit (con el motivo) y persiste el saldo. NO abre transaccion (la abre el
    /// caller); las SaveChanges de aca participan de la transaccion ambiente cuando existe, asi un fallo en
    /// cualquier paso revierte TODO.
    /// </summary>
    private async Task ApplyAnnulWithPaymentsToCreditAsync(
        Reserva reserva, string reason, string? actorUserId, string? actorUserName, CancellationToken ct)
    {
        var fromStatus = reserva.Status;

        // a) Cancelar TODOS los servicios vivos de la reserva. Sin esto, la reserva quedaria Cancelled pero con
        //    venta confirmada exigible (ConfirmedSale > 0): el saldo no daria 0 una vez sacada la plata pagada.
        //    Reusa la MISMA fuente que la anulacion total formal (CancelAllReservaServicesAsync de
        //    BookingCancellationService) via el helper compartido. No hace SaveChanges (lo cerramos abajo).
        await Reservations.ReservaServiceCanceller.CancelAllLiveServicesAsync(_context, reserva.Id, actorUserId, actorUserName, ct);

        // b) Convertir la plata viva en saldo a favor del cliente, por moneda. NO hace SaveChanges (corre dentro
        //    de la transaccion del caller). Devuelve el detalle por moneda para el audit.
        var convertedByCurrency = CancellationToClientCreditConverter.Convert(
            _context, reserva, actorUserId, actorUserName, _logger);

        // c) Pasar la reserva a Cancelled por el PUNTO ÚNICO de transición: rastro auditable + limpieza de la marca
        //    "confirmada con cambios" (FIX B1, 2026-07-04). Antes este camino seteaba el estado a mano y NO limpiaba
        //    la marca: una anulación simple sin factura dejaba pegado el cartel "Se editaron precios..."; si la
        //    reserva se reabría, reaparecía y trababa el pase a viaje. La regla de limpieza para Cancelled apaga la
        //    marca + borra el detalle de cambios pendientes, atómico con el resto (el caller cierra la transacción).
        //    Enriquecemos el log respecto del código viejo agregando ByUserName y el motivo (mismos datos que ya
        //    lleva el evento de auditoría de abajo); son campos aditivos del rastro, no cambian la lógica.
        await TravelApi.Infrastructure.Reservations.ReservaStatusTransitioner.ApplyAsync(
            _context, reserva, EstadoReserva.Cancelled, "Forward",
            actorUserId, actorUserName, reason, ct);

        // d) Audit STAGEADO (no guarda): entra en el mismo commit que el resto. El detail lleva la reserva, el
        //    cliente, el estado origen/destino, el MOTIVO declarado (justificacion de negocio, no es dato
        //    sensible) y la lista de saldos a favor POR MONEDA. Sin costos ni datos sensibles.
        //
        //    Elegimos la ACCION segun si hubo o no conversion a saldo a favor, para que la auditoria no insinue
        //    un "saldo a favor" inexistente en la baja directa:
        //      - con saldo a favor (PaymentsToCredit): ReservaCancelledWithPaymentsToClientCredit.
        //      - sin saldo a favor (DirectCancel):      ReservaAnnulledDirectlyWithoutCredit (creditsByCurrency vacio).
        if (_auditService is not null)
        {
            var details = System.Text.Json.JsonSerializer.Serialize(new
            {
                reservaPublicId = reserva.PublicId,
                reservaId = reserva.Id,
                customerId = reserva.PayerId,
                fromStatus,
                toStatus = EstadoReserva.Cancelled,
                reason,
                creditsByCurrency = convertedByCurrency
                    .Select(c => new { currency = c.Currency, amount = c.Amount })
                    .ToList(),
            });
            var auditAction = convertedByCurrency.Count > 0
                ? AuditActions.ReservaCancelledWithPaymentsToClientCredit
                : AuditActions.ReservaAnnulledDirectlyWithoutCredit;
            _auditService.StageBusinessEvent(
                auditAction,
                AuditActions.ReservaEntityName,
                reserva.Id.ToString(),
                details,
                actorUserId ?? string.Empty,
                actorUserName);
        }

        // e) PRIMER SaveChanges: persiste servicios cancelados + creditos + puentes negativos + cambio de
        //    estado + audit, todo de una. Si algo falla, la transaccion del caller revierte TODO.
        await _context.SaveChangesAsync(ct);

        // f) Recalcular la deuda de CADA operador afectado ahora que sus servicios quedaron cancelados (paso e
        //    ya los persistio). Sin esto la deuda del operador quedaba INFLADA (seguia contando servicios ya
        //    anulados). Mismo helper compartido que usa la anulacion total formal con NC
        //    (BookingCancellationService.RecalculateMoneyAfterTotalCancellationAsync). Hace su propio SaveChanges
        //    (participa de la transaccion ambiente).
        await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistForReservaSuppliersAsync(_context, reserva.Id, ct);

        // g) Recalcular el saldo de la reserva con los puentes ya vivos: la deuda exigible queda en 0 (servicios
        //    cancelados) y la plata pagada quedo trasladada al bolsillo. Persiste internamente (participa de la
        //    transaccion ambiente). Sincroniza el surrogate Balance y la tabla por moneda.
        await ReservaMoneyPersister.PersistAsync(_context, reserva.Id, ct);
    }

    public async Task<TransitionReadinessDto> GetTransitionReadinessAsync(string publicIdOrLegacyId, string targetStatus, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas
            .Include(r => r.Passengers)
            .Include(r => r.Servicios)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.FlightSegments)
            .Include(r => r.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // Composicion derivada de los servicios cargados. Se usa SOLO como SUGERENCIA para
        // pre-rellenar el modal de confirmacion (cuantos adultos/menores proponer), NO para
        // contar. El conteo esperado real es la cantidad DECLARADA de la reserva (abajo).
        var (suggestedAdults, suggestedChildren, suggestedInfants, ambiguous) = ComputePaxCompositionFromServices(reserva);

        // Fuente UNICA del conteo esperado = cantidad DECLARADA de la reserva. Debe coincidir
        // con la regla de EnsureReadinessForSaleAsync para que el modal del front y el gate del
        // backend nunca se contradigan.
        var declaredPax = reserva.AdultCount + reserva.ChildCount + reserva.InfantCount;

        var dto = new TransitionReadinessDto
        {
            TargetStatus = targetStatus,
            Allowed = true,
            ExpectedAdults = suggestedAdults,
            ExpectedChildren = suggestedChildren,
            ExpectedInfants = suggestedInfants,
            AmbiguousComposition = ambiguous,
            ExpectedPassengerCount = declaredPax,
            CurrentPassengerCount = reserva.Passengers?.Count ?? 0
        };

        // ADR-031: preview de la transicion manual real Budget -> InManagement (el cliente acepta el
        // presupuesto). Antes este bloque apuntaba a Budget -> Confirmed, que NO es una transicion
        // manual (Confirmed lo alcanza solo el motor automatico) -> era un preview muerto que ademas
        // exigia nominales, contradiciendo la nueva regla. Realineado al target correcto y a la regla
        // nueva: solo CANTIDAD (≥1 servicio + cantidad declarada > 0). Los nombres ya NO se exigen aca:
        // se exigen al resolver/emitir cada servicio (PassengerNominalRules en BookingService).
        if (targetStatus == EstadoReserva.InManagement && reserva.Status == EstadoReserva.Budget)
        {
            // Chequeamos las 6 tablas de servicios (no solo Servicios genericos). El caso tipico del
            // agente es cargar un Hotel — antes daba "no hay servicios" por mirar solo la generica.
            var hasAnyService = (reserva.Servicios?.Any() ?? false)
                || (reserva.HotelBookings?.Any() ?? false)
                || (reserva.TransferBookings?.Any() ?? false)
                || (reserva.PackageBookings?.Any() ?? false)
                || (reserva.FlightSegments?.Any() ?? false)
                || (reserva.AssistanceBookings?.Any() ?? false);
            if (!hasAnyService)
            {
                dto.Allowed = false;
                dto.BlockingReasons.Add("Cargá al menos un servicio (hotel, vuelo, transfer, paquete o asistencia) antes de continuar.");
            }

            // Regla A: sin pasajeros DECLARADOS no se puede avanzar (coherente con el gate del
            // backend EnsureReadinessForSaleAsync). Es la unica exigencia de pasajeros en este punto.
            if (dto.ExpectedPassengerCount <= 0)
            {
                dto.Allowed = false;
                dto.BlockingReasons.Add(
                    "No se puede continuar sin pasajeros: declará al menos 1 pasajero en la reserva.");
            }
        }

        return dto;
    }

    /// <summary>
    /// Deriva la composicion de pasajeros (adultos/menores/infantes) a partir de los
    /// servicios cargados. El servicio con mayor total (Adults+Children) es el "anchor"
    /// y su composicion se considera la default. Si OTRO servicio tiene mismo total
    /// pero distinta composicion, AmbiguousComposition=true (warning para el agente,
    /// no bloqueo).
    ///
    /// HotelBooking, PackageBooking y AssistanceBooking declaran composicion explicita
    /// (Adults + Children) — los 3 sirven como "anchor". TransferBooking solo tiene
    /// Passengers (total). FlightSegment no declara nada. Por eso esos dos no se usan como
    /// "anchor" — solo extienden el total minimo via fallback. Infants nunca viene de
    /// servicios; queda en 0 a menos que el agente lo ajuste manualmente en el modal.
    /// </summary>
    private static (int adults, int children, int infants, bool ambiguous) ComputePaxCompositionFromServices(Reserva reserva)
    {
        var candidates = new List<(int adults, int children, int total)>();

        foreach (var h in reserva.HotelBookings ?? Enumerable.Empty<HotelBooking>())
        {
            candidates.Add((h.Adults, h.Children, h.Adults + h.Children));
        }
        foreach (var p in reserva.PackageBookings ?? Enumerable.Empty<PackageBooking>())
        {
            candidates.Add((p.Adults, p.Children, p.Adults + p.Children));
        }
        // Asistencia declara Adults+Children (los pasajeros cubiertos por la poliza), igual
        // que Hotel/Package, asi que tambien es un candidato a "anchor" de composicion.
        foreach (var a in reserva.AssistanceBookings ?? Enumerable.Empty<AssistanceBooking>())
        {
            candidates.Add((a.Adults, a.Children, a.Adults + a.Children));
        }

        if (candidates.Count == 0)
        {
            // Sin servicios con composicion explicita. Si hay transfer, usar su Passengers
            // como cantidad de adultos (no se sabe distribucion).
            var transferMax = reserva.TransferBookings?.Max(t => (int?)t.Passengers) ?? 0;
            return (transferMax, 0, 0, false);
        }

        // Anchor: candidato con mayor total. En empate, el primero (orden Hotel -> Package).
        var anchor = candidates.OrderByDescending(c => c.total).First();

        // Ambiguedad: hay otro candidato con mismo total pero distinta composicion?
        var ambiguous = candidates.Any(c =>
            (c.adults != anchor.adults || c.children != anchor.children)
            && c.total == anchor.total);

        return (anchor.adults, anchor.children, 0, ambiguous);
    }

    public async Task<ReservaDto> ArchiveReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        await ArchiveReservaAsync(id);
        return await GetReservaByIdAsync(id);
    }

    // ============= Phase 2.4 — Reversion de Status con autorizacion =============

    // ============================================================
    // ADR-020 (2026-06-07): matriz UNICA del ciclo de vida de la Reserva (murio el ciclo dual
    // y el flag EnableSoldToSettleStates). Ciclo:
    //   Quotation -> Budget -> InManagement -> [Confirmed (AUTOMATICO)] -> Traveling -> Closed
    // con Lost/Cancelled laterales.
    // ADR-036 (2026-06-21, prepago puro): murio ToSettle ("A liquidar"). La unica salida de Traveling es
    // Closed; no hay etapa de liquidacion posterior (el operador cobra el 100% antes del viaje).
    //
    // Reglas que NO viven en estas matrices (a proposito):
    //  - InManagement <-> Confirmed: lo maneja SOLO el motor automatico (ReservaAutoStateService),
    //    NUNCA UpdateStatusAsync ni RevertStatusAsync (INV-020-02). Por eso Confirmed no aparece
    //    como destino forward manual ni InManagement como revert manual de Confirmed.
    //  - Cancelacion ADR-002 / PendingOperatorRefund / Archived: flujos dedicados que escriben
    //    Status por fuera de estas matrices.
    // ============================================================

    // ADR-035 C6 (2026-06-19): las matrices forward/revert se MOVIERON al dominio
    // (TravelApi.Domain.Reservations.ReservaStatusTransitions) para ser FUENTE UNICA compartida con
    // ReservaCapabilities (la fachada de lectura que el frontend consulta). Aca quedan como alias estaticos
    // para no tocar los call-sites (UpdateStatusAsync, RevertStatusAsync, GetRevertOptionsAsync). El gate de
    // escritura sigue siendo la defensa final; la fachada de lectura solo LEE estas mismas tablas.
    private static readonly IReadOnlyDictionary<string, string[]> AllowedForwardTransitions =
        TravelApi.Domain.Reservations.ReservaStatusTransitions.Forward;

    private static readonly IReadOnlyDictionary<string, string[]> AllowedRevertTransitions =
        TravelApi.Domain.Reservations.ReservaStatusTransitions.Revert;

    /// <summary>
    /// ADR-020 (B1): el revert de <c>Lost</c> vuelve al estado desde el que se perdio. Lo deduce del
    /// <c>FromStatus</c> de la ultima transicion hacia Lost en <see cref="ReservaStatusChangeLog"/>.
    /// Fallback defensivo <c>Budget</c> si no hay fila (no deberia pasar: toda transicion loguea).
    /// </summary>
    private async Task<string> ResolveLostRevertTargetAsync(int reservaId, CancellationToken ct)
    {
        var lastToLost = await _context.ReservaStatusChangeLogs
            .AsNoTracking()
            .Where(l => l.ReservaId == reservaId && l.ToStatus == EstadoReserva.Lost)
            .OrderByDescending(l => l.OccurredAt)
            .Select(l => l.FromStatus)
            .FirstOrDefaultAsync(ct);

        // Solo Quotation o Budget son origenes legales de Lost; cualquier otra cosa -> Budget.
        if (string.Equals(lastToLost, EstadoReserva.Quotation, StringComparison.OrdinalIgnoreCase))
            return EstadoReserva.Quotation;
        return EstadoReserva.Budget;
    }

    public async Task<RevertOptionsDto> GetRevertOptionsAsync(string publicIdOrLegacyId, string actorUserId, bool actorIsAdmin, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        var dto = new RevertOptionsDto
        {
            CurrentStatus = reserva.Status,
            ActorIsAdmin = actorIsAdmin,
            RequiresAuthorization = !actorIsAdmin
        };

        // ADR-020: matriz de reverts UNICA (murio el ciclo dual).
        if (AllowedRevertTransitions.TryGetValue(reserva.Status, out var targets))
        {
            if (string.Equals(reserva.Status, EstadoReserva.Lost, StringComparison.OrdinalIgnoreCase))
            {
                // El revert de Lost tiene UN target deterministico (el estado de origen registrado).
                dto.AllowedTargets.Add(await ResolveLostRevertTargetAsync(id, ct));
            }
            else
            {
                dto.AllowedTargets.AddRange(targets);
            }
        }

        // Hard blockers (no se saltean ni siendo admin):
        // - Reserva con factura AFIP con CAE asignado: revertir rompe historia fiscal.
        var hasInvoiceWithCae = await _context.Invoices.AsNoTracking()
            .AnyAsync(i => i.ReservaId == id && !string.IsNullOrEmpty(i.CAE), ct);
        if (hasInvoiceWithCae)
        {
            dto.HardBlockers.Add("La reserva tiene facturas AFIP emitidas con CAE. No se puede revertir el estado (rompe la historia fiscal). Si necesitas anular, emiti una Nota de Credito primero.");
            dto.AllowedTargets.Clear();
        }

        // ADR-033 (2026-06-16, E4/B4 — gate D2 en las OPCIONES): si la reserva es Cancelada con huella fiscal
        // o de plata de la cancelacion (NC / saldo a favor / refund), no se ofrece el revert (la UI no muestra
        // una opcion que despues va a 409). Misma query que el enforcement en RevertStatusAsync (fuente unica
        // del criterio). Solo aplica cuando el estado actual es Cancelled.
        if (string.Equals(reserva.Status, EstadoReserva.Cancelled, StringComparison.OrdinalIgnoreCase)
            && dto.AllowedTargets.Count > 0)
        {
            var hasFiscalOrMoneyTrace = await _context.BookingCancellations.AsNoTracking()
                .Where(bc => bc.ReservaId == id)
                .AnyAsync(bc =>
                    bc.CreditNoteInvoiceId != null
                    || bc.ReceivedRefundAmount > 0
                    || _context.ClientCreditEntries.Any(cce => cce.BookingCancellationId == bc.Id),
                    ct);
            if (hasFiscalOrMoneyTrace)
            {
                dto.HardBlockers.Add("Esta cancelacion ya genero una nota de credito, un saldo a favor o un reintegro del operador. No se puede revertir sin deshacer ese movimiento por su circuito.");
                dto.AllowedTargets.Clear();
            }
        }

        // Si requiere autorizacion, listar supervisores con permiso
        if (!actorIsAdmin && dto.AllowedTargets.Count > 0)
        {
            var superiors = await _context.Users.AsNoTracking()
                .Where(u => u.IsActive)
                .ToListAsync(ct);
            var allUserRoles = await _context.UserRoles.AsNoTracking()
                .Join(_context.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, RoleName = r.Name! })
                .ToListAsync(ct);
            var allRolePerms = await _context.RolePermissions.AsNoTracking()
                .Where(rp => rp.Permission == Permissions.VouchersAuthorizeException)
                .Select(rp => rp.RoleName)
                .ToListAsync(ct);
            var rolesWithAuth = new HashSet<string>(allRolePerms, StringComparer.OrdinalIgnoreCase);
            rolesWithAuth.Add("Admin"); // admin siempre puede

            foreach (var u in superiors)
            {
                if (u.Id == actorUserId) continue; // no se autoriza a si mismo
                var roles = allUserRoles.Where(r => r.UserId == u.Id).Select(r => r.RoleName);
                if (roles.Any(r => rolesWithAuth.Contains(r)))
                {
                    dto.Supervisors.Add(new SupervisorOptionDto
                    {
                        UserId = u.Id,
                        FullName = u.FullName ?? u.UserName ?? u.Email ?? u.Id
                    });
                }
            }
        }

        return dto;
    }

    public async Task<ReservaDto> RevertStatusAsync(
        string publicIdOrLegacyId,
        RevertStatusRequest request,
        string actorUserId,
        string? actorUserName,
        bool actorIsAdmin,
        CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // ADR-020: matriz de reverts UNICA (murio el ciclo dual).
        if (!AllowedRevertTransitions.TryGetValue(reserva.Status, out var allowedTargets) || !allowedTargets.Contains(request.TargetStatus, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"No se puede revertir desde {reserva.Status} a {request.TargetStatus}. " +
                $"Transiciones permitidas desde {reserva.Status}: {(allowedTargets == null ? "(ninguna)" : string.Join(", ", allowedTargets))}.");
        }

        // ADR-020 (B1): el revert de Lost vuelve SOLO al estado de origen registrado (deterministico).
        if (string.Equals(reserva.Status, EstadoReserva.Lost, StringComparison.OrdinalIgnoreCase))
        {
            var legalTarget = await ResolveLostRevertTargetAsync(id, ct);
            if (!string.Equals(request.TargetStatus, legalTarget, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Una reserva Perdida solo puede volver a '{legalTarget}' (el estado desde el que se perdio).");
        }

        // Hard blockers
        var hasInvoiceWithCae = await _context.Invoices.AnyAsync(i => i.ReservaId == id && !string.IsNullOrEmpty(i.CAE), ct);
        if (hasInvoiceWithCae)
            throw new InvalidOperationException("La reserva tiene facturas AFIP emitidas con CAE. No se puede revertir (rompe la historia fiscal).");

        // ADR-020 (M5): el unico revert con gate es InManagement -> Budget (sin pagos vivos + sin
        // facturas + sin servicios resueltos). El gate unificado vive en EnsureCanRevertToBudgetAsync.
        if (string.Equals(reserva.Status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.TargetStatus, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureCanRevertToBudgetAsync(id, ct);
        }

        // ADR-033 (2026-06-16, E4/B4 — gate duro D2): reabrir una Cancelada solo es valido si la cancelacion
        // NO genero ningun movimiento fiscal o de plata por su propio circuito. Si hubo NC, saldo a favor o
        // refund del operador, "reabrir" sin deshacer esos movimientos dejaria la plata descuadrada -> hay que
        // deshacerlos por su circuito, no por un revert de estado. El hard-block CAE (arriba) ya corrio antes,
        // asi que una reserva con factura viva ni llega aca; este gate cubre lo especifico de la cancelacion
        // que la factura no captura. Anclado en la BookingCancellation de la reserva (ata NC + credito + refund).
        if (string.Equals(reserva.Status, EstadoReserva.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            // 1) NC emitida:            BookingCancellation.CreditNoteInvoiceId != null
            // 2) Refund recibido:       BookingCancellation.ReceivedRefundAmount > 0
            // 3) Saldo a favor de la cancelacion: existe un ClientCreditEntry apuntando a una BC de esta reserva
            //    (cubre el caso refund -> credito del cliente).
            var hasFiscalOrMoneyTrace = await _context.BookingCancellations
                .Where(bc => bc.ReservaId == id)
                .AnyAsync(bc =>
                    bc.CreditNoteInvoiceId != null
                    || bc.ReceivedRefundAmount > 0
                    || _context.ClientCreditEntries.Any(cce => cce.BookingCancellationId == bc.Id),
                    ct);
            if (hasFiscalOrMoneyTrace)
                throw new InvalidOperationException(
                    "Esta cancelacion ya genero una nota de credito, un saldo a favor o un reintegro del operador. " +
                    "No se puede revertir sin deshacer ese movimiento por su circuito.");
        }

        // Autorizacion
        string? authSuperiorId = null;
        string? authSuperiorName = null;
        var reason = (request.Reason ?? "").Trim();

        if (!actorIsAdmin)
        {
            if (string.IsNullOrWhiteSpace(request.AuthorizedBySuperiorUserId))
                throw new InvalidOperationException("Necesitas autorizacion de un supervisor para revertir el estado de la reserva. Selecciona un supervisor en el formulario.");
            if (reason.Length < 10)
                throw new InvalidOperationException("Indica un motivo de la reversion (al menos 10 caracteres).");

            var superior = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == request.AuthorizedBySuperiorUserId && u.IsActive, ct)
                ?? throw new InvalidOperationException("El supervisor seleccionado no existe o esta inactivo.");

            var superiorRoles = await _context.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == superior.Id)
                .Join(_context.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
                .ToListAsync(ct);
            var isSuperiorAdmin = superiorRoles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
            var canAuthorize = isSuperiorAdmin || await _context.RolePermissions.AsNoTracking()
                .AnyAsync(p => superiorRoles.Contains(p.RoleName) && p.Permission == Permissions.VouchersAuthorizeException, ct);
            if (!canAuthorize)
                throw new InvalidOperationException("El supervisor seleccionado no tiene permiso para autorizar reversiones.");

            authSuperiorId = superior.Id;
            authSuperiorName = superior.FullName ?? superior.UserName ?? superior.Id;
        }
        else
        {
            // Admin: la reason es opcional pero se loguea si vino.
            if (string.IsNullOrWhiteSpace(reason)) reason = "(reversion por admin sin motivo declarado)";
        }

        // Re-abrir una reserva cerrada borra el ClosedAt. ADR-036 (2026-06-21): el unico revert de Closed es
        // a Traveling (Closed->ToSettle murio junto con el estado). Sino la reserva figura "cerrada el dia X"
        // pero esta abierta -> dato inconsistente.
        if (request.TargetStatus == EstadoReserva.Traveling && reserva.ClosedAt.HasValue)
            reserva.ClosedAt = null;

        // Cambio de estado + rastro auditable (Direction="Revert", con el supervisor autorizante) + limpieza de
        // marcas por el PUNTO ÚNICO de transición. FIX B2 (2026-07-04): antes este revert seteaba el estado a mano
        // y NO limpiaba la marca "confirmada con cambios" -> volver a Presupuesto dejaba pegado el cartel de
        // revisión. Ahora la regla de limpieza para Budget/Quotation apaga la marca + borra el detalle + limpia el
        // motivo de revisión (la reserva vuelve a pre-venta desde cero).
        await TravelApi.Infrastructure.Reservations.ReservaStatusTransitioner.ApplyAsync(
            _context, reserva, request.TargetStatus, "Revert",
            actorUserId, actorUserName, reason, ct,
            authorizedBySuperiorUserId: authSuperiorId,
            authorizedBySuperiorUserName: authSuperiorName);

        // CRM leads (fix de fondo 2026-06-18): un revert puede llevar la reserva a un estado FIRME — el
        // caso real es reabrir una Cancelada de vuelta a En gestion. Si esa reserva nacio de un lead, ese
        // lead debe quedar Ganado igual que por cualquier otra entrada a firme. Idempotente: si el revert
        // va a un estado no-firme (ej. InManagement -> Budget, Lost -> Budget) o el lead ya estaba
        // Ganado/Perdido, es un no-op. Se persiste junto con la transicion en el SaveChanges de abajo.
        await MarkSourceLeadAsWonIfReservaIsFirmAsync(reserva);

        await _context.SaveChangesAsync(ct);
        return await GetReservaByIdAsync(id);
    }

    /// <summary>
    /// ADR-036 (2026-06-22): "Sacar de viaje" — correccion de EXCEPCION de una reserva que entro a "En viaje"
    /// por error (fecha mal cargada o el viaje no salio). La devuelve a Confirmed y borra su StartDate. NO usa
    /// la matriz de revert a proposito (Traveling no revierte en ADR-036): es una accion dedicada y auditada.
    ///
    /// <para>El PERMISO (reservas.correct_traveling, solo Admin) lo verifica el controller; aca validamos las
    /// reglas de NEGOCIO, que no son bypasseables por nadie (ni Admin): estado correcto, sin factura viva, sin
    /// voucher vivo, con motivo. Patron idempotente como el job: re-leemos el estado de la base.</para>
    /// </summary>
    public async Task<ReservaDto> CorrectTravelingEntryAsync(
        string publicIdOrLegacyId,
        CorrectTravelingEntryRequest request,
        string actorUserId,
        string? actorUserName,
        CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // (1) Solo aplica a "En viaje". Re-leemos el estado de la fila (es lo recien cargado), patron idempotente
        // del job: si otra transaccion ya la saco de viaje (o nunca lo estuvo), no es un error de programa sino
        // un 409 con mensaje claro. Asi un doble click / reintento no rompe nada.
        if (!string.Equals(reserva.Status, EstadoReserva.Traveling, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Esta acción solo aplica a una reserva En viaje. La reserva ya no está En viaje.");

        // (2) BLOQUEO FISCAL — factura con CAE VIVO (no NC). NO bypasseable ni por Admin: si ya hay un
        // comprobante fiscal sellado, la correccion se hace por Nota de Credito/ajuste, no devolviendo el
        // estado. Criterio UNICO con MutationGuards / HasLiveMoneyAsync del lifecycle: factura NO-NC + CAE no
        // vacio + AnnulmentStatus != Succeeded. Las NC se excluyen (restan, no mantienen viva la reserva). EF
        // no traduce InvoiceComprobanteHelpers.IsCreditNote a SQL -> se expande inline con los cbteTipo de NC.
        var hasLiveCae = await _context.Invoices.AsNoTracking().AnyAsync(
            i => i.ReservaId == id
                && !CreditNoteComprobanteTipos.Contains(i.TipoComprobante) // excluye NC (3=A, 8=B, 13=C, 53=M)
                && !string.IsNullOrEmpty(i.CAE)
                && i.AnnulmentStatus != AnnulmentStatus.Succeeded,
            ct);
        if (hasLiveCae)
            throw new InvalidOperationException(
                "La reserva tiene factura emitida: la corrección se hace por Nota de Crédito/ajuste.");

        // (3) BLOQUEO POR VOUCHER VIVO — voucher emitido y no anulado (Issued / UploadedExternal). El caso de
        // uso ("entró por error") normalmente no tiene voucher; si lo tiene, hay que anularlo primero (los datos
        // del voucher quedarian incoherentes con una reserva que vuelve atras).
        var hasLiveVoucher = await _context.Vouchers.AsNoTracking().AnyAsync(
            v => v.ReservaId == id
                && (v.Status == VoucherStatuses.Issued || v.Status == VoucherStatuses.UploadedExternal),
            ct);
        if (hasLiveVoucher)
            throw new InvalidOperationException("Anulá el voucher antes de sacar de viaje.");

        // (4) COBROS: NO se bloquea por cobros A PROPOSITO. Toda reserva En viaje esta saldada (candado de pago
        // de ADR-036: para viajar el cliente quedo en Balance == 0). La correccion la devuelve a Confirmed
        // SALDADA; los cobros quedan tal cual (siguen siendo validos: la venta firme sigue firme). No tocamos
        // Payments ni el Libro de Caja.

        // (5) MOTIVO OBLIGATORIO — minimo 10 caracteres, SIN excepcion ni para Admin (esta accion deshace un
        // estado normalmente inmutable: siempre tiene que quedar registrado por que). 400 si no cumple.
        var reason = (request.Reason ?? "").Trim();
        if (reason.Length < 10)
            throw new ArgumentException("Indicá un motivo para sacar de viaje (al menos 10 caracteres).");

        // (6) Se le BORRA StartDate. Por que null y no recomputar: la fecha de salida es derivada de los servicios;
        // ponerla en null saca la reserva del filtro de candidatos del job (AutoTransitionConfirmedToTravelingAsync
        // exige StartDate.HasValue && StartDate <= hoy), evitando que esa misma noche la vuelva a promover. Ademas
        // refleja la realidad: entro por error y falta recargar la fecha del servicio. Cuando se corrija la fecha
        // del servicio, RecalculateReservationScheduleAsync recomputa StartDate desde los servicios (si queda
        // futura, el job no la toma). Verificado (grep 2026-06-22): nada calcula plata/comision/vencimiento sobre
        // Reserva.StartDate — solo filtros de urgencia/worklist y display; borrarla no descuadra ningun monto.
        reserva.StartDate = null;

        // (7/8) Vuelve a Confirmed por el PUNTO ÚNICO de transición: rastro auditable con Direction = "Correction"
        // (valor NUEVO, exclusivo de esta accion; ningun consumidor backend ramifica sobre Direction) + limpieza del
        // motivo de revision. La regla de limpieza para Confirmed limpia SOLO LastRegression* (la franja "volvio sola
        // de Confirmada"): NO apaga la marca "confirmada con cambios", que solo baja una persona con el OK. Antes esta
        // accion limpiaba LastRegression* a mano; ahora esa limpieza vive en la tabla unica de reglas. No hay
        // autorizante: el permiso (Admin) ya gateo en el controller.
        await TravelApi.Infrastructure.Reservations.ReservaStatusTransitioner.ApplyAsync(
            _context, reserva, EstadoReserva.Confirmed, "Correction",
            actorUserId, actorUserName, reason, ct);

        await _context.SaveChangesAsync(ct);
        return await GetReservaByIdAsync(id);
    }

    /// <summary>
    /// cbteTipo de las Notas de Credito de AFIP (3=A, 8=B, 13=C, 53=M). Espejo de
    /// <c>MutationGuards.LiveInvoiceCreditNoteTypes</c>: se excluyen del conteo de "factura viva" porque una NC
    /// resta y no debe, por si sola, frenar la correccion. Array literal porque EF Core no traduce el helper
    /// <c>InvoiceComprobanteHelpers.IsCreditNote</c> a SQL. Mantener sincronizado con el de MutationGuards.
    /// </summary>
    private static readonly int[] CreditNoteComprobanteTipos = { 3, 8, 13, 53 };

    // ============= /Phase 2.4 =============

    // ============================================================
    // ADR-020 F4: candado de reservas confirmadas
    // ============================================================

    /// <summary>Ventana de validez de una autorizacion de edicion bajo candado (A5: 30 minutos).</summary>
    private static readonly TimeSpan EditAuthorizationWindow = TimeSpan.FromMinutes(30);

    /// <summary>Nombre legible del usuario actual desde el HttpContext, o null en tests sin contexto.</summary>
    private string? GetCurrentUserNameOrNull()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        return user?.FindFirstValue("FullName")
            ?? user?.FindFirstValue(ClaimTypes.Name)
            ?? user?.Identity?.Name;
    }

    /// <summary>
    /// ADR-020 F4: aplica el candado a un write-path de la reserva. Resuelve el actor del
    /// HttpContext (null en tests) y delega en <see cref="ReservaLockGuard"/>: si la reserva
    /// esta confirmada y no hay autorizacion viva, lanza; si la hay, registra el cambio.
    /// </summary>
    private Task<ReservaEditAuthorization?> EnsureReservaEditableAsync(
        int reservaId, string operation, string? entityType, int? entityId, string? summary, CancellationToken ct)
        => ReservaLockGuard.EnsureCanEditAsync(
            _context, reservaId, operation,
            GetCurrentUserIdOrNull(), GetCurrentUserNameOrNull(),
            entityType, entityId, summary, ct);

    public async Task<ReservaEditAuthorizationDto> CreateEditAuthorizationAsync(
        string publicIdOrLegacyId,
        CreateEditAuthorizationRequest request,
        string actorUserId,
        string? actorUserName,
        bool actorIsAdmin,
        CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // El candado solo existe de Confirmada en adelante; antes la edicion ya es libre.
        if (!ReservaLockGuard.IsLockedStatus(reserva.Status))
            throw new InvalidOperationException(
                "La reserva no esta bajo candado: todavia se puede editar libremente, no necesita autorizacion.");

        var reason = (request.Reason ?? "").Trim();
        if (reason.Length < 10)
            throw new InvalidOperationException("Indica un motivo de la edicion (al menos 10 caracteres).");

        // Quien autoriza: el propio actor si tiene el permiso (Admin lo tiene por bypass de rol),
        // o un autorizante explicito que lo tenga. Mismo modelo de seleccion que RevertStatusAsync.
        // El registro queda SIEMPRE (auto-autorizacion incluida, vale tambien para Admin: INV-020-05).
        string authorizedById;
        string? authorizedByName;

        var actorCanAuthorize = actorIsAdmin
            || await UserHasPermissionAsync(actorUserId, Permissions.ReservasAuthorizeLockedEdit, ct);
        if (actorCanAuthorize)
        {
            authorizedById = actorUserId;
            authorizedByName = actorUserName;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.AuthorizedByUserId))
                throw new InvalidOperationException(
                    "Necesitas que alguien con permiso autorice la edicion de una reserva confirmada. Selecciona un autorizante.");

            var authorizer = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == request.AuthorizedByUserId && u.IsActive, ct)
                ?? throw new InvalidOperationException("El autorizante seleccionado no existe o esta inactivo.");

            var authorizerRoles = await _context.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == authorizer.Id)
                .Join(_context.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
                .ToListAsync(ct);
            var authorizerIsAdmin = authorizerRoles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
            var authorizerCanAuthorize = authorizerIsAdmin || await _context.RolePermissions.AsNoTracking()
                .AnyAsync(p => authorizerRoles.Contains(p.RoleName) && p.Permission == Permissions.ReservasAuthorizeLockedEdit, ct);
            if (!authorizerCanAuthorize)
                throw new InvalidOperationException(
                    "El autorizante seleccionado no tiene permiso para autorizar ediciones bajo candado.");

            authorizedById = authorizer.Id;
            authorizedByName = authorizer.FullName ?? authorizer.UserName ?? authorizer.Id;
        }

        // Regla de unicidad (INV-020-05): a lo sumo UNA autorizacion viva por reserva. Las vigentes
        // se expiran en el acto y la nueva las reemplaza, asi el guard resuelve con un solo lookup.
        var now = DateTime.UtcNow;
        var stillLive = await _context.ReservaEditAuthorizations
            .Where(a => a.ReservaId == id && a.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var previous in stillLive)
            previous.ExpiresAt = now;

        var authorization = new ReservaEditAuthorization
        {
            ReservaId = id,
            RequestedByUserId = actorUserId,
            RequestedByUserName = actorUserName,
            AuthorizedByUserId = authorizedById,
            AuthorizedByUserName = authorizedByName,
            Reason = reason,
            CreatedAt = now,
            ExpiresAt = now.Add(EditAuthorizationWindow),
            ReservaStatusSnapshot = reserva.Status,
        };
        _context.ReservaEditAuthorizations.Add(authorization);
        await _context.SaveChangesAsync(ct);

        return new ReservaEditAuthorizationDto
        {
            PublicId = authorization.PublicId,
            ReservaStatusSnapshot = authorization.ReservaStatusSnapshot ?? reserva.Status,
            RequestedByUserId = authorization.RequestedByUserId,
            RequestedByUserName = authorization.RequestedByUserName,
            AuthorizedByUserId = authorization.AuthorizedByUserId,
            AuthorizedByUserName = authorization.AuthorizedByUserName,
            Reason = authorization.Reason,
            CreatedAt = authorization.CreatedAt,
            ExpiresAt = authorization.ExpiresAt,
        };
    }

    /// <summary>
    /// ADR-027 (hallazgo #10): el dueño da el OK a los cambios de una reserva "confirmada con cambios".
    /// Limpia la bandera y registra quien/cuando. Idempotente: si la reserva no estaba marcada, igual
    /// devuelve el DTO actual sin tocar nada (no es error acusar dos veces).
    /// </summary>
    public async Task<ReservaDto> AcknowledgeChangesAsync(
        string publicIdOrLegacyId, string actorUserId, string? actorUserName, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // No estaba marcada: no-op idempotente. Devolvemos el estado actual sin escribir auditoria falsa.
        if (!reserva.HasUnacknowledgedChanges)
            return await GetReservaByIdAsync(reservaId);

        var now = DateTime.UtcNow;
        reserva.HasUnacknowledgedChanges = false;
        reserva.ChangesPendingSince = null;
        reserva.ChangesAckByUserId = actorUserId;
        reserva.ChangesAckByUserName = actorUserName;
        reserva.ChangesAckAt = now;

        // 2026-06-24: el motor de estados deja el texto del motivo de revision en LastRegressionReason/
        // LastRegressionAt (los campos que el front ya usa para la franja informativa) cuando una reserva
        // confirmada queda "con cambios" porque un servicio dejo de estar resuelto. Como esa marca y su motivo
        // van en lockstep y solo los baja una persona, los limpiamos JUNTO con el OK. Asi no queda una franja
        // "hay servicios sin resolver" colgada despues de que el dueño ya reviso.
        reserva.LastRegressionReason = null;
        reserva.LastRegressionAt = null;

        // ADR-027 (detalle, 2026-06-13): el OK borra TODO el detalle pendiente de la reserva. La auditoria de
        // "quien dio el OK" queda en la reserva (ChangesAckBy*); el detalle de los cambios ya no es "pendiente".
        var pendingChanges = await _context.ReservaPendingChanges
            .Where(c => c.ReservaId == reservaId)
            .ToListAsync(ct);
        if (pendingChanges.Count > 0)
            _context.ReservaPendingChanges.RemoveRange(pendingChanges);

        await _context.SaveChangesAsync(ct);

        // Auditoria: quien dio el OK y cuando. Solo identificadores, sin montos ni datos de pasajeros.
        _logger.LogInformation(
            "ADR-027: Reserva {ReservaId} acusada ('confirmada con cambios' revisada) por {ActorUserId} en {OccurredAt:o}.",
            reservaId, actorUserId, now);

        return await GetReservaByIdAsync(reservaId);
    }

    public async Task DeleteReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        await DeleteReservaAsync(id);
    }

    public async Task<ReservaListPageDto> GetReservasAsync(ReservaListQuery query, CancellationToken cancellationToken)
    {
        var (page, _) = await GetReservasInternalAsync(query, applyOwnerScope: false, cancellationToken);
        return page;
    }

    /// <summary>
    /// B1.15 Fase 2a: variante que decide el scope segun el permiso del user
    /// actual. Si tiene <c>reservas.view_all</c>, devuelve todas. Sino, filtra
    /// por <c>ResponsibleUserId == currentUserId</c>.
    /// </summary>
    public async Task<(ReservaListPageDto Page, string Scope)> GetReservasWithScopeAsync(ReservaListQuery query, CancellationToken cancellationToken)
    {
        // Admin: bypass total — ve todas. El handler de permisos hace lo mismo,
        // pero aca tenemos que decidir el scope para el header X-Permission-Scope.
        var httpContextUser = _httpContextAccessor?.HttpContext?.User;
        var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
        var hasViewAll = isAdmin || await CurrentUserHasPermissionAsync(Permissions.ReservasViewAll, cancellationToken);

        if (hasViewAll)
        {
            var (allPage, _) = await GetReservasInternalAsync(query, applyOwnerScope: false, cancellationToken);
            return (allPage, "all");
        }

        // Sin view_all: filtrar por ResponsibleUserId = currentUserId. Si no
        // hay user resoluble, fail-safe: filtramos por una cadena vacia que no
        // coincide con ningun ResponsibleUserId (=> 0 resultados).
        var (minePage, _) = await GetReservasInternalAsync(query, applyOwnerScope: true, cancellationToken);
        return (minePage, "mine");
    }

    private async Task<(ReservaListPageDto Page, string? OwnerFilterUserId)> GetReservasInternalAsync(ReservaListQuery query, bool applyOwnerScope, CancellationToken cancellationToken)
    {
        // B1.15 Fase 2a: si el caller pidio aplicar scope "mine" y no podemos
        // resolver el user, devolvemos lista vacia (fail-safe). NO ejecutar
        // queries con userId vacio que mantengan la base sin filtrar.
        string? ownerFilterUserId = null;
        if (applyOwnerScope)
        {
            ownerFilterUserId = GetCurrentUserIdOrNull();
            if (string.IsNullOrEmpty(ownerFilterUserId))
            {
                // Sentinel imposible: ningun ResponsibleUserId real coincide con string.Empty.
                ownerFilterUserId = "__no_user__";
            }
        }

        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var summaryBaseQuery = ApplyReservaSearch(_context.Reservas.AsNoTracking(), query.Search);
        if (ownerFilterUserId is not null)
        {
            summaryBaseQuery = summaryBaseQuery.Where(r => r.ResponsibleUserId == ownerFilterUserId);
        }
        
        // B1.15 Fase D' (2026-05-11): filtros de fecha convertidos via AgencyTimezone.
        // El query string entrega DateTime con Kind=Unspecified; las columnas son
        // timestamptz en Postgres y Npgsql tira 500 al comparar Unspecified.
        // Ademas, rango cerrado-abierto [from, to+1day) captura todo el dia local
        // final sin perder eventos posteriores a la medianoche UTC.
        if (query.CreatedFrom.HasValue)
        {
            var fromUtc = AgencyTimezone.ToUtcFromAgencyDay(query.CreatedFrom.Value, isEndOfDay: false);
            summaryBaseQuery = summaryBaseQuery.Where(r => r.CreatedAt >= fromUtc);
        }

        if (query.CreatedTo.HasValue)
        {
            var toUtc = AgencyTimezone.ToUtcFromAgencyDay(query.CreatedTo.Value, isEndOfDay: true);
            // EXCLUSIVE end: rango cerrado-abierto [from, to+1day). Captura todo el dia "to" local.
            summaryBaseQuery = summaryBaseQuery.Where(r => r.CreatedAt < toUtc);
        }

        if (query.TravelFrom.HasValue)
        {
            var fromUtc = AgencyTimezone.ToUtcFromAgencyDay(query.TravelFrom.Value, isEndOfDay: false);
            summaryBaseQuery = summaryBaseQuery.Where(r => r.StartDate.HasValue && r.StartDate.Value >= fromUtc);
        }

        if (query.TravelTo.HasValue)
        {
            var toUtc = AgencyTimezone.ToUtcFromAgencyDay(query.TravelTo.Value, isEndOfDay: true);
            // EXCLUSIVE end para no perder reservas que arrancan al final del dia "to" local.
            summaryBaseQuery = summaryBaseQuery.Where(r => r.StartDate.HasValue && r.StartDate.Value < toUtc);
        }

        // ADR-020: ciclo unico. Los tabs y contadores reflejan las etapas nuevas.
        var filteredQuery = ApplyReservaView(summaryBaseQuery, query.View);

        var summary = new ReservaListSummaryDto
        {
            QuotationCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Quotation, cancellationToken),
            BudgetCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Budget, cancellationToken),
            InManagementCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.InManagement, cancellationToken),
            // ActiveCount = "en gestion, no cerrada ni perdida ni cancelada" (InManagement reemplaza al viejo Sold).
            // ADR-036 (2026-06-21): ToSettle murio; el activo es {InManagement, Confirmed, Traveling}.
            ActiveCount = await summaryBaseQuery.CountAsync(r =>
                r.Status == EstadoReserva.InManagement ||
                r.Status == EstadoReserva.Confirmed ||
                r.Status == EstadoReserva.Traveling,
                cancellationToken),
            ReservedCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Confirmed, cancellationToken),
            OperativeCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Traveling, cancellationToken),
            ClosedCount = await summaryBaseQuery.CountAsync(r =>
                r.Status == EstadoReserva.Closed ||
                r.Status == EstadoReserva.Cancelled ||
                r.Status == "Archived",
                cancellationToken),
            LostCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Lost, cancellationToken),
            // Totales "activos" via patron NEGATIVO (todo lo que NO esta cerrado/cancelado/archivado/perdido).
            // ADR-020: ahora excluimos Lost igual que Cancelled (una reserva Perdida nunca tuvo venta exigible).
            TotalSaleActive = await summaryBaseQuery
                .Where(r => r.Status != EstadoReserva.Closed && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Lost && r.Status != "Archived")
                .SumAsync(r => (decimal?)r.TotalSale, cancellationToken) ?? 0m,
            TotalCostActive = await summaryBaseQuery
                .Where(r => r.Status != EstadoReserva.Closed && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Lost && r.Status != "Archived")
                .SumAsync(r => (decimal?)r.TotalCost, cancellationToken) ?? 0m,
            // ADR-040 (cuenta corriente, review B4): este KPI es el "saldo pendiente de la cartera ACTIVA" y por
            // diseño EXCLUYE Closed (una reserva finalizada salio de la operativa). Con cuenta corriente puede
            // haber deuda viva en reservas Closed (un cliente a cuenta cerro su viaje debiendo). Esa deuda NO
            // desaparece de la cartera: vive en la CUENTA CORRIENTE del cliente / AR canonico
            // (FinancePositionService, cuyo ReceivableDebtStatuses SI incluye Closed con Balance>0). Este KPI de
            // "activos" se deja como esta a proposito; el seguimiento de la deuda de Account cerrados es el AR /
            // la cuenta del cliente, no este contador. Un KPI dedicado de "cartera vencida" (aging) es aditivo y
            // llega con la Fase 2.
            TotalPendingBalance = await summaryBaseQuery
                .Where(r => r.Status != EstadoReserva.Closed && r.Status != EstadoReserva.Cancelled && r.Status != EstadoReserva.Lost && r.Status != "Archived" && r.Balance > 0)
                .SumAsync(r => (decimal?)r.Balance, cancellationToken) ?? 0m
        };
        summary.GrossProfit = summary.TotalSaleActive - summary.TotalCostActive;

        var reservasQuery = ApplyReservaOrdering(filteredQuery, query)
            .Select(f => new ReservaListDto
            {
                PublicId = f.PublicId,
                NumeroReserva = f.NumeroReserva,
                Name = f.Name,
                Status = f.Status,
                CustomerName = f.Payer != null ? f.Payer.FullName : string.Empty,
                ResponsibleUserId = f.ResponsibleUserId,
                ResponsibleUserName = f.ResponsibleUserName,
                CreatedAt = f.CreatedAt,
                StartDate = f.StartDate,
                EndDate = f.EndDate,
                PassengerCount = f.Passengers.Count,
                TotalCost = f.TotalCost,
                TotalPaid = f.TotalPaid,
                TotalSale = f.TotalSale,
                Balance = f.Balance
            })
            .AsQueryable();

        var paged = await reservasQuery.ToPagedResponseAsync(query, cancellationToken);

        // B1.15 Fase 2a (Decision 4): si el user actual NO tiene
        // cobranzas.see_cost, ocultar TotalCost (solo ven precio de venta).
        // Admin bypass: si es Admin, no se enmascara.
        bool seeCost = true;
        var httpContextUser = _httpContextAccessor?.HttpContext?.User;
        var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
        if (!isAdmin)
        {
            seeCost = await CurrentUserHasPermissionAsync(Permissions.CobranzasSeeCost, cancellationToken);
        }

        foreach (var reserva in paged.Items)
        {
            ApplyEconomicFlags(reserva, settings);
            if (!seeCost)
            {
                reserva.TotalCost = 0m;
            }
        }

        // El summary tambien expone costos agregados — enmascarar si no aplica.
        if (!seeCost)
        {
            summary.TotalCostActive = 0m;
            summary.GrossProfit = 0m;
        }

        // ADR-021 Capa 5: detalle por moneda del listado. A diferencia del detalle (que recalcula con el
        // calculator desde las colecciones cargadas), el listado lee la tabla hija materializada
        // ReservaMoneyByCurrency en UNA sola query batcheada por los PublicId de la pagina (evita N+1 y no
        // trae todas las colecciones de cada reserva). El TotalCost por moneda se enmascara igual que el escalar.
        await FillPorMonedaForListAsync(paged.Items, seeCost, cancellationToken);

        // ADR-037 Capa carril de facturacion: estado de facturacion derivado por fila, en UNA query agrupada
        // por reserva (sin N+1). El facturado neto no es costo: no se enmascara por ver-costos (es lo mismo
        // que ya se muestra en el cuadre del detalle).
        await FillInvoicingStatusForListAsync(paged.Items, cancellationToken);

        // Contexto de plata real en las filas ANULADAS (saldo a favor pendiente / multa cobrable / dato roto).
        // Una sola query batcheada por pagina, y solo si hay filas anuladas (ver el helper). Ver ReservationDebtRules.
        await FillCancelledMoneyContextForListAsync(paged.Items, cancellationToken);

        var page = ReservaListPageDto.Create(paged.Items, paged.Page, paged.PageSize, paged.TotalCount, summary);
        return (page, ownerFilterUserId);
    }

    /// <summary>
    /// ADR-021 Capa 5: llena <c>PorMoneda</c>/<c>EsMultimoneda</c> de cada fila del listado leyendo la
    /// tabla hija materializada <c>ReservaMoneyByCurrency</c>. Una sola query por los PublicId de la pagina
    /// (no recalcula ni trae colecciones por reserva). Si <paramref name="seeCost"/> es false, el costo de
    /// cada moneda se enmascara a 0 (mismo criterio que el escalar TotalCost).
    /// </summary>
    private async Task FillPorMonedaForListAsync(
        IReadOnlyList<ReservaListDto> items, bool seeCost, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var publicIds = items.Select(i => i.PublicId).ToList();

        // Una fila por (reserva, moneda). Join explicito contra Reservas (no nav implicita) para resolver
        // el PublicId con el que matchear el DTO y correr igual en Postgres e InMemory.
        var rows = await (
            from row in _context.ReservaMoneyByCurrency.AsNoTracking()
            join reservaPadre in _context.Reservas.AsNoTracking() on row.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
            select new
            {
                ReservaPublicId = reservaPadre.PublicId,
                row.Currency,
                row.TotalSale,
                row.ConfirmedSale,
                row.TotalCost,
                row.TotalPaid,
                row.Balance
            }).ToListAsync(cancellationToken);

        var byReserva = rows
            .GroupBy(row => row.ReservaPublicId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in items)
        {
            if (!byReserva.TryGetValue(item.PublicId, out var reservaRows))
            {
                // Sin filas hijas (reserva nueva sin servicios, saldada en 0, o legacy sin backfill): no hay
                // detalle por moneda. Usamos el escalar de la fila para deuda/saldo a favor, y las senales de
                // actividad (vendio algo / cobro algo) para distinguir "SinMovimientos" de "Saldado".
                // H1 (2026-06-24): una reserva NUEVA en gestion sin cargos ni cobros NO debe decir "pagada".
                //
                // H1b (2026-06-24, FIX): aca NO hay ConfirmedSale escalar en el DTO (ReservaListDto solo trae
                // TotalSale). Pero el Balance escalar YA se calcula con la venta exigible (ConfirmedSale -
                // TotalPaid), asi que la senal coherente de "hubo cargos exigibles" es Balance != 0 (no
                // TotalSale, que es la venta cotizada). Asi una reserva cotizada-no-confirmada sin cobros
                // (Balance 0, TotalPaid 0) queda en "SinMovimientos", igual que en los paths con filas hijas.
                bool hasCharges = item.Balance != 0m;
                bool hasPayments = item.TotalPaid > 0m;
                item.CollectionStatus = ReservaCollectionStatus.Derive(
                    new[] { new ReservaCollectionLine(item.Balance, hasCharges, hasPayments) });
                continue;
            }

            item.PorMoneda = reservaRows
                .OrderBy(row => row.Currency, StringComparer.Ordinal)
                .Select(row => new ReservaMoneyLineDto
                {
                    Currency = row.Currency,
                    TotalSale = row.TotalSale,
                    ConfirmedSale = row.ConfirmedSale,
                    // NO setear Margin aqui sin guard seeCost: el margen filtra el costo por resta (venta - costo),
                    // asi que exponerlo a un caller sin cobranzas.see_cost reabriria la fuga que el masking cierra.
                    // El listado hoy es seguro porque deja Margin en su default (0); este comentario evita que un
                    // cambio futuro lo setee sin pasar por el enmascarado. Idem TotalCost: ya va gateado por seeCost.
                    TotalCost = seeCost ? row.TotalCost : 0m,
                    TotalPaid = row.TotalPaid,
                    Balance = row.Balance
                })
                .ToList();

            item.EsMultimoneda = item.PorMoneda.Count > 1;

            // ADR-033 (E7/A5): estado de cobro derivado del saldo POR MONEDA de las filas hijas.
            // H1 (2026-06-24): pasamos tambien las senales de actividad (cargos / cobros) para que una reserva
            // con todo en 0 pero SIN movimientos diga "SinMovimientos" y no "Saldado" (que el front pinta "pagada").
            // H1b (2026-06-24, FIX): la senal de CARGOS usa la venta EXIGIBLE (ConfirmedSale), NO la cotizada
            // (TotalSale). El Balance se calcula con ConfirmedSale; usar TotalSale dejaba "pagada" a una reserva
            // con servicios con precio pero NO confirmados y sin cobros (ver detalle en el path de detalle).
            item.CollectionStatus = ReservaCollectionStatus.Derive(
                item.PorMoneda.Select(line => new ReservaCollectionLine(
                    line.Balance,
                    hasCharges: line.ConfirmedSale > 0m,
                    hasPayments: line.TotalPaid > 0m)));
        }
    }

    /// <summary>
    /// ADR-037 (2026-06-21): llena <c>InvoicingStatus</c> de cada fila del listado con el carril de
    /// facturacion DERIVADO (mismo que el detalle). Para no recalcular el cuadre por reserva (N+1), suma el
    /// FACTURADO NETO de toda la pagina en UNA query agrupada por reserva (facturas + ND - NC, contando todo
    /// comprobante con CAE aprobado <c>Resultado == "A"</c> AUNQUE este anulado — misma regla
    /// <c>ReservaInvoicingCuadreCalculator.CountsInNetBilled</c> que el detalle y el extracto) y deriva el
    /// estado por reserva. La factura anulada sigue sumando y su Nota de Credito resta: la anulacion se
    /// cuenta una sola vez (sin esto, full daba -monto y parcial restaba de mas).
    ///
    /// <para>Las reservas sin comprobantes vivos no aparecen en el agregado: quedan en "NotInvoiced" (el
    /// default del DTO), que es lo correcto. El <c>vendido</c> sale del escalar <c>TotalSale</c> que cada
    /// fila ya trae (consistente con la limitacion escalar v1 del carril).</para>
    /// </summary>
    private async Task FillInvoicingStatusForListAsync(
        IReadOnlyList<ReservaListDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var publicIds = items.Select(i => i.PublicId).ToList();

        // Tipos de NOTA DE CREDITO (restan en el cuadre). Mismo conjunto que la bandeja de facturacion
        // (InvoiceService) y que InvoiceComprobanteHelpers: A=3, B=8, C=13, M=53. El resto (facturas y ND) suma.
        var creditNoteTipos = new[] { 3, 8, 13, 53 };

        // Una fila por reserva con su facturado neto. Join explicito contra Reservas (no nav implicita) para
        // resolver el PublicId con el que matchear el DTO y correr igual en Postgres e InMemory.
        var facturadoByReserva = await (
            from invoice in _context.Invoices.AsNoTracking()
            join reservaPadre in _context.Reservas.AsNoTracking() on invoice.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
                  // Regla unica del facturado neto (CountsInNetBilled, inline porque EF no traduce el helper):
                  // cuenta el CAE aprobado AUNQUE este anulado (Succeeded). NO se excluye Succeeded: la factura
                  // anulada sigue sumando y su Nota de Credito resta -> la anulacion se cuenta UNA sola vez.
                  && invoice.Resultado == "A"
            group invoice by reservaPadre.PublicId into byReserva
            select new
            {
                ReservaPublicId = byReserva.Key,
                FacturadoNeto = byReserva.Sum(invoice =>
                    creditNoteTipos.Contains(invoice.TipoComprobante)
                        ? -invoice.ImporteTotal
                        : invoice.ImporteTotal)
            }).ToListAsync(cancellationToken);

        var netoByReserva = facturadoByReserva
            .ToDictionary(row => row.ReservaPublicId, row => row.FacturadoNeto);

        foreach (var item in items)
        {
            var facturadoNeto = netoByReserva.TryGetValue(item.PublicId, out var neto) ? neto : 0m;
            item.InvoicingStatus = ReservaInvoicingStatus.Derive(item.TotalSale, facturadoNeto);
        }
    }

    private async Task<int> ResolveRequiredIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken)
        where TEntity : class, IHasPublicId
    {
        var resolved = await _context.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado");
    }

    private static Passenger MapPassenger(PassengerUpsertRequest passenger)
    {
        return new Passenger
        {
            FullName = passenger.FullName,
            DocumentType = passenger.DocumentType,
            DocumentNumber = passenger.DocumentNumber,
            BirthDate = passenger.BirthDate,
            PassportExpiry = passenger.PassportExpiry,
            Nationality = passenger.Nationality,
            Phone = passenger.Phone,
            Email = passenger.Email,
            Gender = passenger.Gender,
            Notes = passenger.Notes
        };
    }

    private static Payment MapPayment(ReservationPaymentUpsertRequest payment)
    {
        return new Payment
        {
            Amount = payment.Amount,
            PaidAt = payment.PaidAt,
            Method = payment.Method,
            Reference = payment.Reference,
            Notes = payment.Notes
        };
    }

    public async Task<ReservaDto> GetReservaByIdAsync(int id)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(CancellationToken.None);
        var file = await _context.Reservas
            .AsNoTracking()
            .Include(f => f.Payer)
            .Include(f => f.Passengers)
            .Include(f => f.Payments)
            .ThenInclude(p => p.Receipt)
            .Include(f => f.Invoices).ThenInclude(i => i.OriginalInvoice)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments).ThenInclude(fs => fs.Supplier)
            .Include(f => f.HotelBookings).ThenInclude(hb => hb.Supplier)
            .Include(f => f.TransferBookings).ThenInclude(tb => tb.Supplier)
            .Include(f => f.PackageBookings).ThenInclude(pb => pb.Supplier)
            .Include(f => f.AssistanceBookings).ThenInclude(ab => ab.Supplier)
            // ADR-027 (detalle "confirmada con cambios"): cargamos los cambios pendientes para exponerlos en el DTO.
            .Include(f => f.PendingChanges)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (file == null) 
        {
            throw new KeyNotFoundException($"File with ID {id} not found locally");
        }

        var dto = _mapper.Map<ReservaDto>(file);
        ApplyEconomicFlags(dto, settings);

        // ADR-021 Capa 5: detalle de plata por moneda. Se recalcula on-read con el calculator (fuente
        // unica de la cuenta) desde las colecciones ya cargadas; no toca la tabla hija (eso es solo para
        // agregados cross-reserva en SQL). El enmascarado de TotalCost por moneda se aplica mas abajo en
        // ApplyCostMaskingAsync, junto con el escalar, para no dejar costos visibles por una moneda.
        var moneySummary = ReservaMoneyCalculator.Calculate(file);

        // Contexto de plata real en reservas ANULADAS (saldo a favor pendiente / multa cobrable / dato roto).
        // Null para el resto de los estados. Ver ReservationDebtRules. Necesita saber si hay una Nota de Debito
        // de multa viva -> una query chica al aggregate de cancelacion (el detalle no es hot path y ya hace
        // varias queries pequenas). Se hace SOLO si la reserva esta anulada (adentro del helper). El monto
        // pendiente de la multa se calcula ND-BASED (contra la propia Nota de Debito), no contra el saldo de la
        // reserva — ver el XML-doc de AggregatePendingPenaltiesByCurrency.
        var cancelledMoneyInfo = await DeriveCancelledMoneyContextAsync(
            file.Id, file.Status, file.Balance, CancellationToken.None);
        dto.CancelledMoneyContext = cancelledMoneyInfo.Context;
        dto.CancelledPenaltyAmount = cancelledMoneyInfo.PenaltyAmount;
        dto.CancelledPenaltyCurrency = cancelledMoneyInfo.PenaltyCurrency;
        dto.CancelledPenaltiesByCurrency = cancelledMoneyInfo.PenaltiesByCurrency.ToList();

        dto.EsMultimoneda = moneySummary.EsMultimoneda;
        dto.PorMoneda = moneySummary.PorMoneda.Values
            .OrderBy(line => line.Currency, StringComparer.Ordinal)
            .Select(line => new ReservaMoneyLineDto
            {
                Currency = line.Currency,
                TotalSale = line.TotalSale,
                ConfirmedSale = line.ConfirmedSale,
                TotalCost = line.TotalCost,
                TotalPaid = line.TotalPaid,
                Balance = line.Balance,
                // Margen por moneda (venta confirmada - costo). Se carga CRUDO aca; el enmascarado por permiso
                // lo hace ApplyCostMaskingAsync junto al TotalCost (es dato de costo por resta).
                Margin = line.Margin
            })
            .ToList();

        // Margen escalar de la reserva (venta confirmada - costo). Crudo aca; se enmascara junto al TotalCost.
        dto.TotalMargin = moneySummary.TotalMargin;

        // ADR-033 (E7/A5): estado de cobro derivado del saldo POR MONEDA (no del escalar, que mezcla ARS+USD).
        // H1 (2026-06-24): con senales de actividad (cargos / cobros) para distinguir "SinMovimientos" de "Saldado".
        // Una reserva nueva en gestion sin servicios ni cobros debe decir "SinMovimientos", no "pagada".
        //
        // H1b (2026-06-24, FIX del fix): la senal de CARGOS debe ser la venta EXIGIBLE (ConfirmedSale), NO la
        // venta cotizada (TotalSale). El Balance se calcula con ConfirmedSale (Balance = ConfirmedSale -
        // TotalPaid), asi que usar TotalSale para "hubo cargos" era incoherente: una reserva con servicios
        // CON PRECIO pero NO confirmados por el operador y CERO cobros tiene TotalSale>0 (hasCharges=true) pero
        // ConfirmedSale=0 -> Balance=0 -> caia en "Saldado" y el front la pintaba "pagada" sin haber cobrado
        // nada. Con ConfirmedSale ese caso queda en "SinMovimientos", que es lo correcto.
        dto.CollectionStatus = ReservaCollectionStatus.Derive(
            dto.PorMoneda.Select(line => new ReservaCollectionLine(
                line.Balance,
                hasCharges: line.ConfirmedSale > 0m,
                hasPayments: line.TotalPaid > 0m)));

        // P3 (cuadre de facturacion): cuanto se facturo NETO al cliente (facturas + ND - NC,
        // solo comprobantes con CAE vivo y no anulados) y cuanto queda disponible respecto de
        // lo vendido (TotalSale, la fuente unica). La UI usa estos numeros para avisar si se
        // factura de mas. La cuenta vive en ReservaInvoicingCuadreCalculator (probada, un solo lugar).
        var cuadre = ReservaInvoicingCuadreCalculator.Calculate(
            file.TotalSale,
            file.Invoices.Select(i => new CuadreInvoiceLine(
                i.TipoComprobante,
                i.ImporteTotal,
                // Regla unica: cuenta el CAE aprobado aunque este anulado; la NC hace la resta (sin doble conteo).
                IsLive: ReservaInvoicingCuadreCalculator.CountsInNetBilled(i.Resultado))));
        dto.FacturadoNeto = cuadre.FacturadoNeto;
        dto.DisponibleParaFacturar = cuadre.Disponible;

        // ADR-037 (carril de facturacion DERIVADO): estado de facturacion calculado del cuadre escalar
        // (vendido vs facturado neto). Eje independiente del cobro y del estado operativo. Fuente unica:
        // ReservaInvoicingStatus.Derive (probado, un solo lugar), espejo de CollectionStatus.
        dto.InvoicingStatus = ReservaInvoicingStatus.Derive(file.TotalSale, cuadre.FacturadoNeto);

        // (2026-06-24): hay una factura EN PROCESO (encolada, esperando CAE). El cuadre de arriba NO la cuenta
        // (solo suma Resultado="A"), asi que sin este flag el front mostraria "Sin facturar" y volveria a
        // ofrecer "Emitir factura" sobre una reserva que ya tiene una en vuelo -> el usuario reemite y recien
        // ahi rebota el 409. Espejo EXACTO del guard de InvoiceService.CreatePendingInvoice
        // (Resultado=="PENDING" && AnnulmentStatus != Succeeded). file.Invoices ya viene cargado (lo usa el cuadre).
        dto.HasInvoiceInProgress = file.Invoices.Any(i =>
            i.Resultado == "PENDING" && i.AnnulmentStatus != AnnulmentStatus.Succeeded);

        // Sugerencia de factura al cancelar UN servicio (en vez de que el usuario adivine en un
        // desplegable con varias facturas activas). Query batcheada aparte, ver el metodo.
        await PopulateInvoiceServicePublicIdsAsync(file, dto, CancellationToken.None);

        // ADR-037 / cuadre POR MONEDA (2026-06-22): el escalar FacturadoNeto/DisponibleParaFacturar mezcla
        // monedas en multimoneda. Aca calculamos el facturado neto de CADA moneda por separado (facturas + ND
        // - NC vivas, agrupadas por la moneda ISO del comprobante) y lo cargamos en su linea de PorMoneda,
        // con "falta facturar" = TotalSale de esa moneda - facturado de esa moneda (MISMO criterio que el
        // escalar, que usa TotalSale y no ConfirmedSale). La cuenta vive en el calculator (fuente unica).
        PopulateFacturadoPorMoneda(file, dto.PorMoneda);

        // ADR-037 (flag del aviso "Debe — no viaja", ADR-036): hay deuda del cliente y la salida cae en la
        // ventana configurada, con la notificacion habilitada. Reusa la MISMA config y la MISMA regla de
        // ventana que el job nocturno (ReservaUnpaidAlertWindow, fuente unica). "settings" ya se leyo arriba
        // (una sola lectura por request).
        dto.IsWithinUnpaidAlertWindow = ReservaUnpaidAlertWindow.IsWithin(
            notificationsEnabled: settings.EnableUpcomingUnpaidReservationNotifications,
            alertDays: settings.UpcomingUnpaidReservationAlertDays,
            balance: file.Balance,
            startDate: file.StartDate,
            today: DateTime.UtcNow.Date);

        // Sugerencia de fechas computadas desde los servicios cargados — la UI las
        // usa para pre-rellenar inputs cuando StartDate/EndDate estan en null.
        // Costo: 5 queries chicas en una operacion de detalle (no es hot path).
        var (suggestedStart, suggestedEnd) = await ReservaScheduleCalculator.ComputeAsync(_context, file.Id);
        dto.SuggestedStartDate = suggestedStart;
        dto.SuggestedEndDate = suggestedEnd;

        // ADR-017 (pill "creado en esta venta"): el detalle NO incluye la nav Rate de los servicios
        // (a proposito: incluirla cambiaria campos preexistentes como RatePublicId/IsPriceSynced en
        // este response). Se resuelve aparte con UNA query batcheada sobre los RateId cargados.
        await StampProductCreatedInSaleAsync(file, dto, CancellationToken.None);

        // ADR-027 (detalle "confirmada con cambios"): mapeamos el detalle de cambios pendientes a mano (no por
        // AutoMapper) para poder enmascarar el costo segun permiso dentro de ApplyCostMaskingAsync. Los montos
        // se cargan crudos aca; si el cambio es de COSTO y el caller no ve costos, se ponen en 0 mas abajo.
        dto.PendingChanges = file.PendingChanges
            .OrderBy(c => c.ChangedAt)
            .Select(c => new ReservaPendingChangeDto
            {
                ServiceType = c.ServiceType,
                ServiceDescription = c.ServiceDescription,
                ServicePublicId = c.ServicePublicId,
                Field = c.Field,
                OldValue = c.OldValue,
                NewValue = c.NewValue,
                Currency = c.Currency,
                ChangedByUserName = c.ChangedByUserName,
                ChangedAt = c.ChangedAt,
                ValuesMasked = false,
            })
            .ToList();

        // B1.15 Fase 2a (Decision 4): mascara de costos para roles sin
        // cobranzas.see_cost. Admin bypass.
        await ApplyCostMaskingAsync(dto, CancellationToken.None);

        // ADR-020 F4 (candado): indicador de "candado destrabado". El frontend muestra
        // "destrabada por unos minutos" (en vez de "pedi autorizacion") cuando hay una autorizacion
        // de edicion VIVA. "Viva" = ExpiresAt > ahora, mismo criterio que el guard del candado
        // (ReservaEditAuthorizations, INV-020-05). Calculado, sin columna nueva.
        var nowForAuth = DateTime.UtcNow;
        var liveAuthExpiry = await _context.ReservaEditAuthorizations
            .AsNoTracking()
            .Where(a => a.ReservaId == file.Id && a.ExpiresAt > nowForAuth)
            .OrderByDescending(a => a.ExpiresAt)
            .Select(a => (DateTime?)a.ExpiresAt)
            .FirstOrDefaultAsync();
        dto.HasLiveEditAuthorization = liveAuthExpiry.HasValue;
        dto.EditAuthorizationExpiresAt = liveAuthExpiry;

        // ADR-025 (read-model cancelacion parcial): motivo del candado fiscal que impide cancelar CUALQUIER
        // servicio (factura CAE viva o voucher emitido), o null si se puede cancelar. El front pre-bloquea
        // los casilleros con esto. Reusamos el guard (fuente unica) en vez de recalcular: lo que se ve es
        // exactamente lo que el backend enforza al cancelar. Costo: 2 AnyAsync chicos en el detalle (no hot
        // path; misma magnitud que la query de autorizacion de arriba).
        dto.ServiceCancellationBlockReason =
            await MutationGuards.GetReservaCancellationBlockReasonAsync(_context, file.Id, CancellationToken.None);

        // ADR-035 (2026-06-19): bloque de CAPACIDADES (fuente unica que el front lee para apagar botones con
        // motivo) y MONEDA PRINCIPAL del cobro. Se calculan al final, con la plata ya armada (PorMoneda) y los
        // comprobantes ya incluidos (file.Invoices).
        //
        // ADR-036 (2026-06-22): "CAE vivo" usa el MISMO criterio que los guards de mutacion fiscal
        // (MutationGuards.HasLiveCaeForReserva) = factura NO-NC con CAE asignado y AnnulmentStatus != Succeeded.
        // Antes este calculo usaba solo (Resultado == "A"), que cuenta tambien las Notas de Credito (una NC
        // tiene su propio CAE y Resultado "A"). Eso era un FALSO POSITIVO para una reserva cuya unica huella
        // fiscal es una NC (factura original ya anulada): la marcaba "con factura viva" y bloqueaba de mas. El
        // criterio correcto EXCLUYE las NC (una NC resta, no mantiene viva la reserva). Esto alimenta CanCancel,
        // RequiresInvoiceAnnulmentToCancel y la nueva CanCorrectTravelingEntry. EF no traduce el helper
        // InvoiceComprobanteHelpers.IsCreditNote a SQL, pero aca operamos sobre file.Invoices ya en memoria, asi
        // que podemos llamarlo directo.
        var hasLiveCae = file.Invoices.Any(i =>
            !InvoiceComprobanteHelpers.IsCreditNote(i.TipoComprobante)
            && !string.IsNullOrEmpty(i.CAE)
            && i.AnnulmentStatus != AnnulmentStatus.Succeeded);

        // ADR-036 (2026-06-22): "voucher emitido vivo" (no anulado) alimenta CanCorrectTravelingEntry: no se
        // saca de viaje una reserva con voucher vivo (hay que anularlo primero). "Vivo" = Status en
        // {Issued, UploadedExternal} (los dos estados de un voucher entregado; Draft/PendingAuthorization no
        // estan emitidos, Revoked esta anulado). Una sola query AnyAsync chica, misma magnitud que las de
        // arriba; no se incluye la coleccion de vouchers en la entidad para no agrandar el detalle.
        var hasLiveVoucher = await _context.Vouchers.AsNoTracking().AnyAsync(
            v => v.ReservaId == file.Id
                && (v.Status == VoucherStatuses.Issued || v.Status == VoucherStatuses.UploadedExternal),
            CancellationToken.None);

        // ADR-036 (2026-06-21): "tiene cobros vivos" alimenta CanCancel (una reserva con plata viva no admite
        // baja simple: hay que anularla por NC/ND). Cuenta CUALQUIER pago no soft-deleted, INCLUIDOS los pagos
        // puente (AffectsCash=false: sobrepago a saldo a favor / saldo a favor aplicado): aunque no movieron
        // caja, son rastro de plata que hay que deshacer formalmente. file.Payments ya viene incluido arriba.
        var hasAnyLivePayment = file.Payments.Any(p => !p.IsDeleted);

        // Issue 2a (2026-06-28): el boton "Confirmar multa / Cerrar sin multa" exige el permiso
        // cancellations.classify_agency_penalty (o Admin). Antes la capacidad ignoraba el permiso: un usuario sin
        // el permiso VEIA los botones, los clickeaba, y el backend rebotaba 409 (defensa final). Ahora lo gateamos
        // ACA — el unico lugar del armado de capacidades que conoce la identidad del caller — para que esos
        // usuarios NO vean los botones. Es una compuerta de UI; el endpoint confirm/waive revalida el permiso
        // server-side igual (la defensa final no se quita). En tests sin HttpContext/resolver da false (sin
        // identidad no se ofrece la accion sensible, comportamiento seguro).
        var httpContextUser = _httpContextAccessor?.HttpContext?.User;
        var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
        var userCanClassifyOperatorPenalty = isAdmin
            || await CurrentUserHasPermissionAsync(Permissions.CancellationsClassifyAgencyPenalty, CancellationToken.None);

        // Spec "el paso de multa vive en la ficha" (A2, 2026-07-08): read-model DETALLADO del paso de la multa del
        // operador (encolada / fallida / trabada por moneda / emitida / cerrada sin multa / pendiente) con monto,
        // moneda y los botones habilitados. Es la UNICA consulta a la cancelacion en el detalle: de su State
        // DERIVAMOS el RESULTADO grueso (None/Pending/Confirmed/Waived) que consumen las capacidades, en vez de
        // re-consultar la cancelacion por segunda vez (N2, 2026-07-08). Solo en el DETALLE (no en el listado: seria
        // N+1). Si el service no esta inyectado (tests unitarios sin cancelaciones), queda State="None" -> outcome
        // None: ni botones ni cartel (seguro).
        var operatorPenaltyOutcome = OperatorPenaltyOutcome.None;
        if (_cancellationService is not null)
        {
            // ADR-044 T1 (2026-07-10): pedimos SOLO la version LISTA (un elemento por operador con multa en juego,
            // ADR-025) y de ella derivamos tanto el campo singular legacy como el outcome grueso — asi el calculo
            // del operador PRINCIPAL corre UNA sola vez por request (antes se llamaba al singular Y a la lista, que
            // por dentro vuelve a llamar al singular). El PRIMER elemento de la lista ES exactamente el resultado
            // del singular para el operador principal (garantia de la implementacion + test de paridad), asi que
            // el campo legacy dto.OperatorPenaltySituation queda byte-identico al de antes en el caso mono-operador.
            //
            // Defensa (2026-07-08): la interfaz promete "nunca null" (la implementacion real siempre devuelve al
            // menos lista vacia), pero un doble de test (mock parcial) puede devolver null con MockBehavior.Loose.
            // Si eso pasa, nos quedamos con los defaults del DTO (singular "None" + lista vacia): la ficha no
            // revienta, simplemente no muestra el paso de la multa (seguro, igual que "sin cancelacion vigente").
            // ADR-044 T2 Addendum (security, 2026-07-10): el desglose de cargos del operador es dato de COSTO.
            // Resolvemos cobranzas.see_cost (Admin bypass incluido en CostMasking) y se lo pasamos al read-model,
            // que enmascara la lista Charges cuando el caller no puede ver costo. Mismo helper que usa el resto del
            // masking de costo del detalle de la reserva.
            var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, CancellationToken.None);
            var situations = await _cancellationService.GetOperatorPenaltySituationsAsync(
                file.PublicId, userCanClassifyOperatorPenalty, isCallerAdmin: isAdmin, CancellationToken.None,
                canSeeCost: canSeeCost);
            if (situations is not null && situations.Count > 0)
            {
                dto.OperatorPenaltySituations = situations.ToList();
                // El singular legacy = el primer elemento (el operador principal). El State es el token que produjo
                // OperatorPenaltySituationState.ToString() (lo controlamos nosotros): parsearlo de vuelta al enum y
                // colapsarlo al outcome grueso es la fuente unica del mapeo.
                var primary = situations[0];
                dto.OperatorPenaltySituation = primary;
                if (Enum.TryParse<OperatorPenaltySituationState>(primary.State, out var situationState))
                    operatorPenaltyOutcome = OperatorPenaltySituationRules.ToOutcome(situationState);
            }
        }

        // Solo "Pending" + permiso habilita los botones. El resto de los outcomes (Confirmed/Waived/None) ya no
        // son "pendientes": el boton no aplica. Derivar de un unico outcome mantiene una sola verdad.
        var hasPendingOperatorPenalty =
            operatorPenaltyOutcome == OperatorPenaltyOutcome.Pending
            && userCanClassifyOperatorPenalty;

        // (2026-06-26): para que canDelete NO mienta, miramos si hay un servicio confirmado con el operador (mismo
        // bloqueo que DeleteGuards). Solo importa en pre-venta (Cotizacion/Presupuesto): es el unico estado donde
        // canDelete podria dar true. Fuera de pre-venta el borrado ya esta bloqueado por estado, asi que evitamos
        // la query de 6 tablas (queda en false, no cambia el resultado de la capacidad).
        var isPreSaleForDelete =
            string.Equals(file.Status, EstadoReserva.Quotation, StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.Status, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase);
        var hasOperatorConfirmedService = isPreSaleForDelete
            && await DeleteGuards.ReservaHasOperatorConfirmedServiceAsync(_context, file.Id, CancellationToken.None);

        var capabilityContext = new ReservaCapabilityContext(
            Status: file.Status,
            Balance: file.Balance,
            HasLiveCae: hasLiveCae,
            HasLiveVoucher: hasLiveVoucher,
            HasLiveEditAuth: dto.HasLiveEditAuthorization,
            HasAnyPayment: hasAnyLivePayment,
            HasPendingOperatorPenalty: hasPendingOperatorPenalty,
            HasOperatorConfirmedService: hasOperatorConfirmedService,
            OperatorPenaltyOutcome: operatorPenaltyOutcome);
        dto.Capabilities = MapCapabilities(ReservaCapabilityPolicy.For(capabilityContext));

        // Derivado de "tiene CAE vivo": el front explica por que cancelar exige pasar por la NC primero.
        dto.RequiresInvoiceAnnulmentToCancel = hasLiveCae;

        // (2026-06-25) Flujo unificado de "Anular reserva": discriminador del CASO de anulacion + monto que
        // quedaria como saldo a favor (caso 3). El backend decide; el front solo lo lee para el cartel correcto.
        // Reusa las mismas senales que las capacidades (estado + plata viva), sin estado ni columna nueva.
        PopulateCancellationCase(dto, file.Status, hasLiveCae, hasAnyLivePayment);

        // ADR-036 (2026-06-22): "En corrección" = volvio a Confirmada con la fecha de salida borrada tras un
        // "Sacar de viaje" (StartDate null). Derivado, sin estado nuevo. El front muestra el chip "En corrección".
        dto.IsUnderCorrection =
            string.Equals(file.Status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase)
            && !file.StartDate.HasValue;

        // ADR-035 Decision 2 / C5: la moneda principal (default del cobro) la decide el backend, nunca el front.
        dto.MonedaPrincipal = ResolvePrimaryCurrency(dto.PorMoneda);

        return dto;
    }

    /// <summary>
    /// (2026-06-25) Calcula el discriminador del CASO de anulacion (Part B del flujo unificado de "Anular
    /// reserva") y, para el caso de saldo a favor, el monto de cobros vivos POR MONEDA. El front lee esto para
    /// mostrar el cartel correcto; la logica de plata la decide SIEMPRE el backend.
    ///
    /// <para>El criterio es el MISMO que usan las capacidades canCancel/canAnnul (estado + plata viva), para que
    /// no haya dos verdades. Pre-venta = Cotizacion/Presupuesto; los estados terminales y En viaje no admiten
    /// anulacion (NotApplicable). En firme: con factura CAE viva -> NC; sin factura pero con cobros -> saldo a
    /// favor; sin nada -> baja directa.</para>
    /// </summary>
    private static void PopulateCancellationCase(ReservaDto dto, string status, bool hasLiveCae, bool hasAnyLivePayment)
    {
        // Pre-venta: se descarta / marca Perdida. No hay plata viva que conservar (un presupuesto con cobros es
        // un estado raro; igual su anulacion no pasa por aca, el front ofrece "descartar").
        if (string.Equals(status, EstadoReserva.Quotation, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase))
        {
            dto.CancellationCase = ReservaCancellationCases.PreSale;
            return;
        }

        // En firme (InManagement / Confirmed) = los unicos estados donde la anulacion decide entre los 3 caminos
        // de plata. Cualquier otro (Traveling, Closed, Lost, Cancelled, PendingOperatorRefund) no se anula.
        var isFirmAnnulable =
            string.Equals(status, EstadoReserva.InManagement, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, EstadoReserva.Confirmed, StringComparison.OrdinalIgnoreCase);
        if (!isFirmAnnulable)
        {
            dto.CancellationCase = ReservaCancellationCases.NotApplicable;
            return;
        }

        // Con factura con CAE vivo -> camino formal con Nota de Credito (caso 4). Manda sobre los cobros: aunque
        // haya cobros, si hay factura la anulacion correcta es la NC.
        if (hasLiveCae)
        {
            dto.CancellationCase = ReservaCancellationCases.CreditNote;
            return;
        }

        // Sin factura pero con cobros vivos -> Cancelada + saldo a favor (caso 3). Exponemos el monto por moneda
        // (lo PAGADO en cada moneda) para el cartel. Es lo mismo que convertira AnnulWithPaymentsToCreditAsync.
        if (hasAnyLivePayment)
        {
            dto.CancellationCase = ReservaCancellationCases.PaymentsToCredit;
            dto.CancellationCreditByCurrency = dto.PorMoneda
                .Where(line => line.TotalPaid > 0m)
                .Select(line => new ReservaCancellationCreditLineDto
                {
                    Currency = line.Currency,
                    Amount = line.TotalPaid,
                })
                .ToList();
            return;
        }

        // Sin factura y sin cobros -> baja directa a Cancelada (caso 2).
        dto.CancellationCase = ReservaCancellationCases.DirectCancel;
    }

    /// <summary>
    /// ADR-035 Decision 2 / C5 (2026-06-19): elige la moneda PRINCIPAL de la reserva para preseleccionar en
    /// el cobro. La regla vive en el dominio (<see cref="ReservaPrimaryCurrency"/>) y la reusa tambien la
    /// worklist de cobranza; aca solo adaptamos el DTO al par (moneda, saldo) que espera el helper.
    ///
    /// <para>Vive en el armado del DTO a proposito: el front NUNCA decide la moneda principal, solo consume
    /// este valor (ADR-035 §7). Es la unica fuente del default de cobro.</para>
    /// </summary>
    private static string? ResolvePrimaryCurrency(List<ReservaMoneyLineDto> porMoneda)
    {
        if (porMoneda is null || porMoneda.Count == 0)
            return null;

        var lines = porMoneda
            .Select(line => (line.Currency, line.Balance))
            .ToList();
        return ReservaPrimaryCurrency.Resolve(lines);
    }

    /// <summary>
    /// ADR-035 (2026-06-19): mapea el resultado de la politica de dominio (<see cref="ReservaCapabilities"/>)
    /// al DTO que viaja al frontend. Traduccion 1:1; no agrega logica.
    /// </summary>
    private static ReservaCapabilitiesDto MapCapabilities(ReservaCapabilities caps)
    {
        static CapabilityDto Map(Cap cap) => new() { Allowed = cap.Allowed, Reason = cap.Reason };
        return new ReservaCapabilitiesDto
        {
            CanInvoiceSale = Map(caps.CanInvoiceSale),
            CanEmitCreditDebitNote = Map(caps.CanEmitCreditDebitNote),
            CanRegisterPayment = Map(caps.CanRegisterPayment),
            CanEditOrDeletePayment = Map(caps.CanEditOrDeletePayment),
            CanEditServices = Map(caps.CanEditServices),
            CanEditPassengers = Map(caps.CanEditPassengers),
            CanEditReservaData = Map(caps.CanEditReservaData),
            CanCancel = Map(caps.CanCancel),
            CanAnnul = Map(caps.CanAnnul),
            CanDelete = Map(caps.CanDelete),
            CanCancelServices = Map(caps.CanCancelServices),
            CanReschedule = Map(caps.CanReschedule),
            CanUploadDocument = Map(caps.CanUploadDocument),
            CanAdvance = Map(caps.CanAdvance),
            CanEmitVoucher = Map(caps.CanEmitVoucher),
            CanCorrectTravelingEntry = Map(caps.CanCorrectTravelingEntry),
            CanConfirmOperatorPenalty = Map(caps.CanConfirmOperatorPenalty),
            // Estado de resolucion de la pata del operador (None/Pending/Confirmed/Waived) como string estable
            // para el front. Es el nombre del enum del dominio; el front compara contra "Waived".
            OperatorPenaltyOutcome = caps.OperatorPenaltyOutcome.ToString(),
            AllowedForward = caps.AllowedForward.ToList(),
            AllowedRevert = caps.AllowedRevert.ToList(),
        };
    }

    /// <summary>
    /// ADR-017 (pill violeta "creado en esta venta"): marca en cada servicio tipado del detalle si su
    /// producto del tarifario nacio inline durante una venta (<see cref="Rate.CreatedInSale"/>).
    ///
    /// COMO: junta los RateId de las 5 colecciones tipadas ya cargadas en la entidad, consulta UNA sola
    /// vez cuales de esos rates tienen CreatedInSale=true, y estampa el flag en los DTOs matcheando por
    /// PublicId (entidad y DTO comparten el PublicId). Sin servicios con rate, no consulta nada.
    /// NO es dato de costo: se estampa para todos los callers (no se enmascara).
    /// El servicio generico (ServicioReserva) queda afuera: esta excluido del catalogo (ADR-017 §2.3.c).
    /// </summary>
    private async Task StampProductCreatedInSaleAsync(Reserva file, ReservaDto dto, CancellationToken ct)
    {
        var rateIds = new HashSet<int>();
        foreach (var h in file.HotelBookings) if (h.RateId.HasValue) rateIds.Add(h.RateId.Value);
        foreach (var f in file.FlightSegments) if (f.RateId.HasValue) rateIds.Add(f.RateId.Value);
        foreach (var t in file.TransferBookings) if (t.RateId.HasValue) rateIds.Add(t.RateId.Value);
        foreach (var p in file.PackageBookings) if (p.RateId.HasValue) rateIds.Add(p.RateId.Value);
        foreach (var a in file.AssistanceBookings) if (a.RateId.HasValue) rateIds.Add(a.RateId.Value);
        if (rateIds.Count == 0) return;

        var createdInSaleIds = (await _context.Rates
            .AsNoTracking()
            .Where(r => rateIds.Contains(r.Id) && r.CreatedInSale)
            .Select(r => r.Id)
            .ToListAsync(ct)).ToHashSet();
        if (createdInSaleIds.Count == 0) return;

        foreach (var itemDto in dto.HotelBookings)
        {
            var entity = file.HotelBookings.FirstOrDefault(h => h.PublicId == itemDto.PublicId);
            itemDto.ProductCreatedInSale = entity?.RateId is int hotelRateId && createdInSaleIds.Contains(hotelRateId);
        }
        foreach (var itemDto in dto.FlightSegments)
        {
            var entity = file.FlightSegments.FirstOrDefault(f => f.PublicId == itemDto.PublicId);
            itemDto.ProductCreatedInSale = entity?.RateId is int flightRateId && createdInSaleIds.Contains(flightRateId);
        }
        foreach (var itemDto in dto.TransferBookings)
        {
            var entity = file.TransferBookings.FirstOrDefault(t => t.PublicId == itemDto.PublicId);
            itemDto.ProductCreatedInSale = entity?.RateId is int transferRateId && createdInSaleIds.Contains(transferRateId);
        }
        foreach (var itemDto in dto.PackageBookings)
        {
            var entity = file.PackageBookings.FirstOrDefault(p => p.PublicId == itemDto.PublicId);
            itemDto.ProductCreatedInSale = entity?.RateId is int packageRateId && createdInSaleIds.Contains(packageRateId);
        }
        foreach (var itemDto in dto.AssistanceBookings)
        {
            var entity = file.AssistanceBookings.FirstOrDefault(a => a.PublicId == itemDto.PublicId);
            itemDto.ProductCreatedInSale = entity?.RateId is int assistanceRateId && createdInSaleIds.Contains(assistanceRateId);
        }
    }

    /// <summary>
    /// Carga <c>InvoiceDto.ServicePublicIds</c> para que el frontend pueda PRE-SELECCIONAR la factura
    /// correcta al cancelar un servicio de la reserva (en vez de que el usuario adivine en un
    /// desplegable). Es solo informacion de lectura: NO cambia ninguna regla de cancelacion ni obliga
    /// al usuario a nada, el sigue eligiendo la factura a mano.
    ///
    /// <para><b>Dos fuentes de trazabilidad, UNIDAS (2026-07-16)</b>: la lista sale de la union de (a)
    /// <c>InvoiceItem.SourceServicePublicId</c> — la trazabilidad polimorfica NUEVA, que cubre los 6
    /// tipos de servicio (vuelo/hotel/traslado/paquete/asistencia/generico) — y (b)
    /// <c>InvoiceItem.SourceServicioReservaId</c> — la trazabilidad LEGACY (FC1.3/ADR-009), que solo
    /// cubre el servicio generico. Un mismo item nunca aporta por las dos fuentes a la vez (son
    /// caminos de escritura distintos), pero unimos igual por si una factura vieja tiene items con
    /// legacy y una factura nueva tiene items con la trazabilidad nueva.</para>
    ///
    /// <para><b>Por que una query aparte (y no un Include mas arriba)</b>: mismo patron que
    /// <see cref="StampProductCreatedInSaleAsync"/>. Traer <c>Invoices.Items.SourceServicioReserva</c>
    /// con Include cargaria la entidad <c>ServicioReserva</c> COMPLETA por cada item con trazabilidad
    /// (costo, comision, etc.) solo para leer un PublicId. Con una proyeccion batcheada (UNA sola
    /// consulta SQL para TODAS las facturas de la reserva, filtrada por <c>InvoiceId IN (...)</c>) se
    /// evita el N+1 y no se trae al proceso ningun dato de mas.</para>
    /// </summary>
    private async Task PopulateInvoiceServicePublicIdsAsync(Reserva file, ReservaDto dto, CancellationToken ct)
    {
        if (file.Invoices.Count == 0 || dto.Invoices.Count == 0) return;

        var invoiceIds = file.Invoices.Select(i => i.Id).ToList();

        // Solo los items CON trazabilidad (por cualquiera de las dos fuentes) entran en el resultado.
        // Hoy la mayoria de los items no tiene ninguno de los dos datos (ver el XML-doc del campo en
        // InvoiceDto): para esas facturas la lista queda vacia, nunca null.
        var itemsWithSource = await _context.Set<InvoiceItem>()
            .AsNoTracking()
            .Where(item => invoiceIds.Contains(item.InvoiceId) &&
                (item.SourceServicePublicId != null || item.SourceServicioReservaId != null))
            .Select(item => new
            {
                item.InvoiceId,
                DirectPublicId = item.SourceServicePublicId,
                LegacyPublicId = item.SourceServicioReserva != null ? (Guid?)item.SourceServicioReserva.PublicId : null
            })
            .ToListAsync(ct);

        if (itemsWithSource.Count == 0) return;

        var servicePublicIdsByInvoiceId = itemsWithSource
            .GroupBy(x => x.InvoiceId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.DirectPublicId)
                      .Concat(g.Select(x => x.LegacyPublicId))
                      .Where(publicId => publicId != null)
                      .Select(publicId => publicId!.Value)
                      .Distinct()
                      .ToList());

        foreach (var invoiceDto in dto.Invoices)
        {
            var entity = file.Invoices.FirstOrDefault(i => i.PublicId == invoiceDto.PublicId);
            if (entity != null && servicePublicIdsByInvoiceId.TryGetValue(entity.Id, out var servicePublicIds))
            {
                invoiceDto.ServicePublicIds = servicePublicIds;
            }
        }
    }

    /// <summary>
    /// B1.15 Fase 2a (Decision 4): si el user actual NO tiene
    /// <c>cobranzas.see_cost</c>, oculta NetCost/TotalCost/Commission de la
    /// reserva y de cada coleccion de servicios. Admin bypass.
    ///
    /// Centralizado aca para garantizar que cualquier endpoint de detalle aplique
    /// la mascara antes de devolver el DTO al frontend.
    /// </summary>
    private async Task ApplyCostMaskingAsync(ReservaDto dto, CancellationToken ct)
    {
        var httpContextUser = _httpContextAccessor?.HttpContext?.User;
        var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
        if (isAdmin) return;

        var seeCost = await CurrentUserHasPermissionAsync(Permissions.CobranzasSeeCost, ct);
        if (seeCost) return;

        // Reserva-level totals.
        dto.TotalCost = 0m;
        // BLOQUEANTE DE SEGURIDAD: el margen (venta - costo) FILTRA el costo por resta. Se enmascara en el
        // MISMO if que TotalCost y al lado de el: jamas puede quedar TotalCost==0 con TotalMargin con valor.
        dto.TotalMargin = 0m;

        // ADR-021 Capa 5: el TotalCost de CADA linea por moneda es costo/inversion -> se enmascara
        // igual que el escalar. Critico: NO dejar visible el costo de una moneda y ocultar el de otra.
        if (dto.PorMoneda is not null)
        {
            foreach (var line in dto.PorMoneda)
            {
                line.TotalCost = 0m;
                // Mismo motivo que el escalar: el margen por moneda revela el costo por resta -> a 0 aca mismo.
                line.Margin = 0m;
            }
        }

        // Servicios genericos.
        if (dto.Servicios is not null)
        {
            foreach (var s in dto.Servicios)
            {
                s.NetCost = 0m;
                s.Commission = 0m;
                s.Tax = 0m; // Impuesto es componente del costo; revelaria margen/costo proveedor.
            }
        }

        // ADR-017 (guia UX linea 81): CostToConfirm es MARCA de costo -> quien no ve costos tampoco la ve.
        // ProductCreatedInSale NO se toca: no es dato de costo, lo ven todos.
        if (dto.HotelBookings is not null)
        {
            foreach (var b in dto.HotelBookings) { b.NetCost = 0m; b.Tax = 0m; b.CostToConfirm = false; }
        }
        if (dto.FlightSegments is not null)
        {
            foreach (var f in dto.FlightSegments) { f.NetCost = 0m; f.Tax = 0m; f.CostToConfirm = false; }
        }
        if (dto.PackageBookings is not null)
        {
            foreach (var p in dto.PackageBookings) { p.NetCost = 0m; p.Tax = 0m; p.CostToConfirm = false; }
        }
        if (dto.TransferBookings is not null)
        {
            foreach (var t in dto.TransferBookings) { t.NetCost = 0m; t.Tax = 0m; t.CostToConfirm = false; }
        }
        if (dto.AssistanceBookings is not null)
        {
            foreach (var a in dto.AssistanceBookings) { a.NetCost = 0m; a.Tax = 0m; a.CostToConfirm = false; }
        }

        // ADR-027 (detalle "confirmada con cambios"): un cambio de COSTO revela el costo del proveedor -> se
        // enmascara igual que el resto de los costos. El cambio de PRECIO DE VENTA al cliente NO es sensible:
        // se ve siempre. Marcamos ValuesMasked=true para que el front muestre "—" en vez de los ceros.
        if (dto.PendingChanges is not null)
        {
            foreach (var change in dto.PendingChanges)
            {
                if (change.Field == PendingChangeFields.NetCost)
                {
                    change.OldValue = 0m;
                    change.NewValue = 0m;
                    change.ValuesMasked = true;
                }
            }
        }
    }

    public async Task<Reserva> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId)
    {
        // C16: ApplicationUser ya no es nav prop de Reserva — denormalizamos el FullName del
        // responsable al crear. Si el lookup no encuentra usuario, dejamos null (no rompemos
        // la creacion por un nombre faltante; el FK se valida igual al persistir).
        string? responsibleUserName = null;
        if (!string.IsNullOrWhiteSpace(createdByUserId))
        {
            var responsibleUser = await _userManager.FindByIdAsync(createdByUserId);
            responsibleUserName = responsibleUser?.FullName;
        }

        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                int? payerId = null;

                if (!string.IsNullOrWhiteSpace(request.PayerId))
                {
                    payerId = await _context.Customers
                        .AsNoTracking()
                        .ResolveInternalIdAsync(request.PayerId, CancellationToken.None);

                    if (!payerId.HasValue)
                    {
                        throw new KeyNotFoundException("Cliente no encontrado");
                    }
                }

                // CRM leads (2026-06-12): si la reserva nace de un lead, resolvemos el lead de origen
                // ADENTRO de la transaccion para que el linkeo + el cambio de estado del lead viajen
                // junto con la creacion de la reserva (todo o nada). Buscamos la ENTIDAD trackeada (no
                // AsNoTracking) porque despues le cambiamos el Status y necesitamos que EF lo persista.
                Lead? sourceLead = null;
                if (!string.IsNullOrWhiteSpace(request.SourceLeadPublicId))
                {
                    var sourceLeadId = await _context.Leads
                        .AsNoTracking()
                        .ResolveInternalIdAsync(request.SourceLeadPublicId, CancellationToken.None);

                    if (!sourceLeadId.HasValue)
                    {
                        // Lead inexistente = pedido invalido del cliente -> 400 (ArgumentException lo mapea
                        // el controller). No es 404 de "la reserva no existe": la reserva todavia no se creo.
                        throw new ArgumentException("Lead de origen no encontrado.");
                    }

                    sourceLead = await _context.Leads.FindAsync(new object[] { sourceLeadId.Value }, CancellationToken.None);

                    // El botón de CRM puede repetirse por doble clic o por volver a abrir un lead que aún
                    // no llegó a un estado en firme. Reusar el presupuesto abierto evita duplicar expedientes.
                    var existingLeadBudget = await _context.Reservas
                        .Where(r => r.SourceLeadId == sourceLeadId.Value
                            && r.Status == EstadoReserva.Budget)
                        .OrderByDescending(r => r.CreatedAt)
                        .FirstOrDefaultAsync();
                    if (existingLeadBudget != null)
                    {
                        await transaction.CommitAsync();
                        return existingLeadBudget;
                    }

                    // Si el posible cliente ya fue convertido, el presupuesto debe nacer conectado a su
                    // cuenta corriente. Un payer explícito del request conserva prioridad.
                    payerId ??= sourceLead?.ConvertedCustomerId;
                }

                var numeroReserva = await GenerateNumeroReservaAsync(CancellationToken.None);

                var fileName = !string.IsNullOrWhiteSpace(request.Name)
                    ? request.Name
                    : $"Reserva {numeroReserva}";

                var file = new Reserva
                {
                    Name = fileName,
                    NumeroReserva = numeroReserva,
                    PayerId = payerId,
                    ResponsibleUserId = createdByUserId,
                    ResponsibleUserName = responsibleUserName,
                    StartDate = request.StartDate,
                    Description = request.Description,
                    // Decision de producto 2026-07-15: el circuito legacy de cotizaciones quedo
                    // discontinuado. Toda propuesta nueva nace como Reserva-Presupuesto y el estado
                    // inicial sigue sin poder elegirse desde el request.
                    Status = EstadoReserva.Budget,
                    // CRM leads: linkeo de trazabilidad lead -> reserva (se setea aunque el lead ya
                    // estuviera Ganado/Perdido; el linkeo no depende del estado).
                    SourceLeadId = sourceLead?.Id
                };

                _context.Reservas.Add(file);

                // Decision del dueño (auditoria ERP 2026-06-13): crear una reserva/presupuesto desde un
                // lead ya NO lo marca Ganado. Solo dejamos el linkeo de trazabilidad (SourceLeadId, seteado
                // arriba). El lead pasa a Ganado recien cuando la reserva linkeada llega a un estado EN FIRME
                // (ver MarkSourceLeadAsWonIfReservaIsFirmAsync, disparado desde UpdateStatusAsync). Una reserva
                // nace en Presupuesto, que NO es un estado en firme: marcar Ganado aca seria prematuro (el
                // cliente todavia no acepto el presupuesto).

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return file;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    /// <summary>
    /// Integridad de datos (2026-06-25): valida que la fecha de regreso de un servicio generico no sea
    /// anterior a la de salida. El regreso es OPCIONAL (ver <see cref="ServicioReserva.ReturnDate"/>): si no
    /// viene, no hay nada que validar (servicio de una sola fecha). Se compara solo con ambas presentes.
    /// Replica el patron de Hotel/Asistencia/Vuelo/Traslado/Paquete de BookingService, pero vive aca porque
    /// el servicio generico se da de alta/edita en ReservaService (otra clase).
    /// </summary>
    private static void ValidateGenericServiceDates(DateTime departureDate, DateTime? returnDate)
    {
        if (returnDate.HasValue && returnDate.Value < departureDate)
        {
            throw new ArgumentException("La fecha de regreso no puede ser anterior a la de salida.");
        }
    }

    public async Task<(ServicioReserva Reservation, string? Warning)> AddServiceAsync(int reservaId, AddServiceRequest request, CancellationToken ct = default)
    {
        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        int? supplierId = null;

        if (!string.IsNullOrWhiteSpace(request.SupplierId))
        {
            supplierId = await _context.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(request.SupplierId, CancellationToken.None);

            if (!supplierId.HasValue)
            {
                throw new KeyNotFoundException("Proveedor no encontrado");
            }
        }

        if (string.IsNullOrWhiteSpace(request.ServiceType)) throw new ArgumentException("Debe seleccionar un tipo de servicio");
        if (request.DepartureDate == default) throw new ArgumentException("La fecha de salida es obligatoria");
        // Integridad de datos (2026-06-25): la fecha de regreso (opcional) no puede ser anterior a la de
        // salida. Solo se valida si ambas estan presentes; un servicio de una sola fecha (ReturnDate null)
        // sigue siendo valido.
        ValidateGenericServiceDates(request.DepartureDate, request.ReturnDate);
        if (request.SalePrice <= 0) throw new ArgumentException("El precio de venta debe ser mayor a 0");
        if (request.NetCost < 0) throw new ArgumentException("El costo neto no puede ser negativo");

        string? warning = null;
        if (request.NetCost > request.SalePrice)
        {
            warning = $"Atención: el costo ({request.NetCost:C}) supera el precio de venta ({request.SalePrice:C}). Se está vendiendo a pérdida.";
        }

        var reservation = new ServicioReserva
        {
            ReservaId = reservaId,
            ServiceType = request.ServiceType,
            ProductType = request.ServiceType,
            SupplierId = supplierId,
            CustomerId = file.PayerId,
            Description = request.Description ?? request.ServiceType,
            ConfirmationNumber = request.ConfirmationNumber ?? "PENDIENTE",
            Status = "Solicitado",
            DepartureDate = request.DepartureDate.ToUniversalTime(),
            ReturnDate = request.ReturnDate?.ToUniversalTime(),
            SalePrice = request.SalePrice,
            NetCost = request.NetCost,
            Commission = request.SalePrice - request.NetCost,
            // ADR-026 (vencimientos): fecha de pared (medianoche Kind=Utc) igual que los tipos
            // catalogados (NormalizeCalendarDate de BookingService); Npgsql rechaza Kind!=Utc en timestamptz.
            OperatorPaymentDeadline = request.OperatorPaymentDeadline.HasValue
                ? DateTime.SpecifyKind(request.OperatorPaymentDeadline.Value.Date, DateTimeKind.Utc)
                : (DateTime?)null,
            CreatedAt = DateTime.UtcNow
        };

        // B1 (ADR-017 F1b — regresion del masking): si el alta vino del tarifario y el caller
        // NO puede ver costos, el NetCost del request es el 0 enmascarado rebotado por el form,
        // no un dato real. El server resuelve el costo desde la tarifa (el server sabe; el
        // caller sigue sin verlo) y recalcula la ganancia con la formula de este path
        // (Commission = SalePrice - NetCost; el servicio generico no captura Tax en ningun
        // punto de su ciclo, queda en 0). Si la tarifa no tiene costo utilizable, queda 0
        // (no inventar). Con permiso: el request manda, como siempre.
        if (!string.IsNullOrWhiteSpace(request.RateId)
            && !await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            var rateId = await _context.Rates
                .AsNoTracking()
                .ResolveInternalIdAsync(request.RateId, ct);
            var rate = rateId.HasValue
                ? await _context.Rates.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rateId.Value, ct)
                : null;

            if (rate != null)
            {
                reservation.NetCost = rate.NetCost > 0m ? rate.NetCost : 0m;
                reservation.Commission = request.SalePrice - reservation.NetCost;

                // Trazabilidad: el costo lo resolvio el sistema desde la tarifa, no el
                // vendedor. Solo IDs — sin montos en el log.
                _logger.LogInformation(
                    "AddService: caller sin ver-costos; costo resuelto server-side desde el tarifario. ReservaId={ReservaId} RateId={RateId}",
                    reservaId, rate.Id);
            }
        }

        // ADR-031 (bypass B1, servicio generico): si el alta deja el servicio resuelto, exigir el nombre
        // de TODOS los declarados ANTES de persistir. Hoy AddServiceAsync fuerza Status="Solicitado"
        // (nunca resuelve), asi que este gate es defensivo; corre igual para que una ruta futura que deje
        // el generico resuelto no se cuele sin nombres y el motor no auto-confirme la reserva. El alta
        // nace sin resolver previo -> wasResolved=false.
        await EnsureGenericNominalCoverageBeforeResolvingAsync(reservation, serviceWasResolved: false, ct);

        _context.Servicios.Add(reservation);
        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);

        // ADR-022 §4.10 (fix P1): el servicio generico participa de la deuda del proveedor, pero hasta
        // ahora ReservaService solo recalculaba el saldo de la RESERVA y nunca el del PROVEEDOR -> su
        // CurrentBalance / SupplierBalanceByCurrency quedaban stale. Si el servicio recien creado tiene
        // proveedor, recalculamos su deuda (escalar + tabla hija) con el mismo helper sin estado que usa
        // SupplierService, asi el numero es identico. Solo si hay proveedor (un generico sin proveedor no
        // toca ninguna cuenta).
        if (supplierId.HasValue)
        {
            await RecalculateSupplierDebtAsync(supplierId.Value, ct);
        }

        return (reservation, warning);
    }

    /// <summary>
    /// ADR-022 §4.10 (fix P1): recalcula y persiste la deuda de un proveedor (escalar surrogate + tabla
    /// hija por moneda) tras crear/editar/borrar un servicio generico con proveedor. Delega en
    /// <see cref="SupplierDebtPersister"/> — el mismo helper sin estado que usa <c>SupplierService</c>, para
    /// que el numero final sea EXACTAMENTE el que daria el servicio del proveedor (sin inyectar
    /// <c>ISupplierService</c>, evitando el ciclo de dependencias). El persister no hace SaveChanges, por
    /// eso lo cerramos aca con un SaveChanges propio.
    /// </summary>
    private async Task RecalculateSupplierDebtAsync(int supplierId, CancellationToken ct)
    {
        await SupplierDebtPersister.PersistAsync(_context, supplierId, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<ServicioReserva> UpdateServiceAsync(int serviceId, AddServiceRequest request, CancellationToken ct = default)
    {
        var service = await _context.Servicios
            .Include(r => r.Reserva)
            .FirstOrDefaultAsync(r => r.Id == serviceId);


        if (service == null) throw new KeyNotFoundException("Servicio no encontrado");

        // ADR-031: estado de resolucion ANTES de la edicion (para gatear solo la transicion a resuelto).
        // La edicion del generico no toca el Status, asi que en la practica nunca hay transicion aca.
        var genericWasResolved = ServiceResolutionRules.IsResolved(service);

        // B1.15 Fase 0' (CODE-05): inmutabilidad post-CAE / post-voucher. Cambiar
        // monto/proveedor/fechas del servicio rompe la coherencia con la factura
        // AFIP emitida o el voucher entregado al cliente.
        var blockReason = await MutationGuards.GetServiceMutationBlockReasonAsync(_context, serviceId, ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateServiceAsync rejected. ServiceId={ServiceId} ReservaId={ReservaId}. Reason={Reason}",
                serviceId, service.ReservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        int? supplierId = null;
        if (!string.IsNullOrWhiteSpace(request.SupplierId))
        {
            supplierId = await _context.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(request.SupplierId, CancellationToken.None);

            if (!supplierId.HasValue)
            {
                throw new KeyNotFoundException("Proveedor no encontrado");
            }
        }

        if (string.IsNullOrWhiteSpace(request.ServiceType)) throw new ArgumentException("Debe seleccionar un tipo de servicio");
        if (request.SalePrice <= 0) throw new ArgumentException("El precio de venta debe ser mayor a 0");
        // Integridad de datos (2026-06-25): misma regla que en el alta — regreso (opcional) >= salida.
        ValidateGenericServiceDates(request.DepartureDate, request.ReturnDate);

        // ADR-022 §4.10 (fix P1): capturamos el proveedor ANTERIOR antes de pisarlo. Si el usuario cambia
        // de proveedor (o le saca/pone proveedor), hay que recalcular la deuda del VIEJO y del NUEVO: el
        // viejo deja de tener este servicio (su deuda baja) y el nuevo lo gana (su deuda sube). El cambio
        // de NetCost/moneda/estado tambien afecta la deuda del proveedor vigente, por eso siempre que haya
        // proveedor (viejo o nuevo) recalculamos.
        var previousSupplierId = service.SupplierId;

        // ADR-027 (hallazgo #10): capturamos precio/costo ANTES de pisarlos para detectar si esta edicion
        // es "el operador confirmo con otro precio". Si SalePrice o NetCost cambian y la reserva esta viva,
        // se marca "confirmada con cambios" (lo decide UpdateBalanceAsync con el flag de abajo).
        var previousSalePrice = service.SalePrice;
        var previousNetCost = service.NetCost;
        // Plata viva (familia R1): tipo/estado ANTES de pisarlos, para la guarda de reasignacion de operador
        // (cambiar el operador de un servicio confirmado y pagado dejaria la caja del saliente negativa sin ancla).
        // La moneda del generico NO se edita por este path, asi que no se evalua cambio de moneda.
        var previousServiceType = service.ServiceType;
        var previousStatus = service.Status;

        service.ServiceType = request.ServiceType;
        service.ProductType = request.ServiceType;
        service.Description = request.Description ?? request.ServiceType;
        service.ConfirmationNumber = request.ConfirmationNumber ?? service.ConfirmationNumber;
        service.DepartureDate = request.DepartureDate.ToUniversalTime();
        service.ReturnDate = request.ReturnDate?.ToUniversalTime();
        service.SupplierId = supplierId;
        service.SalePrice = request.SalePrice;

        // ADR-026 (vencimientos): anti-pisado igual que los tipos catalogados — solo se asigna
        // si el request trae la fecha; un form viejo que no la manda NO borra la fecha cargada.
        if (request.OperatorPaymentDeadline.HasValue)
            service.OperatorPaymentDeadline = DateTime.SpecifyKind(request.OperatorPaymentDeadline.Value.Date, DateTimeKind.Utc);

        // B2 (ADR-017 F1b — Fuga 3 en el servicio generico): a un caller sin
        // cobranzas.see_cost el GET le enmascara NetCost a 0; el form re-envia ese 0 y la
        // asignacion incondicional destruia el costo real en cada edicion legitima.
        // Mismo patron que BookingService.ResolveUpdateCostFieldsAsync, replicado local
        // porque este path tiene su propia formula (sin Tax en el request) — compartir el
        // helper acoplaria los dos services por una tupla que aca no aplica.
        //  - Con permiso (o Admin): el request manda, identico al comportamiento de siempre.
        //  - Sin permiso: se PRESERVA el NetCost persistido y la ganancia se recalcula con
        //    el SalePrice del request (que el caller SI ve) y los valores preservados.
        //    Se descuenta tambien el Tax persistido (formula canonica); en este path es 0
        //    porque el servicio generico no captura impuesto, pero si algun dato lo trae,
        //    la ganancia no lo ignora.
        if (await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            service.NetCost = request.NetCost;
            // Divergencia menor vs la rama sin permiso (que resta service.Tax): aca la formula
            // NO descuenta Tax. Es inofensivo porque el servicio generico no captura impuesto
            // en ningun punto de su ciclo (Tax ≡ 0 en este path); ambas formulas dan lo mismo.
            // Se deja asi para no cambiar el comportamiento historico de la rama con permiso.
            service.Commission = request.SalePrice - request.NetCost;
        }
        else
        {
            // Trazabilidad: fue el sistema quien preservo el costo, no el vendedor.
            // Solo IDs — sin montos en el log.
            _logger.LogInformation(
                "UpdateService: caller sin ver-costos; se preserva el NetCost persistido y se recalcula la ganancia. ServiceId={ServiceId} ReservaId={ReservaId}",
                serviceId, service.ReservaId);
            service.Commission = request.SalePrice - service.NetCost - service.Tax;
        }

        // ADR-031 (bypass B1, servicio generico): si la edicion deja el servicio resuelto (no lo estaba
        // antes), exigir el nombre de TODOS los declarados ANTES de persistir. Defensivo (la edicion del
        // generico no toca el Status hoy), igual que en AddServiceAsync.
        await EnsureGenericNominalCoverageBeforeResolvingAsync(service, genericWasResolved, ct);

        // Plata viva (familia R1): impedir reasignar el operador de un servicio generico confirmado y pagado al
        // operador saliente sin factura que ancle el receivable (el reconciler lo mintearia como saldo a favor
        // gastable). Reusa el MISMO nucleo de cancelacion (RefundCap del servicio, pool imputado a ESTA reserva ->
        // excluye el prepago a cuenta del operador). Corre ANTES del SaveChanges: el nucleo lee el servicio VIEJO con
        // AsNoTracking (el cambio sigue sin flushear). El candado post-CAE (MutationGuards, arriba) ya cubrio la
        // factura viva. Solo si (a) cambio el operador (previousSupplierId/supplierId son int?; 0 = sin operador) y
        // (b) el servicio venia contando como compra confirmada del saliente (si no, moverlo no baja su caja).
        var previousSupplierIdValue = previousSupplierId ?? 0;
        if (_cancellationService != null
            && previousSupplierIdValue > 0
            && previousSupplierIdValue != (supplierId ?? 0)
            && WorkflowStatusHelper.CountsForSupplierDebtByType(previousServiceType, previousStatus)
            && service.ReservaId.HasValue)
        {
            await _cancellationService.EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync(
                service.ReservaId.Value, CancellableServiceTable.Generic, service.Id, isCurrencyChange: false, ct);
        }

        await _context.SaveChangesAsync();

        // ADR-027 (detalle): armamos el descriptor del cambio (que servicio, antes/despues) y lo pasamos a
        // UpdateBalanceAsync, que decide (estado vivo) si marca la reserva y registra el detalle. Si no cambio
        // ni precio ni costo, el descriptor no tiene cambio significativo y el trigger no hace nada.
        var serviceChange = new PendingServiceChange
        {
            ServiceType = string.IsNullOrWhiteSpace(service.ServiceType) ? "Servicio" : service.ServiceType,
            ServiceDescription = string.IsNullOrWhiteSpace(service.Description) ? service.ServiceType ?? "Servicio" : service.Description,
            ServicePublicId = service.PublicId,
            Currency = service.Currency,
            OldSalePrice = previousSalePrice,
            NewSalePrice = service.SalePrice,
            OldNetCost = previousNetCost,
            NewNetCost = service.NetCost,
        };
        if (service.ReservaId.HasValue)
            await UpdateBalanceAsync(service.ReservaId.Value, serviceChange);

        // ADR-022 §4.10 (fix P1): recalcular la deuda del proveedor VIEJO y del NUEVO. Si no cambio de
        // proveedor, ambos ids son iguales y un HashSet evita recalcular dos veces el mismo. Cada uno solo
        // si no es null (un generico sin proveedor no toca ninguna cuenta).
        var suppliersToRecalculate = new HashSet<int>();
        if (previousSupplierId.HasValue) suppliersToRecalculate.Add(previousSupplierId.Value);
        if (supplierId.HasValue) suppliersToRecalculate.Add(supplierId.Value);
        foreach (var affectedSupplierId in suppliersToRecalculate)
        {
            await RecalculateSupplierDebtAsync(affectedSupplierId, ct);
        }

        return service;
    }

    public async Task RemoveServiceAsync(int serviceId, CancellationToken ct = default)
    {
        // 1. Try generic service
        var service = await _context.Servicios.FindAsync(new object[] { serviceId }, ct);
        if (service != null)
        {
            var confirmed = service.ConfirmedAt != null
                || ServiceResolutionRules.IsOperatorConfirmed(service)
                || ServiceResolutionRules.IsResolved(service);
            // Un servicio generico cancelado NUNCA se borra fisico: es historia de la reserva y
            // puede tener multa/ajuste de cambio/NC asociados (bug de servicios huerfanos).
            var cancelled = ServiceResolutionRules.IsCancelled(service);
            await EnsureCanRemoveServiceAsync(service.ReservaId ?? 0, confirmed, cancelled, service.Id, ct);
            // ADR-022 §4.10 (fix P1): capturamos el proveedor antes de borrar el servicio para recalcular
            // su deuda despues (el servicio borrado deja de contar -> la deuda de ese proveedor baja).
            var removedSupplierId = service.SupplierId;
            // ADR-031 v2.1 (M1): limpiar asignaciones del generico en la misma transaccion que el borrado.
            await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Generic, service.Id, service.ReservaId ?? 0, ct);
            _context.Servicios.Remove(service);
            var resId = service.ReservaId;
            await _context.SaveChangesAsync(ct);
            if (resId.HasValue) await UpdateBalanceAsync(resId.Value);
            if (removedSupplierId.HasValue) await RecalculateSupplierDebtAsync(removedSupplierId.Value, ct);
            return;
        }

        // 2. Try Flight
        var flight = await _context.FlightSegments.FindAsync(new object[] { serviceId }, ct);
        if (flight != null)
        {
            var confirmed = flight.ConfirmedAt != null
                || ServiceResolutionRules.IsOperatorConfirmed(flight)
                || ServiceResolutionRules.IsResolved(flight);
            var cancelled = ServiceResolutionRules.IsCancelled(flight);
            await EnsureCanRemoveServiceAsync(flight.ReservaId, confirmed, cancelled, null, ct);
            await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Flight, flight.Id, flight.ReservaId, ct);
            _context.FlightSegments.Remove(flight);
            var resId = flight.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 3. Try Hotel
        var hotel = await _context.HotelBookings.FindAsync(new object[] { serviceId }, ct);
        if (hotel != null)
        {
            var confirmed = hotel.ConfirmedAt != null
                || ServiceResolutionRules.IsOperatorConfirmed(hotel)
                || ServiceResolutionRules.IsResolved(hotel);
            var cancelled = ServiceResolutionRules.IsCancelled(hotel);
            await EnsureCanRemoveServiceAsync(hotel.ReservaId, confirmed, cancelled, null, ct);
            await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Hotel, hotel.Id, hotel.ReservaId, ct);
            _context.HotelBookings.Remove(hotel);
            var resId = hotel.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 4. Try Transfer
        var transfer = await _context.TransferBookings.FindAsync(new object[] { serviceId }, ct);
        if (transfer != null)
        {
            var confirmed = transfer.ConfirmedAt != null
                || ServiceResolutionRules.IsOperatorConfirmed(transfer)
                || ServiceResolutionRules.IsResolved(transfer);
            var cancelled = ServiceResolutionRules.IsCancelled(transfer);
            await EnsureCanRemoveServiceAsync(transfer.ReservaId, confirmed, cancelled, null, ct);
            await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Transfer, transfer.Id, transfer.ReservaId, ct);
            _context.TransferBookings.Remove(transfer);
            var resId = transfer.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 5. Try Package
        var package = await _context.PackageBookings.FindAsync(new object[] { serviceId }, ct);
        if (package != null)
        {
            var confirmed = package.ConfirmedAt != null
                || ServiceResolutionRules.IsOperatorConfirmed(package)
                || ServiceResolutionRules.IsResolved(package);
            var cancelled = ServiceResolutionRules.IsCancelled(package);
            await EnsureCanRemoveServiceAsync(package.ReservaId, confirmed, cancelled, null, ct);
            await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Package, package.Id, package.ReservaId, ct);
            _context.PackageBookings.Remove(package);
            var resId = package.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 6. Try Assistance (Bloque 3): si no la contemplamos aca, borrar una asistencia por
        // este path generico tiraria "no encontrado" sin tocar el saldo -> descuadre silencioso.
        var assistance = await _context.AssistanceBookings.FindAsync(new object[] { serviceId }, ct);
        if (assistance != null)
        {
            var confirmed = assistance.ConfirmedAt != null
                || ServiceResolutionRules.IsOperatorConfirmed(assistance)
                || ServiceResolutionRules.IsResolved(assistance);
            var cancelled = ServiceResolutionRules.IsCancelled(assistance);
            await EnsureCanRemoveServiceAsync(assistance.ReservaId, confirmed, cancelled, null, ct);
            await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Assistance, assistance.Id, assistance.ReservaId, ct);
            _context.AssistanceBookings.Remove(assistance);
            var resId = assistance.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        throw new KeyNotFoundException("Servicio no encontrado en ninguna categoría.");
    }

    // ComputeMaxExpectedPaxCount fue ELIMINADO: infiere el "esperado" de la capacidad de los
    // servicios (Hotel/Package con Sum, Transfer con Max, sin FlightSegment) de forma
    // inconsistente. El conteo de pasajeros nominales ahora se basa SIEMPRE en la cantidad
    // DECLARADA de la reserva (AdultCount+ChildCount+InfantCount). Ver AddPassengerAsync y
    // EnsureReadinessForSaleAsync.
    //
    // La logica de capacidad pasajeros-vs-servicios (otra dimension, no nominales) vive en
    // ReservaCapacityRules (clase estatica compartida con ReservaLifecycleAutomationService).

    /// <summary>
    /// ADR-020 (F5): valida que un servicio se pueda BORRAR. Manda el servicio: si fue confirmado por
    /// el operador (<paramref name="serviceIsOperatorConfirmed"/>) no se borra, se cancela; si esta
    /// Cancelado (<paramref name="serviceIsCancelled"/>) tampoco se borra NUNCA, tenga o no ConfirmedAt.
    /// El guard vive en DeleteGuards (compartido con BookingService).
    /// </summary>
    private async Task EnsureCanRemoveServiceAsync(int reservaId, bool serviceIsOperatorConfirmed, bool serviceIsCancelled, int? genericServiceId, CancellationToken ct)
    {
        var blockReason = await DeleteGuards.GetServiceDeleteBlockReasonAsync(
            _context, reservaId, serviceIsOperatorConfirmed, serviceIsCancelled, genericServiceId, ct, _logger);
        if (blockReason != null)
        {
            _logger.LogInformation(
                "RemoveServiceAsync rejected. ReservaId={ReservaId}. Reason={Reason}",
                reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }
    }

    public async Task<IEnumerable<PassengerDto>> GetPassengersAsync(int reservaId)
    {
        return await _context.Passengers
            .Where(p => p.ReservaId == reservaId)
            .OrderBy(p => p.FullName)
            .ProjectTo<PassengerDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
    }

    public async Task<PassengerDto> AddPassengerAsync(int reservaId, Passenger passenger)
    {
        // ADR-035 (2026-06-19): PRIMERA COMPUERTA — en una reserva CERRADA (Closed/Lost/Cancelled/
        // PendingOperatorRefund) los pasajeros son solo lectura DURA: no se puede ni agregar uno. Corre antes
        // de cualquier guard fiscal/de capacidad. En estados vivos (EN ARMADO / EN FIRME) no bloquea: ahi sigue
        // valiendo la regla de ADR-031 (agregar/completar no pide autorizacion). Este es el chokepoint: las dos
        // sobrecargas string delegan aca, asi que con gatear el int overload alcanza para todos los caminos.
        await ReservaCapacityRules.EnsurePassengersEditableByStateAsync(_context, reservaId);

        var file = await _context.Reservas
            .Include(r => r.Passengers)
            .FirstOrDefaultAsync(r => r.Id == reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // Nota: NO se bloquea la carga en estado Presupuesto. El modal de Confirmar
        // Reserva carga los pasajeros nominales JUSTO ANTES de transicionar a En gestion.
        // La transicion misma valida via EnsureReadinessForSaleAsync que la cantidad de
        // pasajeros nominales == cantidad DECLARADA de la reserva.

        if (string.IsNullOrWhiteSpace(passenger.FullName)) throw new ArgumentException("El nombre del pasajero es obligatorio");
        if (passenger.FullName.Length < 3) throw new ArgumentException("El nombre debe tener al menos 3 caracteres");

        // Tope de pasajeros nominales = cantidad DECLARADA de la reserva (misma fuente unica
        // que usa EnsureReadinessForSaleAsync). NO se infiere de la capacidad de los servicios:
        // eso daba un tope inconsistente (recalculaba 3 con 0 cargados y bloqueaba, o quedaba
        // en 0). La capacidad pax de cada servicio es dato del servicio y no cuenta nominales.
        var declaredPax = file.AdultCount + file.ChildCount + file.InfantCount;

        // Regla C: si todavia no se declaro la cantidad, el mensaje guia a declararla primero
        // en lugar del guard de capacidad confuso anterior.
        if (declaredPax <= 0)
        {
            throw new InvalidOperationException(
                "Primero declará la cantidad de pasajeros de la reserva (adultos, menores e infantes) " +
                "antes de cargar los nombres.");
        }

        if (file.Passengers.Count >= declaredPax)
        {
            throw new InvalidOperationException(
                $"La reserva declara {declaredPax} pasajero(s) y ya están todos cargados. " +
                "Para sumar más, aumentá la cantidad declarada de pasajeros de la reserva.");
        }

        if (passenger.BirthDate.HasValue)
        {
            passenger.BirthDate = DateTime.SpecifyKind(passenger.BirthDate.Value, DateTimeKind.Utc);
        }

        // Auditoria ERP item 8: el vencimiento de pasaporte es fecha "de pared" date-only (Npgsql exige
        // Kind=Utc en timestamptz). Mismo tratamiento que BirthDate.
        if (passenger.PassportExpiry.HasValue)
        {
            passenger.PassportExpiry = DateTime.SpecifyKind(passenger.PassportExpiry.Value.Date, DateTimeKind.Utc);
        }

        passenger.ReservaId = reservaId;
        passenger.CreatedAt = DateTime.UtcNow;

        _context.Passengers.Add(passenger);
        await _context.SaveChangesAsync();

        return _mapper.Map<PassengerDto>(passenger);
    }

    public async Task<PassengerDto> UpdatePassengerAsync(int passengerId, Passenger updated)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) throw new KeyNotFoundException("Pasajero no encontrado");

        // ADR-035 (2026-06-19): PRIMERA COMPUERTA — en una reserva CERRADA el pasajero es solo lectura DURA: no
        // se puede ni completar un dato faltante. Corre antes del candado fiscal de "datos personales". En
        // estados vivos no bloquea (ahi rige ADR-031). Chokepoint: la sobrecarga string delega aca.
        await ReservaCapacityRules.EnsurePassengersEditableByStateAsync(_context, passenger.ReservaId);

        if (string.IsNullOrWhiteSpace(updated.FullName)) throw new ArgumentException("El nombre del pasajero es obligatorio");
        if (updated.FullName.Length < 3) throw new ArgumentException("El nombre debe tener al menos 3 caracteres");

        // B1.15 Fase 0' (CODE-14): solo bloqueamos si el request cambia DATOS
        // PERSONALES (nombre, documento, fecha de nacimiento, nacionalidad,
        // genero). Email/Phone/Notes son campos de contacto y se permiten editar
        // libremente — son parte de la operativa de la reserva, no del voucher.
        var personalDataChanged =
            !string.Equals(passenger.FullName, updated.FullName, StringComparison.Ordinal) ||
            !string.Equals(passenger.DocumentType, updated.DocumentType, StringComparison.Ordinal) ||
            !string.Equals(passenger.DocumentNumber, updated.DocumentNumber, StringComparison.Ordinal) ||
            passenger.BirthDate != updated.BirthDate ||
            !string.Equals(passenger.Nationality, updated.Nationality, StringComparison.Ordinal) ||
            !string.Equals(passenger.Gender, updated.Gender, StringComparison.Ordinal);

        if (personalDataChanged)
        {
            var blockReason = await MutationGuards.GetPassengerMutationBlockReasonAsync(_context, passengerId);
            if (blockReason != null)
            {
                // PII: no logueamos nombre/documento, solo IDs.
                _logger.LogWarning(
                    "UpdatePassengerAsync rejected. PassengerId={PassengerId} ReservaId={ReservaId}. Reason={Reason}",
                    passengerId, passenger.ReservaId, blockReason);
                throw new InvalidOperationException(blockReason);
            }
        }

        passenger.FullName = updated.FullName;
        passenger.DocumentType = updated.DocumentType;
        passenger.DocumentNumber = updated.DocumentNumber;

        if (updated.BirthDate.HasValue)
        {
            passenger.BirthDate = DateTime.SpecifyKind(updated.BirthDate.Value, DateTimeKind.Utc);
        }
        else
        {
            passenger.BirthDate = null;
        }

        // Auditoria ERP item 8: vencimiento de pasaporte. NO entra al guard de "datos personales"
        // (personalDataChanged): no es identidad que invalide un voucher emitido, es un dato operativo
        // del documento que el vendedor completa/corrige a medida que recibe la documentacion. Se
        // normaliza a fecha de pared Kind=Utc; null = se limpio el dato.
        passenger.PassportExpiry = updated.PassportExpiry.HasValue
            ? DateTime.SpecifyKind(updated.PassportExpiry.Value.Date, DateTimeKind.Utc)
            : null;

        passenger.Nationality = updated.Nationality;
        passenger.Phone = updated.Phone;
        passenger.Email = updated.Email;
        passenger.Gender = updated.Gender;
        passenger.Notes = updated.Notes;

        await _context.SaveChangesAsync();
        return _mapper.Map<PassengerDto>(passenger);
    }

    public async Task RemovePassengerAsync(int passengerId)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) throw new KeyNotFoundException("Pasajero no encontrado");

        // ADR-035 (2026-06-19): PRIMERA COMPUERTA — en una reserva CERRADA el roster es solo lectura DURA: no
        // se puede borrar un pasajero. Corre antes del guard fiscal de borrado. Chokepoint: la sobrecarga
        // string (con su candado de autorizacion) delega aca, asi que cubre ambos caminos.
        await ReservaCapacityRules.EnsurePassengersEditableByStateAsync(_context, passenger.ReservaId);

        var blockReason = await DeleteGuards.GetPassengerDeleteBlockReasonAsync(_context, passengerId);
        if (blockReason != null)
        {
            // Warning: el guard incluye un check fiscal (factura emitida con CAE — C27).
            // El reviewer pidio Warning para marcar potencial riesgo fiscal/auditoria;
            // mantenemos Warning para todos los rechazos del guard para no bifurcar
            // por motivo (todos los demas son tambien rechazos sensibles: vouchers,
            // estado Operativo/Cerrado).
            // No loguear nombre/documento del pasajero (PII) — solo IDs y motivo.
            _logger.LogWarning(
                "RemovePassengerAsync rejected. PassengerId={PassengerId} ReservaId={ReservaId}. Reason={Reason}",
                passengerId, passenger.ReservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        _context.Passengers.Remove(passenger);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<PaymentDto>> GetReservaPaymentsAsync(int reservaId)
    {
        return await _context.Payments
            .Where(p => p.ReservaId == reservaId)
            // ADR-022 §4.9 (fix S1-bis): el Payment puente del saldo a favor (Method "SaldoAFavor",
            // AffectsCash=false, monto negativo) es respaldo INTERNO; no es un cobro real. Se excluye del
            // historial de cobros de la reserva (igual que MovementsService lo excluye de Movimientos): asi el
            // usuario no ve una "fila rara negativa" borrable y "Recaudado" suma lo que el cliente pagó de
            // verdad. El saldo de la reserva NO depende de esta lista (se calcula server-side), asi que ocultar
            // el puente no descuadra el numero grande; el excedente vive en el bolsillo del cliente.
            // FC4 (2026-06-14) + Tanda D1 (2026-07-16): excluir tambien los puentes de saldo a favor APLICADO
            // (positivos), tanto el de otra reserva como el de una multa. Sin esto, aplicar saldo a favor
            // mostraria un "cobro" extra en el historial de la reserva destino.
            .Where(p => !(
                (p.Method == OverpaymentCreditCleanup.BridgeMethod && !p.AffectsCash && p.OriginalPaymentId != null)
                || (p.Method == AppliedCreditBridge.BridgeMethod && !p.AffectsCash && p.AppliedFromCreditWithdrawalId != null)
                || (p.Method == AppliedCreditBridge.PenaltyBridgeMethod && !p.AffectsCash && p.AppliedFromCreditWithdrawalId != null)))
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
    }

    public async Task<PaymentDto> AddPaymentAsync(int reservaId, Payment payment)
    {
        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // ADR-035 (2026-06-19): PRIMERA COMPUERTA tambien en el camino legacy anidado, igual que en
        // PaymentService.CreatePaymentAsync. La politica de dominio decide si el estado admite cobro (misma
        // regla que el front lee). El guard fino EnsureCollectable() (abajo) sigue siendo la defensa final.
        var paymentCapability = ReservaCapabilityPolicy
            .For(new ReservaCapabilityContext(file.Status, file.Balance, false, false, false, false))
            .CanRegisterPayment;
        if (!paymentCapability.Allowed)
            throw new InvalidOperationException(paymentCapability.Reason);

        // ADR-032 (2026-06-15): EL AGUJERO. Este path anidado (POST /api/reservas/{id}/payments) NO
        // chequeaba el estado de la reserva, dejando cobrar en Cancelada/Perdida/etc. Ahora aplica la
        // MISMA regla unica que PaymentService.CreatePaymentAsync. InvalidOperationException -> 409 en
        // ReservasController.AddPayment.
        file.EnsureCollectable();

        if (payment.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");
        if (string.IsNullOrWhiteSpace(payment.Method)) throw new ArgumentException("Debe seleccionar un mÃ©todo de pago");
        
        payment.ReservaId = reservaId;
        payment.PaidAt = payment.PaidAt == default ? DateTime.UtcNow : payment.PaidAt.ToUniversalTime();
        payment.Status = "Paid";
        payment.EntryType = PaymentEntryTypes.Payment;
        payment.AffectsCash = true;

        _context.Payments.Add(payment);

        // ARREGLO 1 (atomicidad del cobro, 2026-06-24): este path legacy anidado (POST /api/reservas/{id}/payments)
        // tenia el MISMO problema que PaymentService.CreatePaymentAsync: el alta encadenaba SaveChanges sueltos
        // (cobro -> recalculo del saldo + comision via UpdateBalanceAsync -> conversion del sobrepago en saldo a
        // favor del cliente). Si se cortaba despues de crear el credito+puente pero antes del recalculo final, el
        // excedente quedaba contado dos veces. Lo envolvemos en UNA transaccion (mismo patron que el canonico y
        // que el resto del service). En InMemory (tests) el provider no soporta transacciones: corre sin ella.
        //
        // NOTA: este path no escribe el asiento de caja del cobro (es deuda conocida del camino legacy, no la
        // tocamos aca); la transaccion igual cubre lo que SI escribe (cobro + saldo + comision + sobrepago).
        if (_context.Database.IsRelational())
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync();
                await PersistLegacyPaymentAsync();
                await transaction.CommitAsync();
            });
        }
        else
        {
            await PersistLegacyPaymentAsync();
        }

        return _mapper.Map<PaymentDto>(payment);

        // Cuerpo comun de persistencia (local function): cobro + recalculo de saldo/comision + conversion del
        // sobrepago, reusable dentro y fuera de la transaccion.
        async Task PersistLegacyPaymentAsync()
        {
            await _context.SaveChangesAsync();
            await UpdateBalanceAsync(reservaId);

            // fix bug #6 (2026-06-17): EL AGUJERO de sobrepago. Este path anidado (POST /api/reservas/{id}/payments)
            // no convertia el excedente en saldo a favor del cliente, asi que un cobro de mas dejaba un saldo
            // NEGATIVO atrapado en la reserva, invisible al bolsillo del cliente y a "aplicar saldo a favor a otra
            // reserva" (FC4). Ahora delega en el MISMO helper que el camino canonico (CreatePaymentAsync) — sin
            // duplicar la regla. El actor sale de los helpers existentes de ReservaService (GetCurrentUser*OrNull).
            await OverpaymentCreditConverter.ConvertAsync(
                _context, payment, GetCurrentUserIdOrNull(), GetCurrentUserNameOrNull(), _logger);
        }
    }

    public async Task<PaymentDto> UpdatePaymentAsync(int reservaId, int paymentId, Payment updatedPayment)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");

        if (payment.ReservaId != reservaId) throw new ArgumentException("El pago no corresponde a la Reserva");

        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // ADR-035 (2026-06-19): PRIMERA COMPUERTA tambien en el camino legacy anidado, igual que en
        // PaymentService.UpdatePaymentAsync. En estado terminal {Closed, Cancelled, Lost, PendingOperatorRefund}
        // el cobro NO se edita: la salida con rastro es anular el cobro (AnnulPaymentAsync, que NO pasa por esta
        // compuerta). Antes este path quedaba sin gate de estado y dejaba editar un cobro (sin recibo/CAE) en una
        // reserva terminal, evadiendo la regla que el front muestra como "boton apagado: anula el cobro".
        // El Balance no influye en CanEditOrDeletePayment (solo el estado); las banderas fiscales/voucher/candado
        // las enforza despues MutationGuards (la defensa final), por eso aca van en false.
        var editCapability = ReservaCapabilityPolicy
            .For(new ReservaCapabilityContext(file.Status, file.Balance, false, false, false, false))
            .CanEditOrDeletePayment;
        if (!editCapability.Allowed)
        {
            _logger.LogWarning(
                "UpdatePaymentAsync (legacy via reserva) rejected by state gate. PaymentId={PaymentId} ReservaId={ReservaId} Status={Status}.",
                paymentId, reservaId, file.Status);
            throw new InvalidOperationException(editCapability.Reason);
        }

        if (updatedPayment.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");

        // ADR-022 §4.9 (fix S1-bis): mismo candado que PaymentService.UpdatePaymentAsync para el path legacy
        // nested. El Payment puente del saldo a favor no se edita a mano (desincroniza credito y reserva).
        if (OverpaymentCreditCleanup.IsOverpaymentBridge(payment))
        {
            _logger.LogWarning(
                "UpdatePaymentAsync (legacy via reserva) rejected (direct overpayment-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, reservaId);
            throw new InvalidOperationException(OverpaymentCreditCleanup.DirectBridgeMutationBlockReason);
        }

        // FC4 (2026-06-14): mismo candado para el puente de saldo a favor aplicado (path legacy nested).
        if (AppliedCreditBridge.IsAppliedCreditBridge(payment))
        {
            _logger.LogWarning(
                "UpdatePaymentAsync (legacy via reserva) rejected (direct applied-credit-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, reservaId);
            throw new InvalidOperationException(AppliedCreditBridge.DirectBridgeMutationBlockReason);
        }

        // Tanda D1 (2026-07-16): mismo candado para el puente de saldo a favor aplicado contra una MULTA
        // (path legacy nested).
        if (AppliedCreditBridge.IsPenaltyCreditBridge(payment))
        {
            _logger.LogWarning(
                "UpdatePaymentAsync (legacy via reserva) rejected (direct penalty-credit-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, reservaId);
            throw new InvalidOperationException(AppliedCreditBridge.PenaltyDirectBridgeMutationBlockReason);
        }

        // ADR-033 (2026-06-16, E3/A2): el gate de ESTADO operativo se ELIMINO tambien en este path legacy
        // anidado, igual que en PaymentService.UpdatePaymentAsync. Editar libre lo restringe la inmutabilidad
        // fiscal (MutationGuards, abajo) + los guards de puente (arriba), no el estado de la reserva.

        // B1.15 Fase 0' (CODE-01): mismo guard que PaymentService.UpdatePaymentAsync
        // — este es el path legacy "via reserva nested". Sin esto, el bypass del
        // controller nested deja editar pagos con recibo o factura AFIP viva.
        var blockReason = await MutationGuards.GetPaymentMutationBlockReasonAsync(_context, paymentId);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdatePaymentAsync (legacy via reserva) rejected. PaymentId={PaymentId} ReservaId={ReservaId}. Reason={Reason}",
                paymentId, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        // ADR-022 §4.9 (fix S1): si cambia el monto y el cobro genero un saldo a favor de sobrepago ya
        // usado, no se permite editar (recomputar destruiria la historia de consumo). Si esta intacto, se
        // revierten los artefactos viejos antes del recalculo. El path legacy NO re-crea el saldo a favor:
        // si el monto nuevo sigue sobrepagando, el excedente queda como saldo a favor de la RESERVA (saldo
        // negativo, no fantasma), que es seguro; la conversion al bolsillo del cliente vive en PaymentService.
        bool amountChanges = updatedPayment.Amount != payment.Amount;
        if (amountChanges)
        {
            var overpaymentBlock = await OverpaymentCreditCleanup.GetConsumedBlockReasonAsync(_context, paymentId);
            if (overpaymentBlock != null)
            {
                _logger.LogWarning(
                    "UpdatePaymentAsync (legacy via reserva) rejected (overpayment credit already consumed). PaymentId={PaymentId} ReservaId={ReservaId}.",
                    paymentId, reservaId);
                throw new InvalidOperationException(overpaymentBlock);
            }
            await OverpaymentCreditCleanup.ReverseOverpaymentArtifactsAsync(
                _context, paymentId, GetCurrentUserIdOrNull(), GetCurrentUserNameOrNull());
        }

        payment.Amount = updatedPayment.Amount;
        payment.Method = updatedPayment.Method;
        payment.PaidAt = updatedPayment.PaidAt.ToUniversalTime();
        payment.Reference = updatedPayment.Reference;
        payment.Notes = updatedPayment.Notes;

        // ADR-022 §4.5 (fix 2026-06-17): re-sincronizar el Libro de Caja con el cobro editado. Igual que
        // PaymentService.UpdatePaymentAsync (camino canonico): se REVIERTE el asiento viejo y se inserta uno
        // nuevo (foto fresca: monto/metodo/fecha actuales), en la MISMA transaccion que la edicion. Antes este
        // path legacy NO tocaba la caja -> el asiento conservaba el monto viejo y la caja quedaba descuadrada.
        // El neto del libro queda: viejo (+) -> reversa (-) -> nuevo (+) = nuevo monto, sin reescribir historia.
        // Un puente/saldo a favor ya fue bloqueado arriba (AffectsCash=false igual no entra aca).
        if (payment.AffectsCash)
        {
            await CashLedgerPaymentReversal.ReverseLivePaymentEntryAsync(
                _context, payment.Id, GetCurrentUserIdOrNull(), GetCurrentUserNameOrNull());
            _context.CashLedgerEntries.Add(
                TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForPayment(
                    payment, GetCurrentUserIdOrNull(), GetCurrentUserNameOrNull()));
        }

        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);
        return _mapper.Map<PaymentDto>(payment);
    }

    public async Task DeletePaymentAsync(int reservaId, int paymentId)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");

        if (payment.ReservaId != reservaId) throw new ArgumentException("El pago no corresponde a la Reserva");

        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // ADR-035 (2026-06-19): PRIMERA COMPUERTA tambien en el camino legacy anidado, igual que en
        // PaymentService.DeletePaymentAsync. En estado terminal {Closed, Cancelled, Lost, PendingOperatorRefund}
        // el cobro NO se borra: la salida con rastro es anular el cobro (AnnulPaymentAsync, que NO pasa por esta
        // compuerta). Antes este path quedaba sin gate de estado y dejaba borrar un cobro (sin recibo/CAE) en una
        // reserva terminal, evadiendo la regla que el front muestra como "boton apagado: anula el cobro".
        // El Balance no influye en CanEditOrDeletePayment (solo el estado); las banderas fiscales/voucher/candado
        // las enforza despues DeleteGuards (la defensa final), por eso aca van en false.
        var deleteCapability = ReservaCapabilityPolicy
            .For(new ReservaCapabilityContext(file.Status, file.Balance, false, false, false, false))
            .CanEditOrDeletePayment;
        if (!deleteCapability.Allowed)
        {
            _logger.LogWarning(
                "DeletePaymentAsync (legacy via reserva) rejected by state gate. PaymentId={PaymentId} ReservaId={ReservaId} Status={Status}.",
                paymentId, reservaId, file.Status);
            throw new InvalidOperationException(deleteCapability.Reason);
        }

        // ADR-022 §4.9 (fix S1-bis): mismo candado que PaymentService.DeletePaymentAsync para el path legacy
        // nested. El Payment puente del saldo a favor no se borra a mano (deja credito fantasma + deuda inflada).
        if (OverpaymentCreditCleanup.IsOverpaymentBridge(payment))
        {
            _logger.LogWarning(
                "DeletePaymentAsync (legacy via reserva) rejected (direct overpayment-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, reservaId);
            throw new InvalidOperationException(OverpaymentCreditCleanup.DirectBridgeMutationBlockReason);
        }

        // FC4 (2026-06-14): mismo candado para el puente de saldo a favor aplicado (path legacy nested).
        if (AppliedCreditBridge.IsAppliedCreditBridge(payment))
        {
            _logger.LogWarning(
                "DeletePaymentAsync (legacy via reserva) rejected (direct applied-credit-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, reservaId);
            throw new InvalidOperationException(AppliedCreditBridge.DirectBridgeMutationBlockReason);
        }

        // Tanda D1 (2026-07-16): mismo candado para el puente de saldo a favor aplicado contra una MULTA
        // (path legacy nested).
        if (AppliedCreditBridge.IsPenaltyCreditBridge(payment))
        {
            _logger.LogWarning(
                "DeletePaymentAsync (legacy via reserva) rejected (direct penalty-credit-bridge mutation). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, reservaId);
            throw new InvalidOperationException(AppliedCreditBridge.PenaltyDirectBridgeMutationBlockReason);
        }

        // ADR-033 (2026-06-16, E3/A2): el gate de ESTADO operativo se ELIMINO tambien en este path legacy
        // anidado, igual que en PaymentService.DeletePaymentAsync. El borrado libre lo restringe la
        // inmutabilidad fiscal (DeleteGuards, abajo) + los guards de puente (arriba), no el estado de la
        // reserva. La salida para un cobro fiscalmente sellado sigue siendo la anulacion con rastro
        // (POST /api/payments/{id}/annul).

        // C28: mismo guard que PaymentService.DeletePaymentAsync — este es el path
        // legacy "via reserva nested" (ReservasController.DeletePayment).
        var blockReason = await DeleteGuards.GetPaymentDeleteBlockReasonAsync(_context, paymentId);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "DeletePaymentAsync (legacy via reserva) rejected. PaymentId={PaymentId} ReservaId={ReservaId}. Reason={Reason}",
                paymentId, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        // ADR-022 §4.9 (fix S1): el path legacy tambien puede anular un cobro que genero un saldo a favor de
        // sobrepago (el credito se crea en PaymentService, pero se borra por aca). Mismo candado: si ese
        // saldo a favor ya fue usado, no se anula; si esta intacto, se revierte el puente y se anula el
        // credito ANTES del recalculo para no dejar credito fantasma ni inflar la deuda.
        var overpaymentBlock = await OverpaymentCreditCleanup.GetConsumedBlockReasonAsync(_context, paymentId);
        if (overpaymentBlock != null)
        {
            _logger.LogWarning(
                "DeletePaymentAsync (legacy via reserva) rejected (overpayment credit already consumed). PaymentId={PaymentId} ReservaId={ReservaId}.",
                paymentId, reservaId);
            throw new InvalidOperationException(overpaymentBlock);
        }
        await OverpaymentCreditCleanup.ReverseOverpaymentArtifactsAsync(
            _context, paymentId, GetCurrentUserIdOrNull(), GetCurrentUserNameOrNull());

        payment.IsDeleted = true;
        payment.DeletedAt = DateTime.UtcNow;

        // ADR-022 §4.5 (fix 2026-06-17): el camino canonico (PaymentService.DeletePaymentCoreAsync) escribe
        // el contra-asiento de caja al dar de baja un cobro; este camino legacy anidado NO lo hacia -> al
        // borrar por aca un cobro que movio caja, el asiento de ingreso quedaba vivo y la caja se inflaba.
        // Mismo helper, en la MISMA transaccion (antes del SaveChanges): si el cobro afecta caja, se revierte
        // su asiento vigente (no-op si no tiene asiento, p.ej. legacy sin backfill). Un puente/saldo a favor
        // tiene AffectsCash=false, asi que no entra aca (ademas ya fue bloqueado/limpiado arriba).
        if (payment.AffectsCash)
        {
            await CashLedgerPaymentReversal.ReverseLivePaymentEntryAsync(
                _context, payment.Id, GetCurrentUserIdOrNull(), GetCurrentUserNameOrNull());
        }

        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);
    }

    public async Task<Reserva> UpdateStatusAsync(int id, string status, string? actorUserId = null)
    {
        var file = await _context.Reservas.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // Refrescamos el saldo (sin disparar el motor de estados: estamos en una transicion MANUAL).
        await RecalculateMoneyAsync(id);
        file = await _context.Reservas.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // Estado origen ANTES de la transicion (para el rastro auditable).
        var fromStatus = file.Status;

        // ADR-020: whitelist de estados-destino aceptables via transicion MANUAL. Confirmed NO esta
        // (solo el motor automatico lleva InManagement -> Confirmed; INV-020-02). Cualquier string
        // fuera de esta lista (incluidos los difuntos "Sold" y "ToSettle") rebota con ArgumentException.
        // ADR-036 (2026-06-21): se quito ToSettle (estado eliminado).
        var validStatuses = new[]
        {
            EstadoReserva.Quotation, EstadoReserva.Budget, EstadoReserva.InManagement,
            EstadoReserva.Traveling, EstadoReserva.Closed,
            EstadoReserva.Lost, EstadoReserva.Cancelled
        };
        if (!validStatuses.Contains(status)) throw new ArgumentException("Estado no válido");

        await ApplyTransitionAsync(file, id, status);

        // ADR-020 (INV-020-06): toda transicion manual REAL escribe ReservaStatusChangeLog. El set
        // idempotente (mismo estado, no-op en ApplyTransitionAsync) no genera log.
        var isRealChange = !string.Equals(fromStatus, status, StringComparison.OrdinalIgnoreCase);

        // Cambio de estado + rastro auditable + limpieza de marcas por el PUNTO ÚNICO de transición. El log solo se
        // escribe si es un cambio real (mismo criterio de antes). Novedad util: pasar manualmente a un estado
        // terminal (Cancelled/Lost/Closed) ahora DESCARTA la marca "confirmada con cambios" que hubiera quedado
        // colgada (antes esta transicion no limpiaba nada). Este overload no recibe nombre de actor ni motivo, asi
        // que el log conserva exactamente los mismos campos que antes (solo ByUserId).
        await TravelApi.Infrastructure.Reservations.ReservaStatusTransitioner.ApplyAsync(
            _context, file, status, "Forward",
            actorUserId, actorUserName: null, reason: null, ct: CancellationToken.None);

        // CRM leads (auditoria ERP 2026-06-13, decision del dueño): el lead de origen pasa a Ganado
        // recien cuando la reserva linkeada llega a un estado EN FIRME (el cliente acepto el presupuesto),
        // no al crear la reserva. Este es el camino MANUAL (Budget -> InManagement). NO es el unico: el
        // mismo disparo vive ahora en SourceLeadWonHook y se invoca desde el motor de estados
        // (auto-confirmacion + reconciliacion), el job de lifecycle y el revert de una Cancelada (fix de
        // fondo 2026-06-18, que cubria solo este punto). Idempotente: si el lead ya estaba Ganado/Perdido,
        // no se toca, por eso es seguro que varios caminos lo llamen.
        if (isRealChange)
        {
            await MarkSourceLeadAsWonIfReservaIsFirmAsync(file);
        }

        await _context.SaveChangesAsync();
        return file;
    }

    /// <summary>
    /// CRM leads: marca el lead de origen como Ganado si la <paramref name="file"/> esta en venta operativa
    /// viva. Delega en la regla UNICA <see cref="SourceLeadWonHook"/> (compartida con el motor de estados, el
    /// job de lifecycle y el revert) para que TODA entrada a un estado firme dispare el mismo criterio.
    ///
    /// <para>2026-06-18 (fix de fondo): antes esta logica era privada de ReservaService y solo corria en la
    /// transicion MANUAL Budget -&gt; InManagement, dejando sin marcar los leads cuya reserva llegaba a firme
    /// por auto-confirmacion, el job o el revert. Se movio el cuerpo a <see cref="SourceLeadWonHook"/> y este
    /// metodo quedo como un fino envoltorio para no tocar los call-sites internos (UpdateStatusAsync).</para>
    /// </summary>
    private Task MarkSourceLeadAsWonIfReservaIsFirmAsync(Reserva file)
        => SourceLeadWonHook.MarkSourceLeadAsWonIfReservaIsFirmAsync(_context, file);

    // ============================================================
    // ADR-020: una sola funcion de transicion manual (murio la bifurcacion clasico/nuevo). Valida
    // contra la matriz forward unica y aplica los gates en el paso correcto. NO hace SaveChanges:
    // el caller (UpdateStatusAsync) persiste una sola vez.
    // ============================================================

    /// <summary>
    /// Aplica una transicion manual del ciclo unico (ADR-020): Quotation -> Budget -> InManagement
    /// -> [Confirmed automatico] -> Traveling -> Closed, con Lost/Cancelled laterales. ADR-036 (2026-06-21,
    /// prepago puro): murio ToSettle. Gates:
    ///  - Quotation -&gt; Budget: ≥1 servicio cargado.
    ///  - Quotation/Budget -&gt; Lost: sin pagos vivos (M4).
    ///  - Budget -&gt; InManagement: readiness (≥1 servicio + normalizar a Solicitado + pasajeros nominales).
    ///  - Confirmed -&gt; Traveling: capacidad + CLIENTE SALDADO (candado duro e incondicional, ADR-036).
    ///  - Traveling -&gt; Closed: bloquea saldo pendiente + estampa ClosedAt.
    ///  - {InManagement, Confirmed} -&gt; Cancelled (B5): el gate "sin factura viva" + "sin cobros vivos"
    ///    (ADR-036) + permisos corre en el wrapper publico/camino compartido; la matriz garantiza los estados
    ///    de origen validos. ADR-035 (2026-06-19): Traveling YA NO cancela (se corrige por NC/ajuste).
    ///
    /// Confirmed como destino NO esta en la matriz: solo el motor automatico lleva a Confirmed (INV-020-02).
    /// </summary>
    private async Task ApplyTransitionAsync(Reserva file, int id, string status)
    {
        // Set idempotente (mismo estado): no-op.
        if (string.Equals(file.Status, status, StringComparison.OrdinalIgnoreCase))
            return;

        if (!AllowedForwardTransitions.TryGetValue(file.Status, out var allowedTargets)
            || !allowedTargets.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"No se puede pasar de {file.Status} a {status}. " +
                $"Transiciones permitidas desde {file.Status}: " +
                $"{(allowedTargets == null || allowedTargets.Length == 0 ? "(ninguna hacia adelante)" : string.Join(", ", allowedTargets))}.");
        }

        var settings = await _operationalFinanceSettingsService.GetEntityAsync(CancellationToken.None);

        // Quotation -> Budget: exige al menos un servicio cargado.
        if (file.Status == EstadoReserva.Quotation && status == EstadoReserva.Budget)
        {
            var hasServices = await HasServicesAsync(id);
            if (!hasServices)
                throw new InvalidOperationException(
                    "No se puede pasar a Presupuesto sin al menos un servicio cargado. Agrega un servicio primero.");
        }

        // Quotation/Budget -> Lost: solo si NO hay pagos vivos (M4). El path legacy AddPaymentAsync
        // no tiene gate de estado, asi que una cotizacion podria tener pagos cargados.
        if (status == EstadoReserva.Lost)
        {
            var hasLivePayments = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted);
            if (hasLivePayments)
                throw new InvalidOperationException(
                    "No se puede marcar como Perdida una reserva con pagos registrados. Elimina los pagos primero.");
        }

        // Budget -> InManagement: readiness (≥1 servicio + normalizar a Solicitado + pasajeros nominales).
        if (file.Status == EstadoReserva.Budget && status == EstadoReserva.InManagement)
        {
            await EnsureReadinessForSaleAsync(id);
        }

        // Confirmed -> Traveling: capacidad + economico (servicios ya resueltos por el motor).
        if (file.Status == EstadoReserva.Confirmed && status == EstadoReserva.Traveling)
        {
            await EnsureCanStartTravelingAsync(file, id, settings, checkUnconfirmedServices: false);
        }

        // Traveling -> Closed: bloquea saldo pendiente (solo prepago) + estampa ClosedAt. (ADR-036: ya no hay
        // ToSettle.) ADR-040 (review B4): un cliente a cuenta corriente puede cerrar debiendo.
        if (status == EstadoReserva.Closed)
        {
            var closingOnAccount =
                await ClientCreditGate.ResolveModeAsync(_context, file.PayerId, settings, CancellationToken.None)
                    == CustomerBillingMode.Account;
            EnsureCanCloseAndStampClosedAt(file, closingOnAccount);

            // B2 (2026-06-24): al FINALIZAR la reserva, sus servicios RESUELTOS pasan a "Finalizado"
            // (prestado/cumplido). Delega en la FUENTE UNICA compartida con el cierre por el job
            // (ReservaServiceFinalizer), para que el cierre manual y el automatico finalicen igual. NO hace
            // SaveChanges: corre en la misma unidad de trabajo que la transicion (UpdateStatusAsync persiste
            // una sola vez), asi servicios y estado quedan atomicos.
            await Reservations.ReservaServiceFinalizer.MarkResolvedServicesFinalizedAsync(_context, id);
        }

        // Cancelled manual (B5): GATE FISCAL en el camino COMPARTIDO. Antes vivia solo en el wrapper
        // publico UpdateStatusAsync(string,...), asi que el overload int (usado por tests y posibles
        // callers internos) lo salteaba: una reserva con factura CAE viva podia cancelarse sin anular,
        // dejando un comprobante fiscal valido para una reserva inexistente. Ahora corre aca, sobre el
        // unico camino de transicion. Los PERMISOS (reservas.cancel / cancel_with_payment) siguen en el
        // wrapper publico porque dependen del actor y del HttpContext (son authz, no integridad fiscal).
        if (status == EstadoReserva.Cancelled)
        {
            var hasLiveCae = await _context.Invoices.AnyAsync(
                i => i.ReservaId == id
                    && !CreditNoteComprobanteTypes.Contains(i.TipoComprobante) // excluye NC (nace para anular)
                    && !string.IsNullOrEmpty(i.CAE)
                    && i.AnnulmentStatus != AnnulmentStatus.Succeeded);
            if (hasLiveCae)
            {
                throw new InvalidOperationException(
                    "La reserva tiene facturas con CAE vigentes. Debe anularlas (se emitira Nota de Credito) antes de cancelar la reserva.");
            }

            // ADR-036 (2026-06-21): refuerzo del guard de escritura. Una reserva con COBROS VIVOS no admite
            // baja simple (cancelacion directa): hay que deshacerla por el camino formal de anulacion (NC/ND,
            // ADR-002), que revierte la plata con rastro. Antes este guard solo bloqueaba por CAE vivo, asi que
            // una reserva con pagos pero sin factura podia cancelarse de una y dejar cobros huerfanos. Cuenta
            // CUALQUIER pago no soft-deleted, INCLUIDOS los puente (AffectsCash=false): tambien son plata viva.
            var hasLivePayments = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted);
            if (hasLivePayments)
            {
                throw new InvalidOperationException(ReservaCapabilityPolicy.HasLiveMoneyMustAnnulReason);
            }
        }
    }

    /// <summary>
    /// Gate de readiness para pasar de Presupuesto a En gestion (Budget -&gt; InManagement):
    /// exige ≥1 servicio + cantidad de pasajeros DECLARADA &gt; 0 + normaliza los servicios a
    /// "Solicitado".
    ///
    /// <para>ADR-031 (2026-06-15): este gate YA NO exige los pasajeros NOMINALES (nombre/documento).
    /// Cuando el cliente acepta el presupuesto la agencia todavia no tiene (ni necesita) los nombres
    /// legales de todos; frenar el avance del file aca era friccion innecesaria. La exigencia de
    /// nombres se MOVIO al momento en que cada servicio se reserva/emite con el operador
    /// (<see cref="TravelApi.Domain.Reservations.PassengerNominalRules"/>, invocado desde BookingService).
    /// Aca queda solo la CANTIDAD.</para>
    /// </summary>
    private async Task EnsureReadinessForSaleAsync(int id)
    {
        var hasServices = await HasServicesAsync(id);
        if (!hasServices)
            // G4 (2026-06-24): mensaje claro y accionable, SIN jerga interna. En este punto el documento todavia
            // es un PRESUPUESTO (pre-venta), por eso NO decimos "reserva"/"confirmar"/"reservar": el usuario
            // marca "El cliente aceptó". El bloqueo de fondo (≥1 servicio) se mantiene: un presupuesto vacio no
            // puede aceptarse.
            throw new InvalidOperationException("Agregá al menos un servicio antes de marcar que el cliente aceptó.");

        // Normalizacion defensiva: en Presupuesto cualquier servicio debe estar en
        // "Solicitado". Si por algun bypass (API directa, data preexistente) hay
        // alguno con otro status, lo forzamos al pasar al siguiente estado. El agente despues
        // los confirma uno por uno antes de pasar a Operativo.
        await NormalizeAllServicesToSolicitadoAsync(id);

        // Fuente UNICA del conteo esperado = la cantidad DECLARADA de la RESERVA
        // (AdultCount + ChildCount + InfantCount), la que el usuario carga en
        // Cotizacion/Presupuesto via PATCH /passenger-counts. Antes esto se inferia de los
        // servicios (ComputePaxCompositionFromServices), lo que daba resultados inconsistentes
        // (0 a veces, 3 otras) y dejaba pasar reservas con 0 pasajeros. La cantidad de pax
        // de cada servicio (FlightSegment.PassengerCount, HotelBooking.Adults, etc.) es dato
        // del servicio y NO se usa para contar pasajeros nominales de la reserva.
        var reservaForPax = await _context.Reservas
            .AsNoTracking()
            .FirstAsync(r => r.Id == id);
        var declaredPax = reservaForPax.AdultCount + reservaForPax.ChildCount + reservaForPax.InfantCount;

        // Regla A: NUNCA 0 pasajeros. Una reserva no puede avanzar a En gestion sin al menos
        // un pasajero declarado. Antes, con declaredPax==0, el if>0 saltaba toda la validacion
        // y permitia avanzar en silencio con 0 pasajeros.
        if (declaredPax <= 0)
        {
            throw new InvalidOperationException(
                "Declará al menos 1 pasajero antes de marcar que el cliente aceptó.");
        }

        // ADR-031: ya NO se exigen los pasajeros NOMINALES (nombre/documento) en este punto. Esa
        // exigencia se movio al momento de resolver/emitir cada servicio con el operador (gate por
        // tipo en PassengerNominalRules, invocado desde BookingService). Aca solo se valida la CANTIDAD.
    }

    /// <summary>
    /// ADR-031: gate de pasajeros nominales para el servicio GENERICO (ServicioReserva), espejo del
    /// envoltorio de BookingService. Solo la TRANSICION no-resuelto -> resuelto exige el nombre de TODOS
    /// los declarados (regla Generico) ANTES de persistir; editar un generico que YA estaba resuelto no
    /// re-valida (la cobertura ya se exigio cuando se resolvio). Hoy el alta fuerza "Solicitado" y la
    /// edicion no toca el Status, asi que es defensivo (no hay transicion posible por estas rutas); se
    /// mantiene para que ninguna ruta futura confirme un generico sin nombres y el motor auto-confirme la
    /// reserva. Mensaje sin numero de documento (lo garantiza PassengerNominalRules).
    /// </summary>
    private async Task EnsureGenericNominalCoverageBeforeResolvingAsync(
        ServicioReserva service, bool serviceWasResolved, CancellationToken ct)
    {
        // Solo la transicion no-resuelto -> resuelto exige nombres.
        if (serviceWasResolved || !ServiceResolutionRules.IsResolved(service))
            return;
        if (!service.ReservaId.HasValue)
            return;

        // v2.1: el gate opera sobre el SET del servicio generico (asignaciones explicitas si existen; si
        // no, toda la reserva), igual que el envoltorio de BookingService. service.Id puede ser 0 en un
        // alta (todavia sin Id) -> no hay asignaciones -> set = toda la reserva (default seguro).
        var serviceSet = await ResolveServiceSetAsync(
            service.ReservaId.Value, AssignmentServiceType.Generic, service.Id, ct);
        PassengerNominalRules.EnsureCovered(serviceSet, PassengerNominalRules.ServiceKind.Generic);
    }

    /// <summary>
    /// ADR-031 v2.1 (§4.2): resuelve el SET de pasajeros de un servicio para el gate generico / preview.
    /// Lee de la DB las dos colecciones (pasajeros de la reserva + ids asignados a este servicio) y delega
    /// la regla de seleccion en el helper PURO <see cref="PassengerNominalRules.ResolveServiceSet"/>, para
    /// que ReservaService y BookingService resuelvan el set EXACTAMENTE igual (fuente unica). serviceId&lt;=0
    /// (alta sin Id) => no hay asignaciones => set = toda la reserva.
    /// </summary>
    private async Task<IReadOnlyList<Passenger>> ResolveServiceSetAsync(
        int reservaId, string assignmentServiceType, int serviceId, CancellationToken ct)
    {
        var reservaPassengers = await _context.Passengers
            .AsNoTracking()
            .Where(p => p.ReservaId == reservaId)
            .ToListAsync(ct);

        var assignedPassengerIds = serviceId > 0
            ? await _context.PassengerServiceAssignments
                .AsNoTracking()
                .Where(a => a.ServiceType == assignmentServiceType && a.ServiceId == serviceId)
                .Select(a => a.PassengerId)
                .ToListAsync(ct)
            : new List<int>();

        return PassengerNominalRules.ResolveServiceSet(reservaPassengers, assignedPassengerIds);
    }

    /// <summary>
    /// ADR-031 v2.1 (§10.9, punto 6): expone al front, POR SERVICIO, los pasajeros del SET y que nombres
    /// faltan, para que el mini-form de nombres sepa a QUIEN pedirle los datos. Usa EXACTAMENTE la misma
    /// resolucion del set (<see cref="PassengerNominalRules.ResolveServiceSet"/>) y la misma matriz por tipo
    /// que el gate del backend, asi front y back nunca se contradicen. El <paramref name="serviceType"/> es
    /// el discriminator (Hotel/Transfer/Package/Flight/Assistance/Generic); el servicio se identifica por su
    /// publicId/legacy id. Respuesta SIN numero de documento.
    /// </summary>
    public async Task<ServiceNominalCoverageDto> GetServiceNominalCoverageAsync(
        string reservaPublicIdOrLegacyId, string serviceType, string servicePublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var serviceId = await ResolveAndValidateServiceIdAsync(serviceType, servicePublicIdOrLegacyId, reservaId, ct);
        return await BuildServiceNominalCoverageDtoAsync(reservaId, serviceType, serviceId, ct);
    }

    /// <summary>
    /// ADR-031 v2.1: REEMPLAZO TOTAL ATOMICO del set de pasajeros de un servicio. El front antes hacia
    /// "borrar todas + crear N" en llamadas separadas; si fallaba a la mitad quedaba un set inconsistente.
    /// Aca todo ocurre en UNA unidad de trabajo: validacion -> borrar asignaciones actuales -> crear solo
    /// las pedidas -> auditar -> UN SOLO SaveChanges. O entra todo o no entra nada.
    ///
    /// <para>Normalizacion "todos" (§3.2, invariante "todos = sin asignaciones"): si la lista pedida es
    /// vacia O es exactamente igual a TODOS los pasajeros de la reserva, NO se crean asignaciones (el set
    /// queda implicito = toda la reserva). Subconjunto estricto => se crean solo esas. Asi una asignacion
    /// explicita siempre significa "este servicio es para MENOS que todos".</para>
    ///
    /// <para>Idempotente: llamarlo dos veces con el mismo set deja el mismo estado final.</para>
    /// </summary>
    public async Task<ServiceNominalCoverageDto> ReplaceServiceAssignmentsAsync(
        string reservaPublicIdOrLegacyId, string serviceType, string servicePublicIdOrLegacyId,
        ReplaceServiceAssignmentsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);

        // ADR-036: reemplazar el set de pasajeros de un servicio es editar pasajeros. En "En viaje" y en los
        // terminales la reserva es solo lectura DURA -> mismo candado por estado, ANTES de cualquier mutacion.
        await ReservaCapacityRules.EnsurePassengersEditableByStateAsync(_context, reservaId, ct);

        var serviceId = await ResolveAndValidateServiceIdAsync(serviceType, servicePublicIdOrLegacyId, reservaId, ct);

        // Pasajeros de la reserva: los necesitamos para (a) traducir publicId -> Id interno validando
        // ownership, y (b) detectar el caso "todos" para la normalizacion.
        var reservaPassengers = await _context.Passengers
            .Where(p => p.ReservaId == reservaId)
            .ToListAsync(ct);
        var passengerByPublicId = reservaPassengers.ToDictionary(p => p.PublicId, p => p);

        // Traducir cada publicId pedido a su Id interno, validando que pertenezca a ESTA reserva. Un publicId
        // de otra reserva (o inexistente) => rechazo. Deduplicamos por si el front manda repetidos.
        var requestedPassengerIds = new HashSet<int>();
        var requestedPublicIds = request.PassengerPublicIds ?? Array.Empty<string>();
        foreach (var rawPublicId in requestedPublicIds)
        {
            if (!Guid.TryParse(rawPublicId, out var passengerPublicId)
                || !passengerByPublicId.TryGetValue(passengerPublicId, out var passenger))
            {
                throw new InvalidOperationException("Uno de los pasajeros indicados no pertenece a esta reserva.");
            }
            requestedPassengerIds.Add(passenger.Id);
        }

        // Normalizacion "todos = sin asignaciones": set vacio, o pidio exactamente a todos -> CERO filas.
        // Cualquier otro caso (subconjunto estricto) -> creamos asignaciones solo de los pedidos.
        var isEffectivelyAll = requestedPassengerIds.Count == 0
            || requestedPassengerIds.Count == reservaPassengers.Count;
        var idsToPersist = isEffectivelyAll
            ? new HashSet<int>()
            : requestedPassengerIds;

        // 1) Borrar las asignaciones actuales del servicio (set completo, reemplazo total).
        var currentAssignments = await _context.PassengerServiceAssignments
            .Where(a => a.ServiceType == serviceType && a.ServiceId == serviceId)
            .ToListAsync(ct);
        var previousAssignedCount = currentAssignments.Count;
        if (currentAssignments.Count > 0)
            _context.PassengerServiceAssignments.RemoveRange(currentAssignments);

        // 2) Crear las nuevas (solo si quedo un subconjunto estricto tras la normalizacion).
        foreach (var passengerId in idsToPersist)
        {
            _context.PassengerServiceAssignments.Add(new PassengerServiceAssignment
            {
                PassengerId = passengerId,
                ServiceType = serviceType,
                ServiceId = serviceId,
                CreatedAt = DateTime.UtcNow
            });
        }

        // 3) Auditar el reemplazo como UN solo evento (con conteos), STAGEADO para entrar en el mismo
        //    SaveChanges que el borrado + las altas => atomico. details sin documento ni nombres.
        StageReplaceAssignmentsAudit(
            serviceType, serviceId, reservaId,
            previousAssignedCount, idsToPersist.Count, isEffectivelyAll);

        // 4) UNA sola escritura: borrado + altas + audit entran juntos o no entra nada.
        await _context.SaveChangesAsync(ct);

        // 5) Devolver el contrato actualizado (mismo shape que el GET) para que el front no re-pida.
        return await BuildServiceNominalCoverageDtoAsync(reservaId, serviceType, serviceId, ct);
    }

    /// <summary>
    /// ADR-031 v2.1: resuelve el Id interno del servicio a partir de su tipo + publicId/legacy id y valida
    /// que pertenezca a la reserva (defensa en profundidad sobre el ownership de la ruta). Centraliza el
    /// switch por tipo que comparten el GET de nominal-coverage y el PUT de reemplazo, para que ambos
    /// resuelvan/validen el servicio EXACTAMENTE igual (fuente unica). Lanza ArgumentException si el tipo es
    /// invalido, KeyNotFoundException si el servicio no existe, InvalidOperationException si es de otra reserva.
    /// </summary>
    private async Task<int> ResolveAndValidateServiceIdAsync(
        string serviceType, string servicePublicIdOrLegacyId, int reservaId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceType) || !AssignmentServiceType.All.Contains(serviceType))
            throw new ArgumentException($"ServiceType invalido. Valores aceptados: {string.Join(", ", AssignmentServiceType.All)}.");

        var serviceId = serviceType switch
        {
            AssignmentServiceType.Hotel => await ResolveRequiredIdAsync<HotelBooking>(servicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Transfer => await ResolveRequiredIdAsync<TransferBooking>(servicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Package => await ResolveRequiredIdAsync<PackageBooking>(servicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Flight => await ResolveRequiredIdAsync<FlightSegment>(servicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Assistance => await ResolveRequiredIdAsync<AssistanceBooking>(servicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Generic => await ResolveRequiredIdAsync<ServicioReserva>(servicePublicIdOrLegacyId, ct),
            _ => throw new ArgumentException("ServiceType no soportado.")
        };

        var serviceBelongsToReserva = serviceType switch
        {
            AssignmentServiceType.Hotel => await _context.HotelBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Transfer => await _context.TransferBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Package => await _context.PackageBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Flight => await _context.FlightSegments.AnyAsync(f => f.Id == serviceId && f.ReservaId == reservaId, ct),
            AssignmentServiceType.Assistance => await _context.AssistanceBookings.AnyAsync(a => a.Id == serviceId && a.ReservaId == reservaId, ct),
            AssignmentServiceType.Generic => await _context.Servicios.AnyAsync(s => s.Id == serviceId && s.ReservaId == reservaId, ct),
            _ => false
        };
        if (!serviceBelongsToReserva)
            throw new InvalidOperationException("El servicio no pertenece a esta reserva.");

        return serviceId;
    }

    /// <summary>
    /// ADR-031 v2.1: arma el <see cref="ServiceNominalCoverageDto"/> de un servicio YA resuelto/validado.
    /// Lo comparten el GET de nominal-coverage y el PUT de reemplazo (que lo devuelve tras escribir), asi el
    /// shape de la respuesta es identico en los dos caminos. Usa la MISMA resolucion del set
    /// (<see cref="PassengerNominalRules.ResolveServiceSet"/>) y la misma matriz por tipo que el gate del
    /// backend. Respuesta SIN numero de documento.
    /// </summary>
    private async Task<ServiceNominalCoverageDto> BuildServiceNominalCoverageDtoAsync(
        int reservaId, string serviceType, int serviceId, CancellationToken ct)
    {
        // Todos los pasajeros de la reserva (para el "N" de "X de N") + ids asignados a este servicio.
        var reservaPassengers = await _context.Passengers
            .AsNoTracking()
            .Where(p => p.ReservaId == reservaId)
            .ToListAsync(ct);
        var assignedPassengerIds = await _context.PassengerServiceAssignments
            .AsNoTracking()
            .Where(a => a.ServiceType == serviceType && a.ServiceId == serviceId)
            .Select(a => a.PassengerId)
            .ToListAsync(ct);

        var serviceSet = PassengerNominalRules.ResolveServiceSet(reservaPassengers, assignedPassengerIds);
        var serviceKind = MapToServiceKind(serviceType);
        var lead = PassengerNominalRules.GetLeadPassenger(serviceSet);

        var dto = new ServiceNominalCoverageDto
        {
            ServiceType = serviceType,
            ServiceId = serviceId,
            ServicePublicId = await ResolveServicePublicIdAsync(serviceType, serviceId, ct),
            HasExplicitAssignments = assignedPassengerIds.Count > 0,
            ServiceSetCount = serviceSet.Count,
            ReservaPassengerCount = reservaPassengers.Count,
            MissingMessage = PassengerNominalRules.GetMissing(serviceSet, serviceKind),
        };

        foreach (var passenger in serviceSet.OrderBy(p => p.Id))
        {
            var isLead = lead != null && passenger.Id == lead.Id;
            dto.ServiceSet.Add(new ServiceSetPassengerDto
            {
                PassengerPublicId = passenger.PublicId,
                FullName = passenger.FullName,
                IsLead = isLead,
                HasRequiredDataForServiceType =
                    PassengerNominalRules.PassengerHasRequiredData(passenger, serviceKind, isLead),
            });
        }

        return dto;
    }

    /// <summary>
    /// ADR-031 v2.1 (§6.5): audita el REEMPLAZO TOTAL del set como UN solo evento con conteos, en vez de N
    /// altas + M bajas sueltas (mas legible para una operacion bulk). STAGEADO con
    /// <c>StageBusinessEvent</c> (no guarda) para entrar en el MISMO SaveChanges que el borrado + las altas
    /// => atomico (mismo patron que la cascada de borrado §4.3). Best-effort: sin IAuditService inyectado no
    /// hace nada; la integridad del set no depende del audit. details SIN numero de documento ni nombres.
    /// </summary>
    private void StageReplaceAssignmentsAudit(
        string serviceType, int serviceId, int reservaId,
        int previousAssignedCount, int newAssignedCount, bool normalizedToAll)
    {
        if (_auditService is null) return;

        var (userId, userName) = ResolveAuditActor();
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            serviceType,
            serviceId,
            reservaId,
            previousAssignedCount,
            newAssignedCount,
            normalizedToAll
        });
        _auditService.StageBusinessEvent(
            AuditActions.PassengerAssignmentsReplaced,
            AuditActions.PassengerServiceAssignmentEntityName,
            serviceId.ToString(),
            details,
            userId ?? string.Empty,
            userName);
    }

    /// <summary>Traduce el discriminator string del assignment al enum del helper de dominio.</summary>
    private static PassengerNominalRules.ServiceKind MapToServiceKind(string assignmentServiceType)
        => assignmentServiceType switch
        {
            AssignmentServiceType.Hotel => PassengerNominalRules.ServiceKind.Hotel,
            AssignmentServiceType.Transfer => PassengerNominalRules.ServiceKind.Transfer,
            AssignmentServiceType.Package => PassengerNominalRules.ServiceKind.Package,
            AssignmentServiceType.Flight => PassengerNominalRules.ServiceKind.Flight,
            AssignmentServiceType.Assistance => PassengerNominalRules.ServiceKind.Assistance,
            AssignmentServiceType.Generic => PassengerNominalRules.ServiceKind.Generic,
            _ => throw new ArgumentException("ServiceType no soportado.")
        };

    /// <summary>
    /// ADR-031 v2.1 (M1, §4.3 — bloqueante de integridad): borra las <c>PassengerServiceAssignment</c> de
    /// un servicio que se esta borrando por ESTE path (<see cref="RemoveServiceAsync"/>). Mismo motivo que el
    /// gemelo de BookingService: <c>ServiceId</c> es soft-FK, EF no cascadea, y el Id se reusa -> sin esta
    /// limpieza un servicio nuevo heredaria el set del muerto. NO hace SaveChanges: el caller marca el
    /// borrado del servicio y la baja de asignaciones en el MISMO contexto y cierra todo con un solo
    /// SaveChanges (atomico).
    ///
    /// <para><b>Atomicidad (I-ATOM / M-ATOM-1)</b>: la auditoria se STAGEA con <c>StageBusinessEvent</c> (no
    /// se guarda) para que el alta del audit entre en ese mismo SaveChanges. Antes usaba
    /// <c>LogBusinessEventAsync</c>, que hace AddAsync + SaveChanges inmediato y flusheaba la baja de
    /// asignaciones en una transaccion separada ANTES del borrado del servicio, rompiendo la atomicidad que
    /// promete el ADR §4.3. details sin numero de documento.</para>
    /// </summary>
    private async Task CleanupAssignmentsForDeletedServiceAsync(
        string assignmentServiceType, int serviceId, int reservaId, CancellationToken ct)
    {
        var orphanAssignments = await _context.PassengerServiceAssignments
            .Where(a => a.ServiceType == assignmentServiceType && a.ServiceId == serviceId)
            .ToListAsync(ct);

        if (orphanAssignments.Count == 0)
            return;

        _context.PassengerServiceAssignments.RemoveRange(orphanAssignments);

        if (_auditService is not null)
        {
            var (userId, userName) = ResolveAuditActor();
            var details = System.Text.Json.JsonSerializer.Serialize(new
            {
                serviceType = assignmentServiceType,
                serviceId,
                reservaId,
                removedAssignmentCount = orphanAssignments.Count
            });
            _auditService.StageBusinessEvent(
                AuditActions.PassengerUnassignedFromServiceByDelete,
                AuditActions.PassengerServiceAssignmentEntityName,
                serviceId.ToString(),
                details,
                userId ?? string.Empty,
                userName);
        }
    }

    /// <summary>
    /// ADR-020 (M5): gate UNIFICADO "volver a Presupuesto" (InManagement -&gt; Budget). UNA sola copia
    /// llamada tanto desde RevertStatusAsync como desde ApplyTransitionAsync. No se puede volver a
    /// Presupuesto si hay pagos vivos, facturas, o algun servicio RESUELTO (si algo ya se confirmo/
    /// resolvio con un operador, el camino es cancelar ese servicio, no retroceder el file).
    ///
    /// <para>El viejo check "tiene servicios cargados" MURIO: en el ciclo nuevo
    /// Budget -&gt; InManagement exige ≥1 servicio, asi que ese check impediria volver para siempre.</para>
    /// </summary>
    private async Task EnsureCanRevertToBudgetAsync(int id, CancellationToken ct = default)
    {
        var hasPayments = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted, ct);
        if (hasPayments) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay pagos registrados. Eliminalos primero.");

        var hasInvoices = await _context.Invoices.AnyAsync(i => i.ReservaId == id, ct);
        if (hasInvoices) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay facturas emitidas. Debes anularlas primero (Nota de Credito).");

        var hasResolved = await HasResolvedServicesAsync(id, ct);
        if (hasResolved) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay servicios ya resueltos/confirmados con el operador. Cancela esos servicios primero.");
    }

    /// <summary>
    /// ADR-020: indica si la reserva tiene al menos un servicio RESUELTO
    /// (<see cref="ServiceResolutionRules"/>.IsResolved). Carga las 6 colecciones (chicas) y evalua
    /// en memoria porque la regla de resolucion (sobre todo el aereo: TicketIssuedAt, y los genericos:
    /// mapeo de texto) no es traducible a SQL de forma uniforme.
    /// </summary>
    private async Task<bool> HasResolvedServicesAsync(int id, CancellationToken ct)
    {
        var reserva = await _context.Reservas
            .AsNoTracking()
            .Include(r => r.FlightSegments)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .Include(r => r.Servicios)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (reserva == null) return false;

        return reserva.FlightSegments.Any(ServiceResolutionRules.IsResolved)
            || reserva.HotelBookings.Any(ServiceResolutionRules.IsResolved)
            || reserva.TransferBookings.Any(ServiceResolutionRules.IsResolved)
            || reserva.PackageBookings.Any(ServiceResolutionRules.IsResolved)
            || reserva.AssistanceBookings.Any(ServiceResolutionRules.IsResolved)
            || reserva.Servicios.Any(ServiceResolutionRules.IsResolved);
    }

    /// <summary>
    /// Gates para pasar a En viaje (Traveling): reserva no vacia + capacidad pax + CLIENTE SALDADO.
    /// El chequeo de "servicios sin confirmar" es opcional: en el ciclo clasico va junto aca
    /// (checkUnconfirmedServices=true), en el nuevo ya se hizo en Sold-&gt;Confirmed
    /// (checkUnconfirmedServices=false).
    ///
    /// <para>ADR-036 (2026-06-21, prepago puro): el gate de pago del CLIENTE es un candado DURO E
    /// INCONDICIONAL — el cliente debe quedar saldado (Balance &lt;= 0) para viajar, SIEMPRE, sin importar la
    /// llave <c>RequireFullPaymentForOperativeStatus</c> (que sigue gobernando otros read-models de tesoreria,
    /// pero NO este pase). Por eso este metodo ya NO usa <c>EconomicRulesHelper.GetOperativeBlockReason</c>
    /// (que respetaba la llave) sino el helper puro <c>ReservationEconomicPolicy.IsClientFullyPaid</c>. El
    /// OPERADOR no entra: por limitacion de datos su deuda es solo AVISO, no traba el viaje (ver ADR-036).</para>
    /// </summary>
    private async Task EnsureCanStartTravelingAsync(Reserva file, int id, OperationalFinanceSettings settings, bool checkUnconfirmedServices)
    {
        var fullReserva = await _context.Reservas
            .Include(r => r.Servicios)
            .Include(r => r.HotelBookings)
            .Include(r => r.FlightSegments)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (fullReserva == null) throw new KeyNotFoundException("Reserva no encontrada");

        var emptyReason = EconomicRulesHelper.GetEmptyReservaBlockReason(fullReserva);
        if (!string.IsNullOrWhiteSpace(emptyReason))
            throw new InvalidOperationException($"No se puede pasar a Operativo: {emptyReason}");

        // GATE "confirmada con cambios" (2026-06-24): una reserva confirmada que tiene cambios sin revisar
        // (un servicio dejo de estar resuelto, se quedo sin servicios, o se edito precio/costo) NO avanza al
        // viaje hasta que una persona de el OK (endpoint acknowledge-changes). Antes este candado lo cumplia
        // la regresion automatica (al volver a En gestion, el pase a Traveling no aplicaba); ahora la reserva
        // queda en Confirmed, asi que la marca es la que frena el avance. Cubre tanto este pase manual como el
        // automatico del job (que tiene el mismo gate). InvalidOperationException -> 409 en los controllers.
        if (fullReserva.HasUnacknowledgedChanges)
            throw new InvalidOperationException(
                "No se puede pasar a En viaje: la reserva tiene cambios sin revisar. " +
                "Revisa los cambios y da el OK antes de continuar.");

        // Inconsistencia de capacidad pasajeros vs servicios — bloqueo independiente del estado financiero.
        var capacityReason = await ReservaCapacityRules.GetBlockReasonAsync(_context, id, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(capacityReason))
            throw new InvalidOperationException($"No se puede pasar a Operativo: {capacityReason}");

        if (checkUnconfirmedServices)
        {
            // Servicios sin confirmar con el proveedor — no entran al balance, datos sucios.
            var unconfirmedReason = await ReservaCapacityRules.GetUnconfirmedServicesBlockReasonAsync(_context, id, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(unconfirmedReason))
                throw new InvalidOperationException($"No se puede pasar a Operativo: {unconfirmedReason}");
        }

        // ADR-036/ADR-040: candado de pago del cliente, BIFURCADO por modo de cobro. El mensaje nunca lleva
        // montos (sin datos sensibles). InvalidOperationException -> 409 en los controllers.
        var billingMode = await ClientCreditGate.ResolveModeAsync(_context, file.PayerId, settings, CancellationToken.None);
        if (billingMode == CustomerBillingMode.Account)
        {
            // Cuenta corriente: puede viajar DEBIENDO mientras su deuda total por moneda no supere su limite
            // (review B1: la exposicion incluye las reservas ya "En viaje"). Si PayerId fuera null, ResolveModeAsync
            // ya habria devuelto Prepaid; por eso aca PayerId es no-null.
            var decision = await ClientCreditGate.EvaluateCanTravelAsync(
                _context, file.PayerId!.Value, file.Balance, settings, CancellationToken.None);

            // El branch Account SIEMPRE avisa cuando hay violacion, aunque la agencia haya elegido "solo avisar"
            // (la llave deja pasar pero nunca sin ningun control). El aviso no lleva montos.
            if (decision.Warning != null)
                _logger.LogWarning(
                    "EnsureCanStartTraveling: Reserva {ReservaId} (cliente a cuenta) viaja con aviso de credito. {Warning}",
                    id, decision.Warning);

            if (!decision.Allowed)
                throw new InvalidOperationException(decision.BlockReason);
        }
        else
        {
            // Prepago (ADR-036): candado DURO e incondicional. CERO cambios respecto de hoy (byte-identico).
            if (!ReservationEconomicPolicy.IsClientFullyPaid(file.Balance))
                throw new InvalidOperationException(ReservationEconomicPolicy.ClientNotFullyPaidForTravelingMessage);
        }
    }

    /// <summary>
    /// Gate de cierre: estampa ClosedAt. ADR-036 (2026-06-21): corre en Traveling -&gt; Closed (ya no existe
    /// ToSettle -&gt; Closed).
    ///
    /// <para>ADR-040 (cuenta corriente, 2026-06-26, review B4): BIFURCADO por modo de cobro. Un cliente PREPAGO
    /// no cierra con saldo pendiente (candado de ADR-036). Un cliente a CUENTA CORRIENTE SI cierra debiendo: el
    /// viaje termino y la deuda sigue viva en su cuenta (FinancePositionService la cuenta como AR aunque la
    /// reserva quede Closed). Sin esta excepcion, las reservas a cuenta con saldo quedarian atascadas "En viaje"
    /// para siempre. Al cierre NO se re-chequea el limite: el viaje ya ocurrio, no tiene sentido trabar el cierre.</para>
    /// </summary>
    private static void EnsureCanCloseAndStampClosedAt(Reserva file, bool customerTravelsOnAccount)
    {
        // Saldo pendiente CON tolerancia de redondeo: un resto de centavo (tipico en cobro cruzado de
        // moneda) no debe trabar el cierre. Antes "Balance > 0" exacto frenaba el cierre por 1 centavo.
        if (!customerTravelsOnAccount && !EconomicRulesHelper.IsEconomicallySettled(file))
            throw new InvalidOperationException($"No se puede cerrar la reserva porque tiene un saldo pendiente de {file.Balance:N2}.");
        file.ClosedAt = DateTime.UtcNow;
    }

    public async Task<Reserva> ArchiveReservaAsync(int id)
    {
        var file = await _context.Reservas
            .Include(r => r.Payments)
            .Include(r => r.Servicios)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        var archiveBlock = EconomicRulesHelper.GetArchiveBlockReason(file);
        if (!string.IsNullOrWhiteSpace(archiveBlock))
            throw new InvalidOperationException(archiveBlock);

        // ADR-020 F6 (M7): rastro auditable ADITIVO del archivado (este path escribe Status por fuera
        // de UpdateStatusAsync/RevertStatusAsync). Solo se agrega el log; el flujo no se reestructura.
        var fromStatus = file.Status;
        file.Status = "Archived";
        _context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = file.Id,
            FromStatus = fromStatus,
            ToStatus = "Archived",
            Direction = "Forward",
            Reason = "Archivado (soft-delete)",
            OccurredAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
        return file;
    }

    public async Task DeleteReservaAsync(int id)
    {
        // Pre-flight guard antes de abrir transaccion: si esta bloqueado, evitamos
        // tocar la BD. Las consultas son AsNoTracking, asi que no interfieren con
        // el SaveChanges posterior.
        var blockReason = await DeleteGuards.GetReservaDeleteBlockReasonAsync(_context, id);
        if (blockReason != null)
        {
            // Information: rechazo benigno por estado/contenido. No hay riesgo fiscal.
            _logger.LogInformation(
                "DeleteReservaAsync rejected. ReservaId={ReservaId}. Reason={Reason}",
                id, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var file = await _context.Reservas
                    .Include(f => f.Servicios)
                    .Include(f => f.Passengers)
                    .Include(f => f.FlightSegments)
                    .Include(f => f.HotelBookings)
                    .Include(f => f.TransferBookings)
                    .Include(f => f.PackageBookings)
                    .Include(f => f.AssistanceBookings)
                    .FirstOrDefaultAsync(f => f.Id == id);

                if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

                if (file.Servicios.Any()) _context.Servicios.RemoveRange(file.Servicios);
                if (file.Passengers.Any()) _context.Passengers.RemoveRange(file.Passengers);
                if (file.FlightSegments.Any()) _context.FlightSegments.RemoveRange(file.FlightSegments);
                if (file.HotelBookings.Any()) _context.HotelBookings.RemoveRange(file.HotelBookings);
                if (file.TransferBookings.Any()) _context.TransferBookings.RemoveRange(file.TransferBookings);
                if (file.PackageBookings.Any()) _context.PackageBookings.RemoveRange(file.PackageBookings);
                // Bloque 3: borrar las asistencias junto con la reserva (cascade explicito, igual
                // que los otros 4 tipos). El DeleteBehavior.Cascade en BD es la red de seguridad.
                if (file.AssistanceBookings.Any()) _context.AssistanceBookings.RemoveRange(file.AssistanceBookings);

                _context.Reservas.Remove(file);

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    public Task UpdateBalanceAsync(int reservaId)
        => UpdateBalanceAsync(reservaId, markChangesIfMeaningfulOnLive: false);

    /// <summary>
    /// ADR-027 (auditoria ERP, hallazgo #10): estados VIVOS en los que una edicion de precio/costo de un
    /// servicio se interpreta como "el operador confirmo con cambios". Editar en Cotizacion/Presupuesto NO
    /// marca nada (todavia no hay nada confirmado con el cliente). Es un conjunto PROPIO, distinto del
    /// candado (<see cref="ReservaLockGuard"/>): incluye InManagement (donde no hay candado) y NO incluye
    /// Closed (una reserva cerrada no deberia recibir cambios; si los recibe, no abrimos un pendiente nuevo).
    /// </summary>
    // ADR-036 (2026-06-21): se quito ToSettle (estado eliminado). El "confirmada con cambios" vive en
    // {InManagement, Confirmed, Traveling}.
    private static readonly HashSet<string> ChangeTrackingLiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
    };

    /// <summary>
    /// Recalcula la plata + corre el motor de estados, y opcionalmente marca la reserva como
    /// "confirmada con cambios" (ADR-027).
    ///
    /// <para><paramref name="markChangesIfMeaningfulOnLive"/>: lo pasan en <c>true</c> SOLO los paths de
    /// EDICION de servicio (generico + 5 tipados) cuando detectaron que cambio el SalePrice o el NetCost.
    /// Los paths de alta/baja de servicio, el recalculo por pago y el de AFIP lo dejan en <c>false</c>: no
    /// son "el operador confirmo con otro precio". La decision de si realmente corresponde marcar (estado
    /// vivo + no re-pisar la fecha) vive abajo, en un solo lugar.</para>
    /// </summary>
    public Task UpdateBalanceAsync(int reservaId, bool markChangesIfMeaningfulOnLive)
        // Sobrecarga legacy sin detalle: si hay que marcar, pasamos un descriptor "vacio" (sin servicio ni
        // montos) para que el trigger levante la bandera igual que antes, pero NO registre una fila de detalle
        // (no tenemos de donde sacar el que/cuanto). Si no hay que marcar, null.
        => UpdateBalanceAsync(reservaId, markChangesIfMeaningfulOnLive ? PendingServiceChange.MarkOnly : null);

    public async Task UpdateBalanceAsync(int reservaId, PendingServiceChange? change)
    {
        await RecalculateMoneyAsync(reservaId);

        // ADR-020 F3 (contrato M2): el motor de estados corre como un SaveChanges SEPARADO
        // inmediatamente despues del recalculo de saldo (post-commit). Como TODOS los chokepoints de
        // mutacion de servicio (BookingService para los 5 tipos + Add/Update/Remove del generico) ya
        // llaman a UpdateBalanceAsync, enchufar el motor aca lo cubre todo sin tocar cada call-site.
        if (_autoStateService != null)
            await _autoStateService.EvaluateAndApplyAsync(reservaId);

        // ADR-027: si fue una EDICION de precio/costo y la reserva quedo (o sigue) en estado vivo, dejamos
        // la marca "confirmada con cambios" Y registramos el detalle (que servicio, que campo, antes/despues).
        // Va DESPUES del motor a proposito: el motor pudo regresar la reserva de Confirmed a InManagement
        // (sigue siendo estado vivo), o no tocarla; en ambos casos el estado leido aca es el definitivo.
        if (change != null && (change.HasMeaningfulChange || change.IsMarkOnly))
            await MarkUnacknowledgedChangesIfLiveAsync(reservaId, change);
    }

    /// <summary>
    /// ADR-027 (hallazgo #10): marca la reserva como "confirmada con cambios" si esta en un estado vivo
    /// (<see cref="ChangeTrackingLiveStatuses"/>). Idempotente: si ya estaba marcada, NO re-pisa
    /// <c>ChangesPendingSince</c> (esa fecha representa "desde cuando hay algo pendiente de revisar", y la
    /// primera vez es la que importa hasta que el dueño de el OK). Si la reserva no esta viva, no hace nada.
    ///
    /// <para>Corre como un SaveChanges propio, mismo patron que el motor de estados. No toca el saldo: el
    /// saldo ya se recalculo solo (ReservaMoneyPersister). Solo levanta la bandera de revision humana.</para>
    /// </summary>
    private async Task MarkUnacknowledgedChangesIfLiveAsync(int reservaId, PendingServiceChange change)
    {
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId);
        if (reserva == null) return;

        if (!ChangeTrackingLiveStatuses.Contains(reserva.Status)) return;

        var now = DateTime.UtcNow;

        // La bandera + la fecha "desde cuando hay pendiente" se ponen la PRIMERA vez (no se re-pisan): una
        // segunda edicion antes del OK no reinicia el reloj. El DETALLE en cambio SI se acumula: cada edicion
        // deja su propia(s) fila(s) de "que cambio", para que el dueño vea TODOS los cambios antes de dar el OK.
        if (!reserva.HasUnacknowledgedChanges)
        {
            reserva.HasUnacknowledgedChanges = true;
            reserva.ChangesPendingSince = now;
        }

        // Detalle del cambio (que servicio, que campo, antes/despues). El descriptor MarkOnly (sin servicio ni
        // montos) levanta la bandera pero no agrega filas — lo usa la sobrecarga legacy que no trae detalle.
        if (!change.IsMarkOnly)
        {
            await AddPendingChangeRowsAsync(reserva, change, now);
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "ADR-027: Reserva {ReservaId} marcada 'confirmada con cambios' (edicion de precio/costo en estado {Status}).",
            reservaId, reserva.Status);
    }

    /// <summary>
    /// ADR-027 (detalle, 2026-06-13): agrega una fila de <see cref="ReservaPendingChange"/> por cada campo que
    /// cambio (precio de venta y/o costo). Resuelve el usuario actual (snapshot del nombre) para auditoria.
    /// No llama a SaveChanges: lo hace el caller junto con la bandera, en una sola transaccion logica.
    /// </summary>
    private async Task AddPendingChangeRowsAsync(Reserva reserva, PendingServiceChange change, DateTime now)
    {
        // Actor: el usuario que hizo la edicion. Resoluble solo si hay HttpContext (en tests/jobs queda null,
        // lo cual es aceptable: el cambio se registra igual, sin nombre).
        string? actorUserId = GetCurrentUserIdOrNull();
        string? actorUserName = null;
        if (!string.IsNullOrEmpty(actorUserId))
        {
            var actor = await _userManager.FindByIdAsync(actorUserId);
            actorUserName = actor?.FullName;
        }

        string currency = Monedas.Normalizar(change.Currency);

        if (change.SalePriceChanged)
        {
            _context.ReservaPendingChanges.Add(new ReservaPendingChange
            {
                ReservaId = reserva.Id,
                ServiceType = change.ServiceType,
                ServiceDescription = change.ServiceDescription,
                ServicePublicId = change.ServicePublicId,
                Field = PendingChangeFields.SalePrice,
                OldValue = change.OldSalePrice!.Value,
                NewValue = change.NewSalePrice!.Value,
                Currency = currency,
                ChangedByUserId = actorUserId,
                ChangedByUserName = actorUserName,
                ChangedAt = now,
            });
        }

        if (change.NetCostChanged)
        {
            _context.ReservaPendingChanges.Add(new ReservaPendingChange
            {
                ReservaId = reserva.Id,
                ServiceType = change.ServiceType,
                ServiceDescription = change.ServiceDescription,
                ServicePublicId = change.ServicePublicId,
                Field = PendingChangeFields.NetCost,
                OldValue = change.OldNetCost!.Value,
                NewValue = change.NewNetCost!.Value,
                Currency = currency,
                ChangedByUserId = actorUserId,
                ChangedByUserName = actorUserName,
                ChangedAt = now,
            });
        }
    }

    /// <summary>
    /// Recalculo de saldo SOLO (sin motor de estados). Lo usa UpdateStatusAsync para refrescar el
    /// saldo antes de evaluar el gate de cierre, sin disparar transiciones automaticas en medio de
    /// una transicion manual.
    /// </summary>
    private async Task RecalculateMoneyAsync(int reservaId)
    {
        // ADR-021 §4.1/§B5: el recalculo + persistencia (escalar surrogate + tabla hija por moneda)
        // viven en el persister consolidado, unico punto de escritura de la plata de la reserva. Asi
        // este camino (recalculo por mutacion de servicio/estado) escribe la hija igual que el de
        // pagos y el de AFIP, y nunca pueden divergir. La matematica sigue en ReservaMoneyCalculator.
        await TravelApi.Infrastructure.Reservations.ReservaMoneyPersister.PersistAsync(_context, reservaId);
    }

    private static void ApplyEconomicFlags(ReservaDto dto, OperationalFinanceSettings settings)
    {
        var reserva = new Reserva { Balance = dto.Balance, Status = dto.Status };
        dto.IsEconomicallySettled = EconomicRulesHelper.IsEconomicallySettled(reserva);
        dto.CanMoveToOperativo = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetOperativeBlockReason(reserva, settings));
        dto.CanEmitVoucher = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetVoucherBlockReason(reserva, settings));
        var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);
        dto.CanEmitAfipInvoice = afip.CanEmit || afip.RequiresOverride;
        dto.EconomicBlockReason = EconomicRulesHelper.GetCombinedEconomicBlockReason(reserva, settings);
        dto.IsInProgress = ComputeIsInProgress(dto.Status, dto.StartDate, dto.EndDate);
        // Saldado = sin deuda CON tolerancia de redondeo (un sobrepago / saldo a favor o un centavo de
        // cobro cruzado NO es deuda). Mismo criterio canonico que IsEconomicallySettled, que ya se calculo
        // arriba. Antes era "Balance == 0m" exacto: una reserva pagada de mas (Balance < 0) o con un resto
        // de centavo mostraba "no pagada / con deuda" — el bug "pagada y figura que debe".
        //
        // H1b (2026-06-24): "Pagada" (IsFullyPaid) requiere ADEMAS que haya habido ACTIVIDAD EXIGIBLE. Una
        // reserva cotizada-no-confirmada (ConfirmedSale=0) sin cobros tiene Balance=0 -> IsEconomicallySettled
        // true, pero NO esta "pagada": no hay nada cobrado. Sin esto, las pantallas que usan IsFullyPaid
        // directo (p. ej. el chip "Pagada" de cobros) heredan el bug "pagada sin cobrar".
        // NO se toca IsEconomicallySettled (lo usa el gate de facturacion AFIP; cambiarlo es riesgoso).
        bool hadCollectibleActivity = dto.ConfirmedSale > 0m || dto.TotalPaid > 0m;
        dto.IsFullyPaid = dto.IsEconomicallySettled && hadCollectibleActivity;
        // "Vencida con deuda" es una regla de dominio ÚNICA (ReservationDebtRules): mira ADEMAS el ESTADO.
        // Una reserva ANULADA (estado no cobrable) NUNCA muestra deuda vencida por un saldo congelado.
        // NO se toca IsEconomicallySettled (gate AFIP), se recibe ya calculado.
        dto.HasOverdueDebt = ReservationDebtRules.HasOverdueDebt(
            dto.Status, dto.EndDate, dto.IsEconomicallySettled, DateTime.UtcNow);
    }

    private static void ApplyEconomicFlags(ReservaListDto dto, OperationalFinanceSettings settings)
    {
        var reserva = new Reserva { Balance = dto.Balance, Status = dto.Status };
        dto.IsEconomicallySettled = EconomicRulesHelper.IsEconomicallySettled(reserva);
        dto.CanMoveToOperativo = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetOperativeBlockReason(reserva, settings));
        dto.CanEmitVoucher = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetVoucherBlockReason(reserva, settings));
        var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);
        dto.CanEmitAfipInvoice = afip.CanEmit || afip.RequiresOverride;
        dto.EconomicBlockReason = EconomicRulesHelper.GetCombinedEconomicBlockReason(reserva, settings);
        dto.IsInProgress = ComputeIsInProgress(dto.Status, dto.StartDate, dto.EndDate);
        // Saldado = sin deuda CON tolerancia de redondeo (un sobrepago / saldo a favor o un centavo de
        // cobro cruzado NO es deuda). Mismo criterio canonico que IsEconomicallySettled, que ya se calculo
        // arriba. Antes era "Balance == 0m" exacto: una reserva pagada de mas (Balance < 0) o con un resto
        // de centavo mostraba "no pagada / con deuda" — el bug "pagada y figura que debe".
        //
        // H1b (2026-06-24): "Pagada" (IsFullyPaid) requiere ADEMAS actividad EXIGIBLE. El ReservaListDto no
        // trae ConfirmedSale escalar, pero el Balance escalar YA se calcula con la venta exigible
        // (ConfirmedSale - TotalPaid): una reserva cotizada-no-confirmada sin cobros tiene Balance=0 y
        // TotalPaid=0 -> sin actividad -> NO "pagada". Mismo arreglo del bug "pagada sin cobrar".
        // NO se toca IsEconomicallySettled (lo usa el gate de facturacion AFIP).
        bool hadCollectibleActivity = dto.Balance != 0m || dto.TotalPaid > 0m;
        dto.IsFullyPaid = dto.IsEconomicallySettled && hadCollectibleActivity;
        // "Vencida con deuda" es una regla de dominio ÚNICA (ReservationDebtRules): mira ADEMAS el ESTADO.
        // Una reserva ANULADA (estado no cobrable) NUNCA muestra deuda vencida por un saldo congelado.
        // NO se toca IsEconomicallySettled (gate AFIP), se recibe ya calculado.
        dto.HasOverdueDebt = ReservationDebtRules.HasOverdueDebt(
            dto.Status, dto.EndDate, dto.IsEconomicallySettled, DateTime.UtcNow);
    }

    /// <summary>
    /// Estados de reserva "anulada" donde tiene sentido calcular el contexto de plata real
    /// (<see cref="ReservationDebtRules.CancelledMoneyContext"/>). Fuera de estos estados el contexto es null:
    /// una reserva viva usa el chip de deuda normal, no el de plata de anulacion.
    /// </summary>
    /// <remarks>
    /// "Multas en la cuenta del cliente" (2026-07-15): visibilidad ampliada a <c>internal</c> (era
    /// <c>private</c>) para que <see cref="TravelApi.Infrastructure.Services.CustomerService"/> reuse el MISMO
    /// criterio de "reserva anulada" al armar el bloque de multas pendientes, en vez de copiar la comparacion
    /// de estados. Mismo patron ya usado con <see cref="AggregatePendingPenaltiesByCurrency"/>. Sin cambio de
    /// comportamiento: la firma y el cuerpo quedan identicos.
    /// </remarks>
    internal static bool IsCancelledLikeStatus(string? status)
        => status == EstadoReserva.Cancelled || status == EstadoReserva.PendingOperatorRefund;

    // El "respaldo fiscal de la multa" (viva / en revision / ninguno) se decide con los predicados compartidos de
    // CancellationPenaltyRules, reusados IDENTICOS aca (detalle + listado) y en el vigia de coherencia (W5), asi los
    // tres no divergen. Antes vivia un predicado local demasiado amplio que contaba como "multa viva" a estados donde
    // la ND habia FALLADO (Failed/ManualReview): eso dejaba el cartel "multa por cobrar" pegado en anuladas cuya
    // multa ya no tenia comprobante valido (bug "multa fantasma", 2026-07-05).

    /// <summary>
    /// Resultado del contexto de plata de una anulada: el token para el chip + (si es multa por cobrar) el
    /// desglose por moneda. <c>PenaltyAmount</c>/<c>PenaltyCurrency</c> (escalares, PRE-existentes) reflejan
    /// SOLO la PRIMERA moneda de <c>PenaltiesByCurrency</c> — quedan por compatibilidad con el front actual;
    /// con 1 sola multa viva (el caso de siempre) coinciden exactamente con la lista.
    /// </summary>
    private readonly record struct CancelledMoneyInfo(
        string? Context,
        decimal? PenaltyAmount,
        string? PenaltyCurrency,
        IReadOnlyList<CancelledPenaltyByCurrencyDto> PenaltiesByCurrency);

    /// <summary>
    /// Normaliza la moneda de la multa congelada (<c>PenaltyCurrencyAtEvent</c>, en espacio ARCA hibrido: "PES"/"DOL")
    /// al codigo ISO del negocio ("ARS"/"USD") para mostrarla. Si no se reconoce el codigo ARCA, cae al normalizador
    /// legacy (null/vacio -> ARS). Ver el comentario de BookingCancellation.PenaltyCurrencyAtEvent.
    /// </summary>
    /// <remarks>
    /// "Multas en la cuenta del cliente" (2026-07-15): visibilidad ampliada a <c>internal</c> (era
    /// <c>private</c>) para que <c>CustomerService</c> muestre la MISMA moneda que el resto del modulo en el
    /// bloque de multas pendientes, sin reimplementar el mapeo ARCA-&gt;ISO. Sin cambio de comportamiento.
    /// </remarks>
    internal static string NormalizePenaltyCurrencyForDisplay(string? penaltyCurrencyAtEvent)
        => TravelApi.Domain.Helpers.ArcaCurrencyMapper.ToIso(penaltyCurrencyAtEvent)
           ?? Monedas.Normalizar(penaltyCurrencyAtEvent);

    /// <summary>
    /// Deriva el contexto de plata de UNA reserva anulada (camino de detalle). Devuelve todo en null si la reserva
    /// NO esta anulada. Para las anuladas, consulta el respaldo fiscal de la multa (multa viva vs multa en revision)
    /// con los predicados compartidos y aplica la regla de dominio <see cref="ReservationDebtRules.DeriveForCancelled"/>.
    /// El monto de la multa solo se expone cuando el contexto es "multa por cobrar", y es lo PENDIENTE de cobro
    /// (neto de lo ya pagado), no el bruto congelado — se calcula ND-BASED, ver
    /// <see cref="AggregatePendingPenaltiesByCurrency"/>.
    ///
    /// <para><b>ADR-044 T5 Addendum, Revision 2, fix B2 (2026-07-11)</b>: ANTES esta consulta tomaba
    /// <c>FirstOrDefaultAsync</c> SIN <c>OrderBy</c> sobre el predicado de multa viva, con el comentario
    /// (ya corregido) "INV-081 garantiza una sola cancelacion activa por reserva". La Decision C de este
    /// addendum ROMPE ese supuesto (pueden convivir 2+ <see cref="BookingCancellation"/> no-abortados en la
    /// misma reserva — una anulacion parcial de un servicio + otra de un servicio distinto, cada una con su
    /// propia multa). Con dos BC con multa viva simultanea, <c>FirstOrDefault</c> devolvia una fila ARBITRARIA:
    /// monto/moneda del cartel podian quedar equivocados, o el total de multa subestimado. Ahora se SUMAN
    /// TODOS los <c>PenaltyAmountAtEvent</c> de las BC con multa viva, AGRUPADO por
    /// <c>PenaltyCurrencyAtEvent</c> (nunca se suman monedas distintas). El caso de 1 sola multa (el 100% de
    /// hoy) rinde exactamente igual que antes (lista de un elemento -> mismo numero).</para>
    /// </summary>
    private async Task<CancelledMoneyInfo> DeriveCancelledMoneyContextAsync(
        int reservaId, string? status, decimal balance, CancellationToken cancellationToken)
    {
        var empty = new CancelledMoneyInfo(null, null, null, Array.Empty<CancelledPenaltyByCurrencyDto>());
        if (!IsCancelledLikeStatus(status))
            return empty;

        var reservaCancellations = _context.BookingCancellations
            .AsNoTracking()
            .Where(bc => bc.ReservaId == reservaId);

        // Multa VIVA: TODAS las BC con respaldo fiscal firme (puede haber mas de una, Decision C), con su
        // monto/moneda congelados + la ND vinculada (si ya se emitio) para calcular el pendiente ND-BASED.
        var liveSnapshots = await reservaCancellations
            .Where(CancellationPenaltyRules.LiveDebitNotePredicate)
            .Select(bc => new { bc.PenaltyAmountAtEvent, bc.PenaltyCurrencyAtEvent, bc.DebitNoteInvoiceId })
            .ToListAsync(cancellationToken);

        ReservationDebtRules.DebitNoteBacking backing;
        if (liveSnapshots.Count > 0)
        {
            backing = ReservationDebtRules.DebitNoteBacking.Live;
        }
        else
        {
            // Sin multa viva: puede ser "en revision" (ND fallida/manual) o directamente sin ningun rastro.
            bool underReview = await reservaCancellations
                .Where(CancellationPenaltyRules.PenaltyUnderReviewPredicate)
                .AnyAsync(cancellationToken);
            backing = underReview
                ? ReservationDebtRules.DebitNoteBacking.UnderReview
                : ReservationDebtRules.DebitNoteBacking.None;
        }

        var context = ReservationDebtRules.DeriveForCancelled(balance, backing);
        var token = ReservationDebtRules.ToDtoString(context);

        // El monto de la multa solo acompaña al caso "multa por cobrar" (PenaltyReceivable). En cualquier otro
        // contexto se deja vacio para no pintar un numero que no corresponde.
        if (context != ReservationDebtRules.CancelledMoneyContext.PenaltyReceivable || liveSnapshots.Count == 0)
            return new CancelledMoneyInfo(token, null, null, Array.Empty<CancelledPenaltyByCurrencyDto>());

        // Consulta batcheada de las NDs en juego (ImporteTotal/moneda, NC vivas asociadas, pagos vivos
        // imputados). Con una reserva tiene, como mucho, un punado de BC vivas, asi que esto NUNCA es un N+1
        // real (a diferencia del listado, que si agrupa por pagina).
        var debitNoteIds = liveSnapshots
            .Where(s => s.DebitNoteInvoiceId.HasValue)
            .Select(s => s.DebitNoteInvoiceId!.Value)
            .Distinct()
            .ToList();
        var debitNoteTotals = await DebitNoteOutstandingLookup.LoadDebitNoteTotalsAsync(
            _context, debitNoteIds, cancellationToken);
        var creditedByDebitNote = await DebitNoteOutstandingLookup.LoadCreditedAmountsAsync(
            _context, debitNoteIds, cancellationToken);
        var collectedByDebitNote = await DebitNoteOutstandingLookup.LoadCollectedAmountsAsync(
            _context, debitNoteIds, cancellationToken);

        var penaltiesByCurrency = AggregatePendingPenaltiesByCurrency(
            liveSnapshots.Select(s => (s.PenaltyAmountAtEvent, s.PenaltyCurrencyAtEvent, s.DebitNoteInvoiceId)),
            debitNoteTotals, creditedByDebitNote, collectedByDebitNote);

        var primary = penaltiesByCurrency.Count > 0 ? penaltiesByCurrency[0] : null;
        return new CancelledMoneyInfo(token, primary?.Amount, primary?.Currency, penaltiesByCurrency);
    }

    /// <summary>
    /// TANDA C "la multa cobrada se ve cerrada" (2026-07-16): agrupa por moneda ISO las multas VIVAS de una
    /// reserva anulada, calculando el PENDIENTE de cobro de cada una con la formula ND-BASED unica del modulo
    /// (<see cref="TravelApi.Domain.Reservations.DebitNoteOutstandingRules.ComputeOutstanding"/>): importe total
    /// de SU Nota de Debito, menos las NC vivas asociadas, menos los pagos vivos imputados a ELLA — no el saldo
    /// de la reserva.
    ///
    /// <para><b>Por que dejo de netear contra el saldo de la reserva (fix "la multa cobrada se ve pendiente para
    /// siempre")</b>: desde el 2026-07-15 un cobro real de multa en una reserva anulada se registra con
    /// <c>Payment.AffectsReservaBalance=false</c> (para no mezclar la deuda fiscal de la ND, ya cobrada, con la
    /// deuda operativa de la reserva, que la anulacion ya saldo). Eso significa que el saldo de la reserva YA NO
    /// baja cuando el cliente paga la multa: la cuenta vieja (contra el saldo) quedaba pegada mostrando el bruto
    /// entero como pendiente aunque estuviera cobrada. La cuenta correcta es la MISMA que valida un cobro nuevo
    /// (<c>PaymentService.EnsureCancelledDebitNoteCollectableAsync</c>) y que arma la bandeja de multas del
    /// cliente (<c>CustomerService.BuildPendingPenaltiesAsync</c>): contra la ND, no contra la reserva.</para>
    ///
    /// <para>Cuando una BC todavia no tiene ND emitida (su <c>DebitNoteInvoiceId</c> no aparece en
    /// <paramref name="debitNoteTotals"/> — la ND se esta encolando/emitiendo, ADR-014) no existe todavia ningun
    /// comprobante contra el cual se le pudiera haber cobrado o acreditado nada: el pendiente es directamente el
    /// bruto congelado, igual que antes de esta tanda.</para>
    ///
    /// <para>Filas con monto bruto null se ignoran (sin monto congelado no hay nada que sumar). Es PURA (sin
    /// acceso a base): los tres diccionarios ya vienen cargados por el caller con
    /// <see cref="DebitNoteOutstandingLookup"/> en consultas batcheadas (nunca una por fila, ni una por pagina en
    /// el listado). Devuelve la lista ordenada por moneda para que el orden sea deterministico.</para>
    /// </summary>
    /// <remarks>
    /// "Multas en la cuenta del cliente" (2026-07-15): visibilidad ampliada a <c>internal</c> (era
    /// <c>private</c>) para que <c>CustomerService</c> la reuse al completar el contexto de plata de la solapa
    /// "Reservas" de la cuenta del cliente, en vez de duplicar la formula.
    /// </remarks>
    internal static List<CancelledPenaltyByCurrencyDto> AggregatePendingPenaltiesByCurrency(
        IEnumerable<(decimal? PenaltyAmountAtEvent, string? PenaltyCurrencyAtEvent, int? DebitNoteInvoiceId)> liveSnapshots,
        IReadOnlyDictionary<int, (decimal ImporteTotal, string? MonId)> debitNoteTotals,
        IReadOnlyDictionary<int, decimal> creditedByDebitNote,
        IReadOnlyDictionary<int, decimal> collectedByDebitNote)
    {
        var pendingByCurrency = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in liveSnapshots)
        {
            if (snapshot.PenaltyAmountAtEvent is null) continue;

            decimal outstanding;
            string currency;

            if (snapshot.DebitNoteInvoiceId is { } debitNoteId
                && debitNoteTotals.TryGetValue(debitNoteId, out var debitNote))
            {
                // ND ya emitida: el pendiente se calcula CONTRA SU COMPROBANTE (fuente de verdad fiscal).
                outstanding = TravelApi.Domain.Reservations.DebitNoteOutstandingRules.ComputeOutstanding(
                    debitNote.ImporteTotal,
                    creditedByDebitNote.GetValueOrDefault(debitNoteId),
                    collectedByDebitNote.GetValueOrDefault(debitNoteId));
                currency = NormalizePenaltyCurrencyForDisplay(debitNote.MonId);
            }
            else
            {
                // Sin ND emitida todavia (rama de emision diferida, ADR-014): no hay comprobante contra el cual
                // se le haya podido cobrar o acreditar nada, asi que el pendiente es el bruto congelado tal cual.
                outstanding = snapshot.PenaltyAmountAtEvent.Value;
                currency = NormalizePenaltyCurrencyForDisplay(snapshot.PenaltyCurrencyAtEvent);
            }

            // Topeado a 0 para MOSTRAR (nunca "deuda negativa" en el cartel); ver el XML-doc de
            // DebitNoteOutstandingRules sobre por que la regla de Dominio devuelve el crudo sin topear.
            var pendingForDisplay = outstanding < 0m ? 0m : outstanding;
            pendingByCurrency[currency] = pendingByCurrency.TryGetValue(currency, out var acc)
                ? acc + pendingForDisplay
                : pendingForDisplay;
        }

        return pendingByCurrency
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CancelledPenaltyByCurrencyDto { Currency = kv.Key, Amount = kv.Value })
            .ToList();
    }

    /// <summary>
    /// Llena el contexto de plata (+ monto de multa) de las filas ANULADAS del listado en queries batcheadas (sin
    /// N+1). Solo se ejecuta si la pagina trae filas anuladas; el resto queda con el contexto en null. Mismo criterio
    /// y misma regla de dominio que el detalle (<see cref="DeriveCancelledMoneyContextAsync"/>).
    ///
    /// <para><b>ADR-044 T5 Addendum, Revision 2, fix B2 (2026-07-11)</b>: mismo fix que el detalle — antes
    /// agrupaba por <c>PublicId</c> y tomaba <c>.First()</c> (comentario propio, ya corregido: "INV-081
    /// garantiza una sola cancelacion activa..."). La Decision C rompe ese supuesto; ahora se SUMAN todas las
    /// filas VIVAS de la reserva, agrupadas por moneda.</para>
    /// </summary>
    private async Task FillCancelledMoneyContextForListAsync(
        IReadOnlyList<ReservaListDto> items, CancellationToken cancellationToken)
    {
        // Solo las filas anuladas necesitan contexto de plata; el resto se deja en null (sin tocar la DB).
        var cancelledItems = items.Where(i => IsCancelledLikeStatus(i.Status)).ToList();
        if (cancelledItems.Count == 0) return;

        var publicIds = cancelledItems.Select(i => i.PublicId).ToList();

        // Query 1: reservas de la pagina con multa VIVA, con el monto/moneda congelados de CADA BC (puede haber
        // mas de una fila por reserva, Decision C) + la ND vinculada (si ya se emitio). Join explicito contra
        // Reservas (no nav implicita) para resolver el PublicId y correr igual en Postgres e InMemory.
        var liveRows = await (
            from bc in _context.BookingCancellations.AsNoTracking().Where(CancellationPenaltyRules.LiveDebitNotePredicate)
            join reservaPadre in _context.Reservas.AsNoTracking() on bc.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
            select new
            {
                reservaPadre.PublicId,
                bc.PenaltyAmountAtEvent,
                bc.PenaltyCurrencyAtEvent,
                bc.DebitNoteInvoiceId
            })
            .ToListAsync(cancellationToken);

        // Agrupa TODAS las filas vivas por reserva (ya no ".First()" de una sola): la suma por moneda se hace
        // mas abajo, por item, con AggregatePendingPenaltiesByCurrency.
        var liveRowsByPublicId = liveRows
            .GroupBy(r => r.PublicId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => (r.PenaltyAmountAtEvent, r.PenaltyCurrencyAtEvent, r.DebitNoteInvoiceId)).ToList());

        // TANDA C (2026-07-16): consulta batcheada de TODAS las NDs de la PAGINA entera (nunca una por fila, ni
        // una por item dentro del foreach) — el pendiente ND-BASED de cada multa se calcula contra SU
        // comprobante, ver el XML-doc de AggregatePendingPenaltiesByCurrency.
        var debitNoteIds = liveRows
            .Where(r => r.DebitNoteInvoiceId.HasValue)
            .Select(r => r.DebitNoteInvoiceId!.Value)
            .Distinct()
            .ToList();
        var debitNoteTotals = await DebitNoteOutstandingLookup.LoadDebitNoteTotalsAsync(
            _context, debitNoteIds, cancellationToken);
        var creditedByDebitNote = await DebitNoteOutstandingLookup.LoadCreditedAmountsAsync(
            _context, debitNoteIds, cancellationToken);
        var collectedByDebitNote = await DebitNoteOutstandingLookup.LoadCollectedAmountsAsync(
            _context, debitNoteIds, cancellationToken);

        // Query 2: reservas de la pagina con multa EN REVISION (ND fallida/manual). Solo necesitamos el set de ids.
        var underReviewIds = (await (
            from bc in _context.BookingCancellations.AsNoTracking().Where(CancellationPenaltyRules.PenaltyUnderReviewPredicate)
            join reservaPadre in _context.Reservas.AsNoTracking() on bc.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
            select reservaPadre.PublicId).Distinct().ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var item in cancelledItems)
        {
            // Live tiene prioridad sobre UnderReview (los predicados son mutuamente excluyentes, pero por las dudas).
            ReservationDebtRules.DebitNoteBacking backing;
            if (liveRowsByPublicId.ContainsKey(item.PublicId))
                backing = ReservationDebtRules.DebitNoteBacking.Live;
            else if (underReviewIds.Contains(item.PublicId))
                backing = ReservationDebtRules.DebitNoteBacking.UnderReview;
            else
                backing = ReservationDebtRules.DebitNoteBacking.None;

            var context = ReservationDebtRules.DeriveForCancelled(item.Balance, backing);
            item.CancelledMoneyContext = ReservationDebtRules.ToDtoString(context);

            // El monto de la multa solo acompaña al caso "multa por cobrar", y es lo PENDIENTE de cobro
            // ND-BASED (contra la propia Nota de Debito, no contra el saldo de la reserva — ver el XML-doc de
            // AggregatePendingPenaltiesByCurrency). Con 2+ BC vivas simultaneas, se SUMAN por moneda en vez de
            // tomar una fila arbitraria; los tres diccionarios ya vienen cargados UNA sola vez para toda la
            // pagina (sin N+1 dentro de este foreach).
            if (context == ReservationDebtRules.CancelledMoneyContext.PenaltyReceivable &&
                liveRowsByPublicId.TryGetValue(item.PublicId, out var snapshots))
            {
                var penaltiesByCurrency = AggregatePendingPenaltiesByCurrency(
                    snapshots, debitNoteTotals, creditedByDebitNote, collectedByDebitNote);
                item.CancelledPenaltiesByCurrency = penaltiesByCurrency;

                var primary = penaltiesByCurrency.Count > 0 ? penaltiesByCurrency[0] : null;
                item.CancelledPenaltyAmount = primary?.Amount;
                item.CancelledPenaltyCurrency = primary?.Currency;
            }
        }
    }

    private static bool ComputeIsInProgress(string status, DateTime? startDate, DateTime? endDate)
    {
        if (status != EstadoReserva.Traveling) return false;
        if (!startDate.HasValue) return false;
        // Sin fecha de fin no podemos saber si esta en curso. Antes retornabamos true
        // y dejaba reservas marcadas "• En curso" indefinidamente (bug observado en
        // reservas viejas con EndDate=null cuyas fechas ya habian pasado).
        if (!endDate.HasValue) return false;
        var today = DateTime.UtcNow.Date;
        if (startDate.Value.Date > today) return false;
        if (endDate.Value.Date < today) return false;
        return true;
    }

    private IQueryable<Reserva> ApplyReservaSearch(IQueryable<Reserva> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLower();
        return query.Where(r =>
            r.Name.ToLower().Contains(normalized) ||
            r.NumeroReserva.ToLower().Contains(normalized) ||
            (r.Payer != null && r.Payer.FullName.ToLower().Contains(normalized)));
    }

    // ADR-020: claves de tab del ciclo unico en kebab-case. La clave "reserved" historica se renombro
    // a "confirmed" (el frontend que mandaba tab=reserved se actualiza en F3).
    private static IQueryable<Reserva> ApplyReservaView(IQueryable<Reserva> query, string? view)
    {
        return (view ?? "active").Trim().ToLowerInvariant() switch
        {
            "quotation" => query.Where(r => r.Status == EstadoReserva.Quotation),
            "budget" => query.Where(r => r.Status == EstadoReserva.Budget),
            "in-management" => query.Where(r => r.Status == EstadoReserva.InManagement),
            "confirmed" => query.Where(r => r.Status == EstadoReserva.Confirmed),
            "traveling" => query.Where(r => r.Status == EstadoReserva.Traveling),
            // ADR-036 (2026-06-21): el tab "to-settle" murio junto con el estado. Una clave desconocida
            // (incluida "to-settle" si quedara cacheada en algun cliente viejo) cae al default "active".
            "closed" => query.Where(r =>
                r.Status == EstadoReserva.Closed ||
                r.Status == EstadoReserva.Cancelled),
            "lost" => query.Where(r => r.Status == EstadoReserva.Lost),
            "archived" => query.Where(r => r.Status == "Archived"),
            // "active" (default) = todo lo que esta en gestion activa (ni Cotizacion/Presupuesto/Perdido,
            // ni cerrada/cancelada/archivada): En gestion + Confirmada + En viaje.
            _ => query.Where(r =>
                r.Status == EstadoReserva.InManagement ||
                r.Status == EstadoReserva.Confirmed ||
                r.Status == EstadoReserva.Traveling)
        };
    }

    private static IQueryable<Reserva> ApplyReservaOrdering(IQueryable<Reserva> query, ReservaListQuery request)
    {
        var sortBy = (request.SortBy ?? "startDate").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "createdat" => desc
                ? query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                : query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
            "numeroreserva" => desc
                ? query.OrderByDescending(r => r.NumeroReserva).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.NumeroReserva).ThenByDescending(r => r.CreatedAt),
            "totalsale" => desc
                ? query.OrderByDescending(r => r.TotalSale).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.TotalSale).ThenByDescending(r => r.CreatedAt),
            "balance" => desc
                ? query.OrderByDescending(r => r.Balance).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.Balance).ThenByDescending(r => r.CreatedAt),
            "startdate" => desc
                ? query.OrderBy(r => r.StartDate == null).ThenByDescending(r => r.StartDate).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.StartDate == null).ThenBy(r => r.StartDate).ThenByDescending(r => r.CreatedAt),
            _ => query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
        };
    }

    private async Task<bool> HasServicesAsync(int reservaId)
    {
        return await _context.Servicios.AnyAsync(s => s.ReservaId == reservaId)
            || await _context.HotelBookings.AnyAsync(h => h.ReservaId == reservaId)
            || await _context.FlightSegments.AnyAsync(f => f.ReservaId == reservaId)
            || await _context.TransferBookings.AnyAsync(t => t.ReservaId == reservaId)
            || await _context.PackageBookings.AnyAsync(p => p.ReservaId == reservaId)
            || await _context.AssistanceBookings.AnyAsync(a => a.ReservaId == reservaId);
    }

    /// <summary>
    /// Defensa al pasar de Presupuesto a Reservado: cualquier servicio con Status
    /// distinto de "Solicitado" se normaliza. Esto cubre bypasses por API directa o
    /// data preexistente. En el flujo normal, los servicios creados en Presupuesto
    /// ya quedan en "Solicitado" gracias a ReservaCapacityRules.ShouldForceSolicitadoStatusAsync
    /// que aplica BookingService al crear/actualizar.
    /// </summary>
    private async Task NormalizeAllServicesToSolicitadoAsync(int reservaId)
    {
        var hotels = await _context.HotelBookings.Where(h => h.ReservaId == reservaId && h.Status != "Solicitado").ToListAsync();
        foreach (var h in hotels) h.Status = "Solicitado";

        var transfers = await _context.TransferBookings.Where(t => t.ReservaId == reservaId && t.Status != "Solicitado").ToListAsync();
        foreach (var t in transfers) t.Status = "Solicitado";

        var packages = await _context.PackageBookings.Where(p => p.ReservaId == reservaId && p.Status != "Solicitado").ToListAsync();
        foreach (var p in packages) p.Status = "Solicitado";

        var flights = await _context.FlightSegments.Where(f => f.ReservaId == reservaId && f.Status != "Solicitado").ToListAsync();
        foreach (var f in flights) f.Status = "Solicitado";

        var assistances = await _context.AssistanceBookings.Where(a => a.ReservaId == reservaId && a.Status != "Solicitado").ToListAsync();
        foreach (var a in assistances) a.Status = "Solicitado";

        var generics = await _context.Servicios.Where(s => s.ReservaId == reservaId && s.Status != "Solicitado").ToListAsync();
        foreach (var g in generics) g.Status = "Solicitado";

        // SaveChanges sucede al final de UpdateStatusAsync.
    }

    private async Task<string> GenerateNumeroReservaAsync(CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var sequence = await _context.BusinessSequences
            .FirstOrDefaultAsync(item => item.DocumentType == "Reserva" && item.Year == year, cancellationToken);

        if (sequence is null)
        {
            sequence = new BusinessSequence
            {
                DocumentType = "Reserva",
                Year = year,
                LastValue = 1000,
                UpdatedAt = DateTime.UtcNow
            };

            _context.BusinessSequences.Add(sequence);
        }
        else
        {
            sequence.LastValue += 1;
            if (sequence.LastValue < 1000)
            {
                sequence.LastValue = 1000;
            }

            sequence.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return $"F-{year}-{sequence.LastValue}";
    }
}


