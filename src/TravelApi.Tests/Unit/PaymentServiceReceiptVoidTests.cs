using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 (2026-05-11): nuevos guards y endpoint para anular comprobantes de pago.
///
/// Cubre:
///  - IssueReceiptAsync: rechaza si Payment soft-deleted, si Payment.Status="Cancelled",
///    y si existe Receipt previo Voided (no reemite automaticamente).
///  - IssueReceiptAsync: idempotente cuando Receipt existente esta Issued.
///  - VoidReceiptAsync: feliz (Admin bypass), idempotencia (Voided -> 409), no-receipt -> 409,
///    falta de approval (Vendedor) -> ApprovalRequiredException, y delete post-void permite delete.
/// </summary>
public class PaymentServiceReceiptVoidTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public PaymentServiceReceiptVoidTests()
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

    private PaymentService BuildPaymentService(
        AppDbContext context,
        IApprovalRequestService? approvalService = null,
        IApprovalPolicyService? approvalPolicyService = null,
        IAuditService? auditService = null)
        => new(
            context,
            new EntityReferenceResolver(context),
            _mapper,
            _settingsServiceMock.Object,
            NullLogger<PaymentService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null,
            approvalService: approvalService,
            approvalPolicyService: approvalPolicyService,
            auditService: auditService);

    private static async Task SeedReservaWithServiceAsync(AppDbContext context, decimal salePrice = 1000m)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0010",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            TotalSale = salePrice,
            TotalCost = 0m,
            Balance = salePrice,
            TotalPaid = 0m
        };
        context.Reservas.Add(reserva);
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

    private static Payment NewPayment(int id, string status = "Paid", bool isDeleted = false, decimal amount = 200m)
        => new()
        {
            Id = id, ReservaId = 1, Amount = amount, IsDeleted = isDeleted, Status = status,
            Method = "Transfer", PaidAt = DateTime.UtcNow,
            EntryType = PaymentEntryTypes.Payment, AffectsCash = true,
            DeletedAt = isDeleted ? DateTime.UtcNow : (DateTime?)null,
        };

    // ===== IssueReceipt guards (Parte A del plan) =====

    [Fact]
    public async Task IssueReceipt_WhenPaymentIsDeleted_Throws_NotPersisted()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        context.Payments.Add(NewPayment(id: 201, isDeleted: true));
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException (hereda de
        // InvalidOperationException, pero xUnit exige tipo EXACTO en Assert.ThrowsAsync<T>).
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => service.IssueReceiptAsync(paymentId: 201, CancellationToken.None));
        Assert.Contains("anulado o eliminado", ex.Message, StringComparison.OrdinalIgnoreCase);

        // No se persistio ningun recibo.
        Assert.Equal(0, await context.PaymentReceipts.CountAsync(r => r.PaymentId == 201));
    }

    [Fact]
    public async Task IssueReceipt_WhenPaymentStatusCancelled_Throws_NotPersisted()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        context.Payments.Add(NewPayment(id: 202, status: "Cancelled"));
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => service.IssueReceiptAsync(paymentId: 202, CancellationToken.None));
        Assert.Contains("anulado o eliminado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.PaymentReceipts.CountAsync(r => r.PaymentId == 202));
    }

    [Fact]
    public async Task IssueReceipt_WhenPaymentHadVoidedReceipt_Throws_NotReemitted()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        context.Payments.Add(NewPayment(id: 203));
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 80, PaymentId = 203, ReservaId = 1,
            ReceiptNumber = "REC-VOIDED-001", Amount = 200m,
            Status = PaymentReceiptStatuses.Voided,
            IssuedAt = DateTime.UtcNow.AddDays(-1),
            VoidedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => service.IssueReceiptAsync(paymentId: 203, CancellationToken.None));
        Assert.Contains("REC-VOIDED-001", ex.Message);

        // El receipt Voided sigue siendo el unico — no se creo uno nuevo.
        Assert.Equal(1, await context.PaymentReceipts.CountAsync(r => r.PaymentId == 203));
    }

    [Fact]
    public async Task IssueReceipt_WhenPaymentHasIssuedReceipt_ReturnsExistingIdempotent()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        context.Payments.Add(NewPayment(id: 204));
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 81, PaymentId = 204, ReservaId = 1,
            ReceiptNumber = "REC-ISSUED-001", Amount = 200m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var result = await service.IssueReceiptAsync(paymentId: 204, CancellationToken.None);

        Assert.Equal("REC-ISSUED-001", result.ReceiptNumber);
        // Idempotente: sigue existiendo solo 1 recibo.
        Assert.Equal(1, await context.PaymentReceipts.CountAsync(r => r.PaymentId == 204));
    }

    // ===== VoidReceipt happy path & idempotencia (Parte D del plan) =====

    [Fact]
    public async Task VoidReceipt_WhenIssued_Succeeds_PopulatesFields()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = NewPayment(id: 301);
        context.Payments.Add(payment);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 90, PaymentId = 301, ReservaId = 1,
            ReceiptNumber = "REC-300", Amount = 200m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        // Admin path: requesterIsAdmin=true => bypassa workflow incluso si policy on.
        var service = BuildPaymentService(context);

        await service.VoidReceiptAsync(
            payment.PublicId.ToString(),
            reason: "Error de carga",
            userId: "admin-1",
            userName: "Admin Test",
            requesterIsAdmin: true,
            cancellationToken: CancellationToken.None);

        var receipt = await context.PaymentReceipts.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == 90);
        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);
        Assert.NotNull(receipt.VoidedAt);
        Assert.Equal("admin-1", receipt.VoidedByUserId);
        Assert.Equal("Admin Test", receipt.VoidedByUserName);
        Assert.Equal("Error de carga", receipt.VoidReason);
        // Fila persiste con numeracion original.
        Assert.Equal("REC-300", receipt.ReceiptNumber);
    }

    [Fact]
    public async Task VoidReceipt_WhenVoidedAlready_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = NewPayment(id: 302);
        context.Payments.Add(payment);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 91, PaymentId = 302, ReservaId = 1,
            ReceiptNumber = "REC-301", Amount = 200m,
            Status = PaymentReceiptStatuses.Voided,
            IssuedAt = DateTime.UtcNow.AddDays(-1), VoidedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => service.VoidReceiptAsync(
                payment.PublicId.ToString(),
                reason: null, userId: "admin", userName: null,
                requesterIsAdmin: true, cancellationToken: CancellationToken.None));
        Assert.Contains("no existe o ya esta anulado", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VoidReceipt_WhenNoReceipt_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = NewPayment(id: 303);
        context.Payments.Add(payment);
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);

        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => service.VoidReceiptAsync(
                payment.PublicId.ToString(),
                reason: null, userId: "admin", userName: null,
                requesterIsAdmin: true, cancellationToken: CancellationToken.None));
        Assert.Contains("no existe o ya esta anulado", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VoidReceipt_AdminBypassesApproval_Succeeds()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = NewPayment(id: 304);
        context.Payments.Add(payment);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 94, PaymentId = 304, ReservaId = 1,
            ReceiptNumber = "REC-304", Amount = 200m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var policyMock = new Mock<IApprovalPolicyService>();
        policyMock
            .Setup(p => p.RequiresApprovalAsync(
                ApprovalRequestType.ReceiptVoidance, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var approvalMock = new Mock<IApprovalRequestService>();
        // Si el bypass de Admin no se aplicara, el service consultaria FindActiveApproved.
        // Setup defensivo: si llega, devolvemos null y eso provocaria ApprovalRequiredException.

        var service = BuildPaymentService(context, approvalMock.Object, policyMock.Object);

        // Admin: NO debe consultar policy ni approval service.
        await service.VoidReceiptAsync(
            payment.PublicId.ToString(),
            reason: "Admin override",
            userId: "admin-2",
            userName: "Admin",
            requesterIsAdmin: true,
            cancellationToken: CancellationToken.None);

        var receipt = await context.PaymentReceipts.AsNoTracking().FirstAsync(r => r.Id == 94);
        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);
        policyMock.Verify(p => p.RequiresApprovalAsync(
            It.IsAny<ApprovalRequestType>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        approvalMock.Verify(a => a.FindActiveApprovedAsync(
            It.IsAny<ApprovalRequestType>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VoidReceipt_VendedorTriggersApprovalRequest_WhenNoApprovalActive()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = NewPayment(id: 305);
        context.Payments.Add(payment);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 95, PaymentId = 305, ReservaId = 1,
            ReceiptNumber = "REC-305", Amount = 200m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var policyMock = new Mock<IApprovalPolicyService>();
        policyMock
            .Setup(p => p.RequiresApprovalAsync(
                ApprovalRequestType.ReceiptVoidance, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var approvalMock = new Mock<IApprovalRequestService>();
        approvalMock
            .Setup(a => a.FindActiveApprovedAsync(
                ApprovalRequestType.ReceiptVoidance, "PaymentReceipt", 95, "vendedor-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApprovalRequest?)null);

        var service = BuildPaymentService(context, approvalMock.Object, policyMock.Object);

        var ex = await Assert.ThrowsAsync<ApprovalRequiredException>(
            () => service.VoidReceiptAsync(
                payment.PublicId.ToString(),
                reason: "Error",
                userId: "vendedor-1",
                userName: "Vendedor",
                requesterIsAdmin: false,
                cancellationToken: CancellationToken.None));

        Assert.Equal(ApprovalRequestType.ReceiptVoidance, ex.RequestType);
        Assert.Equal("PaymentReceipt", ex.EntityType);
        Assert.Equal(95, ex.EntityId);

        // El receipt sigue Issued — no se mutó.
        var receipt = await context.PaymentReceipts.AsNoTracking().FirstAsync(r => r.Id == 95);
        Assert.Equal(PaymentReceiptStatuses.Issued, receipt.Status);
    }

    [Fact]
    public async Task VoidReceipt_VendedorWithApprovedRequest_Succeeds_AndMarksConsumed()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = NewPayment(id: 306);
        context.Payments.Add(payment);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 96, PaymentId = 306, ReservaId = 1,
            ReceiptNumber = "REC-306", Amount = 200m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var policyMock = new Mock<IApprovalPolicyService>();
        policyMock
            .Setup(p => p.RequiresApprovalAsync(
                ApprovalRequestType.ReceiptVoidance, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var approvedRequest = new ApprovalRequest
        {
            Id = 777,
            RequestType = ApprovalRequestType.ReceiptVoidance,
            EntityType = "PaymentReceipt",
            EntityId = 96,
            RequestedByUserId = "vendedor-1",
            Status = ApprovalStatus.Approved,
            Reason = "Motivo del vendedor desde modal"
        };
        var approvalMock = new Mock<IApprovalRequestService>();
        approvalMock
            .Setup(a => a.FindActiveApprovedAsync(
                ApprovalRequestType.ReceiptVoidance, "PaymentReceipt", 96, "vendedor-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(approvedRequest);

        var service = BuildPaymentService(context, approvalMock.Object, policyMock.Object);

        // Reason null al llamar: deberia heredar el del approval.
        await service.VoidReceiptAsync(
            payment.PublicId.ToString(),
            reason: null,
            userId: "vendedor-1",
            userName: "Vendedor",
            requesterIsAdmin: false,
            cancellationToken: CancellationToken.None);

        var receipt = await context.PaymentReceipts.AsNoTracking().FirstAsync(r => r.Id == 96);
        Assert.Equal(PaymentReceiptStatuses.Voided, receipt.Status);
        Assert.Equal("Motivo del vendedor desde modal", receipt.VoidReason);
        approvalMock.Verify(a => a.MarkConsumedAsync(777, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ===== Parte E: delete post-void permite delete =====

    [Fact]
    public async Task DeletePayment_AfterReceiptVoided_Succeeds()
    {
        // E2E ligero: el ciclo Issued -> Voided -> DeletePayment es el caso de uso
        // que motivo el cambio de regla. Confirmamos que el Payment se borra y la
        // fila Receipt sigue en DB.
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithServiceAsync(context);
        var payment = NewPayment(id: 400);
        context.Payments.Add(payment);
        context.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 200, PaymentId = 400, ReservaId = 1,
            ReceiptNumber = "REC-400", Amount = 200m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildPaymentService(context);
        var publicId = payment.PublicId.ToString();

        // 1) Vendedor (o Admin) anula el comprobante. Aca pasamos Admin para evitar
        //    setear policy/approval — el flow de Vendedor ya tiene su propio test.
        await service.VoidReceiptAsync(
            publicId, reason: "ajuste", userId: "admin", userName: "Admin",
            requesterIsAdmin: true, cancellationToken: CancellationToken.None);

        // 2) Ahora el delete del pago procede (regla 2026-05-11).
        await service.DeletePaymentAsync(publicId, CancellationToken.None);

        var refreshed = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p => p.Id == 400);
        Assert.True(refreshed.IsDeleted);

        var receiptStill = await context.PaymentReceipts.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.Id == 200);
        Assert.Equal(PaymentReceiptStatuses.Voided, receiptStill.Status);
        Assert.Equal("REC-400", receiptStill.ReceiptNumber);
    }
}
