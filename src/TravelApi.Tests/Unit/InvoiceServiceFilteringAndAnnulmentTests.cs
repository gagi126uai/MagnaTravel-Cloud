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
using Npgsql;
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

    /// <summary>
    /// 2026-05-11 (fix arca-tax-expert, fiscal critico): el endpoint de anulacion
    /// solo debe procesar Facturas A/B/C. NDs (2,7,12,52), NCs (3,8,13,53) y
    /// Facturas M (51) deben rechazarse upfront con mensaje claro al operador,
    /// SIN dejar la factura en Pending y SIN encolar el job.
    /// </summary>
    [Theory]
    [InlineData(2)]   // ND A
    [InlineData(7)]   // ND B
    [InlineData(12)]  // ND C
    [InlineData(52)]  // ND M
    [InlineData(3)]   // NC A
    [InlineData(8)]   // NC B
    [InlineData(13)]  // NC C
    [InlineData(53)]  // NC M
    [InlineData(51)]  // Factura M (sin mapeo a NC M)
    public async Task EnqueueAnnulmentAsync_UnsupportedTipoComprobante_Throws_AndDoesNotEnqueue(int tipoComprobante)
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        // Cambiar el tipo de la factura 1 al tipo NO soportado bajo prueba.
        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        var statusAntes = inv.AnnulmentStatus;
        inv.TipoComprobante = tipoComprobante;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueueAnnulmentAsync(1, "user-X", "Admin", "test", requesterIsAdmin: true, CancellationToken.None));
        Assert.Contains("no soporta anulacion automatica", ex.Message);

        // Status no debe cambiar a Pending (no se persistio la solicitud).
        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(statusAntes, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);

        // Y el job NO debe haberse encolado.
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// 2026-05-11 (fiscal critico — UX pendiente al emitir): el guard de CreateAsync
    /// debe rechazar emitir una nueva factura mientras hay OTRA Invoice en estado
    /// Resultado="PENDING" para la misma reserva (no anulada). Esto evita el escenario:
    ///  1. Operador clickea Emitir -> job1 encolado, Resultado=PENDING.
    ///  2. UI sigue mostrando "Lista para emitir" durante la ventana del job.
    ///  3. Operador duda y vuelve a clickear -> sin guard, se persistiria una segunda
    ///     Invoice PENDING -> 2 jobs concurrentes -> 2 CAEs distintos en AFIP ->
    ///     correlatividad fiscal rota.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenAnotherInvoicePendingForSameReserva_Throws_AndDoesNotCreate()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        // La reserva 1 ya tiene la invoice 1 con Resultado="A". Agregamos otra invoice
        // con Resultado="PENDING" (simulando un job de emision en vuelo).
        var reserva = await context.Reservas.FirstAsync(r => r.Id == 1);
        var pending = new Invoice
        {
            Id = 100,
            ReservaId = reserva.Id,
            TipoComprobante = 6,
            Resultado = "PENDING",
            AnnulmentStatus = AnnulmentStatus.None,
            ImporteTotal = 500m,
            CreatedAt = DateTime.UtcNow
        };
        context.Invoices.Add(pending);
        await context.SaveChangesAsync();

        var initialCount = await context.Invoices.CountAsync();

        var service = BuildService(context);
        var request = new CreateInvoiceRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            CbteTipo = 6,
            Concepto = 3,
            DocTipo = 99,
            DocNro = 0,
            // Total > 0: el guard de importe (2026-07-04) rechaza comprobantes en $0. Estos tests ejercitan otra
            // logica (guard anti-doble-emision, traduccion de errores, sello del emisor), no el importe.
            Items = { new InvoiceItemDto { Description = "Servicio", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 } }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "user-X", "Usuario", CancellationToken.None));
        Assert.Contains("en proceso", ex.Message, StringComparison.OrdinalIgnoreCase);

        // No se debe haber persistido una nueva invoice.
        Assert.Equal(initialCount, await context.Invoices.CountAsync());

        // AFIP no debe haber sido invocado (ni CreatePendingInvoice, ni ProcessInvoiceJob).
        _afipMock.Verify(
            a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()),
            Times.Never);

        // El job de emision no debe haberse encolado.
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// 2026-05-11 (fiscal critico): el guard de CreateAsync NO debe bloquear cuando la
    /// invoice PENDING existente ya fue anulada (AnnulmentStatus=Succeeded). Esa invoice
    /// quedo cerrada con NC aprobada y el flujo de emision ya no esta en vuelo. Permite
    /// emitir una factura nueva sobre la misma reserva en ese caso.
    ///
    /// Caso real: factura A emitida, anulada con NC, ahora se necesita reemitir.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenPendingButAnnulmentSucceeded_DoesNotBlock()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var reserva = await context.Reservas.FirstAsync(r => r.Id == 1);
        // Invoice PENDING pero ya anulada (NC aprobada). No esta "en vuelo".
        context.Invoices.Add(new Invoice
        {
            Id = 101,
            ReservaId = reserva.Id,
            TipoComprobante = 6,
            Resultado = "PENDING",
            AnnulmentStatus = AnnulmentStatus.Succeeded,
            ImporteTotal = 500m,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        // Setup AFIP mock para que CreatePendingInvoice devuelva una invoice valida
        // (el flujo necesita continuar tras el guard para verificar que NO se rompio).
        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .ReturnsAsync(new Invoice
            {
                Id = 200,
                ReservaId = reserva.Id,
                TipoComprobante = 6,
                Resultado = "PENDING",
                AnnulmentStatus = AnnulmentStatus.None,
                ImporteTotal = 500m
            });

        var service = BuildService(context);
        var request = new CreateInvoiceRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            CbteTipo = 6,
            Concepto = 3,
            DocTipo = 99,
            DocNro = 0,
            // Total > 0: el guard de importe (2026-07-04) rechaza comprobantes en $0. Estos tests ejercitan otra
            // logica (guard anti-doble-emision, traduccion de errores, sello del emisor), no el importe.
            Items = { new InvoiceItemDto { Description = "Servicio", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 } }
        };

        // No debe tirar. El guard solo bloquea invoices PENDING no anuladas.
        var result = await service.CreateAsync(request, "user-X", "Usuario", CancellationToken.None);
        Assert.NotNull(result);

        // El job debe haberse encolado (caso de exito).
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// 2026-05-11 (fiscal critico — race condition): cuando el guard aplicativo AnyAsync
    /// pasa pero el INSERT colisiona con el unique index parcial UX_Invoices_OnePendingPerReserva
    /// (T1 y T2 ven "no hay PENDING" en paralelo, ambos insertan), Postgres rechaza el
    /// segundo INSERT con SQLSTATE 23505. CreateAsync debe atrapar esa DbUpdateException
    /// y traducirla a la misma InvalidOperationException que el guard aplicativo — el
    /// contrato del endpoint hacia el frontend no cambia (sigue siendo 409 con el mismo
    /// mensaje, "Ya hay una factura en proceso...").
    ///
    /// El test simula el path catch del catch alrededor de _afipService.CreatePendingInvoice
    /// haciendo que el mock tire la DbUpdateException directamente. No exigimos hacer
    /// un test integration con Postgres real para este caso — la equivalencia del path
    /// se prueba aca; el index real va a smoke en VPS.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenDbUpdate23505Race_TranslatesToInvalidOperation()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var reserva = await context.Reservas.FirstAsync(r => r.Id == 1);

        // Simulamos la race: el guard AnyAsync NO ve PENDING en este context (no hay
        // ninguna en la DB), llega a llamar a _afipService.CreatePendingInvoice, y el
        // SaveChanges interno colisiona con el unique index parcial -> 23505.
        var postgresEx = new PostgresException(
            messageText: "duplicate key value violates unique constraint \"UX_Invoices_OnePendingPerReserva\"",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);
        var dbUpdateEx = new DbUpdateException("Error al guardar la factura PENDING.", postgresEx);

        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .ThrowsAsync(dbUpdateEx);

        var service = BuildService(context);
        var request = new CreateInvoiceRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            CbteTipo = 6,
            Concepto = 3,
            DocTipo = 99,
            DocNro = 0,
            // Total > 0: el guard de importe (2026-07-04) rechaza comprobantes en $0. Estos tests ejercitan otra
            // logica (guard anti-doble-emision, traduccion de errores, sello del emisor), no el importe.
            Items = { new InvoiceItemDto { Description = "Servicio", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 } }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(request, "user-X", "Usuario", CancellationToken.None));

        // Mensaje IDENTICO al del guard aplicativo — el frontend trata ambos casos igual.
        Assert.Equal(
            "Ya hay una factura en proceso para esta reserva. Espera a que termine antes de emitir otra.",
            ex.Message);

        // El job de emision no debe haberse encolado (la traduccion bloquea el path feliz).
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// 2026-05-11 (race condition guard): otras DbUpdateException que NO sean 23505
    /// (foreign key violation, check constraint, timeout, etc.) deben propagarse tal
    /// cual hacia arriba — no las queremos enmascarar como "en proceso para esta
    /// reserva" porque seria un mensaje incorrecto. El handler 500 del controller las
    /// captura como Problem(500) genericas.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenDbUpdateNonUnique_DoesNotTranslate()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var reserva = await context.Reservas.FirstAsync(r => r.Id == 1);

        // FK violation (23503) NO debe ser traducida.
        var postgresEx = new PostgresException(
            messageText: "insert or update on table \"Invoices\" violates foreign key constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.ForeignKeyViolation);
        var dbUpdateEx = new DbUpdateException("FK violation simulada.", postgresEx);

        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .ThrowsAsync(dbUpdateEx);

        var service = BuildService(context);
        var request = new CreateInvoiceRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            CbteTipo = 6,
            Concepto = 3,
            DocTipo = 99,
            DocNro = 0,
            // Total > 0: el guard de importe (2026-07-04) rechaza comprobantes en $0. Estos tests ejercitan otra
            // logica (guard anti-doble-emision, traduccion de errores, sello del emisor), no el importe.
            Items = { new InvoiceItemDto { Description = "Servicio", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 } }
        };

        // Debe propagar la DbUpdateException original — no atrapar 23503 como si fuera 23505.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.CreateAsync(request, "user-X", "Usuario", CancellationToken.None));
        Assert.Same(postgresEx, ex.InnerException);
    }

    /// <summary>
    /// 2026-05-11 (UX pendiente al emitir): la worklist debe marcar la reserva como
    /// FiscalStatus="in_progress" cuando hay una invoice PENDING en vuelo. El frontend
    /// usa este flag para deshabilitar el boton Emitir y evitar el doble click.
    /// </summary>
    [Fact]
    public async Task GetInvoicingWorklist_WhenReservaHasInvoicePending_ReturnsInProgressStatus()
    {
        await using var context = new AppDbContext(_dbOptions);
        // No usamos SeedAsync porque queremos una reserva sin invoices aprobadas previas
        // para que la fila aparezca como Lista para emitir (y se sobreescriba a in_progress).
        context.Reservas.Add(new Reserva
        {
            Id = 10,
            NumeroReserva = "F-INV-WL-1",
            Name = "Reserva en proceso",
            Status = EstadoReserva.Confirmed,
            TotalSale = 1500m,
            Balance = 0m, // settled => seria Ready si no fuera por el PENDING en curso
            StartDate = DateTime.UtcNow.Date
        });
        context.Invoices.Add(new Invoice
        {
            Id = 50,
            ReservaId = 10,
            TipoComprobante = 6,
            Resultado = "PENDING",
            AnnulmentStatus = AnnulmentStatus.None,
            ImporteTotal = 1500m,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);
        // Status="all" para no filtrar por ready (el default), porque el item nuevo es in_progress.
        var page = await service.GetInvoicingWorklistAsync(new InvoicingWorklistQuery { Status = "all" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("in_progress", item.FiscalStatus);
        Assert.Equal("En proceso AFIP", item.FiscalStatusLabel);
        // PendingFiscalAmount sigue >0 (la fila aparece, la UI solo deshabilita el boton).
        Assert.True(item.PendingFiscalAmount > 0m);
        // RequiresOverride debe estar en false: el flujo dominante es "esperar AFIP",
        // no "pedir autorizacion para emitir con deuda".
        Assert.False(item.RequiresOverride);
        Assert.Null(item.EconomicBlockReason);
    }

    /// <summary>
    /// 2026-05-11 (fix arca-tax-expert, defensa en profundidad): si por alguna razon
    /// (Hangfire retry, dato sucio, llamada interna) el job se ejecuta con un tipo
    /// no soportado, debe abortar marcando Failed + notificacion, SIN llamar a AFIP.
    /// Asegura que cbteTipo=0 nunca se envia al webservice.
    /// </summary>
    [Theory]
    [InlineData(51)]  // Factura M
    [InlineData(52)]  // ND M
    [InlineData(2)]   // ND A (caso reportado por arca-tax-expert)
    public async Task ProcessAnnulmentJob_UnsupportedTipoComprobante_MarksFailed_AndDoesNotCallAfip(int tipoComprobante)
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        // Forzar la factura a un tipo no soportado y dejarla en Pending (estado
        // donde estaria si el guard de Enqueue no hubiera corrido).
        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.TipoComprobante = tipoComprobante;
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        // Debe completar sin tirar excepcion (return temprano).
        await service.ProcessAnnulmentJob(1, "user-X", approvalRequestId: null);

        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(AnnulmentStatus.Failed, refreshed.AnnulmentStatus);

        // AFIP nunca debe haberse invocado.
        _afipMock.Verify(
            a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()),
            Times.Never);
        _afipMock.Verify(
            a => a.ProcessInvoiceJob(It.IsAny<int>()),
            Times.Never);

        // Notificacion al operador con explicacion clara.
        var notif = await context.Notifications.FirstOrDefaultAsync(n => n.RelatedEntityId == 1);
        Assert.NotNull(notif);
        Assert.Equal("Error", notif!.Type);
        // Voz de los avisos (2026-07-08): mensaje de negocio ("anularla a mano"), sin jerga técnica.
        Assert.Contains("hay que anularla a mano", notif.Message);
        Assert.Contains("Hacé la devolución", notif.Message);
    }

    /// <summary>
    /// ADR-012 §3.3 (no-regresion, flag multimoneda OFF): con <c>EnableMultiCurrencyInvoicing</c>
    /// apagado (default del mock de settings), una factura en moneda extranjera (MonId != "PES")
    /// sigue RECHAZANDO la anulacion total EXACTAMENTE como antes de ADR-012. <c>EnqueueAnnulmentAsync</c>
    /// falla SINCRONO (sin encolar el job, sin dejar la factura en Pending) con un mensaje claro.
    ///
    /// <para>Por que importa: este es el path al que cae una cancelacion auto-aprobable
    /// (ConfirmAsync step 8) o el fallback FC1.2 (OnApprovedAsync). Con el flag OFF nunca se emite
    /// una NC total para una factura USD — se deriva a NC parcial F2.5 o resolucion manual.</para>
    /// </summary>
    [Theory]
    [InlineData("DOL")]   // dolar
    [InlineData("dol")]   // casing distinto: el guard usa OrdinalIgnoreCase para PES, igual lo rechaza
    [InlineData("EUR")]   // moneda no soportada todavia
    public async Task EnqueueAnnulmentAsync_ForeignCurrencyInvoice_FlagOff_Throws_AndDoesNotEnqueue(string monId)
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        // La factura 1 es Factura B (tipo soportado) pero en moneda extranjera.
        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        var statusAntes = inv.AnnulmentStatus;
        inv.MonId = monId;
        inv.MonCotiz = 1234.56m;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueueAnnulmentAsync(1, "user-X", "Admin", "cancelacion USD", requesterIsAdmin: true, CancellationToken.None));
        // El mensaje debe mencionar la moneda para que el operador entienda por que se bloqueo.
        Assert.Contains(monId, ex.Message);

        // No se persistio la solicitud (status sin cambios, sin razon).
        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(statusAntes, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);

        // Y el job NO se encolo: jamas se emite una NC total en pesos para una factura en moneda extranjera.
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// ADR-012 §3.3 (no-regresion, flag multimoneda OFF, defensa en profundidad EN EL JOB): aunque
    /// <c>EnqueueAnnulmentAsync</c> ya bloquea moneda extranjera con el flag OFF, <c>ProcessAnnulmentJob</c>
    /// es un punto de entrada independiente (Hangfire retry, dato sucio, llamada interna). Con el flag
    /// OFF, si llega una factura en moneda extranjera directo al job, debe marcar Failed + notificar
    /// SIN llamar a AFIP — nunca emite NC en PES.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_ForeignCurrencyInvoice_FlagOff_MarksFailed_AndDoesNotCallAfip()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        // Factura B (tipo soportado) en USD, en Pending (estado donde estaria si el guard de
        // Enqueue no hubiera corrido — ej. dato insertado por SQL crudo + retry de Hangfire).
        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.MonId = "DOL";
        inv.MonCotiz = 1000m;
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        // Completa sin tirar (return temprano controlado).
        await service.ProcessAnnulmentJob(1, "user-X", approvalRequestId: null);

        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(AnnulmentStatus.Failed, refreshed.AnnulmentStatus);

        // AFIP nunca se invoca: cero riesgo de NC en pesos.
        _afipMock.Verify(
            a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()),
            Times.Never);
        _afipMock.Verify(
            a => a.ProcessInvoiceJob(It.IsAny<int>()),
            Times.Never);

        // Notificacion clara al operador.
        var notif = await context.Notifications.FirstOrDefaultAsync(n => n.RelatedEntityId == 1);
        Assert.NotNull(notif);
        Assert.Equal("Error", notif!.Type);
        // Voz de los avisos (2026-07-08): al usuario le hablamos de "dólares", nunca del código ARCA "DOL".
        Assert.Contains("dólares", notif.Message);
        Assert.DoesNotContain("DOL", notif.Message);
    }

    /// <summary>
    /// ADR-012 §3.3: prende el flag multimoneda en el mock de settings. Lo usan los tests que
    /// verifican la herencia de moneda/TC (camino nuevo del ADR). Por default el mock devuelve
    /// el flag OFF (ver constructor), que cubre la no-regresion.
    /// </summary>
    private void EnableMultiCurrencyFlag()
    {
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableMultiCurrencyInvoicing = true });
    }

    /// <summary>
    /// ADR-012 §3.3 (caso b — herencia automatica): con el flag multimoneda ON, anular una factura
    /// USD con TC coherente debe emitir la NC HEREDANDO MonId/MonCotiz de la factura ORIGINAL. El
    /// operador no elige moneda: el request que se manda a ARCA lleva exactamente "DOL" + el TC
    /// congelado del original. Verifica el requisito "inteligente" del dueno.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_FlagOn_ForeignCurrencyCoherentRate_InheritsCurrencyAndRate()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        EnableMultiCurrencyFlag();

        // Factura B (tipo 6, soportado) en USD con TC coherente (congelado del comprobante origen).
        const decimal originalRate = 1050.500000m;
        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.MonId = "DOL";
        inv.MonCotiz = originalRate;
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        // El job necesita una reserva con PublicId resoluble (el Include(Reserva) ya esta seedeado).
        await context.SaveChangesAsync();

        // Capturamos el request que el job arma para ARCA. La NC se emite OK (Resultado="A") para que el job
        // complete el camino feliz; a este test solo le importa que el request herede la moneda correcta.
        // (Antes se devolvia "PENDING" a proposito; desde el fix F2, "PENDING" significa "AFIP no respondio"
        // y dispara reintento, no un cierre benigno — por eso ahora modelamos una emision completa.)
        CreateInvoiceRequest? capturedRequest = null;
        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .Callback<int, CreateInvoiceRequest>((_, req) => capturedRequest = req)
            .ReturnsAsync(new Invoice
            {
                Id = 500,
                ReservaId = 1,
                TipoComprobante = 8, // NC B
                Resultado = "A",
                CAE = "CAE-NC-DOL",
                MonId = "DOL",
                MonCotiz = originalRate
            });

        var service = BuildService(context);
        await service.ProcessAnnulmentJob(1, "user-X", approvalRequestId: null);

        // El job NO se bloqueo por moneda (flag ON + TC coherente) y armo el request HEREDANDO
        // la moneda y el TC del comprobante origen. Este es el corazon del requisito "inteligente".
        Assert.NotNull(capturedRequest);
        Assert.Equal("DOL", capturedRequest!.MonId);
        Assert.Equal(originalRate, capturedRequest.MonCotiz);

        // Y efectivamente intento emitir la NC (no se freno en seco).
        _afipMock.Verify(
            a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()),
            Times.Once);
    }

    /// <summary>
    /// ADR-012 §3.3 (caso c — candado de incoherencia, vale incluso con flag ON): con el flag ON,
    /// una factura USD cuyo TC es incoherente (MonCotiz == 1 o <= 0) NO debe emitir la NC. Va a
    /// revision manual: <c>EnqueueAnnulmentAsync</c> rechaza sincrono y el job marca Failed sin
    /// llamar a AFIP. Evita valuar un dolar como un peso en facturas USD legacy sin TC bien cargado.
    /// </summary>
    [Theory]
    [InlineData(1d)]    // TC == 1: un dolar valdria un peso (dato corrupto)
    [InlineData(0d)]    // TC == 0: sin cotizacion
    [InlineData(-5d)]   // TC negativo: imposible
    public async Task EnqueueAnnulmentAsync_FlagOn_ForeignCurrencyIncoherentRate_Throws(double rate)
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        EnableMultiCurrencyFlag();

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        var statusAntes = inv.AnnulmentStatus;
        inv.MonId = "DOL";
        inv.MonCotiz = (decimal)rate;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueueAnnulmentAsync(1, "user-X", "Admin", "cancelacion USD", requesterIsAdmin: true, CancellationToken.None));
        Assert.Contains("incoherente", ex.Message);

        // No se persistio la solicitud ni se encolo el job: el TC corrupto nunca llega a ARCA.
        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(statusAntes, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// ADR-012 §3.3 (caso c en el JOB — punto de entrada independiente): con el flag ON, una factura
    /// USD con TC incoherente que llega directo al job (Hangfire retry, dato sucio) debe marcar
    /// Failed + notificar SIN llamar a AFIP. Mismo candado de incoherencia, defensa en profundidad.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_FlagOn_ForeignCurrencyIncoherentRate_MarksFailed_AndDoesNotCallAfip()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        EnableMultiCurrencyFlag();

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.MonId = "DOL";
        inv.MonCotiz = 1m; // incoherente
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await service.ProcessAnnulmentJob(1, "user-X", approvalRequestId: null);

        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(AnnulmentStatus.Failed, refreshed.AnnulmentStatus);

        _afipMock.Verify(
            a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()),
            Times.Never);
        _afipMock.Verify(
            a => a.ProcessInvoiceJob(It.IsAny<int>()),
            Times.Never);

        var notif = await context.Notifications.FirstOrDefaultAsync(n => n.RelatedEntityId == 1);
        Assert.NotNull(notif);
        Assert.Equal("Error", notif!.Type);
        // Voz de los avisos (2026-07-08): mensaje de negocio, sin la palabra técnica "incoherente".
        Assert.Contains("le falta cargar bien el valor del dólar", notif.Message);
    }

    /// <summary>
    /// ADR-012 fix MENOR-1 (fail-fast moneda no soportada, capa SINCRONA): con el flag ON, una factura
    /// en una moneda extranjera que el sistema NO sabe emitir al ARCA (ej "EUR") pero con TC COHERENTE
    /// (MonCotiz > 0 y != 1) debe rechazarse TEMPRANO en <c>EnqueueAnnulmentAsync</c>, NO dejarse pasar
    /// para que reviente recien el boundary de AfipService. Importante: el TC es coherente, asi que el
    /// candado de incoherencia NO la atrapa — la atrapa el guard de moneda soportada (IsValidArcaCurrencyCode).
    /// </summary>
    [Fact]
    public async Task EnqueueAnnulmentAsync_FlagOn_UnsupportedCurrencyCoherentRate_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        EnableMultiCurrencyFlag();

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        var statusAntes = inv.AnnulmentStatus;
        // "EUR" no esta en el catalogo ARCA del mapper (solo PES/DOL). TC coherente a proposito:
        // 1200 es > 0 y != 1, asi que NO lo frena el candado de incoherencia sino el de moneda soportada.
        inv.MonId = "EUR";
        inv.MonCotiz = 1200m;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EnqueueAnnulmentAsync(1, "user-X", "Admin", "cancelacion EUR", requesterIsAdmin: true, CancellationToken.None));
        // El mensaje debe ser el del guard de moneda soportada, no el de incoherencia (el TC es coherente).
        Assert.Contains("EUR", ex.Message);
        Assert.DoesNotContain("incoherente", ex.Message);

        // No se persistio la solicitud ni se encolo el job: la moneda no soportada nunca llega a ARCA.
        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(statusAntes, refreshed.AnnulmentStatus);
        Assert.Null(refreshed.AnnulmentReason);
        _jobClientMock.Verify(
            j => j.Create(It.IsAny<Job>(), It.IsAny<EnqueuedState>()),
            Times.Never);
    }

    /// <summary>
    /// ADR-012 fix MENOR-1 (fail-fast moneda no soportada, capa JOB — punto de entrada independiente):
    /// con el flag ON, una factura en moneda no soportada (ej "EUR") con TC coherente que llega directo
    /// al job (Hangfire retry, dato sucio) debe marcar Failed + notificar SIN llamar a AFIP. Mismo
    /// criterio que la capa sincrona, defensa en profundidad. El TC coherente descarta que la atrape
    /// el candado de incoherencia: la frena el guard de moneda soportada.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_FlagOn_UnsupportedCurrencyCoherentRate_MarksFailed_AndDoesNotCallAfip()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        EnableMultiCurrencyFlag();

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.MonId = "EUR"; // no soportada
        inv.MonCotiz = 1200m; // coherente: descarta el candado de incoherencia
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await service.ProcessAnnulmentJob(1, "user-X", approvalRequestId: null);

        var refreshed = await context.Invoices.AsNoTracking().FirstAsync(i => i.Id == 1);
        Assert.Equal(AnnulmentStatus.Failed, refreshed.AnnulmentStatus);

        _afipMock.Verify(
            a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()),
            Times.Never);
        _afipMock.Verify(
            a => a.ProcessInvoiceJob(It.IsAny<int>()),
            Times.Never);

        var notif = await context.Notifications.FirstOrDefaultAsync(n => n.RelatedEntityId == 1);
        Assert.NotNull(notif);
        Assert.Equal("Error", notif!.Type);
        // Voz de los avisos (2026-07-08): NO se filtra el código de moneda ("EUR") al usuario; el aviso
        // habla de "la moneda en que está emitida" y lo deriva a la devolución manual.
        Assert.Contains("por la moneda en que está emitida", notif.Message);
        Assert.DoesNotContain("EUR", notif.Message);
        Assert.DoesNotContain("incoherente", notif.Message);
    }

    /// <summary>
    /// ADR-012 §3.3 (caso d — pesos sigue igual): una factura en pesos hereda "PES"/1 (los defaults
    /// del origen), byte-identico al comportamiento de siempre, con cualquier valor del flag. La
    /// anulacion total en pesos no se ve afectada por el cambio multimoneda.
    /// </summary>
    [Theory]
    [InlineData(false)] // flag OFF
    [InlineData(true)]  // flag ON
    public async Task ProcessAnnulmentJob_PesosInvoice_InheritsPesos_RegardlessOfFlag(bool flagOn)
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        if (flagOn) EnableMultiCurrencyFlag();

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.MonId = "PES";
        inv.MonCotiz = 1m;
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        await context.SaveChangesAsync();

        CreateInvoiceRequest? capturedRequest = null;
        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .Callback<int, CreateInvoiceRequest>((_, req) => capturedRequest = req)
            .ReturnsAsync(new Invoice
            {
                Id = 501,
                ReservaId = 1,
                TipoComprobante = 8,
                // NC emitida OK: a este test solo le importa el request; desde F2 un "PENDING" dispararia reintento.
                Resultado = "A",
                CAE = "CAE-NC-PES",
                MonId = "PES",
                MonCotiz = 1m
            });

        var service = BuildService(context);
        await service.ProcessAnnulmentJob(1, "user-X", approvalRequestId: null);

        // El request a ARCA hereda los defaults de pesos del comprobante origen.
        Assert.NotNull(capturedRequest);
        Assert.Equal("PES", capturedRequest!.MonId);
        Assert.Equal(1m, capturedRequest.MonCotiz);
    }

    /// <summary>
    /// ADR-024 item 3 (auditoria de emision, 2026-06-12): CreateAsync sella quien emite con el usuario
    /// actual (userId/userName que el controller resolvio del HttpContext), SOBREESCRIBIENDO lo que pudiera
    /// venir en el body. Verificamos que el request que llega a CreatePendingInvoice (= lo que se persiste
    /// en Invoice.IssuedByUser*) lleve el actor server-side, no el spoofeado.
    /// </summary>
    [Fact]
    public async Task CreateAsync_StampsIssuedByUser_ServerSide_OverridingBody()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);
        var reserva = await context.Reservas.FirstAsync(r => r.Id == 1);

        CreateInvoiceRequest? capturedRequest = null;
        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .Callback<int, CreateInvoiceRequest>((_, req) => capturedRequest = req)
            .ReturnsAsync(new Invoice { Id = 600, ReservaId = 1, TipoComprobante = 11, Resultado = "PENDING" });

        var service = BuildService(context);
        var request = new CreateInvoiceRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            CbteTipo = 11,
            // El cliente intenta spoofear el actor: debe ser ignorado.
            IssuedByUserId = "atacante",
            IssuedByUserName = "Atacante",
            // Total > 0: el guard de importe (2026-07-04) rechaza comprobantes en $0.
            Items = { new InvoiceItemDto { Description = "Servicio", Quantity = 1, UnitPrice = 100m, Total = 100m, AlicuotaIvaId = 3 } }
        };

        await service.CreateAsync(request, "user-real", "Usuario Real", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("user-real", capturedRequest!.IssuedByUserId);
        Assert.Equal("Usuario Real", capturedRequest.IssuedByUserName);
    }

    /// <summary>
    /// ADR-024 item 3 (auditoria de emision, 2026-06-12): la NC de anulacion es un camino AUTOMATICO que
    /// arma el request a mano y llama CreatePendingInvoice SIN pasar por CreateAsync (donde se sella
    /// IssuedByUserId server-side). Verificamos que la NC de anulacion igual persista el actor: el request
    /// que llega a CreatePendingInvoice (= lo que termina en Invoice.IssuedByUserId via AfipService) debe
    /// llevar el userId que disparo el ProcessAnnulmentJob. Sin el fix M2 quedaba en NULL = NC sin rastro.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_StampsIssuedByUser_FromJobActor()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedAsync(context);

        var inv = await context.Invoices.FirstAsync(i => i.Id == 1);
        inv.MonId = "PES";
        inv.MonCotiz = 1m;
        inv.AnnulmentStatus = AnnulmentStatus.Pending;
        await context.SaveChangesAsync();

        CreateInvoiceRequest? capturedRequest = null;
        _afipMock
            .Setup(a => a.CreatePendingInvoice(It.IsAny<int>(), It.IsAny<CreateInvoiceRequest>()))
            .Callback<int, CreateInvoiceRequest>((_, req) => capturedRequest = req)
            .ReturnsAsync(new Invoice
            {
                Id = 700,
                ReservaId = 1,
                TipoComprobante = 8,
                // NC emitida OK: a este test solo le importa el request; desde F2 un "PENDING" dispararia reintento.
                Resultado = "A",
                CAE = "CAE-NC-STAMP",
                MonId = "PES",
                MonCotiz = 1m
            });

        var service = BuildService(context);
        await service.ProcessAnnulmentJob(1, "vendedor-anula", approvalRequestId: null);

        Assert.NotNull(capturedRequest);
        Assert.Equal("vendedor-anula", capturedRequest!.IssuedByUserId);
    }

    // =========================================================================================
    // Pantalla GLOBAL de Facturacion (2026-06-28): filtros server-side de GetAllAsync.
    //
    // Todos estos tests usan un Admin (ownerScope = null => ve TODA la agencia) y un set de
    // comprobantes variado sembrado inline, para validar cada filtro de forma aislada.
    // =========================================================================================

    /// <summary>
    /// Siembra una unica reserva y una lista de comprobantes variados (distintas fechas, tipos,
    /// monedas y estados de anulacion) para los tests de filtro de la pantalla global.
    /// </summary>
    private static async Task SeedGlobalInvoicesAsync(AppDbContext context)
    {
        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-GLOB-0001", Name = "Reserva global",
            Status = EstadoReserva.Confirmed, ResponsibleUserId = "vendedor-A",
            TotalSale = 10000m, Balance = 0m
        });

        context.Invoices.AddRange(
            // Factura A en ARS, emitida el 2026-01-10
            new Invoice
            {
                Id = 1, ReservaId = 1, TipoComprobante = 1, PuntoDeVenta = 5,
                NumeroComprobante = 9001, Resultado = "A", CAE = "CAE-A",
                MonId = "PES", ImporteTotal = 1000m,
                CreatedAt = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc)
            },
            // Factura B en USD, emitida el 2026-02-15
            new Invoice
            {
                Id = 2, ReservaId = 1, TipoComprobante = 6, PuntoDeVenta = 5,
                NumeroComprobante = 9002, Resultado = "A", CAE = "CAE-B",
                MonId = "DOL", MonCotiz = 1000m, ImporteTotal = 2000m,
                CreatedAt = new DateTime(2026, 2, 15, 12, 0, 0, DateTimeKind.Utc)
            },
            // Nota de credito B en ARS, emitida el 2026-03-20, ya anulando (Pending)
            new Invoice
            {
                Id = 3, ReservaId = 1, TipoComprobante = 8, PuntoDeVenta = 5,
                NumeroComprobante = 9003, Resultado = "A", CAE = "CAE-NC",
                MonId = "PES", ImporteTotal = 500m, AnnulmentStatus = AnnulmentStatus.Pending,
                CreatedAt = new DateTime(2026, 3, 20, 12, 0, 0, DateTimeKind.Utc)
            },
            // Nota de debito B en ARS, emitida el 2026-03-25
            new Invoice
            {
                Id = 4, ReservaId = 1, TipoComprobante = 7, PuntoDeVenta = 5,
                NumeroComprobante = 9004, Resultado = "A", CAE = "CAE-ND",
                MonId = "PES", ImporteTotal = 300m,
                CreatedAt = new DateTime(2026, 3, 25, 12, 0, 0, DateTimeKind.Utc)
            },
            // Factura B en ARS anulada (NC aprobada => Succeeded), emitida el 2026-04-01
            new Invoice
            {
                Id = 5, ReservaId = 1, TipoComprobante = 6, PuntoDeVenta = 5,
                NumeroComprobante = 9005, Resultado = "A", CAE = "CAE-ANU",
                MonId = "PES", ImporteTotal = 1500m, AnnulmentStatus = AnnulmentStatus.Succeeded,
                CreatedAt = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc)
            });

        await context.SaveChangesAsync();
    }

    private InvoiceService BuildAdminService(AppDbContext context)
        => BuildService(context, BuildContextAccessor("admin-1", "Admin"), BuildResolver("admin-1"));

    [Fact]
    public async Task GetAll_DateRange_FiltersByIssueDate()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        // Rango 2026-02-01 .. 2026-03-20 inclusive => Factura B (02-15), NC B (03-20).
        var query = new InvoicesListQuery
        {
            DateFrom = new DateTime(2026, 2, 1),
            DateTo = new DateTime(2026, 3, 20)
        };
        var page = await service.GetAllAsync(query, CancellationToken.None);

        var numeros = page.Items.Select(i => i.NumeroComprobante).OrderBy(n => n).ToList();
        Assert.Equal(new long[] { 9002, 9003 }, numeros);
    }

    [Fact]
    public async Task GetAll_DateTo_IsInclusiveOfWholeDay()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        // La NC B se emitio a las 12:00 del 2026-03-20. Un "hasta" sin hora (medianoche) debe
        // incluirla igual (dia completo), no excluirla.
        var query = new InvoicesListQuery
        {
            DateFrom = new DateTime(2026, 3, 20),
            DateTo = new DateTime(2026, 3, 20)
        };
        var page = await service.GetAllAsync(query, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9003L, item.NumeroComprobante);
    }

    [Fact]
    public async Task GetAll_DocumentFactura_ReturnsOnlyInvoices()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        var page = await service.GetAllAsync(new InvoicesListQuery { Document = "factura" }, CancellationToken.None);

        // Facturas = tipos 1 y 6 (ids 1, 2, 5). NC (8) y ND (7) quedan fuera.
        var numeros = page.Items.Select(i => i.NumeroComprobante).OrderBy(n => n).ToList();
        Assert.Equal(new long[] { 9001, 9002, 9005 }, numeros);
    }

    [Fact]
    public async Task GetAll_DocumentDebitNote_ReturnsOnlyDebitNotes()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        var page = await service.GetAllAsync(new InvoicesListQuery { Document = "debitnote" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9004L, item.NumeroComprobante); // ND B
    }

    [Fact]
    public async Task GetAll_DocumentAndLetter_PinpointsExactTipoComprobante()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        // NC + letra B => tipo 8 (solo la NC B).
        var page = await service.GetAllAsync(
            new InvoicesListQuery { Document = "creditnote", Letter = "B" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9003L, item.NumeroComprobante);
    }

    [Fact]
    public async Task GetAll_LetterOnly_FiltersAcrossFamilies()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        // Letra A sin familia => Factura A (tipo 1) es el unico comprobante letra A del set.
        var page = await service.GetAllAsync(new InvoicesListQuery { Letter = "A" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9001L, item.NumeroComprobante);
    }

    [Fact]
    public async Task GetAll_Currency_Usd_ReturnsOnlyDollarInvoices()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        var page = await service.GetAllAsync(new InvoicesListQuery { Currency = "USD" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9002L, item.NumeroComprobante);
        Assert.Equal("USD", item.Currency);
    }

    [Fact]
    public async Task GetAll_Currency_Ars_ExcludesDollarInvoices()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        var page = await service.GetAllAsync(new InvoicesListQuery { Currency = "ARS" }, CancellationToken.None);

        Assert.DoesNotContain(page.Items, i => i.NumeroComprobante == 9002L);
        Assert.All(page.Items, i => Assert.Equal("ARS", i.Currency));
        Assert.Equal(4, page.Items.Count); // todos menos la USD
    }

    [Fact]
    public async Task GetAll_NumberSearch_MatchesComprobanteNumber()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        var page = await service.GetAllAsync(new InvoicesListQuery { VoucherNumber = "9004" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9004L, item.NumeroComprobante);
    }

    [Fact]
    public async Task GetAll_AnnulmentFilter_Annulled_ReturnsOnlySucceeded()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        var page = await service.GetAllAsync(new InvoicesListQuery { Annulment = "annulled" }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9005L, item.NumeroComprobante);
        Assert.Equal("Succeeded", item.AnnulmentStatus);
    }

    [Fact]
    public async Task GetAll_DefaultOrdering_IsNewestFirst()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        var page = await service.GetAllAsync(new InvoicesListQuery(), CancellationToken.None);

        // El comprobante mas reciente (2026-04-01, id 5) debe venir primero.
        Assert.Equal(9005L, page.Items.First().NumeroComprobante);
    }

    [Fact]
    public async Task GetAll_Pagination_RespectsPageSizeAndTotalCount()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        // PageSize 25 es el minimo permitido; sembramos 5 comprobantes asi que entran en 1 pagina,
        // pero validamos que el conteo total y los flags de paginado sean correctos.
        var page = await service.GetAllAsync(new InvoicesListQuery { Page = 1, PageSize = 25 }, CancellationToken.None);

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(5, page.Items.Count);
    }

    [Fact]
    public async Task GetAll_CombinedFilters_AreAndedTogether()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedGlobalInvoicesAsync(context);
        var service = BuildAdminService(context);

        // Facturas + ARS + dentro de enero => solo la Factura A (id 1). La Factura B (USD) cae por moneda,
        // la Factura B anulada (04-01) cae por fecha.
        var query = new InvoicesListQuery
        {
            Document = "factura",
            Currency = "ARS",
            DateFrom = new DateTime(2026, 1, 1),
            DateTo = new DateTime(2026, 1, 31)
        };
        var page = await service.GetAllAsync(query, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(9001L, item.NumeroComprobante);
    }
}
