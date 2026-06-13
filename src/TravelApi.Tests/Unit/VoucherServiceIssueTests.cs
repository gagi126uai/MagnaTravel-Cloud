using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class VoucherServiceIssueTests
{
    [Fact]
    public async Task IssueVoucherAsync_AllowsNormalIssue_WhenReservationIsSettled()
    {
        using var db = CreateDbContext();
        GrantIssuePermission(db);
        var voucher = await SeedDraftVoucherAsync(db, balance: 0m);
        var service = CreateService(db);

        var result = await service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest(),
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None);

        Assert.Equal(VoucherStatuses.Issued, result.Status);
        Assert.True(result.CanSend);
        Assert.False(result.WasExceptionalIssue);

        var audit = await db.VoucherAuditEntries.SingleAsync(entry => entry.Action == VoucherAuditActions.Issued);
        Assert.False(audit.ReservationHadOutstandingBalance);
        Assert.Equal("issuer-1", audit.UserId);
    }

    [Fact]
    public async Task IssueVoucherAsync_BlocksIssue_WhenReservationHasOutstandingBalanceWithoutException()
    {
        using var db = CreateDbContext();
        GrantIssuePermission(db);
        var voucher = await SeedDraftVoucherAsync(db, balance: 120m);
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest(),
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None));

        Assert.Empty(db.VoucherAuditEntries.Where(entry => entry.Action == VoucherAuditActions.Issued || entry.Action == VoucherAuditActions.ExceptionalIssue));
    }

    [Fact]
    public async Task IssueVoucherAsync_AllowsExceptionalIssue_ForAdminAndAuditsOutstandingBalance()
    {
        using var db = CreateDbContext();
        var voucher = await SeedDraftVoucherAsync(db, balance: 120m);
        var service = CreateService(db);

        var result = await service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest { ExceptionalReason = "Emision autorizada por administracion" },
            new OperationActor("admin-1", "Admin", new[] { "Admin" }),
            CancellationToken.None);

        Assert.True(result.WasExceptionalIssue);
        Assert.Equal(120m, result.OutstandingBalance);

        var audit = await db.VoucherAuditEntries.SingleAsync(entry => entry.Action == VoucherAuditActions.ExceptionalIssue);
        Assert.True(audit.ReservationHadOutstandingBalance);
        Assert.Equal(120m, audit.OutstandingBalance);
        Assert.Equal("admin-1", audit.UserId);
        Assert.Null(audit.AuthorizedBySuperiorUserId);
    }

    [Fact]
    public async Task IssueVoucherAsync_RequestsAuthorization_WhenReservationHasOutstandingBalanceAndSuperiorIsSelected()
    {
        using var db = CreateDbContext();
        GrantIssuePermission(db);
        GrantSuperiorAuthorizationPermission(db);
        var voucher = await SeedDraftVoucherAsync(db, balance: 120m);
        var service = CreateService(db);

        var result = await service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest
            {
                ExceptionalReason = "Superior autoriza por urgencia operativa",
                AuthorizedBySuperiorUserId = "superior-1"
            },
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None);

        Assert.Equal(VoucherStatuses.PendingAuthorization, result.Status);
        Assert.Equal(VoucherAuthorizationStatuses.Pending, result.AuthorizationStatus);
        Assert.False(result.CanSend);
        Assert.True(result.WasExceptionalIssue);
        Assert.Equal("Superior", result.AuthorizedBySuperiorUserName);

        var audit = await db.VoucherAuditEntries.SingleAsync(entry => entry.Action == VoucherAuditActions.AuthorizationRequested);
        Assert.True(audit.ReservationHadOutstandingBalance);
        Assert.Equal("superior-1", audit.AuthorizedBySuperiorUserId);
        Assert.Equal("Superior", audit.AuthorizedBySuperiorUserName);
    }

    [Theory]
    [InlineData(VoucherStatuses.Draft)]
    [InlineData(VoucherStatuses.PendingAuthorization)]
    [InlineData(VoucherStatuses.Issued)]
    [InlineData(VoucherStatuses.UploadedExternal)]
    public async Task RevokeVoucherAsync_RevokesSupportedStatusesAndAuditsReason(string status)
    {
        using var db = CreateDbContext();
        GrantRevokePermission(db);
        var voucher = await SeedDraftVoucherAsync(db, balance: status == VoucherStatuses.PendingAuthorization ? 120m : 0m);
        voucher.Status = status;
        voucher.Source = status == VoucherStatuses.UploadedExternal ? VoucherSources.External : VoucherSources.Generated;
        voucher.IsEnabledForSending = status is VoucherStatuses.Issued or VoucherStatuses.UploadedExternal;
        voucher.AuthorizationStatus = status == VoucherStatuses.PendingAuthorization
            ? VoucherAuthorizationStatuses.Pending
            : VoucherAuthorizationStatuses.NotRequired;
        voucher.AuthorizedBySuperiorUserId = status == VoucherStatuses.PendingAuthorization ? "superior-1" : null;
        voucher.AuthorizedBySuperiorUserName = status == VoucherStatuses.PendingAuthorization ? "Superior" : null;
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var result = await service.RevokeVoucherAsync(
            voucher.PublicId.ToString(),
            new RevokeVoucherRequest { Reason = "Documento cargado o generado por error" },
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None);

        Assert.Equal(VoucherStatuses.Revoked, result.Status);
        Assert.False(result.CanSend);
        Assert.Equal("Documento cargado o generado por error", result.RevocationReason);
        Assert.Equal("Operador", result.RevokedByUserName);
        Assert.NotNull(result.RevokedAt);

        var audit = await db.VoucherAuditEntries.SingleAsync(entry => entry.Action == VoucherAuditActions.Revoked);
        Assert.Equal("issuer-1", audit.UserId);
        Assert.Equal("Documento cargado o generado por error", audit.Reason);

        if (status == VoucherStatuses.PendingAuthorization)
        {
            Assert.Equal(VoucherAuthorizationStatuses.Cancelled, result.AuthorizationStatus);
            Assert.Equal("superior-1", audit.AuthorizedBySuperiorUserId);
        }
    }

    // ===== Gate de servicio no resuelto por el operador (auditoria de negocio 2026-06-12, item 2) =====

    [Fact]
    public async Task IssueVoucherAsync_BlocksIssue_WhenAServiceIsNotResolvedByOperator()
    {
        using var db = CreateDbContext();
        GrantIssuePermission(db);
        // Reserva saldada (sin saldo) pero con un hotel todavia "Solicitado": no esta asegurado por
        // el operador. Sin justificar excepcion, el voucher NO debe emitirse.
        var voucher = await SeedDraftVoucherAsync(db, balance: 0m, hotelStatus: "Solicitado");
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest(),
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None));

        Assert.Empty(db.VoucherAuditEntries.Where(entry =>
            entry.Action == VoucherAuditActions.Issued || entry.Action == VoucherAuditActions.ExceptionalIssue));
    }

    [Fact]
    public async Task IssueVoucherAsync_AllowsExceptionalIssue_WhenServiceNotResolvedAndAdminJustifies()
    {
        using var db = CreateDbContext();
        // Hotel sin confirmar + admin que justifica -> emision excepcional auditada.
        var voucher = await SeedDraftVoucherAsync(db, balance: 0m, hotelStatus: "Solicitado");
        var service = CreateService(db);

        var result = await service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest { ExceptionalReason = "Cliente viaja manana, operador confirma por telefono" },
            new OperationActor("admin-1", "Admin", new[] { "Admin" }),
            CancellationToken.None);

        Assert.Equal(VoucherStatuses.Issued, result.Status);
        Assert.True(result.WasExceptionalIssue);

        var audit = await db.VoucherAuditEntries.SingleAsync(entry => entry.Action == VoucherAuditActions.ExceptionalIssue);
        Assert.Equal("admin-1", audit.UserId);
        Assert.Contains("sin confirmar por el operador", audit.Details);
    }

    [Fact]
    public async Task IssueVoucherAsync_RequestsAuthorization_WhenServiceNotResolvedAndSuperiorSelected()
    {
        using var db = CreateDbContext();
        GrantIssuePermission(db);
        GrantSuperiorAuthorizationPermission(db);
        // Hotel sin confirmar, reserva saldada: un vendedor (no admin) pide autorizacion al superior.
        var voucher = await SeedDraftVoucherAsync(db, balance: 0m, hotelStatus: "Solicitado");
        var service = CreateService(db);

        var result = await service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest
            {
                ExceptionalReason = "Operador confirma despues, urgencia del cliente",
                AuthorizedBySuperiorUserId = "superior-1"
            },
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None);

        Assert.Equal(VoucherStatuses.PendingAuthorization, result.Status);
        Assert.Equal(VoucherAuthorizationStatuses.Pending, result.AuthorizationStatus);
        Assert.True(result.WasExceptionalIssue);

        var audit = await db.VoucherAuditEntries.SingleAsync(entry => entry.Action == VoucherAuditActions.AuthorizationRequested);
        Assert.Equal("superior-1", audit.AuthorizedBySuperiorUserId);
    }

    [Fact]
    public async Task IssueVoucherAsync_AllowsNormalIssue_WhenAllServicesResolved()
    {
        using var db = CreateDbContext();
        GrantIssuePermission(db);
        // Reserva saldada + hotel confirmado por el operador: emision normal, sin excepcion.
        var voucher = await SeedDraftVoucherAsync(db, balance: 0m, hotelStatus: "Confirmado");
        var service = CreateService(db);

        var result = await service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest(),
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None);

        Assert.Equal(VoucherStatuses.Issued, result.Status);
        Assert.False(result.WasExceptionalIssue);

        await db.VoucherAuditEntries.SingleAsync(entry => entry.Action == VoucherAuditActions.Issued);
    }

    [Fact]
    public async Task IssueVoucherAsync_BlocksIssue_WhenVoucherIsRevoked()
    {
        using var db = CreateDbContext();
        GrantIssuePermission(db);
        GrantRevokePermission(db);
        var voucher = await SeedDraftVoucherAsync(db, balance: 0m);
        var service = CreateService(db);

        await service.RevokeVoucherAsync(
            voucher.PublicId.ToString(),
            new RevokeVoucherRequest { Reason = "Documento generado por error" },
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.IssueVoucherAsync(
            voucher.PublicId.ToString(),
            new IssueVoucherRequest(),
            new OperationActor("issuer-1", "Operador", new[] { "Ops" }),
            CancellationToken.None));
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AppDbContext(options);
    }

    private static VoucherService CreateService(AppDbContext db) =>
        new(
            db,
            Mock.Of<IOperationalFinanceSettingsService>(),
            Mock.Of<IFileStoragePort>());

    // hotelStatus: cuando se pasa, la reserva nace con UN hotel en ese estado. Sirve para los gates de
    // "servicio no resuelto" (Solicitado = no resuelto; Confirmado = resuelto). null = reserva sin
    // servicios (caso historico: el gate de servicio no aplica, solo el de saldo).
    private static async Task<Voucher> SeedDraftVoucherAsync(AppDbContext db, decimal balance, string? hotelStatus = null)
    {
        var reserva = new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "R-TEST",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            Balance = balance
        };

        if (hotelStatus != null)
        {
            reserva.HotelBookings.Add(new HotelBooking
            {
                Id = 1,
                ReservaId = reserva.Id,
                HotelName = "Hotel test",
                Status = hotelStatus
            });
        }

        var voucher = new Voucher
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            Reserva = reserva,
            Status = VoucherStatuses.Draft,
            Source = VoucherSources.Generated,
            Scope = VoucherScopes.Reservation,
            FileName = "voucher-test.pdf",
            ContentType = "application/pdf",
            IsEnabledForSending = false,
            CreatedByUserId = "creator-1",
            CreatedByUserName = "Creator",
            CreatedAt = DateTime.UtcNow
        };

        db.Reservas.Add(reserva);
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();
        return voucher;
    }

    private static void GrantIssuePermission(AppDbContext db)
    {
        db.RolePermissions.Add(new RolePermission
        {
            RoleName = "Ops",
            Permission = Permissions.VouchersIssue
        });
        db.SaveChanges();
    }

    private static void GrantRevokePermission(AppDbContext db)
    {
        db.RolePermissions.Add(new RolePermission
        {
            RoleName = "Ops",
            Permission = Permissions.VouchersRevoke
        });
        db.SaveChanges();
    }

    private static void GrantSuperiorAuthorizationPermission(AppDbContext db)
    {
        db.Users.Add(new ApplicationUser
        {
            Id = "superior-1",
            UserName = "superior",
            FullName = "Superior",
            IsActive = true
        });
        db.Roles.Add(new IdentityRole
        {
            Id = "role-supervisor",
            Name = "Supervisor",
            NormalizedName = "SUPERVISOR"
        });
        db.UserRoles.Add(new IdentityUserRole<string>
        {
            UserId = "superior-1",
            RoleId = "role-supervisor"
        });
        db.RolePermissions.Add(new RolePermission
        {
            RoleName = "Supervisor",
            Permission = Permissions.VouchersAuthorizeException
        });
        db.SaveChanges();
    }
}
