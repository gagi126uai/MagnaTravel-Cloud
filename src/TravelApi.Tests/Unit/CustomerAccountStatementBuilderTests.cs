using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tests PUROS (sin base de datos) del constructor del extracto del cliente. Cubre puntualmente el cambio de
/// la Tanda D2 (extracto profesional, 2026-07-16): el "Saldo" que se muestra en cada renglon pasa a ser el
/// saldo TOTAL de la moneda en ese instante (estilo banco, sumando todas las reservas abiertas), no el saldo
/// aislado de la reserva de esa linea puntual. Antes de esta tanda, un extracto con dos reservas mostraba un
/// numero que "saltaba" entre expedientes y no se leia como un extracto de verdad (el pedido original de
/// Gastón: "no parece un extracto de verdad").
/// </summary>
public class CustomerAccountStatementBuilderTests
{
    private static CustomerAccountStatementInputLine Line(
        DateTime date, string kind, Guid reservaPublicId, string numeroReserva,
        decimal charge = 0m, decimal credit = 0m, string currency = "ARS")
        => new(
            Date: date,
            Kind: kind,
            Description: kind,
            DocumentRef: null,
            ReservaPublicId: reservaPublicId,
            NumeroReserva: numeroReserva,
            Currency: currency,
            Charge: charge,
            Credit: credit,
            SourcePublicId: null);

    [Fact]
    public void RunningBalance_is_the_total_open_debt_of_the_currency_not_just_this_reserva()
    {
        // Reproduce el mockup de la spec UX (docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md §1):
        // dos reservas (R-1050 y R-1042) en la MISMA moneda. Cada renglon debe mostrar el saldo TOTAL abierto
        // de la moneda hasta ese momento, no el saldo aislado de la reserva de esa linea.
        var r1050 = Guid.NewGuid();
        var r1042 = Guid.NewGuid();

        var lines = new List<CustomerAccountStatementInputLine>
        {
            Line(new DateTime(2026, 5, 20), CustomerAccountStatementLineKinds.Invoice, r1050, "R-1050", charge: 90000m),
            Line(new DateTime(2026, 5, 21), CustomerAccountStatementLineKinds.CreditNote, r1050, "R-1050", credit: 90000m),
            Line(new DateTime(2026, 6, 2), CustomerAccountStatementLineKinds.Invoice, r1042, "R-1042", charge: 180000m),
            Line(new DateTime(2026, 6, 10), CustomerAccountStatementLineKinds.Payment, r1042, "R-1042", credit: 100000m),
            Line(new DateTime(2026, 6, 15), CustomerAccountStatementLineKinds.DebitNote, r1050, "R-1050", charge: 20000m),
        };

        var statement = CustomerAccountStatementBuilder.Build(lines);
        var ars = Assert.Single(statement.Currencies);
        Assert.Equal(5, ars.Lines.Count);

        // Factura R-1050 90.000 -> saldo total 90.000 (todavia no hay nada de R-1042).
        Assert.Equal(90000m, ars.Lines[0].RunningBalance);
        // NC de la anulacion de R-1050 -> el contra-asiento la cancela, saldo total vuelve a 0.
        Assert.Equal(0m, ars.Lines[1].RunningBalance);
        // Factura R-1042 180.000 -> saldo total 180.000 (R-1050 sigue en 0).
        Assert.Equal(180000m, ars.Lines[2].RunningBalance);
        // Cobro parcial de R-1042 -> saldo total 80.000.
        Assert.Equal(80000m, ars.Lines[3].RunningBalance);
        // Multa (ND) de R-1050: su saldo propio pasa a 20.000, pero el saldo TOTAL mostrado suma ademas los
        // 80.000 que sigue debiendo R-1042 -> 100.000. Este es el numero que antes de la Tanda D2 daba mal
        // (mostraba 20.000, el saldo aislado de R-1050, no el total del extracto).
        Assert.Equal(100000m, ars.Lines[4].RunningBalance);

        Assert.Equal(100000m, ars.ClosingBalance);
    }

