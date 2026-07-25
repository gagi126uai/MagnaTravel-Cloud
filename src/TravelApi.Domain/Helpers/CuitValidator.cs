namespace TravelApi.Domain.Helpers;

/// <summary>
/// Hallazgo H2 (barrido E2E 2026-07-25): el alta de cliente aceptaba un CUIT con el digito
/// verificador mal tipeado sin avisar nada — riesgo fiscal real, porque ese CUIT despues se usa
/// para facturar (Factura A/B exige un CUIT valido de verdad, no cualquier numero de 11 digitos).
///
/// <para><b>Por que este helper y no repetir el calculo</b>: el algoritmo de digito verificador
/// (modulo 11) YA vive en <see cref="ArcaReceptorResolver.IsValidCuit"/>, probado y usado hoy para
/// decidir como facturar. Este validador NO reimplementa esa cuenta — solo agrega el paso de
/// "limpiar lo que tipeo el usuario" (sacar guiones/puntos/espacios) antes de preguntarle al
/// mismo algoritmo si el numero cierra. Una sola fuente de verdad para el digito verificador,
/// usada tanto para decidir el DocTipo de ARCA como para bloquear el alta de un cliente.</para>
///
/// <para><b>Que NO valida</b>: no verifica que el CUIT exista de verdad en ARCA (eso requeriria
/// consultar el padron, fuera de alcance de este gate) — solo que el numero este bien formado y
/// que su digito verificador cierre matematicamente. Es la misma verificacion que usan los
/// sistemas de facturacion electronica antes de mandar un comprobante.</para>
/// </summary>
public static class CuitValidator
{
    /// <summary>
    /// Mensaje criollo para el usuario final cuando el CUIT no pasa el chequeo. Centralizado aca
    /// para que el mensaje que ve el vendedor sea SIEMPRE el mismo, sin importar desde que
    /// pantalla (alta o edicion de cliente) dispare el bloqueo.
    /// </summary>
    public const string InvalidCuitMessage = "Ese CUIT no es válido. Revisá los números.";

    /// <summary>
    /// True si <paramref name="rawValue"/>, una vez limpio de guiones/puntos/espacios, es un
    /// CUIT/CUIL de 11 digitos con digito verificador correcto (modulo 11). Un valor vacio o
    /// nulo se considera VALIDO a proposito: este helper solo bloquea un CUIT MAL TIPEADO, no
    /// exige que el cliente tenga uno cargado (hay clientes que solo tienen DNI).
    /// </summary>
    public static bool IsValidOrEmpty(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        var clean = ArcaReceptorResolver.CleanNumericString(rawValue);
        return ArcaReceptorResolver.IsValidCuit(clean);
    }
}
