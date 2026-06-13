namespace TravelApi.Application.DTOs;

/// <summary>
/// Auditoria ERP 2026-06-12 (hallazgo #1): filtros del listado de comisiones devengadas (pantalla de
/// liquidacion al vendedor). Todos opcionales: sin filtros lista todas las comisiones devengadas.
/// </summary>
public class CommissionAccrualsQuery : PagedQuery
{
    /// <summary>Filtra por vendedor (Id del usuario responsable). Null = todos.</summary>
    public string? SellerUserId { get; set; }

    /// <summary>Filtra por estado: "Devengada" / "Liquidada". Null = todos.</summary>
    public string? Status { get; set; }

    /// <summary>
    /// Filtra por periodo de devengo (sobre CreatedAt). "From" inclusive. Null = sin limite inferior.
    /// </summary>
    public DateTime? From { get; set; }

    /// <summary>Filtra por periodo de devengo (sobre CreatedAt). "To" inclusive. Null = sin limite superior.</summary>
    public DateTime? To { get; set; }

    public CommissionAccrualsQuery()
    {
        SortBy = "createdAt";
        SortDir = "desc";
    }
}

/// <summary>
/// Fila del listado de comisiones devengadas. El <c>Amount</c> es dato sensible (tipo costo): el endpoint
/// que lo expone se gatea con <c>cobranzas.see_cost</c>.
/// </summary>
public class CommissionAccrualDto
{
    public Guid PublicId { get; set; }
    public string SellerUserId { get; set; } = string.Empty;
    public string? SellerName { get; set; }
    public Guid ReservaPublicId { get; set; }
    public string ReservaNumber { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal RatePercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
