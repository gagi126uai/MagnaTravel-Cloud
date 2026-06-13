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
/// Auditoria ERP 2026-06-13 (decision del dueño): resumen MENSUAL de comisiones por vendedor para la
/// pantalla "Comisiones" (solo la ve el dueño/admin). Una fila por vendedor con su total por moneda.
/// El detalle reserva-por-reserva se obtiene aparte (<c>GET /api/commissions/accruals?sellerUserId=...</c>).
/// </summary>
public class CommissionMonthlySummaryDto
{
    /// <summary>Año del periodo consultado (sobre la fecha de devengo, CreatedAt).</summary>
    public int Year { get; set; }

    /// <summary>Mes del periodo consultado (1..12).</summary>
    public int Month { get; set; }

    /// <summary>Un renglon por vendedor con comision devengada en el mes.</summary>
    public List<CommissionSellerMonthlyTotalDto> Sellers { get; set; } = new();
}

/// <summary>
/// Total de comision de UN vendedor en el mes, abierto por moneda (consistente con ADR-021: la plata no se
/// mezcla entre monedas). Los montos son dato sensible (tipo costo): el endpoint es admin-only.
/// </summary>
public class CommissionSellerMonthlyTotalDto
{
    public string SellerUserId { get; set; } = string.Empty;
    public string? SellerName { get; set; }

    /// <summary>Total de comision del mes por moneda (ej. ARS y USD). Una entrada por moneda con devengo.</summary>
    public List<CommissionCurrencyTotalDto> TotalsByCurrency { get; set; } = new();
}

/// <summary>Subtotal de comision en una moneda (suma de los <c>Amount</c> de los devengos de esa moneda).</summary>
public class CommissionCurrencyTotalDto
{
    public string Currency { get; set; } = string.Empty;
    public decimal Amount { get; set; }
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
