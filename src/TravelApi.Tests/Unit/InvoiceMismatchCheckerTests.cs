using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Hallazgo auditoria ERP #9 (2026-06-13): tests del aviso de descuadre entre lo facturado y lo vendido
/// confirmado. Verifican que el aviso aparece cuando no cuadra, NO aparece cuando cuadra, y que la
/// tolerancia de centavo no dispara avisos por redondeo. El aviso es solo texto: el caracter no
/// bloqueante se verifica a nivel del service (esta clase no emite nada, solo decide el texto).
/// </summary>
public class InvoiceMismatchCheckerTests
{
    [Fact]
    public void BuildMismatchWarning_WhenMatches_ReturnsNull()
    {
        var warning = InvoiceMismatchChecker.BuildMismatchWarning("ARS", invoicedItemsTotal: 1000m, confirmedSaleForCurrency: 1000m);
        Assert.Null(warning);
    }

    [Fact]
    public void BuildMismatchWarning_WhenInvoicedMore_ReturnsWarning()
    {
        var warning = InvoiceMismatchChecker.BuildMismatchWarning("ARS", invoicedItemsTotal: 1200m, confirmedSaleForCurrency: 1000m);

        Assert.NotNull(warning);
        Assert.Contains("mas", warning);
    }

    [Fact]
    public void BuildMismatchWarning_WhenInvoicedLess_ReturnsWarning()
    {
        var warning = InvoiceMismatchChecker.BuildMismatchWarning("USD", invoicedItemsTotal: 800m, confirmedSaleForCurrency: 1000m);

        Assert.NotNull(warning);
        Assert.Contains("menos", warning);
        Assert.Contains("USD", warning);
    }

    [Theory]
    // Diferencias de hasta 1 centavo NO disparan aviso (redondeo entre suma de lineas y agregado).
    [InlineData(1000.00, 1000.01)]
    [InlineData(1000.01, 1000.00)]
    [InlineData(1000.00, 999.99)]
    public void BuildMismatchWarning_WithinCentTolerance_ReturnsNull(double invoiced, double confirmed)
    {
        var warning = InvoiceMismatchChecker.BuildMismatchWarning("ARS", (decimal)invoiced, (decimal)confirmed);
        Assert.Null(warning);
    }

    [Theory]
    // Diferencias mayores a 1 centavo SI disparan aviso.
    [InlineData(1000.00, 1000.02)]
    [InlineData(1000.05, 1000.00)]
    public void BuildMismatchWarning_BeyondCentTolerance_ReturnsWarning(double invoiced, double confirmed)
    {
        var warning = InvoiceMismatchChecker.BuildMismatchWarning("ARS", (decimal)invoiced, (decimal)confirmed);
        Assert.NotNull(warning);
    }

    [Fact]
    public void BuildMismatchWarning_NoConfirmedSale_WarnsWhenInvoicingSomething()
    {
        // Reserva sin venta confirmada en esa moneda (0) pero facturando algo -> descuadre que se avisa.
        var warning = InvoiceMismatchChecker.BuildMismatchWarning("ARS", invoicedItemsTotal: 500m, confirmedSaleForCurrency: 0m);
        Assert.NotNull(warning);
    }
}
