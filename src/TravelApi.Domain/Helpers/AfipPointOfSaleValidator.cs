namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para><b>Por que existe</b>: el punto de venta es el numero con el que ARCA identifica la "caja" que
/// emite los comprobantes (aparece como los primeros 5 digitos del numero de factura: 00003-00000124).
/// El campo aceptaba cualquier entero, incluido 0 o un numero disparatado; recien al intentar facturar
/// ARCA rechazaba el comprobante con un error tecnico que no dice nada. Se frena al guardar.</para>
///
/// <para><b>El rango 1..99998 no es inventado</b>: el numero de punto de venta viaja a ARCA en un campo
/// de 5 digitos (por eso el tope de 5 cifras), el 0 no identifica ninguna caja habilitada, y el 99999
/// esta reservado. Cualquier valor fuera de ese rango no puede corresponder a un punto de venta real.</para>
/// </summary>
public static class AfipPointOfSaleValidator
{
    /// <summary>Mensaje criollo unico para el admin que carga la configuracion de facturacion.</summary>
    public const string InvalidPointOfSaleMessage =
        "El punto de venta tiene que ser un número entre 1 y 99998.";

    private const int MinimumPointOfSale = 1;
    private const int MaximumPointOfSale = 99998;

    /// <summary>
    /// True si <paramref name="pointOfSale"/> cae dentro del rango que ARCA puede aceptar.
    ///
    /// <para>Ojo: aca el 0 es INVALIDO (a diferencia del CUIT, donde 0 significa "todavia no
    /// configurado"). Quien llama decide si el 0 merece un trato especial — hoy
    /// <c>AfipService.UpdateSettingsAsync</c> solo valida cuando el numero CAMBIA, asi que una
    /// configuracion vieja con 0 no traba al admin que solo viene a subir un certificado.</para>
    /// </summary>
    public static bool IsValid(int pointOfSale)
    {
        return pointOfSale >= MinimumPointOfSale && pointOfSale <= MaximumPointOfSale;
    }
}
