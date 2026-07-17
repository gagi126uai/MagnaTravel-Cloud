using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

public class CustomerService : ICustomerService
{
    private readonly AppDbContext _dbContext;
    // ADR-023 T1: fuente UNICA del saldo a cobrar del cliente. Antes CustomerService calculaba el saldo con
    // su propio predicado de estados (incluia canceladas/cotizaciones) -> daba un numero distinto al del
    // dashboard/tesoreria. Ahora pide el saldo a este componente, que lo deriva de ReservaMoneyByCurrency
    // con la lista canonica de estados en firme. NO es opcional a proposito: no debe quedar un segundo camino
    // de saldo dentro de este service.
    private readonly IFinancePositionService _financePosition;

    // ADR-040: auditoria de la config de cuenta corriente (accion sensible). OPCIONAL a proposito: muchos tests
    // construyen CustomerService sin auditoria; con StageBusinessEvent la fila de audit entra en el MISMO
    // SaveChanges que el cambio (atomico). Si es null (test sin auditoria), el cambio se aplica igual sin audit.
    private readonly IAuditService? _auditService;
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public CustomerService(
        AppDbContext dbContext,
        IFinancePositionService financePosition,
        IAuditService? auditService = null,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dbContext = dbContext;
        _financePosition = financePosition;
        _auditService = auditService;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Null = puede ver todas las cobranzas. En caso contrario, las cifras se limitan a reservas a cargo del
    /// usuario actual. Sin HttpContext se conserva el comportamiento de los tests unitarios directos.
    /// </summary>
    private async Task<string?> GetOwnerScopeOrNullAsync(CancellationToken cancellationToken)
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        if (user is null || user.IsInRole("Admin")) return null;

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (_permissionResolver is null || string.IsNullOrWhiteSpace(userId))
            return string.IsNullOrWhiteSpace(userId) ? "__no_user__" : userId;

        var permissions = await _permissionResolver.GetPermissionsAsync(userId, cancellationToken);
        return permissions.Contains(Permissions.CobranzasViewAll) ? null : userId;
    }

    public async Task<PagedResponse<CustomerListItemDto>> GetCustomersAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        var customersQuery = ApplyCustomerSearch(_dbContext.Customers.AsNoTracking(), query.Search);

        if (!query.IncludeInactive)
        {
            customersQuery = customersQuery.Where(customer => customer.IsActive);
        }

        var sortBy = (query.SortBy ?? "fullName").Trim().ToLowerInvariant();
        var sortByBalance = sortBy == "currentbalance";

        // ADR-023 T1: el saldo del cliente ya no vive en la entidad ni se calcula por fila en SQL; sale del
        // componente canonico (ReservaMoneyByCurrency en firme). Por eso el ordenamiento por saldo no se puede
        // hacer en SQL: la proyeccion sale por un orden estable (FullName) y, si se pidio orden por saldo, se
        // reordena EN MEMORIA la pagina ya enriquecida. Limitacion conocida (R2 del ADR): con paginacion
        // server-side el orden por saldo es POR PAGINA, no global. Aceptable para el volumen actual.
        customersQuery = ApplyCustomerOrdering(customersQuery, query);

        // La proyeccion NO calcula CurrentBalance (se enriquece despues desde el componente canonico).
        var projectedQuery = customersQuery.Select(customer => new CustomerListItemDto
        {
            PublicId = customer.PublicId,
            FullName = customer.FullName,
            Email = customer.Email,
            Phone = customer.Phone,
            DocumentNumber = customer.DocumentNumber,
            Address = customer.Address,
            Notes = customer.Notes,
            TaxId = customer.TaxId,
            CreditLimit = customer.CreditLimit,
            IsActive = customer.IsActive,
            TaxConditionId = customer.TaxConditionId
        });

        var page = await projectedQuery.ToPagedResponseAsync(query, cancellationToken);

        var pagePublicIds = page.Items.Select(item => item.PublicId).ToList();
        var pageCustomerIds = await _dbContext.Customers
            .AsNoTracking()
            .Where(customer => pagePublicIds.Contains(customer.PublicId))
            .Select(customer => new { customer.Id, customer.PublicId })
            .ToListAsync(cancellationToken);
        var ids = pageCustomerIds.Select(customer => customer.Id).ToList();
        var publicIdById = pageCustomerIds.ToDictionary(customer => customer.Id, customer => customer.PublicId);
        var netByOpenItem = new Dictionary<(Guid CustomerPublicId, string Currency, int ReservaId), decimal>();

        var invoiceRows = await _dbContext.Invoices.AsNoTracking()
            .Where(invoice => invoice.Reserva != null && invoice.Reserva.PayerId.HasValue
                && ids.Contains(invoice.Reserva.PayerId.Value) && invoice.Resultado == "A"
                && (ownerScope == null || invoice.Reserva.ResponsibleUserId == ownerScope))
            .Select(invoice => new
            {
                CustomerId = invoice.Reserva!.PayerId!.Value,
                ReservaId = invoice.ReservaId!.Value,
                invoice.TipoComprobante,
                invoice.ImporteTotal,
                invoice.MonId
            })
            .ToListAsync(cancellationToken);
        foreach (var row in invoiceRows)
        {
            var category = InvoiceComprobanteHelpers.Categorize(row.TipoComprobante);
            if (category == InvoiceComprobanteCategory.Unknown) continue;
            var key = (publicIdById[row.CustomerId], Domain.Helpers.ArcaCurrencyMapper.ToIso(row.MonId) ?? Monedas.ARS, row.ReservaId);
            var signed = category == InvoiceComprobanteCategory.CreditNote ? -row.ImporteTotal : row.ImporteTotal;
            netByOpenItem[key] = netByOpenItem.GetValueOrDefault(key) + signed;
        }

        var paymentRows = await _dbContext.Payments.AsNoTracking()
            .Where(payment => payment.Reserva != null && payment.Reserva.PayerId.HasValue
                && ids.Contains(payment.Reserva.PayerId.Value) && payment.Status != "Cancelled" && !payment.IsDeleted
                && (ownerScope == null || payment.Reserva.ResponsibleUserId == ownerScope))
            .Select(payment => new
            {
                CustomerId = payment.Reserva!.PayerId!.Value,
                ReservaId = payment.ReservaId!.Value,
                payment.Currency,
                payment.ImputedCurrency,
                payment.Amount,
                payment.ImputedAmount
            })
            .ToListAsync(cancellationToken);
        foreach (var row in paymentRows)
        {
            var key = (publicIdById[row.CustomerId], Monedas.Normalizar(row.ImputedCurrency ?? row.Currency), row.ReservaId);
            netByOpenItem[key] = netByOpenItem.GetValueOrDefault(key) - (row.ImputedAmount ?? row.Amount);
        }

        foreach (var item in page.Items)
        {
            item.BalancesByCurrency = netByOpenItem
                .Where(entry => entry.Key.CustomerPublicId == item.PublicId && entry.Value > 0.01m)
                .GroupBy(entry => entry.Key.Currency)
                .Select(group => new CurrencyAmountDto
                {
                    Currency = group.Key,
                    Amount = EconomicRulesHelper.RoundCurrency(group.Sum(entry => entry.Value))
                })
                .OrderBy(entry => entry.Currency, StringComparer.Ordinal)
                .ToList();
            item.UnappliedCreditsByCurrency = netByOpenItem
                .Where(entry => entry.Key.CustomerPublicId == item.PublicId && entry.Value < -0.01m)
                .GroupBy(entry => entry.Key.Currency)
                .Select(group => new CurrencyAmountDto
                {
                    Currency = group.Key,
                    Amount = EconomicRulesHelper.RoundCurrency(-group.Sum(entry => entry.Value))
                })
                .OrderBy(entry => entry.Currency, StringComparer.Ordinal)
                .ToList();
            item.CurrentBalance = item.BalancesByCurrency.Count == 1 ? item.BalancesByCurrency[0].Amount : 0m;
        }

        if (!sortByBalance)
        {
            return page;
        }

        // Reordenar la pagina por saldo (en memoria) y reconstruir el PagedResponse: Items es init-only.
        var desc = string.Equals(query.SortDir, "desc", StringComparison.OrdinalIgnoreCase);
        var ordered = desc
            ? page.Items.OrderByDescending(item => item.CurrentBalance).ThenBy(item => item.FullName).ToList()
            : page.Items.OrderBy(item => item.CurrentBalance).ThenBy(item => item.FullName).ToList();

        return PagedResponse<CustomerListItemDto>.Create(ordered, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<CustomerListItemDto> GetCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Where(found => found.Id == id)
            .Select(found => new CustomerListItemDto
            {
                PublicId = found.PublicId,
                FullName = found.FullName,
                Email = found.Email,
                Phone = found.Phone,
                DocumentNumber = found.DocumentNumber,
                Address = found.Address,
                Notes = found.Notes,
                TaxId = found.TaxId,
                CreditLimit = found.CreditLimit,
                IsActive = found.IsActive,
                TaxConditionId = found.TaxConditionId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new KeyNotFoundException("Cliente no encontrado");
        }

        // ADR-023 T1: el saldo sale del componente canonico (reservas en firme), no de un subquery local
        // que incluia cotizaciones/canceladas.
        // Fuente unica de la UI: open items documentados (Factura/ND - NC - cobros - compensaciones).
        // Una venta confirmada sin comprobante aun no integra "Debe"; una ND emitida en una anulada si.
        var documentedStatement = await GetCustomerAccountStatementAsync(id, cancellationToken);
        var receivableByCurrency = documentedStatement.Currencies
            .Where(block => block.ClosingBalance > 0m)
            .Select(block => new CurrencyAmountDto
            {
                Currency = block.Currency,
                Amount = EconomicRulesHelper.RoundCurrency(block.ClosingBalance)
            })
            .OrderBy(item => item.Currency, StringComparer.Ordinal)
            .ToList();
        customer.BalancesByCurrency = receivableByCurrency;
        customer.UnappliedCreditsByCurrency = documentedStatement.Currencies
            .Where(block => block.UnappliedCredit > 0m)
            .Select(block => new CurrencyAmountDto
            {
                Currency = block.Currency,
                Amount = EconomicRulesHelper.RoundCurrency(block.UnappliedCredit)
            })
            .OrderBy(item => item.Currency, StringComparer.Ordinal)
            .ToList();
        customer.CurrentBalance = receivableByCurrency.Count == 1 ? receivableByCurrency[0].Amount : 0m;
        return customer;
    }

    public async Task<Customer> CreateCustomerAsync(Customer customer, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(customer.DocumentType) && !string.IsNullOrWhiteSpace(customer.DocumentNumber))
        {
            var docType = customer.DocumentType.Trim();
            var docNumber = customer.DocumentNumber.Trim();
            var duplicate = await _dbContext.Customers
                .AsNoTracking()
                .Where(c => c.DocumentType == docType && c.DocumentNumber == docNumber)
                .Select(c => new { c.PublicId, c.FullName })
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicate != null)
            {
                throw new InvalidOperationException($"Ya existe un cliente con {docType} {docNumber}: {duplicate.FullName}.");
            }
        }

        // Fix R2 (2026-07-17, causa raiz del bug "edito la condicion fiscal y no hace nada"): antes esta
        // funcion solo defaulteaba el TEXTO cuando venia vacio, sin mirar el CODIGO (TaxConditionId). Un
        // alta que llegaba con codigo (ej. 1 = Responsable Inscripto) pero sin texto (el formulario del
        // cliente nunca manda el texto) quedaba con Id=1 y texto="Consumidor Final" (desalineados desde el
        // dia 1). Ahora se resuelve con el catalogo UNICO (ver docstring de
        // CustomerTaxConditionCatalog.ResolveIncoming): si vino un codigo, el texto SIEMPRE sale de ahi.
        // "existingId: null, existingText: Consumidor Final" representa el estado de un cliente que
        // todavia no existe (el default de alta de siempre).
        var (resolvedTaxConditionId, resolvedTaxCondition) = CustomerTaxConditionCatalog.ResolveIncoming(
            customer.TaxConditionId, customer.TaxCondition, existingId: null, existingText: "Consumidor Final");
        customer.TaxConditionId = resolvedTaxConditionId;
        customer.TaxCondition = resolvedTaxCondition;

