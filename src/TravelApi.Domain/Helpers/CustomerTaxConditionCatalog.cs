using System;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Fuente UNICA de la traduccion entre el codigo AFIP de la condicion fiscal del cliente
/// (<c>Customer.TaxConditionId</c>) y el texto legible que la agencia usa en pantalla, en el PDF de la
/// factura y en el snapshot fiscal de una cancelacion (<c>Customer.TaxCondition</c>).
///
/// <para><b>Por que existe esta clase (bug real, 2026-07-17)</b>: <c>Customer</c> guarda la condicion
/// fiscal en DOS campos que antes se llenaban por separado — el codigo (<c>TaxConditionId</c>, lo que
/// manda el dropdown "Condicion AFIP" de la ficha) y el texto (<c>TaxCondition</c>, lo que lee el motor
/// de cancelaciones). El formulario del cliente (<c>CustomerFormModal.jsx</c>) SOLO manda el codigo —
/// nunca el texto — asi que el codigo se guardaba bien pero el texto quedaba VIEJO para siempre. Como
/// <c>BookingCancellationService.ResolveServerSideTaxIdentity</c> solo lee el texto, un vendedor que
/// corregia la condicion fiscal del cliente en la ficha veia el cambio guardado (el desplegable mostraba
/// la opcion correcta al reabrir) pero la devolucion seguia bloqueada pidiendole "completa la condicion
/// fiscal", porque el texto detras de escena nunca se habia movido.</para>
///
/// <para><b>La regla que esta clase hace cumplir</b>: cuando hay un codigo (<c>TaxConditionId</c>), el
/// texto SIEMPRE sale de este catalogo — nunca se confia en un texto suelto que puede haber quedado
/// desalineado. Es el UNICO lugar del repo que hace esta traduccion (ver
/// <see cref="CustomerService.CreateCustomerAsync"/> / <see cref="CustomerService.UpdateCustomerAsync"/>
/// y la defensa en <see cref="BookingCancellationService.ResolveServerSideTaxIdentity"/>).</para>
///
/// <para>Reusa los mismos codigos AFIP que <see cref="ArcaReceptorResolver"/> (tabla
/// CondicionIVAReceptorId, RG 5616) — este catalogo es el subconjunto de 4 codigos que el dropdown de la
/// ficha del cliente ofrece hoy (Responsable Inscripto, Monotributo, Exento, Consumidor Final).</para>
/// </summary>
public static class CustomerTaxConditionCatalog
{
    public const int ResponsableInscripto = ArcaReceptorResolver.CondicionIvaResponsableInscripto; // 1
    public const int Exento = ArcaReceptorResolver.CondicionIvaExento; // 4
    public const int ConsumidorFinal = ArcaReceptorResolver.CondicionIvaConsumidorFinal; // 5
    public const int Monotributo = ArcaReceptorResolver.CondicionIvaMonotributo; // 6

    /// <summary>
    /// El texto legible (mismo formato que ofrece el dropdown de <c>CustomerFormModal.jsx</c>) para un
    /// codigo AFIP conocido. <c>null</c> si el codigo no es ninguno de los 4 que la ficha del cliente
    /// maneja hoy — defensivo: el dropdown solo puede mandar 1/4/5/6, pero un codigo futuro o un dato
    /// corrupto no debe inventarse un texto (el caller decide el fallback).
    /// </summary>
    public static string? TryGetLabel(int taxConditionId) => taxConditionId switch
    {
        ResponsableInscripto => "Responsable Inscripto",
        Monotributo => "Monotributo",
        Exento => "Exento",
        ConsumidorFinal => "Consumidor Final",
        _ => null,
    };

    /// <summary>
    /// Derivacion inversa (texto -&gt; codigo), para el caso de un caller legacy que solo manda el texto
    /// (nunca el codigo). Usa <see cref="TaxConditionNormalizer"/> para tolerar mayusculas/tildes/variantes
    /// (ej. "MONOTRIBUTO", "Monotríbutista"). Devuelve <c>null</c> si el texto no matchea sin ambiguedad
    /// ninguna de las 4 condiciones que este catalogo cubre (ej. "Extranjero", vacio, o texto libre raro):
    /// el caller decide que hacer (tipicamente, preservar el codigo que ya habia).
    /// </summary>
    public static int? TryGetIdFromLabel(string? taxConditionText)
    {
        var canonical = TaxConditionNormalizer.Normalize(taxConditionText);
        return canonical switch
        {
            TaxConditionCanonical.ResponsableInscripto => ResponsableInscripto,
            TaxConditionCanonical.Monotributista => Monotributo,
            TaxConditionCanonical.Exento => Exento,
            TaxConditionCanonical.ConsumidorFinal => ConsumidorFinal,
            _ => null,
        };
    }

