using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// DISEÑO 1 (Estado de Cuenta como libro mayor): cobertura PURA del builder que arma el extracto estilo
/// banco. El builder recibe lineas planas YA clasificadas (factura/ND = cargo, cobro/NC = abono) y calcula
/// el saldo corriente por moneda. No toca EF: estos tests no necesitan Postgres.
///
/// <para>Donde aplica, los tests recrean el MISMO escenario con <see cref="ReservaMoneyCalculator"/> para
/// pinear el invariante "ClosingBalance por moneda == PorMoneda[moneda].Balance" cuando lo facturado iguala
/// lo confirmado (que es como se construyen estos casos).</para>
/// </summary>
public class ReservaAccountStatementBuilderTests
{
    // Helpers para armar lineas de entrada de forma legible. SourcePublicId opcional: la mayoria de los casos
    // no lo necesita (testean orden/saldo); el test dedicado lo pasa para verificar que se arrastra hasta el
    // resultado.
    private static AccountStatementInputLine Invoice(DateTime date, string currency, decimal amount, string? doc = null, Guid? sourceId = null)
        => new(date, AccountStatementLineKinds.Invoice, "Factura", doc, currency, Charge: amount, Credit: 0m, SourcePublicId: sourceId);

    private static AccountStatementInputLine CreditNote(DateTime date, string currency, decimal amount, Guid? sourceId = null)
        => new(date, AccountStatementLineKinds.CreditNote, "Nota de crédito", null, currency, Charge: 0m, Credit: amount, SourcePublicId: sourceId);

    private static AccountStatementInputLine DebitNote(DateTime date, string currency, decimal amount, Guid? sourceId = null)
        => new(date, AccountStatementLineKinds.DebitNote, "Nota de débito", null, currency, Charge: amount, Credit: 0m, SourcePublicId: sourceId);

    private static AccountStatementInputLine Payment(DateTime date, string currency, decimal amount, Guid? sourceId = null)
        => new(date, AccountStatementLineKinds.Payment, "Cobro", null, currency, Charge: 0m, Credit: amount, SourcePublicId: sourceId);

    private static AccountStatementCurrencyBlock SingleBlock(ReservaAccountStatement statement)
        => Assert.Single(statement.Currencies);

    // ===================== Mono-moneda: factura + 2 cobros + NC =====================

    [Fact]
    public void MonoCurrency_Factura2CobrosYNc_OrdenSignosYSaldoCorriente()
    {
        var d = new DateTime(2026, 1, 1);

        // Llegan desordenadas a proposito: el builder debe ordenarlas por fecha.
        var lines = new[]
        {
            Payment(d.AddDays(3), "ARS", 30000m),    // abono
            Invoice(d.AddDays(1), "ARS", 100000m),   // cargo
            CreditNote(d.AddDays(4), "ARS", 20000m), // abono (NC)
            Payment(d.AddDays(2), "ARS", 50000m),    // abono
        };

        var statement = ReservaAccountStatementBuilder.Build(lines);
        var block = SingleBlock(statement);

        Assert.Equal("ARS", block.Currency);
        Assert.Equal(4, block.Lines.Count);

        // Orden cronologico: Factura, Cobro 50k, Cobro 30k, NC 20k.
        Assert.Equal(AccountStatementLineKinds.Invoice, block.Lines[0].Kind);
        Assert.Equal(100000m, block.Lines[0].Charge);
        Assert.Equal(100000m, block.Lines[0].RunningBalance); // 100k

        Assert.Equal(50000m, block.Lines[1].Credit);
        Assert.Equal(50000m, block.Lines[1].RunningBalance);  // 100k - 50k

        Assert.Equal(30000m, block.Lines[2].Credit);
        Assert.Equal(20000m, block.Lines[2].RunningBalance);  // 50k - 30k

        Assert.Equal(AccountStatementLineKinds.CreditNote, block.Lines[3].Kind);
        Assert.Equal(20000m, block.Lines[3].Credit);
        Assert.Equal(0m, block.Lines[3].RunningBalance);      // 20k - 20k = 0

        // Saldo de cierre = ultimo running balance.
        Assert.Equal(0m, block.ClosingBalance);
    }

