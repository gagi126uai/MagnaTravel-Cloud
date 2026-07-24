using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "anular sin factura" (2026-07-23, decisión del dueño; respaldo fiscal Ley de IVA art. 5 inc. b):
/// REESCRITURA de este archivo al invariante nuevo. Antes (R1, 2026-06-30) cancelar PARCIALMENTE un servicio
/// PAGADO al operador (pago IMPUTADO a la reserva) BLOQUEABA cuando la reserva no tenía factura de venta
/// viva. Ese bloqueo se ELIMINÓ: ahora el servicio se cancela igual, y en su lugar SIEMPRE se deja una
/// <see cref="BookingCancellationLine"/> que ancla el receivable "el operador me tiene que devolver" — sin
/// ancla fiscal (<c>BookingCancellation.OriginatingInvoiceId</c> null) porque no hay factura.
///
/// <para><b>Precisión de cuándo se crea la línea</b>: solo cuando el servicio tiene plata pagada IMPUTADA
/// (RefundCap reconstruido &gt; 0). Un servicio IMPAGO no genera línea (no hay nada que anclar). Un ADVANCE
/// "a cuenta" (no imputado a la reserva) ES saldo a favor genuino, no genera línea tampoco (lo cubre el test
/// existente <c>CancelService_ConfirmedPaidHotel_DropsSupplierDebt_B1</c>).</para>
/// </summary>
public class PartialCancellationPaidServiceNoInvoiceGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"r1-guard-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static BookingCancellationService BuildBcService(AppDbContext ctx, IAuditService? auditService = null)
    {
        var settings = new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 };
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        return new BookingCancellationService(
            ctx, new Mock<IInvoiceService>().Object, new Mock<IApprovalRequestService>().Object,
            auditService ?? new Mock<IAuditService>().Object, NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object, new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    private static SupplierService SeeCostSupplierService(AppDbContext ctx)
    {
        const string userId = "tester";
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"))
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new System.Collections.Generic.HashSet<string> { Permissions.CobranzasSeeCost, Permissions.TesoreriaSupplierPayments };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        return new SupplierService(ctx, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);
    }

    /// <summary>Reserva Confirmed + hotel Confirmado + (opcional) pago IMPUTADO a la reserva. SIN factura de venta.</summary>
    private static async Task<(Reserva Reserva, Supplier Supplier, HotelBooking Hotel)> SeedAsync(
        AppDbContext ctx, decimal paidImputedToReserva)
    {
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-R1", Name = "R-R1", PayerId = customer.Id, Status = EstadoReserva.Confirmed };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        if (paidImputedToReserva > 0m)
        {
            // Pago IMPUTADO a la reserva (ReservaId set) -> entra al pool de RefundCap -> receivable real.
            ctx.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = paidImputedToReserva, Currency = "ARS",
                ImputedCurrency = "ARS", ImputedAmount = paidImputedToReserva, Method = "Transferencia",
            });
            await ctx.SaveChangesAsync();
            await TravelApi.Infrastructure.Reservations.SupplierDebtPersister.PersistAsync(ctx, supplier.Id, CancellationToken.None);
            await ctx.SaveChangesAsync();
        }

        // Obra "candado coherente" C2 (2026-07-22): CancelServiceAsync ahora exige autorizacion viva en
        // una reserva Confirmada. Este archivo prueba la regla R1 (plata pagada sin factura), no el
        // candado en si (eso lo cubre Adr020LockGuardTests) — el seed ya nace autorizado.
        ctx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            ReservaId = reserva.Id,
            Reason = "Autorizacion de test para ejercitar CancelServiceAsync",
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });
        await ctx.SaveChangesAsync();

        return (reserva, supplier, hotel);
    }

    // ============================================================
    // Servicio PAGADO (imputado) + sin factura -> CANCELA igual, deja la linea-ancla del receivable
    // ============================================================
    [Fact]
    public async Task PaidImputedService_noInvoice_partialCancel_CreatesAnchorLine_AndCancelsService()
    {
        await using var ctx = NewContext();
        var (reserva, supplier, hotel) = await SeedAsync(ctx, paidImputedToReserva: 50_000m);
        var service = BuildBcService(ctx);

        // Obra "anular sin factura" (2026-07-23): ya NO lanza. El servicio se cancela igual.
        var result = await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);
        Assert.Equal(1, result.CancelledServicesCount);

        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Cancelado", reloaded.Status);

        // Se creó la línea-ancla, con el RefundCap correcto, en un BC SIN factura de venta.
        var bc = await ctx.BookingCancellations.AsNoTracking()
            .Include(b => b.Lines)
            .SingleAsync(b => b.ReservaId == reserva.Id);
        Assert.Null(bc.OriginatingInvoiceId);
        // Ultima pieza de la obra (2026-07-23): un BC sin ancla con plata real que reclamar (RefundCap > 0) ya
        // NO se queda en Drafted — salta directo a AwaitingOperatorRefund (nunca pasa por ConfirmAsync, que
        // exige factura). Sin este salto, OperatorRefundService jamas podria recibirle el reembolso real.
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);
        var line = Assert.Single(bc.Lines);
        Assert.Equal(supplier.Id, line.SupplierId);
        Assert.Equal(50_000m, line.RefundCap);
        // Decision explicita del dueño (2026-07-23): un BC sin ancla NO tiene plazo de vencimiento — el job de
        // timeout/la alarma de abandono lo ignoran (filtran por OperatorRefundDueBy != null), asi que nunca se
        // "abandona" por vencido. Follow-up documentado, no un olvido (ver XML-doc del helper de promocion).
        Assert.Null(bc.OperatorRefundDueBy);
    }

    // ============================================================
    // N4 (PR-12, 2026-07-23): la transicion Drafted -> AwaitingOperatorRefund deja rastro reusando el MISMO
    // mecanismo que ConfirmAsync (StageBusinessEvent, staged en la misma transaccion). Verifica quien (actor
    // que cancelo el servicio) y con que RefundCap quedo el salto.
    // ============================================================
    [Fact]
    public async Task PaidImputedService_noInvoice_partialCancel_PromotionStagesAuditTrail_WithActorAndRefundCap()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedAsync(ctx, paidImputedToReserva: 50_000m);
        var auditMock = new Mock<IAuditService>();
        var service = BuildBcService(ctx, auditMock.Object);

        await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        var bc = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.ReservaId == reserva.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);

        // El evento quedo STAGEADO (no LogBusinessEventAsync — participa del MISMO SaveChanges que crea la
        // linea-ancla y cambia el Status), con el actor real (quien canceló el servicio) y el RefundCap total.
        auditMock.Verify(s => s.StageBusinessEvent(
            AuditActions.BookingCancellationPromotedToAwaitingOperatorRefund,
            AuditActions.BookingCancellationEntityName,
            bc.Id.ToString(),
            It.Is<string>(details => details != null && details.Contains("50000")),
            "vendedor-1",
            "Vendedor"),
            Times.Once);
        auditMock.Verify(s => s.LogBusinessEventAsync(
            AuditActions.BookingCancellationPromotedToAwaitingOperatorRefund,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never); // staged, NUNCA por el camino que hace su propio commit.
    }

    // ============================================================
    // NUCLEO del item 5(i) del fix: el BC promovido a AwaitingOperatorRefund puede RECIBIR el reembolso real
    // del operador (antes de este fix se hubiera quedado en Drafted para siempre, y OperatorRefundService lo
    // hubiera rechazado con INV-093 "no esta en condiciones de recibir el reembolso").
    // ============================================================
    [Fact]
    public async Task PaidImputedService_noInvoice_partialCancel_ThenRecordAndAllocateRefund_Settles()
    {
        await using var ctx = NewContext();
        var (reserva, supplier, hotel) = await SeedAsync(ctx, paidImputedToReserva: 50_000m);
        var bcService = BuildBcService(ctx);

        var cancelResult = await bcService.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel"),
            "vendedor-1", "Vendedor", CancellationToken.None);
        Assert.Equal(1, cancelResult.CancelledServicesCount);

        var bc = await ctx.BookingCancellations.AsNoTracking()
            .Include(b => b.Lines)
            .SingleAsync(b => b.ReservaId == reserva.Id);
        Assert.Equal(BookingCancellationStatus.AwaitingOperatorRefund, bc.Status);

        // El operador devuelve la plata: registrar + imputar en un solo paso (atajo real que usa la pantalla).
        var refundService = BuildOperatorRefundService(ctx, bcService);
        var allocation = await refundService.RecordAndAllocateAsync(
            new RecordAndAllocateRefundRequest(
                SupplierPublicId: supplier.PublicId,
                BookingCancellationPublicId: bc.PublicId,
                ReceivedAmount: 50_000m,
                Currency: "ARS",
                ReceivedAt: DateTime.UtcNow,
                Method: "Transferencia",
                Reference: "OP-1",
                Notes: null,
                IdempotencyKey: Guid.NewGuid()),
            userId: "cajero-1", userName: "Cajero", ct: CancellationToken.None);

        // PASA (no tira INV-093) y liquida por completo: Net == Gross (sin deducciones), el "me tiene que
        // devolver" del operador queda en 0 y el BC avanza a ClientCreditApplied (primera allocation).
        Assert.Equal(50_000m, allocation.NetAmount);
        Assert.False(allocation.IsVoided);

        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == bc.Id);
        Assert.Equal(BookingCancellationStatus.ClientCreditApplied, bcAfter.Status);
        Assert.Equal(50_000m, bcAfter.ReceivedRefundAmount);

        var lineAfter = await ctx.Set<BookingCancellationLine>().AsNoTracking()
            .FirstAsync(l => l.BookingCancellationId == bc.Id);
        Assert.Equal(50_000m, lineAfter.ReceivedRefundAmount);
        Assert.Equal(BookingCancellationLineRefundStatus.Settled, lineAfter.RefundStatus);

        // El residual (lo que sigue pendiente) cae correctamente en el read-model de la bandeja "Reembolsos a
        // cobrar" (P1..P4): con RefundCap == ReceivedRefundAmount, el receivable en vivo da 0.
        var circuit = await SupplierCancellationCircuitReader.LoadAsync(ctx, supplier.Id, CancellationToken.None);
        Assert.Equal(0m, circuit.ReceivableByCurrency.GetValueOrDefault("ARS"));
    }

    /// <summary>Construye OperatorRefundService REAL, compartiendo el contexto y el bcService del test (como DI scoped).</summary>
    private static OperatorRefundService BuildOperatorRefundService(AppDbContext ctx, IBookingCancellationService bcService)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        var clientCreditMock = new Mock<IClientCreditService>();
        clientCreditMock.Setup(s => s.CreateEntryAsync(
                It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientCreditEntry());

        return new OperatorRefundService(
            ctx, bcService, clientCreditMock.Object, new Mock<IAuditService>().Object,
            settingsMock.Object, NullLogger<OperatorRefundService>.Instance);
    }

    // ============================================================
    // Servicio IMPAGO + sin factura -> NO se bloquea (no hay receivable que anclar)
    // ============================================================
    [Fact]
    public async Task UnpaidService_noInvoice_partialCancel_proceeds()
    {
        await using var ctx = NewContext();
        var (reserva, _, hotel) = await SeedAsync(ctx, paidImputedToReserva: 0m);
        var service = BuildBcService(ctx);

        var result = await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Cliente baja el hotel impago"),
            "vendedor-1", "Vendedor", CancellationToken.None);

        Assert.Equal(1, result.CancelledServicesCount);
        var reloaded = await ctx.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel.Id);
        Assert.Equal("Cancelado", reloaded.Status);
    }

    // ============================================================
    // Tras cancelar SIN factura, un pago al operador (trigger real del reconcile) NO mintea la plata anclada
    // — solo cuenta el advance legítimo, no el receivable que ya quedó anclado por la línea.
    // ============================================================
    [Fact]
    public async Task AfterCancel_supplierPaymentTrigger_doesNotMint()
    {
        await using var ctx = NewContext();
        var (reserva, supplier, hotel) = await SeedAsync(ctx, paidImputedToReserva: 50_000m);
        var bcService = BuildBcService(ctx);

        // Cancelar el servicio pagado sin factura: ya NO se bloquea, deja la línea-ancla (RefundCap 50.000).
        var result = await bcService.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "intento"),
            "vendedor-1", "Vendedor", CancellationToken.None);
        Assert.Equal(1, result.CancelledServicesCount);

        // Un pago al operador (trigger real del reconcile) por un advance legítimo de 10.000.
        var supplierService = SeeCostSupplierService(ctx);
        var paymentRequest = new SupplierPaymentRequest(
            Amount: 10_000m, Method: "T", Reference: null, Notes: null, ReservaId: null,
            ServicioReservaId: null, IsAdvanceToAccount: true, Currency: "ARS");
        await supplierService.AddSupplierPaymentAsync(supplier.Id, paymentRequest, CancellationToken.None);

        // LA RED: el pool consumible es SOLO el advance legítimo (10.000). Los 50.000 pagados por el hotel
        // cancelado están anclados por la línea (Y del circuito) y el reconciler los descuenta del cálculo
        // del sobrepago — NUNCA se mintean como saldo a favor gastable.
        var pool = (await ctx.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplier.Id).ToListAsync())
            .Sum(e => e.RemainingBalance);
        Assert.Equal(10_000m, pool);
    }

    // ============================================================
    // Diagnóstico CI Postgres (2026-07-24): DOS servicios del MISMO operador+moneda comparten un pool
    // insuficiente (30.000 pagados, 50.000 cada uno). Reproduce la secuencia EXACTA del test de integración
    // ServiceCancellationPreflightIntegrationTests.DosServiciosMismoOperadorYMoneda_PoolInsuficiente_...
    // con DbContexts SEPARADOS (mismo nombre de base InMemory, como dos conexiones reales) por cada fase —
    // igual que _fixture.CreateDbContext() en el test de integración — para que la lectura de la línea de A
    // (AssignRefundCapsAsync -> existingLineConsumption) pase por una consulta fresca a la "base", no por el
    // ChangeTracker de un contexto compartido.
    //
    // VEREDICTO (a): la plata está BIEN. AssignRefundCapsAsync (línea ~13013) descuenta del pool lo que la
    // línea de A YA consumió (RefundCap + RetainedDeductionAmount) ANTES de repartir para B — el pool
    // disponible para B da 0, y GetOrCreateServiceCancellationBcAndLineAsync (skipIfNoOperatorRefundCap=true)
    // NO crea línea para un RefundCap 0. Este test lo prueba empíricamente (no solo por lectura de código).
    //
    // La aserción real que rompía en Postgres NO era esta: CancelServiceAsync.CancelledServicesCount es un
    // contador AGREGADO de la reserva completa ("N de M servicios cancelado", CountServicesAsync cuenta TODOS
    // los servicios con operador de la reserva, cancelados vs total — ver su propio XML-doc). El test de
    // integración afirmaba 1 en la SEGUNDA cancelación (Hotel B), pero para ese momento la reserva tiene 2
    // servicios cancelados de 2 (A y B) — el valor correcto es 2. Ese fue el único error: el test asumía
    // (incorrectamente) que el contador era "cancelados EN ESTA LLAMADA" en vez de "cancelados en la reserva
    // hasta ahora". El fix (PR-6, sin relajar nada) va en el archivo de integración, corrigiendo la aserción
    // al valor cumulativo correcto — no en el código de plata, que este test confirma que ya es correcto.
    // ============================================================
    [Fact]
    public async Task TwoServicesSameSupplierAndCurrency_InsufficientPool_FirstToCancelTakesThePool_SecondGetsNoLine()
    {
        string dbName = $"pool-split-{Guid.NewGuid()}";
        DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        int reservaId;
        Guid reservaPublicId, hotelAPublicId, hotelBPublicId;
        int supplierId;

        // Fase SEED: reserva Confirmada, DOS hoteles del MISMO operador (50.000 cada uno), UN pago imputado de
        // 30.000 (insuficiente para los dos) + autorización de edición viva (candado C2, reserva Confirmada).
        await using (var seedCtx = new AppDbContext(Options()))
        {
            var customer = new Customer { FullName = "Cliente pool compartido", IsActive = true };
            var supplier = new Supplier { Name = "Operador pool compartido", IsActive = true };
            seedCtx.Customers.Add(customer);
            seedCtx.Suppliers.Add(supplier);
            await seedCtx.SaveChangesAsync();
            supplierId = supplier.Id;

            var reserva = new Reserva
            {
                NumeroReserva = "R-POOLSPLIT", Name = "Pool compartido", PayerId = customer.Id, Status = EstadoReserva.Confirmed,
            };
            seedCtx.Reservas.Add(reserva);
            await seedCtx.SaveChangesAsync();
            reservaId = reserva.Id;
            reservaPublicId = reserva.PublicId;

            var hotelA = new HotelBooking { ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado", NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS" };
            var hotelB = new HotelBooking { ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado", NetCost = 50_000m, SalePrice = 80_000m, Currency = "ARS" };
            seedCtx.HotelBookings.AddRange(hotelA, hotelB);
            await seedCtx.SaveChangesAsync();
            hotelAPublicId = hotelA.PublicId;
            hotelBPublicId = hotelB.PublicId;

            seedCtx.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 30_000m, Currency = "ARS",
                ImputedCurrency = "ARS", ImputedAmount = 30_000m, PaidAt = DateTime.UtcNow, Method = "Transfer",
            });
            await seedCtx.SaveChangesAsync();

            seedCtx.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
            {
                ReservaId = reserva.Id,
                Reason = "Autorizacion de test para ejercitar CancelServiceAsync",
                ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            });
            await seedCtx.SaveChangesAsync();
        }

        // Fase ACT A: cancelar Hotel A en un DbContext PROPIO (mismo patrón que _fixture.CreateDbContext() en
        // el test de integración) — se lleva TODO el pool disponible (30.000), topeado por su costo (50.000).
        int cancelledAfterA;
        await using (var ctxA = new AppDbContext(Options()))
        {
            var serviceA = BuildBcService(ctxA);
            var resultA = await serviceA.CancelServiceAsync(
                new CancelServiceRequest(reservaPublicId, "Hotel", hotelAPublicId, "pool compartido, primero"),
                "tester", "Tester Integracion", CancellationToken.None);
            cancelledAfterA = resultA.CancelledServicesCount;
        }
        // "N de M servicios cancelado": tras cancelar SOLO A, 1 de los 2 servicios de la reserva está cancelado.
        Assert.Equal(1, cancelledAfterA);

        // Fase ACT B: cancelar Hotel B en OTRO DbContext propio — el pool ya está agotado por la línea de A,
        // que este contexto lee FRESCA desde la "base" (no hay ChangeTracker compartido con la fase A).
        int cancelledAfterB;
        await using (var ctxB = new AppDbContext(Options()))
        {
            var serviceB = BuildBcService(ctxB);
            var resultB = await serviceB.CancelServiceAsync(
                new CancelServiceRequest(reservaPublicId, "Hotel", hotelBPublicId, "pool compartido, segundo"),
                "tester", "Tester Integracion", CancellationToken.None);
            cancelledAfterB = resultB.CancelledServicesCount;
        }
        // Tras cancelar TAMBIÉN B, los 2 de los 2 servicios de la reserva están cancelados — NO es "1 cancelado
        // en esta llamada" (el contador es acumulado de la reserva, ver el comentario largo arriba del test).
        Assert.Equal(2, cancelledAfterB);

        // Fase ASSERT: LA PLATA. Solo UNA línea (la de A) — B no tenía nada que anclar (pool ya consumido) —
        // y esa única línea tiene el RefundCap correcto (30.000, no 50.000 ni 60.000): sin doble conteo.
        await using (var assertCtx = new AppDbContext(Options()))
        {
            var bc = await assertCtx.BookingCancellations.AsNoTracking()
                .Include(b => b.Lines)
                .SingleAsync(b => b.ReservaId == reservaId);
            var line = Assert.Single(bc.Lines);
            Assert.Equal(30_000m, line.RefundCap);

            // Ambos hoteles quedaron Cancelados igual (B se cancela aunque no tenga línea que anclar: no hay
            // plata suya pendiente de devolver, no hay nada que ocultarle al reconciler).
            var hotels = await assertCtx.HotelBookings.AsNoTracking().Where(h => h.ReservaId == reservaId).ToListAsync();
            Assert.All(hotels, h => Assert.Equal("Cancelado", h.Status));
        }
    }
}
