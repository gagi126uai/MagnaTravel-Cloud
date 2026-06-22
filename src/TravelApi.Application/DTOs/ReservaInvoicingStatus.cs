namespace TravelApi.Application.DTOs;

/// <summary>
/// ADR-037 (2026-06-21): ESTADO DE FACTURACION de una reserva, DERIVADO del cuadre entre lo VENDIDO y lo
/// FACTURADO NETO. NO se persiste (no hay columna en BD): es la lectura del cuadre que ya calcula
/// <c>ReservaInvoicingCuadreCalculator</c> (facturas + ND - NC, solo comprobantes vivos con CAE aprobado
/// no anulado).
///
/// <para>Es un eje INDEPENDIENTE del estado operativo (<c>Reserva.Status</c>) y del estado de cobro
/// (<see cref="ReservaCollectionStatus"/>). Una reserva En viaje puede estar "Sin facturar"; una En gestion
/// puede estar "Facturada total". Este es el carril que el front pinta para que el usuario sepa, de un
/// vistazo, cuanto de la venta ya tiene comprobante fiscal.</para>
///
/// <para>ESPEJO de <see cref="ReservaCollectionStatus"/>: misma forma (clase estatica pura con tres valores
/// string y un <c>Derive</c>), misma tolerancia de centavo. La diferencia es la fuente: cobro deriva del
/// SALDO, facturacion deriva del CUADRE facturado.</para>
///
/// <para><b>Definicion de "total" = POR MONTO</b> (decision del dueño H1): "Facturada total" es
/// <c>facturadoNeto &gt;= vendido</c>. NO existe vinculo factura-servicio en el modelo, asi que no se puede
/// medir "por servicio"; la unica verdad disponible es el monto. Facturar de mas (over-invoicing) tambien
/// cuenta como "Facturada total" (no hay un cuarto valor "excedido": el aviso de exceso ya lo da el cuadre
/// con <c>Excedido</c>/<c>Disponible</c> negativo).</para>
///
/// <para><b>Limitacion ESCALAR v1</b> (decision del dueño H4): <c>facturadoNeto</c> y <c>vendido</c> son
/// montos ESCALARES (suman ARS + USD), consistentes con lo que el front ya muestra en el cuadre
/// (<c>FacturadoNeto</c> / <c>DisponibleParaFacturar</c>). A diferencia de <see cref="ReservaCollectionStatus"/>,
/// que ya es por-moneda, este carril NO separa por moneda en v1. Un carril de facturacion por moneda queda
/// como follow-up cuando el cuadre exponga el facturado por moneda.</para>
/// </summary>
public static class ReservaInvoicingStatus
{
    /// <summary>No hay nada facturado (facturadoNeto en ~0). Chip "Sin facturar".</summary>
    public const string NotInvoiced = "NotInvoiced";

    /// <summary>Hay algo facturado pero menos que lo vendido. Chip "Facturada en parte".</summary>
    public const string PartiallyInvoiced = "PartiallyInvoiced";

    /// <summary>Se facturo todo lo vendido (o de mas). Chip "Facturada total".</summary>
    public const string FullyInvoiced = "FullyInvoiced";

    /// <summary>
    /// Deriva el estado de facturacion a partir del cuadre escalar. Regla (ADR-037):
    ///   - <c>facturadoNeto &lt;= epsilon</c>                          -&gt; "NotInvoiced" (sin facturar);
    ///   - <c>epsilon &lt; facturadoNeto &lt; vendido - epsilon</c>    -&gt; "PartiallyInvoiced" (facturada en parte);
    ///   - <c>facturadoNeto &gt;= vendido - epsilon</c>                -&gt; "FullyInvoiced" (total o excedido).
    ///
    /// <para>Misma tolerancia de centavo que <see cref="ReservaCollectionStatus"/> (los importes ya vienen
    /// redondeados a 2 decimales desde el calculador de plata). El orden de los chequeos importa: primero
    /// "sin facturar", despues "total" (incluye el caso vendido &lt;= 0 con algo facturado), si no "parcial".</para>
    /// </summary>
    public static string Derive(decimal vendido, decimal facturadoNeto)
    {
        // Umbral por debajo del centavo: mismo epsilon que ReservaCollectionStatus, para no clasificar como
        // facturado/no-facturado un resto de redondeo.
        const decimal epsilon = 0.005m;

        // Sin (o casi sin) nada facturado: "Sin facturar". Cubre tambien NC que dejaron el neto en ~0 o en
        // negativo (una reserva con NC que supera a sus facturas: no hay facturacion viva, vuelve a "Sin facturar").
        if (facturadoNeto <= epsilon)
            return NotInvoiced;

        // Se facturo lo vendido o de mas: "Facturada total". El borde usa el mismo epsilon para tolerar el
        // ultimo centavo. Tambien cae aca el caso degenerado vendido <= 0 con algo facturado (over-invoicing).
        if (facturadoNeto >= vendido - epsilon)
            return FullyInvoiced;

        // Entre medio: hay facturacion pero todavia no cubre toda la venta.
        return PartiallyInvoiced;
    }
}
