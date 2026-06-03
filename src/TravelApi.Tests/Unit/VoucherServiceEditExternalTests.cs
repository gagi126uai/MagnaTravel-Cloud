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
/// Tests de la edicion de vouchers externos (PATCH api/vouchers/{id}/external):
///   1. Solo aplica a vouchers externos (Source = External).
///   2. No se puede editar un voucher anulado (Revoked).
///   3. El origen (ExternalOrigin) es obligatorio.
///   4. Camino feliz: actualiza el origen y, si viene archivo, lo reemplaza.
/// </summary>
public class VoucherServiceEditExternalTests
{
    // ─── Guarda 1: solo vouchers externos ─────────────────────────────────────

    [Fact]
    public async Task EditExternalVoucherAsync_Blocks_WhenVoucherIsNotExternal()
    {
        using var db = CreateDbContext();
        GrantUploadPermission(db);
        var (reserva, _) = await SeedReservaWithPassengerAsync(db);
        var voucher = await SeedVoucherAsync(db, reserva.Id, VoucherSources.Generated, VoucherStatuses.Issued);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EditExternalVoucherAsync(
                voucher.PublicId.ToString(),
                new EditExternalVoucherRequest { ExternalOrigin = "Operador nuevo" },
                stream: null,
                fileName: null,
                contentType: null,
                fileSize: 0L,
                Ops(),
                CancellationToken.None));

