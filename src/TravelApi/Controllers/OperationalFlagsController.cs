using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

/// <summary>
/// Endpoint liviano de SOLO LECTURA para que cualquier usuario autenticado (vendedores
/// incluidos) sepa que features de UI estan prendidas, sin necesitar permisos especiales.
///
/// <para><b>Por que es un controller aparte y no una accion mas de
/// <c>OperationalFinanceSettingsController</c></b>: ese controller es Admin-only en todas sus
/// acciones (lee/escribe el settings completo, con umbrales de negocio). Este endpoint tiene
/// otro nivel de autorizacion ([Authorize] plano) y otro contrato (5 booleanos y nada mas);
/// separarlo en su propio archivo hace imposible confundir las dos fronteras de permiso.</para>
///
/// <para><b>Seguridad</b>: exponer estos 5 booleanos a cualquier autenticado NO filtra datos
/// sensibles — son toggles de comportamiento de UI (que pestanas/modales/buscadores se montan).
/// El gating real de DATOS sigue siendo server-side en cada endpoint de negocio (permisos +
/// ownership), ya verificado por security review en las fases de cada flag. Aca no viaja CUIT,
/// punto de venta, condicion fiscal, dias ni montos.</para>
/// </summary>
[ApiController]
[Route("api/settings/operational-flags")]
[Authorize]
public class OperationalFlagsController : ControllerBase
{
    private readonly IOperationalFinanceSettingsService _settingsService;

    public OperationalFlagsController(IOperationalFinanceSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Devuelve los feature flags de comportamiento que el frontend usa para decidir que UI
    /// montar. GetEntityAsync crea la fila de settings con defaults (todo OFF) si no existe,
    /// asi que este endpoint nunca da 404.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<OperationalFlagsResponse>> Get(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetEntityAsync(cancellationToken);

        return Ok(new OperationalFlagsResponse
        {
            EnableSoldToSettleStates = settings.EnableSoldToSettleStates,
            EnableMultiCurrencyInvoicing = settings.EnableMultiCurrencyInvoicing,
            EnableCancellationDebitNote = settings.EnableCancellationDebitNote,
            EnableCatalogFindOrCreate = settings.EnableCatalogFindOrCreate,
            EnableServiceDeadlineAlerts = settings.EnableServiceDeadlineAlerts
        });
    }
}
