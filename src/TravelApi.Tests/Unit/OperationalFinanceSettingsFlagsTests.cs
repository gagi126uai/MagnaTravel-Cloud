using System;
using System.ComponentModel.DataAnnotations;
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
/// Tests del panel de Configuracion -> Operational Finance para los dos flags nuevos
/// expuestos via PUT /api/settings/operational-finance:
///
/// <list type="bullet">
///   <item><b>EnableSoldToSettleStates</b> (ciclo de vida extendido): flag de comportamiento,
///   sin dependencias, patch-like.</item>
///   <item><b>EnableCancellationDebitNote</b> (ND en cancelacion): patch-like + validacion
///   cruzada (requiere EnableNewCancellationFlow=true), mismo estilo GR-002.</item>
/// </list>
///
/// <para>Son tests UNITARIOS sobre el service directo con EF Core InMemory: no tocan ARCA,
/// Postgres ni HTTP. El service llama OperationalFinanceSchemaBootstrapper.EnsureAsync solo
/// cuando Postgres tira UndefinedTable; con InMemory ese camino no se ejecuta.</para>
/// </summary>
public class OperationalFinanceSettingsFlagsTests
{
    /// <summary>
    /// Cada test arranca con su propia base InMemory aislada (Guid unico) para no
    /// pisarse entre tests que corren en paralelo.
    /// </summary>
    private static AppDbContext BuildDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Siembra una fila de settings con valores conocidos para poder verificar
    /// que el patch-like NO la pisa cuando el campo viene omitido.
    /// </summary>
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

    /// <summary>
    /// DTO base valido (los campos no-nullable que el binder siempre completa).
    /// Los flags nuevos quedan en null por defecto = omitidos = no se tocan.
    /// </summary>
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
    // (a) Patch-like: omitir el flag NO lo pisa
    // ============================================================

