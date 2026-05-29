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
            DocNro = 0
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
            DocNro = 0
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
            DocNro = 0
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
            DocNro = 0
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
        Assert.Contains("no soporta anulacion automatica", notif.Message);
    }

    /// <summary>
    /// FIX B-1 capa 2 / B-2 (revision backend+contable, 2026-05-28): DEFENSE IN DEPTH del path de
    /// NC TOTAL. Una factura en moneda extranjera (MonId != "PES") NUNCA debe emitir una NC total
    /// en pesos. <c>EnqueueAnnulmentAsync</c> tiene que fallar SINCRONO (sin encolar el job, sin
    /// dejar la factura en Pending) con un mensaje claro.
    ///
    /// <para>Por que importa: este es el path al que cae una cancelacion auto-aprobable
    /// (ConfirmAsync step 8) o el fallback FC1.2 con flag OFF (OnApprovedAsync). Antes de F2.5
    /// el calculator forzaba manual review para toda moneda no-ARS, asi que nunca llegaba aca.
    /// Con el flag ON, una factura USD podria llegar; este guard impide la NC total en PES.</para>
    /// </summary>
    [Theory]
    [InlineData("DOL")]   // dolar
    [InlineData("dol")]   // casing distinto: el guard usa OrdinalIgnoreCase para PES, igual lo rechaza
    [InlineData("EUR")]   // moneda no soportada todavia
    public async Task EnqueueAnnulmentAsync_ForeignCurrencyInvoice_Throws_AndDoesNotEnqueue(string monId)
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
    /// FIX B-1 capa 2 (defensa en profundidad EN EL JOB): aunque <c>EnqueueAnnulmentAsync</c> ya
    /// bloquea moneda extranjera, <c>ProcessAnnulmentJob</c> es un punto de entrada independiente
    /// (Hangfire retry, dato sucio, llamada interna). Si llega una factura en moneda extranjera
    /// directo al job, debe marcar Failed + notificar SIN llamar a AFIP — nunca emite NC en PES.
    /// </summary>
    [Fact]
    public async Task ProcessAnnulmentJob_ForeignCurrencyInvoice_MarksFailed_AndDoesNotCallAfip()
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
        Assert.Contains("DOL", notif.Message);
    }
}
