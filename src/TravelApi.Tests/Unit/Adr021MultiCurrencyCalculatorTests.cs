using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-021 Capa 2 (multimoneda, 2026-06-09): tests PUROS del calculo de plata por moneda.
///
/// <para>Cubre: (a) REGRESION mono-ARS identica al calculo escalar de siempre; (b) reserva con
/// servicios USD+ARS produce dos lineas con los montos correctos y el surrogate Balance correcto;
/// (c) pago cruzado (ARS contra saldo USD) imputa por ImputedAmount a la moneda USD; (d) el surrogate
/// no compensa deuda de una moneda con saldo a favor de otra.</para>
/// </summary>
public class Adr021MultiCurrencyCalculatorTests
{
    // ===================== (a) REGRESION mono-ARS: identico a hoy =====================

    [Fact]
    public void MonoArs_NoCurrencyOnServices_BehavesLikeLegacyScalar()
    {
        // Reserva 100% ARS (Currency null = ARS). Debe dar exactamente los mismos escalares que el
        // calculo viejo: una sola linea ARS, y los escalares = esa linea.
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 400m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.False(money.EsMultimoneda);
        Assert.Single(money.PorMoneda);
        Assert.True(money.PorMoneda.ContainsKey(Monedas.ARS));

        // Escalares de compat = la unica linea (identico a legacy).
        Assert.Equal(1000m, money.TotalSale);
        Assert.Equal(1000m, money.ConfirmedSale);
        Assert.Equal(700m, money.TotalCost);
        Assert.Equal(400m, money.TotalPaid);
        Assert.Equal(600m, money.Balance); // 1000 - 400

        var ars = money.PorMoneda[Monedas.ARS];
        Assert.Equal(1000m, ars.TotalSale);
        Assert.Equal(600m, ars.Balance);
    }

    [Fact]
    public void MonoArs_Overpaid_Surrogate_PreservesNegativeBalance_LikeLegacy()
    {
        // Sobrepago en una sola moneda: legacy daba Balance negativo (saldo a favor) y el gate usa
        // <= 0. La refinacion conservadora del surrogate preserva el negativo en mono-moneda (regla
        // de oro de regresion). Antes era confirmedSale(0 sin resolver) ... usamos un hotel resuelto.
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1500m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.False(money.EsMultimoneda);
        Assert.Equal(-500m, money.Balance); // 1000 - 1500, negativo preservado (identico a legacy)
    }

    // ===================== (b) Servicios USD + ARS: dos lineas =====================

