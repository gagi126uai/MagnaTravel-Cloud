using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Cancellation.Integration;

/// <summary>
/// ADR-044 T5 Addendum (2026-07-11), test obligatorio 10: dos cancelaciones PARCIALES casi simultaneas sobre
/// servicios que comparten la MISMA factura destino no pueden ver el mismo remanente "libre" y excederlo
/// juntas. Requiere Postgres real (<see cref="PostgresIntegrationFixture"/>): el candado
/// <c>RunUnderInvoiceLockAsync</c> usa <c>SELECT ... FOR UPDATE</c>, que InMemory no soporta (corre el
/// cuerpo directo, sin serializar) — la serializacion real SOLO se puede validar aca.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Adr044T5PartialCancellationConcurrencyIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public Adr044T5PartialCancellationConcurrencyIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static BookingCancellationService BuildService(AppDbContext ctx, IAuditService? audit = null)
    {
        var invoiceMock = new Mock<IInvoiceService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                EnableNewCancellationFlow = true,
                OperatorRefundTimeoutDays = 60,
            });

        var approvalSettings = new Mock<IOperationalFinanceSettingsService>();
        approvalSettings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        return new BookingCancellationService(
            ctx, invoiceMock.Object,
            new ApprovalRequestService(ctx, approvalSettings.Object),
            audit ?? new Mock<IAuditService>().Object,
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);
    }

    private static async Task<(Guid ReservaPublicId, int ReservaId, int InvoiceId, int SupplierId)> SeedReservaWithLiveInvoiceAsync(
        PostgresIntegrationFixture fixture, decimal importeTotal)
    {
        await using var seed = fixture.CreateDbContext();
        var (_, supplierId, resId, invId) = await CancellationTestData.SeedBaseAsync(seed);
        var invoice = await seed.Invoices.FirstAsync(i => i.Id == invId);
        invoice.ImporteTotal = importeTotal;
        await seed.SaveChangesAsync();
        var reserva = await seed.Reservas.FirstAsync(r => r.Id == resId);

        // Obra "candado coherente" C2 (2026-07-22): las cancelaciones parciales de este archivo cancelan
        // servicios de VERDAD (no solo prueban rechazos), asi que la reserva necesita autorizacion viva.
        await CancellationTestData.SeedLiveEditAuthorizationAsync(seed, resId);

        return (reserva.PublicId, resId, invId, supplierId);
    }

    private static async Task<(int Id, Guid PublicId)> AddHotelAsync(
        PostgresIntegrationFixture fixture, int reservaId, int supplierId, decimal salePrice)
    {
        await using var ctx = fixture.CreateDbContext();
        var hotel = new HotelBooking
        {
            ReservaId = reservaId, SupplierId = supplierId, Status = "Confirmado",
            NetCost = salePrice / 2m, SalePrice = salePrice, Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();
        return (hotel.Id, hotel.PublicId);
    }

    // Cuantas veces se repite cada bloque concurrente. Ejercitar la carrera N veces (opcion (b) del re-review
    // de QA) es la forma MAS ROBUSTA que no toca codigo de produccion y no arriesga el deadlock que tendria un
    // seam de sincronizacion inyectado frente a un FOR UPDATE / a un unique-index bloqueante: un hilo que
    // sostiene el lock y espera en el seam a un segundo hilo que a su vez esta bloqueado por ese mismo lock se
    // trabaria para siempre. Repetir con datos frescos maximiza la chance de pegarle a la ventana de carrera y
    // exige el outcome EXACTO en cada vuelta.
    private const int ConcurrencyRepetitions = 25;

    [Fact]
    public async Task TwoConcurrentPartialCancellations_SameInvoice_ExactlyOneConfirmsRemainderStaysExact()
    {
        // Factura de 100 con DOS servicios de 60 cada uno: si el lock NO serializara, ambas cancelaciones
        // verian el remanente completo (100) al mismo tiempo y confirmarian 60+60=120 > 100 (over-credit). El
        // outcome CORRECTO y EXACTO es: exactamente UNA confirma 60, la otra queda null (su 60 excede el
        // remanente 40 que dejo la primera), y el remanente final de la factura == 40. Este assert exacto
        // tambien mata el falso verde degenerado "ambas null" que el assert debil `total <= 100` dejaba pasar.
        for (int i = 0; i < ConcurrencyRepetitions; i++)
        {
            var (reservaPublicId, reservaId, invoiceId, supplierId) =
                await SeedReservaWithLiveInvoiceAsync(_fixture, importeTotal: 100m);
            var (hotel1Id, hotel1PublicId) = await AddHotelAsync(_fixture, reservaId, supplierId, salePrice: 60m);
            var (hotel2Id, hotel2PublicId) = await AddHotelAsync(_fixture, reservaId, supplierId, salePrice: 60m);

            await using (var ctxA = _fixture.CreateDbContext())
            await using (var ctxB = _fixture.CreateDbContext())
            {
                await Task.WhenAll(
                    BuildService(ctxA).CancelServiceAsync(
                        new CancelServiceRequest(reservaPublicId, "Hotel", hotel1PublicId, "Cancelo hotel1 (concurrente)"),
                        "cajero-1", "Cajero 1", CancellationToken.None),
                    BuildService(ctxB).CancelServiceAsync(
                        new CancelServiceRequest(reservaPublicId, "Hotel", hotel2PublicId, "Cancelo hotel2 (concurrente)"),
                        "cajero-2", "Cajero 2", CancellationToken.None));
            }

            await using var verify = _fixture.CreateDbContext();
            var lines = await verify.BookingCancellationLines.AsNoTracking()
                .Where(l => l.ServiceId == hotel1Id || l.ServiceId == hotel2Id)
                .ToListAsync();
            Assert.Equal(2, lines.Count);
            // Outcome EXACTO: exactamente una con 60, exactamente una null.
            Assert.Equal(1, lines.Count(l => l.ConfirmedGrossCreditAmount == 60m));
            Assert.Equal(1, lines.Count(l => l.ConfirmedGrossCreditAmount == null));
            // Remanente final de la factura == 40 (solo la que confirmo 60 reserva contra los 100).
            var remaining = await BuildService(verify)
                .ComputeInvoiceRemainingCreditableAmountAsync(invoiceId, CancellationToken.None);
            Assert.Equal(40m, remaining);
            // Ambos servicios quedan cancelados igual (la compuerta nunca bloquea el servicio en si).
            var hotel1Reloaded = await verify.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel1Id);
            var hotel2Reloaded = await verify.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel2Id);
            Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotel1Reloaded));
            Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotel2Reloaded));
        }
    }

    // =====================================================================================
    // FRENTE C (fix seguridad B1) — atomicidad de punta a punta: si el paso 5 falla, el Status
    // del servicio + la plata recalculada + la linea de credito revierten TODO junto. Nunca
    // queda un servicio cancelado (venta bajada) sin la linea que respalde el credito.
    // Forzamos la falla haciendo que la auditoria (StageBusinessEvent, dentro de la unidad
    // transaccional, antes del commit) tire una excepcion — simula cualquier error a mitad de camino.
    // =====================================================================================

    [Fact]
    public async Task Paso5Failure_RollsBackServiceCancellationAndMoney()
    {
        var (reservaPublicId, reservaId, _, supplierId) =
            await SeedReservaWithLiveInvoiceAsync(_fixture, importeTotal: 100m);
        var (hotelId, hotelPublicId) = await AddHotelAsync(_fixture, reservaId, supplierId, salePrice: 60m);

        // Snapshot de la plata ANTES: el saldo del cliente (ReservaMoneyByCurrency) y la deuda del operador
        // (SupplierBalanceByCurrency). Los persisters recalculan estas filas DENTRO de la transaccion (pasos
        // 3 y 4); si el rollback funciona, tienen que quedar EXACTAMENTE igual que antes de la cancelacion.
        List<(string Currency, decimal Balance)> ReadMoney(AppDbContext c) => c.ReservaMoneyByCurrency
            .AsNoTracking().Where(m => m.ReservaId == reservaId)
            .Select(m => new { m.Currency, m.Balance }).ToList()
            .Select(m => (m.Currency, m.Balance)).OrderBy(m => m.Currency).ToList();
        List<(string Currency, decimal Balance)> ReadDebt(AppDbContext c) => c.SupplierBalanceByCurrency
            .AsNoTracking().Where(s => s.SupplierId == supplierId)
            .Select(s => new { s.Currency, s.Balance }).ToList()
            .Select(s => (s.Currency, s.Balance)).OrderBy(s => s.Currency).ToList();
        // Comisiones (CommissionAccrual): el CommissionAccrualPersister participa de la transaccion ambiente,
        // asi que si el rollback funciona sus filas tambien tienen que quedar identicas a antes.
        List<(string Currency, decimal Amount)> ReadCommissions(AppDbContext c) => c.CommissionAccruals
            .AsNoTracking().Where(a => a.ReservaId == reservaId)
            .Select(a => new { a.Currency, a.Amount }).ToList()
            .Select(a => (a.Currency, a.Amount)).OrderBy(a => a.Currency).ToList();

        List<(string Currency, decimal Balance)> moneyBefore, debtBefore;
        List<(string Currency, decimal Amount)> commissionsBefore;
        await using (var snap = _fixture.CreateDbContext())
        {
            moneyBefore = ReadMoney(snap);
            debtBefore = ReadDebt(snap);
            commissionsBefore = ReadCommissions(snap);
        }

        await using var ctx = _fixture.CreateDbContext();
        var throwingAudit = new Mock<IAuditService>();
        throwingAudit
            .Setup(a => a.StageBusinessEvent(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("falla forzada del paso 5 (simulacion)"));
        var service = BuildService(ctx, throwingAudit.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reservaPublicId, "Hotel", hotelPublicId, "Cancelo hotel (falla en paso 5)"),
                "v1", "V", CancellationToken.None));

        // Rollback total: el servicio NO quedo cancelado y NO se creo ninguna linea.
        await using var verify = _fixture.CreateDbContext();
        var hotelReloaded = await verify.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotelId);
        Assert.False(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotelReloaded));
        Assert.Empty(await verify.BookingCancellationLines.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.BookingCancellations.AsNoTracking().ToListAsync());

        // Y la plata volvio a su valor previo: el saldo del cliente, la deuda del operador y las comisiones
        // quedaron IDENTICOS a antes de la cancelacion (los recalculos revirtieron junto con todo lo demas).
        Assert.Equal(moneyBefore, ReadMoney(verify));
        Assert.Equal(debtBefore, ReadDebt(verify));
        Assert.Equal(commissionsBefore, ReadCommissions(verify));
    }

    // =====================================================================================
    // FRENTE C (gap-b) — colision del PRIMER BC concurrente. Dos cancelaciones de servicios
    // DISTINTOS de la misma reserva, ambas "el primer servicio cancelado", por el camino AMBIGUO
    // (2+ facturas vivas sin eleccion -> sin FOR UPDATE que las serialice): ambas intentan crear
    // el primer BC y chocan en el unico por ReservaId. El retro-intento reusa al ganador. Resultado:
    // UN solo BC, ambas lineas, cero 500, cero servicio-sin-linea.
    // =====================================================================================

    [Fact]
    public async Task ConcurrentFirstBc_AmbiguousMultiInvoice_BothConsistent_SingleBcTwoLines()
    {
        // Repetimos la carrera N veces (opcion (b) del re-review): sin FOR UPDATE que serialice (camino ambiguo),
        // las dos cancelaciones chocan en el INSERT del primer BC; el retry-por-colision tiene que dejar UN solo
        // BC con ambas lineas en CADA vuelta. Repetir maximiza la chance de pegarle a la colision real.
        for (int i = 0; i < ConcurrencyRepetitions; i++)
        {
            var (reservaPublicId, reservaId, _, supplierId) =
                await SeedReservaWithLiveInvoiceAsync(_fixture, importeTotal: 500m);
            // Segunda factura de venta viva (con CAE) -> el caso pasa a ser AMBIGUO (2+ activas sin eleccion).
            await using (var seed = _fixture.CreateDbContext())
            {
                seed.Invoices.Add(new Invoice
                {
                    TipoComprobante = 1, PuntoDeVenta = 1, NumeroComprobante = 2, ImporteTotal = 500m,
                    ImporteNeto = 413.22m, ImporteIva = 86.78m, ReservaId = reservaId,
                    CAE = "68000000000001", Resultado = "A", CreatedAt = DateTime.UtcNow.AddMinutes(1),
                });
                await seed.SaveChangesAsync();
            }
            var (hotel1Id, hotel1PublicId) = await AddHotelAsync(_fixture, reservaId, supplierId, salePrice: 60m);
            var (hotel2Id, hotel2PublicId) = await AddHotelAsync(_fixture, reservaId, supplierId, salePrice: 40m);

            // Ninguna manda TargetInvoicePublicId -> ambiguo -> sin FOR UPDATE -> ambas quieren crear el primer BC.
            await using (var ctxA = _fixture.CreateDbContext())
            await using (var ctxB = _fixture.CreateDbContext())
            {
                await Task.WhenAll(
                    BuildService(ctxA).CancelServiceAsync(
                        new CancelServiceRequest(reservaPublicId, "Hotel", hotel1PublicId, "Cancelo hotel1 (concurrente ambiguo)"),
                        "cajero-1", "Cajero 1", CancellationToken.None),
                    BuildService(ctxB).CancelServiceAsync(
                        new CancelServiceRequest(reservaPublicId, "Hotel", hotel2PublicId, "Cancelo hotel2 (concurrente ambiguo)"),
                        "cajero-2", "Cajero 2", CancellationToken.None));
            }

            await using var verify = _fixture.CreateDbContext();
            // Un SOLO BC vivo para la reserva (el unico por ReservaId lo garantiza).
            var bcs = await verify.BookingCancellations.AsNoTracking()
                .Where(b => b.ReservaId == reservaId).ToListAsync();
            Assert.Single(bcs);
            // Ambas lineas presentes (ningun servicio quedo cancelado sin linea).
            var lines = await verify.BookingCancellationLines.AsNoTracking()
                .Where(l => l.ServiceId == hotel1Id || l.ServiceId == hotel2Id).ToListAsync();
            Assert.Equal(2, lines.Count);
            Assert.Contains(lines, l => l.ServiceId == hotel1Id);
            Assert.Contains(lines, l => l.ServiceId == hotel2Id);
            // Ambos servicios cancelados.
            var hotel1Reloaded = await verify.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel1Id);
            var hotel2Reloaded = await verify.HotelBookings.AsNoTracking().FirstAsync(h => h.Id == hotel2Id);
            Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotel1Reloaded));
            Assert.True(TravelApi.Domain.Reservations.ServiceResolutionRules.IsCancelled(hotel2Reloaded));
        }
    }

    // =====================================================================================
    // FRENTE F (M1/C2) — el candado del cap en el legacy se apoya en el indice UNICO parcial por
    // reserva/factura: dos DraftAsync concurrentes sobre la MISMA reserva no pueden crear dos BC
    // vivos. El segundo o reusa al ganador (Drafted puro) o rebota INV-081; NUNCA quedan dos.
    // =====================================================================================

    [Fact]
    public async Task ConcurrentDraftAsync_SameReserva_NeverTwoLiveBookingCancellations()
    {
        // Repetimos la carrera N veces (opcion (b) del re-review): dos DraftAsync concurrentes sobre la misma
        // reserva compiten por el unico parcial por ReservaId. En CADA vuelta al menos una resuelve y NUNCA
        // pueden coexistir dos BC vivos (el segundo reusa el draft o rebota INV-081).
        for (int i = 0; i < ConcurrencyRepetitions; i++)
        {
            var (reservaPublicId, reservaId, _, supplierId) =
                await SeedReservaWithLiveInvoiceAsync(_fixture, importeTotal: 1000m);
            await AddHotelAsync(_fixture, reservaId, supplierId, salePrice: 1000m);

            bool[] results;
            await using (var ctxA = _fixture.CreateDbContext())
            await using (var ctxB = _fixture.CreateDbContext())
            {
                results = await Task.WhenAll(
                    RunDraftSwallowingExpectedAsync(BuildService(ctxA), reservaPublicId, "cajero-1"),
                    RunDraftSwallowingExpectedAsync(BuildService(ctxB), reservaPublicId, "cajero-2"));
            }

            Assert.Contains(true, results); // al menos una resolvio

            await using var verify = _fixture.CreateDbContext();
            var liveBcs = await verify.BookingCancellations.AsNoTracking()
                .Where(b => b.ReservaId == reservaId
                         && b.Status != BookingCancellationStatus.Aborted
                         && b.Status != BookingCancellationStatus.Closed)
                .ToListAsync();
            Assert.Single(liveBcs);
        }
    }

    private static async Task<bool> RunDraftSwallowingExpectedAsync(
        BookingCancellationService service, Guid reservaPublicId, string userId)
    {
        try
        {
            await service.DraftAsync(
                new DraftCancellationRequest(reservaPublicId, "Anulacion total concurrente"),
                userId, userId, CancellationToken.None);
            return true;
        }
        catch (TravelApi.Domain.Exceptions.BusinessInvariantViolationException)
        {
            // INV-081: la otra operacion gano la carrera. Esperado, no es un fallo del test.
            return false;
        }
    }
}
