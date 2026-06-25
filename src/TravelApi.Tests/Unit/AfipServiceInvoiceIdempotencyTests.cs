using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tests UNIT (sin DB, sin HTTP a ARCA) de la idempotencia anti-doble-CAE que se le agrego a
/// <see cref="AfipService.ProcessInvoiceJob"/> (2026-06-25). El job real toca ARCA por HTTP, asi
/// que aca probamos las DOS piezas puras y deterministicas del mecanismo:
///
/// <list type="number">
///   <item><see cref="AfipService.BuildInvoiceIdempotencyKey"/>: la clave que registra el job
///   ANTES del POST. Tiene que ser deterministica (mismo invoiceId => misma key, para que un
///   re-despacho choque con el indice UNIQUE) y distinta entre facturas distintas.</item>
///   <item><see cref="AfipService.ArcaResultMatchesInvoice"/>: la decision del recovery. Cuando
///   un re-despacho consulta ARCA, este metodo decide si el comprobante que ARCA reporta
///   corresponde a ESTA factura (y por lo tanto hay que adoptar su CAE en vez de re-emitir) o
///   no (y hay que reintentar limpio). Es el corazon fiscal del anti-duplicado.</item>
/// </list>
///
/// <para>Lo que requiere ARCA real o Postgres (el POST efectivo, el choque del INSERT UNIQUE, el
/// corto-circuito completo al re-despachar, el manejo de "Observado") queda para los tests de
/// Integration — InMemory no aplica indices UNIQUE ni habla SOAP.</para>
/// </summary>
public class AfipServiceInvoiceIdempotencyTests
{
    // Codigos de tipo de comprobante ARCA usados en los tests.
    private const int FacturaA = 1;
    private const int FacturaB = 6;
    private const int NotaCreditoA = 3;  // NC total sobre Factura A
    private const int NotaDebitoA = 2;   // ND sobre Factura A
    private const int PuntoDeVenta = 7;

    // ------------------------------------------------------------------------------------
    // BuildInvoiceIdempotencyKey: determinismo + unicidad.
    // ------------------------------------------------------------------------------------

