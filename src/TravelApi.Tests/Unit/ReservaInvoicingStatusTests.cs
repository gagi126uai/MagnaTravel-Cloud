using TravelApi.Application.DTOs;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-037 (2026-06-21): cobertura PURA del carril de facturacion DERIVADO (espejo de
/// <c>ReservaCollectionStatus</c>). Verifica los tres valores + los bordes de epsilon y el caso excedido.
/// </summary>
public class ReservaInvoicingStatusTests
{
    [Fact]
    public void NoInvoices_IsNotInvoiced()
    {
        // Una reserva vendida en 1000 sin nada facturado -> "Sin facturar".
        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 0m));
    }

    [Fact]
    public void ResidualCentavoFacturado_StillNotInvoiced()
    {
        // Un resto por debajo del centavo no clasifica como facturado (misma tolerancia que CollectionStatus).
        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 0.004m));
    }

    [Fact]
    public void PartialInvoice_IsPartiallyInvoiced()
    {
        // Facturado parte de lo vendido -> "Facturada en parte".
        Assert.Equal(ReservaInvoicingStatus.PartiallyInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 400m));
    }

    [Fact]
    public void ExactMatch_IsFullyInvoiced()
    {
        // Facturado exactamente lo vendido -> "Facturada total".
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 1000m));
    }

    [Fact]
    public void OneCentavoBelowVendido_IsFullyInvoiced_ByEpsilon()
    {
        // Borde superior: a un centavo por debajo de lo vendido, la tolerancia lo da por "total".
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 999.996m));
    }

    [Fact]
    public void JustBelowVendido_BeyondEpsilon_IsPartiallyInvoiced()
    {
        // Apenas mas abajo del umbral de tolerancia: todavia "parcial".
        Assert.Equal(ReservaInvoicingStatus.PartiallyInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 999m));
    }

    [Fact]
    public void OverInvoiced_IsFullyInvoiced()
    {
        // Facturado de MAS (over-invoicing) -> sigue siendo "Facturada total" (decision H1: no hay cuarto valor).
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 1200m));
    }

    [Fact]
    public void NetCreditedToZero_BackToNotInvoiced()
    {
        // Una NC que dejo el neto en 0 (factura + NC que la anula) vuelve a "Sin facturar".
        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, ReservaInvoicingStatus.Derive(vendido: 1000m, facturadoNeto: 0m));
    }
}
