namespace TravelApi.Application.Interfaces;

/// <summary>
/// Los 5 tipos de servicio de una reserva que tienen boton de "resolucion" (Marcar confirmado / emitido /
/// no requiere confirmacion). Se usa para saber en que tabla buscar el servicio y para armar la clave de
/// deduplicacion del aviso de error (ver <see cref="TravelApi.Domain.Entities.NotificationResolutionKeys.ForServiceResolutionError"/>).
/// </summary>
public enum ServiceResolutionKind
{
    FlightSegment,
    HotelBooking,
    TransferBooking,
    PackageBooking,
    AssistanceBooking,
}

/// <summary>
/// Decision firmada 2026-08-18 (Gaston): cuando un vendedor intenta "Marcar confirmado / emitido / No
/// requiere confirmacion" sobre un servicio y el intento FALLA (el operador o una regla de negocio lo
/// rechaza), ese error TAMBIEN queda en la campanita de notificaciones — ademas del mensaje que ya se ve
/// en la fila del servicio. Es la contraparte de negocio de <c>INotificationService</c>: sabe COMO se
/// arma el aviso de "no se pudo confirmar" (mensaje, clave de deduplicacion, a quien avisar); el "como se
/// guarda y se manda" sigue siendo responsabilidad de <c>INotificationService</c>.
///
/// <para><b>Regla dura</b>: SOLO los errores generan un aviso nuevo. Los exitos NUNCA notifican (serian
/// ruido — el vendedor ya ve el cambio reflejado en la pantalla); lo unico que hace un exito es apagar un
/// aviso de error anterior sobre ese MISMO servicio, si quedo alguno vivo.</para>
///
/// <para>Lo consume <c>NotificarFalloDeResolucionAlUsuarioAttribute</c> (action filter en el proyecto
/// TravelApi), que mira el resultado de los 7 endpoints de resolucion de servicio y llama a esta interfaz
/// en vez de mezclar logica de notificacion adentro de cada controller.</para>
/// </summary>
public interface IServiceResolutionFailureNotifier
{
    /// <summary>
    /// El intento de resolver el servicio fallo. Crea un aviso de Error para cada admin (si no tenia ya
    /// uno vivo sobre este mismo servicio). Nunca tira excepcion: si el aviso no se pudo crear (DB caida,
    /// servicio no encontrado, etc.) queda solo logueado — el endpoint que disparo esto ya respondio y esa
    /// respuesta no debe romperse por esto.
    /// </summary>
    /// <param name="kind">Tipo de servicio (en que tabla buscarlo).</param>
    /// <param name="servicePublicIdOrLegacyId">Id del servicio tal cual llego en la ruta del endpoint (GUID publico o id legacy numerico).</param>
    /// <param name="businessErrorMessage">Mensaje ya redactado para el usuario (el mismo que ve en la fila), sin GUIDs ni jerga tecnica.</param>
    Task NotifyFailureAsync(
        ServiceResolutionKind kind,
        string servicePublicIdOrLegacyId,
        string businessErrorMessage,
        CancellationToken ct);

    /// <summary>
    /// El servicio se resolvio con exito. Si habia un aviso de error VIVO sobre este mismo servicio, se
    /// apaga solo (la causa murio) — no crea nada nuevo. Igual que <see cref="NotifyFailureAsync"/>, nunca
    /// tira excepcion.
    /// </summary>
    Task NotifyResolvedAsync(
        ServiceResolutionKind kind,
        string servicePublicIdOrLegacyId,
        CancellationToken ct);
}