    [Fact]
    public void BuildInvoiceIdempotencyKey_SameInvoice_ProducesSameKey()
    {
        // Dos despachos del MISMO job (mismo invoiceId/tipo/punto de venta) deben producir
        // EXACTAMENTE la misma key: es lo que hace que el segundo INSERT choque con el UNIQUE.
        string first = AfipService.BuildInvoiceIdempotencyKey(invoiceId: 42, tipoComprobante: FacturaA, puntoDeVenta: PuntoDeVenta);
        string second = AfipService.BuildInvoiceIdempotencyKey(invoiceId: 42, tipoComprobante: FacturaA, puntoDeVenta: PuntoDeVenta);

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildInvoiceIdempotencyKey_DifferentInvoices_ProduceDifferentKeys()
    {
        string invoice42 = AfipService.BuildInvoiceIdempotencyKey(invoiceId: 42, tipoComprobante: FacturaA, puntoDeVenta: PuntoDeVenta);
        string invoice43 = AfipService.BuildInvoiceIdempotencyKey(invoiceId: 43, tipoComprobante: FacturaA, puntoDeVenta: PuntoDeVenta);

        Assert.NotEqual(invoice42, invoice43);
    }

    [Fact]
    public void BuildInvoiceIdempotencyKey_IsSha256Hex_64Chars()
    {
        // La columna Key es varchar(64). SHA256 en hex son 64 chars exactos en minuscula.
        string key = AfipService.BuildInvoiceIdempotencyKey(invoiceId: 1, tipoComprobante: FacturaA, puntoDeVenta: PuntoDeVenta);

        Assert.Equal(64, key.Length);
        Assert.Equal(key, key.ToLowerInvariant());
        Assert.All(key, c => Assert.True(Uri.IsHexDigit(c)));
    }

    // ------------------------------------------------------------------------------------
    // ArcaResultMatchesInvoice: factura de venta (sin comprobante asociado).
    // Match por monto + moneda.
    // ------------------------------------------------------------------------------------

    private static Invoice SaleInvoice(decimal importeTotal, string monId = "PES")
        => new()
        {
            Id = 100,
            TipoComprobante = FacturaB,
            ImporteTotal = importeTotal,
            MonId = monId,
            // Factura de venta: NO tiene comprobante asociado.
            OriginalInvoiceId = null,
            OriginalInvoice = null,
        };

    private static ArcaCompoundQueryResult ArcaFound(
        decimal? importeTotal,
        int? cbteAsoc = null,
        string? monId = "PES",
        int? lastNumero = 5)
        => new(
            Found: true,
            LastNumero: lastNumero,
            Cae: "70000000000001",
            CbteAsoc: cbteAsoc,
            IssuedAt: DateTime.UtcNow,
            ImporteTotal: importeTotal,
            MonId: monId,
            MonCotiz: 1m);

    [Fact]
    public void ArcaResultMatchesInvoice_SaleInvoice_AmountAndCurrencyMatch_ReturnsTrue()
    {
        var invoice = SaleInvoice(importeTotal: 121_000m, monId: "PES");
        var arca = ArcaFound(importeTotal: 121_000m, monId: "PES");

        Assert.True(AfipService.ArcaResultMatchesInvoice(arca, invoice, roundingTolerance: 0.01m));
    }

    [Fact]
    public void ArcaResultMatchesInvoice_SaleInvoice_AmountMismatch_ReturnsFalse()
    {
        // ARCA reporta otro monto: NO es nuestra factura -> no adoptar su CAE (reintentar limpio).
        var invoice = SaleInvoice(importeTotal: 121_000m, monId: "PES");
        var arca = ArcaFound(importeTotal: 999_000m, monId: "PES");

        Assert.False(AfipService.ArcaResultMatchesInvoice(arca, invoice, roundingTolerance: 0.01m));
    }

    [Fact]
    public void ArcaResultMatchesInvoice_SaleInvoice_CurrencyMismatch_ReturnsFalse()
    {
        // Mismo monto pero distinta moneda: no es la misma operacion (una factura USD no es la
        // misma que una en pesos del mismo importe nominal).
        var invoice = SaleInvoice(importeTotal: 1_000m, monId: "DOL");
        var arca = ArcaFound(importeTotal: 1_000m, monId: "PES");

        Assert.False(AfipService.ArcaResultMatchesInvoice(arca, invoice, roundingTolerance: 0.01m));
    }

    [Fact]
    public void ArcaResultMatchesInvoice_SaleInvoice_AmountWithinTolerance_ReturnsTrue()
    {
        // Diferencia de 1 centavo, dentro de la tolerancia de redondeo: sigue siendo match.
        var invoice = SaleInvoice(importeTotal: 121_000.00m, monId: "PES");
        var arca = ArcaFound(importeTotal: 121_000.01m, monId: "PES");

        Assert.True(AfipService.ArcaResultMatchesInvoice(arca, invoice, roundingTolerance: 0.01m));
    }

    [Fact]
    public void ArcaResultMatchesInvoice_SaleInvoice_ArcaWithoutCurrency_MatchesByAmountOnly()
    {
        // Degradacion segura: si ARCA no reporto moneda, no la contradecimos. Aceptamos por monto
        // (que ya es exacto y el numerador avanzo).
        var invoice = SaleInvoice(importeTotal: 121_000m, monId: "DOL");
        var arca = ArcaFound(importeTotal: 121_000m, monId: null);

        Assert.True(AfipService.ArcaResultMatchesInvoice(arca, invoice, roundingTolerance: 0.01m));
    }

    // ------------------------------------------------------------------------------------
    // ArcaResultMatchesInvoice: NC total y ND (con comprobante asociado).
    // Match fuerte por CbteAsoc + monto.
    // ------------------------------------------------------------------------------------

    private static Invoice AssociatedInvoice(int tipoComprobante, decimal importeTotal, long originalNumero)
        => new()
        {
            Id = 200,
            TipoComprobante = tipoComprobante,
            ImporteTotal = importeTotal,
            MonId = "PES",
            OriginalInvoiceId = 50,
            OriginalInvoice = new Invoice
            {
                Id = 50,
                TipoComprobante = FacturaA,
                NumeroComprobante = originalNumero,
            },
        };

    [Fact]
    public void ArcaResultMatchesInvoice_CreditNote_CbteAsocAndAmountMatch_ReturnsTrue()
    {
        // NC total que apunta a la factura origen Nro 1234. ARCA reporta un comprobante cuyo
        // CbteAsoc == 1234 y mismo monto -> es NUESTRA NC: adoptar el CAE (no re-emitir).
        var nc = AssociatedInvoice(NotaCreditoA, importeTotal: 121_000m, originalNumero: 1234);
        var arca = ArcaFound(importeTotal: 121_000m, cbteAsoc: 1234);

        Assert.True(AfipService.ArcaResultMatchesInvoice(arca, nc, roundingTolerance: 0.01m));
    }

    [Fact]
    public void ArcaResultMatchesInvoice_DebitNote_CbteAsocAndAmountMatch_ReturnsTrue()
    {
        var nd = AssociatedInvoice(NotaDebitoA, importeTotal: 5_000m, originalNumero: 1234);
        var arca = ArcaFound(importeTotal: 5_000m, cbteAsoc: 1234);

        Assert.True(AfipService.ArcaResultMatchesInvoice(arca, nd, roundingTolerance: 0.01m));
    }

    [Fact]
    public void ArcaResultMatchesInvoice_CreditNote_CbteAsocPointsElsewhere_ReturnsFalse()
    {
        // CRITICO (anti-doble-CAE de otra NC del mismo monto): el comprobante de ARCA tiene el
        // mismo monto pero apunta a OTRA factura origen (Nro 9999, no 1234). NO es nuestra NC:
        // adoptar ese CAE seria robarle el comprobante a otra operacion. Debe dar false.
        var nc = AssociatedInvoice(NotaCreditoA, importeTotal: 121_000m, originalNumero: 1234);
        var arca = ArcaFound(importeTotal: 121_000m, cbteAsoc: 9999);

        Assert.False(AfipService.ArcaResultMatchesInvoice(arca, nc, roundingTolerance: 0.01m));
    }

    [Fact]
    public void ArcaResultMatchesInvoice_CreditNote_AmountMismatch_ReturnsFalse()
    {
        // Apunta a nuestra factura origen pero el monto no cuadra: tratar como mismatch.
        var nc = AssociatedInvoice(NotaCreditoA, importeTotal: 121_000m, originalNumero: 1234);
        var arca = ArcaFound(importeTotal: 50_000m, cbteAsoc: 1234);

        Assert.False(AfipService.ArcaResultMatchesInvoice(arca, nc, roundingTolerance: 0.01m));
    }

    // ------------------------------------------------------------------------------------
    // ArcaResultMatchesInvoice: Found=false (el POST nunca viajo).
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ArcaResultMatchesInvoice_ArcaNotFound_ReturnsFalse()
    {
        // El numerador de ARCA no avanzo: el POST de la corrida anterior nunca llego. Hay que
        // reintentar limpio (false), no adoptar nada.
        var invoice = SaleInvoice(importeTotal: 121_000m);
        var arcaNotFound = new ArcaCompoundQueryResult(
            Found: false,
            LastNumero: 4,
            Cae: null,
            CbteAsoc: null,
            IssuedAt: null,
            ImporteTotal: null,
            MonId: null,
            MonCotiz: null);

        Assert.False(AfipService.ArcaResultMatchesInvoice(arcaNotFound, invoice, roundingTolerance: 0.01m));
    }
}
