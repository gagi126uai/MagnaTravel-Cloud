namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Rechazo de negocio cuando una CANTIDAD de un servicio (habitaciones, pasajeros) viene por debajo
/// del minimo real (hallazgo de la prueba con navegador en PROD del 2026-08-11: se guardo un hotel
/// con Habitaciones = -1).
///
/// <para><b>Por que una excepcion propia y no una <see cref="System.ArgumentException"/> pelada</b>
/// (T-1 / T-2): el controller no puede saber, mirando una ArgumentException cualquiera, si el texto
/// que trae adentro esta escrito para una PERSONA o es un mensaje tecnico de .NET (que ademas suele
/// traer el sufijo "(Parameter 'x')"). Con un tipo propio, el <c>Message</c> es SIEMPRE texto en
/// criollo listo para mostrar. Mismo criterio que <see cref="RateValidationException"/> y
/// <see cref="PaymentValidationException"/>.</para>
///
/// <para><b>Por que hereda de <see cref="System.ArgumentException"/></b>: los 5 controllers de
/// servicios (hotel/aereo/traslado/paquete/asistencia) ya atrapan <c>ArgumentException</c> y
/// responden 400 con <c>{ message }</c>. Heredando de ella, este rechazo viaja por ese mismo carril
/// sin tocar ningun contrato de API. Si en el futuro el front necesita REACCIONAR distinto a este
/// error (y no solo mostrarlo), ahi se le agrega un Code estable y se lo ecoa en los controllers.</para>
/// </summary>
public sealed class ServiceQuantityValidationException : ArgumentException
{
    public ServiceQuantityValidationException(string message)
        : base(message)
    {
    }
}