    // ===================== Invariante ClosingBalance == PorMoneda[moneda].Balance =====================

    [Fact]
    public void ClosingBalance_CuadraConBalanceDeLaReserva_MonoCurrency()
    {
        // Reserva con UN servicio resuelto (venta confirmada = 100k) y un cobro de 70k -> Balance = 30k.
        var reserva = new Reserva
        {
            Servicios = new List<ServicioReserva>
            {
                new() { Status = "Confirmado", SalePrice = 100000m, NetCost = 60000m, Currency = "ARS" },
            },
            Payments = new List<Payment>
            {
                new() { Amount = 70000m, Currency = "ARS", Status = "Paid", AffectsCash = true },
            },
        };
        var money = ReservaMoneyCalculator.Calculate(reserva);
        decimal balanceArs = money.PorMoneda["ARS"].Balance;
        Assert.Equal(30000m, balanceArs);

        // Extracto del MISMO escenario: factura 100k (= venta confirmada facturada) + cobro 70k.
        var d = new DateTime(2026, 2, 1);
        var statement = ReservaAccountStatementBuilder.Build(new[]
        {
            Invoice(d, "ARS", 100000m),
            Payment(d.AddDays(1), "ARS", 70000m),
        });
        var block = SingleBlock(statement);

        // Invariante: el saldo de cierre del extracto coincide con el Balance de la reserva.
        Assert.Equal(balanceArs, block.ClosingBalance);
        Assert.Equal(30000m, block.ClosingBalance);
    }

    // ===================== Multimoneda: 2 bloques, cada uno cuadra =====================

    [Fact]
    public void MultiCurrency_DosBloques_CadaUnoConSuSaldo()
    {
        var d = new DateTime(2026, 3, 1);
        var lines = new[]
        {
            Invoice(d, "ARS", 80000m),
            Payment(d.AddDays(1), "ARS", 80000m),  // ARS queda en 0
            Invoice(d, "USD", 1000m),
            Payment(d.AddDays(1), "USD", 600m),    // USD queda en 400
        };

        var statement = ReservaAccountStatementBuilder.Build(lines);

        Assert.Equal(2, statement.Currencies.Count);
        // Orden alfabetico estable: ARS primero, USD despues.
        Assert.Equal("ARS", statement.Currencies[0].Currency);
        Assert.Equal("USD", statement.Currencies[1].Currency);

        Assert.Equal(0m, statement.Currencies[0].ClosingBalance);
        Assert.Equal(400m, statement.Currencies[1].ClosingBalance);

        // Cada bloque solo tiene sus propias lineas (no se mezclan).
        Assert.All(statement.Currencies[0].Lines, line => Assert.Equal("ARS", line.Currency));
        Assert.All(statement.Currencies[1].Lines, line => Assert.Equal("USD", line.Currency));
    }

    // ===================== Cobro cruzado USD->ARS cae en bloque ARS y cuadra =====================

    [Fact]
    public void CrossCurrencyPayment_CaeEnBloqueImputado_CuadraConBalance()
    {
        // Reserva en ARS (venta confirmada 100k). Cobro CRUZADO: entro 50 USD, imputado a ARS como 50000.
        var reserva = new Reserva
        {
            Servicios = new List<ServicioReserva>
            {
                new() { Status = "Confirmado", SalePrice = 100000m, NetCost = 0m, Currency = "ARS" },
            },
            Payments = new List<Payment>
            {
                new()
                {
                    Amount = 50m, Currency = "USD",
                    ImputedCurrency = "ARS", ImputedAmount = 50000m,
                    Status = "Paid", AffectsCash = true,
                },
            },
        };
        var money = ReservaMoneyCalculator.Calculate(reserva);
        // El cobro cruzado baja la deuda ARS por su ImputedAmount -> Balance ARS = 100k - 50k = 50k.
        Assert.Equal(50000m, money.PorMoneda["ARS"].Balance);
        // No se crea un bloque USD por el pago cruzado (cae en la moneda imputada).
        Assert.False(money.PorMoneda.ContainsKey("USD"));

        // Extracto del MISMO escenario: la linea del cobro cruzado cae en el bloque ARS por su ImputedAmount.
        var d = new DateTime(2026, 4, 1);
        var statement = ReservaAccountStatementBuilder.Build(new[]
        {
            Invoice(d, "ARS", 100000m),
            // El mapper imputa moneda+monto: aca simulamos la linea ya imputada a ARS por 50000.
            Payment(d.AddDays(1), "ARS", 50000m),
        });
        var block = SingleBlock(statement);

        Assert.Equal("ARS", block.Currency);
        Assert.Equal(money.PorMoneda["ARS"].Balance, block.ClosingBalance);
        Assert.Equal(50000m, block.ClosingBalance);
    }

