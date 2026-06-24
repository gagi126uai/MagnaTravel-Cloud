using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Exceptions;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// CAMBIO 1 (2026-06-24) — tests UNIT (EF InMemory) del BYPASS del Admin sobre la barrera de doble firma
/// del retiro <c>ReversedToOperator</c> (el cliente devuelve plata ya retirada para que la agencia se la
/// re-acredite al operador). Hoy el dueno es el unico Admin y pedirse a si mismo el approval
/// <c>ClientRefundReversal</c> es teatro (se auto-aprobaba): el Admin lo saltea, pero queda el audit
/// <c>AdminSelfAuthorized</c>. Un no-Admin sin approval SIGUE rebotando (la maquinaria no se borro).
///
/// <para><b>Nota InMemory</b>: el provider InMemory no soporta transacciones ni CHECK constraints; el flujo
/// de WithdrawAsync para este kind no usa transaccion envolvente (no toca otra reserva), asi que corre igual.</para>
/// </summary>
public class ClientCreditReversalAdminBypassTests
{
    private static AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static (ClientCreditService Service, Mock<IAuditService> AuditMock) BuildService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        // El modulo de cancelacion/refund debe estar ON para que WithdrawAsync opere.
        settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true });

        var auditMock = new Mock<IAuditService>();

        var service = new ClientCreditService(
            ctx,
            Mock.Of<IBookingCancellationService>(),
            Mock.Of<IApprovalRequestService>(),
            auditMock.Object,
            settingsMock.Object,
            NullLogger<ClientCreditService>.Instance);

        return (service, auditMock);
    }

    /// <summary>Crea un cliente + un bolsillo (ClientCreditEntry de SOBREPAGO, sin BC detras) con saldo.</summary>
    private static async Task<Guid> SeedEntryAsync(AppDbContext ctx, decimal balance = 500m, string currency = "ARS")
    {
        var customer = new Customer { FullName = "Cliente Reversal", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var entry = new ClientCreditEntry
        {
            CustomerId = customer.Id,
            Currency = currency,
            CreditedAmount = balance,
            RemainingBalance = balance,
            CreatedAt = DateTime.UtcNow,
            // SourcePaymentId marca origen sobrepago: sin BookingCancellationId no dispara cierre de BC.
            SourcePaymentId = 999,
        };
        ctx.ClientCreditEntries.Add(entry);
        await ctx.SaveChangesAsync();
        return entry.PublicId;
    }

    private static WithdrawClientCreditRequest ReversalRequest(decimal amount = 100m, Guid? approvalPublicId = null, string? reference = null)
        => new(
            Kind: WithdrawalKind.ReversedToOperator,
            Amount: amount,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: null,
            ApprovalRequestPublicId: approvalPublicId,
            Reference: reference);

    [Fact]
    public async Task AdminBypass_NoApproval_BuildsWithdrawalWithoutApprovalAndLogsSelfAuthorized()
    {
        await using var ctx = NewContext();
        var (svc, auditMock) = BuildService(ctx);
        var entryPublicId = await SeedEntryAsync(ctx, balance: 500m);

        // Admin, SIN approval ClientRefundReversal -> bypass (antes tiraba ApprovalRequiredException).
        var dto = await svc.WithdrawAsync(
            entryPublicId, ReversalRequest(amount: 100m, approvalPublicId: null, reference: "dev-operador-123"),
            userId: "owner", userName: "Owner", ct: CancellationToken.None, requesterIsAdmin: true);

        // El withdrawal se construyo SIN approval consumido (ApprovalRequestId null).
        Assert.Equal(WithdrawalKind.ReversedToOperator, dto.Kind);
        Assert.Null(dto.ApprovalRequestId);

        // Genero el ManualCashMovement (la plata vuelve a caja). El DTO no expone el id del movement
        // (MapWithdrawal lo deja null a proposito), asi que verificamos sobre la tabla persistida.
        Assert.Single(ctx.ManualCashMovements);

        // El saldo del bolsillo bajo.
        var entry = ctx.ClientCreditEntries.Single();
        Assert.Equal(400m, entry.RemainingBalance);

        // Quedo el audit AdminSelfAuthorized (ademas del audit base ClientCreditWithdrawn).
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.AdminSelfAuthorized,
            AuditActions.ClientCreditEntryEntityName,
            It.IsAny<string>(),
            It.IsAny<string?>(),
            "owner",
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NonAdmin_NoApproval_ThrowsApprovalRequired()
    {
        await using var ctx = NewContext();
        var (svc, auditMock) = BuildService(ctx);
        var entryPublicId = await SeedEntryAsync(ctx, balance: 500m);

        // No-Admin, SIN approval -> sigue rebotando (el bypass es SOLO para Admin).
        await Assert.ThrowsAsync<ApprovalRequiredException>(() =>
            svc.WithdrawAsync(
                entryPublicId, ReversalRequest(amount: 100m, approvalPublicId: null),
                userId: "vendedor", userName: "V", ct: CancellationToken.None, requesterIsAdmin: false));

        // No se emitio ningun audit AdminSelfAuthorized.
        auditMock.Verify(a => a.LogBusinessEventAsync(
            AuditActions.AdminSelfAuthorized,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // Y el saldo no se toco.
        Assert.Equal(500m, ctx.ClientCreditEntries.Single().RemainingBalance);
    }
}
