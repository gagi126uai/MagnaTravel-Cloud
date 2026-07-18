using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-048 T5 (2026-07-17, hardening — materializacion de los ejes secundarios): prueba de
/// EQUIVALENCIA. El objetivo NO es re-probar las reglas de <c>ReservaCollectionStatus</c> /
/// <c>ReservaInvoicingStatus</c> (eso ya lo cubren sus propios tests) sino demostrar que
/// <see cref="ReservaDerivedAxesProjector"/> — el codigo que corre DENTRO de
/// <c>ReservaMoneyPersister</c> y escribe las columnas materializadas — devuelve EXACTAMENTE lo
/// mismo que calcularia el detalle/listado EN VIVO para el mismo dato. Si algun dia alguien cambia
/// el wiring del proyector sin tocar las reglas puras, este test lo agarra.
/// </summary>
public class ReservaDerivedAxesProjectorTests
{
    // ================================================================================================
    // Eje de COBRO
    // ================================================================================================

    [Fact]
    public void ProjectCollectionStatus_ReservaSinServiciosNiPagos_SinMovimientos()
    {
        var reserva = new Reserva();
        var summary = ReservaMoneyCalculator.Calculate(reserva);

        var materializado = ReservaDerivedAxesProjector.ProjectCollectionStatus(summary);

        Assert.Equal(ReservaCollectionStatus.NoCharges, materializado);
    }

