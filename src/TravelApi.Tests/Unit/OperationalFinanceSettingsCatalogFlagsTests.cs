using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.1 (catalogo find-or-create + fechas limite, 2026-06-05): tests de los 2 flags nuevos
/// (<c>EnableCatalogFindOrCreate</c>, <c>EnableServiceDeadlineAlerts</c>) y el setting
/// <c>StaleCostReferenceDays</c> expuestos por PUT/GET /api/settings/operational-finance.
///
/// <para>Garantizan: (a) default OFF / 60; (b) patch-like (omitir != apagar); (c) togglean explicito;
/// (d) sin validacion cruzada (son flags de comportamiento puro). Son tests UNITARIOS sobre el service
/// con EF Core InMemory — no tocan Postgres ni HTTP.</para>
/// </summary>
public class OperationalFinanceSettingsCatalogFlagsTests
{
    private static AppDbContext BuildDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<OperationalFinanceSettings> SeedSettingsAsync(
        AppDbContext db,
        Action<OperationalFinanceSettings>? customize = null)
    {
        var settings = new OperationalFinanceSettings();
        customize?.Invoke(settings);
        db.OperationalFinanceSettings.Add(settings);
        await db.SaveChangesAsync();
        return settings;
    }

    // DTO base valido: los flags/setting nuevos quedan null = omitidos = no se tocan.
    private static OperationalFinanceSettingsDto BaseRequest() => new()
    {
        RequireFullPaymentForOperativeStatus = true,
        RequireFullPaymentForVoucher = true,
        AfipInvoiceControlMode = "AllowAgentOverrideWithReason",
        EnableUpcomingUnpaidReservationNotifications = true,
        UpcomingUnpaidReservationAlertDays = 7,
        MaxDiscountPercentWithoutOverride = 10m,
    };

    // ============================================================
    // (a) Defaults: la entidad nueva nace con ambos flags OFF y el umbral en 60
    // ============================================================

    [Fact]
    public void NewEntity_DefaultsToOffAndSixtyDays()
    {
        var settings = new OperationalFinanceSettings();

        Assert.False(settings.EnableCatalogFindOrCreate);
        Assert.False(settings.EnableServiceDeadlineAlerts);
        Assert.Equal(60, settings.StaleCostReferenceDays);
    }

    [Fact]
    public async Task GetAsync_FreshStore_ReturnsFlagsOffAndDefaultThreshold()
    {
        await using var db = BuildDbContext();
        var service = new OperationalFinanceSettingsService(db);

        var dto = await service.GetAsync(CancellationToken.None);

        Assert.False(dto.EnableCatalogFindOrCreate);
        Assert.False(dto.EnableServiceDeadlineAlerts);
        Assert.Equal(60, dto.StaleCostReferenceDays);
    }

    // ============================================================
    // (b) Patch-like: omitir un flag/setting NO lo pisa
    // ============================================================

    [Fact]
    public async Task UpdateAsync_OmittedCatalogFlag_DoesNotOverwriteCurrentValue()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.EnableCatalogFindOrCreate = true);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        Assert.Null(request.EnableCatalogFindOrCreate);

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.True(result.EnableCatalogFindOrCreate);
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableCatalogFindOrCreate);
    }

    [Fact]
    public async Task UpdateAsync_OmittedStaleDays_DoesNotOverwriteCurrentValue()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.StaleCostReferenceDays = 90);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        Assert.Null(request.StaleCostReferenceDays);

        var result = await service.UpdateAsync(request, CancellationToken.None);

        // El admin habia configurado 90 dias: un PUT que omita el campo no lo vuelve al default.
        Assert.Equal(90, result.StaleCostReferenceDays);
    }

    // ============================================================
    // (c) Toggle explicito: prender / cambiar persiste, sin validacion cruzada
    // ============================================================

    [Fact]
    public async Task UpdateAsync_TurnOnCatalogAndDeadlineFlags_Persists()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableCatalogFindOrCreate = true;
        request.EnableServiceDeadlineAlerts = true;
        request.StaleCostReferenceDays = 45;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.True(result.EnableCatalogFindOrCreate);
        Assert.True(result.EnableServiceDeadlineAlerts);
        Assert.Equal(45, result.StaleCostReferenceDays);

        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableCatalogFindOrCreate);
        Assert.True(persisted.EnableServiceDeadlineAlerts);
        Assert.Equal(45, persisted.StaleCostReferenceDays);
    }

    /// <summary>
    /// Son flags de comportamiento puro: prender SOLO el del catalogo (sin tocar ningun otro flag) NO
    /// dispara ninguna validacion cruzada. Asegura que no se acoplo por error a GR-002/GR-013.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_CatalogFlagAloneWithCancellationFlowOff_DoesNotThrow()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.EnableNewCancellationFlow = false);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableCatalogFindOrCreate = true;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.True(result.EnableCatalogFindOrCreate);
    }
}
