using Hangfire.Common;
using Hangfire.Server;
using TravelApi.Application.Interfaces;

namespace TravelApi.Filters;

/// <summary>
/// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B-10 de la revisión funcional, "el worker sigue
/// escribiendo durante el restore"): frena CUALQUIER job de Hangfire mientras el sistema está en modo
/// mantenimiento. Sin esto, el proceso <c>worker</c> (que corre TODOS los jobs en background: recordatorios,
/// vencimientos, reconciliaciones, etc. — ver <c>docker-compose.yml</c>, <c>Hangfire__ServerEnabled=true</c>
/// SOLO ahí) seguiría escribiendo en la base mientras <c>pg_restore</c> la reemplaza entera, y sus conexiones
/// nuevas podrían además trabar los <c>DROP TABLE</c> del <c>--clean</c> (justo lo que
/// <c>IDatabaseRestorePort.RestoreTotalAsync</c> corta ANTES de restaurar).
///
/// <para><b>Por qué tirar una excepción en vez de "cancelar" el job</b>: Hangfire distingue "cancelado"
/// (<c>PerformingContext.Canceled = true</c>, el job queda marcado como succeeded SIN haber corrido — se
/// pierde para siempre) de "falló" (tira una excepción, Hangfire lo reintenta automáticamente según su
/// política de reintentos por default). Un job que no pudo correr por mantenimiento tiene que reintentarse
/// más tarde, no darse por "hecho" — por eso este filtro TIRA en vez de cancelar.</para>
/// </summary>
public sealed class MaintenanceModeHangfireFilter : JobFilterAttribute, IServerFilter
{
    private readonly IMaintenanceModeService _maintenanceMode;

    public MaintenanceModeHangfireFilter(IMaintenanceModeService maintenanceMode)
    {
        _maintenanceMode = maintenanceMode;
    }

    public void OnPerforming(PerformingContext filterContext)
    {
        if (_maintenanceMode.IsActive)
        {
            throw new InvalidOperationException(
                "Sistema en mantenimiento (restauración total en curso): el job se reintentará más tarde.");
        }
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        // No hace falta nada despues: si OnPerforming dejo pasar el job, no hay nada que revisar al terminar.
    }
}