    [Fact]
    public void Closing_balance_always_equals_the_running_balance_of_the_last_line()
    {
        // Invariante de diseño (documentada en el propio dominio): con el saldo corrido GLOBAL, el cierre de
        // la moneda coincide SIEMPRE con lo que el cliente vio en el ultimo renglon. Antes de la Tanda D2 esto
        // se rompia apenas habia mas de una reserva en la misma moneda (el running balance era por reserva).
        var reservaA = Guid.NewGuid();
        var reservaB = Guid.NewGuid();
        var lines = new List<CustomerAccountStatementInputLine>
        {
            Line(new DateTime(2026, 1, 1), CustomerAccountStatementLineKinds.Invoice, reservaA, "R-A", charge: 500m),
            Line(new DateTime(2026, 1, 5), CustomerAccountStatementLineKinds.Invoice, reservaB, "R-B", charge: 300m),
            Line(new DateTime(2026, 1, 10), CustomerAccountStatementLineKinds.Payment, reservaA, "R-A", credit: 200m),
        };

        var statement = CustomerAccountStatementBuilder.Build(lines);
        var ars = Assert.Single(statement.Currencies);

        Assert.Equal(ars.Lines[^1].RunningBalance, ars.ClosingBalance);
        Assert.Equal(600m, ars.ClosingBalance); // (500-200) de R-A + 300 de R-B
    }

    [Fact]
    public void Unapplied_credit_of_one_reserva_never_reduces_the_shown_balance_of_another()
    {
        // R-CREDIT queda con saldo NEGATIVO (cobro sin venta que lo justifique todavia). Ese credito no
        // aplicado NUNCA debe restar del saldo mostrado de R-DEBT: el open item de cada reserva es
        // independiente hasta que haya una aplicacion EXPLICITA (regla de negocio ya vigente, ahora tambien
        // valida linea a linea, no solo en el ClosingBalance).
        var reservaDebt = Guid.NewGuid();
        var reservaCredit = Guid.NewGuid();
        var lines = new List<CustomerAccountStatementInputLine>
        {
            Line(new DateTime(2026, 1, 1), CustomerAccountStatementLineKinds.Invoice, reservaDebt, "R-DEBT", charge: 1000m),
            Line(new DateTime(2026, 1, 2), CustomerAccountStatementLineKinds.Payment, reservaCredit, "R-CREDIT", credit: 400m),
        };

        var statement = CustomerAccountStatementBuilder.Build(lines);
        var ars = Assert.Single(statement.Currencies);

        Assert.Equal(1000m, ars.Lines[0].RunningBalance);
        // El cobro de R-CREDIT no tiene venta que compensar: no debe hacer bajar el saldo mostrado de R-DEBT.
        Assert.Equal(1000m, ars.Lines[1].RunningBalance);
        Assert.Equal(1000m, ars.ClosingBalance);
        Assert.Equal(400m, ars.UnappliedCredit);
    }

    [Fact]
    public void Single_reserva_scenario_keeps_previous_behavior()
    {
        // Caso base (una sola reserva en la moneda): el saldo corrido global coincide, linea a linea, con lo
        // que ya devolvia el builder antes de la Tanda D2. Ningun cliente con reservas simples deberia ver un
        // numero distinto al de siempre.
        var reserva = Guid.NewGuid();
        var lines = new List<CustomerAccountStatementInputLine>
        {
            Line(new DateTime(2026, 1, 1), CustomerAccountStatementLineKinds.Invoice, reserva, "R-1", charge: 1000m),
            Line(new DateTime(2026, 1, 15), CustomerAccountStatementLineKinds.Payment, reserva, "R-1", credit: 400m),
        };

        var statement = CustomerAccountStatementBuilder.Build(lines);
        var ars = Assert.Single(statement.Currencies);

        Assert.Equal(1000m, ars.Lines[0].RunningBalance);
        Assert.Equal(600m, ars.Lines[1].RunningBalance);
        Assert.Equal(600m, ars.ClosingBalance);
    }
}
