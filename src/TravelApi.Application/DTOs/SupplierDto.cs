using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class SupplierDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// ADR-044 T3b Decision 3 (2026-07-10): excepcion opcional de "quién asume el ajuste por el dólar" en las
    /// multas de ESTE operador. Null = hereda el default de la agencia. Ver el mismo campo en
    /// <c>SuppliersController.ToSupplierResponse</c> (contrato real hoy en produccion: este DTO con nombre
    /// esta mapeado via AutoMapper pero ningun endpoint lo devuelve todavia — se mantiene en sync igual, por
    /// si un futuro endpoint empieza a usarlo).
    /// </summary>
    public TreasuryFxAssumedBy? TreasuryFxAssumedByOverride { get; set; }
}
