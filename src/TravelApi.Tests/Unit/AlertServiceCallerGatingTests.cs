using System.Collections;
using System.Collections.Generic;
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
/// Fuga 2 (ADR-017 §2.7, F1b — fix de seguridad SIN flag): gating server-side de /api/alerts.
/// Antes el endpoint era [Authorize] plano y el "solo admin" vivia unicamente en el frontend
/// (AlertsContext.jsx): cualquier logueado podia leer SupplierDebts/UrgentTrips (informacion
/// financiera de TODA la agencia) con un curl. Contrato fijado:
///   - caller NO admin -> payload con la MISMA forma pero vacio (UrgentTrips=[], SupplierDebts=[],
///     TotalCount=0). NO un 403: la forma estable deja listo el contrato para que F3 sume
///     buckets por-vendedor sin otro cambio.
///   - caller admin -> payload identico al de siempre (byte-identico).
/// </summary>
public class AlertServiceCallerGatingTests
{
    private static DbContextOptions<AppDbContext> NewDbOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static Mock<IOperationalFinanceSettingsService> SettingsMock()
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                UpcomingUnpaidReservationAlertDays = 30
            });
        return mock;
    }

    // Seed: una reserva "urgente" (viaje en 5 dias con saldo) + un proveedor con deuda,
    // para que el bucket de admin tenga contenido real que contrastar.
    private static async Task<AppDbContext> SeedContextAsync()
    {
        var context = new AppDbContext(NewDbOptions());
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            Name = "Reserva urgente",
            NumeroReserva = "R-1",
            Status = EstadoReserva.Confirmed,
            Balance = 100m,
            TotalSale = 1000m,
            StartDate = DateTime.UtcNow.Date.AddDays(5)
        });
        context.Suppliers.Add(new Supplier
        {
            Id = 1,
            Name = "Mayorista con deuda",
            CurrentBalance = 500m,
            IsActive = true
        });
        await context.SaveChangesAsync();
        return context;
    }

    [Fact]
    public async Task GetAlertsAsync_NonAdmin_ReturnsEmptyBucketsWithSameShape()
    {
        await using var context = await SeedContextAsync();
        var service = new AlertService(context, SettingsMock().Object, NullLogger<AlertService>.Instance);

        dynamic result = await service.GetAlertsAsync(
            new AlertCallerContext("vendedor-test", IsAdmin: false), CancellationToken.None);

        // Mismas tres propiedades que el payload de admin, pero vacias.
        Assert.Empty((IEnumerable)result.UrgentTrips);
        Assert.Empty((IEnumerable)result.SupplierDebts);
        Assert.Equal(0, (int)result.TotalCount);
    }

    [Fact]
    public async Task GetAlertsAsync_Admin_ReturnsFinancialBuckets()
    {
        await using var context = await SeedContextAsync();
        var service = new AlertService(context, SettingsMock().Object, NullLogger<AlertService>.Instance);

        // El controller siempre arma el admin con CanSeeCost=true (AlertsController: canSeeCost = isAdmin || ...).
        // SupplierDebts (deuda al operador = COSTO) ahora se gatea por CanSeeCost, asi que el caller de test
        // debe reflejar ese contrato real.
        dynamic result = await service.GetAlertsAsync(
            new AlertCallerContext("admin-test", IsAdmin: true, CanSeeCost: true), CancellationToken.None);

        Assert.Single((IEnumerable)result.UrgentTrips);
        Assert.Single((IEnumerable)result.SupplierDebts);
        Assert.Equal(2, (int)result.TotalCount);
    }

    [Fact]
    public async Task GetAlertsAsync_NonAdmin_PayloadHasExactlySameProperties_AsAdmin()
    {
        // Garantia de "misma forma": el front actual (y el de F3) no debe romperse
        // segun quien llame. Comparamos los NOMBRES de propiedades de ambos payloads.
        await using var context = await SeedContextAsync();
        var service = new AlertService(context, SettingsMock().Object, NullLogger<AlertService>.Instance);

        object adminPayload = await service.GetAlertsAsync(
            new AlertCallerContext("admin-test", IsAdmin: true, CanSeeCost: true), CancellationToken.None);
        object sellerPayload = await service.GetAlertsAsync(
            new AlertCallerContext("vendedor-test", IsAdmin: false), CancellationToken.None);

        var adminProps = PropertyNames(adminPayload);
        var sellerProps = PropertyNames(sellerPayload);

        Assert.Equal(adminProps, sellerProps);
    }

    private static List<string> PropertyNames(object payload)
    {
        var names = new List<string>();
        foreach (var prop in payload.GetType().GetProperties())
        {
            names.Add(prop.Name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
