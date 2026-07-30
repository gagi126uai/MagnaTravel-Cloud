namespace TravelApi.Application.Interfaces;

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño): un flag EN MEMORIA (con respaldo en disco para
/// sobrevivir un reinicio del proceso a mitad de una restauración, y para que OTRO proceso .NET que comparte
/// el mismo archivo —el <c>worker</c> de Hangfire— se entere) que indica si el sistema está en modo
/// mantenimiento. Mientras está activo, un middleware (<c>TravelApi.Middleware.MaintenanceModeMiddleware</c>)
/// responde 503 a casi todos los pedidos bajo <c>/api/**</c> y un filtro de Hangfire
/// (<c>TravelApi.Filters.MaintenanceModeHangfireFilter</c>) frena los jobs en curso — el sistema queda "fuera
/// de servicio" sin que nadie (ni un pedido HTTP, ni un job en segundo plano) pueda tocar datos a medio
/// restaurar.
///
/// <para><b>Por qué en memoria y no en la base de datos</b>: durante una restauración TOTAL la base de datos
/// JUSTAMENTE está siendo reemplazada — no se puede confiar en ella para saber "¿estamos en mantenimiento?"
/// en ese preciso momento. El archivo en disco (ver <c>FileMaintenanceModeService</c>) es la red de seguridad
/// para dos escenarios: (1) el proceso se reinicia a mitad de camino — sin el archivo, el flag en memoria se
/// perdería y el sistema volvería a aceptar pedidos contra una base a medio escribir; (2) el proceso
/// <c>worker</c> (que corre los jobs de Hangfire) es un proceso .NET DISTINTO del proceso <c>api</c> — sin un
/// archivo compartido, el worker jamás se enteraría de que la API activó el mantenimiento.</para>
///
/// <para><b>Auto-expiración (hallazgo B-11a, revisión funcional 2026-07-28)</b>: si el mantenimiento sigue
/// activo pasado <c>Maintenance:MaxDurationMinutes</c> (default 30), la implementación se AUTO-desactiva con
/// un log crítico. Sin esto, un proceso que muere a mitad de una restauración (entre activar y desactivar)
/// deja el sistema tapiado PARA SIEMPRE — ni siquiera el login funciona mientras el mantenimiento está activo,
/// así que nadie podría entrar a arreglarlo. Ver el comentario completo en <c>FileMaintenanceModeService</c>.</para>
///
/// <para><b>Excepción a la auto-expiración (hallazgo B-N2(a), 2026-07-28)</b>: si el desenlace de la
/// restauración queda INCIERTO (ver <c>SuppressAutoExpiry</c>), la auto-expiración NO aplica — sería
/// exactamente el mismo error que el hallazgo B1 vino a corregir (reabrir el sistema sin saber con certeza si
/// es seguro). Un estado incierto solo se resuelve con intervención humana (ver el runbook en
/// <c>docs/db-operations.md</c>), nunca solo.</para>
/// </summary>
public interface IMaintenanceModeService
{
    /// <summary>true si el sistema está actualmente en modo mantenimiento.</summary>
    bool IsActive { get; }

    /// <summary>Motivo en criollo de por qué está en mantenimiento (null si no está activo).</summary>
    string? Reason { get; }

    /// <summary>Momento (UTC) en que se activó el modo mantenimiento actual (null si no está activo).</summary>
    DateTime? SinceUtc { get; }

    /// <summary>
    /// Rediseño de la pantalla de resguardos (2026-07-30, firmado, §7 punto 2): código del paso en curso de la
    /// restauración total (uno de <c>RestoreProgressSteps</c>), o <c>null</c> si no hay ninguno publicado.
    ///
    /// <para><b>Por qué viaja por acá y no por un canal nuevo</b>: este servicio YA es el único lugar que sabe
    /// que hay una restauración corriendo, YA sobrevive a un reinicio del proceso y YA está expuesto por el
    /// único endpoint que el mantenimiento deja pasar (<c>GET /api/system/status</c>, ver
    /// <c>MaintenanceModeMiddleware</c>). Un canal aparte tendría que resolver esos tres problemas de nuevo.</para>
    /// </summary>
    string? CurrentStep { get; }

    /// <summary>
    /// Publica el paso en curso (ver <see cref="CurrentStep"/>). No hace nada si el mantenimiento no está
    /// activo: un paso sin restauración en curso no significa nada y solo podría confundir a la pantalla.
    /// </summary>
    void SetStep(string step);

    /// <summary>
    /// Obra "Restaurar TOTAL" hardening (2026-07-28, hallazgo B4 de seguridad, "dos restauraciones a la vez se
    /// pisan"): intenta activar el modo mantenimiento de forma ATÓMICA. Si YA estaba activo (otra restauración
    /// en curso, sea del mismo o de otro pedido concurrente), NO hace nada y devuelve <c>false</c> — el caller
    /// tiene que rechazar con "Ya hay una restauración en curso.", nunca pisar el motivo/fecha de una
    /// restauración que ya está en marcha. Devuelve <c>true</c> solo si ESTA llamada fue la que lo activó.
    /// </summary>
    bool TryActivate(string reason);

    /// <summary>
    /// Hallazgo B-N2(c) de seguridad (2026-07-28, "los timeouts no cierran entre sí"): renueva
    /// <see cref="SinceUtc"/> a AHORA, sin tocar <see cref="Reason"/> ni desactivar nada. Se llama justo antes
    /// de arrancar el <c>pg_restore</c> real — así el presupuesto de <c>Maintenance:MaxDurationMinutes</c> mide
    /// el tiempo DESDE ESE PUNTO (acotado por el timeout propio del <c>pg_restore</c>), no desde el arranque de
    /// toda la operación (que además incluye el chequeo de esquema, el candado fiscal y el backup previo — cada
    /// uno con su PROPIO timeout, pero que sin este "touch" se sumarían al presupuesto de auto-expiración de
    /// forma innecesaria y frágil). No hace nada si el mantenimiento no está activo.
    /// </summary>
    void Touch();

    /// <summary>
    /// Hallazgo B-N2(a) de seguridad (2026-07-28): marca la sesión de mantenimiento ACTUAL como "desenlace
    /// incierto, requiere intervención humana" — desde este punto, la auto-expiración de
    /// <see cref="FileMaintenanceModeService"/> deja de aplicar para esta sesión (nunca se auto-desactiva
    /// sola). Se llama cuando no hay certeza de que sea seguro reabrir el sistema (el <c>pg_restore</c> excedió
    /// su timeout y tuvo que matarse, o la base ya se reemplazó pero no se pudo confirmar que AFIP quedó en
    /// homologación). La única salida es manual (ver el runbook en <c>docs/db-operations.md</c>).
    /// </summary>
    void SuppressAutoExpiry(string reason);

    /// <summary>Desactiva el modo mantenimiento y persiste el estado en disco.</summary>
    void Deactivate();
}