    // ===================== ND carga, NC abona =====================

    [Fact]
    public void DebitNoteCarga_CreditNoteAbona()
    {
        var d = new DateTime(2026, 5, 1);
        var statement = ReservaAccountStatementBuilder.Build(new[]
        {
            Invoice(d, "ARS", 100000m),       // +100k
            DebitNote(d.AddDays(1), "ARS", 5000m),  // +5k (penalidad/ajuste) -> 105k
            CreditNote(d.AddDays(2), "ARS", 30000m),// -30k -> 75k
        });
        var block = SingleBlock(statement);

        Assert.Equal(105000m, block.Lines[1].RunningBalance);
        Assert.Equal(75000m, block.Lines[2].RunningBalance);
        Assert.Equal(75000m, block.ClosingBalance);
    }

    // ===================== Empate de fecha: orden estable de entrada =====================

    [Fact]
    public void SameDate_PreservesInputOrder_Stable()
    {
        var d = new DateTime(2026, 6, 1);
        // Factura y cobro el MISMO dia: el builder respeta el orden de entrada (factura primero).
        var statement = ReservaAccountStatementBuilder.Build(new[]
        {
            Invoice(d, "ARS", 100000m),
            Payment(d, "ARS", 40000m),
        });
        var block = SingleBlock(statement);

        Assert.Equal(AccountStatementLineKinds.Invoice, block.Lines[0].Kind);
        Assert.Equal(100000m, block.Lines[0].RunningBalance);
        Assert.Equal(60000m, block.Lines[1].RunningBalance);
    }

    // ===================== Vacio: sin lineas, sin bloques =====================

    [Fact]
    public void EmptyInput_NoBlocks()
    {
        var statement = ReservaAccountStatementBuilder.Build(Array.Empty<AccountStatementInputLine>());
        Assert.Empty(statement.Currencies);
    }

    // ===================== SourcePublicId se arrastra hasta el resultado, por linea =====================

    [Fact]
    public void SourcePublicId_IsCarriedThrough_PerLine()
    {
        var d = new DateTime(2026, 7, 1);
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var creditNoteId = Guid.NewGuid();

        // Cada linea de entrada trae el PublicId de su documento de origen. El builder no lo interpreta:
        // solo debe llevarlo tal cual a la linea de resultado correcta.
        var statement = ReservaAccountStatementBuilder.Build(new[]
        {
            Invoice(d, "ARS", 100000m, sourceId: invoiceId),
            Payment(d.AddDays(1), "ARS", 40000m, sourceId: paymentId),
            CreditNote(d.AddDays(2), "ARS", 10000m, sourceId: creditNoteId),
        });
        var block = SingleBlock(statement);

        Assert.Equal(invoiceId, block.Lines[0].SourcePublicId);
        Assert.Equal(paymentId, block.Lines[1].SourcePublicId);
        Assert.Equal(creditNoteId, block.Lines[2].SourcePublicId);
    }

    // ===================== Contrato: la linea no transporta costo ni margen =====================

    [Fact]
    public void StatementLine_HasNoCostOrMarginFields_ContractGuard()
    {
        // Guard de contrato: la linea del extracto SOLO expone venta/cobranza. Si alguien agrega un campo de
        // costo/margen al tipo, este test rompe y obliga a revisar el enmascarado (el extracto es PURO).
        var props = typeof(AccountStatementResultLine).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Cost", props);
        Assert.DoesNotContain("NetCost", props);
        Assert.DoesNotContain("TotalCost", props);
        Assert.DoesNotContain("Margin", props);
        Assert.DoesNotContain("Commission", props);
    }
}