        customer.IsActive = true;
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return customer;
    }

    public async Task<IReadOnlyList<CustomerSimilarMatchDto>> SearchSimilarAsync(
        string? fullName,
        string? documentType,
        string? documentNumber,
        string? phone,
        int take,
        CancellationToken cancellationToken)
    {
        var docType = documentType?.Trim();
        var docNumber = documentNumber?.Trim();
        var phoneNorm = NormalizePhone(phone);
        var nameNorm = NormalizeName(fullName);

        var hasAnyCriteria = !string.IsNullOrEmpty(docNumber) || !string.IsNullOrEmpty(phoneNorm) || !string.IsNullOrEmpty(nameNorm);
        if (!hasAnyCriteria)
        {
            return Array.Empty<CustomerSimilarMatchDto>();
        }

        var candidates = await _dbContext.Customers
            .AsNoTracking()
            .Where(c =>
                (docNumber != null && c.DocumentNumber == docNumber && (docType == null || c.DocumentType == docType)) ||
                (phoneNorm != null && c.Phone != null && c.Phone.Replace(" ", "").Replace("+", "").Replace("-", "") == phoneNorm) ||
                (nameNorm != null && c.FullName.ToLower().Contains(nameNorm)))
            .Take(50)
            .Select(c => new
            {
                c.PublicId,
                c.FullName,
                c.DocumentType,
                c.DocumentNumber,
                c.Phone,
                c.Email,
                c.IsActive
            })
            .ToListAsync(cancellationToken);

        var matches = candidates
            .Select(c =>
            {
                int score = 0;
                if (docNumber != null && c.DocumentNumber == docNumber)
                {
                    score = (docType != null && c.DocumentType == docType) ? 100 : 90;
                }
                else if (phoneNorm != null && NormalizePhone(c.Phone) == phoneNorm)
                {
                    score = 80;
                }
                else if (nameNorm != null && NormalizeName(c.FullName) == nameNorm)
                {
                    score = 70;
                }
                else if (nameNorm != null && (c.FullName ?? "").ToLower().Contains(nameNorm))
                {
                    score = 60;
                }

                return new CustomerSimilarMatchDto
                {
                    PublicId = c.PublicId,
                    FullName = c.FullName,
                    DocumentType = c.DocumentType,
                    DocumentNumber = c.DocumentNumber,
                    Phone = c.Phone,
                    Email = c.Email,
                    IsActive = c.IsActive,
                    Score = score
                };
            })
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.FullName)
            .Take(take > 0 ? take : 5)
            .ToList();

        return matches;
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        return phone.Replace(" ", "").Replace("+", "").Replace("-", "").Replace("(", "").Replace(")", "");
    }

    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant();
    }

    public async Task<Customer> UpdateCustomerAsync(int id, Customer customer, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Customers.FindAsync(new object[] { id }, cancellationToken);
        if (existing == null) throw new KeyNotFoundException("Cliente no encontrado");

        // B1.15 Fase 0' (CODE-06), separado en dos ejes el 2026-07-17 por decision del dueño:
        // "el CUIT es una identidad; la condicion fiscal es un dato de HOY".
        //
        //  - taxIdChanged: el CUIT SI se sigue bloqueando si el cliente tiene factura con CAE
        //    viva (identidad no reescribible sin anular el comprobante primero).
        //  - taxConditionChanged: la condicion (TaxConditionId/TaxCondition, ej. Mono -> RI) se
        //    permite editar SIEMPRE, incluso con facturas vivas — cada comprobante ya emitido
        //    congelo su propia condicion al momento de facturar, la ficha de HOY no lo reescribe
        //    (ver docstring de MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync). Este eje
        //    NUNCA pasa por el guard, pero SI queda auditado (accion CustomerTaxConditionChanged).
        //
        // Otros campos (FullName, Email, Phone, Address, Notes, IsActive, CreditLimit,
        // DocumentType, DocumentNumber) siguen libres de editar sin guard ni auditoria especial —
        // son datos operativos del cliente, no del comprobante AFIP.
        //
        // Fix R2 (2026-07-17, causa raiz del bug "edito la condicion fiscal del cliente y no hace nada"):
        // el Fix R1 de esta misma tarde solucionaba el "pisado silencioso" (omitir el campo ya no borraba
        // la condicion real) pero dejaba un agujero: el form de ficha del cliente (CustomerFormModal.jsx)
        // SOLO manda taxConditionId — NUNCA el string taxCondition — asi que con el criterio viejo
        // ("omitido = se preserva") el CODIGO se actualizaba bien pero el TEXTO quedaba SIEMPRE con el
        // valor anterior (nunca se derivaba del codigo nuevo). Resultado: el vendedor cambiaba la condicion
        // en la ficha, el desplegable mostraba el cambio guardado al reabrir, pero
        // BookingCancellationService.ResolveServerSideTaxIdentity (que solo lee el TEXTO) seguia viendo la
        // condicion vieja y bloqueaba la devolucion con "completa la condicion fiscal".
        //
        // Ahora los dos campos se resuelven JUNTOS con el catalogo UNICO (ver docstring de
        // CustomerTaxConditionCatalog.ResolveIncoming): si vino un codigo, el texto SIEMPRE sale de ahi
        // (nunca puede quedar un codigo nuevo con un texto viejo). El criterio "omitido = se preserva"
        // para DocumentType/DocumentNumber de mas abajo NO cambia.
        var (incomingTaxConditionId, incomingTaxCondition) = CustomerTaxConditionCatalog.ResolveIncoming(
            customer.TaxConditionId, customer.TaxCondition, existing.TaxConditionId, existing.TaxCondition);

        var taxIdChanged = !string.Equals(existing.TaxId, customer.TaxId, StringComparison.Ordinal);
        var taxConditionChanged =
            existing.TaxConditionId != incomingTaxConditionId ||
            !string.Equals(existing.TaxCondition, incomingTaxCondition, StringComparison.Ordinal);

        if (taxIdChanged)
        {
            // N2(a): si el guard bloquea, se sale ACA — antes de tocar cualquier campo fiscal (ni el CUIT
            // ni la condicion). Con doble eje (CUIT + condicion) en el mismo PUT y factura viva, el bloqueo
            // es TOTAL: no se persiste ni se audita la condicion tampoco, aunque esa parte sola hubiera sido
            // valida por separado. Evita el mensaje contradictorio "no se puede" + "pero la condicion sí se
            // guardó".
            var blockReason = await MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync(_dbContext, id, cancellationToken);
            if (blockReason != null)
            {
                throw new InvalidOperationException(blockReason);
            }
        }

        // Snapshot ANTES de mutar: la auditoria necesita el valor viejo, no el que estamos por pisar.
        var oldTaxId = existing.TaxId;
        var oldTaxConditionId = existing.TaxConditionId;
        var oldTaxCondition = existing.TaxCondition;

        existing.FullName = customer.FullName;
        existing.Email = customer.Email;
        existing.Phone = customer.Phone;

        // ADR-023 T1 (fix clobber documento): un PUT que OMITE documentType/documentNumber llega con null aca.
        // No se confia en el front: si el entrante viene vacio se PRESERVA el valor guardado (mandar vacio =
        // "no tocar"). Asi se corta la corrupcion silenciosa del documento al editar otros campos del cliente.
        // Trade-off: hoy no se puede "borrar" el documento por PUT; si algun dia hace falta, va con una
        // intencion explicita (no omitiendo el campo).
        if (!string.IsNullOrWhiteSpace(customer.DocumentType))
        {
            existing.DocumentType = customer.DocumentType;
        }
        if (!string.IsNullOrWhiteSpace(customer.DocumentNumber))
        {
            existing.DocumentNumber = customer.DocumentNumber;
        }

        existing.Address = customer.Address;
        existing.Notes = customer.Notes;
        existing.IsActive = customer.IsActive;
        existing.TaxId = customer.TaxId;
        // ADR-023 T1.5: CreditLimit sale de la vista. Ya no llega por request (se quito de CustomerUpsertRequest
        // y de MapCustomer), asi que no se toca aca: el valor en DB queda como esta (columna historica).
        existing.TaxConditionId = incomingTaxConditionId;
        existing.TaxCondition = incomingTaxCondition;

        var (actorUserId, actorUserName) = ResolveCurrentActor();

        // N1 (2026-07-17): el CUIT es una IDENTIDAD; un cambio legitimo (ej. corregir un typo) tambien
        // merece rastro, aunque haya pasado el guard porque no habia factura viva. Se audita SIEMPRE que
        // realmente cambio (no en cada PUT: si el CUIT no se toco, no hay evento).
        if (taxIdChanged)
        {
            _auditService?.StageBusinessEvent(
                action: AuditActions.CustomerTaxIdChanged,
                entityName: "Customer",
                entityId: existing.Id.ToString(),
                details: $"TaxId: {FormatNullableText(oldTaxId)} -> {FormatNullableText(customer.TaxId)}.",
                userId: actorUserId,
                userName: actorUserName);
        }

        if (taxConditionChanged)
        {
            var details =
                $"TaxConditionId: {FormatNullableInt(oldTaxConditionId)} -> {FormatNullableInt(incomingTaxConditionId)}. " +
                $"TaxCondition: {FormatNullableText(oldTaxCondition)} -> {FormatNullableText(incomingTaxCondition)}.";

            _auditService?.StageBusinessEvent(
                action: AuditActions.CustomerTaxConditionChanged,
                entityName: "Customer",
                entityId: existing.Id.ToString(),
                details: details,
                userId: actorUserId,
                userName: actorUserName);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Resuelve el actor actual (userId, userName) para auditoria a partir del HttpContext. Si no hay
    /// HttpContext (tests unitarios o jobs sin request en curso), devuelve "System"/"Sistema" — el mismo
    /// criterio que <c>SupplierService.ResolveCurrentActor</c>, para no inventar un segundo mecanismo.
    /// </summary>
    private (string UserId, string UserName) ResolveCurrentActor()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        if (user is null) return ("System", "Sistema");

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "System";
        var userName = user.FindFirst("FullName")?.Value
                       ?? user.FindFirstValue(ClaimTypes.Name)
                       ?? "Sistema";
        return (userId, userName);
    }

    private static string FormatNullableInt(int? value) => value?.ToString() ?? "(vacio)";

    private static string FormatNullableText(string? value) => string.IsNullOrWhiteSpace(value) ? "(vacio)" : value;

    public async Task<Customer> UpdateCustomerCreditConfigAsync(
        int id,
        CustomerBillingMode? billingMode,
        int paymentTermsDays,
        IReadOnlyDictionary<string, decimal> creditLimitsByCurrency,
        string actorUserId,
        string? actorUserName,
        CancellationToken cancellationToken)
    {
        if (paymentTermsDays < 0)
            throw new InvalidOperationException("El plazo de pago no puede ser negativo.");

        // Validamos los limites ANTES de tocar nada: un limite negativo es un error de entrada, no algo que
        // debamos persistir a medias. Tambien normalizamos la moneda (ARS/USD) y, si vinieran dos claves que
        // normalizan a la misma moneda, nos quedamos con la mas alta (no es un caso esperado del front).
        //
        // N1 (defensa en profundidad): un limite en CERO se trata como AUSENCIA (sin credito en esa moneda =
        // prepago duro). NO se persiste una fila-cero ambigua: no entra al estado deseado, asi la fila existente
        // (si la habia) se BORRA mas abajo. La politica de credito ya trata fila-cero == ausencia, pero evitamos
        // dejar el dato ambiguo en la base. Solo se persisten limites estrictamente positivos.
        var desiredLimits = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (currency, limit) in creditLimitsByCurrency)
        {
            if (limit < 0)
                throw new InvalidOperationException("El límite de crédito no puede ser negativo.");

            if (limit == 0m)
                continue; // 0 = sin credito = ausencia: no se persiste fila (se borra la existente si la hay).

            var key = Monedas.Normalizar(currency);
            if (!desiredLimits.TryGetValue(key, out var existing) || limit > existing)
                desiredLimits[key] = limit;
        }

        var customer = await _dbContext.Customers
            .Include(c => c.CreditLimitsByCurrency)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer == null)
            throw new KeyNotFoundException("Cliente no encontrado");

        // Snapshot del estado VIEJO para el detalle de auditoria (viejo -> nuevo).
        var oldBillingMode = customer.BillingMode;
        var oldPaymentTermsDays = customer.PaymentTermsDays;
        var oldLimits = customer.CreditLimitsByCurrency
            .ToDictionary(limit => Monedas.Normalizar(limit.Currency), limit => limit.Limit, StringComparer.Ordinal);

        customer.BillingMode = billingMode;
        customer.PaymentTermsDays = paymentTermsDays;

        // Upsert de los limites por moneda contra el ESTADO DESEADO: actualizar/crear las monedas presentes,
        // borrar las que el cliente tenia y ya no estan (ausencia = esa moneda vuelve a ser prepago).
        var existingByCurrency = customer.CreditLimitsByCurrency
            .ToDictionary(limit => Monedas.Normalizar(limit.Currency), StringComparer.Ordinal);

        foreach (var (currency, limit) in desiredLimits)
        {
            if (existingByCurrency.TryGetValue(currency, out var row))
                row.Limit = limit;
            else
                customer.CreditLimitsByCurrency.Add(new CustomerCreditLimitByCurrency
                {
                    CustomerId = customer.Id,
                    Currency = currency,
                    Limit = limit
                });
        }

        foreach (var (currency, row) in existingByCurrency)
        {
            if (!desiredLimits.ContainsKey(currency))
                _dbContext.CustomerCreditLimitByCurrency.Remove(row);
        }

        // Auditoria SENSIBLE (atomica via StageBusinessEvent: entra en el mismo SaveChanges). El detalle lleva
        // viejo -> nuevo de los tres ejes. Los limites son dato de plata, pero el audit log es de por si
        // restringido (pantalla de auditoria con permiso), por eso aca SI se registran los montos.
        var details = BuildCreditConfigAuditDetails(
            oldBillingMode, billingMode,
            oldPaymentTermsDays, paymentTermsDays,
            oldLimits, desiredLimits);

        _auditService?.StageBusinessEvent(
            action: AuditActions.CustomerCreditConfigUpdated,
            entityName: "Customer",
            entityId: customer.Id.ToString(),
            details: details,
            userId: actorUserId,
            userName: actorUserName);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return customer;
    }

    /// <summary>
    /// Arma el detalle legible (viejo -&gt; nuevo) de un cambio de config de cuenta corriente para la auditoria.
    /// Texto plano y compacto (no JSON estricto) — suficiente para la pantalla de auditoria.
    /// </summary>
    private static string BuildCreditConfigAuditDetails(
        CustomerBillingMode? oldMode, CustomerBillingMode? newMode,
        int oldTerms, int newTerms,
        IReadOnlyDictionary<string, decimal> oldLimits,
        IReadOnlyDictionary<string, decimal> newLimits)
    {
        string FormatMode(CustomerBillingMode? mode) => mode?.ToString() ?? "Hereda(default)";
        string FormatLimits(IReadOnlyDictionary<string, decimal> limits) =>
            limits.Count == 0
                ? "(sin limites)"
                : string.Join(", ", limits.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .Select(kvp => $"{kvp.Key}:{kvp.Value:N2}"));

        return $"BillingMode: {FormatMode(oldMode)} -> {FormatMode(newMode)}. " +
               $"PaymentTermsDays: {oldTerms} -> {newTerms}. " +
               $"CreditLimits: [{FormatLimits(oldLimits)}] -> [{FormatLimits(newLimits)}].";
    }

    public async Task<CustomerDeletionResult> DeleteOrArchiveCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers.FindAsync(new object[] { id }, cancellationToken);
        if (customer == null) throw new KeyNotFoundException("Cliente no encontrado");

        var hasReservas = await _dbContext.Reservas.AnyAsync(r => r.PayerId == id, cancellationToken);
        var hasPayments = await _dbContext.Payments.AnyAsync(p => p.Reserva != null && p.Reserva.PayerId == id, cancellationToken);
        var hasInvoices = await _dbContext.Invoices.AnyAsync(i => i.Reserva != null && i.Reserva.PayerId == id, cancellationToken);

        if (hasReservas || hasPayments || hasInvoices)
        {
            if (!customer.IsActive)
            {
                return new CustomerDeletionResult(CustomerDeletionOutcome.Archived, "El cliente ya estaba archivado.");
            }

            customer.IsActive = false;
            await _dbContext.SaveChangesAsync(cancellationToken);
            return new CustomerDeletionResult(CustomerDeletionOutcome.Archived, "El cliente tiene reservas o pagos asociados, por lo que se archivo (IsActive=false) en lugar de borrarse.");
        }

        _dbContext.Customers.Remove(customer);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new CustomerDeletionResult(CustomerDeletionOutcome.HardDeleted, "Cliente eliminado.");
    }

    public async Task<Customer> ReactivateCustomerAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers.FindAsync(new object[] { id }, cancellationToken);
        if (customer == null) throw new KeyNotFoundException("Cliente no encontrado");

        customer.IsActive = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return customer;
    }

    public async Task<CustomerAccountOverviewDto> GetCustomerAccountOverviewAsync(int id, CancellationToken cancellationToken)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new CustomerAccountCustomerDto
            {
                PublicId = entity.PublicId,
                FullName = entity.FullName,
                Email = entity.Email,
                Phone = entity.Phone,
                TaxId = entity.TaxId,
                CreditLimit = entity.CreditLimit
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new KeyNotFoundException("Cliente no encontrado");
        }

        // La cuenta del cliente se apoya en open items documentados: comprobantes aprobados menos cobros e
        // imputaciones explicitas. Cada moneda conserva su propio saldo; nunca se compensan ARS y USD.
        var documentedStatement = await GetCustomerAccountStatementAsync(id, cancellationToken);
        var receivableByCurrency = documentedStatement.Currencies
            .Where(block => block.ClosingBalance > 0m)
            .Select(block => new CurrencyAmountDto
            {
                Currency = block.Currency,
                Amount = EconomicRulesHelper.RoundCurrency(block.ClosingBalance)
            })
            .OrderBy(item => item.Currency, StringComparer.Ordinal)
            .ToList();
        var unappliedCreditByCurrency = documentedStatement.Currencies
            .Where(block => block.UnappliedCredit > 0m)
            .Select(block => new CurrencyAmountDto
            {
                Currency = block.Currency,
                Amount = EconomicRulesHelper.RoundCurrency(block.UnappliedCredit)
            })
            .OrderBy(item => item.Currency, StringComparer.Ordinal)
            .ToList();
        // Escalar legacy: solo tiene significado si existe una unica moneda. Nunca sumar ARS + USD.
        customer.CurrentBalance = receivableByCurrency.Count == 1 ? receivableByCurrency[0].Amount : 0m;

        // ADR-023 T1 (fix bug A3): el resumen TotalSales/TotalPaid/TotalBalance se calcula SOLO sobre las
        // reservas en firme. Antes sumaba TODAS las reservas del cliente (incluidas canceladas y cotizaciones)
        // -> el "Saldo Actual" grande del front estaba inflado. Con el mismo conjunto en los tres, el trio
        // TotalSales - TotalPaid queda coherente con TotalBalance (INV-T1-2).
        // ADR-033 (2026-06-16, A3/B1): el "Saldo Actual" y el resumen del cliente usan ahora la lista de
        // DEUDA cobrable (ReceivableDebtStatuses, incluye Closed). Una reserva Finalizada con deuda es deuda
        // real del cliente y debe sumar a su saldo. El conteo de reservas (abajo) NO cambia.
        // El contador de reservas sigue contando TODAS las del cliente (es el badge "Reservas: N" de la
        // pestaña, no un numero de plata): solo los tres importes pasan a la lista en firme.
        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id);
        if (ownerScope is not null)
            reservasQuery = reservasQuery.Where(reserva => reserva.ResponsibleUserId == ownerScope);

        var statementLines = documentedStatement.Currencies.SelectMany(block => block.Lines).ToList();
        var documentedByCurrency = documentedStatement.Currencies
            .Select(block => new CurrencyAmountDto
            {
                Currency = block.Currency,
                Amount = EconomicRulesHelper.RoundCurrency(block.Lines.Sum(line => line.Charge))
            })
            .Where(item => item.Amount != 0m)
            .ToList();
        var collectedByCurrency = documentedStatement.Currencies
            .Select(block => new CurrencyAmountDto
            {
                Currency = block.Currency,
                Amount = EconomicRulesHelper.RoundCurrency(block.Lines
                    .Where(line => line.Kind == CustomerAccountStatementLineKinds.Payment)
                    .Sum(line => line.Credit))
            })
            .Where(item => item.Amount != 0m)
            .ToList();

        // Se calculan ANTES del summary porque la composicion del saldo (BalanceCompositionByCurrency, Tanda
        // D2) los necesita a los dos: asi no repetimos consultas, solo reordenamos lo que ya se pedia.
        var pendingPenalties = await BuildPendingPenaltiesAsync(id, ownerScope, cancellationToken);
        var creditBalanceByCurrency = await BuildCustomerCreditByCurrencyAsync(id, ownerScope, cancellationToken);

        var summary = new CustomerAccountSummaryDto
        {
            TotalSales = documentedByCurrency.Count == 1 ? documentedByCurrency[0].Amount : 0m,
            TotalPaid = collectedByCurrency.Count == 1 ? collectedByCurrency[0].Amount : 0m,
            TotalBalance = receivableByCurrency.Count == 1 ? receivableByCurrency[0].Amount : 0m,
            DocumentedByCurrency = documentedByCurrency,
            CollectedByCurrency = collectedByCurrency,
            ReservaCount = await reservasQuery.CountAsync(cancellationToken),
            // ADR-022 §4.9 (fix S1-bis): el contador NO debe incluir el Payment puente del saldo a favor,
            // o el badge "Pagos: N" no coincidiria con las filas visibles en GetCustomerAccountPaymentsAsync
            // (que ya lo excluye). Mismo predicado del puente.
            PaymentCount = await _dbContext.Payments
                .AsNoTracking()
                // FC4 (2026-06-14): el contador excluye AMBOS puentes (sobrepago + saldo a favor aplicado),
                // para que el badge "Pagos: N" coincida con las filas visibles en GetCustomerAccountPaymentsAsync.
                .CountAsync(payment => payment.Reserva != null && payment.Reserva.PayerId == id
                    && (ownerScope == null || payment.Reserva.ResponsibleUserId == ownerScope)
                    && payment.AffectsCash
                    && payment.Status != "Cancelled"
                    && !payment.IsDeleted, cancellationToken),
            InvoiceCount = await _dbContext.Invoices
                .AsNoTracking()
                .CountAsync(invoice => invoice.Reserva != null && invoice.Reserva.PayerId == id
                    && (ownerScope == null || invoice.Reserva.ResponsibleUserId == ownerScope)
                    && invoice.Resultado == "A", cancellationToken),
            // ADR-022 Capa 8 (C2) / ADR-023 T1: la cuenta corriente por moneda sale del componente canonico
            // (mismo predicado en firme que el AR de tesoreria). El bolsillo de saldo a favor sigue local
            // (es de ClientCreditEntry, no de cuentas por cobrar).
            ReceivableByCurrency = receivableByCurrency,
            CreditBalanceByCurrency = creditBalanceByCurrency,
            UnappliedCreditByCurrency = unappliedCreditByCurrency,
            // Tanda D2 (extracto profesional, 2026-07-16): composicion del saldo por moneda para la foto de
            // saldo del encabezado. Se arma con datos que YA calculamos arriba (el extracto + las multas
            // pendientes de abajo), asi que no dispara ninguna consulta nueva.
            BalanceCompositionByCurrency = BuildBalanceComposition(
                documentedStatement, pendingPenalties.TotalsByCurrency, creditBalanceByCurrency)
        };

        return new CustomerAccountOverviewDto
        {
            Customer = customer,
            Summary = summary,
            // "Multas en la cuenta del cliente" (2026-07-15): bloque APARTE del summary (no toca
            // ReceivableByCurrency ni el resto de los totales existentes).
            PendingPenalties = pendingPenalties
        };
    }

    /// <summary>
    /// Tanda D2 (extracto profesional, 2026-07-16): arma la composicion del saldo POR MONEDA para la foto de
    /// saldo del encabezado (ver <see cref="CustomerAccountBalanceCompositionDto"/>). Funcion PURA en memoria:
    /// recibe datos ya calculados (el extracto y los totales de multas pendientes) y solo los recombina, sin
    /// disparar ninguna consulta nueva.
    ///
    /// <para><b>Por que hace falta</b>: hoy el extracto mezcla venta documentada y multa firme en un unico
    /// <c>ClosingBalance</c> por moneda (desde que el extracto paso a ser invoice-driven, commit 44fcea6). Esta
    /// funcion separa esa mezcla en las piezas que la pantalla nueva necesita pintar sin volver a sumar nada.</para>
    /// </summary>
    private static List<CustomerAccountBalanceCompositionDto> BuildBalanceComposition(
        CustomerAccountStatementDto statement,
        List<CustomerPendingPenaltyTotalDto> penaltyTotalsByCurrency,
        List<CurrencyAmountDto> creditBalanceByCurrency)
    {
        // Union de monedas: una moneda entra en la composicion si tiene AL MENOS un componente (extracto,
        // multa o credito). Asi un cliente con credito en USD pero sin ninguna reserva en USD todavia
        // igual recibe su fila (el front decide si la muestra).
        var currencies = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var block in statement.Currencies) currencies.Add(block.Currency);
        foreach (var penalty in penaltyTotalsByCurrency) currencies.Add(penalty.Currency);
        foreach (var credit in creditBalanceByCurrency) currencies.Add(credit.Currency);

        var result = new List<CustomerAccountBalanceCompositionDto>();
        foreach (var currency in currencies)
        {
            // ClosingBalance de ESTA moneda: ya incluye la multa FIRME (tiene comprobante -> es un Charge mas
            // del extracto), pero NUNCA la multa en tramite (todavia no tiene comprobante, no es un open item
            // documentado). Ver el comentario de CustomerAccountBalanceCompositionDto para la identidad completa.
            var closingBalance = statement.Currencies
                .FirstOrDefault(block => block.Currency == currency)?.ClosingBalance ?? 0m;

            var penaltyTotal = penaltyTotalsByCurrency.FirstOrDefault(p => p.Currency == currency);
            var multasFirmes = penaltyTotal?.FirmAmount ?? 0m;
            var multasEnTramite = penaltyTotal?.NotYetIssuedAmount ?? 0m;
            var multasAbiertasBrutas = multasFirmes + multasEnTramite;

            // Correccion M1 (review Tanda D2): "multasFirmes" sale de DebitNoteOutstandingRules (ND total menos
            // lo credited/collected atado a ESA ND puntual); "closingBalance" sale del open item POR RESERVA del
            // extracto (Charge-Credit de TODAS las lineas de la reserva, no solo las atadas a la ND). En el caso
            // normal coinciden, pero si la reserva anulada tiene ADEMAS un credito grande sin imputar
            // especificamente a la ND, esa reserva puede cerrar en saldo NEGATIVO (aporta 0 al ClosingBalance,
            // el negativo se va a UnappliedCredit) mientras la ND sigue mostrando outstanding > 0. Restar
            // multasFirmes de closingBalance daria "Facturado sin cobrar" NEGATIVO, algo que la pantalla nunca
            // debe mostrar. Frenamos en 0 y el residuo que "no entro" se lo restamos a MultasAbiertas (tambien
            // con piso 0): el split entre las dos lineas queda indicativo, pero el SALDO final (unas lineas mas
            // abajo) es la fuente de verdad y NO se mueve un centavo por este ajuste (identidad verificada por
            // test: closingBalance + multasEnTramite - creditoAFavor se preserva siempre).
            var multaFirmeSobreCierre = Math.Max(0m, multasFirmes - closingBalance);
            var facturadoSinCobrar = EconomicRulesHelper.RoundCurrency(Math.Max(0m, closingBalance - multasFirmes));
            var multasAbiertas = EconomicRulesHelper.RoundCurrency(
                Math.Max(0m, multasAbiertasBrutas - multaFirmeSobreCierre));

            var creditoAFavor = creditBalanceByCurrency.FirstOrDefault(c => c.Currency == currency)?.Amount ?? 0m;
            var saldo = EconomicRulesHelper.RoundCurrency(facturadoSinCobrar + multasAbiertas - creditoAFavor);

            result.Add(new CustomerAccountBalanceCompositionDto
            {
                Currency = currency,
                FacturadoSinCobrar = facturadoSinCobrar,
                MultasAbiertas = multasAbiertas,
                MultasEnTramite = EconomicRulesHelper.RoundCurrency(multasEnTramite),
                CreditoAFavor = EconomicRulesHelper.RoundCurrency(creditoAFavor),
                Saldo = saldo,
            });
        }

        return result;
    }

    /// <summary>
    /// Bloque "Multa pendiente de cobro" de la cuenta del cliente (UX 2026-07-15): junta las multas de
    /// anulación de TODAS las reservas ANULADAS del cliente, una fila por cada <c>BookingCancellation</c> con
    /// respaldo fiscal VIVO o EN REVISIÓN. Reusa los MISMOS predicados compartidos que el resto del módulo
    /// (<c>CancellationPenaltyRules.LiveDebitNotePredicate</c> / <c>PenaltyUnderReviewPredicate</c>) — el
    /// criterio de "multa por cobrar" no se vuelve a escribir acá.
    ///
    /// <para><b>Por qué esta plata estaba invisible</b>: el resto de la cuenta del cliente (resumen, extracto,
    /// solapa Reservas) filtra a reservas VIVAS o las trae sin cruzar; la multa SOLO existe en una reserva
    /// ANULADA, así que nunca aparecía agrupada a nivel cliente. Este es el primer lugar que la junta.</para>
    ///
    /// <para><b>El monto es el BRUTO congelado</b> (<c>PenaltyAmountAtEvent</c>), no el neto de lo ya cobrado:
    /// es la cifra con la que se emitió (o se va a emitir) el comprobante de la multa — a propósito distinta
    /// del cartel de la ficha, que muestra lo neto pendiente contra el saldo (otro cartel, otro propósito).</para>
    ///
    /// <para>Dos queries batcheadas (vivas / en revisión), nunca una por reserva: no importa cuántas reservas
    /// anuladas tenga el cliente.</para>
    /// </summary>
    private async Task<CustomerPendingPenaltiesDto> BuildPendingPenaltiesAsync(
        int customerId, string? ownerScope, CancellationToken cancellationToken)
    {
        var empty = new CustomerPendingPenaltiesDto();

        // Universo: reservas ANULADAS del cliente. OJO: acá se INLINEA la MISMA comparación que
        // ReservaService.IsCancelledLikeStatus porque EF no puede traducir el helper dentro de la query.
        // Si algún día se agrega un tercer estado "anulado", hay que actualizar LOS DOS lugares
        // (este Where y el helper) — mantenerlos en sync a mano (nota del review 2026-07-15, N1).
        var cancelledReservas = await _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == customerId
                && (reserva.Status == EstadoReserva.Cancelled || reserva.Status == EstadoReserva.PendingOperatorRefund)
                && (ownerScope == null || reserva.ResponsibleUserId == ownerScope))
            .Select(reserva => new { reserva.Id, reserva.PublicId, reserva.NumeroReserva, reserva.Name })
            .ToListAsync(cancellationToken);

        if (cancelledReservas.Count == 0) return empty;

        var reservaIds = cancelledReservas.Select(r => r.Id).ToList();
        var reservaById = cancelledReservas.ToDictionary(r => r.Id);

        // Multas VIVAS: respaldo fiscal firme. El propio predicado mezcla dos ramas (ver
        // CancellationPenaltyRules.LiveDebitNotePredicate); acá traemos el DebitNoteStatus para distinguirlas:
        // Issued = ya tiene comprobante (chip "pendingCollection"); NotApplicable/Pending = se está emitiendo
        // todavía (chip "issuing").
        var liveRows = await _dbContext.BookingCancellations
            .AsNoTracking()
            .Where(CancellationPenaltyRules.LiveDebitNotePredicate)
            .Where(bc => reservaIds.Contains(bc.ReservaId))
            .Select(bc => new
            {
                bc.ReservaId,
                bc.PenaltyAmountAtEvent,
                bc.PenaltyCurrencyAtEvent,
                bc.DebitNoteStatus,
                bc.DebitNoteInvoiceId
            })
            .ToListAsync(cancellationToken);

        var debitNoteIds = liveRows
            .Where(row => row.DebitNoteStatus == DebitNoteStatus.Issued && row.DebitNoteInvoiceId.HasValue)
            .Select(row => row.DebitNoteInvoiceId!.Value)
            .Distinct()
            .ToList();

        var debitNotes = await _dbContext.Invoices
            .AsNoTracking()
            .Where(invoice => debitNoteIds.Contains(invoice.Id))
            .Select(invoice => new
            {
                invoice.Id,
                invoice.PublicId,
                invoice.TipoComprobante,
                invoice.ImporteTotal,
                invoice.MonId,
                invoice.PuntoDeVenta,
                invoice.NumeroComprobante,
                invoice.Resultado
            })
            .ToDictionaryAsync(invoice => invoice.Id, cancellationToken);

        // TANDA C "la multa cobrada se ve cerrada" (2026-07-16): estas dos consultas (NC vivas asociadas + pagos
        // vivos imputados) se centralizaron en DebitNoteOutstandingLookup para que esta bandeja, el guard de
        // cobro (PaymentService) y el cartel de la ficha/listado (ReservaService) nunca diverjan.
        var creditedByDebitNote = await DebitNoteOutstandingLookup.LoadCreditedAmountsAsync(
            _dbContext, debitNoteIds, cancellationToken);
        var collectedByDebitNote = await DebitNoteOutstandingLookup.LoadCollectedAmountsAsync(
            _dbContext, debitNoteIds, cancellationToken);

        // Multas EN REVISION: el comprobante falló su emisión o quedó para revisión manual (back-office). Ya
        // cuenta como deuda del cliente (decisión del dueño, 2026-07-15) pero distinguida con su propio chip.
        var underReviewRows = await _dbContext.BookingCancellations
            .AsNoTracking()
            .Where(CancellationPenaltyRules.PenaltyUnderReviewPredicate)
            .Where(bc => reservaIds.Contains(bc.ReservaId))
            .Select(bc => new { bc.ReservaId, bc.PenaltyAmountAtEvent, bc.PenaltyCurrencyAtEvent })
            .ToListAsync(cancellationToken);

        var items = new List<CustomerPendingPenaltyItemDto>();

        foreach (var row in liveRows)
        {
            var reserva = reservaById[row.ReservaId];
            if (row.DebitNoteStatus == DebitNoteStatus.Issued
                && row.DebitNoteInvoiceId.HasValue
                && debitNotes.TryGetValue(row.DebitNoteInvoiceId.Value, out var debitNote)
                && debitNote.Resultado == "A"
                && InvoiceComprobanteHelpers.IsDebitNote(debitNote.TipoComprobante))
            {
                var credited = creditedByDebitNote.GetValueOrDefault(debitNote.Id);
                var collected = collectedByDebitNote.GetValueOrDefault(debitNote.Id);
                var outstanding = TravelApi.Domain.Reservations.DebitNoteOutstandingRules.ComputeOutstanding(
                    debitNote.ImporteTotal, credited, collected);
                if (outstanding <= 0m) continue;

                // Correccion N3 (review Tanda D2): antes esta llamada pre-convertia con ArcaCurrencyMapper.ToIso
                // Y BuildPendingPenaltyItem volvia a normalizar adentro (NormalizePenaltyCurrencyForDisplay ya
                // hace ToIso ?? Monedas.Normalizar) -> doble conversion fragil. Se manda el MonId CRUDO, igual
                // que las otras dos llamadas de este metodo (mas abajo), para que la normalizacion pase UNA sola
                // vez y el bucket de moneda de la multa firme nunca pueda divergir del bucket del extracto.
                items.Add(BuildPendingPenaltyItem(
                    reserva.PublicId,
                    reserva.NumeroReserva,
                    reserva.Name,
                    outstanding,
                    debitNote.MonId,
                    CustomerPendingPenaltyStatus.PendingCollection,
                    debitNote.PublicId,
                    $"{debitNote.PuntoDeVenta:D5}-{debitNote.NumeroComprobante:D8}"));
                continue;
            }

            // Una multa sin ND aprobada todavia no es un open item cobrable. Se muestra como pendiente
            // operativo, pero no se suma a la deuda firme ni ofrece registrar un cobro.
            if (row.PenaltyAmountAtEvent is null) continue;
            var status = row.DebitNoteStatus == DebitNoteStatus.Issued
                ? CustomerPendingPenaltyStatus.UnderReview
                : CustomerPendingPenaltyStatus.Issuing;
            items.Add(BuildPendingPenaltyItem(reserva.PublicId, reserva.NumeroReserva, reserva.Name,
                row.PenaltyAmountAtEvent.Value, row.PenaltyCurrencyAtEvent, status));
        }

        foreach (var row in underReviewRows)
        {
            if (row.PenaltyAmountAtEvent is null) continue;

            var reserva = reservaById[row.ReservaId];
            items.Add(BuildPendingPenaltyItem(reserva.PublicId, reserva.NumeroReserva, reserva.Name,
                row.PenaltyAmountAtEvent.Value, row.PenaltyCurrencyAtEvent, CustomerPendingPenaltyStatus.UnderReview));
        }

        // Orden estable por numero de reserva, igual criterio que el resto de los desgloses del modulo.
        items = items.OrderBy(item => item.NumeroReserva, StringComparer.Ordinal).ToList();

        return new CustomerPendingPenaltiesDto
        {
            Items = items,
            TotalsByCurrency = BuildPendingPenaltyTotalsByCurrency(items)
        };
    }

    /// <summary>Arma UNA fila del bloque de multas pendientes, normalizando moneda y redondeando el monto.</summary>
    private static CustomerPendingPenaltyItemDto BuildPendingPenaltyItem(
        Guid reservaPublicId, string numeroReserva, string name,
        decimal grossPenaltyAmount, string? penaltyCurrencyAtEvent, string status,
        Guid? debitNotePublicId = null, string? documentRef = null)
        => new()
        {
            ReservaPublicId = reservaPublicId,
            NumeroReserva = numeroReserva,
            Name = name,
            Amount = EconomicRulesHelper.RoundCurrency(grossPenaltyAmount),
            Currency = ReservaService.NormalizePenaltyCurrencyForDisplay(penaltyCurrencyAtEvent),
            Status = status,
            DebitNotePublicId = debitNotePublicId,
            DocumentRef = documentRef
        };

    /// <summary>
    /// Agrupa las multas por moneda en "deuda firme" (comprobante ya emitido) vs "todavía sin comprobante"
    /// (emitiéndose o en revisión), para que el front pinte el número grande + la segunda línea en ámbar sin
    /// tener que re-sumar los Items (ver <see cref="CustomerPendingPenaltyTotalDto"/>).
    /// </summary>
    private static List<CustomerPendingPenaltyTotalDto> BuildPendingPenaltyTotalsByCurrency(
        IReadOnlyList<CustomerPendingPenaltyItemDto> items)
    {
        var totalsByCurrency = new Dictionary<string, CustomerPendingPenaltyTotalDto>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!totalsByCurrency.TryGetValue(item.Currency, out var total))
            {
                total = new CustomerPendingPenaltyTotalDto { Currency = item.Currency };
                totalsByCurrency[item.Currency] = total;
            }

            if (item.Status == CustomerPendingPenaltyStatus.PendingCollection)
                total.FirmAmount += item.Amount;
            else
                total.NotYetIssuedAmount += item.Amount;
        }

        return totalsByCurrency.Values
            .OrderBy(total => total.Currency, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// ADR-023 T1: mapea el resultado neutral del componente canonico (FinanceCurrencyAmount) al DTO de la
    /// capa de aplicacion (CurrencyAmountDto). El componente ya normaliza moneda, redondea y ordena, asi que
    /// aca solo se cambia el tipo.
    /// </summary>
    private static List<CurrencyAmountDto> MapToCurrencyAmounts(IEnumerable<FinanceCurrencyAmount> amounts)
        => amounts
            .Select(x => new CurrencyAmountDto { Currency = x.Currency, Amount = x.Amount })
            .ToList();

    /// <summary>
    /// ADR-022 Capa 8 / decision #3: el "bolsillo" unificado de saldo A FAVOR del cliente POR MONEDA. Suma los
    /// ClientCreditEntry activos (RemainingBalance &gt; 0) del cliente, CUALQUIER origen (cancelacion o sobrepago).
    /// Es el eje OPUESTO al de cobrar; el backend los expone separados y NUNCA netea uno contra el otro.
    /// </summary>
    private async Task<List<CurrencyAmountDto>> BuildCustomerCreditByCurrencyAsync(
        int customerId, string? ownerScope, CancellationToken cancellationToken)
    {
        var grouped = await _dbContext.ClientCreditEntries
            .AsNoTracking()
            .Where(entry => entry.CustomerId == customerId && entry.RemainingBalance > 0m
                && (ownerScope == null
                    || (entry.SourceReserva != null && entry.SourceReserva.ResponsibleUserId == ownerScope)
                    || (entry.BookingCancellation != null && entry.BookingCancellation.Reserva != null
                        && entry.BookingCancellation.Reserva.ResponsibleUserId == ownerScope)))
            .GroupBy(entry => entry.Currency)
            .Select(g => new { Currency = g.Key, Amount = g.Sum(x => x.RemainingBalance) })
            .ToListAsync(cancellationToken);

        return ToOrderedCurrencyAmounts(grouped.Select(x => (x.Currency, x.Amount)));
    }

    /// <summary>
    /// DETALLE de los saldos a favor disponibles del cliente (cada ClientCreditEntry con saldo &gt; 0).
    /// A diferencia de <see cref="BuildCustomerCreditByCurrencyAsync"/> (que AGREGA por moneda para el cartel),
    /// esta devuelve fila por fila para que el front arme el flujo "usar saldo": el usuario elige de qué entry
    /// retira. Orden FIFO (más viejo primero) para que el front sugiera consumir primero el crédito más antiguo.
    /// </summary>
    public async Task<IReadOnlyList<CustomerAvailableCreditEntryDto>> GetCustomerAvailableCreditAsync(int id, CancellationToken cancellationToken)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        // Una sola query con los dos posibles orígenes incluidos:
        //  - Cancelación: la reserva vive en BookingCancellation.Reserva.
        //  - Sobrepago: la reserva vive en SourceReserva (FK directa del entry).
        // Traemos solo los campos que el DTO necesita (proyección) para no materializar entidades enteras
        // ni disparar N+1: el Select arma la fila con el número/PublicId de la reserva de origen en el mismo SQL.
        var entries = await _dbContext.ClientCreditEntries
            .AsNoTracking()
            .Where(entry => entry.CustomerId == id && entry.RemainingBalance > 0m
                && (ownerScope == null
                    || (entry.SourceReserva != null && entry.SourceReserva.ResponsibleUserId == ownerScope)
                    || (entry.BookingCancellation != null && entry.BookingCancellation.Reserva != null
                        && entry.BookingCancellation.Reserva.ResponsibleUserId == ownerScope)))
            .OrderBy(entry => entry.CreatedAt)
            .Select(entry => new CustomerAvailableCreditEntryDto
            {
                EntryPublicId = entry.PublicId,
                RemainingBalance = entry.RemainingBalance,
                CreditedAmount = entry.CreditedAmount,
                Currency = entry.Currency,
                CreatedAt = entry.CreatedAt,

                // Origen: primero el de la cancelación; si el crédito nació de un sobrepago,
                // la cancelación es null y usamos la reserva sobre-pagada. Si ninguno existe
                // (crédito legacy sin trazabilidad), quedan null y el front simplemente no muestra origen.
                OriginReservaNumber = entry.BookingCancellation != null && entry.BookingCancellation.Reserva != null
                    ? entry.BookingCancellation.Reserva.NumeroReserva
                    : (entry.SourceReserva != null ? entry.SourceReserva.NumeroReserva : null),
                OriginReservaPublicId = entry.BookingCancellation != null && entry.BookingCancellation.Reserva != null
                    ? entry.BookingCancellation.Reserva.PublicId
                    : (entry.SourceReserva != null ? (Guid?)entry.SourceReserva.PublicId : null),
            })
            .ToListAsync(cancellationToken);

        // Normalizamos la moneda en memoria (null/vacío -> ARS para créditos legacy) por coherencia con el
        // resto de la cuenta. No se puede hacer dentro del Select porque Monedas.Normalizar no traduce a SQL.
        foreach (var dto in entries)
        {
            dto.Currency = Monedas.Normalizar(dto.Currency);
        }

        return entries;
    }

    /// <summary>
    /// Deuda del cliente DESGLOSADA POR RESERVA y por moneda. Reusa la MISMA fuente y el MISMO filtro que
    /// <see cref="IFinancePositionService.GetCustomerReceivableByCurrencyAsync"/> (la deuda del cliente por
    /// moneda): filas de <c>ReservaMoneyByCurrency</c> con saldo &gt; 0, de reservas donde el cliente es el
    /// PAGADOR (<c>PayerId</c>) y el estado es venta firme (<c>ReceivableDebtStatuses</c>). La unica diferencia
    /// es que NO agrega a traves de reservas: deja una linea por reserva y moneda. Asi el total por moneda de
    /// esta vista reconcilia exactamente con el agregado global del cliente (una sola fuente de verdad).
    ///
    /// El lado VENTA no enmascara montos (a diferencia de la cuenta del proveedor); el gate de permiso vive en
    /// el controller (clientes.view + cobranzas.view), igual que el resto de los montos de la cuenta del cliente.
    /// </summary>
    public async Task<CustomerDebtByReservaDto> GetCustomerDebtByReservaAsync(int id, CancellationToken cancellationToken)
    {
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new { entity.PublicId, entity.FullName })
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new KeyNotFoundException("Cliente no encontrado");
        }

        // El selector de cobro usa exactamente los mismos open items documentados del extracto, sin paginado
        // ni limite artificial de 100 reservas. Un saldo positivo queda separado por reserva y moneda.
        var statement = await GetCustomerAccountStatementAsync(id, cancellationToken);
        var reservaNames = await _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id)
            .Select(reserva => new { reserva.PublicId, reserva.Name })
            .ToDictionaryAsync(reserva => reserva.PublicId, reserva => reserva.Name, cancellationToken);

        var rows = statement.Currencies
            .SelectMany(block => block.Lines.Select(line => new
            {
                line.ReservaPublicId,
                line.NumeroReserva,
                Currency = block.Currency,
                Net = line.Charge - line.Credit
            }))
            .GroupBy(row => new { row.ReservaPublicId, row.NumeroReserva, row.Currency })
            .Select(group => new
            {
                group.Key.ReservaPublicId,
                group.Key.NumeroReserva,
                group.Key.Currency,
                Balance = EconomicRulesHelper.RoundCurrency(group.Sum(row => row.Net))
            })
            .Where(row => row.Balance > 0m)
            .ToList();

        return BuildCustomerDebtByReserva(customer.PublicId, customer.FullName, rows.Select(row => (
            row.ReservaPublicId,
            row.NumeroReserva,
            reservaNames.GetValueOrDefault(row.ReservaPublicId),
            row.Currency,
            row.Balance)));
    }

    /// <summary>
    /// EXTRACTO (libro mayor) de la cuenta por cobrar del cliente, calculado EN EL SERVIDOR. Espejo del
    /// extracto del proveedor pero del lado VENTA y cruzando TODAS las reservas en firme del cliente.
    ///
    /// <para><b>Por que en el servidor (y no en el navegador)</b>: el extracto viejo se armaba en el front
    /// mezclando pagos + facturas con un techo de 500 movimientos, y su saldo NO cerraba con el "Debe" del
    /// header. Aca el saldo de cierre de cada moneda reconcilia POR CONSTRUCCION con
    /// <see cref="IFinancePositionService.GetCustomerReceivableByCurrencyAsync"/>: parte de la MISMA fuente
    /// (venta confirmada como cargo, cobros imputados como abono, con la imputacion de
    /// <see cref="ReservaMoneyCalculator"/>), asi que Σcargos - Σabonos por moneda = ΣBalance de
    /// <c>ReservaMoneyByCurrency</c> en firme = el receivable del header.</para>
    ///
    /// <para><b>Facturar tarde</b>: el cargo es la venta CONFIRMADA (ConfirmedSale), NO la factura; una venta
    /// confirmada sin facturar todavia igual cuenta como cargo y el extracto cierra con el receivable (que
    /// tampoco mira facturas). <b>Saldo a favor</b>: el sobrepago se traslada al bolsillo del cliente
    /// (ClientCreditEntry) via un cobro puente que deja la reserva en 0; ese bolsillo es un ledger APARTE
    /// (el "A favor" del header) y NO forma parte del saldo de este extracto.</para>
    /// </summary>
    public async Task<CustomerAccountStatementDto> GetCustomerAccountStatementAsync(int id, CancellationToken cancellationToken)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        var customer = await _dbContext.Customers
            .AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.PublicId, c.FullName })
            .FirstOrDefaultAsync(cancellationToken);

        if (customer == null)
        {
            throw new KeyNotFoundException("Cliente no encontrado");
        }

        // Universo IDENTICO al del header receivable: reservas donde el cliente es el PAGADOR y el estado es
        // venta firme (ReceivableDebtStatuses). Solo la identidad de la reserva; los montos vienen de la
        // proyeccion (cargos) y de los pagos (abonos).
        var reservas = await _dbContext.Reservas
            .AsNoTracking()
            .Where(r => r.PayerId == id && (ownerScope == null || r.ResponsibleUserId == ownerScope))
            .Select(r => new ReservaIdentityRow(r.Id, r.PublicId, r.NumeroReserva, r.Name, r.CreatedAt))
            .ToListAsync(cancellationToken);

        if (reservas.Count == 0)
        {
            // Cliente sin reservas en firme: extracto vacio coherente (sin bloques de moneda).
            return new CustomerAccountStatementDto
            {
                CustomerPublicId = customer.PublicId,
                CustomerName = customer.FullName,
                AmountsVisible = true,
            };
        }

        var reservaIds = reservas.Select(r => r.Id).ToList();
        var reservaById = reservas.ToDictionary(r => r.Id);

        // CARGOS = venta confirmada por (reserva, moneda), leida de la MISMA proyeccion que usa el header. Solo
        // filas con ConfirmedSale != 0 (una reserva sin venta resuelta —p.ej. solo con una sena— no aporta
        // cargo; su cobro igual aparece como abono).
        var invoiceRows = await _dbContext.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.ReservaId != null
                && reservaIds.Contains(invoice.ReservaId.Value)
                && invoice.Resultado == "A")
            .Select(invoice => new
            {
                ReservaId = invoice.ReservaId!.Value,
                invoice.PublicId,
                invoice.TipoComprobante,
                invoice.ImporteTotal,
                invoice.MonId,
                invoice.PuntoDeVenta,
                invoice.NumeroComprobante,
                invoice.IssuedAt,
                invoice.CreatedAt
            })
            .ToListAsync(cancellationToken);

        // ABONOS = cobros VIVOS de esas reservas. El filtro global !IsDeleted ya excluye los borrados; sumamos
        // Status != "Cancelled" para igualar EXACTAMENTE el universo de ReservaMoneyCalculator (asi la suma de
        // abonos por moneda == TotalPaid de la proyeccion, y el saldo de cierre cuadra con el Balance). Incluye
        // los cobros PUENTE (sobrepago / saldo a favor aplicado): no movieron caja pero BAJAN la deuda, por eso
        // deben estar o el saldo no cerraria. No exponemos sus Notes (llevan un GUID interno): la descripcion la
        // arma BuildStatementPaymentDescription sin filtrarlo.
        var paymentRows = await _dbContext.Payments
            .AsNoTracking()
            .Where(p => p.ReservaId != null && reservaIds.Contains(p.ReservaId.Value)
                && p.Status != "Cancelled")
            .Select(p => new
            {
                ReservaId = p.ReservaId!.Value,
                p.PublicId,
                p.PaidAt,
                p.Method,
                p.Amount,
                p.Currency,
                p.ImputedCurrency,
                p.ImputedAmount,
                p.AffectsCash,
                ReceiptNumber = p.Receipt != null ? p.Receipt.ReceiptNumber : null,
            })
            .ToListAsync(cancellationToken);

        // Armamos las lineas planas: primero TODAS las ventas, despues TODOS los cobros. El builder ordena por
        // fecha de forma estable, asi ante misma fecha la venta (cargo) queda antes que el cobro (abono).
        var inputLines = new List<CustomerAccountStatementInputLine>(invoiceRows.Count + paymentRows.Count);

        foreach (var invoice in invoiceRows)
        {
            var category = InvoiceComprobanteHelpers.Categorize(invoice.TipoComprobante);
            if (category == InvoiceComprobanteCategory.Unknown) continue;

            var reserva = reservaById[invoice.ReservaId];
            var isCredit = category == InvoiceComprobanteCategory.CreditNote;
            var kind = category switch
            {
                InvoiceComprobanteCategory.Invoice => CustomerAccountStatementLineKinds.Invoice,
                InvoiceComprobanteCategory.DebitNote => CustomerAccountStatementLineKinds.DebitNote,
                InvoiceComprobanteCategory.CreditNote => CustomerAccountStatementLineKinds.CreditNote,
                _ => CustomerAccountStatementLineKinds.Invoice
            };
            var description = category switch
            {
                InvoiceComprobanteCategory.Invoice => "Factura",
                InvoiceComprobanteCategory.DebitNote => "Nota de debito",
                InvoiceComprobanteCategory.CreditNote => "Nota de credito",
                _ => "Comprobante"
            };
            inputLines.Add(new CustomerAccountStatementInputLine(
                Date: invoice.IssuedAt ?? invoice.CreatedAt,
                Kind: kind,
                Description: description,
                DocumentRef: $"{invoice.PuntoDeVenta:D4}-{invoice.NumeroComprobante:D8}",
                ReservaPublicId: reserva.PublicId,
                NumeroReserva: reserva.NumeroReserva,
                Currency: Domain.Helpers.ArcaCurrencyMapper.ToIso(invoice.MonId) ?? Monedas.ARS,
                Charge: isCredit ? 0m : invoice.ImporteTotal,
                Credit: isCredit ? invoice.ImporteTotal : 0m,
                SourcePublicId: invoice.PublicId));
        }

        foreach (var payment in paymentRows)
        {
            var reserva = reservaById[payment.ReservaId];

            // Misma imputacion que ReservaMoneyCalculator: la moneda y el monto a los que el cobro baja la deuda.
            string imputedCurrency = Monedas.Normalizar(payment.ImputedCurrency ?? payment.Currency);
            decimal imputedAmount = payment.ImputedAmount ?? payment.Amount;

            inputLines.Add(new CustomerAccountStatementInputLine(
                Date: payment.PaidAt,
                Kind: payment.AffectsCash
                    ? CustomerAccountStatementLineKinds.Payment
                    : CustomerAccountStatementLineKinds.CreditApplication,
                Description: BuildStatementPaymentDescription(payment.AffectsCash, imputedAmount, payment.ReceiptNumber, payment.Method),
                DocumentRef: payment.ReceiptNumber,
                ReservaPublicId: reserva.PublicId,
                NumeroReserva: reserva.NumeroReserva,
                Currency: imputedCurrency,
                Charge: 0m,
                Credit: imputedAmount,
                SourcePublicId: payment.PublicId));
        }

        var statement = CustomerAccountStatementBuilder.Build(inputLines);

        return MapCustomerAccountStatement(customer.PublicId, customer.FullName, statement);
    }

    /// <summary>Identidad minima de una reserva para el extracto (sin montos: esos vienen de proyeccion/pagos).</summary>
    private readonly record struct ReservaIdentityRow(
        int Id, Guid PublicId, string NumeroReserva, string Name, DateTime CreatedAt);

    /// <summary>Texto legible de una linea de venta del extracto: el nombre del expediente, o un fallback claro.</summary>
    private static string BuildStatementSaleDescription(string? reservaName)
        => string.IsNullOrWhiteSpace(reservaName) ? "Venta confirmada" : reservaName.Trim();

    /// <summary>
    /// Arma el texto legible de un cobro para el extracto SIN filtrar datos internos. Para un cobro PUENTE
    /// (no movio caja) distingue por el signo: negativo = excedente que se traslado al saldo a favor; positivo
    /// = saldo a favor que se aplico a esta reserva. Para un cobro real: nº de recibo si existe, si no el metodo.
    /// </summary>
    private static string BuildStatementPaymentDescription(bool affectsCash, decimal imputedAmount, string? receiptNumber, string? method)
    {
        if (!affectsCash)
        {
            // Puente: sobrepago trasladado (negativo) vs saldo a favor aplicado (positivo). Nunca exponemos Notes.
            return imputedAmount < 0m ? "Excedente trasladado a saldo a favor" : "Saldo a favor aplicado";
        }

        if (!string.IsNullOrWhiteSpace(receiptNumber))
        {
            return $"Cobro recibo {receiptNumber}";
        }

        // Cobro sin recibo emitido todavia: mostramos el metodo en ESPAÑOL, nunca el codigo interno
        // ("Transfer"/"Cash"). Un metodo vacio o desconocido cae a "Cobro" pelado para no filtrar el codigo crudo.
        var metodoLabel = MetodoCobroLabelEspanol(method);
        return metodoLabel is null ? "Cobro" : $"Cobro por {metodoLabel}";
    }

    /// <summary>
    /// Etiqueta en español del metodo de pago. El modelo guarda claves internas en ingles
    /// (Transfer/Cash/Check/Card); esta funcion las traduce para el extracto de cara al usuario.
    /// Devuelve null si el metodo es vacio o desconocido, para NO exponer un codigo crudo.
    /// </summary>
    private static string? MetodoCobroLabelEspanol(string? method) => method?.Trim().ToLowerInvariant() switch
    {
        "transfer" => "transferencia",
        "cash" => "efectivo",
        "check" => "cheque",
        "card" => "tarjeta",
        _ => null,
    };

    /// <summary>
    /// Mapea el extracto del dominio (value object puro) al DTO de salida, redondeando los montos a 2 decimales
    /// (coherente con las columnas decimal(18,2)). Lado VENTA: NO se enmascara (no hay costo ni margen), asi que
    /// <see cref="CustomerAccountStatementDto.AmountsVisible"/> va siempre en true tras el gate de permiso.
    /// </summary>
    private static CustomerAccountStatementDto MapCustomerAccountStatement(
        Guid customerPublicId, string customerName, CustomerAccountStatement statement)
    {
        var dto = new CustomerAccountStatementDto
        {
            CustomerPublicId = customerPublicId,
            CustomerName = customerName,
            AmountsVisible = true,
        };

        foreach (var block in statement.Currencies)
        {
            var blockDto = new CustomerAccountStatementCurrencyBlockDto
            {
                Currency = block.Currency,
                ClosingBalance = EconomicRulesHelper.RoundCurrency(block.ClosingBalance),
                UnappliedCredit = EconomicRulesHelper.RoundCurrency(block.UnappliedCredit),
            };

            foreach (var line in block.Lines)
            {
                blockDto.Lines.Add(new CustomerAccountStatementLineDto
                {
                    Date = line.Date,
                    Kind = line.Kind,
                    Description = line.Description,
                    DocumentRef = line.DocumentRef,
                    ReservaPublicId = line.ReservaPublicId,
                    NumeroReserva = line.NumeroReserva,
                    SourcePublicId = line.SourcePublicId,
                    Currency = line.Currency,
                    Charge = EconomicRulesHelper.RoundCurrency(line.Charge),
                    Credit = EconomicRulesHelper.RoundCurrency(line.Credit),
                    RunningBalance = EconomicRulesHelper.RoundCurrency(line.RunningBalance),
                });
            }

            dto.Currencies.Add(blockDto);
        }

        return dto;
    }

    /// <summary>
    /// Agrupa las filas (reserva + moneda + saldo) en una linea por reserva con sus monedas. Funcion pura en
    /// memoria (sin EF): normaliza la moneda (legacy null/vacio -> ARS), suma por moneda dentro de cada reserva,
    /// y ordena reservas por numero y monedas por codigo, para que el shape sea estable.
    /// </summary>
    private static CustomerDebtByReservaDto BuildCustomerDebtByReserva(
        Guid customerPublicId,
        string customerName,
        IEnumerable<(Guid ReservaPublicId, string? NumeroReserva, string? FileName, string? Currency, decimal Balance)> rows)
    {
        var accumulatorsByReserva = new Dictionary<Guid, CustomerReservaDebtAccumulator>();

        foreach (var row in rows)
        {
            if (!accumulatorsByReserva.TryGetValue(row.ReservaPublicId, out var accumulator))
            {
                accumulator = new CustomerReservaDebtAccumulator(row.ReservaPublicId, row.NumeroReserva, row.FileName);
                accumulatorsByReserva[row.ReservaPublicId] = accumulator;
            }

            string currency = Monedas.Normalizar(row.Currency);
            accumulator.AddDebt(currency, row.Balance);
        }

        var reservaLines = accumulatorsByReserva.Values
            .Select(accumulator => accumulator.ToDto())
            .OrderBy(line => line.NumeroReserva, StringComparer.Ordinal)
            .ToList();

        return new CustomerDebtByReservaDto
        {
            CustomerPublicId = customerPublicId,
            CustomerName = customerName,
            Reservas = reservaLines
        };
    }

    /// <summary>
    /// Acumulador en memoria de la deuda de UNA reserva por moneda mientras se arma el desglose. Guarda la
    /// identidad de la reserva y suma saldos por moneda (por si llegaran dos filas de la misma moneda tras
    /// normalizar legacy null -> ARS).
    /// </summary>
    private sealed class CustomerReservaDebtAccumulator
    {
        private readonly Guid _reservaPublicId;
        private readonly string? _numeroReserva;
        private readonly string? _fileName;
        private readonly Dictionary<string, decimal> _debtByCurrency = new(StringComparer.Ordinal);

        public CustomerReservaDebtAccumulator(Guid reservaPublicId, string? numeroReserva, string? fileName)
        {
            _reservaPublicId = reservaPublicId;
            _numeroReserva = numeroReserva;
            _fileName = fileName;
        }

        public void AddDebt(string currency, decimal amount)
        {
            _debtByCurrency.TryGetValue(currency, out var current);
            _debtByCurrency[currency] = current + amount;
        }

        public CustomerDebtReservaLineDto ToDto()
        {
            var currencies = _debtByCurrency
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => new CurrencyAmountDto
                {
                    Currency = kvp.Key,
                    Amount = EconomicRulesHelper.RoundCurrency(kvp.Value)
                })
                .ToList();

            return new CustomerDebtReservaLineDto
            {
                ReservaPublicId = _reservaPublicId,
                NumeroReserva = _numeroReserva,
                FileName = _fileName,
                DebtByCurrency = currencies
            };
        }
    }

    /// <summary>
    /// Normaliza la moneda (null/vacio -> ARS para datos legacy), redondea y ordena por moneda. Las lineas en 0
    /// no se omiten aca (un grupo solo existe si tuvo saldo &gt; 0 en la query).
    /// </summary>
    private static List<CurrencyAmountDto> ToOrderedCurrencyAmounts(IEnumerable<(string? Currency, decimal Amount)> rows)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (currency, amount) in rows)
        {
            var key = Monedas.Normalizar(currency);
            totals[key] = totals.TryGetValue(key, out var current) ? current + amount : amount;
        }

        return totals
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new CurrencyAmountDto
            {
                Currency = kvp.Key,
                Amount = EconomicRulesHelper.RoundCurrency(kvp.Value)
            })
            .ToList();
    }

    public async Task<PagedResponse<CustomerAccountReservaListItemDto>> GetCustomerAccountReservasAsync(int id, PagedQuery query, CancellationToken cancellationToken)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id
                && (ownerScope == null || reserva.ResponsibleUserId == ownerScope));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            reservasQuery = reservasQuery.Where(reserva =>
                reserva.NumeroReserva.ToLower().Contains(normalized) ||
                reserva.Name.ToLower().Contains(normalized) ||
                reserva.Status.ToLower().Contains(normalized));
        }

        reservasQuery = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
            ? reservasQuery.OrderByDescending(reserva => reserva.CreatedAt).ThenByDescending(reserva => reserva.Id)
            : reservasQuery.OrderBy(reserva => reserva.CreatedAt).ThenBy(reserva => reserva.Id);

        var paged = await reservasQuery
            .Select(reserva => new CustomerAccountReservaListItemDto
            {
                PublicId = reserva.PublicId,
                NumeroReserva = reserva.NumeroReserva,
                Name = reserva.Name,
                Status = reserva.Status,
                TotalSale = reserva.TotalSale,
                Balance = reserva.Balance,
                CreatedAt = reserva.CreatedAt,
                StartDate = reserva.StartDate,
                Paid = reserva.TotalPaid
            })
            .ToPagedResponseAsync(query, cancellationToken);

        // Tanda 6 (C4): completamos la plata REAL por moneda de cada fila de la pagina, para que el front deje
        // de mostrar "ARS" hardcodeado. Una sola query batcheada por los PublicId de la pagina (sin N+1).
        await FillReservaMoneyByCurrencyAsync(paged.Items, cancellationToken);

        // "Multas en la cuenta del cliente" (2026-07-15): completamos el contexto de plata de las filas
        // ANULADAS (CancelledMoneyContext/CancelledPenaltyAmount/CancelledPenaltyCurrency). Depende de
        // PorMoneda ya lleno (arriba) para netear el monto pendiente contra el saldo de su moneda.
        await FillCancelledMoneyContextForCustomerReservasAsync(paged.Items, cancellationToken);

        return paged;
    }

    /// <summary>
    /// Llena <c>CancelledMoneyContext</c>/<c>CancelledPenaltyAmount</c>/<c>CancelledPenaltyCurrency</c> de las
    /// filas ANULADAS de la solapa "Reservas" de la cuenta del cliente. Es el MISMO derivador que usa el
    /// listado general de reservas (<c>ReservaService.FillCancelledMoneyContextForListAsync</c>): reusa
    /// idénticas las piezas compartidas de la regla (<c>CancellationPenaltyRules</c>, <c>ReservationDebtRules</c>,
    /// <c>ReservaService.AggregatePendingPenaltiesByCurrency</c>). Lo único que cambia acá es el tipo de fila
    /// de salida (esta cuenta usa <c>CustomerAccountReservaListItemDto</c>, no <c>ReservaListDto</c>), así que
    /// el armado de la query se repite pero la REGLA no: viene de las mismas funciones compartidas.
    ///
    /// <para>Dos queries batcheadas (vivas / en revisión) para toda la página, nunca una por fila (sin N+1).</para>
    /// </summary>
    private async Task FillCancelledMoneyContextForCustomerReservasAsync(
        IReadOnlyList<CustomerAccountReservaListItemDto> items, CancellationToken cancellationToken)
    {
        // Solo las filas anuladas necesitan contexto de plata; el resto queda con los 3 campos en null.
        var cancelledItems = items.Where(item => ReservaService.IsCancelledLikeStatus(item.Status)).ToList();
        if (cancelledItems.Count == 0) return;

        var publicIds = cancelledItems.Select(item => item.PublicId).ToList();

        // Query 1: filas de la pagina con multa VIVA, con el monto/moneda congelados de CADA BC (puede haber
        // mas de una linea por reserva si hubo mas de una cancelacion parcial con multa propia) + la ND
        // vinculada (si ya se emitio).
        var liveRows = await (
            from bc in _dbContext.BookingCancellations.AsNoTracking().Where(CancellationPenaltyRules.LiveDebitNotePredicate)
            join reservaPadre in _dbContext.Reservas.AsNoTracking() on bc.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
            select new
            {
                reservaPadre.PublicId,
                bc.PenaltyAmountAtEvent,
                bc.PenaltyCurrencyAtEvent,
                bc.DebitNoteInvoiceId
            })
            .ToListAsync(cancellationToken);

        var liveRowsByPublicId = liveRows
            .GroupBy(row => row.PublicId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => (row.PenaltyAmountAtEvent, row.PenaltyCurrencyAtEvent, row.DebitNoteInvoiceId)).ToList());

        // TANDA C (2026-07-16): consulta batcheada de TODAS las NDs de la PAGINA entera (mismo patron que
        // ReservaService.FillCancelledMoneyContextForListAsync) — el pendiente de cada multa se calcula
        // ND-BASED, contra SU comprobante, no contra el saldo de la reserva.
        var debitNoteIds = liveRows
            .Where(row => row.DebitNoteInvoiceId.HasValue)
            .Select(row => row.DebitNoteInvoiceId!.Value)
            .Distinct()
            .ToList();
        var debitNoteTotals = await DebitNoteOutstandingLookup.LoadDebitNoteTotalsAsync(
            _dbContext, debitNoteIds, cancellationToken);
        var creditedByDebitNote = await DebitNoteOutstandingLookup.LoadCreditedAmountsAsync(
            _dbContext, debitNoteIds, cancellationToken);
        var collectedByDebitNote = await DebitNoteOutstandingLookup.LoadCollectedAmountsAsync(
            _dbContext, debitNoteIds, cancellationToken);

        // Query 2: filas de la pagina con multa EN REVISION. Solo necesitamos el set de PublicId.
        var underReviewIds = (await (
            from bc in _dbContext.BookingCancellations.AsNoTracking().Where(CancellationPenaltyRules.PenaltyUnderReviewPredicate)
            join reservaPadre in _dbContext.Reservas.AsNoTracking() on bc.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
            select reservaPadre.PublicId).Distinct().ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var item in cancelledItems)
        {
            ReservationDebtRules.DebitNoteBacking backing;
            if (liveRowsByPublicId.ContainsKey(item.PublicId))
                backing = ReservationDebtRules.DebitNoteBacking.Live;
            else if (underReviewIds.Contains(item.PublicId))
                backing = ReservationDebtRules.DebitNoteBacking.UnderReview;
            else
                backing = ReservationDebtRules.DebitNoteBacking.None;

            var context = ReservationDebtRules.DeriveForCancelled(item.Balance, backing);
            item.CancelledMoneyContext = ReservationDebtRules.ToDtoString(context);

            // El monto solo acompaña al caso "multa por cobrar" (PenaltyReceivable); es lo PENDIENTE de cobro
            // ND-BASED (contra la propia Nota de Debito) — mismo criterio que la ficha/listado.
            if (context == ReservationDebtRules.CancelledMoneyContext.PenaltyReceivable
                && liveRowsByPublicId.TryGetValue(item.PublicId, out var snapshots))
            {
                var penalties = ReservaService.AggregatePendingPenaltiesByCurrency(
                    snapshots, debitNoteTotals, creditedByDebitNote, collectedByDebitNote);
                var primary = penalties.Count > 0 ? penalties[0] : null;
                item.CancelledPenaltyAmount = primary?.Amount;
                item.CancelledPenaltyCurrency = primary?.Currency;
            }
        }
    }

    /// <summary>
    /// Tanda 6 (C4): llena <c>PorMoneda</c>/<c>EsMultimoneda</c> de cada fila de la cuenta del cliente leyendo
    /// la tabla materializada <c>ReservaMoneyByCurrency</c>. Es el espejo SOLO-VENTA de
    /// <c>ReservaService.FillPorMonedaForListAsync</c>: NO expone costo ni margen (la cuenta del cliente es del
    /// lado venta). Una sola query por los PublicId de la pagina (evita N+1). Una reserva sin filas de plata
    /// materializadas (nueva o legacy sin backfill) queda con <c>PorMoneda</c> vacio y el front cae al escalar.
    /// </summary>
    private async Task FillReservaMoneyByCurrencyAsync(
        IReadOnlyList<CustomerAccountReservaListItemDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        var publicIds = items.Select(item => item.PublicId).ToList();

        // Join explicito contra Reservas (no nav implicita) para resolver el PublicId con el que matchear el
        // DTO y correr igual en Postgres e InMemory. Solo campos de VENTA: no traemos TotalCost.
        var rows = await (
            from row in _dbContext.ReservaMoneyByCurrency.AsNoTracking()
            join reservaPadre in _dbContext.Reservas.AsNoTracking() on row.ReservaId equals reservaPadre.Id
            where publicIds.Contains(reservaPadre.PublicId)
            select new
            {
                ReservaPublicId = reservaPadre.PublicId,
                row.Currency,
                row.TotalSale,
                row.TotalPaid,
                row.Balance
            }).ToListAsync(cancellationToken);

        var byReserva = rows
            .GroupBy(row => row.ReservaPublicId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var item in items)
        {
            if (!byReserva.TryGetValue(item.PublicId, out var reservaRows)) continue;

            item.PorMoneda = reservaRows
                .OrderBy(row => row.Currency, StringComparer.Ordinal)
                .Select(row => new CustomerAccountReservaMoneyLineDto
                {
                    Currency = row.Currency,
                    TotalSale = row.TotalSale,
                    Paid = row.TotalPaid,
                    Balance = row.Balance
                })
                .ToList();

            item.EsMultimoneda = item.PorMoneda.Count > 1;
        }
    }

    public async Task<PagedResponse<CustomerAccountPaymentListItemDto>> GetCustomerAccountPaymentsAsync(int id, PagedQuery query, CancellationToken cancellationToken)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        var paymentsQuery = _dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.Reserva != null && payment.Reserva.PayerId == id
                && (ownerScope == null || payment.Reserva.ResponsibleUserId == ownerScope))
            // ADR-022 §4.9 (fix S1-bis): excluir el Payment puente del saldo a favor (Method="SaldoAFavor",
            // AffectsCash=false, OriginalPaymentId!=null, monto negativo). Es respaldo INTERNO del sobrepago, no
            // un cobro real, y su Notes contiene un GUID que no debe filtrarse a la pestaña Pagos del cliente.
            // Mismo predicado inline que PaymentService.GetPaymentsForReservaAsync: OverpaymentCreditCleanup.
            // IsOverpaymentBridge(Payment) NO se usa aca porque recibe la entidad y EF no lo traduce a SQL.
            // Los totales de la cuenta del cliente (TotalSales/TotalPaid/TotalBalance, ReceivableByCurrency,
            // CreditBalanceByCurrency) se calculan por OTRA via (Reserva.* y ClientCreditEntries), no sumando
            // esta lista, asi que ocultar el puente no los altera.
            // FC4 (2026-06-14): excluir tambien el puente de saldo a favor APLICADO (positivo). Su Notes lleva
            // el GUID del bolsillo y no debe filtrarse a la pestaña Pagos del cliente.
            .Where(payment => !(
                (payment.Method == OverpaymentCreditCleanup.BridgeMethod
                    && !payment.AffectsCash && payment.OriginalPaymentId != null)
                || (payment.Method == AppliedCreditBridge.BridgeMethod
                    && !payment.AffectsCash && payment.AppliedFromCreditWithdrawalId != null)
                // ADR-044 "Deshacer una multa ya emitida": el puente de multa deshecha tampoco es un cobro real;
                // su Notes no debe filtrarse a la pestaña Pagos del cliente. Method + !AffectsCash lo identifica.
                || (payment.Method == ClientCreditService.DebitNoteUndoBridgeMethod
                    && !payment.AffectsCash)
                // Tanda D1 (2026-07-16): idem para el puente de saldo a favor aplicado contra una MULTA.
                || (payment.Method == AppliedCreditBridge.PenaltyBridgeMethod
                    && !payment.AffectsCash && payment.AppliedFromCreditWithdrawalId != null)));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            paymentsQuery = paymentsQuery.Where(payment =>
                payment.Method.ToLower().Contains(normalized) ||
                payment.Reference != null && payment.Reference.ToLower().Contains(normalized) ||
                payment.Notes != null && payment.Notes.ToLower().Contains(normalized) ||
                payment.Reserva != null && payment.Reserva.NumeroReserva.ToLower().Contains(normalized));
        }

        paymentsQuery = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
            ? paymentsQuery.OrderByDescending(payment => payment.PaidAt).ThenByDescending(payment => payment.Id)
            : paymentsQuery.OrderBy(payment => payment.PaidAt).ThenBy(payment => payment.Id);

        var response = await paymentsQuery
            .Select(payment => new CustomerAccountPaymentListItemDto
            {
                PublicId = payment.PublicId,
                Amount = payment.Amount,
                // Moneda real del cobro (sobre la que esta expresado Amount). Es el detalle "caja que entro".
                Currency = payment.Currency,
                // Moneda y monto IMPUTADOS (lo que efectivamente bajo de la deuda). El extracto agrupa y lleva
                // saldo corriente por ESTA moneda para que cuadre con lo que el cliente debe (ADR-021). El
                // fallback "?? Currency" / "?? Amount" SI se traduce a SQL (COALESCE); lo unico que NO traduce
                // es Monedas.Normalizar, asi que dejamos el codigo crudo aca y lo normalizamos en memoria abajo.
                ImputedCurrency = payment.ImputedCurrency ?? payment.Currency,
                ImputedAmount = payment.ImputedAmount ?? payment.Amount,
                Method = payment.Method,
                PaidAt = payment.PaidAt,
                Notes = payment.Notes,
                ReservaPublicId = payment.Reserva != null ? (Guid?)payment.Reserva.PublicId : null,
                NumeroReserva = payment.Reserva != null ? payment.Reserva.NumeroReserva : null,
                FileName = payment.Reserva != null ? payment.Reserva.Name : null,
                ReceiptPublicId = payment.Receipt != null ? (Guid?)payment.Receipt.PublicId : null,
                ReceiptNumber = payment.Receipt != null ? payment.Receipt.ReceiptNumber : null,
                ReceiptStatus = payment.Receipt != null ? payment.Receipt.Status : null
            })
            .ToPagedResponseAsync(query, cancellationToken);

        // Normalizamos la moneda imputada en memoria (legacy null/vacio -> ARS), igual que en
        // GetCustomerAvailableCreditAsync: Monedas.Normalizar no traduce a SQL, por eso va aca y no en el Select.
        // Garantiza codigos ISO limpios ("ARS"/"USD") y nunca un codigo interno o vacio.
        foreach (var item in response.Items)
        {
            item.ImputedCurrency = Monedas.Normalizar(item.ImputedCurrency);
        }

        return response;
    }

    public async Task<PagedResponse<InvoiceListDto>> GetCustomerAccountInvoicesAsync(int id, PagedQuery query, CancellationToken cancellationToken)
    {
        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        var invoicesQuery = _dbContext.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.Reserva != null && invoice.Reserva.PayerId == id
                && (ownerScope == null || invoice.Reserva.ResponsibleUserId == ownerScope));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            invoicesQuery = invoicesQuery.Where(invoice =>
                invoice.NumeroComprobante.ToString().Contains(normalized) ||
                invoice.ForceReason != null && invoice.ForceReason.ToLower().Contains(normalized) ||
                invoice.Reserva != null && invoice.Reserva.NumeroReserva.ToLower().Contains(normalized));
        }

        invoicesQuery = !string.Equals(query.SortDir, "asc", StringComparison.OrdinalIgnoreCase)
            ? invoicesQuery.OrderByDescending(invoice => invoice.CreatedAt).ThenByDescending(invoice => invoice.Id)
            : invoicesQuery.OrderBy(invoice => invoice.CreatedAt).ThenBy(invoice => invoice.Id);

        return await invoicesQuery
            .Select(invoice => new InvoiceListDto
            {
                PublicId = invoice.PublicId,
                ReservaPublicId = invoice.Reserva != null ? (Guid?)invoice.Reserva.PublicId : null,
                NumeroReserva = invoice.Reserva != null ? invoice.Reserva.NumeroReserva : null,
                CustomerName = invoice.Reserva != null && invoice.Reserva.Payer != null ? invoice.Reserva.Payer.FullName : null,
                TipoComprobante = invoice.TipoComprobante,
                PuntoDeVenta = invoice.PuntoDeVenta,
                NumeroComprobante = invoice.NumeroComprobante,
                ImporteTotal = invoice.ImporteTotal,
                CreatedAt = invoice.CreatedAt,
                CAE = invoice.CAE,
                Resultado = invoice.Resultado,
                Observaciones = invoice.Observaciones,
                WasForced = invoice.WasForced,
                ForceReason = invoice.ForceReason,
                // ForcedByUserId (GUID interno de Identity) NO se proyecta: minimizacion de datos.
                ForcedByUserName = invoice.ForcedByUserName,
                ForcedAt = invoice.ForcedAt,
                OutstandingBalanceAtIssuance = invoice.OutstandingBalanceAtIssuance,
                InvoiceType =
                    invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3 ? "A" :
                    invoice.TipoComprobante == 6 || invoice.TipoComprobante == 7 || invoice.TipoComprobante == 8 ? "B" :
                    invoice.TipoComprobante == 11 || invoice.TipoComprobante == 12 || invoice.TipoComprobante == 13 ? "C" :
                    invoice.TipoComprobante == 51 ? "M" :
                    "UNK",
                // Invoice.MonId guarda el codigo de ARCA ("PES"/"DOL"); el front (y los cobros) hablan ISO
                // ("ARS"/"USD"). Mapeo inline (no ArcaCurrencyMapper.ToIso, que EF no traduce a SQL) para
                // que la factura caiga en el bloque de su moneda en el extracto. Fallback "ARS" = regla
                // legacy (fila sin moneda explicita se lee como pesos).
                Currency =
                    invoice.MonId == "DOL" ? "USD" :
                    "ARS"
            })
            .ToPagedResponseAsync(query, cancellationToken);
    }

    private static IQueryable<Customer> ApplyCustomerSearch(IQueryable<Customer> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLowerInvariant();
        return query.Where(customer =>
            customer.FullName.ToLower().Contains(normalized) ||
            customer.DocumentNumber != null && customer.DocumentNumber.ToLower().Contains(normalized) ||
            customer.TaxId != null && customer.TaxId.ToLower().Contains(normalized) ||
            customer.Email != null && customer.Email.ToLower().Contains(normalized));
    }

    private static IQueryable<Customer> ApplyCustomerOrdering(IQueryable<Customer> query, CustomerListQuery request)
    {
        var sortBy = (request.SortBy ?? "fullName").Trim().ToLowerInvariant();
        var desc = string.Equals(request.SortDir, "desc", StringComparison.OrdinalIgnoreCase);

        // ADR-023 T1.4: el orden por saldo ("currentbalance") ya NO se hace en SQL (el saldo dejo de vivir en
        // Customer.CurrentBalance, que era zombie). Cuando se pide ese orden, GetCustomersAsync trae la pagina
        // por el orden estable (FullName) y la reordena en memoria con el saldo canonico ya enriquecido. Por eso
        // aca "currentbalance" cae al orden por FullName (estable) en vez de ordenar por la columna zombie.
        return sortBy switch
        {
            "createdat" => desc
                ? query.OrderByDescending(customer => customer.CreatedAt).ThenByDescending(customer => customer.Id)
                : query.OrderBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id),
            _ => desc
                ? query.OrderByDescending(customer => customer.FullName).ThenByDescending(customer => customer.Id)
                : query.OrderBy(customer => customer.FullName).ThenBy(customer => customer.Id)
        };
    }
}
