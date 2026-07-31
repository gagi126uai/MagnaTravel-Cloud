namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para><b>Por que existe</b>: la condicion fiscal decide QUE COMPROBANTE se emite (A, B o C) y como se
/// discrimina el IVA. Los desplegables de las pantallas ofrecen solo las opciones reales, pero el motor
/// aceptaba cualquier texto que llegara por la API — y un texto que el motor despues no reconoce cae en
/// "Desconocido", que es justo lo que bloquea una anulacion o hace elegir mal la letra de la factura.</para>
///
/// <para><b>Contra que lista valida</b>: NO inventa una lista nueva. Usa las dos que ya existen y que el
/// resto del sistema lee para facturar:</para>
/// <list type="bullet">
///   <item><b>Texto</b> (operador, agencia, configuracion de ARCA): <see cref="TaxConditionNormalizer"/>,
///     la misma tabla que usa el motor de anulaciones para armar el snapshot fiscal. Si el texto no
///     normaliza a ninguna condicion conocida, no existe.</item>
///   <item><b>Codigo AFIP</b> (ficha del cliente, campo <c>TaxConditionId</c>):
///     <see cref="CustomerTaxConditionCatalog"/>, el catalogo de los 4 codigos que la ficha ofrece.</item>
/// </list>
///
/// <para><b>Vacio = valido</b>, igual que los demas validadores de esta tanda: hay fichas viejas sin
/// condicion cargada, y este gate frena un valor MAL, no exige completar el dato. Quien necesita la
/// condicion SI o SI (facturar, anular) ya la exige por su cuenta con su propio mensaje.</para>
/// </summary>
public static class TaxConditionValidator
{
    /// <summary>Mensaje criollo unico para todas las puertas (cliente, operador, agencia, ARCA).</summary>
    public const string InvalidTaxConditionMessage = "Esa condición fiscal no existe. Elegila de la lista.";

    /// <summary>
    /// True si <paramref name="rawText"/> esta vacio o es una condicion fiscal que el sistema reconoce
    /// (tolera mayusculas, tildes y las dos formas de escribirla: "Monotributo" y "MONOTRIBUTISTA").
    /// </summary>
    public static bool IsKnownTextOrEmpty(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return true;
        }

        return TaxConditionNormalizer.Normalize(rawText) != TaxConditionCanonical.Unknown;
    }

    /// <summary>
    /// True si <paramref name="taxConditionId"/> es null (no vino el dato) o es uno de los codigos AFIP
    /// que la ficha del cliente maneja (Responsable Inscripto, Monotributo, Exento, Consumidor Final).
    /// </summary>
    public static bool IsKnownCustomerCodeOrEmpty(int? taxConditionId)
    {
        if (taxConditionId is null)
        {
            return true;
        }

        return CustomerTaxConditionCatalog.TryGetLabel(taxConditionId.Value) != null;
    }
}
