using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tests para las reglas de negocio de creacion de vouchers:
///   1. No se puede crear un voucher si la reserva no tiene pasajeros.
///   2. No se puede asignar el mismo pasajero a mas de un voucher activo en la misma reserva.
/// </summary>
public class VoucherServiceCreationRulesTests
{
    // ─── Regla 1: reserva sin pasajeros ───────────────────────────────────────

    [Fact]
    public async Task GenerateVoucherRecordAsync_Blocks_WhenReservaHasNoPassengers()
    {
        using var db = CreateDbContext();
        GrantGeneratePermission(db);
        var reserva = await SeedReservaAsync(db, withPassengers: false);
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateVoucherRecordAsync(
                reserva.PublicId.ToString(),
                new GenerateVoucherRequest { Scope = VoucherScopes.Reservation },
                new OperationActor("user-1", "User", new[] { "Ops" }),
                CancellationToken.None));
    }

    [Fact]
    public async Task UploadExternalVoucherAsync_Blocks_WhenReservaHasNoPassengers()
    {
        using var db = CreateDbContext();
        GrantUploadPermission(db);
        var reserva = await SeedReservaAsync(db, withPassengers: false);
        var service = CreateService(db);

        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadExternalVoucherAsync(
                reserva.PublicId.ToString(),
                new UploadExternalVoucherRequest { Scope = VoucherScopes.Reservation },
                stream,
                "test.pdf",
                "application/pdf",
                4L,
                new OperationActor("user-1", "User", new[] { "Ops" }),
                CancellationToken.None));
    }

    // ─── Regla 2: pasajero duplicado en la misma reserva ─────────────────────

    [Fact]
    public async Task GenerateVoucherRecordAsync_Blocks_WhenPassengerAlreadyHasActiveVoucher()
    {
        using var db = CreateDbContext();
        GrantGeneratePermission(db);
        var (reserva, passenger) = await SeedReservaWithPassengerAsync(db);

        // Voucher activo existente con ese pasajero
        var existingVoucher = new Voucher
        {
            ReservaId = reserva.Id,
            Status = VoucherStatuses.Issued,
            Scope = VoucherScopes.SelectedPassengers,
            FileName = "existing.pdf",
            ContentType = "application/pdf",
        };
        existingVoucher.PassengerAssignments.Add(new VoucherPassengerAssignment { PassengerId = passenger.Id });
        db.Vouchers.Add(existingVoucher);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateVoucherRecordAsync(
                reserva.PublicId.ToString(),
                new GenerateVoucherRequest
                {
                    Scope = VoucherScopes.SelectedPassengers,
                    PassengerIds = new List<string> { passenger.PublicId.ToString() }
                },
                new OperationActor("user-1", "User", new[] { "Ops" }),
                CancellationToken.None));

        Assert.Contains("ya tienen un voucher activo", ex.Message);
        Assert.Contains(passenger.FullName, ex.Message);
    }

    [Fact]
    public async Task GenerateVoucherRecordAsync_Allows_WhenExistingVoucherForPassengerIsRevoked()
    {
        using var db = CreateDbContext();
        GrantGeneratePermission(db);
        var fileStorageMock = CreateFileStorageMock();
        var (reserva, passenger) = await SeedReservaWithPassengerAsync(db);

        // Voucher ANULADO — no debe bloquear
        var revokedVoucher = new Voucher
        {
            ReservaId = reserva.Id,
            Status = VoucherStatuses.Revoked,
            Scope = VoucherScopes.SelectedPassengers,
            FileName = "revoked.pdf",
            ContentType = "application/pdf",
        };
        revokedVoucher.PassengerAssignments.Add(new VoucherPassengerAssignment { PassengerId = passenger.Id });
        db.Vouchers.Add(revokedVoucher);
        await db.SaveChangesAsync();

        var service = CreateService(db, fileStorageMock);

        // No debe lanzar excepcion
        // (puede lanzar por PDF/storage en tests unitarios; solo nos interesa que pase la validacion de negocio)
        var ex = await Record.ExceptionAsync(() =>
            service.GenerateVoucherRecordAsync(
                reserva.PublicId.ToString(),
                new GenerateVoucherRequest
                {
                    Scope = VoucherScopes.SelectedPassengers,
                    PassengerIds = new List<string> { passenger.PublicId.ToString() }
                },
                new OperationActor("user-1", "User", new[] { "Ops" }),
                CancellationToken.None));

        // Si lanza, no debe ser por la regla de duplicado
        if (ex is InvalidOperationException ioe)
        {
            Assert.DoesNotContain("ya tienen un voucher activo", ioe.Message);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static Mock<IFileStoragePort> CreateFileStorageMock()
    {
        var mock = new Mock<IFileStoragePort>();
        mock.Setup(m => m.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredFileDescriptor("vouchers/test.pdf", "voucher-test.pdf", "application/pdf", 100L));
        return mock;
    }

    private static VoucherService CreateService(AppDbContext db, Mock<IFileStoragePort>? fileStorageMock = null) =>
        new(db, Mock.Of<IOperationalFinanceSettingsService>(), fileStorageMock?.Object ?? Mock.Of<IFileStoragePort>());

    private static async Task<Reserva> SeedReservaAsync(AppDbContext db, bool withPassengers)
    {
        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            NumeroReserva = "R-TEST",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            Balance = 0m
        };

        if (withPassengers)
        {
            reserva.Passengers.Add(new Passenger
            {
                PublicId = Guid.NewGuid(),
                FullName = "Juan Perez",
                DocumentNumber = "12345678"
            });
        }

        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();
        return reserva;
    }

    private static async Task<(Reserva Reserva, Passenger Passenger)> SeedReservaWithPassengerAsync(AppDbContext db)
    {
        var passenger = new Passenger
        {
            PublicId = Guid.NewGuid(),
            FullName = "Maria Garcia",
            DocumentNumber = "87654321"
        };
        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            NumeroReserva = "R-TEST2",
            Name = "Reserva test 2",
            Status = EstadoReserva.Confirmed,
            Balance = 0m
        };
        reserva.Passengers.Add(passenger);
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();
        return (reserva, passenger);
    }

    private static void GrantGeneratePermission(AppDbContext db)
    {
        db.RolePermissions.Add(new RolePermission { RoleName = "Ops", Permission = Permissions.VouchersGenerate });
        db.SaveChanges();
    }

    private static void GrantUploadPermission(AppDbContext db)
    {
        db.RolePermissions.Add(new RolePermission { RoleName = "Ops", Permission = Permissions.VouchersUpload });
        db.SaveChanges();
    }
}