    [Fact]
    public void ProjectCollectionStatus_ServicioResueltoSinPago_ConDeuda()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 700m });

        var summary = ReservaMoneyCalculator.Calculate(reserva);
        var materializado = ReservaDerivedAxesProjector.ProjectCollectionStatus(summary);

        Assert.Equal(ReservaCollectionStatus.WithDebt, materializado);
    }

    [Fact]
    public void ProjectCollectionStatus_ServicioResueltoPagadoEntero_Saldado()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Amount = 1000m, Status = "Confirmed", IsDeleted = false });

        var summary = ReservaMoneyCalculator.Calculate(reserva);
        var materializado = ReservaDerivedAxesProjector.ProjectCollectionStatus(summary);

        Assert.Equal(ReservaCollectionStatus.Settled, materializado);
    }

    [Fact]
    public void ProjectCollectionStatus_Sobrepago_SaldoAFavor()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Amount = 1500m, Status = "Confirmed", IsDeleted = false });

        var summary = ReservaMoneyCalculator.Calculate(reserva);
        var materializado = ReservaDerivedAxesProjector.ProjectCollectionStatus(summary);

        Assert.Equal(ReservaCollectionStatus.CreditBalance, materializado);
    }

    /// <summary>
    /// EQUIVALENCIA (multimoneda): arma una reserva con una moneda en deuda (ARS) y otra con saldo a
    /// favor (USD) y verifica que <see cref="ReservaDerivedAxesProjector.ProjectCollectionStatus"/>
    /// coincide EXACTO con llamar a <see cref="ReservaCollectionStatus.Derive(System.Collections.Generic.IEnumerable{ReservaCollectionLine})"/>
    /// a mano con el mismo detalle por moneda (la MISMA cuenta que hace el detalle en
    /// <c>ReservaService.GetReservaByIdAsync</c>). "ConDeuda" gana sobre "SaldoAFavor" cuando hay ambas.
    /// </summary>
    [Fact]
    public void ProjectCollectionStatus_Multimoneda_CoincideConLaDerivacionEnVivoDelDetalle()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking
        {
            Status = "Confirmado", SalePrice = 1000m, NetCost = 700m, Currency = "ARS"
        });
        reserva.FlightSegments.Add(new FlightSegment
        {
            Status = "HK", SalePrice = 500m, NetCost = 300m, Currency = "USD"
        });
        // El USD queda con saldo a favor (se pago de mas); el ARS queda en deuda.
        reserva.Payments.Add(new Payment { Amount = 700m, Currency = "USD", ImputedCurrency = "USD", Status = "Confirmed", IsDeleted = false });

        var summary = ReservaMoneyCalculator.Calculate(reserva);

        var materializado = ReservaDerivedAxesProjector.ProjectCollectionStatus(summary);

        // Misma cuenta que hace el detalle EN VIVO (ReservaService.cs, dto.CollectionStatus = ...).
        var enVivo = ReservaCollectionStatus.Derive(
            summary.PorMoneda.Values.Select(line => new ReservaCollectionLine(
                line.Balance,
                hasCharges: line.ConfirmedSale > 0m,
                hasPayments: line.TotalPaid > 0m)));

        Assert.Equal(enVivo, materializado);
        Assert.Equal(ReservaCollectionStatus.WithDebt, materializado);
    }

    // ================================================================================================
    // Eje de FACTURACION
    // ================================================================================================

    [Fact]
    public void ProjectInvoicingStatus_SinComprobantes_NotInvoiced()
    {
        var materializado = ReservaDerivedAxesProjector.ProjectInvoicingStatus(1000m, new List<Invoice>());

        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, materializado);
    }

    [Fact]
    public void ProjectInvoicingStatus_ComprobantePendienteSinCae_NoCuenta_SigueNotInvoiced()
    {
        var invoices = new List<Invoice>
        {
            new() { TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "PENDING" }
        };

        var materializado = ReservaDerivedAxesProjector.ProjectInvoicingStatus(1000m, invoices);

        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, materializado);
    }

    [Fact]
    public void ProjectInvoicingStatus_FacturaMenorQueLoVendido_PartiallyInvoiced()
    {
        var invoices = new List<Invoice>
        {
            new() { TipoComprobante = 1, ImporteTotal = 800m, Resultado = "A" }
        };

        var materializado = ReservaDerivedAxesProjector.ProjectInvoicingStatus(1000m, invoices);

        Assert.Equal(ReservaInvoicingStatus.PartiallyInvoiced, materializado);
    }

    [Fact]
    public void ProjectInvoicingStatus_FacturaCubreLoVendido_FullyInvoiced()
    {
        var invoices = new List<Invoice>
        {
            new() { TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "A" }
        };

        var materializado = ReservaDerivedAxesProjector.ProjectInvoicingStatus(1000m, invoices);

        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, materializado);
    }

    /// <summary>
    /// ADR-048 T3, replicado aca: factura con CAE + Nota de Credito TOTAL -> el neto queda en 0 pero el
    /// BRUTO sigue siendo &gt; 0 -> "Facturada y devuelta", NUNCA "Sin facturar" (la mentira #2 original).
    /// </summary>
    [Fact]
    public void ProjectInvoicingStatus_FacturaMasNotaDeCreditoTotal_FullyReturned()
    {
        var invoices = new List<Invoice>
        {
            new() { TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "A" }, // Factura A
            new() { TipoComprobante = 3, ImporteTotal = 1000m, Resultado = "A" }  // Nota de Credito A
        };

        var materializado = ReservaDerivedAxesProjector.ProjectInvoicingStatus(1000m, invoices);

        Assert.Equal(ReservaInvoicingStatus.FullyReturned, materializado);
    }

    /// <summary>
    /// EQUIVALENCIA: para el mismo conjunto de comprobantes, el proyector tiene que devolver EXACTO lo
    /// mismo que armar el cuadre a mano con <see cref="ReservaInvoicingCuadreCalculator"/> y
    /// <see cref="ReservaInvoicingStatus.Derive"/> — la MISMA cadena de llamadas que usa
    /// <c>ReservaService.GetReservaByIdAsync</c> para el detalle.
    /// </summary>
    [Fact]
    public void ProjectInvoicingStatus_CoincideConLaDerivacionEnVivoDelDetalle()
    {
        const decimal totalSale = 1000m;
        var invoices = new List<Invoice>
        {
            new() { TipoComprobante = 1, ImporteTotal = 700m, Resultado = "A" },  // Factura A
            new() { TipoComprobante = 7, ImporteTotal = 100m, Resultado = "A" },  // Nota de Debito A (suma)
            new() { TipoComprobante = 3, ImporteTotal = 200m, Resultado = "A" },  // Nota de Credito A (resta)
        };

        var materializado = ReservaDerivedAxesProjector.ProjectInvoicingStatus(totalSale, invoices);

        // Misma cadena que hace el detalle EN VIVO (ReservaService.cs: cuadre + Derive).
        var cuadreEnVivo = ReservaInvoicingCuadreCalculator.Calculate(
            totalSale,
            invoices.Select(i => new CuadreInvoiceLine(
                i.TipoComprobante, i.ImporteTotal,
                IsLive: ReservaInvoicingCuadreCalculator.CountsInNetBilled(i.Resultado))));
        var enVivo = ReservaInvoicingStatus.Derive(totalSale, cuadreEnVivo.FacturadoNeto, cuadreEnVivo.BrutoEmitido);

        Assert.Equal(enVivo, materializado);
        // Facturado neto = 700 + 100 - 200 = 600, menor a 1000 -> parcial.
        Assert.Equal(ReservaInvoicingStatus.PartiallyInvoiced, materializado);
    }
}
