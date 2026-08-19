using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda 3 (2026-08-18): el sistema ya pedia "motivo" en acciones sensibles y lo guardaba, pero en 3 de
/// 4 casos nunca lo devolvia al frontend. Estos tests cubren dos de esos agujeros: el motivo de anulacion
/// de una Factura (<c>Invoice.AnnulmentReason</c>) y el motivo de anulacion de una factura de operador
/// (<c>SupplierInvoice.VoidReason</c>), que ya se persistian pero se perdian al armar el DTO de respuesta.
/// </summary>
public class AnnulmentAndVoidReasonExposureTests
{
    private static DbContextOptions<AppDbContext> BuildInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    // ===================================================================
    // (a) InvoiceDto / InvoiceListDto traen AnnulmentReason tras anular.
    // ===================================================================

    [Fact]
    public void InvoiceDto_MappedByAutoMapper_CarriesAnnulmentReason()
    {
        // AutoMapper mapea AnnulmentReason por CONVENCION (mismo nombre en Invoice y en InvoiceDto):
        // no hace falta un ForMember explicito en el profile. Este test es el guardian de esa convencion
        // — si algun dia alguien renombra el campo en un solo lado, este test se rompe y avisa.
        var mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        var invoice = new Invoice
        {
            Id = 1,
            TipoComprobante = 3, // NC A
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            ImporteTotal = 500m,
            AnnulmentStatus = AnnulmentStatus.Succeeded,
            AnnulmentReason = "El cliente cancelo el viaje por motivos personales.",
        };

        var dto = mapper.Map<InvoiceDto>(invoice);

        Assert.Equal("El cliente cancelo el viaje por motivos personales.", dto.AnnulmentReason);
    }

