namespace TravelApi.Application.DTOs;

public class ReservaListDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Budget";
    public string? CustomerName { get; set; }
    public decimal TotalSale { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public decimal TotalCost { get; set; }
    public decimal TotalPaid { get; set; }
    public int PassengerCount { get; set; }
    public string? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public bool IsEconomicallySettled { get; set; }
    public bool CanMoveToOperativo { get; set; }
    public bool CanEmitVoucher { get; set; }
    public bool CanEmitAfipInvoice { get; set; }
    public string? EconomicBlockReason { get; set; }
    public bool IsInProgress { get; set; }
    /// <summary>True si el cliente no debe nada (Balance == 0). Chip verde "Pagada".</summary>
    public bool IsFullyPaid { get; set; }
    /// <summary>True si el viaje termino y todavia hay deuda (EndDate &lt; hoy AND Balance &gt; 0). Chip rojo "Vencida con deuda".</summary>
    public bool HasOverdueDebt { get; set; }

    /// <summary>
    /// ADR-021 Capa 5: detalle de plata por moneda para la fila del listado. Se llena leyendo la tabla
    /// hija materializada (no recalcula on-read fila por fila). Una sola linea = reserva mono-moneda.
    /// El <c>TotalCost</c> de cada linea se enmascara igual que el escalar para usuarios sin ver-costos.
    /// </summary>
    public List<ReservaMoneyLineDto> PorMoneda { get; set; } = new();

    /// <summary>ADR-021: true si la reserva mueve mas de una moneda.</summary>
    public bool EsMultimoneda { get; set; }
}
