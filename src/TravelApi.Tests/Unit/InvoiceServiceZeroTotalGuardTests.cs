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
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-07-04) Guard de importe en <see cref="InvoiceService.CreateAsync"/>: ningún comprobante puede emitirse por
/// total cero. Un escenario real es la anulación que arma una factura sin líneas con valor: sin el guard se crearía
/// una factura PENDING en $0 y se la mandaría a AFIP para nada. El guard corre ANTES de tocar AFIP y devuelve un
/// mensaje apto para el usuario final (sin jerga ni internos).
/// </summary>
public class InvoiceServiceZeroTotalGuardTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;

    public InvoiceServiceZeroTotalGuardTests()
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
    /// InvoiceService con un AfipService mockeado que NUNCA debe llamarse en el caso $0 (el guard corta antes).
    /// Devolvemos el mock para poder verificar que CreatePendingInvoice no se invocó.
    /// </summary>
    private (InvoiceService Service, Mock<IAfipService> Afip) BuildService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var afip = new Mock<IAfipService>();
        var service = new InvoiceService(
            context, new EntityReferenceResolver(context), afip.Object,
            new Mock<IInvoicePdfService>().Object, _mapper, new Mock<IBackgroundJobClient>().Object,
            NullLogger<InvoiceService>.Instance, settings.Object, BuildUserManager(),
            permissionResolver: null, httpContextAccessor: null);
        return (service, afip);
    }

    private static async Task<string> SeedReservaAsync(AppDbContext context)
    {
        context.AfipSettings.Add(new AfipSettings { TaxCondition = "Monotributo" });
        context.Customers.Add(new Customer { Id = 50, FullName = "Cliente Test", TaxCondition = "Consumidor Final" });
        var reserva = new Reserva
        {
            Id = 1, PublicId = Guid.NewGuid(), NumeroReserva = "F-ZERO", Name = "Reserva cero",
            Status = EstadoReserva.Confirmed, PayerId = 50, Balance = 0m,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva.PublicId.ToString();
    }

    [Fact]
    public async Task CreateAsync_WithEmptyItems_Rejects_WithBusinessMessage_AndDoesNotTouchAfip()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reservaPublicId = await SeedReservaAsync(context);
        var (service, afip) = BuildService(context);

        var request = new CreateInvoiceRequest
        {
            ReservaId = reservaPublicId,
            Items = new List<InvoiceItemDto>(), // sin líneas -> total 0
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User One", CancellationToken.None));
        Assert.Equal("El total del comprobante debe ser mayor a cero.", ex.Message);

        // No se creó factura ni se llamó a AFIP.
        afip.Verify(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()), Times.Never);
        Assert.Empty(await context.Invoices.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_WithZeroValuedItem_Rejects_WithBusinessMessage()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reservaPublicId = await SeedReservaAsync(context);
        var (service, afip) = BuildService(context);

        var request = new CreateInvoiceRequest
        {
            ReservaId = reservaPublicId,
            Items = new List<InvoiceItemDto>
            {
                new() { Description = "Item sin valor", Quantity = 1, UnitPrice = 0m, Total = 0m, AlicuotaIvaId = 3 },
            },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "u1", "User One", CancellationToken.None));
        Assert.Equal("El total del comprobante debe ser mayor a cero.", ex.Message);
        afip.Verify(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()), Times.Never);
    }
}
