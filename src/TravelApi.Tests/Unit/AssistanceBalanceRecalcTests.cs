using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bloque 3 (Asistencia al viajero) - tapa el "olvido silencioso" en las OTRAS dos copias del
/// calculo de saldo, fuera de ReservaService.UpdateBalanceAsync (que ya contempla Asistencia):
///
///  - PaymentService.RecalculateReservaBalanceAsync: corre en CADA pago. Si no suma la venta de la
///    asistencia, el cliente queda debiendo de menos apenas registra un pago.
///  - AfipService.RecalculateReservaBalanceAsync: corre despues de emitir factura / nota de credito.
///
/// Ambas copias son una suma PLANA (sin el filtro CountsForReservaBalance que aplica ReservaService);
/// estos tests fijan el criterio LOCAL de cada copia, no lo unifican (eso es follow-up, ver TODO en
/// el codigo). Antes del fix de Bloque 3 estos tests fallarian porque la venta de la asistencia (250)
/// no entraba en TotalSale ni en Balance.
/// </summary>
public class AssistanceBalanceRecalcTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    // === B1: PaymentService.RecalculateReservaBalanceAsync (corre en cada pago) ===

    [Fact]
    public async Task CreatePaymentAsync_WithAssistance_KeepsAssistanceSaleInBalance()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();

        // Reserva fuera de Budget (CreatePaymentAsync rechaza pagos en Budget).
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-7001",
            Name = "Reserva con asistencia",
            Status = EstadoReserva.Confirmed
        };
        context.Reservas.Add(reserva);
        // Venta 250 / costo 100 - es la unica linea de la reserva, asi que TODO el saldo
        // depende de que la asistencia entre en el calculo.
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 1,
            ReservaId = 1,
            SupplierId = 1,
            ValidFrom = DateTime.UtcNow.Date.AddDays(10),
            ValidTo = DateTime.UtcNow.Date.AddDays(20),
            Status = "Confirmado",
            NetCost = 100m,
            SalePrice = 250m,
            Commission = 150m
        });
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context, mapper, reserva);

        // Pago parcial de 60. El recalculo corre dentro de CreatePaymentAsync.
        var request = new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            Amount = 60m,
            Method = "Transfer"
        };
        await service.CreatePaymentAsync(request, CancellationToken.None);

        var reloaded = await context.Reservas.FindAsync(1);
        Assert.NotNull(reloaded);
        // La venta de la asistencia debe seguir contemplada tras el pago.
        Assert.Equal(250m, reloaded!.TotalSale);
        Assert.Equal(100m, reloaded.TotalCost);
        Assert.Equal(60m, reloaded.TotalPaid);
        // Balance = venta (250) - pagado (60). Si la asistencia se "perdiera", daria -60.
        Assert.Equal(190m, reloaded.Balance);
    }

    private static PaymentService BuildPaymentService(AppDbContext context, IMapper mapper, Reserva reserva)
    {
        // El resolver traduce el PublicId del request al Id interno de la reserva sembrada.
        var entityResolver = new Mock<IEntityReferenceResolver>();
        entityResolver
            .Setup(r => r.ResolveRequiredIdAsync<Reserva>(reserva.PublicId.ToString(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(reserva.Id);

        var financeSettings = new Mock<IOperationalFinanceSettingsService>();
        financeSettings
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        // Sin HttpContext/permissionResolver: GetOwnerScopeOrNull devuelve null => comportamiento
        // legacy (sin filtro de ownership), suficiente para ejercitar el recalculo de saldo.
        return new PaymentService(
            context,
            entityResolver.Object,
            mapper,
            financeSettings.Object,
            NullLogger<PaymentService>.Instance);
    }

    // === B2: AfipService.RecalculateReservaBalanceAsync (post factura / NC) ===
    //
    // El metodo publico que dispara este recalculo es parte del pipeline de nota de credito
    // (Invoices + OriginalInvoice + BookingCancellation + reversa de Payments). Montar ese pipeline
    // entero InMemory es pesado y fragil y NO ejercitaria mejor la linea que arreglamos.
    // Como la formula de saldo de AfipService es identica a la de PaymentService, cubrimos el calculo
    // lo MAS cerca posible: invocamos el metodo privado por reflexion sobre una instancia real de
    // AfipService con AppDbContext InMemory. Esto ejecuta la query real (incluido el .Include de
    // AssistanceBookings) y la suma real, que es exactamente lo que toca el fix.

    [Fact]
    public async Task AfipRecalculateBalance_WithAssistance_KeepsAssistanceSaleInBalance()
    {
        await using var context = CreateContext();

        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-7002",
            Name = "Reserva con asistencia (afip)",
            Status = EstadoReserva.Confirmed
        };
        context.Reservas.Add(reserva);
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 1,
            ReservaId = 1,
            SupplierId = 1,
            ValidFrom = DateTime.UtcNow.Date.AddDays(10),
            ValidTo = DateTime.UtcNow.Date.AddDays(20),
            Status = "Confirmado",
            NetCost = 100m,
            SalePrice = 250m,
            Commission = 150m
        });
        await context.SaveChangesAsync();

        var afip = BuildAfipService(context);
        await InvokeAfipRecalculateAsync(afip, reservaId: 1);

        var reloaded = await context.Reservas.FindAsync(1);
        Assert.NotNull(reloaded);
        Assert.Equal(250m, reloaded!.TotalSale);
        Assert.Equal(100m, reloaded.TotalCost);
        // Sin pagos: el saldo es la venta completa, incluida la asistencia.
        Assert.Equal(250m, reloaded.Balance);
    }

    private static AfipService BuildAfipService(AppDbContext context)
    {
        // El protector no se ejerce en el recalculo de saldo; un mock identidad alcanza.
        var protector = new Mock<ISensitiveDataProtector>();
        protector.Setup(p => p.UnprotectString(It.IsAny<string?>())).Returns((string? v) => v);
        protector.Setup(p => p.UnprotectBytes(It.IsAny<byte[]?>())).Returns((byte[]? v) => v);

        return new AfipService(
            context,
            NullLogger<AfipService>.Instance,
            new HttpClient(),
            protector.Object);
    }

    // RecalculateReservaBalanceAsync es privado (detalle de implementacion del pipeline AFIP).
    // Lo invocamos por reflexion solo para los tests: ejercita la misma query/suma que corre en prod.
    private static async Task InvokeAfipRecalculateAsync(AfipService afip, int reservaId)
    {
        var method = typeof(AfipService).GetMethod(
            "RecalculateReservaBalanceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(afip, new object[] { reservaId })!;
        await task;
    }
}
