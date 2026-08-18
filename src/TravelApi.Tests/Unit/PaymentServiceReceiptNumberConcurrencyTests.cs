using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bug concurrencia/numeracion de recibos (2026-06-18).
///
/// El numero de recibo se generaba con CountAsync()+1, calculo no atomico: dos emisiones simultaneas
/// (o un doble-click) podian leer el mismo Count y producir el MISMO numero correlativo. El fix:
///   - indice UNIQUE en PaymentReceipt.ReceiptNumber (garantia real en Postgres);
///   - reintento sobre DbUpdateException en PaymentService.CreateReceiptWithCorrelativeNumberAsync
///     (recomputa el numero y reintenta, mismo patron que AlertService.UpsertDismissalAsync).
///
/// LIMITE DE ESTOS UNIT TESTS: el provider InMemory NO aplica indices UNIQUE, asi que NO puede
/// reproducir la colision REAL de "dos inserts compiten por el mismo numero". Eso queda para los
/// tests de integracion contra Postgres. Aca cubrimos lo que SI es testeable sin la DB real:
///   1. el formato del numero generado se mantiene (regresion) y la secuencia incrementa;
///   2. la logica de reintento recomputa el numero cuando el primer SaveChanges falla con
///      DbUpdateException — simulamos ese fallo con un interceptor, sin depender del UNIQUE.
/// </summary>
public class PaymentServiceReceiptNumberConcurrencyTests
{
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public PaymentServiceReceiptNumberConcurrencyTests()
    {
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private DbContextOptions<AppDbContext> BuildOptions(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

        if (interceptor != null)
            builder.AddInterceptors(interceptor);

        return builder.Options;
    }

    private PaymentService BuildPaymentService(AppDbContext context)
        => new(
            context,
            new EntityReferenceResolver(context),
            _mapper,
            _settingsServiceMock.Object,
            NullLogger<PaymentService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null,
            approvalService: null,
            approvalPolicyService: null,
            auditService: null);

    private static async Task SeedPaidPaymentAsync(AppDbContext context, int paymentId)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0010",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            TotalSale = 1000m,
            TotalCost = 0m,
            Balance = 1000m,
            TotalPaid = 0m
        };
        context.Reservas.Add(reserva);
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = 1000m, NetCost = 0m, Commission = 1000m,
            CreatedAt = DateTime.UtcNow
        });
        context.Payments.Add(new Payment
        {
            Id = paymentId, ReservaId = 1, Amount = 200m, IsDeleted = false, Status = "Paid",
            Method = "Transfer", PaidAt = DateTime.UtcNow,
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true
        });
        await context.SaveChangesAsync();
    }

    // ===== 1. Regresion de formato + secuencia =====

    [Fact]
    public async Task IssueReceipt_FirstReceipt_UsesExpectedNumberFormat()
    {
        await using var context = new AppDbContext(BuildOptions());
        await SeedPaidPaymentAsync(context, paymentId: 501);
        var service = BuildPaymentService(context);

        var result = await service.IssueReceiptAsync(paymentId: 501, CancellationToken.None);

        // Formato historico: "RCP-{anio}-{secuencia de 6 digitos}". Primer recibo => secuencia 1.
        var expected = $"RCP-{DateTime.UtcNow:yyyy}-000001";
        Assert.Equal(expected, result.ReceiptNumber);
    }

    [Fact]
    public async Task IssueReceipt_SecondReceipt_IncrementsSequence()
    {
        await using var context = new AppDbContext(BuildOptions());
        await SeedPaidPaymentAsync(context, paymentId: 502);
        // Segundo pago de la misma reserva para emitir un segundo recibo.
        context.Payments.Add(new Payment
        {
            Id = 503, ReservaId = 1, Amount = 300m, IsDeleted = false, Status = "Paid",
            Method = "Transfer", PaidAt = DateTime.UtcNow,
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true
        });
        await context.SaveChangesAsync();
        var service = BuildPaymentService(context);

        var first = await service.IssueReceiptAsync(paymentId: 502, CancellationToken.None);
        var second = await service.IssueReceiptAsync(paymentId: 503, CancellationToken.None);

        Assert.Equal($"RCP-{DateTime.UtcNow:yyyy}-000001", first.ReceiptNumber);
        Assert.Equal($"RCP-{DateTime.UtcNow:yyyy}-000002", second.ReceiptNumber);
    }

    // ===== 1.b. Candado de regresion: la secuencia GLOBAL no se pisa con un pago deshecho en el medio =====

    /// <summary>
    /// Tanda 7.2 post-rollout (2026-08-18): PaymentReceipt gana un HasQueryFilter que espeja el soft-delete
    /// de su Payment padre (mismo espiritu que Payment/SupplierPayment). Pero OJO — GenerateReceiptNumberAsync
    /// NO puede usar ese filtro para su CountAsync: la secuencia es GLOBAL y NUNCA se reinicia ni salta numeros
    /// (ver el comentario de esa funcion), y el indice UNIQUE de ReceiptNumber es una restriccion FISICA de la
    /// tabla que el filtro no oculta. Si el conteo excluyera los recibos de pagos deshechos, calcularia un
    /// numero mas chico que el fisicamente ya usado por esos recibos "ocultos" -> el INSERT chocaria SIEMPRE
    /// contra el UNIQUE, el reintento recalcularia el MISMO numero chico de nuevo, y tras 5 intentos la emision
    /// de recibos quedaria rota para SIEMPRE en cuanto exista un solo pago deshecho con recibo Voided (que es
    /// un escenario real y permitido, ver PaymentServiceDeleteTests.DeletePaymentAsync_WithVoidedReceipt_...).
    ///
    /// Por eso GenerateReceiptNumberAsync sigue contando con IgnoreQueryFilters(): este test es el candado de
    /// regresion que prueba que la numeracion NO se rompe con un pago deshecho de por medio.
    /// </summary>
    [Fact]
    public async Task IssueReceipt_AfterPriorPaymentWasDeletedWithVoidedReceipt_SequenceKeepsIncrementing_NoCollision()
    {
        await using var context = new AppDbContext(BuildOptions());
        await SeedPaidPaymentAsync(context, paymentId: 601);
        var service = BuildPaymentService(context);

        // 1) Se emite el primer recibo (numero 000001)...
        var firstReceipt = await service.IssueReceiptAsync(paymentId: 601, CancellationToken.None);
        Assert.Equal($"RCP-{DateTime.UtcNow:yyyy}-000001", firstReceipt.ReceiptNumber);

        // 2) ...se anula (Voided, la unica forma de poder deshacer el pago despues)...
        await service.VoidReceiptAsync(
            paymentPublicIdOrLegacyId: "601", reason: "Error de carga", userId: "u1", userName: "Vendedor 1",
            requesterIsAdmin: true, cancellationToken: CancellationToken.None);

        // 3) ...y se deshace el pago (soft-delete). La fila del recibo Voided se PRESERVA en la tabla
        // (DeleteBehavior.Restrict), pero el pago que la respalda queda IsDeleted = true.
        await service.DeletePaymentAsync("601", CancellationToken.None);

        // 4) Un pago NUEVO, sin relacion con el anterior, emite su propio recibo.
        context.Payments.Add(new Payment
        {
            Id = 602, ReservaId = 1, Amount = 300m, IsDeleted = false, Status = "Paid",
            Method = "Transfer", PaidAt = DateTime.UtcNow,
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true
        });
        await context.SaveChangesAsync();

        var secondReceipt = await service.IssueReceiptAsync(paymentId: 602, CancellationToken.None);

        // La secuencia GLOBAL sigue: numero 000002, NO se reusa el 000001 (que sigue fisicamente ocupado
        // por el recibo Voided del pago deshecho, aunque ese recibo ya no aparezca en consultas por defecto).
        Assert.Equal($"RCP-{DateTime.UtcNow:yyyy}-000002", secondReceipt.ReceiptNumber);
    }

    // ===== 2. Logica de reintento (recompute) sin depender del UNIQUE =====

    [Fact]
    public async Task IssueReceipt_WhenFirstSaveCollides_RecomputesAndRetries()
    {
        // Interceptor que lanza DbUpdateException en el PRIMER SaveChanges y deja pasar el resto.
        // Simula la colision del indice UNIQUE en Postgres sin necesitar la DB real.
        var interceptor = new ThrowOnceOnSaveInterceptor();
        await using var context = new AppDbContext(BuildOptions(interceptor));
        await SeedPaidPaymentAsync(context, paymentId: 504);

        // El interceptor empieza a "armar la trampa" recien ahora, para no chocar con el seed.
        interceptor.Arm();

        var service = new RecordingPaymentService(
            context, new EntityReferenceResolver(context), _mapper,
            _settingsServiceMock.Object);

        var result = await service.IssueReceiptAsync(paymentId: 504, CancellationToken.None);

        // El recibo termino persistido (el reintento funciono).
        Assert.Equal($"RCP-{DateTime.UtcNow:yyyy}-000001", result.ReceiptNumber);
        // Se recomputo el numero: GenerateReceiptNumberAsync se llamo DOS veces (intento + reintento).
        Assert.Equal(2, service.GenerateCallCount);
        Assert.Equal(1, await context.PaymentReceipts.CountAsync(r => r.PaymentId == 504));
    }

    /// <summary>
    /// Subclase de prueba que cuenta cuantas veces se recomputa el numero de recibo. Sirve para
    /// verificar que el reintento (CreateReceiptWithCorrelativeNumberAsync) recalcula el numero
    /// ante un choque, en vez de reusar el mismo.
    /// </summary>
    private sealed class RecordingPaymentService : PaymentService
    {
        public int GenerateCallCount { get; private set; }

        public RecordingPaymentService(
            AppDbContext dbContext,
            IEntityReferenceResolver entityReferenceResolver,
            IMapper mapper,
            IOperationalFinanceSettingsService settingsService)
            : base(dbContext, entityReferenceResolver, mapper, settingsService,
                   NullLogger<PaymentService>.Instance)
        {
        }

        protected override Task<string> GenerateReceiptNumberAsync(CancellationToken cancellationToken)
        {
            GenerateCallCount++;
            return base.GenerateReceiptNumberAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Interceptor de prueba: una vez "armado", lanza DbUpdateException en el proximo SaveChanges y
    /// luego se desarma. Reproduce la colision del UNIQUE (que InMemory no enforce) de forma deterministica.
    /// </summary>
    private sealed class ThrowOnceOnSaveInterceptor : SaveChangesInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                throw new DbUpdateException("Simulated unique violation on ReceiptNumber.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
