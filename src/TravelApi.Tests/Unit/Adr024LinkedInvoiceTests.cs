using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-024 item 4 (vinculo basico cobro&lt;-&gt;factura, 2026-06-12): el cobro puede vincularse de forma
/// INFORMATIVA a una factura. Reglas: solo se vincula a una factura de la MISMA reserva (si no, 400); el
/// vinculo NO congela el cobro (los guards de borrado/edicion miran RelatedInvoiceId, NO LinkedInvoiceId);
/// el vinculo se proyecta en el historial.
/// </summary>
public class Adr024LinkedInvoiceTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public Adr024LinkedInvoiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private PaymentService BuildPaymentService(AppDbContext context)
        => new(context, new EntityReferenceResolver(context), _mapper, _settingsServiceMock.Object, NullLogger<PaymentService>.Instance);

    /// <summary>PaymentService con caller que ve costos + view_all (para GetHistoryAsync sin enmascarado).</summary>
    private PaymentService BuildHistoryService(AppDbContext context, string userId = "tester")
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId), new(ClaimTypes.Role, "Admin") };
        var identity = new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        var resolverMock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost, Permissions.CobranzasViewAll };
        resolverMock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);

        return new PaymentService(
            context, new EntityReferenceResolver(context), _mapper, _settingsServiceMock.Object,
            NullLogger<PaymentService>.Instance, resolverMock.Object, accessor);
    }

    private static async Task<Reserva> SeedReservaAsync(AppDbContext context, int id, decimal salePrice = 1000m)
    {
        var reserva = new Reserva
        {
            Id = id,
            NumeroReserva = $"F-2026-{id:D4}",
            Name = $"Reserva {id}",
            Status = EstadoReserva.Confirmed,
            TotalSale = salePrice,
            TotalCost = 0m,
            Balance = salePrice,
            TotalPaid = 0m
        };
        context.Reservas.Add(reserva);
        context.Servicios.Add(new ServicioReserva
        {
            Id = id, ReservaId = id, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = salePrice, NetCost = 0m, Commission = salePrice,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return reserva;
    }

    private static async Task<Invoice> SeedInvoiceAsync(AppDbContext context, int id, int reservaId)
    {
        var invoice = new Invoice
        {
            Id = id,
            ReservaId = reservaId,
            TipoComprobante = 11, // Factura C
            PuntoDeVenta = 7,
            NumeroComprobante = id,
            Resultado = "A",
            CAE = "12345678901234",
            ImporteTotal = 1000m,
            ImporteNeto = 1000m,
            ImporteIva = 0m,
            CreatedAt = DateTime.UtcNow
        };
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice;
    }

    // ============================================================
    // Validacion de pertenencia a la reserva
    // ============================================================

    [Fact]
    public async Task CreatePayment_LinkToInvoiceOfAnotherReserva_Throws400()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reservaA = await SeedReservaAsync(context, id: 1);
        await SeedReservaAsync(context, id: 2);
        // Factura de la reserva 2.
        var invoiceOfReservaB = await SeedInvoiceAsync(context, id: 10, reservaId: 2);

        var service = BuildPaymentService(context);

        // Intentar cobrar en la reserva 1 vinculando una factura de la reserva 2 -> rechazo (ArgumentException -> 400).
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reservaA.PublicId.ToString(),
                Amount = 100m,
                Method = "Transfer",
                LinkedInvoicePublicId = invoiceOfReservaB.PublicId.ToString()
            },
            CancellationToken.None));

        Assert.Contains("misma reserva", ex.Message, StringComparison.OrdinalIgnoreCase);

        // No se creo ningun pago.
        Assert.Equal(0, await context.Payments.CountAsync());
    }

    [Fact]
    public async Task CreatePayment_LinkToInvoiceOfSameReserva_SetsLink()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, id: 1);
        var invoice = await SeedInvoiceAsync(context, id: 10, reservaId: 1);

        var service = BuildPaymentService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 100m,
                Method = "Transfer",
                LinkedInvoicePublicId = invoice.PublicId.ToString()
            },
            CancellationToken.None);

        var persisted = await context.Payments.AsNoTracking().FirstAsync(p => p.PublicId == dto.PublicId);
        Assert.Equal(invoice.Id, persisted.LinkedInvoiceId);
        // El vinculo es informativo: NO toca RelatedInvoiceId (el eje fiscal/economico).
        Assert.Null(persisted.RelatedInvoiceId);
    }

    [Fact]
    public async Task CreatePayment_WithoutLink_LeavesLinkNull()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, id: 1);

        var service = BuildPaymentService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 100m,
                Method = "Transfer"
                // LinkedInvoicePublicId no se manda -> sin vinculo (comportamiento previo).
            },
            CancellationToken.None);

        var persisted = await context.Payments.AsNoTracking().FirstAsync(p => p.PublicId == dto.PublicId);
        Assert.Null(persisted.LinkedInvoiceId);
    }

    [Fact]
    public async Task CreatePayment_LinkToNonexistentInvoice_Throws400()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, id: 1);

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 100m,
                Method = "Transfer",
                LinkedInvoicePublicId = Guid.NewGuid().ToString() // PublicId que no existe
            },
            CancellationToken.None));

        Assert.Contains("no existe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.Payments.CountAsync());
    }

    // ============================================================
    // El vinculo NO congela el cobro (guards miran RelatedInvoiceId, no LinkedInvoiceId)
    // ============================================================

    [Fact]
    public async Task PaymentWithLink_IsEditable()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, id: 1);
        var invoice = await SeedInvoiceAsync(context, id: 10, reservaId: 1);
        var service = BuildPaymentService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 100m,
                Method = "Transfer",
                LinkedInvoicePublicId = invoice.PublicId.ToString()
            },
            CancellationToken.None);

        // Editar el pago vinculado NO debe lanzar: el vinculo informativo no lo congela.
        await service.UpdatePaymentAsync(
            dto.PublicId.ToString(),
            new UpdatePaymentRequest { Amount = 150m, Method = "Cash", Reference = "TX-9" },
            CancellationToken.None);

        var refreshed = await context.Payments.AsNoTracking().FirstAsync(p => p.PublicId == dto.PublicId);
        Assert.Equal(150m, refreshed.Amount);
        Assert.Equal("Cash", refreshed.Method);
        // El vinculo se mantiene tras la edicion.
        Assert.Equal(invoice.Id, refreshed.LinkedInvoiceId);
    }

    [Fact]
    public async Task PaymentWithLink_IsDeletable()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, id: 1);
        var invoice = await SeedInvoiceAsync(context, id: 10, reservaId: 1);
        var service = BuildPaymentService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 100m,
                Method = "Transfer",
                LinkedInvoicePublicId = invoice.PublicId.ToString()
            },
            CancellationToken.None);

        // Borrar el pago vinculado NO debe lanzar (a diferencia de RelatedInvoiceId, que SI bloquea el borrado).
        await service.DeletePaymentAsync(dto.PublicId.ToString(), CancellationToken.None);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.PublicId == dto.PublicId);
        Assert.True(refreshed.IsDeleted);
    }

    // ============================================================
    // Proyeccion en el historial
    // ============================================================

    [Fact]
    public async Task GetHistory_ProjectsLinkedInvoicePublicId()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, id: 1);
        var invoice = await SeedInvoiceAsync(context, id: 10, reservaId: 1);
        var service = BuildHistoryService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                ReservaId = reserva.PublicId.ToString(),
                Amount = 100m,
                Method = "Transfer",
                LinkedInvoicePublicId = invoice.PublicId.ToString()
            },
            CancellationToken.None);

        var history = await service.GetHistoryAsync(new FinanceHistoryQuery(), CancellationToken.None);

        // La fila de cobro debe traer el PublicId de la factura vinculada.
        var paymentRow = history.Items.Single(i => i.PublicId == dto.PublicId && i.EntityType == "payment");
        Assert.Equal(invoice.PublicId, paymentRow.LinkedInvoicePublicId);

        // La fila de la factura (comprobante) NO es un cobro: no lleva vinculo.
        var invoiceRow = history.Items.Single(i => i.EntityType == "invoice");
        Assert.Null(invoiceRow.LinkedInvoicePublicId);
    }
}
