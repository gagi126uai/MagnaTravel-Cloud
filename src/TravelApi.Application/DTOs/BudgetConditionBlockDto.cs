namespace TravelApi.Application.DTOs;

/// <summary>
/// Obra "PDF de presupuesto" (2026-08-11/12), TANDA 1: un bloque de condiciones por categoría. Kind
/// viaja como TEXTO legible ("Aereos", "Hoteles", ...) — ver
/// <see cref="TravelApi.Domain.Entities.BudgetConditionBlockKindText"/> — nunca el número crudo del
/// enum interno (gate de exposición de datos).
/// </summary>
public record BudgetConditionBlockDto(string Kind, string? Text);
