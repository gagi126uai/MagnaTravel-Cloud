namespace TravelApi.Application.DTOs;

/// <summary>
/// Obra "PDF ronda 2" (2026-08-14, spec §6): UNA fila del plan de pagos del TOTAL del presupuesto, ya
/// ordenada por <see cref="Position"/>. Ver <c>TravelApi.Domain.Entities.BudgetPaymentPlanInstallment</c>.
/// </summary>
public class BudgetPaymentPlanInstallmentDto
{
    public int Position { get; set; }
    public string DueText { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ARS";
}
