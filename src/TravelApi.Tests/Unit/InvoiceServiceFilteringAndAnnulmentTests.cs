using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
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
/// B1.15 Fase 2a (FIX 6): InvoiceService.
///  - Listings filtran por owner segun cobranzas.view_all.
///  - EnqueueAnnulmentAsync persiste AnnulledByUser*, AnnulmentReason y
///    AnnulmentStatus = Pending antes de encolar el job.
/// </summary>
public class InvoiceServiceFilteringAndAnnulmentTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;
    private readonly Mock<IBackgroundJobClient> _jobClientMock;
    private readonly Mock<IAfipService> _afipMock;
    private readonly Mock<IInvoicePdfService> _pdfMock;

    public InvoiceServiceFilteringAndAnnulmentTests()
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

        _jobClientMock = new Mock<IBackgroundJobClient>();
        _afipMock = new Mock<IAfipService>();
        _pdfMock = new Mock<IInvoicePdfService>();
    }

    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
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

    private InvoiceService BuildService(AppDbContext context, IHttpContextAccessor? accessor = null, IUserPermissionResolver? resolver = null)
        => new(context,
               new EntityReferenceResolver(context),
               _afipMock.Object,
               _pdfMock.Object,
               _mapper,
               _jobClientMock.Object,
               NullLogger<InvoiceService>.Instance,
               _settingsServiceMock.Object,
               BuildUserManager(),
               resolver,
               accessor);

    private static async Task SeedAsync(AppDbContext context)
    {
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1, NumeroReserva = "F-INV-0001", Name = "Reserva mia",
                Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-A",
                TotalSale = 1000m, Balance = 0m
            },
            new Reserva
            {
                Id = 2, NumeroReserva = "F-INV-0002", Name = "Reserva ajena",
                Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-B",
                TotalSale = 2000m, Balance = 0m
            });
        context.Invoices.AddRange(
            new Invoice
            {
                Id = 1, ReservaId = 1, TipoComprobante = 6, PuntoDeVenta = 1,
                NumeroComprobante = 1001, Resultado = "A", CAE = "CAE-1",
                ImporteTotal = 1000m, CreatedAt = DateTime.UtcNow
            },
            new Invoice
            {
                Id = 2, ReservaId = 2, TipoComprobante = 6, PuntoDeVenta = 1,
                NumeroComprobante = 1002, Resultado = "A", CAE = "CAE-2",
                ImporteTotal = 2000m, CreatedAt = DateTime.UtcNow
            });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetInvoices_VendedorWithoutViewAll_OnlyReturnsOwnInvoices()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("vendedor-A", "Vendedor");
        var resolver = BuildResolver("vendedor-A", Permissions.CobranzasView);

        var service = BuildService(context, accessor, resolver);
        var page = await service.GetAllAsync(new InvoicesListQuery(), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(1001L, page.Items.First().NumeroComprobante);
    }

    [Fact]
    public async Task GetInvoices_AdminBypass_ReturnsAll()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");

        var service = BuildService(context, accessor, resolver);
        var page = await service.GetAllAsync(new InvoicesListQuery(), CancellationToken.None);

        Assert.Equal(2, page.Items.Count());
    }

    [Fact]
    public async Task EnqueueAnnulmentAsync_SetsAnnulmentStatusPending_AndPersistsReason()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var service = BuildService(context);

        // requesterIsAdmin: true para bypassar el workflow de Fase D en este test
        // unitario — el flujo de approval esta cubierto por tests de integracion.
        await service.EnqueueAnnulmentAsync(1, "user-X", "Carlos Admin", "Cliente cancelo", requesterIsAdmin: true, CancellationToken.None);

        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(AnnulmentStatus.Pending, refreshed.AnnulmentStatus);
        Assert.Equal("user-X", refreshed.AnnulledByUserId);
        Assert.Equal("Carlos Admin", refreshed.AnnulledByUserName);
        Assert.Equal("Cliente cancelo", refreshed.AnnulmentReason);
        Assert.Null(refreshed.AnnulledAt); // se setea cuando AFIP confirma la NC

        // Verifica que se llamo a Enqueue (BackgroundJobClient no acepta moq trivial,
        // chequeamos via Create(...) de la API publica).
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task EnqueueAnnulmentAsync_InvoiceNotFound_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var service = BuildService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.EnqueueAnnulmentAsync(9999, "user-X", "Admin", "test", requesterIsAdmin: true, CancellationToken.None));
    }

    /// <summary>
    /// B1.15 Fase 2a (review final — fiscal critico): idempotencia. Bloquea doble click
    /// del operador (Pending + Pending = 2 NCs en AFIP, numeracion correlativa rota).
    /// </summary>
    [Fact]
    public async Task EnqueueAnnulmentAsync_OnPending_Throws_InvalidOperationException()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        // Forzar la factura 1 a Pending (simulando una solicitud previa en curso).
        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueueAnnulmentAsync(1, "user-X", "Admin", "retry", requesterIsAdmin: true, CancellationToken.None));
        Assert.Contains("anulacion en curso", ex.Message);

        // No debe haber encolado un segundo job.
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// B1.15 Fase 2a (review final — fiscal critico): re-anulacion de una factura
    /// ya con NC aprobada (Succeeded) queda bloqueada con mensaje claro.
    /// </summary>
    [Fact]
    public async Task EnqueueAnnulmentAsync_OnSucceeded_Throws_InvalidOperationException()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.AnnulmentStatus = AnnulmentStatus.Succeeded;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueueAnnulmentAsync(1, "user-X", "Admin", "duplicate", requesterIsAdmin: true, CancellationToken.None));
        Assert.Contains("ya fue anulada", ex.Message);
    }

    /// <summary>
    /// B1.15 Fase 2a (review final): re-intento desde Failed permitido. Util cuando
    /// AFIP devolvio timeout o error tecnico — operador puede reintentar manual.
    /// </summary>
    [Fact]
    public async Task EnqueueAnnulmentAsync_OnFailed_AllowsRetry_SetsStatusPending()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.AnnulmentStatus = AnnulmentStatus.Failed;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        // No debe lanzar. Debe re-encolar y dejar status en Pending.
        await service.EnqueueAnnulmentAsync(1, "user-X", "Admin", "retry-after-failed", requesterIsAdmin: true, CancellationToken.None);

        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(AnnulmentStatus.Pending, refreshed.AnnulmentStatus);
        Assert.Equal("retry-after-failed", refreshed.AnnulmentReason);

        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.AtLeastOnce);
    }
}
