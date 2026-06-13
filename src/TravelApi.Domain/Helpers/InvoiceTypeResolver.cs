using TravelApi.Domain.Helpers;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Resuelve la LETRA del comprobante de VENTA (Factura A=1 / B=6 / C=11) segun la
/// condicion fiscal del EMISOR (la agencia) cruzada con la del RECEPTOR (el cliente).
///
/// <para><b>Por que existe</b>: esta decision vivia inline en
/// <c>AfipService.CreatePendingInvoice</c> comparando strings literales
/// (<c>== "Responsable Inscripto"</c>). Eso tenia dos problemas:
/// <list type="number">
///   <item>Un BUG fiscal: emisor RI a un receptor Monotributo daba B, cuando ARCA
///   (RG 5003/2021, Ley 27.618 "Sostenimiento e Inclusion Fiscal para Pequenos
///   Contribuyentes") exige Factura A.</item>
///   <item>Fragilidad: cualquier variante de texto ("Monotributista", "MONOTRIBUTO")
///   no matcheaba el literal y degradaba en silencio a B/C.</item>
/// </list>
/// Centralizar la matriz aca permite testearla como unidad pura (sin DB ni ARCA) y que
/// la normalizacion de variantes sea responsabilidad de <see cref="TaxConditionNormalizer"/>.</para>
///
/// <para><b>Alcance</b>: SOLO la letra de la FACTURA de venta. La letra de las Notas de
/// Credito/Debito se deriva del comprobante asociado (ver
/// <see cref="InvoiceComprobanteHelpers"/>), no de esta matriz, y eso queda intacto.</para>
/// </summary>
public static class InvoiceTypeResolver
{
    // Codigos de comprobante ARCA (WSFEv1).
    public const int FacturaA = 1;
    public const int FacturaB = 6;
    public const int FacturaC = 11;

    /// <summary>
    /// Texto EXACTO de la leyenda obligatoria que debe llevar una Factura A emitida por un
    /// Responsable Inscripto a un Monotributista (RG 5003/2021, Ley 27.618). Se inserta tal
    /// cual en las observaciones del comprobante (campo Obs del WSFEv1 y/o el PDF). No
    /// modificar el texto: es el literal que pide la norma.
    /// </summary>
    public const string LeyendaFacturaAMonotributista =
        "El crédito fiscal discriminado en el presente comprobante sólo podrá ser computado a efectos del Régimen de Sostenimiento e Inclusión Fiscal para Pequeños Contribuyentes de la Ley 27.618.";

    /// <summary>
    /// Matriz emisor x receptor (verificada contra ARCA, confirmada por el dueno + contador):
    ///
    /// <list type="bullet">
    ///   <item>Emisor <b>Responsable Inscripto</b>:
    ///     <list type="bullet">
    ///       <item>receptor Responsable Inscripto -> A (1)</item>
    ///       <item>receptor <b>Monotributo -> A (1)</b> (RG 5003/Ley 27.618)</item>
    ///       <item>receptor Consumidor Final / Exento / cualquier otro -> B (6)</item>
    ///     </list>
    ///   </item>
    ///   <item>Emisor <b>Monotributo</b>: siempre C (11).</item>
    ///   <item>Emisor <b>Exento</b>: siempre C (11).</item>
    ///   <item>Emisor desconocido (default conservador): B (6). OJO: esto NO es igual al
    ///   comportamiento historico — el inline viejo devolvia C (11) para cualquier emisor que no
    ///   fuera "Responsable Inscripto". Elegimos B como default defensivo (no discriminar IVA de mas)
    ///   porque B nunca permite computar credito fiscal indebido. La diferencia con el historico
    ///   (C) solo se manifestaria con dato CORRUPTO del emisor, caso que hoy no ocurre (ver nota
    ///   abajo).</item>
    /// </list>
    ///
    /// <para>Ambas condiciones se normalizan con <see cref="TaxConditionNormalizer"/> ANTES de
    /// decidir, asi variantes de formato ("Monotributista", "MONOTRIBUTO", con tildes) no
    /// degradan la letra en silencio.</para>
    /// </summary>
    /// <param name="emisorTaxCondition">Condicion fiscal de la agencia (texto crudo de AfipSettings).</param>
    /// <param name="receptorTaxCondition">Condicion fiscal del cliente (texto crudo de Customer).</param>
    /// <returns>El cbteTipo de la factura de venta (1, 6 u 11).</returns>
    public static int ResolveSaleInvoiceType(string? emisorTaxCondition, string? receptorTaxCondition)
    {
        var emisor = TaxConditionNormalizer.Normalize(emisorTaxCondition);
        var receptor = TaxConditionNormalizer.Normalize(receptorTaxCondition);

        // Monotributo y Exento emiten SIEMPRE Factura C (no discriminan IVA).
        if (emisor == TaxConditionCanonical.Monotributista ||
            emisor == TaxConditionCanonical.Exento)
        {
            return FacturaC;
        }

        if (emisor == TaxConditionCanonical.ResponsableInscripto)
        {
            // RI a RI o a Monotributo -> Factura A. El caso Mono es el FIX del bug:
            // antes daba B. ARCA exige A (Ley 27.618).
            if (receptor == TaxConditionCanonical.ResponsableInscripto ||
                receptor == TaxConditionCanonical.Monotributista)
            {
                return FacturaA;
            }

            // RI a Consumidor Final, Exento, Extranjero, o receptor desconocido -> Factura B.
            return FacturaB;
        }

        // Emisor desconocido (dato corrupto): default conservador B (6).
        //
        // OJO - esto difiere del comportamiento historico: el inline viejo asignaba C (11) cuando
        // el emisor no era exactamente "Responsable Inscripto". Aca elegimos B a proposito porque es
        // mas defensivo: B no discrimina IVA, asi que no habilita computar credito fiscal indebido
        // si el emisor llegara corrupto. La diferencia (B vs el C historico) solo se manifestaria con
        // un emisor invalido, caso que hoy NO ocurre: AfipSettings.TaxCondition tiene default
        // "Responsable Inscripto" y el dropdown solo ofrece valores conocidos, asi que en la practica
        // esta rama no se alcanza.
        return FacturaB;
    }

    /// <summary>
    /// Indica si una Factura A emitida por RI a un Monotributista requiere la leyenda
    /// obligatoria de la Ley 27.618 (<see cref="LeyendaFacturaAMonotributista"/>).
    ///
    /// <para>La leyenda va SOLO en ese caso (RI -> Mono, Factura A). No en RI->RI, ni en B, ni
    /// en C. El caller usa este metodo para decidir si concatena la leyenda al campo Obs del
    /// comprobante.</para>
    /// </summary>
    public static bool RequiresMonotributistaLegend(string? emisorTaxCondition, string? receptorTaxCondition)
    {
        var emisor = TaxConditionNormalizer.Normalize(emisorTaxCondition);
        var receptor = TaxConditionNormalizer.Normalize(receptorTaxCondition);

        return emisor == TaxConditionCanonical.ResponsableInscripto &&
               receptor == TaxConditionCanonical.Monotributista;
    }
}
