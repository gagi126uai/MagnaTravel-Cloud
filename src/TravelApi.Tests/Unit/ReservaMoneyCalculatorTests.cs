using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tests golden/caracterizacion del calculador de plata de la reserva (P1).
///
/// <para>Objetivo: FIJAR los numeros que hoy produce <c>ReservaService.UpdateBalanceAsync</c>
/// y demostrar que el calculador de dominio <see cref="ReservaMoneyCalculator"/> es
/// behavior-preserving. Son tests puros (sin base de datos): arman una <see cref="Reserva"/>
/// en memoria con sus colecciones y verifican los 4 totales.</para>
///
/// <para>El ultimo test (<c>Calculate_MatchesLegacyInlineMath</c>) reproduce la cuenta vieja
/// inline y la compara contra el calculador sobre una reserva mixta, como prueba directa de
/// equivalencia.</para>
/// </summary>
public class ReservaMoneyCalculatorTests
{
    // --- Cada uno de los 6 tipos suma su SalePrice/NetCost cuando Confirmado y cuando Solicitado ---

    [Theory]
    // Vuelo: HK mapea a Confirmado, codigo desconocido mapea a Solicitado. Ambos cuentan.
    [InlineData("HK")]
    [InlineData("ZZ")] // desconocido -> Solicitado
    public void Calculate_Flight_CountsWhenConfirmedOrRequested(string flightStatus)
    {
        var reserva = new Reserva();
        reserva.FlightSegments.Add(new FlightSegment { Status = flightStatus, SalePrice = 100m, NetCost = 70m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(100m, money.TotalSale);
        Assert.Equal(70m, money.TotalCost);
    }

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Solicitado")] // texto no reconocido por contains -> mapea a Solicitado y cuenta
    public void Calculate_Hotel_CountsWhenConfirmedOrRequested(string status)
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = status, SalePrice = 200m, NetCost = 150m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(200m, money.TotalSale);
        Assert.Equal(150m, money.TotalCost);
    }

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Solicitado")]
    public void Calculate_Transfer_CountsWhenConfirmedOrRequested(string status)
    {
        var reserva = new Reserva();
        reserva.TransferBookings.Add(new TransferBooking { Status = status, SalePrice = 50m, NetCost = 30m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(50m, money.TotalSale);
        Assert.Equal(30m, money.TotalCost);
    }

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Solicitado")]
    public void Calculate_Package_CountsWhenConfirmedOrRequested(string status)
    {
        var reserva = new Reserva();
        reserva.PackageBookings.Add(new PackageBooking { Status = status, SalePrice = 1000m, NetCost = 800m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(1000m, money.TotalSale);
        Assert.Equal(800m, money.TotalCost);
    }

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Solicitado")]
    public void Calculate_Assistance_CountsWhenConfirmedOrRequested(string status)
    {
        var reserva = new Reserva();
        reserva.AssistanceBookings.Add(new AssistanceBooking { Status = status, SalePrice = 250m, NetCost = 180m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(250m, money.TotalSale);
        Assert.Equal(180m, money.TotalCost);
    }

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Solicitado")]
    public void Calculate_GenericService_CountsWhenConfirmedOrRequested(string status)
    {
        var reserva = new Reserva();
        reserva.Servicios.Add(new ServicioReserva { Status = status, SalePrice = 300m, NetCost = 200m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(300m, money.TotalSale);
        Assert.Equal(200m, money.TotalCost);
    }

    // --- Un servicio cancelado NO suma ---

    [Fact]
    public void Calculate_CancelledGenericService_DoesNotCount()
    {
        var reserva = new Reserva();
        reserva.Servicios.Add(new ServicioReserva { Status = "Cancelado", SalePrice = 500m, NetCost = 400m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(0m, money.TotalSale);
        Assert.Equal(0m, money.TotalCost);
    }

    // --- Mapeo de vuelo: HK/TK/KK/KL cuentan; UN/HX no cuentan; codigo raro = Solicitado (cuenta) ---

    [Theory]
    [InlineData("HK")]
    [InlineData("TK")]
    [InlineData("KK")]
    [InlineData("KL")]
    public void Calculate_FlightConfirmedCodes_Count(string code)
    {
        var reserva = new Reserva();
        reserva.FlightSegments.Add(new FlightSegment { Status = code, SalePrice = 100m, NetCost = 60m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(100m, money.TotalSale);
        Assert.Equal(60m, money.TotalCost);
    }

    [Theory]
    [InlineData("UN")]
    [InlineData("UC")]
    [InlineData("HX")]
    [InlineData("NO")]
    public void Calculate_FlightCancelledCodes_DoNotCount(string code)
    {
        var reserva = new Reserva();
        reserva.FlightSegments.Add(new FlightSegment { Status = code, SalePrice = 100m, NetCost = 60m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(0m, money.TotalSale);
        Assert.Equal(0m, money.TotalCost);
    }

    [Fact]
    public void Calculate_FlightUnknownCode_MapsToRequested_AndCounts()
    {
        var reserva = new Reserva();
        reserva.FlightSegments.Add(new FlightSegment { Status = "XYZ", SalePrice = 100m, NetCost = 60m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(100m, money.TotalSale);
        Assert.Equal(60m, money.TotalCost);
    }

    // --- Mapeo generico: "Confirmado"/"Emitido" cuentan; "Cancelado" no; texto raro = Solicitado ---

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Emitido")]
    public void Calculate_GenericConfirmedOrIssued_Count(string status)
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = status, SalePrice = 100m, NetCost = 80m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(100m, money.TotalSale);
        Assert.Equal(80m, money.TotalCost);
    }

    [Fact]
    public void Calculate_GenericCancelled_DoesNotCount()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Cancelado", SalePrice = 100m, NetCost = 80m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(0m, money.TotalSale);
        Assert.Equal(0m, money.TotalCost);
    }

    [Fact]
    public void Calculate_GenericUnknownText_MapsToRequested_AndCounts()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "loQueSea", SalePrice = 100m, NetCost = 80m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(100m, money.TotalSale);
        Assert.Equal(80m, money.TotalCost);
    }

    // --- Payments: excluye Cancelled e IsDeleted; suma el resto ---

    [Fact]
    public void Calculate_Payments_ExcludesCancelledAndDeleted_SumsRest()
    {
        var reserva = new Reserva();
        // El saldo necesita una venta para no quedar negativo; usamos un hotel confirmado.
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 0m });

        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 300m });
        reserva.Payments.Add(new Payment { Status = "Pending", IsDeleted = false, Amount = 100m }); // vivo (no Cancelled)
        reserva.Payments.Add(new Payment { Status = "Cancelled", IsDeleted = false, Amount = 999m }); // excluido
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = true, Amount = 999m }); // excluido (borrado)

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(400m, money.TotalPaid);
    }

    // --- Balance = TotalSale - TotalPaid (NO TotalCost) ---

    [Fact]
    public void Calculate_Balance_IsSaleMinusPaid_NotCost()
    {
        var reserva = new Reserva();
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 400m });

        var money = ReservaMoneyCalculator.Calculate(reserva);

        Assert.Equal(1000m, money.TotalSale);
        Assert.Equal(700m, money.TotalCost);
        Assert.Equal(400m, money.TotalPaid);
        // 1000 - 400 = 600. Si usara el costo seria 1000 - 700 = 300 (NO es el caso).
        Assert.Equal(600m, money.Balance);
    }

    // --- Reserva mixta (varios tipos + varios estados) da el total esperado ---

    [Fact]
    public void Calculate_MixedReserva_ReturnsExpectedTotals()
    {
        var reserva = BuildMixedReserva();

        var money = ReservaMoneyCalculator.Calculate(reserva);

        // Suman (estado que cuenta):
        //   vuelo HK 500/300, hotel Confirmado 1000/700, transfer Solicitado 50/30,
        //   paquete Emitido 2000/1500, asistencia Confirmado 250/180, generico Confirmado 300/200.
        // NO suman: vuelo UN, hotel Cancelado, generico Cancelado.
        Assert.Equal(500m + 1000m + 50m + 2000m + 250m + 300m, money.TotalSale); // 4100
        Assert.Equal(300m + 700m + 30m + 1500m + 180m + 200m, money.TotalCost); // 2910
        Assert.Equal(1500m, money.TotalPaid); // 1000 + 500 vivos; 999 cancelled excluido
        Assert.Equal(4100m - 1500m, money.Balance); // 2600
    }

    // --- Reserva vacia = todo 0 ---

    [Fact]
    public void Calculate_EmptyReserva_AllZero()
    {
        var money = ReservaMoneyCalculator.Calculate(new Reserva());

        Assert.Equal(0m, money.TotalSale);
        Assert.Equal(0m, money.TotalCost);
        Assert.Equal(0m, money.TotalPaid);
        Assert.Equal(0m, money.Balance);
    }

    // --- Prueba directa de equivalencia: calculador vs cuenta vieja inline ---

    [Fact]
    public void Calculate_MatchesLegacyInlineMath()
    {
        var reserva = BuildMixedReserva();

        var money = ReservaMoneyCalculator.Calculate(reserva);
        var legacy = LegacyInlineMath(reserva);

        Assert.Equal(legacy.TotalSale, money.TotalSale);
        Assert.Equal(legacy.TotalCost, money.TotalCost);
        Assert.Equal(legacy.TotalPaid, money.TotalPaid);
        Assert.Equal(legacy.Balance, money.Balance);
    }

    // === Helpers de los tests ===

    private static Reserva BuildMixedReserva()
    {
        var reserva = new Reserva();

        reserva.FlightSegments.Add(new FlightSegment { Status = "HK", SalePrice = 500m, NetCost = 300m }); // cuenta
        reserva.FlightSegments.Add(new FlightSegment { Status = "UN", SalePrice = 999m, NetCost = 999m }); // cancelado

        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", SalePrice = 1000m, NetCost = 700m }); // cuenta
        reserva.HotelBookings.Add(new HotelBooking { Status = "Cancelado", SalePrice = 999m, NetCost = 999m }); // no cuenta

        reserva.TransferBookings.Add(new TransferBooking { Status = "Solicitado", SalePrice = 50m, NetCost = 30m }); // cuenta

        reserva.PackageBookings.Add(new PackageBooking { Status = "Emitido", SalePrice = 2000m, NetCost = 1500m }); // cuenta

        reserva.AssistanceBookings.Add(new AssistanceBooking { Status = "Confirmado", SalePrice = 250m, NetCost = 180m }); // cuenta

        reserva.Servicios.Add(new ServicioReserva { Status = "Confirmado", SalePrice = 300m, NetCost = 200m }); // cuenta
        reserva.Servicios.Add(new ServicioReserva { Status = "Cancelado", SalePrice = 999m, NetCost = 999m }); // no cuenta

        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m }); // vivo
        reserva.Payments.Add(new Payment { Status = "Pending", IsDeleted = false, Amount = 500m }); // vivo
        reserva.Payments.Add(new Payment { Status = "Cancelled", IsDeleted = false, Amount = 999m }); // excluido

        return reserva;
    }

    /// <summary>
    /// Replica EXACTA de la cuenta inline que vivia en ReservaService.UpdateBalanceAsync antes
    /// del refactor P1. Sirve solo para el test de equivalencia: si el calculador cambiara algun
    /// criterio, este test fallaria.
    /// </summary>
    private static ReservaMoneySummary LegacyInlineMath(Reserva file)
    {
        var totalSale =
            (file.FlightSegments?.Where(f => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapFlightStatus(f.Status))).Sum(f => f.SalePrice) ?? 0) +
            (file.HotelBookings?.Where(h => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(h.Status))).Sum(h => h.SalePrice) ?? 0) +
            (file.TransferBookings?.Where(t => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(t.Status))).Sum(t => t.SalePrice) ?? 0) +
            (file.PackageBookings?.Where(p => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(p.Status))).Sum(p => p.SalePrice) ?? 0) +
            (file.AssistanceBookings?.Where(a => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(a.Status))).Sum(a => a.SalePrice) ?? 0) +
            (file.Servicios?.Where(r => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(r.Status))).Sum(r => r.SalePrice) ?? 0);

        var totalCost =
            (file.FlightSegments?.Where(f => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapFlightStatus(f.Status))).Sum(f => f.NetCost) ?? 0) +
            (file.HotelBookings?.Where(h => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(h.Status))).Sum(h => h.NetCost) ?? 0) +
            (file.TransferBookings?.Where(t => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(t.Status))).Sum(t => t.NetCost) ?? 0) +
            (file.PackageBookings?.Where(p => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(p.Status))).Sum(p => p.NetCost) ?? 0) +
            (file.AssistanceBookings?.Where(a => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(a.Status))).Sum(a => a.NetCost) ?? 0) +
            (file.Servicios?.Where(r => WorkflowStatusHelper.CountsForReservaBalance(WorkflowStatusHelper.MapGenericStatus(r.Status))).Sum(r => r.NetCost) ?? 0);

        var totalPaid = file.Payments?.Where(p => p.Status != "Cancelled" && !p.IsDeleted).Sum(p => p.Amount) ?? 0;

        return new ReservaMoneySummary(totalSale, totalCost, totalPaid, totalSale - totalPaid);
    }
}
