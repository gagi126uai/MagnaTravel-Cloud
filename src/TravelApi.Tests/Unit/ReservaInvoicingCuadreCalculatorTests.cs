using System.Collections.Generic;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// P3: el cuadre avisa cuando se factura MAS de lo vendido en la reserva.
/// Facturas (1/6/11/51) y Notas de Debito (2/7/12/52) suman; Notas de Credito
/// (3/8/13/53) restan. Solo cuentan los comprobantes "vivos" (CAE aprobado, no anulados).
/// </summary>
public class ReservaInvoicingCuadreCalculatorTests
{
    private static CuadreInvoiceLine Live(int tipo, decimal importe) => new(tipo, importe, IsLive: true);

    [Fact]
    public void SinComprobantes_NoFacturado_DisponibleIgualVendido()
    {
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new List<CuadreInvoiceLine>());
        Assert.Equal(0m, r.FacturadoNeto);
        Assert.Equal(80_000m, r.Disponible);
        Assert.False(r.Excedido);
        Assert.Equal(0m, r.Exceso);
    }

    [Fact]
    public void FacturaIgualAlVendido_CuadraJusto_NoExcede()
    {
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[] { Live(11, 80_000m) });
        Assert.Equal(80_000m, r.FacturadoNeto);
        Assert.Equal(0m, r.Disponible);
        Assert.False(r.Excedido);
    }

    [Fact]
    public void FacturaMayorAlVendido_Excede_ConElExcesoCorrecto()
    {
        // El caso del diagnostico: facturar 100k una reserva de 80k.
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[] { Live(11, 100_000m) });
        Assert.True(r.Excedido);
        Assert.Equal(20_000m, r.Exceso);
        Assert.Equal(-20_000m, r.Disponible);
    }

    [Fact]
    public void NotaDeCredito_Resta_DelFacturadoNeto()
    {
        // Factura 100k + NC 30k = neto 70k sobre vendido 80k -> no excede, quedan 10k.
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[]
        {
            Live(11, 100_000m), // Factura C
            Live(13, 30_000m),  // NC C
        });
        Assert.Equal(70_000m, r.FacturadoNeto);
        Assert.Equal(10_000m, r.Disponible);
        Assert.False(r.Excedido);
    }

    [Fact]
    public void NotaDeDebito_Suma_AlFacturadoNeto()
    {
        // Factura 80k + ND 10k = 90k sobre vendido 80k -> excede 10k.
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[]
        {
            Live(11, 80_000m), // Factura C
            Live(12, 10_000m), // ND C
        });
        Assert.Equal(90_000m, r.FacturadoNeto);
        Assert.True(r.Excedido);
        Assert.Equal(10_000m, r.Exceso);
    }

    [Fact]
    public void FacturacionParcial_AcumuladaExcede()
    {
        // Dos facturas parciales que sumadas superan lo vendido.
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[]
        {
            Live(6, 50_000m),
            Live(6, 50_000m),
        });
        Assert.Equal(100_000m, r.FacturadoNeto);
        Assert.True(r.Excedido);
        Assert.Equal(20_000m, r.Exceso);
    }

    [Fact]
    public void ComprobantesNoVivos_NoCuentan()
    {
        // Una factura anulada/rechazada (IsLive=false) no debe contar.
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[]
        {
            new CuadreInvoiceLine(11, 100_000m, IsLive: false),
            Live(11, 80_000m),
        });
        Assert.Equal(80_000m, r.FacturadoNeto);
        Assert.False(r.Excedido);
    }

    [Fact]
    public void TipoDesconocido_NoCuenta()
    {
        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[]
        {
            Live(999, 100_000m), // tipo no reconocido
            Live(1, 80_000m),    // Factura A
        });
        Assert.Equal(80_000m, r.FacturadoNeto);
        Assert.False(r.Excedido);
    }
}
