namespace TravelApi.Application.DTOs;

/// <summary>
/// Obra "PDF de presupuesto" (2026-08-11/12), mini-tanda PDF-2a: el BORRADOR que la inteligencia
/// artificial sugiere para un bloque de condiciones. Regla P-21 de la constitución ("el sistema
/// sugiere, no decide"): este texto NUNCA se guarda solo — el dueño lo revisa en el textarea y, si le
/// sirve, lo confirma con el PUT de <see cref="TravelApi.Domain.Entities.BudgetConditionBlock"/> de
/// siempre. Por eso este DTO no tiene ni <c>Kind</c> ni fecha: es un borrador de un solo uso, no una
/// entidad guardada.
/// </summary>
public record BudgetConditionDraftDto(string Text);