        Assert.Contains("externos", ex.Message);
    }

    // ─── Guarda 2: estado no editable (anulado) ───────────────────────────────

    [Fact]
    public async Task EditExternalVoucherAsync_Blocks_WhenVoucherIsRevoked()
    {
        using var db = CreateDbContext();
        GrantUploadPermission(db);
        var (reserva, _) = await SeedReservaWithPassengerAsync(db);
        var voucher = await SeedVoucherAsync(db, reserva.Id, VoucherSources.External, VoucherStatuses.Revoked);
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EditExternalVoucherAsync(
                voucher.PublicId.ToString(),
                new EditExternalVoucherRequest { ExternalOrigin = "Operador nuevo" },
                stream: null,
                fileName: null,
                contentType: null,
                fileSize: 0L,
                Ops(),
                CancellationToken.None));

        Assert.Contains("anulado", ex.Message);
    }

    // ─── Guarda 3: origen obligatorio ─────────────────────────────────────────

    [Fact]
    public async Task EditExternalVoucherAsync_Blocks_WhenExternalOriginIsEmpty()
    {
        using var db = CreateDbContext();
        GrantUploadPermission(db);
        var (reserva, _) = await SeedReservaWithPassengerAsync(db);
        var voucher = await SeedVoucherAsync(db, reserva.Id, VoucherSources.External, VoucherStatuses.UploadedExternal);
        var service = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.EditExternalVoucherAsync(
                voucher.PublicId.ToString(),
                new EditExternalVoucherRequest { ExternalOrigin = "   " },
                stream: null,
                fileName: null,
                contentType: null,
                fileSize: 0L,
                Ops(),
                CancellationToken.None));
    }

    // ─── Guarda permisos: sin VouchersUpload no puede ─────────────────────────

    [Fact]
    public async Task EditExternalVoucherAsync_Blocks_WhenActorLacksUploadPermission()
    {
        using var db = CreateDbContext();
        // NO se otorga el permiso VouchersUpload al rol.
        var (reserva, _) = await SeedReservaWithPassengerAsync(db);
        var voucher = await SeedVoucherAsync(db, reserva.Id, VoucherSources.External, VoucherStatuses.UploadedExternal);
        var service = CreateService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.EditExternalVoucherAsync(
                voucher.PublicId.ToString(),
                new EditExternalVoucherRequest { ExternalOrigin = "Operador nuevo" },
                stream: null,
                fileName: null,
                contentType: null,
                fileSize: 0L,
                Ops(),
                CancellationToken.None));
    }

    // ─── Camino feliz: solo origen (sin archivo) ──────────────────────────────

    [Fact]
    public async Task EditExternalVoucherAsync_UpdatesOrigin_WithoutFile()
    {
        using var db = CreateDbContext();
        GrantUploadPermission(db);
        var (reserva, _) = await SeedReservaWithPassengerAsync(db);
        var voucher = await SeedVoucherAsync(db, reserva.Id, VoucherSources.External, VoucherStatuses.UploadedExternal);
        var fileStorageMock = CreateFileStorageMock();
        var service = CreateService(db, fileStorageMock);

        var result = await service.EditExternalVoucherAsync(
            voucher.PublicId.ToString(),
            new EditExternalVoucherRequest { ExternalOrigin = "  Operador actualizado  " },
            stream: null,
            fileName: null,
            contentType: null,
            fileSize: 0L,
            Ops(),
            CancellationToken.None);

        Assert.Equal("Operador actualizado", result.ExternalOrigin);

        // Sin archivo => no se toca el storage.
        fileStorageMock.Verify(m => m.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        fileStorageMock.Verify(m => m.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // Se registro la auditoria de edicion.
        var audit = await db.VoucherAuditEntries.FirstOrDefaultAsync(a => a.Action == VoucherAuditActions.ExternalEdited);
        Assert.NotNull(audit);
    }

    // ─── Camino feliz: reemplaza archivo ──────────────────────────────────────

    [Fact]
    public async Task EditExternalVoucherAsync_ReplacesFile_AndDeletesPrevious()
    {
        using var db = CreateDbContext();
        GrantUploadPermission(db);
        var (reserva, _) = await SeedReservaWithPassengerAsync(db);
        var voucher = await SeedVoucherAsync(db, reserva.Id, VoucherSources.External, VoucherStatuses.UploadedExternal);
        voucher.StoredFileName = "vouchers/external/2026/old.pdf";
        await db.SaveChangesAsync();

        var fileStorageMock = CreateFileStorageMock();
        var service = CreateService(db, fileStorageMock);

        // %PDF magic bytes para pasar la validacion de tipo/firma del upload.
        using var stream = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var result = await service.EditExternalVoucherAsync(
            voucher.PublicId.ToString(),
            new EditExternalVoucherRequest { ExternalOrigin = "Operador con archivo" },
            stream,
            "nuevo.pdf",
            "application/pdf",
            4L,
            Ops(),
            CancellationToken.None);

        Assert.Equal("Operador con archivo", result.ExternalOrigin);

        // Se guardo el archivo nuevo y se borro el anterior (best-effort).
        fileStorageMock.Verify(m => m.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fileStorageMock.Verify(m => m.DeleteAsync("vouchers/external/2026/old.pdf", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static OperationActor Ops() => new("user-1", "User", new[] { "Ops" });

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
            .ReturnsAsync(new StoredFileDescriptor("vouchers/external/2026/new.pdf", "nuevo.pdf", "application/pdf", 4L));
        return mock;
    }

    private static VoucherService CreateService(AppDbContext db, Mock<IFileStoragePort>? fileStorageMock = null) =>
        new(db, Mock.Of<IOperationalFinanceSettingsService>(), fileStorageMock?.Object ?? Mock.Of<IFileStoragePort>());

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
            NumeroReserva = "R-EDIT",
            Name = "Reserva edit",
            Status = EstadoReserva.Confirmed,
            Balance = 0m
        };
        reserva.Passengers.Add(passenger);
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();
        return (reserva, passenger);
    }

    private static async Task<Voucher> SeedVoucherAsync(AppDbContext db, int reservaId, string source, string status)
    {
        var voucher = new Voucher
        {
            PublicId = Guid.NewGuid(),
            ReservaId = reservaId,
            Source = source,
            Status = status,
            Scope = VoucherScopes.Reservation,
            FileName = "actual.pdf",
            StoredFileName = "vouchers/external/2026/actual.pdf",
            ContentType = "application/pdf",
            FileSize = 100L,
            ExternalOrigin = source == VoucherSources.External ? "Operador original" : null
        };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();
        return voucher;
    }

    private static void GrantUploadPermission(AppDbContext db)
    {
        db.RolePermissions.Add(new RolePermission { RoleName = "Ops", Permission = Permissions.VouchersUpload });
        db.SaveChanges();
    }
}
