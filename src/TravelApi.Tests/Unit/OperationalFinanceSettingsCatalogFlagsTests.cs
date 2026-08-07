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
        // Linea base de test = flags de cancelacion APAGADOS, independiente del default de
        // produccion (que desde 2026-06-23 viene prendido). Cada test opta-in con customize.
        settings.EnableNewCancellationFlow = false;
        settings.EnableCancellationDebitNote = false;
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

        // La llave del catalogo quedo DEROGADA el 2026-08-06 (P8=A): el GET devuelve true fijo, no lo
        // que haya en la base, porque el catalogo que aprende de las ventas ya no se puede apagar.
        Assert.True(dto.EnableCatalogFindOrCreate);
        Assert.False(dto.EnableServiceDeadlineAlerts);
        Assert.Equal(60, dto.StaleCostReferenceDays);
    }

    /// <summary>
    /// La llave derogada (2026-08-06, P8=A): si un cliente viejo todavia la manda en el PUT, se IGNORA.
    /// La columna de la base queda como estaba y el GET sigue diciendo true.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_LlaveDerogadaDelCatalogo_SeIgnoraYNoCambiaNada()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.EnableCatalogFindOrCreate = false);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableCatalogFindOrCreate = true;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.True(result.EnableCatalogFindOrCreate);
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.False(persisted.EnableCatalogFindOrCreate);
    }

    /// <summary>
    /// Numero nuevo de Cobranzas (spec firmada 2026-08-06, P15=A): "el saldo tiene que estar completo N
    /// dias antes de la salida". Default 21, se puede cambiar, y omitirlo en el PUT no lo pisa.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_DiasParaSaldoCompleto_PersisteYRespetaElOmitido()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db);
        var service = new OperationalFinanceSettingsService(db);

        Assert.Equal(21, (await service.GetAsync(CancellationToken.None)).FullPaymentDueDaysBeforeDeparture);

        var request = BaseRequest();
        request.FullPaymentDueDaysBeforeDeparture = 30;
        var result = await service.UpdateAsync(request, CancellationToken.None);
        Assert.Equal(30, result.FullPaymentDueDaysBeforeDeparture);

        // Un PUT que NO lo trae deja el 30 configurado (patch-like, criterio B-002).
        var withoutTheField = BaseRequest();
        Assert.Null(withoutTheField.FullPaymentDueDaysBeforeDeparture);
        var afterOmit = await service.UpdateAsync(withoutTheField, CancellationToken.None);
        Assert.Equal(30, afterOmit.FullPaymentDueDaysBeforeDeparture);
    }

    // ============================================================
    // (b) Patch-like: omitir un flag/setting NO lo pisa
    // ============================================================

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
    public async Task UpdateAsync_TurnOnDeadlineFlagAndThreshold_Persists()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableServiceDeadlineAlerts = true;
        request.StaleCostReferenceDays = 45;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.True(result.EnableServiceDeadlineAlerts);
        Assert.Equal(45, result.StaleCostReferenceDays);

        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableServiceDeadlineAlerts);
        Assert.Equal(45, persisted.StaleCostReferenceDays);
    }

    /// <summary>
    /// Son ajustes de comportamiento puro: cambiar SOLO el umbral de costo viejo (sin tocar ningun otro
    /// flag) NO dispara ninguna validacion cruzada. Asegura que no se acoplo por error a GR-002/GR-013.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_StaleDaysAloneWithCancellationFlowOff_DoesNotThrow()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.EnableNewCancellationFlow = false);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.StaleCostReferenceDays = 75;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.Equal(75, result.StaleCostReferenceDays);
    }
}
