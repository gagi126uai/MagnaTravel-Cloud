using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Time;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Las dos listas de deudores de la pantalla Cobranzas (spec firmada 2026-08-06, §4.2 y §4.3):
/// "Viajan pronto y deben" (una fila por RESERVA, ordenada por fecha de salida) y "Deuda por cliente"
/// (una fila por CLIENTE, cruzando todas sus reservas).
///
/// <para><b>Regla de plata (P-3, no negociable)</b>: los pesos y los dolares NUNCA se suman. Cada fila y
/// cada total viajan como una LISTA de (moneda, monto). El escalar <c>Reserva.Balance</c> se usa solo
/// como semaforo ("¿debe si o no?"); el monto real por moneda sale de la tabla materializada
/// <c>ReservaMoneyByCurrency</c>, la misma que usa el resto del sistema.</para>
///
/// <para><b>Que reservas entran</b>: solo las de VENTA FIRME con saldo (En gestión, Confirmada,
/// Finalizada = <c>CollectableDebtStatuses</c>). Quedan afuera, por construccion, las anuladas, las
/// perdidas, los presupuestos y las que estan esperando el reembolso del operador: esa plata se resuelve
/// por el circuito de cancelacion, no cobrandole al cliente.</para>
///
/// <para><b>Quien ve que</b>: mismo alcance que el resto de Cobranzas — con <c>cobranzas.view_all</c> (o
/// Admin) se ven todas; sin ese permiso, el vendedor ve solo las reservas a su cargo.</para>
/// </summary>
public partial class PaymentService
{
    public async Task<DebtorsByDepartureResponse> GetDebtorsByDepartureAsync(
        DebtorsQuery query, CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var dueDays = Math.Max(settings.FullPaymentDueDaysBeforeDeparture, 1);
        var today = AgencyTimezone.TodayWallClockUtc();

        var reservations = await LoadReservationsWithDebtAsync(query.Search, cancellationToken);
        var moneyByReservaId = await LoadPendingByCurrencyAsync(
            reservations.Select(reserva => reserva.Id).ToList(), cancellationToken);

        var items = new List<DebtorByDepartureDto>();
        foreach (var reserva in reservations)
        {
            var money = moneyByReservaId.TryGetValue(reserva.Id, out var found) ? found : new List<MoneyLine>();
            var pending = BuildPendingLines(money, reserva.Balance, reserva.Id);
            var total = BuildConfirmedLines(money);
            var dueDate = ResolvePaymentDueDate(reserva.StartDate, dueDays);
            var isPastDue = dueDate.HasValue && dueDate.Value.Date < today.Date;

            items.Add(new DebtorByDepartureDto
            {
                ReservaPublicId = reserva.PublicId,
                NumeroReserva = reserva.NumeroReserva,
                ReservaName = reserva.Name,
                CustomerName = reserva.CustomerName ?? "Consumidor Final",
                CustomerPublicId = reserva.CustomerPublicId,
                ResponsibleUserName = reserva.ResponsibleUserName,
                DepartureDate = reserva.StartDate,
                DepartureCountdownText = RelativeDateText.Countdown(today, reserva.StartDate),
                DaysUntilDeparture = reserva.StartDate.HasValue
                    ? RelativeDateText.DaysBetween(today, reserva.StartDate.Value)
                    : null,
                Total = total,
                Pending = pending,
                PaymentDueDate = dueDate,
                IsPastDue = isPastDue,
                DaysPastDue = isPastDue ? RelativeDateText.DaysBetween(dueDate!.Value, today) : null,
                PastDueText = isPastDue
                    ? $"El saldo tenía que estar completo el {dueDate!.Value:dd/MM/yyyy}."
                    : null
            });
        }

        // El que sale primero, arriba. Las reservas sin fecha de salida van al final: no se puede decir
        // "viaja pronto" de algo que todavia no tiene fecha, pero tampoco se esconde su deuda.
        var ordered = items
            .OrderBy(item => item.DepartureDate.HasValue ? 0 : 1)
            .ThenBy(item => item.DepartureDate ?? DateTime.MaxValue)
            .ThenBy(item => item.NumeroReserva, StringComparer.Ordinal)
            .ToList();

        return new DebtorsByDepartureResponse
        {
            Items = ordered,
            TotalsPending = SumByCurrency(ordered.SelectMany(item => item.Pending)),
            PaymentDueDaysBeforeDeparture = dueDays
        };
    }

