namespace TravelApi.Domain.Exceptions;

/// <summary>
/// Excepcion de dominio para las validaciones de NEGOCIO del Tarifario (alta simple de producto y
/// renombrado de producto, spec firmada 2026-08-06).
///
/// <para><b>Por que existe</b>: mismo criterio que <see cref="PaymentValidationException"/> del lado
/// cobros. El controller no puede saber, mirando un <see cref="System.ArgumentException"/> cualquiera, si
/// el texto que trae adentro fue escrito para una PERSONA o es un mensaje tecnico de framework (que ademas
/// suele traer el sufijo "(Parameter 'x')"). Devolver <c>ex.Message</c> crudo de una excepcion generica es
/// justamente la fuga que el gate de exposicion de datos prohibe.</para>
///
/// <para>Con esta excepcion propia, el controller atrapa SOLO esta para responder 400 con el texto tal
/// cual; cualquier otra cosa que reviente sigue de largo hasta el manejador global, que responde el
/// generico amable sin filtrar nada tecnico.</para>
///
/// <para><b>IMPORTANTE</b>: el <c>Message</c> de esta excepcion es SIEMPRE lo que ve el usuario. Nunca
/// debe llevar nombres de clase/campo internos, ni ids, ni texto en ingles: mensaje de negocio en español,
/// autocontenido.</para>
/// </summary>
public sealed class RateValidationException : InvalidOperationException
{
    public RateValidationException(string message)
        : base(message)
    {
    }
}
