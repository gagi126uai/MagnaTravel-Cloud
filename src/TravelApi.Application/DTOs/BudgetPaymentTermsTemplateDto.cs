namespace TravelApi.Application.DTOs;

/// <summary>
/// Fix bloqueante (2026-08-13): SOLO el texto de la plantilla de "Formas de pago" de Configuración
/// (<see cref="TravelApi.Domain.Entities.AgencySettings.BudgetPaymentTermsTemplate"/>), para que la
/// ficha de reserva la pueda precargar sin necesitar el permiso de Admin que exige
/// <c>GET /api/reports/settings</c> (que además devuelve la entidad entera, con campos internos que acá
/// no corresponde exponer). <c>Text</c> puede ser <c>null</c> si la agencia todavía no cargó ninguna.
/// </summary>
public record BudgetPaymentTermsTemplateDto(string? Text);
