using System.Collections.Generic;
using System.Linq;
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

    // ============================================================================================
    // FIX doble conteo de la anulacion (2026-06-25): la regla CountsInNetBilled cuenta el CAE aprobado
    // AUNQUE este anulado (Succeeded). El llamador arma IsLive con esa regla, asi la factura anulada
    // sigue sumando y su NC resta -> la anulacion se cuenta UNA sola vez.
    // ============================================================================================

    [Fact]
    public void CountsInNetBilled_SoloElCaeAprobado_CuentaAunqueEsteAnulado()
    {
        // CAE aprobado cuenta (sin importar si despues se anulo: eso lo refleja la NC, no el filtro).
        Assert.True(ReservaInvoicingCuadreCalculator.CountsInNetBilled("A"));
        // Sin CAE firme no cuenta: PENDING, rechazado, null.
        Assert.False(ReservaInvoicingCuadreCalculator.CountsInNetBilled("PENDING"));
        Assert.False(ReservaInvoicingCuadreCalculator.CountsInNetBilled("R"));
        Assert.False(ReservaInvoicingCuadreCalculator.CountsInNetBilled(null));
    }

    [Fact]
    public void AnulacionTotal_FacturaAnuladaMasNC_FacturadoNetoCero()
    {
        // La factura anulada (CAE aprobado) SIGUE sumando porque CountsInNetBilled("A") = true; su NC resta.
        // 80k (factura anulada, suma) - 80k (NC) = 0. Antes (excluyendo Succeeded) daba -80k.
        bool facturaAnuladaCuenta = ReservaInvoicingCuadreCalculator.CountsInNetBilled("A");
        bool ncCuenta = ReservaInvoicingCuadreCalculator.CountsInNetBilled("A");

        var r = ReservaInvoicingCuadreCalculator.Calculate(80_000m, new[]
        {
            new CuadreInvoiceLine(11, 80_000m, IsLive: facturaAnuladaCuenta), // Factura C anulada
            new CuadreInvoiceLine(13, 80_000m, IsLive: ncCuenta),             // NC C
        });

        Assert.Equal(0m, r.FacturadoNeto);
        Assert.Equal(80_000m, r.Disponible);
        Assert.False(r.Excedido);
    }

    [Fact]
    public void NotaDeCreditoParcial_FacturaCompletaMenosLoAcreditado()
    {
        // NC parcial: la factura cuenta por su monto completo (100k) y la NC resta solo lo acreditado (30k).
        // Facturado neto = 70k. Antes (si la factura caia en Succeeded y se excluia) daba -30k.
        var r = ReservaInvoicingCuadreCalculator.Calculate(100_000m, new[]
        {
            new CuadreInvoiceLine(11, 100_000m, IsLive: ReservaInvoicingCuadreCalculator.CountsInNetBilled("A")),
            new CuadreInvoiceLine(13, 30_000m, IsLive: ReservaInvoicingCuadreCalculator.CountsInNetBilled("A")),
        });

        Assert.Equal(70_000m, r.FacturadoNeto);
        Assert.Equal(30_000m, r.Disponible);
        Assert.False(r.Excedido);
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

    // ============================================================================================
    // ADR-037 / cuadre POR MONEDA (2026-06-22): CalculatePerCurrency suma facturas + ND - NC vivas
    // dentro de cada moneda, sin mezclar. Helper local para lineas vivas con moneda.
    // ============================================================================================

    private static CuadreInvoiceLineByCurrency LiveCur(string moneda, int tipo, decimal importe)
        => new(moneda, tipo, importe, IsLive: true);

    [Fact]
    public void PorMoneda_SinComprobantes_DiccionarioVacio()
    {
        var result = ReservaInvoicingCuadreCalculator.CalculatePerCurrency(
            new List<CuadreInvoiceLineByCurrency>());

        Assert.Empty(result);
    }

    [Fact]
    public void PorMoneda_MonoMoneda_CoincideConElEscalar()
    {
        // Invariante: en mono-moneda, la suma por moneda == el FacturadoNeto escalar de hoy.
        var lines = new[]
        {
            LiveCur("ARS", 11, 100_000m), // Factura
            LiveCur("ARS", 12, 10_000m),  // ND suma
            LiveCur("ARS", 13, 30_000m),  // NC resta
        };

        var escalar = ReservaInvoicingCuadreCalculator.Calculate(
            80_000m,
            lines.Select(l => new CuadreInvoiceLine(l.TipoComprobante, l.ImporteTotal, l.IsLive)));

        var porMoneda = ReservaInvoicingCuadreCalculator.CalculatePerCurrency(lines);

        Assert.Equal(80_000m, escalar.FacturadoNeto); // 100k + 10k - 30k
        var ars = Assert.Single(porMoneda);
        Assert.Equal("ARS", ars.Key);
        Assert.Equal(escalar.FacturadoNeto, ars.Value);
    }

    [Fact]
    public void PorMoneda_Multimoneda_NoMezcla()
    {
        // Factura ARS + factura USD: cada una en su moneda, jamas sumadas entre si.
        var porMoneda = ReservaInvoicingCuadreCalculator.CalculatePerCurrency(new[]
        {
            LiveCur("ARS", 11, 50_000m),
            LiveCur("USD", 11, 300m),
        });

        Assert.Equal(2, porMoneda.Count);
        Assert.Equal(50_000m, porMoneda["ARS"]);
        Assert.Equal(300m, porMoneda["USD"]);
    }

    [Fact]
    public void PorMoneda_NC_RestaSoloEnSuMoneda()
    {
        // NC en USD baja SOLO el facturado USD; el ARS queda intacto.
        var porMoneda = ReservaInvoicingCuadreCalculator.CalculatePerCurrency(new[]
        {
            LiveCur("ARS", 11, 50_000m),
            LiveCur("USD", 11, 300m),
            LiveCur("USD", 13, 100m), // NC USD
        });

        Assert.Equal(50_000m, porMoneda["ARS"]);
        Assert.Equal(200m, porMoneda["USD"]); // 300 - 100
    }

    [Fact]
    public void PorMoneda_ComprobanteNoVivo_NoSuma_PeroRegistraLaMoneda()
    {
        // Un comprobante anulado aporta 0, pero deja registrada su moneda (hubo facturacion en ella).
        var porMoneda = ReservaInvoicingCuadreCalculator.CalculatePerCurrency(new[]
        {
            new CuadreInvoiceLineByCurrency("USD", 11, 300m, IsLive: false),
        });

        var usd = Assert.Single(porMoneda);
        Assert.Equal("USD", usd.Key);
        Assert.Equal(0m, usd.Value);
    }
}
