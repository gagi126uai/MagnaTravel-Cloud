using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
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

    public CustomerService(AppDbContext dbContext, IFinancePositionService financePosition, IAuditService? auditService = null)
    {
        _dbContext = dbContext;
        _financePosition = financePosition;
        _auditService = auditService;
    }

    public async Task<PagedResponse<CustomerListItemDto>> GetCustomersAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
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

        // Una sola pasada trae el saldo en firme de todos los clientes con deuda (PublicId -> escalar).
        var scalarsByPublicId = await _financePosition.GetReceivableScalarByCustomerPublicIdAsync(cancellationToken);
        foreach (var item in page.Items)
        {
            item.CurrentBalance = scalarsByPublicId.GetValueOrDefault(item.PublicId, 0m);
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
        customer.CurrentBalance = await _financePosition.GetCustomerReceivableScalarAsync(id, cancellationToken);
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

        // B1.15 Fase 0' (CODE-06): bloquear cambios fiscales (TaxId, TaxConditionId,
        // TaxCondition) cuando el cliente tiene factura emitida con CAE no anulada.
        // Otros campos (FullName, Email, Phone, Address, Notes, IsActive,
        // CreditLimit, DocumentType, DocumentNumber) siguen libres de editar —
        // representan datos operativos del cliente, no del comprobante AFIP.
        var fiscalDataChanged =
            !string.Equals(existing.TaxId, customer.TaxId, StringComparison.Ordinal) ||
            existing.TaxConditionId != customer.TaxConditionId ||
            !string.Equals(existing.TaxCondition, customer.TaxCondition, StringComparison.Ordinal);

        if (fiscalDataChanged)
        {
            var blockReason = await MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync(_dbContext, id, cancellationToken);
            if (blockReason != null)
            {
                throw new InvalidOperationException(blockReason);
            }
        }

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
        existing.TaxConditionId = customer.TaxConditionId;
        existing.TaxCondition = customer.TaxCondition;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

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

        // ADR-023 T1: el "Saldo Actual" del cliente sale del componente canonico (reservas en firme).
        customer.CurrentBalance = await _financePosition.GetCustomerReceivableScalarAsync(id, cancellationToken);

        // ADR-023 T1 (fix bug A3): el resumen TotalSales/TotalPaid/TotalBalance se calcula SOLO sobre las
        // reservas en firme. Antes sumaba TODAS las reservas del cliente (incluidas canceladas y cotizaciones)
        // -> el "Saldo Actual" grande del front estaba inflado. Con el mismo conjunto en los tres, el trio
        // TotalSales - TotalPaid queda coherente con TotalBalance (INV-T1-2).
        // ADR-033 (2026-06-16, A3/B1): el "Saldo Actual" y el resumen del cliente usan ahora la lista de
        // DEUDA cobrable (ReceivableDebtStatuses, incluye Closed). Una reserva Finalizada con deuda es deuda
        // real del cliente y debe sumar a su saldo. El conteo de reservas (abajo) NO cambia.
        var firmReservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id
                && FinancePositionService.ReceivableDebtStatuses.Contains(reserva.Status));

        // El contador de reservas sigue contando TODAS las del cliente (es el badge "Reservas: N" de la
        // pestaña, no un numero de plata): solo los tres importes pasan a la lista en firme.
        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id);

        var summary = new CustomerAccountSummaryDto
        {
            TotalSales = await firmReservasQuery.SumAsync(reserva => (decimal?)reserva.TotalSale, cancellationToken) ?? 0m,
            TotalPaid = await firmReservasQuery.SumAsync(reserva => (decimal?)reserva.TotalPaid, cancellationToken) ?? 0m,
            TotalBalance = await firmReservasQuery.SumAsync(reserva => (decimal?)reserva.Balance, cancellationToken) ?? 0m,
            ReservaCount = await reservasQuery.CountAsync(cancellationToken),
            // ADR-022 §4.9 (fix S1-bis): el contador NO debe incluir el Payment puente del saldo a favor,
            // o el badge "Pagos: N" no coincidiria con las filas visibles en GetCustomerAccountPaymentsAsync
            // (que ya lo excluye). Mismo predicado del puente.
            PaymentCount = await _dbContext.Payments
                .AsNoTracking()
                // FC4 (2026-06-14): el contador excluye AMBOS puentes (sobrepago + saldo a favor aplicado),
                // para que el badge "Pagos: N" coincida con las filas visibles en GetCustomerAccountPaymentsAsync.
                .CountAsync(payment => payment.Reserva != null && payment.Reserva.PayerId == id
                    && !((payment.Method == OverpaymentCreditCleanup.BridgeMethod
                            && !payment.AffectsCash && payment.OriginalPaymentId != null)
                        || (payment.Method == AppliedCreditBridge.BridgeMethod
                            && !payment.AffectsCash && payment.AppliedFromCreditWithdrawalId != null)), cancellationToken),
            InvoiceCount = await _dbContext.Invoices
                .AsNoTracking()
                .CountAsync(invoice => invoice.Reserva != null && invoice.Reserva.PayerId == id, cancellationToken),
            // ADR-022 Capa 8 (C2) / ADR-023 T1: la cuenta corriente por moneda sale del componente canonico
            // (mismo predicado en firme que el AR de tesoreria). El bolsillo de saldo a favor sigue local
            // (es de ClientCreditEntry, no de cuentas por cobrar).
            ReceivableByCurrency = MapToCurrencyAmounts(
                await _financePosition.GetCustomerReceivableByCurrencyAsync(id, cancellationToken)),
            CreditBalanceByCurrency = await BuildCustomerCreditByCurrencyAsync(id, cancellationToken)
        };

        return new CustomerAccountOverviewDto
        {
            Customer = customer,
            Summary = summary
        };
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
    private async Task<List<CurrencyAmountDto>> BuildCustomerCreditByCurrencyAsync(int customerId, CancellationToken cancellationToken)
    {
        var grouped = await _dbContext.ClientCreditEntries
            .AsNoTracking()
            .Where(entry => entry.CustomerId == customerId && entry.RemainingBalance > 0m)
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
        // Una sola query con los dos posibles orígenes incluidos:
        //  - Cancelación: la reserva vive en BookingCancellation.Reserva.
        //  - Sobrepago: la reserva vive en SourceReserva (FK directa del entry).
        // Traemos solo los campos que el DTO necesita (proyección) para no materializar entidades enteras
        // ni disparar N+1: el Select arma la fila con el número/PublicId de la reserva de origen en el mismo SQL.
        var entries = await _dbContext.ClientCreditEntries
            .AsNoTracking()
            .Where(entry => entry.CustomerId == id && entry.RemainingBalance > 0m)
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

        // Mismo predicado que GetCustomerReceivableByCurrencyAsync, pero trayendo la identidad de la reserva
        // para poder abrir el desglose. Join explicito contra Reservas (no nav implicita) para correr igual en
        // Postgres e InMemory. Solo saldos positivos (deuda viva del cliente).
        var rows = await (
            from row in _dbContext.ReservaMoneyByCurrency
            join reserva in _dbContext.Reservas on row.ReservaId equals reserva.Id
            where reserva.PayerId == id
                && FinancePositionService.ReceivableDebtStatuses.Contains(reserva.Status)
                && row.Balance > 0
            select new
            {
                reserva.PublicId,
                reserva.NumeroReserva,
                reserva.Name,
                row.Currency,
                row.Balance
            })
            .ToListAsync(cancellationToken);

        return BuildCustomerDebtByReserva(customer.PublicId, customer.FullName, rows
            .Select(r => (r.PublicId, r.NumeroReserva, r.Name, r.Currency, r.Balance)));
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
        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => reserva.PayerId == id);

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

        return await reservasQuery
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
    }

    public async Task<PagedResponse<CustomerAccountPaymentListItemDto>> GetCustomerAccountPaymentsAsync(int id, PagedQuery query, CancellationToken cancellationToken)
    {
        var paymentsQuery = _dbContext.Payments
            .AsNoTracking()
            .Where(payment => payment.Reserva != null && payment.Reserva.PayerId == id)
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
        var invoicesQuery = _dbContext.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.Reserva != null && invoice.Reserva.PayerId == id);

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
                ForcedByUserId = invoice.ForcedByUserId,
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
