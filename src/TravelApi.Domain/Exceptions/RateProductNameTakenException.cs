namespace TravelApi.Domain.Exceptions;

/// <summary>
/// El nombre nuevo que se le quiere poner a un producto del tarifario YA lo tiene otro producto
/// (spec firmada 2026-08-06, §2.2: renombrar NO fusiona).
///
/// <para><b>Por que es su propia excepcion y no una validacion mas</b>: no es un dato mal escrito (400),
/// es un CONFLICTO con el estado actual del tarifario (409). Y sobre todo: la salida correcta no es
/// "corregí el dato", es una decision de negocio — o le ponés otro nombre, o usás el producto que ya
/// existe. Unir dos productos en uno es otra obra, con su propia pantalla; aca NO se fusiona nada
/// silenciosamente, que es justo como se pierden precios.</para>
///
/// <para>El <c>Message</c> es lo que ve el usuario: español, sin jerga, sin ids.</para>
/// </summary>
public sealed class RateProductNameTakenException : InvalidOperationException
{
    public RateProductNameTakenException(string message)
        : base(message)
    {
    }
}
