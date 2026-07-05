using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-04, hallazgo A1) Tests del recalculador de coherencia de plata de reservas anuladas
/// (<see cref="CoherenceMoneyRecalculator"/>), el PASO 2 de la reparación (el PASO 1 —cancelar en la base los
/// servicios que quedaron vivos— lo hace la migración RepairLegacyAnnulledReservaServices, que no se puede
/// testear con el provider InMemory; su SQL queda revisado a mano).
///
/// <para>Cubre: (1) reserva anulada con servicios ya cancelados pero proyección vieja &gt; 0 → se recalcula a 0;
/// (2) reserva anulada con crédito previo (puente negativo) → el recálculo NO duplica el saldo a favor;
/// (3) reserva anulada sana → 0 cambios; (4) segunda corrida idempotente; (5) reserva NO anulada → intacta.</para>
/// </summary>
public class CoherenceMoneyRecalculatorTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static CoherenceMoneyRecalculator NewRecalculator(AppDbContext context) =>
        new(context, NullLogger<CoherenceMoneyRecalculator>.Instance);

    /// <summary>Hotel ya cancelado (como lo dejó la migración): no aporta a la venta ni al saldo.</summary>
    private static HotelBooking CancelledHotel(int id, int reservaId, decimal salePrice) => new()
    {
        Id = id, ReservaId = reservaId, HotelName = "Hotel test", City = "BRC",
        RoomType = "Doble", MealPlan = "Desayuno", Adults = 1, Rooms = 1,
        CheckIn = DateTime.UtcNow.Date, CheckOut = DateTime.UtcNow.Date.AddDays(2),
        Status = WorkflowStatuses.Cancelado, SalePrice = salePrice, NetCost = 0m
    };

    // ============================================================================================
    // 1) Reserva anulada con servicios ya cancelados pero proyección vieja (Balance > 0) → recalcula a 0.
    // ============================================================================================

    [Fact]
    public async Task RecalculatesStaleAnnulledReservaToZero()
    {
        await using var context = NewContext();

        // Reserva anulada con escalares STALE (como quedó una anulación vieja: servicio sin cancelar dejaba
        // ConfirmedSale/Balance inflados). El servicio YA está cancelado (lo hizo la migración), pero la plata
        // nunca se recalculó.
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-ANULADA-STALE", Name = "Anulada con plata vieja",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 1000m, ConfirmedSale = 1000m, Balance = 1000m
        };
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(CancelledHotel(id: 10, reservaId: 1, salePrice: 1000m));
        // Fila hija por moneda igual de vieja.
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            Id = 100, ReservaId = 1, Currency = Monedas.ARS,
            TotalSale = 1000m, ConfirmedSale = 1000m, Balance = 1000m
        });
        await context.SaveChangesAsync();

        var result = await NewRecalculator(context).RecalculateAnnulledReservasMoneyAsync(CancellationToken.None);

        Assert.Equal(1, result.Reviewed);
        Assert.Equal(1, result.Corrected);
        Assert.Equal(0, result.Failed);

        var recalculated = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, recalculated.ConfirmedSale);
        Assert.Equal(0m, recalculated.Balance);
        Assert.Equal(0m, recalculated.TotalSale);

        // La fila hija por moneda queda sin plata: se borra al no haber servicios/pagos vivos.
        var moneyRows = await context.ReservaMoneyByCurrency.AsNoTracking()
            .Where(m => m.ReservaId == 1).ToListAsync();
        Assert.Empty(moneyRows);
    }

    // ============================================================================================
    // 2) Reserva anulada con crédito previo (puente negativo): el recálculo cuenta el puente UNA vez y NO duplica.
    // ============================================================================================

    [Fact]
    public async Task DoesNotDuplicateCreditWhenNegativeBridgeExists()
    {
        await using var context = NewContext();

        // Escalares STALE (mal): dicen que la reserva vende y debe. La realidad económica: hotel cancelado
        // (venta confirmada 0), el cliente pagó 1500 y ya se le "minteó" 1000 de crédito (puente negativo).
        // Saldo real = 0 - (1500 - 1000) = -500 (saldo a favor pendiente).
        var reserva = new Reserva
        {
            Id = 2, NumeroReserva = "F-ANULADA-CREDITO", Name = "Anulada con saldo a favor",
            Status = EstadoReserva.PendingOperatorRefund, AdultCount = 1,
            TotalSale = 1000m, ConfirmedSale = 1000m, Balance = 1000m
        };
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(CancelledHotel(id: 20, reservaId: 2, salePrice: 1000m));

        // Cobro real del cliente.
        context.Payments.Add(new Payment
        {
            Id = 200, ReservaId = 2, Amount = 1500m, Currency = Monedas.ARS, Status = "Paid"
        });
        // Puente NEGATIVO: crédito ya minteado hacia otro lado. Debe netear TotalPaid, no ignorarse ni duplicarse.
        context.Payments.Add(new Payment
        {
            Id = 201, ReservaId = 2, Amount = -1000m, Currency = Monedas.ARS, Status = "Paid"
        });
        await context.SaveChangesAsync();

        var result = await NewRecalculator(context).RecalculateAnnulledReservasMoneyAsync(CancellationToken.None);

        Assert.Equal(1, result.Reviewed);
        Assert.Equal(1, result.Corrected);

        var recalculated = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 2);
        // -500 exacto: si el puente se ignorara sería -1500; si se duplicara, otro número. Cuenta una sola vez.
        Assert.Equal(-500m, recalculated.Balance);
        Assert.Equal(0m, recalculated.ConfirmedSale);
    }

    // ============================================================================================
    // 3) Reserva anulada sana (ya en cero, sin filas hijas): no es candidata → 0 revisadas, 0 corregidas.
    // ============================================================================================

    [Fact]
    public async Task HealthyAnnulledReservaIsNotTouched()
    {
        await using var context = NewContext();

        context.Reservas.Add(new Reserva
        {
            Id = 3, NumeroReserva = "F-ANULADA-SANA", Name = "Anulada sana",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 0m, ConfirmedSale = 0m, Balance = 0m
        });
        context.HotelBookings.Add(CancelledHotel(id: 30, reservaId: 3, salePrice: 500m));
        await context.SaveChangesAsync();

        var result = await NewRecalculator(context).RecalculateAnnulledReservasMoneyAsync(CancellationToken.None);

        Assert.Equal(0, result.Reviewed);
        Assert.Equal(0, result.Corrected);
    }

    // ============================================================================================
    // 4) Segunda corrida idempotente: tras la primera corrección, correr de nuevo no cambia nada.
    // ============================================================================================

    [Fact]
    public async Task SecondRunIsIdempotent()
    {
        await using var context = NewContext();

        context.Reservas.Add(new Reserva
        {
            Id = 4, NumeroReserva = "F-ANULADA-IDEM", Name = "Anulada idempotente",
            Status = EstadoReserva.Cancelled, AdultCount = 1,
            TotalSale = 800m, ConfirmedSale = 800m, Balance = 800m
        });
        context.HotelBookings.Add(CancelledHotel(id: 40, reservaId: 4, salePrice: 800m));
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            Id = 400, ReservaId = 4, Currency = Monedas.ARS,
            TotalSale = 800m, ConfirmedSale = 800m, Balance = 800m
        });
        await context.SaveChangesAsync();

        var recalculator = NewRecalculator(context);

        var firstRun = await recalculator.RecalculateAnnulledReservasMoneyAsync(CancellationToken.None);
        Assert.Equal(1, firstRun.Corrected);

        // Segunda corrida: la reserva ya quedó en cero → no es candidata → nada que revisar ni corregir.
        var secondRun = await recalculator.RecalculateAnnulledReservasMoneyAsync(CancellationToken.None);
        Assert.Equal(0, secondRun.Reviewed);
        Assert.Equal(0, secondRun.Corrected);
    }

    // ============================================================================================
    // 5) Reserva NO anulada con saldo: el recalculador NO la toca (solo repara anuladas).
    // ============================================================================================

    [Fact]
    public async Task NonAnnulledReservaIsIgnored()
    {
        await using var context = NewContext();

        // Reserva viva (Confirmada) con saldo real: NO debe entrar al barrido de reparación de anuladas.
        context.Reservas.Add(new Reserva
        {
            Id = 5, NumeroReserva = "F-VIVA", Name = "Reserva viva con saldo",
            Status = EstadoReserva.Confirmed, AdultCount = 1,
            TotalSale = 1200m, ConfirmedSale = 1200m, Balance = 1200m
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 50, ReservaId = 5, HotelName = "Hotel vivo", City = "BRC",
            RoomType = "Doble", MealPlan = "Desayuno", Adults = 1, Rooms = 1,
            CheckIn = DateTime.UtcNow.Date, CheckOut = DateTime.UtcNow.Date.AddDays(2),
            Status = WorkflowStatuses.Confirmado, SalePrice = 1200m, NetCost = 0m
        });
        await context.SaveChangesAsync();

        var result = await NewRecalculator(context).RecalculateAnnulledReservasMoneyAsync(CancellationToken.None);

        Assert.Equal(0, result.Reviewed);

        // El escalar de la reserva viva queda EXACTAMENTE como estaba.
        var untouched = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 5);
        Assert.Equal(1200m, untouched.Balance);
        Assert.Equal(1200m, untouched.ConfirmedSale);
    }
}
