using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Conveniencia (2026-07-01): tests del atajo <c>RecordAndAllocateAsync</c> = "registrar el reembolso del operador
/// e imputarlo a UNA cancelacion en UNA sola llamada" (camino simple, sin deducciones). Cubre:
/// <list type="bullet">
///   <item>Camino feliz: baja el "me tiene que devolver" (Y) del operador, crea el saldo a favor del cliente por
///         el monto completo (Net == Gross) y deja el ingreso fisico registrado.</item>
///   <item>Atomicidad: si la imputacion no procede (estado no imputable), NO queda ingreso huerfano.</item>
///   <item>Validaciones: operador que no coincide, moneda que no coincide, monto &lt;= 0, cancelacion abandonada
///         -> error claro y SIN mutar nada.</item>
///   <item>Feature flag off -> rechazo claro.</item>
/// </list>
///
/// <para>Tests UNIT con EF InMemory (sin Docker). InMemory NO soporta transacciones: la atomicidad REAL (rollback)
/// se testea en integracion Postgres. Aca la garantia de "no huerfano" la da el PRE-FLIGHT del atajo (valida antes
/// de registrar el ingreso), que es exactamente lo que estos tests ejercitan.</para>
/// </summary>
public class OperatorRefundRecordAndAllocateTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"record-and-allocate-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>
    /// Construye el service con colaboradores mockeados (callback de transicion, saldo a favor del cliente y
    /// auditoria). El flag <c>EnableNewCancellationFlow</c> se controla con <paramref name="flowEnabled"/>.
    /// </summary>
    private static (OperatorRefundService service, Mock<IClientCreditService> clientCreditMock,
        Mock<IBookingCancellationService> bcServiceMock) BuildService(AppDbContext ctx, bool flowEnabled = true)
    {
        var bcServiceMock = new Mock<IBookingCancellationService>();
        var clientCreditMock = new Mock<IClientCreditService>();
        var auditMock = new Mock<IAuditService>();
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();

        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = flowEnabled, OperatorRefundTimeoutDays = 60 });

        bcServiceMock.Setup(s => s.OnAllocationRecordedAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        clientCreditMock.Setup(s => s.CreateEntryAsync(
                It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientCreditEntry());

        var service = new OperatorRefundService(
            ctx,
            bcServiceMock.Object,
            clientCreditMock.Object,
            auditMock.Object,
            settingsMock.Object,
            NullLogger<OperatorRefundService>.Instance);

        return (service, clientCreditMock, bcServiceMock);
    }

    /// <summary>
    /// Siembra un BC con UNA linea del operador A (RefundCap alto para no chocar el tope) en la moneda dada y el
    /// estado dado. Deja tambien un operador B (para el test de operador que no coincide). Devuelve los ids.
    /// </summary>
    private static async Task<Seed> SeedBcAsync(
        AppDbContext ctx,
        BookingCancellationStatus status = BookingCancellationStatus.AwaitingOperatorRefund,
        string lineCurrency = "ARS")
    {
        var customer = new Customer { FullName = "Cliente Atajo", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A", IsActive = true };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplierA);
        ctx.Suppliers.Add(supplierB);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva { NumeroReserva = "R-ATAJO", Name = "Reserva atajo", PayerId = customer.Id, Status = EstadoReserva.Cancelled };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var invoice = new Invoice { TipoComprobante = 11, PuntoDeVenta = 1, NumeroComprobante = 500, ImporteTotal = 1_000m, ReservaId = reserva.Id };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplierA.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = status,
            Reason = "cancelacion con reembolso esperado",
            DraftedByUserId = "vendedor-1",
            AmountPaidAtCancellation = 0m,
            EstimatedRefundAmount = 0m,
            ReceivedRefundAmount = 0m,
            OperatorRefundDueBy = DateTime.UtcNow.AddDays(30),
            FiscalSnapshot = new FiscalSnapshot
            {
                CurrencyAtEvent = lineCurrency,
                AgencyTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                SupplierTaxConditionAtEvent = "RESPONSABLE_INSCRIPTO",
                Source = ExchangeRateSource.Manual,
                ExchangeRateAtOriginalInvoice = 1m,
                FetchedAt = DateTime.UtcNow,
            },
        };
        bc.Lines.Add(new BookingCancellationLine
        {
            SupplierId = supplierA.Id,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Full,
            Currency = lineCurrency,
            LineSaleAmount = 1_000m,
            RefundCap = 1_000m,
            ReceivedRefundAmount = 0m,
        });
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        return new Seed(customer, supplierA, supplierB, bc);
    }

    private record Seed(Customer Customer, Supplier SupplierA, Supplier SupplierB, BookingCancellation Bc);

    private static RecordAndAllocateRefundRequest NewRequest(
        Guid supplierPublicId, Guid bcPublicId, decimal amount, string currency = "ARS",
        Guid? idempotencyKey = null) =>
        new(
            SupplierPublicId: supplierPublicId,
            BookingCancellationPublicId: bcPublicId,
            ReceivedAmount: amount,
            Currency: currency,
            ReceivedAt: DateTime.UtcNow,
            Method: "Transfer",
            Reference: "Op-999",
            Notes: "Reembolso recibido del operador",
            // Por defecto una llave FRESCA por request: replica el caso comun (acciones distintas => llaves
            // distintas). Los tests de idempotencia pasan explicitamente la MISMA llave a dos requests.
            IdempotencyKey: idempotencyKey ?? Guid.NewGuid());

    // =====================================================================================
    // Camino feliz
    // =====================================================================================

    [Fact]
    public async Task RecordAndAllocate_HappyPath_LowersReceivable_CreatesClientCredit_AndRegistersIncome()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, clientCreditMock, _) = BuildService(ctx);

        var dto = await service.RecordAndAllocateAsync(
            NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m),
            userId: "cajero-1", userName: "Cajero", ct: CancellationToken.None);

        // Camino simple: sin deducciones, todo el bruto va a saldo a favor del cliente (Net == Gross).
        Assert.Equal(700m, dto.GrossAmount);
        Assert.Equal(700m, dto.NetAmount);
        Assert.False(dto.IsVoided);
        Assert.Empty(dto.Deductions);

        // El ingreso fisico quedo registrado y totalmente imputado.
        var refund = await ctx.OperatorRefundReceived.AsNoTracking().SingleAsync();
        Assert.Equal(700m, refund.ReceivedAmount);
        Assert.Equal(700m, refund.AllocatedAmount);
        Assert.Equal(seed.SupplierA.Id, refund.SupplierId);

        // El "me tiene que devolver" del operador baja: la linea acumula lo recibido (Y = RefundCap - recibido).
        var bcAfter = await ctx.BookingCancellations.AsNoTracking().FirstAsync(b => b.Id == seed.Bc.Id);
        Assert.Equal(700m, bcAfter.ReceivedRefundAmount);
        var lineAfter = await ctx.Set<BookingCancellationLine>().AsNoTracking().FirstAsync(l => l.BookingCancellationId == seed.Bc.Id);
        Assert.Equal(700m, lineAfter.ReceivedRefundAmount); // Y = 1000 - 700 = 300 pendiente

        // Se creo el saldo a favor del cliente por el monto completo, en la moneda del reembolso.
        clientCreditMock.Verify(s => s.CreateEntryAsync(
            seed.Bc.Id, It.IsAny<OperatorRefundAllocation>(), seed.Customer.Id, 700m, "ARS",
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // El ingreso fisico se ve en caja (ManualCashMovement Income linkeado al refund).
        var movement = await ctx.ManualCashMovements.AsNoTracking().SingleAsync();
        Assert.Equal(700m, movement.Amount);
        Assert.Equal(refund.Id, movement.OperatorRefundReceivedId);
    }

    // =====================================================================================
    // Atomicidad: si la imputacion no procede, NO queda ingreso huerfano
    // =====================================================================================

    [Fact]
    public async Task RecordAndAllocate_WhenCancellationNotInImputableState_DoesNotRegisterOrphanIncome()
    {
        await using var ctx = NewDbContext();
        // Closed no es un estado imputable -> el pre-flight rechaza ANTES de registrar el ingreso.
        var seed = await SeedBcAsync(ctx, status: BookingCancellationStatus.Closed);
        var (service, clientCreditMock, _) = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Equal("INV-093", ex.InvariantCode);

        // Nada de plata huerfana: ni ingreso, ni movimiento de caja, ni saldo a favor.
        Assert.Empty(await ctx.OperatorRefundReceived.ToListAsync());
        Assert.Empty(await ctx.ManualCashMovements.ToListAsync());
        clientCreditMock.Verify(s => s.CreateEntryAsync(
            It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =====================================================================================
    // Validaciones
    // =====================================================================================

    [Fact]
    public async Task RecordAndAllocate_OperatorDoesNotMatchAnyLine_Rejects_INV126_NoMutation()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx); // linea del operador A
        var (service, _, _) = BuildService(ctx);

        // Registramos un reembolso del operador B, que no tiene ninguna linea en esta cancelacion.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(seed.SupplierB.PublicId, seed.Bc.PublicId, amount: 700m),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Equal("INV-126", ex.InvariantCode);
        Assert.Empty(await ctx.OperatorRefundReceived.ToListAsync());
        Assert.Empty(await ctx.ManualCashMovements.ToListAsync());
    }

    [Fact]
    public async Task RecordAndAllocate_CurrencyDoesNotMatchLine_Rejects_INV118_NoMutation()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx, lineCurrency: "USD"); // la linea del operador es en USD
        var (service, _, _) = BuildService(ctx);

        // Reembolso en ARS: no coincide con la moneda de la linea del operador (USD).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m, currency: "ARS"),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Equal("INV-118", ex.InvariantCode);
        Assert.Empty(await ctx.OperatorRefundReceived.ToListAsync());
        Assert.Empty(await ctx.ManualCashMovements.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task RecordAndAllocate_NonPositiveAmount_Rejects_NoMutation(decimal amount)
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, _, _) = BuildService(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Empty(await ctx.OperatorRefundReceived.ToListAsync());
        Assert.Empty(await ctx.ManualCashMovements.ToListAsync());
    }

    [Fact]
    public async Task RecordAndAllocate_AbandonedCancellation_GivesActionableMessage_NoMutation()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx, status: BookingCancellationStatus.AbandonedByOperator);
        var (service, _, _) = BuildService(ctx);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m),
                "cajero-1", "Cajero", CancellationToken.None));

        // Mensaje accionable (dice que hay que reabrir desde la bandeja), sin jerga ni nombres internos.
        Assert.Contains("reabri", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reembolsos a cobrar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AbandonedByOperator", ex.Message);

        Assert.Empty(await ctx.OperatorRefundReceived.ToListAsync());
        Assert.Empty(await ctx.ManualCashMovements.ToListAsync());
    }

    // =====================================================================================
    // Feature flag off -> rechazo claro
    // =====================================================================================

    [Fact]
    public async Task RecordAndAllocate_FeatureFlagOff_Rejects_NoMutation()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, _, _) = BuildService(ctx, flowEnabled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Empty(await ctx.OperatorRefundReceived.ToListAsync());
        Assert.Empty(await ctx.ManualCashMovements.ToListAsync());
    }

    // =====================================================================================
    // Idempotencia (candado server-side contra el doble cobro)
    // =====================================================================================

    /// <summary>
    /// Dos requests con la MISMA llave (doble clic / reintento de red / dos pestañas) NO duplican plata: se crea UN
    /// solo ingreso, UN solo saldo a favor del cliente, el "me tiene que devolver" baja UNA vez, y la segunda
    /// respuesta es EXITO idempotente (identica a la primera). Es el caso que hoy provocaba el doble cobro.
    /// </summary>
    [Fact]
    public async Task RecordAndAllocate_SameIdempotencyKey_Twice_CreatesSingleRefundAndCredit_ReturnsSameResult()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, clientCreditMock, _) = BuildService(ctx);

        var sharedKey = Guid.NewGuid();
        var request = NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m, idempotencyKey: sharedKey);

        // Primer envio: procesa normal.
        var first = await service.RecordAndAllocateAsync(request, "cajero-1", "Cajero", CancellationToken.None);
        // Segundo envio con la MISMA llave (mismo objeto request): debe devolver la operacion original, sin crear
        // nada nuevo. El CHECK PREVIO por llave lo intercepta.
        var second = await service.RecordAndAllocateAsync(request, "cajero-1", "Cajero", CancellationToken.None);

        // Misma respuesta: mismo movimiento de plata, no un duplicado.
        Assert.Equal(first.PublicId, second.PublicId);
        Assert.Equal(first.RefundPublicId, second.RefundPublicId);
        Assert.Equal(700m, second.GrossAmount);
        Assert.Equal(700m, second.NetAmount);

        // UN solo ingreso fisico, con la llave sellada.
        var refund = await ctx.OperatorRefundReceived.AsNoTracking().SingleAsync();
        Assert.Equal(sharedKey, refund.IdempotencyKey);
        Assert.Equal(700m, refund.ReceivedAmount);

        // UN solo movimiento de caja, UNA sola allocation.
        Assert.Single(await ctx.ManualCashMovements.AsNoTracking().ToListAsync());
        Assert.Single(await ctx.OperatorRefundAllocations.AsNoTracking().ToListAsync());

        // El "me tiene que devolver" del operador bajo UNA sola vez (no 1400).
        var lineAfter = await ctx.Set<BookingCancellationLine>().AsNoTracking()
            .FirstAsync(l => l.BookingCancellationId == seed.Bc.Id);
        Assert.Equal(700m, lineAfter.ReceivedRefundAmount);

        // El saldo a favor del cliente se creo UNA sola vez.
        clientCreditMock.Verify(s => s.CreateEntryAsync(
            It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Dos requests con llaves DISTINTAS (mismo operador/cancelacion/monto) SI son dos reembolsos legitimos: el
    /// operador pudo mandar dos transferencias. Se crean dos ingresos y dos saldos a favor; el candado de
    /// idempotencia NO los bloquea (no comparten llave).
    /// </summary>
    [Fact]
    public async Task RecordAndAllocate_DifferentIdempotencyKeys_CreateTwoRefunds()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, clientCreditMock, _) = BuildService(ctx);

        // Dos acciones distintas => dos llaves distintas (las que genera NewRequest por defecto).
        await service.RecordAndAllocateAsync(
            NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m),
            "cajero-1", "Cajero", CancellationToken.None);
        await service.RecordAndAllocateAsync(
            NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m),
            "cajero-1", "Cajero", CancellationToken.None);

        // Dos ingresos fisicos distintos, con llaves distintas.
        var refunds = await ctx.OperatorRefundReceived.AsNoTracking().ToListAsync();
        Assert.Equal(2, refunds.Count);
        Assert.Equal(2, refunds.Select(r => r.IdempotencyKey).Distinct().Count());

        // Dos allocations y dos saldos a favor del cliente (plata legitima distinta).
        Assert.Equal(2, (await ctx.OperatorRefundAllocations.AsNoTracking().ToListAsync()).Count);
        clientCreditMock.Verify(s => s.CreateEntryAsync(
            It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    /// <summary>
    /// Sin llave real (Guid.Empty, p.ej. un cliente viejo que no la manda) NO hay candado posible -> rechazo claro,
    /// sin mutar nada. Cubre el hueco de que [Required] sobre un Guid nunca rechaza Guid.Empty.
    /// </summary>
    [Fact]
    public async Task RecordAndAllocate_EmptyIdempotencyKey_Rejects_NoMutation()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, _, _) = BuildService(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RecordAndAllocateAsync(
                NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 700m, idempotencyKey: Guid.Empty),
                "cajero-1", "Cajero", CancellationToken.None));

        Assert.Empty(await ctx.OperatorRefundReceived.ToListAsync());
        Assert.Empty(await ctx.ManualCashMovements.ToListAsync());
    }

    /// <summary>
    /// B1 (bloqueante de plata): la <c>IdempotencyKey</c> se sella EN EL INSERT del ingreso, NO en un UPDATE
    /// posterior. Antes del fix se seteaba como cambio pendiente que persistia recien el SaveChanges de la
    /// imputacion; si un retry xmin hacia <c>ChangeTracker.Clear()</c> en el medio, el sello se perdia -> la fila
    /// quedaba con llave NULL -> el indice unico nunca disparaba -> doble cobro.
    ///
    /// <para>El saldo a favor del cliente se crea DENTRO de la imputacion: DESPUES del INSERT del ingreso y ANTES
    /// del SaveChanges final de la imputacion. En ese instante leemos el estado PERSISTIDO (AsNoTracking = store
    /// InMemory, que NO refleja cambios pendientes). Si la llave YA esta en la fila, se sello en el INSERT (fix).
    /// Con el bug viejo (update pendiente) la fila persistida mostraria NULL en ese momento — este test lo
    /// distingue. La garantia DURA de la carrera real (23505 + xmin concurrentes) se valida en integracion
    /// Postgres; ver el follow-up documentado al pie de la clase.</para>
    /// </summary>
    [Fact]
    public async Task RecordAndAllocate_SealsIdempotencyKey_OnInsert_BeforeAllocationCompletes()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, clientCreditMock, _) = BuildService(ctx);

        var key = Guid.NewGuid();
        var creditCallbackRan = false;
        Guid? keyPersistedWhenCreditCreated = null;

        clientCreditMock
            .Setup(s => s.CreateEntryAsync(
                It.IsAny<int>(), It.IsAny<OperatorRefundAllocation>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClientCreditEntry())
            .Callback(() =>
            {
                creditCallbackRan = true;
                // Estado PERSISTIDO en ese instante (sin cambios pendientes): si la llave ya esta, vino del INSERT.
                var persisted = ctx.OperatorRefundReceived.AsNoTracking().SingleOrDefault();
                keyPersistedWhenCreditCreated = persisted?.IdempotencyKey;
            });

        await service.RecordAndAllocateAsync(
            NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 600m, idempotencyKey: key),
            "cajero-1", "Cajero", CancellationToken.None);

        Assert.True(creditCallbackRan);
        // Sellada en el INSERT del ingreso, no en un update posterior: sobrevive a un ChangeTracker.Clear() del retry.
        Assert.Equal(key, keyPersistedWhenCreditCreated);
    }

    /// <summary>
    /// CARRERA (documentado): con una llave ya persistida por un request previo, el segundo request con esa MISMA
    /// llave resuelve por el CHECK PREVIO y devuelve la operacion original — sin crear un duplicado. Este test
    /// ejercita la RAMA de check-previo, que es lo que InMemory puede validar.
    ///
    /// <para><b>La garantia REAL de carrera</b> (dos requests exactamente simultaneos, donde ambos pasan el check
    /// previo antes de que el otro committee) la da el INDICE UNICO PARCIAL en Postgres (23505 -> resuelto
    /// idempotentemente en el catch). InMemory NO enforcea indices unicos, asi que esa rama se valida en
    /// integracion Postgres, no aca.</para>
    /// </summary>
    [Fact]
    public async Task RecordAndAllocate_KeyAlreadyPersisted_ResolvesViaPreCheck_NoDuplicate()
    {
        await using var ctx = NewDbContext();
        var seed = await SeedBcAsync(ctx);
        var (service, _, _) = BuildService(ctx);

        var key = Guid.NewGuid();

        // "Ganador" ya persistido.
        var winner = await service.RecordAndAllocateAsync(
            NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 500m, idempotencyKey: key),
            "cajero-1", "Cajero", CancellationToken.None);

        // "Perdedor" que reintenta con la misma llave: el check previo lo intercepta y devuelve lo del ganador.
        var replay = await service.RecordAndAllocateAsync(
            NewRequest(seed.SupplierA.PublicId, seed.Bc.PublicId, amount: 500m, idempotencyKey: key),
            "cajero-1", "Cajero", CancellationToken.None);

        Assert.Equal(winner.PublicId, replay.PublicId);
        Assert.Single(await ctx.OperatorRefundReceived.AsNoTracking().ToListAsync());
    }

    // =====================================================================================
    // FOLLOW-UP OBLIGATORIO (integracion Postgres, no corre sin Docker aca):
    //   Test de carrera REAL: dos RecordAndAllocateAsync CONCURRENTES con la MISMA llave sobre el MISMO
    //   BookingCancellation deben terminar con UN solo OperatorRefundReceived (llave NO null), UNA sola
    //   allocation y UN solo ClientCreditEntry. Cubre las dos ramas que InMemory no puede ejercitar:
    //     (a) 23505 del indice unico parcial disparado en el INSERT -> resuelto idempotentemente en el catch;
    //     (b) conflicto xmin del BC con allowInternalRetry=false -> rollback total -> 409 -> replay del usuario.
    //   Ubicar junto a OperatorRefundConcurrencyTests (Cancellation/Integration), que ya monta el fixture Postgres.
    // =====================================================================================
}
