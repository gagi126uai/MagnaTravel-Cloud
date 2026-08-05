using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-043 Fase 1 (2026-08-05, "gate de facturar"): cubre el guard REAL de
/// <see cref="InvoiceService.CreateAsync"/> cuando la reserva tiene <c>HasUnacknowledgedChanges</c>
/// prendido (el operador avisó un cambio que todavía nadie revisó con "Dar OK").
///
/// <para>Verifica tres cosas del §8.1 del ADR: (1) la FACTURA DE VENTA nueva se rechaza con la excepcion
/// tipada correcta; (2) sin la marca, la emision sigue funcionando igual que siempre (no rompe nada
/// existente); (3) la NC/ND NO pasa por este guard — es la accion que resuelve el cambio pendiente, asi
/// que bloquearla trabaria ese flujo.</para>
/// </summary>
public class Adr043InvoiceGateChangesReviewTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;

    public Adr043InvoiceGateChangesReviewTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    /// <summary>
    /// Arma InvoiceService con un IAfipService MOCKEADO: estos tests ejercitan el guard de ADR-043 (que
    /// corre ANTES de tocar AFIP), no el circuito fiscal real. El mock de CreatePendingInvoice persiste una
    /// Invoice PENDING minima en el mismo contexto, como haria el AfipService real, para que el resto del
    /// metodo (mapeo a DTO, aviso de descuadre) tenga algo que leer.
    /// </summary>
    private InvoiceService BuildService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var afipService = new Mock<IAfipService>();
        afipService
            .Setup(s => s.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .ReturnsAsync((int reservaId, CreateInvoiceRequest request) =>
            {
                var invoice = new Invoice
                {
                    PublicId = Guid.NewGuid(),
                    ReservaId = reservaId,
                    Resultado = "PENDING",
                };
                context.Invoices.Add(invoice);
                context.SaveChanges();
                return invoice;
            });

        return new InvoiceService(
            context, new EntityReferenceResolver(context), afipService.Object,
            new Mock<IInvoicePdfService>().Object, _mapper, new Mock<IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance, settings.Object, BuildUserManager(),
            permissionResolver: null, httpContextAccessor: null);
    }

    private static Reserva SeedReserva(AppDbContext context, bool hasUnacknowledgedChanges, decimal balance = 0m)
    {
        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-ADR043-1",
            Name = "Reserva gate facturar",
            Status = EstadoReserva.Confirmed,
            Balance = balance,
            HasUnacknowledgedChanges = hasUnacknowledgedChanges,
        };
        context.Reservas.Add(reserva);
        context.SaveChanges();
        return reserva;
    }

    private static CreateInvoiceRequest BuildSaleInvoiceRequest(Reserva reserva) => new()
    {
        ReservaId = reserva.PublicId.ToString(),
        Items = new List<InvoiceItemDto>
        {
            new() { Description = "Paquete turistico", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 5 },
        },
    };

    [Fact]
    public async Task CreateAsync_FacturaDeVenta_ReservaConCambiosSinRevisar_Rechaza()
    {
        using var context = new AppDbContext(_dbOptions);
        var reserva = SeedReserva(context, hasUnacknowledgedChanges: true, balance: 0m);
        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<ReservaChangesPendingReviewException>(() =>
            service.CreateAsync(BuildSaleInvoiceRequest(reserva), "u1", "Vendedor Test", CancellationToken.None));

        // T-6: el texto es el MISMO que ya usa ReservaCapabilityPolicy para apagar el boton en el front.
        Assert.Equal(ReservaCapabilityPolicy.ChangesPendingReviewReason, ex.Message);
        Assert.Equal(ReservaChangesPendingReviewException.CodeValue, ex.Code);
    }

    [Fact]
    public async Task CreateAsync_FacturaDeVenta_ReservaSinCambiosSinRevisar_EmiteNormalmente()
    {
        using var context = new AppDbContext(_dbOptions);
        var reserva = SeedReserva(context, hasUnacknowledgedChanges: false, balance: 0m);
        var service = BuildService(context);

        var dto = await service.CreateAsync(BuildSaleInvoiceRequest(reserva), "u1", "Vendedor Test", CancellationToken.None);

        Assert.NotNull(dto);
        Assert.True(await context.Invoices.AsNoTracking().AnyAsync(i => i.ReservaId == reserva.Id));
    }

    /// <summary>
    /// §8.1 del ADR: el gate SOLO frena la factura de venta nueva. La NC/ND es la accion que resuelve el
    /// cambio pendiente sobre una reserva ya facturada — si la bloqueara, ese flujo quedaria trabado.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NotaDeCredito_ReservaConCambiosSinRevisar_NoDisparaElGate()
    {
        using var context = new AppDbContext(_dbOptions);
        var reserva = SeedReserva(context, hasUnacknowledgedChanges: true, balance: 0m);
        var service = BuildService(context);

        var request = BuildSaleInvoiceRequest(reserva);
        request.IsCreditNote = true;
        request.OriginalInvoiceId = Guid.NewGuid().ToString();

        // No debe tirar ReservaChangesPendingReviewException (el gate de ADR-043 no aplica a NC/ND). Con
        // AFIP mockeado no hay ningun otro guard que frene esta NC minima, asi que directamente no tira.
        var dto = await service.CreateAsync(request, "u1", "Vendedor Test", CancellationToken.None);
        Assert.NotNull(dto);
    }
}
