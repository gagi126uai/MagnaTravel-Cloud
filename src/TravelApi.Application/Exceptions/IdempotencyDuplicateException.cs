namespace TravelApi.Application.Exceptions;

/// <summary>
/// FC1.3 Fase 2 (plan tactico §FC1.3.F2.2 capa 1.5, 2026-05-27): se lanza cuando el
/// job de emision de NC parcial detecta que ya existe una clave de idempotencia
/// ACTIVA (otro intento sigue procesando la MISMA cancelacion) y todavia no paso el
/// umbral para considerarla huerfana.
///
/// <para><b>Por que existe como tipo propio</b> (y no una <c>InvalidOperationException</c>
/// generica): el job la usa como senal de "no soy yo el que tiene que emitir ahora,
/// hay otro intento en vuelo". El job la atrapa, NO emite el comprobante y deja que el
/// job de reconciliacion lo recoja en el proximo ciclo si nadie lo resuelve. Tenerla
/// como tipo dedicado permite distinguir "duplicado legitimo, no reintentar a ciegas"
/// de un error tecnico real.</para>
///
/// <para><b>Ejemplo pelotudo</b>: dos cajeros aprietan "emitir NC" casi al mismo tiempo
/// para la misma cancelacion. El primero inserta la clave y arranca el POST al ARCA.
/// El segundo choca con esa clave ACTIVA (recien creada) y rebota con esta excepcion:
/// no manda un segundo comprobante por el mismo hecho economico.</para>
/// </summary>
public class IdempotencyDuplicateException : Exception
{
    public IdempotencyDuplicateException(string message) : base(message)
    {
    }
}
