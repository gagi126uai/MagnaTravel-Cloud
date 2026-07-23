using System.Net.Http;
using AutoMapper;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Regresion 2026-07-23 (bug real confirmado en PROD): <see cref="InvoiceService.CreateAsync"/>
/// END TO END (con el <see cref="AfipService"/> REAL, no mockeado) para una reserva SIN cliente
/// asignado. Antes de este fix, <c>AfipService.CreatePendingInvoice</c> tiraba apenas veia
/// <c>Payer == null</c> y el 500 llegaba SIEMPRE, sin importar el estado de deuda de la reserva.
///
/// <para>Cubre los DOS caminos reales que puede tomar el guard de deuda ANTES de llegar a
/// <c>CreatePendingInvoice</c> (que es donde vivia el bug): reserva con deuda + ForceIssue (el
/// vendedor fuerza la emision con motivo), y reserva 100% cobrada (sin deuda, sin necesidad de
/// forzar nada). En los dos casos la falta de cliente NUNCA debe ser la causa de un error.</para>
/// </summary>
public class InvoiceServiceCreateNoPayerTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;

    public InvoiceServiceCreateNoPayerTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
    }

    /// <summary>
    /// El store implementa TAMBIEN IUserRoleStore (via Mock.As): CreateAsync con ForceIssue=true dispara
    /// NotifyAdminsOfForcedInvoiceAsync, que llama a UserManager.GetUsersInRoleAsync("Admin"). Con un store
    /// que solo implementa IUserStore, esa llamada tira NotSupportedException ANTES de llegar a nuestro
    /// assert — no es un bug de produccion (ahi el store real de Identity si soporta roles), es un hueco del
    /// mock. GetUsersInRoleAsync -> lista vacia: el aviso a admins queda como no-op, que es justamente lo que
    /// necesitamos para poder assertar solo el comportamiento bajo prueba (Fix 2).
    /// </summary>
    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        var roleStore = store.As<IUserRoleStore<ApplicationUser>>();
        roleStore
            .Setup(s => s.GetUsersInRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ApplicationUser>());

        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>
    /// Arma InvoiceService con el AfipService REAL (no mock): es lo que hace que este test sea
    /// "end to end" para el bug — ejercita CreatePendingInvoice de verdad, el mismo metodo que
    /// tenia el throw.
    /// </summary>
    private InvoiceService BuildServiceWithRealAfip(AppDbContext context, string afipInvoiceControlMode)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings { AfipInvoiceControlMode = afipInvoiceControlMode });

        var protector = new Mock<ISensitiveDataProtector>();
        protector.Setup(p => p.UnprotectString(It.IsAny<string?>())).Returns((string? v) => v);
        protector.Setup(p => p.UnprotectBytes(It.IsAny<byte[]?>())).Returns((byte[]? v) => v);
        var afipService = new AfipService(context, NullLogger<AfipService>.Instance, new HttpClient(), protector.Object);

        return new InvoiceService(
            context, new EntityReferenceResolver(context), afipService,
            new Mock<IInvoicePdfService>().Object, _mapper, new Mock<IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance, settings.Object, BuildUserManager(),
            permissionResolver: null, httpContextAccessor: null);
    }

    private static CreateInvoiceRequest BuildRequest(bool forceIssue, string? forceReason = null)
        => new()
        {
            ReservaId = string.Empty, // se completa en cada test con el PublicId real
            ForceIssue = forceIssue,
            ForceReason = forceReason,
            Items = new List<InvoiceItemDto>
            {
                new()
                {
                    Description = "Paquete turistico",
                    Quantity = 1,
                    UnitPrice = 1000m,
                    Total = 1000m,
                    AlicuotaIvaId = 5,
                },
            },
        };

    /// <summary>
    /// CASO (b1) del brief: reserva CON deuda (Balance > 0), sin cliente asignado, forzando la
    /// emision con motivo (el circuito normal de "vendedor override deuda"). Debe emitir a
    /// Consumidor Final sin que la falta de cliente interfiera con el guard de deuda.
    /// </summary>
    [Fact]
    public async Task CreateAsync_ReservaConDeuda_SinPayer_ForceIssue_EmiteAConsumidorFinal()
    {
        using var context = new AppDbContext(_dbOptions);
        context.AfipSettings.Add(new AfipSettings { TaxCondition = "Responsable Inscripto" });
        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-DEUDA-1",
            Name = "Reserva con deuda sin cliente",
            Status = EstadoReserva.Confirmed,
            PayerId = null,
            Balance = 500m, // <-- deuda: exige ForceIssue
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = BuildServiceWithRealAfip(context, AfipInvoiceControlModes.AllowAgentOverrideWithReason);
        var request = BuildRequest(forceIssue: true, forceReason: "Cliente pidio factura anticipada, autorizado por gerencia");
        request.ReservaId = reserva.PublicId.ToString();

        var dto = await service.CreateAsync(request, userId: "u1", userName: "Vendedor Test", CancellationToken.None);

        Assert.NotNull(dto);
        var persisted = await context.Invoices.AsNoTracking().SingleAsync(i => i.ReservaId == reserva.Id);
        Assert.Null(persisted.CustomerSnapshot);
        Assert.True(persisted.WasForced);
    }

    /// <summary>
    /// CASO (b2) del brief: reserva 100% COBRADA (Balance = 0), sin cliente asignado, SIN
    /// necesidad de ForceIssue (no hay deuda que forzar). Mismo resultado: emite a Consumidor
    /// Final sin tirar.
    /// </summary>
    [Fact]
    public async Task CreateAsync_ReservaCienPorCientoCobrada_SinPayer_EmiteAConsumidorFinal_SinForceIssue()
    {
        using var context = new AppDbContext(_dbOptions);
        context.AfipSettings.Add(new AfipSettings { TaxCondition = "Monotributo" });
        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-COBRADA-1",
            Name = "Reserva cobrada sin cliente",
            Status = EstadoReserva.Confirmed,
            PayerId = null,
            Balance = 0m, // 100% cobrada: EconomicRulesHelper.IsEconomicallySettled = true
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = BuildServiceWithRealAfip(context, AfipInvoiceControlModes.AllowAgentOverrideWithReason);
        var request = BuildRequest(forceIssue: false);
        request.ReservaId = reserva.PublicId.ToString();

        var dto = await service.CreateAsync(request, userId: "u1", userName: "Vendedor Test", CancellationToken.None);

        Assert.NotNull(dto);
        var persisted = await context.Invoices.AsNoTracking().SingleAsync(i => i.ReservaId == reserva.Id);
        Assert.Equal(InvoiceTypeResolver.FacturaC, persisted.TipoComprobante); // emisor Monotributo -> siempre C
        Assert.Null(persisted.CustomerSnapshot);
        Assert.False(persisted.WasForced);
    }
}
