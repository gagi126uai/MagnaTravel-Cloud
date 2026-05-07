using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Red de seguridad para refactor C17. Pin del comportamiento ACTUAL de
/// cancelacion de reservas en ReservaService.
///
/// Importante: la cancelacion HOY se hace via UpdateStatusAsync(id, "Cancelled").
/// El servicio NO tiene un metodo CancelAsync dedicado. Estos tests fijan el
/// comportamiento observado (no necesariamente el deseado por el dominio):
/// las "ambiguedades" que detecta la auditoria (no hay validacion de voucher
/// emitido, no se loguea el cambio en ReservaStatusChangeLogs) se documentan
/// con asserts explicitos para que no muten silenciosamente en el refactor.
/// </summary>
public class ReservaServiceCancellationTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IMapper> _mapperMock;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceCancellationTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapperMock = new Mock<IMapper>();
        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store
            .Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object,
            null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private ReservaService BuildService(AppDbContext context)
        => new(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

    [Fact]
    public async Task UpdateStatusAsync_CancelFromBudget_IsAllowed()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva budget",
            Status = EstadoReserva.Budget
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Cancelled);

        Assert.Equal(EstadoReserva.Cancelled, result.Status);
        var dbReserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Cancelled, dbReserva.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_CancelFromConfirmed_IsAllowed()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0002",
            Name = "Reserva confirmada",
            Status = EstadoReserva.Confirmed
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Cancelled);

        Assert.Equal(EstadoReserva.Cancelled, result.Status);
    }

    /// <summary>
    /// Pin del comportamiento actual: la cancelacion NO se registra en
    /// ReservaStatusChangeLogs (eso solo ocurre en RevertStatusAsync). Si el
    /// equipo decide migrar tambien la cancelacion al log de auditoria, este
    /// test debe actualizarse. Mientras tanto, deja registrado que NO hay
    /// rastro auditable de la cancelacion a nivel de tabla de logs.
    /// </summary>
    [Fact]
    public async Task UpdateStatusAsync_Cancel_DoesNotWriteReservaStatusChangeLog_Today()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0003",
            Name = "Reserva confirmada",
            Status = EstadoReserva.Confirmed
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await service.UpdateStatusAsync(1, EstadoReserva.Cancelled);

        var logCount = await context.ReservaStatusChangeLogs.CountAsync(l => l.ReservaId == 1);
        Assert.Equal(0, logCount);
    }

    /// <summary>
    /// Comportamiento ACTUAL: el servicio NO chequea VoucherStatus.Issued al
    /// cancelar. Si este test llegara a fallar, indica que se agrego la
    /// validacion (cambio deseable, pero requiere actualizar este test). Si
    /// pasa, indica que la regla aun no esta puesta.
    /// </summary>
    [Fact]
    public async Task UpdateStatusAsync_Cancel_WithIssuedVoucher_IsCurrentlyAllowed()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0004",
            Name = "Reserva con voucher",
            Status = EstadoReserva.Confirmed
        });
        context.Vouchers.Add(new Voucher
        {
            Id = 1,
            ReservaId = 1,
            Status = VoucherStatuses.Issued,
            FileName = "voucher.pdf"
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Cancelled);

        Assert.Equal(EstadoReserva.Cancelled, result.Status);

        // Pin defensivo: el voucher Issued sigue existiendo y NO se revoca
        // automaticamente al cancelar la reserva.
        var voucher = await context.Vouchers.AsNoTracking().FirstAsync(v => v.Id == 1);
        Assert.Equal(VoucherStatuses.Issued, voucher.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_Cancel_WithRegisteredPayments_KeepsPaymentsAndDoesNotRefund()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0005",
            Name = "Reserva con pagos",
            Status = EstadoReserva.Confirmed,
            TotalSale = 1000m,
            TotalPaid = 600m,
            Balance = 400m
        });
        context.Payments.Add(new Payment
        {
            Id = 1,
            ReservaId = 1,
            Amount = 600m,
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            PaidAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Cancelled);

        Assert.Equal(EstadoReserva.Cancelled, result.Status);

        // El pago original sigue en la tabla, NO se borra ni se marca como
        // soft-deleted. NO se genera ningun pago de reversion (refund/CN
        // automatico es B2 futuro).
        var paymentCount = await context.Payments.CountAsync(p => p.ReservaId == 1);
        Assert.Equal(1, paymentCount);

        var payment = await context.Payments.AsNoTracking().FirstAsync(p => p.Id == 1);
        Assert.False(payment.IsDeleted);
        Assert.Equal("Paid", payment.Status);
        Assert.Equal(600m, payment.Amount);
        Assert.Equal(PaymentEntryTypes.Payment, payment.EntryType);
    }

    /// <summary>
    /// Cancelar una reserva ya cancelada es idempotente: no lanza, no genera
    /// efecto colateral, el estado sigue "Cancelled".
    /// </summary>
    [Fact]
    public async Task UpdateStatusAsync_CancelAlreadyCancelled_IsIdempotent()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0006",
            Name = "Reserva ya cancelada",
            Status = EstadoReserva.Cancelled
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Cancelled);
        Assert.Equal(EstadoReserva.Cancelled, result.Status);
    }

    /// <summary>
    /// Comportamiento ACTUAL: cancelar desde Traveling no esta bloqueado en
    /// ReservaService (la unica validacion en UpdateStatusAsync para Cancelled
    /// es que el estado destino este en validStatuses). Esto puede ser un gap
    /// de dominio: una reserva "En viaje" probablemente no deberia poder
    /// cancelarse sin pasar antes por reversion. Lo dejamos clavado aqui:
    /// si el equipo decide bloquearlo, el test fallara y pedira actualizacion.
    /// </summary>
    [Fact]
    public async Task UpdateStatusAsync_CancelFromTraveling_IsCurrentlyAllowed()
    {
        await using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0007",
            Name = "Reserva en viaje",
            Status = EstadoReserva.Traveling
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Cancelled);
        Assert.Equal(EstadoReserva.Cancelled, result.Status);
    }

    /// <summary>
    /// Cancelar una reserva inexistente lanza KeyNotFoundException.
    /// </summary>
    [Fact]
    public async Task UpdateStatusAsync_CancelMissingReserva_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        var service = BuildService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateStatusAsync(9999, EstadoReserva.Cancelled));
    }
}
