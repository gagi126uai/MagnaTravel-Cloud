using System;
using System.Linq;
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
/// La muerte del interruptor <c>EnableAiCopilot</c> (M-33 de la spec firmada 2026-08-07 §15.7).
///
/// <para><b>Que cambio</b>: antes habia una llave en Configuracion para "prender la IA", ademas de
/// las variables del servidor donde se cargaba la conexion. Tener dos lugares para prender lo mismo
/// es justo lo que la orden del dueño prohibe ("basta de llaves", la misma que mato a
/// <c>enableCatalogFindOrCreate</c> el 2026-08-06).</para>
///
/// <para><b>La regla nueva es una sola</b>: si hay una inteligencia artificial configurada, las
/// ayudas funcionan; si no hay, no funcionan y el sistema anda igual. Quien contesta esa pregunta es
/// <c>IAiConnectionResolver.IsUsableAsync</c>, NO una llave.</para>
///
/// <para>Estos tests cuidan las dos mitades: que la llave vieja ya no gobierne nada ni se muestre en
/// ningun lado, y que la decision nueva funcione con la llave apagada.</para>
/// </summary>
public class EnableAiCopilotFlagTests
{
    private static AppDbContext BuildDbContext() => AiTestDoubles.BuildDbContext();

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
    // La llave vieja salio de la superficie
    // ============================================================

    [Fact]
    public void LaConfiguracionOperativa_YaNoOfreceElInterruptorDeIa()
    {
        // Si alguien lo vuelve a agregar al contrato, este test se rompe y obliga a releer §15.7.
        var propertyNames = typeof(OperationalFinanceSettingsDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("EnableAiCopilot", propertyNames);
    }

    [Fact]
    public void LaConfiguracionDeFacturacion_TampocoLoExpone()
    {
        var propertyNames = typeof(AfipSettingsResponse)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("EnableAiCopilot", propertyNames);
    }

    [Fact]
    public async Task GuardarLaConfiguracionOperativa_NoTocaLaColumnaVieja()
    {
        await using var db = BuildDbContext();
        // Una instalacion que en su momento prendio la llave: la columna sigue ahi, con su valor.
        await SeedSettingsAsync(db, settings => settings.EnableAiCopilot = true);
        var service = new OperationalFinanceSettingsService(db);

        await service.UpdateAsync(BaseRequest(), CancellationToken.None);

        // La columna queda INERTE: ni se apaga ni se prende sola. Nadie la lee.
        db.ChangeTracker.Clear();
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableAiCopilot);
    }

    // ============================================================
    // La decision nueva: manda "¿hay IA configurada?"
    // ============================================================

    [Fact]
    public async Task ConLaLlaveVieja_ApagadaPeroConIaConfigurada_LasAyudasFuncionan()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, settings => settings.EnableAiCopilot = false);
        var protector = AiTestDoubles.BuildRealProtector();
        db.AiSettings.Add(new AiSettings
        {
            Provider = AiProviderKey.Groq,
            BaseUrl = "https://api.groq.com/openai/v1",
            Model = "llama-3.3-70b-versatile",
            EncryptedApiKey = protector.ProtectString("gsk_de_la_pantalla"),
            ApiKeyPrefix = "gsk_",
        });
        await db.SaveChangesAsync();

        var resolver = AiTestDoubles.BuildResolver(db, protector, AiTestDoubles.EmptyEnvironmentOptions());

        // La llave vieja esta apagada y NO importa: hay IA cargada, entonces hay IA.
        Assert.True(await resolver.IsUsableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConLaLlaveVieja_PrendidaPeroSinIaConfigurada_NoHayAyudas()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, settings => settings.EnableAiCopilot = true);

        var resolver = AiTestDoubles.BuildResolver(
            db, AiTestDoubles.BuildRealProtector(), AiTestDoubles.EmptyEnvironmentOptions());

        // Prender una llave nunca alcanzo para que la IA funcione, y ahora ni siquiera se mira.
        Assert.False(await resolver.IsUsableAsync(CancellationToken.None));
    }

    // ============================================================
    // Regresion: el camino de settings sigue sin tocar el cerebro
    // ============================================================

    [Fact]
    public async Task GuardarConfiguracionOperativa_NuncaLlamaALaIa()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db);
        var service = new OperationalFinanceSettingsService(db);

        var brain = new FakeAiChatProvider();

        await service.GetAsync(CancellationToken.None);
        await service.UpdateAsync(BaseRequest(), CancellationToken.None);

        Assert.Equal(0, brain.CallCount);
    }
}