    public async Task<CustomerDebtsResponse> GetCustomerDebtsAsync(
        DebtorsQuery query, CancellationToken cancellationToken)
    {
        // Se apoya en la MISMA lista de reservas con deuda que "Viajan pronto y deben": si las dos
        // pantallas leyeran fuentes distintas, tarde o temprano mostrarian numeros distintos.
        var byDeparture = await GetDebtorsByDepartureAsync(query, cancellationToken);

        var customers = new List<CustomerDebtSummaryDto>();
        // Agrupamos por cliente. La clave es el PublicId; las reservas sin cliente cargado caen todas
        // juntas en un grupo "Consumidor Final" (su plata existe: no se puede esconder de la lista).
        foreach (var group in byDeparture.Items.GroupBy(item => item.CustomerPublicId))
        {
            var reservationsOfCustomer = group.ToList();
            var firstDeparture = reservationsOfCustomer
                .Where(item => item.DepartureDate.HasValue)
                .Select(item => item.DepartureDate!.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Min();

            customers.Add(new CustomerDebtSummaryDto
            {
                CustomerPublicId = group.Key,
                CustomerName = reservationsOfCustomer[0].CustomerName,
                ReservationsWithDebt = reservationsOfCustomer.Count,
                Debt = SumByCurrency(reservationsOfCustomer.SelectMany(item => item.Pending)),
                FirstDeparture = firstDeparture == DateTime.MinValue ? null : firstDeparture,
                FirstDepartureCountdownText = firstDeparture == DateTime.MinValue
                    ? string.Empty
                    : RelativeDateText.Countdown(AgencyTimezone.TodayWallClockUtc(), firstDeparture),
                HasPastDue = reservationsOfCustomer.Any(item => item.IsPastDue)
            });
        }

        // Detalle abierto D2 de la spec: con dos monedas no existe un "de mayor a menor" sin sumarlas, y
        // sumarlas esta prohibido (P-3). Mientras el dueño no decida otra cosa, se ordena por la primera
        // salida, el mismo criterio que firmo para la otra lista.
        var ordered = customers
            .OrderBy(customer => customer.FirstDeparture.HasValue ? 0 : 1)
            .ThenBy(customer => customer.FirstDeparture ?? DateTime.MaxValue)
            .ThenBy(customer => customer.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new CustomerDebtsResponse
        {
            Items = ordered,
            TotalsDebt = SumByCurrency(ordered.SelectMany(customer => customer.Debt))
        };
    }

    // ============================================================
    // Piezas internas
    // ============================================================

    /// <summary>Fecha limite de pago = salida - N dias. Sin fecha de salida no hay fecha limite.</summary>
    private static DateTime? ResolvePaymentDueDate(DateTime? departureDate, int dueDays)
        => departureDate?.Date.AddDays(-dueDays);

    /// <summary>
    /// Reservas de venta firme con saldo pendiente, con el alcance del caller ya aplicado. Proyeccion
    /// chica: nunca se traen las entidades enteras ni sus colecciones.
    /// </summary>
    private async Task<List<DebtorReservationRow>> LoadReservationsWithDebtAsync(
        string? search, CancellationToken cancellationToken)
    {
        var reservationsQuery = _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva => CollectableDebtStatuses.Contains(reserva.Status) && reserva.Balance > 0);

        var ownerScope = await GetOwnerScopeOrNullAsync(cancellationToken);
        if (ownerScope is not null)
        {
            reservationsQuery = reservationsQuery.Where(reserva => reserva.ResponsibleUserId == ownerScope);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = search.Trim().ToLowerInvariant();
            reservationsQuery = reservationsQuery.Where(reserva =>
                reserva.NumeroReserva.ToLower().Contains(normalized) ||
                (reserva.Payer != null && reserva.Payer.FullName.ToLower().Contains(normalized)));
        }

        return await reservationsQuery
            .Select(reserva => new DebtorReservationRow
            {
                Id = reserva.Id,
                PublicId = reserva.PublicId,
                NumeroReserva = reserva.NumeroReserva,
                Name = reserva.Name,
                StartDate = reserva.StartDate,
                Balance = reserva.Balance,
                ResponsibleUserName = reserva.ResponsibleUserName,
                CustomerName = reserva.Payer != null ? reserva.Payer.FullName : null,
                CustomerPublicId = reserva.Payer != null ? (Guid?)reserva.Payer.PublicId : null
            })
            .ToListAsync(cancellationToken);
    }

    /// <summary>Detalle de plata por moneda de esas reservas, en UNA sola consulta (sin N+1).</summary>
    private async Task<Dictionary<int, List<MoneyLine>>> LoadPendingByCurrencyAsync(
        List<int> reservaIds, CancellationToken cancellationToken)
    {
        if (reservaIds.Count == 0) return new Dictionary<int, List<MoneyLine>>();

        var rows = await _dbContext.ReservaMoneyByCurrency
            .AsNoTracking()
            .Where(row => reservaIds.Contains(row.ReservaId))
            .Select(row => new MoneyLine
            {
                ReservaId = row.ReservaId,
                Currency = row.Currency,
                ConfirmedSale = row.ConfirmedSale,
                Balance = row.Balance
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ReservaId)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    /// <summary>
    /// Lo que falta cobrar, una linea por moneda. Solo entran las monedas con saldo POSITIVO: un saldo a
    /// favor en dolares no tapa una deuda en pesos (decision multimoneda ADR-021 §2.4).
    ///
    /// <para><b>Dos casos que NO son lo mismo</b> (y antes se trataban igual):</para>
    /// <list type="bullet">
    ///   <item><b>Sin ninguna fila por moneda</b> = reserva vieja que nunca se recalculo desde ADR-021. Ahi
    ///   SI se usa el saldo escalar con la moneda por defecto, la misma convencion legacy del resto de
    ///   Cobranzas: es la unica forma de no esconder esa plata.</item>
    ///   <item><b>Con filas, pero ninguna positiva</b> = el detalle por moneda dice que NO debe nada,
    ///   aunque el semaforo escalar diga que si. Eso es una incoherencia entre las dos fuentes, y la
    ///   respuesta correcta es NO inventar una deuda en pesos: se devuelve vacio y se deja rastro en el log
    ///   (con el id de la reserva, ningun dato de persona) para que el vigia de coherencia lo levante.</item>
    /// </list>
    /// </summary>
    private List<CurrencyAmountDto> BuildPendingLines(List<MoneyLine> money, decimal scalarBalance, int reservaId)
    {
        var lines = money
            .Where(line => line.Balance > 0m)
            .Select(line => new CurrencyAmountDto
            {
                Currency = line.Currency,
                Amount = ReservationEconomicPolicy.RoundCurrency(line.Balance)
            })
            .OrderBy(line => line.Currency, StringComparer.Ordinal)
            .ToList();

        if (lines.Count > 0) return lines;

        if (money.Count > 0)
        {
            _logger.LogWarning(
                "Deudores: la reserva {ReservaId} tiene saldo escalar {Balance} pero ninguna moneda con saldo " +
                "positivo en el detalle. No se inventa una deuda: la fila viaja sin monto.",
                reservaId, scalarBalance);
            return new List<CurrencyAmountDto>();
        }

        return new List<CurrencyAmountDto>
        {
            new() { Currency = Monedas.ARS, Amount = ReservationEconomicPolicy.RoundCurrency(scalarBalance) }
        };
    }

    /// <summary>La venta confirmada de la reserva, una linea por moneda (columna TOTAL de la lista).</summary>
    private static List<CurrencyAmountDto> BuildConfirmedLines(List<MoneyLine> money)
        => money
            .Where(line => line.ConfirmedSale != 0m)
            .Select(line => new CurrencyAmountDto
            {
                Currency = line.Currency,
                Amount = ReservationEconomicPolicy.RoundCurrency(line.ConfirmedSale)
            })
            .OrderBy(line => line.Currency, StringComparer.Ordinal)
            .ToList();

    /// <summary>Suma montos AGRUPANDO POR MONEDA. Es el unico "sumar" permitido (P-3).</summary>
    private static List<CurrencyAmountDto> SumByCurrency(IEnumerable<CurrencyAmountDto> lines)
        => lines
            .GroupBy(line => string.IsNullOrWhiteSpace(line.Currency) ? Monedas.ARS : line.Currency,
                     StringComparer.Ordinal)
            .Select(group => new CurrencyAmountDto
            {
                Currency = group.Key,
                Amount = ReservationEconomicPolicy.RoundCurrency(group.Sum(line => line.Amount))
            })
            .OrderBy(line => line.Currency, StringComparer.Ordinal)
            .ToList();

    /// <summary>Proyeccion interna de una reserva con deuda (no sale al cliente).</summary>
    private sealed class DebtorReservationRow
    {
        public int Id { get; set; }
        public Guid PublicId { get; set; }
        public string NumeroReserva { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public decimal Balance { get; set; }
        public string? ResponsibleUserName { get; set; }
        public string? CustomerName { get; set; }
        public Guid? CustomerPublicId { get; set; }
    }

    /// <summary>Una linea de plata de la tabla materializada (no sale al cliente).</summary>
    private sealed class MoneyLine
    {
        public int ReservaId { get; set; }
        public string Currency { get; set; } = Monedas.ARS;
        public decimal ConfirmedSale { get; set; }
        public decimal Balance { get; set; }
    }
}
