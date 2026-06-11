using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-022 Capas 1-2: el Libro de Caja (<see cref="CashLedgerEntry"/>) y la escritura del asiento en
/// cada puerta. Cubre: 1 asiento por cobro / pago a proveedor / movimiento manual / refund de cancelacion
/// (en USD = asiento USD, caso B1); ciclo de edicion (viejo IsReversed + reversa + nuevo); borrado (reversa);
/// sobrepago -> ClientCreditEntry; backfill idempotente.
///
/// <para><b>Nota sobre InMemory</b>: el provider InMemory NO aplica los CHECK constraints ni el indice
/// unico parcial (son SQL). Estos tests verifican el COMPORTAMIENTO de los servicios (que asiento se crea,
/// con que datos, y que el viejo se marca IsReversed antes de insertar el nuevo). La validacion del CHECK
/// y del indice unico parcial contra Postgres real es de la tanda de integracion (ADR §8).</para>
/// </summary>
public class Adr022CashLedgerTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public Adr022CashLedgerTests()
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

    private static async Task<Reserva> SeedReservaAsync(
        AppDbContext context, int reservaId = 1, decimal salePrice = 1000m, int? payerId = 7)
    {
        if (payerId is not null && !await context.Customers.AnyAsync(c => c.Id == payerId.Value))
        {
            context.Customers.Add(new Customer { Id = payerId.Value, FullName = "Cliente Test" });
        }

        var reserva = new Reserva
        {
            Id = reservaId,
            NumeroReserva = $"F-2026-{reservaId:D4}",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            TotalSale = salePrice,
            ConfirmedSale = salePrice,
            TotalCost = 0m,
            Balance = salePrice,
            TotalPaid = 0m,
            PayerId = payerId,
        };
        context.Reservas.Add(reserva);
        // Servicio confirmado que sustenta ConfirmedSale (el saldo se recalcula desde servicios).
        context.Servicios.Add(new ServicioReserva
        {
            Id = reservaId * 100,
            ReservaId = reservaId,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "S",
            ConfirmationNumber = "ABC",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = salePrice,
            NetCost = 0m,
            Commission = salePrice,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return reserva;
    }

    // ====================================================================================
    // Factory (puro): un test por origen
    // ====================================================================================

    [Fact]
    public void ForPayment_UsesRealCurrencyAndAmount_NotImputed()
    {
        // Cobro cruzado: entro USD a caja pero se imputa contra el saldo ARS. El asiento debe llevar la
        // moneda REAL de caja (USD), no la imputada.
        var payment = new Payment
        {
            Id = 5,
            Amount = 100m,
            Currency = Monedas.USD,
            ImputedCurrency = Monedas.ARS,
            ImputedAmount = 100000m,
            Method = "Transfer",
            PaidAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ReservaId = 1,
        };

        var entry = CashLedgerEntryFactory.ForPayment(payment, "user-1", "User One");

        Assert.Equal(CashMovementDirections.Income, entry.Direction);
        Assert.Equal(100m, entry.Amount);
        Assert.Equal(Monedas.USD, entry.Currency); // moneda real, no la imputada ARS
        Assert.Equal(CashLedgerSourceTypes.CustomerPayment, entry.SourceType);
        Assert.Same(payment, entry.Payment);
        Assert.Equal(1, entry.ReservaId);
    }

    [Fact]
    public void ForSupplierPayment_IsExpense()
    {
        var payment = new SupplierPayment
        {
            Id = 9, SupplierId = 3, Amount = 500m, Currency = Monedas.ARS,
            Method = "Cash", PaidAt = DateTime.UtcNow, ReservaId = 1,
        };

        var entry = CashLedgerEntryFactory.ForSupplierPayment(payment, "u", "U");

        Assert.Equal(CashMovementDirections.Expense, entry.Direction);
        Assert.Equal(500m, entry.Amount);
        Assert.Equal(CashLedgerSourceTypes.SupplierPayment, entry.SourceType);
        Assert.Equal(3, entry.SupplierId);
    }

    [Fact]
    public void ForManualMovement_RefundOrigin_TakesOverrideCurrency_NotManualCurrency()
    {
        // B1: el manual de un refund nace en ARS (default) pero el hecho real fue en USD. El asiento debe
        // tomar la moneda del ORIGEN REAL (currencyOverride = USD), no la del manual.
        var movement = new ManualCashMovement
        {
            Id = 11,
            Direction = CashMovementDirections.Income,
            Amount = 200m,
            Currency = Monedas.ARS, // default del manual de cancelacion
            Method = "Transfer",
            OccurredAt = DateTime.UtcNow,
            OperatorRefundReceivedId = 4, // discrimina SourceType = OperatorRefund
        };

        var entry = CashLedgerEntryFactory.ForManualMovement(movement, currencyOverride: Monedas.USD, "u", "U");

        Assert.Equal(Monedas.USD, entry.Currency);
        Assert.Equal(CashLedgerSourceTypes.OperatorRefund, entry.SourceType);
        Assert.Equal(CashMovementDirections.Income, entry.Direction);
    }

    [Fact]
    public void ForManualMovement_PureAdjustment_UsesManualCurrency()
    {
        var movement = new ManualCashMovement
        {
            Id = 12, Direction = CashMovementDirections.Expense, Amount = 50m,
            Currency = Monedas.USD, Method = "Cash", OccurredAt = DateTime.UtcNow,
        };

        var entry = CashLedgerEntryFactory.ForManualMovement(movement, currencyOverride: null, "u", "U");

        Assert.Equal(Monedas.USD, entry.Currency);
        Assert.Equal(CashLedgerSourceTypes.ManualAdjustment, entry.SourceType);
    }

    [Fact]
    public void Reverse_InvertsDirectionAndLinksOriginal()
    {
        var original = new CashLedgerEntry
        {
            Id = 20, Direction = CashMovementDirections.Income, Amount = 100m,
            Currency = Monedas.ARS, Method = "Transfer", SourceType = CashLedgerSourceTypes.CustomerPayment,
            PaymentId = 5, ReservaId = 1,
        };

        var reversal = CashLedgerEntryFactory.Reverse(original, DateTime.UtcNow, "u", "U");

        Assert.True(reversal.IsReversal);
        Assert.Equal(20, reversal.ReversedEntryId);
        Assert.Equal(CashMovementDirections.Expense, reversal.Direction); // invertida
        Assert.Equal(100m, reversal.Amount);
        Assert.Equal(5, reversal.PaymentId); // conserva el FK de origen para trazabilidad
    }

    // ====================================================================================
    // PaymentService: cobro -> 1 asiento; editar -> reversa+nuevo; borrar -> reversa
    // ====================================================================================

    [Fact]
    public async Task CreatePayment_WritesExactlyOneIncomeLedgerEntry()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context);
        var service = BuildPaymentService(context);

        await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(), Amount = 400m, Method = "Transfer",
        }, CancellationToken.None);

        var entries = await context.CashLedgerEntries.ToListAsync();
        Assert.Single(entries);
        var entry = entries[0];
        Assert.Equal(CashMovementDirections.Income, entry.Direction);
        Assert.Equal(400m, entry.Amount);
        Assert.Equal(CashLedgerSourceTypes.CustomerPayment, entry.SourceType);
        Assert.False(entry.IsReversal);
        Assert.False(entry.IsReversed);
    }

    [Fact]
    public async Task EditPaymentAmount_MarksOldReversedAndAddsReversalAndNew()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context);
        var service = BuildPaymentService(context);

        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer",
        }, CancellationToken.None);

        await service.UpdatePaymentAsync(dto.PublicId.ToString(), new UpdatePaymentRequest
        {
            Amount = 500m, Method = "Transfer",
        }, CancellationToken.None);

        var entries = await context.CashLedgerEntries.OrderBy(e => e.Id).ToListAsync();
        // 3 asientos: original (revertido), reversa, nuevo.
        Assert.Equal(3, entries.Count);

        var original = entries[0];
        Assert.True(original.IsReversed);
        Assert.False(original.IsReversal);
        Assert.Equal(300m, original.Amount);

        var reversal = entries.Single(e => e.IsReversal);
        Assert.Equal(300m, reversal.Amount);
        Assert.Equal(CashMovementDirections.Expense, reversal.Direction);
        Assert.Equal(original.Id, reversal.ReversedEntryId);

        // El unico asiento VIGENTE (no reversa, no revertido) es el nuevo por 500.
        var vigente = entries.Single(e => !e.IsReversal && !e.IsReversed);
        Assert.Equal(500m, vigente.Amount);

        // Neto del libro = 300 - 300 + 500 = 500 (la edicion no reescribio el pasado).
        decimal neto = entries.Sum(e =>
            e.Direction == CashMovementDirections.Income ? e.Amount : -e.Amount);
        Assert.Equal(500m, neto);
    }

    [Fact]
    public async Task DeletePayment_AddsReversalAndNetsToZero()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context);
        var service = BuildPaymentService(context);

        var dto = await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(), Amount = 250m, Method = "Transfer",
        }, CancellationToken.None);

        await service.DeletePaymentAsync(dto.PublicId.ToString(), CancellationToken.None);

        var entries = await context.CashLedgerEntries.ToListAsync();
        Assert.Equal(2, entries.Count); // original revertido + reversa
        Assert.Contains(entries, e => e.IsReversed && !e.IsReversal);
        Assert.Contains(entries, e => e.IsReversal);
        // No queda ningun asiento vigente para ese pago.
        Assert.DoesNotContain(entries, e => !e.IsReversal && !e.IsReversed);

        decimal neto = entries.Sum(e =>
            e.Direction == CashMovementDirections.Income ? e.Amount : -e.Amount);
        Assert.Equal(0m, neto);
    }

    // ====================================================================================
    // Sobrepago (Q1): excedente -> ClientCreditEntry; NO genera asiento extra
    // ====================================================================================

    [Fact]
    public async Task Overpayment_ConvertsExcessToClientCredit_ReservaSettledToZero_NoExtraLedgerEntry()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, salePrice: 1000m, payerId: 7);
        var service = BuildPaymentService(context);

        // El cliente paga 1500 sobre un saldo de 1000 -> excedente 500.
        await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(), Amount = 1500m, Method = "Transfer",
        }, CancellationToken.None);

        // Se creo un ClientCreditEntry de origen sobrepago por 500.
        var credit = await context.ClientCreditEntries.SingleAsync();
        Assert.Equal(500m, credit.CreditedAmount);
        Assert.Equal(500m, credit.RemainingBalance);
        Assert.Equal(Monedas.ARS, credit.Currency);
        Assert.Null(credit.BookingCancellationId); // discriminador de sobrepago (guarda B5)
        Assert.Null(credit.OperatorRefundAllocationId);
        Assert.Equal(7, credit.CustomerId);
        Assert.Equal(1, credit.SourceReservaId);
        Assert.NotNull(credit.SourcePaymentId);

        // La reserva queda saldada en 0 en ARS.
        var row = await context.ReservaMoneyByCurrency.SingleAsync(m => m.ReservaId == 1 && m.Currency == Monedas.ARS);
        Assert.Equal(0m, row.Balance);

        // En el Libro de Caja hay UN solo asiento (el cobro real de 1500). El puente AffectsCash=false NO
        // genera asiento (la plata ya entro una sola vez).
        var ledger = await context.CashLedgerEntries.ToListAsync();
        Assert.Single(ledger);
        Assert.Equal(1500m, ledger[0].Amount);
    }

    [Fact]
    public async Task ExactPayment_NoOverpayment_NoClientCredit()
    {
        await using var context = new AppDbContext(_dbOptions);
        var reserva = await SeedReservaAsync(context, salePrice: 1000m, payerId: 7);
        var service = BuildPaymentService(context);

        await service.CreatePaymentAsync(new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(), Amount = 1000m, Method = "Transfer",
        }, CancellationToken.None);

        Assert.False(await context.ClientCreditEntries.AnyAsync());
        Assert.Single(await context.CashLedgerEntries.ToListAsync());
    }

    // ====================================================================================
    // Backfill: idempotente (2 corridas = mismos asientos)
    // ====================================================================================

    [Fact]
    public async Task Backfill_IsIdempotent_RunningTwiceDoesNotDuplicate()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context);

        // Sembramos hechos vivos SIN asiento (simula datos legacy previos al libro).
        context.Payments.Add(new Payment
        {
            Id = 50, ReservaId = 1, Amount = 100m, Currency = Monedas.ARS, Method = "Transfer",
            PaidAt = DateTime.UtcNow, Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
        });
        context.SupplierPayments.Add(new SupplierPayment
        {
            Id = 60, SupplierId = 0, Amount = 80m, Currency = Monedas.ARS, Method = "Transfer", PaidAt = DateTime.UtcNow,
        });
        context.ManualCashMovements.Add(new ManualCashMovement
        {
            Id = 70, Direction = CashMovementDirections.Expense, Amount = 30m, Currency = Monedas.ARS,
            Method = "Cash", Category = "Oficina", Description = "Gasto", OccurredAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var backfill = new CashLedgerBackfillService(context, NullLogger<CashLedgerBackfillService>.Instance);

        Assert.True(await backfill.NeedsBackfillAsync());
        var first = await backfill.RunAsync();
        Assert.Equal((1, 1, 1), first);

        int afterFirst = await context.CashLedgerEntries.CountAsync();
        Assert.Equal(3, afterFirst);

        // Segunda corrida: nada pendiente, no duplica.
        Assert.False(await backfill.NeedsBackfillAsync());
        var second = await backfill.RunAsync();
        Assert.Equal((0, 0, 0), second);
        Assert.Equal(3, await context.CashLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Backfill_DoesNotAssentSoftDeletedPaymentAsLive()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context);

        context.Payments.Add(new Payment
        {
            Id = 51, ReservaId = 1, Amount = 100m, Currency = Monedas.ARS, Method = "Transfer",
            PaidAt = DateTime.UtcNow, Status = "Paid", EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true, IsDeleted = true, DeletedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var backfill = new CashLedgerBackfillService(context, NullLogger<CashLedgerBackfillService>.Instance);
        await backfill.RunAsync();

        // El pago soft-deleted (excluido por el query filter global) NO genera asiento.
        Assert.False(await context.CashLedgerEntries.AnyAsync(e => e.PaymentId == 51));
    }
}
