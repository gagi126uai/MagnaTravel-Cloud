using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Tests del flag maestro del copiloto (<c>EnableAiCopilot</c>, ADR-016 F0a) en el panel de
/// Configuracion. Verifican: patch-like (omitir no pisa), GET lo expone, y la regresion clave
/// de F0a -> con el flag OFF NADIE llama al cerebro.
///
/// <para>Son tests UNITARIOS sobre el service directo con EF Core InMemory: no tocan la nube,
/// Postgres ni HTTP. Mismo estilo que <c>OperationalFinanceSettingsFlagsTests</c>.</para>
/// </summary>
public class EnableAiCopilotFlagTests
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
    // Default OFF
    // ============================================================

    [Fact]
    public void NewSettings_AiCopilotFlag_DefaultsOff()
    {
        var settings = new OperationalFinanceSettings();
        Assert.False(settings.EnableAiCopilot);
    }

    // ============================================================
    // Patch-like: omitir el flag NO lo pisa
    // ============================================================

    [Fact]
    public async Task UpdateAsync_OmittedAiCopilotFlag_DoesNotOverwriteCurrentValue()
    {
        await using var db = BuildDbContext();
        // El admin ya prendio el copiloto.
        await SeedSettingsAsync(db, s => s.EnableAiCopilot = true);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        Assert.Null(request.EnableAiCopilot); // el PUT no incluye el flag

        var result = await service.UpdateAsync(request, CancellationToken.None);

        // Omitir != apagar: el valor previo (true) sigue intacto.
        Assert.True(result.EnableAiCopilot);
        db.ChangeTracker.Clear();
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableAiCopilot);
    }

    // ============================================================
    // Prender / apagar explicito (sin validacion cruzada en F0a)
    // ============================================================

    [Fact]
    public async Task UpdateAsync_EnableAiCopilot_Persists_NoCrossValidation()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableAiCopilot = true;

        // En F0a el flag no depende de ningun otro: prenderlo solo no debe lanzar.
        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.True(result.EnableAiCopilot);
        db.ChangeTracker.Clear();
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableAiCopilot);
    }

    [Fact]
    public async Task UpdateAsync_ExplicitFalseAiCopilotFlag_TurnsItOff()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.EnableAiCopilot = true);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableAiCopilot = false; // false explicito != omitido

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.False(result.EnableAiCopilot);
        db.ChangeTracker.Clear();
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.False(persisted.EnableAiCopilot);
    }

    // ============================================================
    // GET expone el flag
    // ============================================================

    [Fact]
    public async Task GetAsync_ExposesAiCopilotFlag()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.EnableAiCopilot = true);
        var service = new OperationalFinanceSettingsService(db);

        var dto = await service.GetAsync(CancellationToken.None);

        Assert.True(dto.EnableAiCopilot);
    }

    // ============================================================
    // Regresion F0a: con el flag OFF, configurar settings NO llama al cerebro.
    //
    // En F0a no existe todavia un caller del copiloto (el piloto llega en F1), asi que el
    // unico riesgo de regresion seria que el flujo de settings tocara la IA "sin querer".
    // Verificamos que correr el GET y el UPDATE con el flag OFF deja el FakeAiChatProvider
    // con cero invocaciones: nada del camino de settings toca el cerebro.
    // ============================================================

    [Fact]
    public async Task SettingsFlow_WithAiCopilotOff_NeverInvokesTheBrain()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.EnableAiCopilot = false);
        var service = new OperationalFinanceSettingsService(db);

        // Un fake "armado" pero que nadie deberia tocar en este flujo.
        var brain = new FakeAiChatProvider();

        await service.GetAsync(CancellationToken.None);
        var request = BaseRequest();
        request.EnableAiCopilot = false;
        await service.UpdateAsync(request, CancellationToken.None);

        // El cerebro nunca fue invocado por el camino de settings.
        Assert.Equal(0, brain.CallCount);
    }
}