    [Fact]
    public async Task UpdateAsync_OmittedDebitNoteFlag_DoesNotOverwriteCurrentValue()
    {
        await using var db = BuildDbContext();
        // Estado actual: cancelacion nueva ON + ND ya prendida (combinacion valida).
        await SeedSettingsAsync(db, s =>
        {
            s.EnableNewCancellationFlow = true;
            s.EnableCancellationDebitNote = true;
        });
        var service = new OperationalFinanceSettingsService(db);

        // El PUT NO incluye EnableCancellationDebitNote (queda null).
        var request = BaseRequest();
        Assert.Null(request.EnableCancellationDebitNote);

        var result = await service.UpdateAsync(request, CancellationToken.None);

        // El flag fiscal sigue prendido: un PUT legacy no lo apago sin querer.
        Assert.True(result.EnableCancellationDebitNote);
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableCancellationDebitNote);
    }

    // ============================================================
    // (b) Validacion cruzada: ND ON con flujo de cancelacion OFF -> rechaza
    // ============================================================

    [Fact]
    public async Task UpdateAsync_EnableDebitNoteWithoutCancellationFlow_ThrowsValidation()
    {
        await using var db = BuildDbContext();
        // Flujo de cancelacion nuevo OFF -> prender la ND es invalido.
        await SeedSettingsAsync(db, s => s.EnableNewCancellationFlow = false);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableCancellationDebitNote = true;

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => service.UpdateAsync(request, CancellationToken.None));

        Assert.Contains("GR-013", ex.Message);
        Assert.Contains("EnableNewCancellationFlow=true", ex.Message);

        // Nada se persistio: la validacion corre antes del SaveChanges del flag.
        // IMPORTANTE: limpiamos el ChangeTracker antes de re-leer. Sin esto, EF
        // devuelve la MISMA instancia trackeada (que el service ya muto en memoria
        // aunque nunca llamo SaveChanges) por el identity-map, y el assert daria un
        // falso verde/rojo. Con Clear() forzamos una lectura real del store, que es
        // lo que de verdad queremos verificar: que el flag NO quedo guardado.
        db.ChangeTracker.Clear();
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.False(persisted.EnableCancellationDebitNote);
    }

    // ============================================================
    // (b.2) Apagar explicitamente (false != omitido): SI persiste el apagado
    // ============================================================

    [Fact]
    public async Task UpdateAsync_ExplicitFalseDebitNoteFlag_TurnsItOff()
    {
        await using var db = BuildDbContext();
        // Estado actual: cancelacion nueva ON + ND prendida.
        await SeedSettingsAsync(db, s =>
        {
            s.EnableNewCancellationFlow = true;
            s.EnableCancellationDebitNote = true;
        });
        var service = new OperationalFinanceSettingsService(db);

        // El PUT manda explicitamente false (no null): apagar es distinto de omitir.
        var request = BaseRequest();
        request.EnableCancellationDebitNote = false;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        // Mandar false SI apaga el flag (no se confunde con "omitido = no tocar").
        Assert.False(result.EnableCancellationDebitNote);
        db.ChangeTracker.Clear();
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.False(persisted.EnableCancellationDebitNote);
    }

    // ============================================================
    // (b.3) GR-013 NO dispara al apagar la ND con el flujo de cancelacion OFF
    // ============================================================

    [Fact]
    public async Task UpdateAsync_DebitNoteOffWithCancellationFlowOff_DoesNotThrow()
    {
        await using var db = BuildDbContext();
        // Flujo de cancelacion nuevo OFF y ND tambien OFF: combinacion coherente.
        await SeedSettingsAsync(db, s => s.EnableNewCancellationFlow = false);
        var service = new OperationalFinanceSettingsService(db);

        // Apagar/omitir la ND no debe bloquearse aunque el flujo este OFF: GR-013
        // solo prohibe PRENDER la ND sin el flujo, no guardar settings con la ND apagada.
        var request = BaseRequest();
        request.EnableCancellationDebitNote = false;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.False(result.EnableCancellationDebitNote);
    }

    // ============================================================
    // (c) Camino feliz: prender ambos flags validos -> persiste
    // ============================================================

    [Fact]
    public async Task UpdateAsync_BothFlagsValid_Persists()
    {
        await using var db = BuildDbContext();
        // Pre-condicion para la ND: el flujo de cancelacion nuevo esta ON.
        await SeedSettingsAsync(db, s => s.EnableNewCancellationFlow = true);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.EnableCancellationDebitNote = true;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.True(result.EnableCancellationDebitNote);

        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.True(persisted.EnableCancellationDebitNote);
    }

    /// <summary>
    /// El GET expone el flag de ND (no solo el PUT). Asegura que la pantalla de
    /// Configuracion lo pueda mostrar como toggle.
    /// </summary>
    [Fact]
    public async Task GetAsync_ExposesDebitNoteFlag()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s =>
        {
            s.EnableNewCancellationFlow = true;
            s.EnableCancellationDebitNote = true;
        });
        var service = new OperationalFinanceSettingsService(db);

        var dto = await service.GetAsync(CancellationToken.None);

        Assert.True(dto.EnableCancellationDebitNote);
    }

    // ============================================================
    // Auditoria ERP 2026-06-13: porcentaje unico de comision del vendedor
    // ============================================================

    [Fact]
    public async Task UpdateAsync_SellerCommissionPercent_Persists()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db);
        var service = new OperationalFinanceSettingsService(db);

        var request = BaseRequest();
        request.SellerCommissionPercent = 12.5m;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.Equal(12.5m, result.SellerCommissionPercent);
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.Equal(12.5m, persisted.SellerCommissionPercent);
    }

    [Fact]
    public async Task UpdateAsync_OmittedSellerCommissionPercent_DoesNotOverwrite()
    {
        await using var db = BuildDbContext();
        // El admin ya configuro 10%.
        await SeedSettingsAsync(db, s => s.SellerCommissionPercent = 10m);
        var service = new OperationalFinanceSettingsService(db);

        // El PUT NO incluye SellerCommissionPercent (queda null = omitido).
        var request = BaseRequest();
        Assert.Null(request.SellerCommissionPercent);

        var result = await service.UpdateAsync(request, CancellationToken.None);

        // Un PUT legacy que no manda el campo no lo vuelve a 0.
        Assert.Equal(10m, result.SellerCommissionPercent);
        var persisted = await db.OperationalFinanceSettings.SingleAsync();
        Assert.Equal(10m, persisted.SellerCommissionPercent);
    }

    [Fact]
    public async Task UpdateAsync_SellerCommissionPercent_ClampsOutOfRange()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db);
        var service = new OperationalFinanceSettingsService(db);

        // Defensa adicional al [Range] del DTO: si entra un valor fuera de 0..100 por un camino que no pasa
        // por el binder (seed/test), el service lo clampa en vez de persistir basura.
        var request = BaseRequest();
        request.SellerCommissionPercent = 150m;

        var result = await service.UpdateAsync(request, CancellationToken.None);

        Assert.Equal(100m, result.SellerCommissionPercent);
    }

    [Fact]
    public async Task GetAsync_ExposesSellerCommissionPercent()
    {
        await using var db = BuildDbContext();
        await SeedSettingsAsync(db, s => s.SellerCommissionPercent = 8m);
        var service = new OperationalFinanceSettingsService(db);

        var dto = await service.GetAsync(CancellationToken.None);

        Assert.Equal(8m, dto.SellerCommissionPercent);
    }
}