    [Fact]
    public async Task GetAllAsync_ManualProjection_CarriesAnnulmentReason()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-ANN-0001", Name = "Reserva anulada",
            Status = EstadoReserva.Cancelled, TotalSale = 1000m, Balance = 0m
        });
        context.Invoices.Add(new Invoice
        {
            Id = 1, ReservaId = 1, TipoComprobante = 6, PuntoDeVenta = 1,
            NumeroComprobante = 5001, Resultado = "A", CAE = "CAE-ANN",
            ImporteTotal = 1000m, CreatedAt = DateTime.UtcNow,
            AnnulmentStatus = AnnulmentStatus.Succeeded,
            AnnulmentReason = "Factura duplicada, se emitio por error.",
        });
        await context.SaveChangesAsync();

        var service = BuildInvoiceService(context);
        var page = await service.GetAllAsync(new InvoicesListQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("Factura duplicada, se emitio por error.", item.AnnulmentReason);
    }

    [Fact]
    public async Task GetByReservaIdAsync_ManualProjection_CarriesAnnulmentReason()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-ANN-0002", Name = "Reserva anulada",
            Status = EstadoReserva.Cancelled, TotalSale = 1000m, Balance = 0m
        });
        context.Invoices.Add(new Invoice
        {
            Id = 1, ReservaId = 1, TipoComprobante = 6, PuntoDeVenta = 1,
            NumeroComprobante = 5002, Resultado = "A", CAE = "CAE-ANN2",
            ImporteTotal = 1000m, CreatedAt = DateTime.UtcNow,
            AnnulmentStatus = AnnulmentStatus.Succeeded,
            AnnulmentReason = "Error de tipeo en el CUIT del cliente.",
        });
        await context.SaveChangesAsync();

        var service = BuildInvoiceService(context);
        var items = (await service.GetByReservaIdAsync(1, CancellationToken.None)).ToList();

        var item = Assert.Single(items);
        Assert.Equal("Error de tipeo en el CUIT del cliente.", item.AnnulmentReason);
    }

    private static InvoiceService BuildInvoiceService(AppDbContext context)
    {
        var mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        var settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var userStore = new Mock<IUserStore<ApplicationUser>>();
        var userManager = new UserManager<ApplicationUser>(
            userStore.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);

        return new InvoiceService(
            context,
            new EntityReferenceResolver(context),
            Mock.Of<IAfipService>(),
            Mock.Of<IInvoicePdfService>(),
            mapper,
            Mock.Of<IBackgroundJobClient>(),
            NullLogger<InvoiceService>.Instance,
            settingsServiceMock.Object,
            userManager,
            null,
            null);
    }

    // ===================================================================
    // (b) SupplierInvoiceDto trae VoidReason tras anular la factura del operador,
    // pero SOLO si el usuario tiene permiso de ver montos de costo (F-14): el
    // motivo de anulacion de una factura de proveedor suele mencionar plata
    // ("se anulo por diferencia de USD 200"), asi que es informacion de costo.
    // ===================================================================

    [Fact]
    public async Task VoidSupplierInvoiceAsync_WithCostPermission_ReturnsDtoWithVoidReason()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        context.Suppliers.Add(new Supplier { Id = 1, Name = "Operador Test" });
        context.SupplierInvoices.Add(new SupplierInvoice
        {
            Id = 1, SupplierId = 1, Number = "OP-001", Currency = Monedas.ARS,
            IssuedAt = DateTime.UtcNow.AddDays(-10), DueDate = DateTime.UtcNow.AddDays(20),
            Status = SupplierInvoiceStatus.Open, CreatedByUserId = "user-1",
        });
        await context.SaveChangesAsync();

        var invoicePublicId = context.SupplierInvoices.Single().PublicId;
        var service = BuildSupplierServiceWithCostVisibility(context);

        var dto = await service.VoidSupplierInvoiceAsync(
            1, invoicePublicId, "Factura del operador cargada con el numero equivocado.", CancellationToken.None);

        Assert.Equal("anulada", dto.Status);
        Assert.True(dto.AmountsVisible);
        Assert.Equal("Factura del operador cargada con el numero equivocado.", dto.VoidReason);

        // El motivo tambien debe quedar persistido en la fila real (no solo en la respuesta de este call).
        var persisted = await context.SupplierInvoices.AsNoTracking().SingleAsync(x => x.Id == 1);
        Assert.Equal("Factura del operador cargada con el numero equivocado.", persisted.VoidReason);
    }

    [Fact]
    public async Task VoidSupplierInvoiceAsync_WithoutCostPermission_HidesVoidReasonInDto()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        context.Suppliers.Add(new Supplier { Id = 1, Name = "Operador Test" });
        context.SupplierInvoices.Add(new SupplierInvoice
        {
            Id = 1, SupplierId = 1, Number = "OP-002", Currency = Monedas.ARS,
            IssuedAt = DateTime.UtcNow.AddDays(-10), DueDate = DateTime.UtcNow.AddDays(20),
            Status = SupplierInvoiceStatus.Open, CreatedByUserId = "user-1",
        });
        await context.SaveChangesAsync();

        var invoicePublicId = context.SupplierInvoices.Single().PublicId;
        // Sin IHttpContextAccessor/IUserPermissionResolver: fail-closed, igual que un vendedor
        // sin cobranzas.see_cost (ver CostMasking.CanSeeCostAsync).
        var service = new SupplierService(context);

        var dto = await service.VoidSupplierInvoiceAsync(
            1, invoicePublicId, "Factura del operador cargada con el numero equivocado.", CancellationToken.None);

        Assert.Equal("anulada", dto.Status);
        Assert.False(dto.AmountsVisible);
        Assert.Null(dto.VoidReason);

        // El motivo SI queda persistido en la fila real: solo se oculta en el DTO de salida.
        var persisted = await context.SupplierInvoices.AsNoTracking().SingleAsync(x => x.Id == 1);
        Assert.Equal("Factura del operador cargada con el numero equivocado.", persisted.VoidReason);
    }

    /// <summary>
    /// Arma un SupplierService con permiso cobranzas.see_cost concedido (mismo patron que
    /// SupplierAccountEmptyCurrencyIntegrationTests.BuildServiceWithCostVisibility), para poder
    /// leer VoidReason y montos reales sin caer en el fail-closed por defecto.
    /// </summary>
    private static SupplierService BuildSupplierServiceWithCostVisibility(AppDbContext context)
    {
        const string userId = "test-admin";
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
            },
        };

        var resolverMock = new Mock<IUserPermissionResolver>();
        resolverMock
            .Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string> { Permissions.CobranzasSeeCost });

        return new SupplierService(
            context,
            auditService: null,
            httpContextAccessor: accessor,
            logger: null,
            permissionResolver: resolverMock.Object);
    }

    // ===================================================================
    // Bloqueante de data-exposure-reviewer (2026-08-18): algunos flujos INTERNOS (cancelacion de reserva,
    // revision manual FC1.3) arman AnnulmentReason con un prefijo tecnico pegado ("BC override {GUID}:",
    // "FC1.3 F2 partial NC:"). AnnulmentReasonUiSanitizer.ForDisplay tiene que sacar ese prefijo antes de
    // que el motivo llegue a pantalla. Casos (a)-(d) pedidos por el reviewer.
    // ===================================================================

    [Fact]
    public void ForDisplay_BcOverrideWithGuid_StripsPrefixAndKeepsUserText()
    {
        var raw = "BC override 3f8a1c22-9b1e-4a2f-8e3d-1234567890ab: cliente pidió cambio de fecha";

        var visible = AnnulmentReasonUiSanitizer.ForDisplay(raw);

        Assert.Equal("cliente pidió cambio de fecha", visible);
    }

    [Fact]
    public void ForDisplay_Fc13PartialNcPrefix_StripsPrefixAndKeepsUserText()
    {
        var raw = "FC1.3 F2 partial NC: lo que sea";

        var visible = AnnulmentReasonUiSanitizer.ForDisplay(raw);

        Assert.Equal("lo que sea", visible);
    }

    [Fact]
    public void ForDisplay_TechnicalPrefixWithoutUserText_ReturnsNull()
    {
        var raw = "BC retry-credit-notes:";

        var visible = AnnulmentReasonUiSanitizer.ForDisplay(raw);

        Assert.Null(visible);
    }

    [Fact]
    public void ForDisplay_NormalUserTypedReason_IsUntouched()
    {
        var raw = "factura con datos mal cargados";

        var visible = AnnulmentReasonUiSanitizer.ForDisplay(raw);

        Assert.Equal("factura con datos mal cargados", visible);
    }

    /// <summary>
    /// Wiring end-to-end: el sanitizador se aplica DESPUES de materializar la query (EF no puede traducir
    /// Regex a SQL), sobre la proyeccion manual de InvoiceListDto en GetAllAsync. Este test confirma que
    /// el post-proceso realmente corre y no se quedo solo en la firma del helper.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_TechnicalPrefixReason_IsSanitizedForDisplay()
    {
        var options = BuildInMemoryOptions();
        await using var context = new AppDbContext(options);

        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-ANN-0003", Name = "Reserva cancelada con override",
            Status = EstadoReserva.Cancelled, TotalSale = 1000m, Balance = 0m
        });
        context.Invoices.Add(new Invoice
        {
            Id = 1, ReservaId = 1, TipoComprobante = 6, PuntoDeVenta = 1,
            NumeroComprobante = 5003, Resultado = "A", CAE = "CAE-ANN3",
            ImporteTotal = 1000m, CreatedAt = DateTime.UtcNow,
            AnnulmentStatus = AnnulmentStatus.Succeeded,
            AnnulmentReason = "BC admin self-authorized override: se vencio el plazo del cliente",
        });
        await context.SaveChangesAsync();

        var service = BuildInvoiceService(context);
        var page = await service.GetAllAsync(new InvoicesListQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("se vencio el plazo del cliente", item.AnnulmentReason);
    }
}
