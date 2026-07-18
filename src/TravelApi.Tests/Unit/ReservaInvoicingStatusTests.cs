using TravelApi.Application.DTOs;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-037 (2026-06-21) + ADR-048 T3 (2026-07-17, regla 5): cobertura PURA del carril de
/// facturacion DERIVADO (espejo de <c>ReservaCollectionStatus</c>). Verifica los CUATRO valores
/// (incluido "FullyReturned", el que corrige la mentira #2: NC total que dejaba el chip diciendo
/// "Sin facturar" como si nunca se hubiera facturado nada) + los bordes de epsilon + el excedido.
/// </summary>
public class ReservaInvoicingStatusTests
{
    [Fact]
    public void NuncaHuboComprobante_NetoYBrutoEnCero_EsNotInvoiced()
    {
        // Una reserva vendida en 1000 que JAMAS tuvo un comprobante -> "Sin facturar".
        Assert.Equal(
            ReservaInvoicingStatus.NotInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 0m, brutoEmitido: 0m));
    }

    [Fact]
    public void ResidualCentavoFacturado_SinBruto_SigueNotInvoiced()
    {
        // Un resto por debajo del centavo (ni el neto ni el bruto llegan a redondear) no clasifica
        // como facturado (misma tolerancia que ReservaCollectionStatus).
        Assert.Equal(
            ReservaInvoicingStatus.NotInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 0.004m, brutoEmitido: 0.004m));
    }

    [Fact]
    public void PartialInvoice_SinNotasDeCredito_IsPartiallyInvoiced()
    {
        // Facturado parte de lo vendido, sin NC de por medio (bruto == neto) -> "Facturada en parte".
        Assert.Equal(
            ReservaInvoicingStatus.PartiallyInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 400m, brutoEmitido: 400m));
    }

    [Fact]
    public void ExactMatch_SinNotasDeCredito_IsFullyInvoiced()
    {
        // Facturado exactamente lo vendido, sin NC -> "Facturada total".
        Assert.Equal(
            ReservaInvoicingStatus.FullyInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 1000m, brutoEmitido: 1000m));
    }

    [Fact]
    public void OneCentavoBelowVendido_IsFullyInvoiced_ByEpsilon()
    {
        // Borde superior: a un centavo por debajo de lo vendido, la tolerancia lo da por "total".
        Assert.Equal(
            ReservaInvoicingStatus.FullyInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 999.996m, brutoEmitido: 999.996m));
    }

    [Fact]
    public void JustBelowVendido_BeyondEpsilon_IsPartiallyInvoiced()
    {
        // Apenas mas abajo del umbral de tolerancia: todavia "parcial".
        Assert.Equal(
            ReservaInvoicingStatus.PartiallyInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 999m, brutoEmitido: 999m));
    }

    [Fact]
    public void OverInvoiced_IsFullyInvoiced()
    {
        // Facturado de MAS (over-invoicing) -> sigue siendo "Facturada total" (decision H1: no hay cuarto
        // valor "excedido"; ese aviso lo da el cuadre con Excedido/Disponible negativo).
        Assert.Equal(
            ReservaInvoicingStatus.FullyInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 1200m, brutoEmitido: 1200m));
    }

    // ================================================================================================
    // ADR-048 T3 (2026-07-17, regla 5): "Facturada y devuelta" — cierra la mentira #2. Antes de este
    // ADR, una NC total dejaba el neto en ~0 y el chip decia "Sin facturar" como si la reserva NUNCA
    // hubiera tenido un comprobante, escondiendo el rastro fiscal al usuario.
    // ================================================================================================

    [Fact]
    public void FacturaTotalDevueltaPorNC_NetoCero_BrutoPositivo_EsFullyReturned()
    {
        // Se facturaron 1000 (bruto) y una NC total los devolvio enteros -> neto 0, bruto 1000.
        // Ya NO cae en "Sin facturar": el sistema SI facturo, y despues devolvio.
        Assert.Equal(
            ReservaInvoicingStatus.FullyReturned,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 0m, brutoEmitido: 1000m));
    }

    [Fact]
    public void FacturaTotalDevueltaPorNC_NetoNegativo_BrutoPositivo_EsFullyReturned()
    {
        // Caso degenerado: la NC "de mas" deja el neto negativo (dato historico raro, pero no debe
        // reventar la regla). Con bruto>0 sigue siendo "Facturada y devuelta", no "Sin facturar".
        Assert.Equal(
            ReservaInvoicingStatus.FullyReturned,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: -5m, brutoEmitido: 1000m));
    }

    [Fact]
    public void NetoCasiCeroPorRedondeo_ConBrutoReal_EsFullyReturned()
    {
        // Caso borde de redondeo: la reserva SI tuvo un comprobante real (bruto 1000) y la NC lo
        // devolvio casi entero, dejando un resto de 0.004 (por debajo del epsilon de centavo). El
        // resto no alcanza a clasificar como "parcial": sigue siendo "se facturo y se devolvio".
        Assert.Equal(
            ReservaInvoicingStatus.FullyReturned,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 0.004m, brutoEmitido: 1000m));
    }

    [Fact]
    public void PartialInvoice_ConNotaDeCreditoParcial_SigueSiendoPartiallyInvoiced()
    {
        // Regla explicita del diseno: el nuevo eje SOLO se activa cuando el NETO queda en ~0. Con NC
        // parcial (neto > epsilon), el resultado sigue como hasta hoy: "Facturada en parte", aunque
        // el bruto (antes de la NC) haya sido mayor. Factura 1000 + NC 600 = neto 400, bruto 1000.
        Assert.Equal(
            ReservaInvoicingStatus.PartiallyInvoiced,
            ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 400m, brutoEmitido: 1000m));
    }
}
