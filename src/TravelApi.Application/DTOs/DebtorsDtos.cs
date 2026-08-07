namespace TravelApi.Application.DTOs;

/// <summary>Filtro comun de las dos listas de deudores: un buscador y nada mas (spec 2026-08-06 §4.2/§4.3).</summary>
public class DebtorsQuery
{
    /// <summary>Busca por numero de reserva o por nombre del cliente. Vacio = todos.</summary>
    public string? Search { get; set; }
}

/// <summary>
/// Una reserva que DEBE, vista desde "Viajan pronto y deben" (spec firmada 2026-08-06, §4.2 / M-6).
///
/// <para>Todo lo derivado viene calculado del motor (T-13): la cuenta regresiva, la fecha limite de pago
/// y el veredicto de vencido. La pantalla no resta fechas ni suma monedas (P-3).</para>
/// </summary>
public class DebtorByDepartureDto
{
    public Guid ReservaPublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;

    /// <summary>Nombre de la reserva (lo que se muestra como destino: "Cancún").</summary>
    public string ReservaName { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;
    public Guid? CustomerPublicId { get; set; }

    /// <summary>Vendedor a cargo. Sirve para que el admin sepa a quien reclamarle.</summary>
    public string? ResponsibleUserName { get; set; }

    public DateTime? DepartureDate { get; set; }

    /// <summary>"en 3 días", "hoy", "en 4 meses". Vacio si la reserva todavia no tiene fecha de salida.</summary>
    public string DepartureCountdownText { get; set; } = string.Empty;

    /// <summary>Dias que faltan para la salida (negativo si ya salio). Null si no hay fecha.</summary>
    public int? DaysUntilDeparture { get; set; }

    /// <summary>Venta confirmada, separada por moneda. NUNCA sumada (P-3).</summary>
    public IReadOnlyList<CurrencyAmountDto> Total { get; set; } = Array.Empty<CurrencyAmountDto>();

    /// <summary>Lo que falta cobrar, separado por moneda. NUNCA sumado (P-3).</summary>
    public IReadOnlyList<CurrencyAmountDto> Pending { get; set; } = Array.Empty<CurrencyAmountDto>();

    /// <summary>Fecha en la que el saldo tenia que estar completo (salida - N dias). Null si no hay salida.</summary>
    public DateTime? PaymentDueDate { get; set; }

    /// <summary>True cuando esa fecha ya paso y la reserva sigue debiendo. No traba nada (P16=A).</summary>
    public bool IsPastDue { get; set; }

    /// <summary>Dias de atraso. Null cuando no esta vencida.</summary>
    public int? DaysPastDue { get; set; }

    /// <summary>Frase lista para la fila roja: "El saldo tenía que estar completo el 22/07/2026." Null si no vencio.</summary>
    public string? PastDueText { get; set; }
}

/// <summary>Respuesta de "Viajan pronto y deben": la lista y sus totales por moneda.</summary>
public class DebtorsByDepartureResponse
{
    public IReadOnlyList<DebtorByDepartureDto> Items { get; set; } = Array.Empty<DebtorByDepartureDto>();

    /// <summary>Total que falta cobrar en TODA la lista, una linea por moneda (P-3).</summary>
    public IReadOnlyList<CurrencyAmountDto> TotalsPending { get; set; } = Array.Empty<CurrencyAmountDto>();

    /// <summary>Los dias configurados ("el saldo tiene que estar completo N días antes de la salida").</summary>
    public int PaymentDueDaysBeforeDeparture { get; set; }
}

/// <summary>
/// Un cliente que debe, visto desde "Deuda por cliente" (spec firmada 2026-08-06, §4.3 / M-7).
/// Cruza TODAS sus reservas vivas con saldo.
/// </summary>
public class CustomerDebtSummaryDto
{
    /// <summary>Null cuando las reservas que deben no tienen cliente cargado (consumidor final).</summary>
    public Guid? CustomerPublicId { get; set; }

    public string CustomerName { get; set; } = string.Empty;

    /// <summary>Cuantas reservas suyas tienen saldo pendiente.</summary>
    public int ReservationsWithDebt { get; set; }

    /// <summary>Lo que debe, separado por moneda. NUNCA sumado (P-3).</summary>
    public IReadOnlyList<CurrencyAmountDto> Debt { get; set; } = Array.Empty<CurrencyAmountDto>();

    /// <summary>La salida mas proxima entre sus reservas con deuda. Null si ninguna tiene fecha.</summary>
    public DateTime? FirstDeparture { get; set; }

    /// <summary>"en 3 días" para esa primera salida. Vacio si no hay fecha.</summary>
    public string FirstDepartureCountdownText { get; set; } = string.Empty;

    /// <summary>True si al menos una de sus reservas ya paso la fecha en que el saldo tenia que estar completo.</summary>
    public bool HasPastDue { get; set; }
}

/// <summary>Respuesta de "Deuda por cliente": la lista y el total que te deben, por moneda.</summary>
public class CustomerDebtsResponse
{
    public IReadOnlyList<CustomerDebtSummaryDto> Items { get; set; } = Array.Empty<CustomerDebtSummaryDto>();

    /// <summary>Total adeudado por TODOS los clientes de la lista, una linea por moneda (P-3).</summary>
    public IReadOnlyList<CurrencyAmountDto> TotalsDebt { get; set; } = Array.Empty<CurrencyAmountDto>();
}