    /// <summary>
    /// Resuelve el par (Id, Texto) que se debe PERSISTIR a partir de lo que llego en el request y de lo
    /// que ya habia guardado. Es la unica funcion que decide como se combinan los dos campos — ni
    /// <c>CustomerService.CreateCustomerAsync</c> ni <c>UpdateCustomerAsync</c> repiten esta logica.
    ///
    /// <para><b>Orden de reglas (cada una gana sobre la siguiente):</b></para>
    /// <list type="number">
    /// <item><b>Vino un codigo (<paramref name="incomingId"/>)</b>: el texto SIEMPRE sale del catalogo
    /// para ese codigo — el texto que haya venido en el request se IGNORA a proposito (es la regla que
    /// arregla el bug: nunca puede quedar un codigo nuevo con un texto viejo). Si el codigo no esta en el
    /// catalogo (no deberia pasar desde el dropdown), se degrada a la regla 3.</item>
    /// <item><b>No vino codigo, pero vino un texto DISTINTO al que ya habia</b>: caller legacy que solo
    /// maneja el texto. Se guarda el texto tal cual vino y se intenta derivar el codigo inverso; si el
    /// texto no matchea sin ambiguedad, se conserva el codigo que ya habia (no se inventa uno).</item>
    /// <item><b>No vino nada nuevo (codigo ausente, texto ausente o igual al que ya habia)</b>: se
    /// preserva TAL CUAL lo que ya estaba guardado — mismo criterio "omitido = no se toca" que ya se usa
    /// para <c>DocumentType</c>/<c>DocumentNumber</c> (ADR-023 T1). Esto es lo que hace que editar un campo
    /// cualquiera de la ficha (ej. el telefono) nunca pise la condicion fiscal por accidente.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Resuelve la condicion fiscal CANONICA (<see cref="TaxConditionCanonical"/>) del cliente para saber si
    /// esta "pendiente" (Unknown) o no. Es la MISMA formula que usa el motor de anulaciones para bloquear con
    /// INV-118 (<c>BookingCancellationService.ResolveServerSideTaxIdentity</c>) — la extrajimos aca, a este
    /// catalogo, para que un segundo consumidor (la solapa "Datos" de la ficha del cliente) pueda mostrar el
    /// mismo veredicto sin inventar una segunda formula que se pueda desalinear con el tiempo.
    ///
    /// <para><b>Orden de resolucion (igual al que ya usaba el motor de anulaciones)</b>: primero el TEXTO
    /// (<paramref name="taxConditionText"/>) via <see cref="TaxConditionNormalizer"/>; si el texto esta vacio
    /// o no normaliza, se cae al CODIGO AFIP (<paramref name="taxConditionId"/>) buscando su label en este
    /// catalogo. Si ninguno de los dos resuelve, el resultado es <see cref="TaxConditionCanonical.Unknown"/> —
    /// "faltan datos fiscales".</para>
    /// </summary>
    public static TaxConditionCanonical ResolveCanonical(string? taxConditionText, int? taxConditionId)
    {
        var fromText = TaxConditionNormalizer.Normalize(taxConditionText);
        if (fromText != TaxConditionCanonical.Unknown)
        {
            return fromText;
        }

        if (taxConditionId is int id)
        {
            var label = TryGetLabel(id);
            if (label != null)
            {
                return TaxConditionNormalizer.Normalize(label);
            }
        }

        return TaxConditionCanonical.Unknown;
    }

    public static (int? TaxConditionId, string TaxCondition) ResolveIncoming(
        int? incomingId, string? incomingText, int? existingId, string existingText)
    {
        if (incomingId.HasValue)
        {
            var label = TryGetLabel(incomingId.Value);
            if (label != null)
            {
                return (incomingId.Value, label);
            }

            // Codigo fuera del catalogo (no deberia pasar desde el dropdown real): no confiamos en el
            // ni para el texto — se degrada de lleno a la regla 3 (preservar lo que ya habia). Un
            // codigo corrupto no debe combinarse con el texto entrante para inventar un par nuevo.
            return (existingId, existingText);
        }

        var hasDifferentText = !string.IsNullOrWhiteSpace(incomingText)
            && !string.Equals(incomingText, existingText, StringComparison.Ordinal);
        if (hasDifferentText)
        {
            var derivedId = TryGetIdFromLabel(incomingText) ?? existingId;
            return (derivedId, incomingText!);
        }

        var preservedText = string.IsNullOrWhiteSpace(incomingText) ? existingText : incomingText!;
        return (existingId, preservedText);
    }
}
