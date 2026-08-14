using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "PDF de presupuesto" (maqueta v2 firmada, 2026-08-11/12), TANDA 3: cubre lo que NO es puro —
/// el guard de etapa (solo Presupuesto), la persistencia del texto de "Formas de pago" con su fallback
/// a la plantilla, y que el generador arme bytes de verdad (usando el <see cref="QuotePdfService"/>
/// REAL — es determinístico y no toca disco/red, así que no hace falta mockearlo).
/// </summary>
public class ReservaServiceBudgetPdfTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>Mapper minimo que refleja los campos que estos tests necesitan verificar (mismo patron que Adr020LifecycleTests).</summary>
    private static IMapper BuildMapper()
    {
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto
              {
                  PublicId = r.PublicId,
                  NumeroReserva = r.NumeroReserva,
                  Name = r.Name,
                  Status = r.Status,
                  BudgetPaymentTermsText = r.BudgetPaymentTermsText,
              });
        return mapper.Object;
    }

    private static ReservaService NewReservaService(AppDbContext context, bool withQuotePdfService = true)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        return new ReservaService(
            context, BuildMapper(), settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            quotePdfService: withQuotePdfService ? new QuotePdfService() : null,
            reportService: null);
    }

    private static Reserva Reserva(int id, string status, string numero = "2026-1") => new()
    {
        Id = id,
        NumeroReserva = numero,
        Name = $"Reserva {id}",
        Status = status,
    };

    // ================================================================================
    // Guard de etapa: SOLO Presupuesto (Cotización/Presupuesto).
    // ================================================================================

    [Fact]
    public async Task GetBudgetPdfAsync_OutsidePresupuestoStage_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetBudgetPdfAsync("1", porPersona: true));

        Assert.Contains("presupuesto", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    public async Task GetBudgetPdfAsync_InPresupuestoStage_GeneratesNonEmptyPdf(string status)
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, status, numero: "2026-1067"));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Bayahibe", City = "Bayahibe",
            Status = "Solicitado", SalePrice = 1000m, Currency = "USD", RoomType = "Doble",
        });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var (bytes, numero) = await service.GetBudgetPdfAsync("1", porPersona: true);

        Assert.NotEmpty(bytes);
        Assert.Equal("2026-1067", numero);
        // Firma de un PDF valido (QuestPDF genera PDF real): arranca con "%PDF-".
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
    }

    // ================================================================================
    // Fix de review (2026-08-14): GetBudgetPdfAsync cargaba la reserva SIN Include(r => r.Servicios) --
    // el bloque "Otro" (ServicioReserva) del renderer nunca se dibujaba en producción porque la colección
    // llegaba siempre vacía (sin lazy loading configurado). El harness de QuotePdfService no lo detectaba
    // porque arma la Reserva a mano en memoria, sin pasar por esta query real. Este test usa el camino de
    // PRODUCCIÓN (GetBudgetPdfAsync, con el servicio sembrado de verdad en el DbContext) para que un
    // Include que se vuelva a caer en el futuro rompa acá, no en el navegador de Gastón.
    // ================================================================================

    [Fact]
    public async Task GetBudgetPdfAsync_WithGenericServicioReserva_LoadsItFromDb_AndDrawsIt()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Budget, numero: "2026-1"));
        ctx.Reservas.Add(Reserva(2, EstadoReserva.Budget, numero: "2026-2"));
        // Servicio "Otro" sembrado como fila REAL de base (no un objeto en memoria armado por el test) --
        // asi el Include nuevo es el UNICO camino por el que puede llegar al renderer.
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 20, ReservaId = 1, Status = "Solicitado",
            ServiceType = ServiceTypes.Other, Description = "Alquiler de auto 7 días",
            SalePrice = 350m, Currency = "USD",
        });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var (bytesConServicio, _) = await service.GetBudgetPdfAsync("1", porPersona: false);
        var (bytesSinServicio, _) = await service.GetBudgetPdfAsync("2", porPersona: false);

        Assert.NotEmpty(bytesConServicio);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytesConServicio, 0, 5));
        // Criterio observable (mismo que usa QuotePdfServiceMaquetaVisualHarnessTests para "algo se
        // dibujó"): sin poder leer píxeles desde un test, un bloque nuevo con texto real ("Alquiler de
        // auto 7 días" + su tarifa) tiene que agregar bytes al PDF -- si el Include se rompe de nuevo y
        // la colección vuelve a llegar vacía, este assert lo detecta.
        Assert.True(bytesConServicio.Length > bytesSinServicio.Length,
            "El PDF con el servicio 'Otro' cargado en la base debería pesar más que el mismo presupuesto sin ningún servicio.");
    }

    [Fact]
    public async Task GetBudgetPdfAsync_ReservaNotFound_ThrowsKeyNotFound()
    {
        await using var ctx = NewContext();
        var service = NewReservaService(ctx);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetBudgetPdfAsync("999", porPersona: true));
    }

    [Fact]
    public async Task GetBudgetPdfAsync_ZeroPassengersLoaded_StillGeneratesPdf_FallsBackToTotal()
    {
        // AdultCount/ChildCount/InfantCount en 0 (nadie cargo pasajeros todavia): la generacion NO debe
        // reventar (division por cero) — cae a tarifa TOTAL (ver QuoteBudgetPdfRulesTests para el detalle
        // de la regla pura).
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Budget));
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel Test", City = "Ciudad",
            Status = "Solicitado", SalePrice = 1000m,
        });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var (bytes, _) = await service.GetBudgetPdfAsync("1", porPersona: true);

        Assert.NotEmpty(bytes);
    }

    // ================================================================================
    // "Formas de pago": texto propio de la reserva (persistencia + borrado).
    // ================================================================================

    [Fact]
    public async Task UpdateBudgetPaymentTermsAsync_SavesText()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Budget));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        await service.UpdateBudgetPaymentTermsAsync("1", new UpdateBudgetPaymentTermsRequest("3 cuotas sin interés"));

        var stored = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal("3 cuotas sin interés", stored.BudgetPaymentTermsText);
    }

    [Fact]
    public async Task UpdateBudgetPaymentTermsAsync_EmptyText_ClearsIt()
    {
        await using var ctx = NewContext();
        var reserva = Reserva(1, EstadoReserva.Budget);
        reserva.BudgetPaymentTermsText = "Texto viejo";
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        await service.UpdateBudgetPaymentTermsAsync("1", new UpdateBudgetPaymentTermsRequest("   "));

        var stored = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Null(stored.BudgetPaymentTermsText);
    }

    [Fact]
    public async Task UpdateBudgetPaymentTermsAsync_TooLong_Throws()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Budget));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        var tooLong = new string('a', 4001);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateBudgetPaymentTermsAsync("1", new UpdateBudgetPaymentTermsRequest(tooLong)));
    }

    [Fact]
    public async Task UpdateBudgetPaymentTermsAsync_ReservaNotFound_ThrowsKeyNotFound()
    {
        await using var ctx = NewContext();
        var service = NewReservaService(ctx);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.UpdateBudgetPaymentTermsAsync("999", new UpdateBudgetPaymentTermsRequest("cualquier texto")));
    }

    [Fact]
    public async Task UpdateBudgetPaymentTermsAsync_OutsidePresupuestoStage_StillWorks_NoStageGate()
    {
        // Desvío deliberado (documentado en el XML-doc de IReservaService.UpdateBudgetPaymentTermsAsync):
        // este texto NO mueve plata ni fechas, así que a diferencia de GetBudgetPdfAsync (que SÍ exige
        // etapa Presupuesto) este PATCH no tiene candado de estado — se puede corregir en cualquier
        // momento (ej. para dejarlo prolijo en el historial, aunque el PDF ya no se vuelva a emitir).
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.InManagement));
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        await service.UpdateBudgetPaymentTermsAsync("1", new UpdateBudgetPaymentTermsRequest("Seña 30% + saldo"));

        var stored = await ctx.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal("Seña 30% + saldo", stored.BudgetPaymentTermsText);
    }
}