    [Fact]
    public void MultiCurrency_UsdAndArsServices_ProduceTwoLines_WithCorrectAmounts()
    {
        var reserva = new Reserva();
        // Hotel ARS resuelto 1000/700; vuelo USD emitido 300/200 (TicketIssuedAt = resuelto).
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m });
        reserva.FlightSegments.Add(new FlightSegment
        {
            Status = "HK",
            Currency = "USD",
            TicketIssuedAt = System.DateTime.UtcNow, // resuelto -> entra a ConfirmedSale
            SalePrice = 300m,
            NetCost = 200m
        });
        // Pagos: 400 ARS al saldo ARS; 100 USD al saldo USD (no cruzados).
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 400m, Currency = "ARS" });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 100m, Currency = "USD" });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.True(money.EsMultimoneda);
        Assert.Equal(2, money.PorMoneda.Count);

        var ars = money.PorMoneda["ARS"];
        Assert.Equal(1000m, ars.ConfirmedSale);
        Assert.Equal(400m, ars.TotalPaid);
        Assert.Equal(600m, ars.Balance);

        var usd = money.PorMoneda["USD"];
        Assert.Equal(300m, usd.ConfirmedSale);
        Assert.Equal(100m, usd.TotalPaid);
        Assert.Equal(200m, usd.Balance);

        // Surrogate = suma de los positivos (ambas monedas deben): 600 + 200 = 800.
        Assert.Equal(800m, money.Balance);
    }

    // ===================== (c) Pago cruzado: imputa por ImputedAmount a USD =====================

    [Fact]
    public void CrossPayment_ArsAgainstUsdBalance_ImputesByImputedAmountToUsd()
    {
        var reserva = new Reserva();
        // Saldo USD: vuelo USD resuelto 300/200.
        reserva.FlightSegments.Add(new FlightSegment
        {
            Status = "HK",
            Currency = "USD",
            TicketIssuedAt = System.DateTime.UtcNow,
            SalePrice = 300m,
            NetCost = 200m
        });
        // Pago real ARS 100.000, imputado a USD con TC 1000 -> ImputedAmount = 100 USD.
        reserva.Payments.Add(new Payment
        {
            Status = "Paid",
            IsDeleted = false,
            Amount = 100000m,
            Currency = "ARS",
            ImputedCurrency = "USD",
            ExchangeRate = 1000m,
            ImputedAmount = 100m
        });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        // El saldo USD bajo por el EQUIVALENTE imputado (100 USD), NO por el Amount de caja (100.000 ARS).
        Assert.True(money.PorMoneda.ContainsKey("USD"));
        var usd = money.PorMoneda["USD"];
        Assert.Equal(300m, usd.ConfirmedSale);
        Assert.Equal(100m, usd.TotalPaid);     // ImputedAmount, no Amount
        Assert.Equal(200m, usd.Balance);       // 300 - 100

        // No se crea una linea ARS por el pago: el pago se imputa a USD, no a ARS.
        Assert.False(money.PorMoneda.ContainsKey("ARS"));
        Assert.Equal(200m, money.Balance);     // surrogate (una sola moneda con deuda)
    }

    // ===================== (d) Surrogate no compensa entre monedas =====================

    [Fact]
    public void Surrogate_OverpayInOneCurrency_DoesNotCancelDebtInAnother()
    {
        var reserva = new Reserva();
        // ARS sobrepagado (saldo a favor); USD con deuda.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 100m, NetCost = 0m });
        reserva.FlightSegments.Add(new FlightSegment
        {
            Status = "HK", Currency = "USD", TicketIssuedAt = System.DateTime.UtcNow, SalePrice = 500m, NetCost = 0m
        });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 300m, Currency = "ARS" }); // ARS a favor -200
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 100m, Currency = "USD" }); // USD debe 400

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(-200m, money.PorMoneda["ARS"].Balance); // saldo a favor ARS
        Assert.Equal(400m, money.PorMoneda["USD"].Balance);  // deuda USD

        // El surrogate NO compensa: el saldo a favor ARS (-200) no baja la deuda USD (400).
        // Solo cuentan los positivos -> surrogate = 400.
        Assert.Equal(400m, money.Balance);
    }

    // ===================== Eje proveedor: SupplierDebtCalculator =====================

    [Fact]
    public void SupplierDebt_MonoArs_IsIdenticalToLegacyScalar()
    {
        var purchases = new[]
        {
            new SupplierDebtCalculator.ConfirmedPurchase(null, 1000m),   // null = ARS
            new SupplierDebtCalculator.ConfirmedPurchase("ARS", 500m)
        };
        var payments = new[]
        {
            new SupplierDebtCalculator.SupplierPaymentInput(600m, "ARS", null, null)
        };

        var porMoneda = SupplierDebtCalculator.Calculate(purchases, payments);
        var surrogate = SupplierDebtCalculator.ToSurrogateBalance(porMoneda);

        Assert.Single(porMoneda);
        Assert.Equal(900m, porMoneda["ARS"].Balance); // 1500 - 600
        Assert.Equal(900m, surrogate);                 // mono-moneda = crudo, identico a legacy
    }

    [Fact]
    public void SupplierDebt_UsdAndArs_ProduceTwoLines_AndSurrogateSumsPositives()
    {
        var purchases = new[]
        {
            new SupplierDebtCalculator.ConfirmedPurchase("ARS", 1000m),
            new SupplierDebtCalculator.ConfirmedPurchase("USD", 500m)
        };
        var payments = new[]
        {
            new SupplierDebtCalculator.SupplierPaymentInput(400m, "ARS", null, null),
            // pago cruzado: salio en ARS, imputado a deuda USD por 100 USD equivalente.
            new SupplierDebtCalculator.SupplierPaymentInput(100000m, "ARS", "USD", 100m)
        };

        var porMoneda = SupplierDebtCalculator.Calculate(purchases, payments);

        Assert.Equal(600m, porMoneda["ARS"].Balance); // 1000 - 400
        Assert.Equal(400m, porMoneda["USD"].Balance); // 500 - 100 (imputado, no caja)
        Assert.Equal(1000m, SupplierDebtCalculator.ToSurrogateBalance(porMoneda));
    }

    [Fact]
    public void SupplierDebt_Empty_IsZero()
    {
        var porMoneda = SupplierDebtCalculator.Calculate(
            System.Array.Empty<SupplierDebtCalculator.ConfirmedPurchase>(),
            System.Array.Empty<SupplierDebtCalculator.SupplierPaymentInput>());

        Assert.Empty(porMoneda);
        Assert.Equal(0m, SupplierDebtCalculator.ToSurrogateBalance(porMoneda));
    }
}
