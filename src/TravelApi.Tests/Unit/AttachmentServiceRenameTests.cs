using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tests del renombre de adjuntos (PATCH api/attachments/{id}).
/// El renombre es un cambio de etiqueta: NO toca MinIO ni el ContentType, y preserva
/// la extension original del archivo.
/// </summary>
public class AttachmentServiceRenameTests
{
    // ─── Validacion: nombre vacio ─────────────────────────────────────────────

    [Fact]
    public async Task RenameAttachmentAsync_Blocks_WhenFileNameIsEmpty()
    {
        using var db = CreateDbContext();
        var attachment = await SeedAttachmentAsync(db, "documento.pdf");
        var (service, _) = CreateService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenameAttachmentAsync(attachment.PublicId.ToString(), "   ", "tester", CancellationToken.None));
    }

    // ─── Validacion: longitud maxima ──────────────────────────────────────────

    [Fact]
    public async Task RenameAttachmentAsync_Blocks_WhenFileNameTooLong()
    {
        using var db = CreateDbContext();
        var attachment = await SeedAttachmentAsync(db, "documento.pdf");
        var (service, _) = CreateService(db);

        var tooLong = new string('a', 250) + ".pdf";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenameAttachmentAsync(attachment.PublicId.ToString(), tooLong, "tester", CancellationToken.None));
    }

    // ─── No encontrado ────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameAttachmentAsync_Throws_WhenAttachmentNotFound()
    {
        using var db = CreateDbContext();
        var (service, _) = CreateService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.RenameAttachmentAsync("999", "nuevo.pdf", "tester", CancellationToken.None));
    }

    // ─── Camino feliz: actualiza el FileName y audita ─────────────────────────

    [Fact]
    public async Task RenameAttachmentAsync_UpdatesFileName_AndAudits()
    {
        using var db = CreateDbContext();
        var attachment = await SeedAttachmentAsync(db, "viejo.pdf");
        var (service, auditMock) = CreateService(db);

        var result = await service.RenameAttachmentAsync(
            attachment.PublicId.ToString(),
            "contrato firmado.pdf",
            "tester",
            CancellationToken.None);

        Assert.Equal("contrato firmado.pdf", result.FileName);

        var reloaded = await db.Set<ReservaAttachment>().FirstAsync(a => a.Id == attachment.Id);
        Assert.Equal("contrato firmado.pdf", reloaded.FileName);

        auditMock.Verify(a => a.LogBusinessEventAsync(
            "Rename",
            nameof(ReservaAttachment),
            attachment.Id.ToString(),
            It.IsAny<string>(),
            "tester",
            "tester",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Preserva la extension original aunque el usuario mande otra ──────────

    [Fact]
    public async Task RenameAttachmentAsync_PreservesOriginalExtension()
    {
        using var db = CreateDbContext();
        var attachment = await SeedAttachmentAsync(db, "factura.pdf");
        var (service, _) = CreateService(db);

        // El usuario intenta cambiar la extension a .txt; debe conservarse .pdf.
        var result = await service.RenameAttachmentAsync(
            attachment.PublicId.ToString(),
            "factura-2026.txt",
            "tester",
            CancellationToken.None);

        Assert.Equal("factura-2026.pdf", result.FileName);
    }

    // ─── Sin cambio efectivo: no audita ───────────────────────────────────────

    [Fact]
    public async Task RenameAttachmentAsync_NoOp_WhenNameUnchanged()
    {
        using var db = CreateDbContext();
        var attachment = await SeedAttachmentAsync(db, "igual.pdf");
        var (service, auditMock) = CreateService(db);

        var result = await service.RenameAttachmentAsync(
            attachment.PublicId.ToString(),
            "igual.pdf",
            "tester",
            CancellationToken.None);

        Assert.Equal("igual.pdf", result.FileName);
        auditMock.Verify(a => a.LogBusinessEventAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
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

    private static (AttachmentService Service, Mock<IAuditService> AuditMock) CreateService(AppDbContext db)
    {
        var attachmentRepo = new Repository<ReservaAttachment>(db);
        var reservaRepo = new Repository<Reserva>(db);
        var minioMock = new Mock<IMinioClient>();
        var auditMock = new Mock<IAuditService>();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AttachmentService(
            attachmentRepo,
            reservaRepo,
            minioMock.Object,
            config,
            NullLogger<AttachmentService>.Instance,
            auditMock.Object);

        return (service, auditMock);
    }

    private static async Task<ReservaAttachment> SeedAttachmentAsync(AppDbContext db, string fileName)
    {
        var reserva = new Reserva
        {
            PublicId = Guid.NewGuid(),
            NumeroReserva = "R-ATT",
            Name = "Reserva attach",
            Status = EstadoReserva.Confirmed,
            Balance = 0m
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var attachment = new ReservaAttachment
        {
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            FileName = fileName,
            StoredFileName = "2026/abc.pdf",
            ContentType = "application/pdf",
            FileSize = 1234,
            UploadedBy = "uploader",
            UploadedAt = DateTime.UtcNow
        };
        db.Set<ReservaAttachment>().Add(attachment);
        await db.SaveChangesAsync();
        return attachment;
    }
}
