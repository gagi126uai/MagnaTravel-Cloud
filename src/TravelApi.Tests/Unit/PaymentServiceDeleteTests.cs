using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// C28 — Bloquear borrado de pagos cuando tienen un Receipt asociado
/// (Issued o Voided) o estan vinculados a una factura (RelatedInvoiceId).
///
/// IMPORTANTE: hay 2 paths para borrar un pago, ambos deben aplicar el guard:
///  1. PaymentService.DeletePaymentAsync (api/payments/{id}).
///  2. ReservaService.DeletePaymentAsync (api/reservas/{id}/payments/{id}, legacy nested).
///
/// Voided tambien bloquea: el recibo ocupa numeracion correlativa y debe
/// preservarse — confirmado por ARCA + Contable (2026-05-06).
/// </summary>
public class PaymentServiceDeleteTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public PaymentServiceDeleteTests()
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

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private ReservaService BuildReservaService(AppDbContext context)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

    private static async Task SeedReservaWithServiceAsync(AppDbContext context, decimal salePrice = 1000m)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            TotalSale = salePrice,
            TotalCost = 0m,
            Balance = salePrice,
            TotalPaid = 0m
        };
        context.Reservas.Add(reserva);
        // Servicio que sustenta TotalSale (RecalculateReservaBalanceAsync recalcula
        // a partir de servicios; sin servicio, TotalSale se reescribiria a 0).
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = salePrice, NetCost = 0m, Commission = salePrice,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static async Task<Payment> SeedPaymentAsync(AppDbContext context, int id, decimal amount = 100m)
    {
        var payment = new Payment
        {
            Id = id, ReservaId = 1, Amount = amount, IsDeleted = false, Status = "Paid",
            Method = "Transfer", PaidAt = DateTime.UtcNow,
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true
        };
        context.Payments.Add(payment);
        await context.SaveChangesAsync();
        return payment;
    }

    // ===== PaymentService.DeletePaymentAsync (api/payments/{id}) =====

    [Fact]
    public async Task DeletePaymentAsync_HappyPath_NoReceiptNoInvoice_SoftDeletes()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = await SeedPaymentAsync(context, id: 100, amount: 200m);

        var service = BuildPaymentService(context);

        await service.DeletePaymentAsync(payment.PublicId.ToString(), CancellationToken.None);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 100);
        Assert.True(refreshed.IsDeleted);
        Assert.NotNull(refreshed.DeletedAt);
    }

    [Fact]
    public async Task DeletePaymentAsync_WithIssuedReceipt_Throws_AndPreservesPayment()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = await SeedPaymentAsync(context, id: 101, amount: 300m);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 50, PaymentId = 101, ReservaId = 1,
            ReceiptNumber = "REC-001", Amount = 300m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeletePaymentAsync(payment.PublicId.ToString(), CancellationToken.None));
        Assert.Contains("recibo emitido", ex.Message, StringComparison.OrdinalIgnoreCase);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 101);
        Assert.False(refreshed.IsDeleted);
    }

    [Fact]
    public async Task DeletePaymentAsync_WithVoidedReceipt_Throws_PreservesNumeracion()
    {
        // ARCA + Contable (2026-05-06): recibos anulados ocupan numeracion correlativa
        // y deben preservarse para auditoria. Voided tambien bloquea el delete.
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = await SeedPaymentAsync(context, id: 102, amount: 400m);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 51, PaymentId = 102, ReservaId = 1,
            ReceiptNumber = "REC-002", Amount = 400m,
            Status = PaymentReceiptStatuses.Voided,
            IssuedAt = DateTime.UtcNow.AddDays(-1), VoidedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeletePaymentAsync(payment.PublicId.ToString(), CancellationToken.None));
        Assert.Contains("anulado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auditoria", ex.Message, StringComparison.OrdinalIgnoreCase);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 102);
        Assert.False(refreshed.IsDeleted);
    }

    [Fact]
    public async Task DeletePayment_PaymentWithVoidedAndIssuedReceipts_ShouldBlockWithIssuedMessage()
    {
        // Caso de defensa en profundidad: si por reemision o data legacy un payment
        // termina con 2 receipts (Voided + Issued), el guard debe devolver el mensaje
        // del Issued (el activo). Hoy el modelo declara 1:1 (Payment.Receipt) y el
        // flow lo bloquea, pero el guard NO debe asumir el invariante — antes de C28+
        // hacia FirstOrDefault sobre el set y el orden era inestable entre InMemory/Postgres.
        //
        // Sembrado: usamos un context aparte para insertar el segundo receipt sin que
        // el ChangeTracker dispare el fixup de la navegacion 1:1 sobre el mismo Payment.
        await using (var seedCtx = new AppDbContext(_dbOptions))
        {
            await SeedReservaWithServiceAsync(seedCtx);
            await SeedPaymentAsync(seedCtx, id: 105, amount: 500m);
            seedCtx.PaymentReceipts.Add(new PaymentReceipt
            {
                Id = 55, PaymentId = 105, ReservaId = 1,
                ReceiptNumber = "REC-100", Amount = 500m,
                Status = PaymentReceiptStatuses.Voided,
                IssuedAt = DateTime.UtcNow.AddDays(-2), VoidedAt = DateTime.UtcNow.AddDays(-1)
            });
            await seedCtx.SaveChangesAsync();
        }
        await using (var seedCtx2 = new AppDbContext(_dbOptions))
        {
            // Forzamos el segundo receipt sin cargar el Payment en este context — asi el
            // ChangeTracker no detecta el conflicto de la navegacion 1:1 inversa.
            seedCtx2.PaymentReceipts.Add(new PaymentReceipt
            {
                Id = 56, PaymentId = 105, ReservaId = 1,
                ReceiptNumber = "REC-101", Amount = 500m,
                Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
            });
            await seedCtx2.SaveChangesAsync();
        }

        await using var context = new AppDbContext(_dbOptions);
        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 105);

        // Verificacion del seed: hay 2 receipts persistidos.
        Assert.Equal(2, await context.PaymentReceipts.CountAsync(r => r.PaymentId == 105));

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeletePaymentAsync(payment.PublicId.ToString(), CancellationToken.None));
        Assert.Contains("anulá el recibo", ex.Message, StringComparison.OrdinalIgnoreCase);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 105);
        Assert.False(refreshed.IsDeleted);
    }

    [Fact]
    public async Task DeletePaymentAsync_WithRelatedInvoice_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        context.Invoices.Add(new Invoice
        {
            Id = 60, ReservaId = 1, CAE = "012",
            ImporteTotal = 100m, ImporteNeto = 82.64m, ImporteIva = 17.36m
        });
        await context.SaveChangesAsync();

        var payment = new Payment
        {
            Id = 103, ReservaId = 1, Amount = 100m, IsDeleted = false, Status = "Paid",
            Method = "Transfer", PaidAt = DateTime.UtcNow,
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
            RelatedInvoiceId = 60
        };
        context.Payments.Add(payment);
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeletePaymentAsync(payment.PublicId.ToString(), CancellationToken.None));
        Assert.Contains("factura", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nota de credito", ex.Message, StringComparison.OrdinalIgnoreCase);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 103);
        Assert.False(refreshed.IsDeleted);
    }

    // ===== ReservaService.DeletePaymentAsync (api/reservas/{id}/payments/{id}, legacy) =====

    [Fact]
    public async Task ReservaService_DeletePaymentAsync_HappyPath_SoftDeletes()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        await SeedPaymentAsync(context, id: 110, amount: 50m);

        var service = BuildReservaService(context);

        await service.DeletePaymentAsync(reservaId: 1, paymentId: 110);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 110);
        Assert.True(refreshed.IsDeleted);
    }

    [Fact]
    public async Task ReservaService_DeletePaymentAsync_WithIssuedReceipt_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        await SeedPaymentAsync(context, id: 111, amount: 70m);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 70, PaymentId = 111, ReservaId = 1,
            ReceiptNumber = "REC-010", Amount = 70m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeletePaymentAsync(reservaId: 1, paymentId: 111));
        Assert.Contains("recibo emitido", ex.Message, StringComparison.OrdinalIgnoreCase);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 111);
        Assert.False(refreshed.IsDeleted);
    }

    [Fact]
    public async Task ReservaService_DeletePaymentAsync_WithVoidedReceipt_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        await SeedPaymentAsync(context, id: 112, amount: 80m);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 71, PaymentId = 112, ReservaId = 1,
            ReceiptNumber = "REC-011", Amount = 80m,
            Status = PaymentReceiptStatuses.Voided,
            IssuedAt = DateTime.UtcNow.AddDays(-1), VoidedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeletePaymentAsync(reservaId: 1, paymentId: 112));
        Assert.Contains("anulado", ex.Message, StringComparison.OrdinalIgnoreCase);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 112);
        Assert.False(refreshed.IsDeleted);
    }

    // ===== Pin del contrato HTTP 409 en ambos controllers =====

    [Fact]
    public async Task PaymentsController_DeletePayment_MapsInvalidOperationExceptionTo409()
    {
        var paymentService = new Mock<IPaymentService>();
        paymentService
            .Setup(s => s.DeletePaymentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No se puede eliminar el pago porque tiene un recibo emitido."));

        var controller = new PaymentsController(paymentService.Object);

        var result = await controller.DeletePayment("payment-1", CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task ReservasController_DeletePayment_MapsInvalidOperationExceptionTo409()
    {
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(s => s.DeletePaymentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No se puede eliminar el pago porque esta vinculado a una factura."));

        var controller = new ReservasController(
            reservaService.Object,
            Mock.Of<IVoucherService>(),
            Mock.Of<ITimelineService>(),
            NullLogger<ReservasController>.Instance);

        var result = await controller.DeletePayment("reserva-1", "payment-1", CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }
}
