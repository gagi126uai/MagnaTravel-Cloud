using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Q9 (2026-06-24): alarma "presupuesto/cotizacion por caducar" — un aviso POR RESERVA en Budget o
/// Quotation cuya caducidad esta configurada (dias > 0) y a la que le faltan &lt;= N dias para pasar a
/// Perdido por el job G6. La antigüedad se mide IGUAL que el job (ultimo log de entrada al estado, fallback
/// CreatedAt) para que aviso y caducidad sean coherentes.
/// </summary>
public class AlertServiceExpiringPreSalesTests
{
    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static AlertService BuildService(AppDbContext context, int budgetDays, int quotationDays)
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                BudgetExpirationDays = budgetDays,
                QuotationExpirationDays = quotationDays,
                // Mantener los otros buckets nuevos apagados/sin datos para aislar este aviso.
                EnableServiceDeadlineAlerts = false,
                EnableCatalogFindOrCreate = false
            });
        return new AlertService(context, mock.Object, NullLogger<AlertService>.Instance);
    }

    private static readonly AlertCallerContext Admin = new("admin", IsAdmin: true);

    private static Reserva BuildPreSale(int id, string status, DateTime createdAt, string? responsible = "vendedor-A")
        => new()
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            NumeroReserva = $"R-{id}",
            Name = $"Reserva {id}",
            Status = status,
            CreatedAt = createdAt,
            ResponsibleUserId = responsible
        };

    private static List<object> Bucket(object payload, string key)
    {
        var value = payload.GetType().GetProperty(key)?.GetValue(payload);
        return value is System.Collections.IEnumerable items
            ? items.Cast<object>().ToList()
            : new List<object>();
    }

    private static T Prop<T>(object item, string name)
        => (T)item.GetType().GetProperty(name)!.GetValue(item)!;

    [Fact]
    public async Task Quotation_WithinAnticipation_Alerts_UsingCreatedAtFallback()
    {
        await using var context = new AppDbContext(NewDbOptions());
        // Caduca a los 20 dias. Creada hace 18 -> faltan 2 (<= 3): avisa. Quotation nace sin log de entrada,
        // asi que se usa CreatedAt como fallback (igual que el job).
        context.Reservas.Add(BuildPreSale(1, EstadoReserva.Quotation, DateTime.UtcNow.AddDays(-18)));
        await context.SaveChangesAsync();

        var service = BuildService(context, budgetDays: 0, quotationDays: 20);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var bucket = Bucket(payload, "ExpiringPreSales");
        var item = Assert.Single(bucket);
        Assert.Equal(2, Prop<int>(item, "DaysLeft"));
        Assert.Equal(EstadoReserva.Quotation, Prop<string>(item, "PreSaleKind"));
    }

    [Fact]
    public async Task Budget_TooFarFromExpiry_DoesNotAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        // Caduca a los 30 dias. Creada hace 10 -> faltan 20 (> 3): NO avisa.
        context.Reservas.Add(BuildPreSale(1, EstadoReserva.Budget, DateTime.UtcNow.AddDays(-10)));
        await context.SaveChangesAsync();

        var service = BuildService(context, budgetDays: 30, quotationDays: 0);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "ExpiringPreSales"));
    }

    [Fact]
    public async Task ExpirationDisabled_DoesNotAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildPreSale(1, EstadoReserva.Quotation, DateTime.UtcNow.AddDays(-100)));
        await context.SaveChangesAsync();

        // Caducidad desactivada (0) para ambos tipos -> nunca avisa, aunque sea vieja.
        var service = BuildService(context, budgetDays: 0, quotationDays: 0);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "ExpiringPreSales"));
    }

    [Fact]
    public async Task AlreadyPastExpiry_DoesNotAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        // Caduca a los 10 dias. Creada hace 15 -> ya supero el plazo (la caduca el job, no es "por caducar").
        context.Reservas.Add(BuildPreSale(1, EstadoReserva.Quotation, DateTime.UtcNow.AddDays(-15)));
        await context.SaveChangesAsync();

        var service = BuildService(context, budgetDays: 0, quotationDays: 10);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "ExpiringPreSales"));
    }

    [Fact]
    public async Task Budget_UsesEntryLog_NotCreatedAt()
    {
        await using var context = new AppDbContext(NewDbOptions());
        // Creada hace mucho (40 dias) pero ENTRO a Budget hace 28. Caduca a los 30 -> faltan 2: avisa.
        // Si se midiera por CreatedAt (40), ya habria caducado y no avisaria. Comprueba que usa el log.
        var reserva = BuildPreSale(1, EstadoReserva.Budget, DateTime.UtcNow.AddDays(-40));
        context.Reservas.Add(reserva);
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = 1,
            FromStatus = EstadoReserva.Quotation,
            ToStatus = EstadoReserva.Budget,
            Direction = "Forward",
            OccurredAt = DateTime.UtcNow.AddDays(-28)
        });
        await context.SaveChangesAsync();

        var service = BuildService(context, budgetDays: 30, quotationDays: 0);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "ExpiringPreSales"));
        Assert.Equal(2, Prop<int>(item, "DaysLeft"));
    }

    [Theory]
    [InlineData(0, "vence hoy")]
    [InlineData(1, "vence mañana")]
    [InlineData(3, "vence en 3 días")]
    public async Task Message_TextMatchesDaysLeft(int expectedDaysLeft, string expectedFragment)
    {
        await using var context = new AppDbContext(NewDbOptions());
        // Caduca a los 10 dias. daysLeft = 10 - antigüedad, asi que para daysLeft = N usamos antigüedad = 10 - N.
        var age = 10 - expectedDaysLeft;
        var reserva = BuildPreSale(1, EstadoReserva.Quotation, DateTime.UtcNow.AddDays(-age));
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = BuildService(context, budgetDays: 0, quotationDays: 10);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "ExpiringPreSales"));
        Assert.Contains(expectedFragment, Prop<string>(item, "Message"));
    }

    [Fact]
    public async Task RespectsOwnerScope_VendedorSeesOnlyOwn()
    {
        await using var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(BuildPreSale(1, EstadoReserva.Quotation, DateTime.UtcNow.AddDays(-18), responsible: "vendedor-A"));
        context.Reservas.Add(BuildPreSale(2, EstadoReserva.Quotation, DateTime.UtcNow.AddDays(-18), responsible: "vendedor-B"));
        await context.SaveChangesAsync();

        var service = BuildService(context, budgetDays: 0, quotationDays: 20);
        var caller = new AlertCallerContext("vendedor-A", IsAdmin: false);
        var payload = await service.GetAlertsAsync(caller, CancellationToken.None);

        var item = Assert.Single(Bucket(payload, "ExpiringPreSales"));
        Assert.Equal("R-1", Prop<string>(item, "NumeroReserva"));
    }

    [Fact]
    public async Task NonPreSaleStatus_DoesNotAlert()
    {
        await using var context = new AppDbContext(NewDbOptions());
        // Una reserva ya firme (Confirmed) NO es pre-venta: aunque sea vieja, este aviso no aplica.
        context.Reservas.Add(BuildPreSale(1, EstadoReserva.Confirmed, DateTime.UtcNow.AddDays(-100)));
        await context.SaveChangesAsync();

        var service = BuildService(context, budgetDays: 5, quotationDays: 5);
        var payload = await service.GetAlertsAsync(Admin, CancellationToken.None);

        Assert.Empty(Bucket(payload, "ExpiringPreSales"));
    }
}
