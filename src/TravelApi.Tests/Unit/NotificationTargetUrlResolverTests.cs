using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-08-19, "descalce devolución-caja"; extendido 2026-08-22) <see cref="NotificationTargetUrlResolver"/>
/// arma el link de la campanita SIN una columna nueva en <c>Notification</c> — deriva la URL de
/// <c>RelatedEntityType</c>/<c>RelatedEntityId</c> (ya persistidos) al leer/enviar el aviso. Estos tests
/// cubren los CUATRO tipos de aviso que hoy resuelven destino (<c>CashLedgerRefundReconciliation</c>,
/// <c>ReservaUnpaidDeparture</c>, <c>Reserva</c>, <c>PartialCreditNoteReviewPending</c> y
/// <c>PartialCreditNotePostingStuck</c> — cinco constantes, cuatro métodos porque
/// <c>ResolveDirectReservaLinksAsync</c> atiende dos tipos con la misma lógica) y el comportamiento "sin
/// destino" para el resto (T-5: nunca un id crudo, y el resto de avisos se comporta EXACTAMENTE igual que
/// hoy).
/// </summary>
public class NotificationTargetUrlResolverTests
{
    private static AppDbContext NewDbContext() =>
        new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"notification-targeturl-tests-{Guid.NewGuid()}")
            .Options);

    [Fact]
    public async Task ResolveManyAsync_CashLedgerRefundReconciliationNotification_ReturnsUrlWithSupplierPublicId()
    {
        await using var ctx = NewDbContext();

        var supplierPublicId = Guid.NewGuid();
        ctx.Suppliers.Add(new Supplier { Id = 1, PublicId = supplierPublicId, Name = "Operador Test" });
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            Id = 5, PublicId = Guid.NewGuid(), ReservaId = 1, CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.AwaitingOperatorRefund, Reason = "Test",
            DraftedAt = DateTime.UtcNow, DraftedByUserId = "vendedor-1", FiscalSnapshot = new FiscalSnapshot(),
        });
        await ctx.SaveChangesAsync();

        var notification = new Notification
        {
            Id = 100,
            UserId = "admin-1",
            Message = "aviso",
            Type = "Warning",
            Priority = "Normal",
            RelatedEntityType = NotificationRelatedEntityTypes.CashLedgerRefundReconciliation,
            RelatedEntityId = 5,
        };

        var resolver = new NotificationTargetUrlResolver(ctx);

        var result = await resolver.ResolveManyAsync(new[] { notification }, CancellationToken.None);

        Assert.True(result.TryGetValue(100, out var targetUrl));
        Assert.Equal($"/suppliers/{supplierPublicId}/account?tab=reembolsos", targetUrl);
        // T-5: nunca el id interno de la cancelacion (5) crudo en la URL.
        Assert.DoesNotContain("/5", targetUrl);
    }

    [Theory]
    [InlineData("ReservaUnpaidDeparture")]
    [InlineData("Reserva")]
    public async Task ResolveManyAsync_DirectReservaNotification_ReturnsReservaUrl(string relatedEntityType)
    {
        // (2026-08-22) ResolveDirectReservaLinksAsync atiende DOS tipos con la misma logica (el
        // RelatedEntityId YA es el Id interno de la Reserva): "sale pronto y debe" del monitor financiero, y
        // "confirmada con cambios" / errores de resolucion de servicio, que comparten el tipo "Reserva".
        await using var ctx = NewDbContext();

        var reservaPublicId = Guid.NewGuid();
        ctx.Reservas.Add(new Reserva
        {
            Id = 7, PublicId = reservaPublicId, NumeroReserva = "F-7", Name = "Test", AdultCount = 1,
        });
        await ctx.SaveChangesAsync();

        var notification = new Notification
        {
            Id = 200,
            UserId = "admin-1",
            Message = "aviso",
            Type = "Warning",
            Priority = "Normal",
            RelatedEntityType = relatedEntityType,
            RelatedEntityId = 7,
        };

        var resolver = new NotificationTargetUrlResolver(ctx);

        var result = await resolver.ResolveManyAsync(new[] { notification }, CancellationToken.None);

        Assert.True(result.TryGetValue(200, out var targetUrl));
        Assert.Equal($"/reservas/{reservaPublicId}", targetUrl);
        // T-5: nunca el id interno de la reserva (7) crudo en la URL.
        Assert.DoesNotContain("/7", targetUrl);
    }

    [Fact]
    public async Task ResolveManyAsync_PartialCreditNoteReviewPendingNotification_ReturnsReservaUrlViaBookingCancellation()
    {
        // El aviso guarda el Id interno de la BookingCancellation (no el de la reserva): el resolver tiene
        // que atravesar BookingCancellation.Reserva para llegar al PublicId al que navega el link.
        await using var ctx = NewDbContext();

        var reservaPublicId = Guid.NewGuid();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, PublicId = reservaPublicId, NumeroReserva = "F-1", Name = "Test", AdultCount = 1,
        });
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            Id = 9, PublicId = Guid.NewGuid(), ReservaId = 1, CustomerId = 1, SupplierId = 1,
            Status = BookingCancellationStatus.ManualReviewPending, Reason = "Test",
            DraftedAt = DateTime.UtcNow, DraftedByUserId = "vendedor-1", FiscalSnapshot = new FiscalSnapshot(),
        });
        await ctx.SaveChangesAsync();

        var notification = new Notification
        {
            Id = 300,
            UserId = "admin-1",
            Message = "aviso",
            Type = "Warning",
            Priority = "Normal",
            RelatedEntityType = NotificationRelatedEntityTypes.PartialCreditNoteReviewPending,
            RelatedEntityId = 9,
        };

        var resolver = new NotificationTargetUrlResolver(ctx);

        var result = await resolver.ResolveManyAsync(new[] { notification }, CancellationToken.None);

        Assert.True(result.TryGetValue(300, out var targetUrl));
        Assert.Equal($"/reservas/{reservaPublicId}", targetUrl);
        // T-5: nunca el id interno de la cancelacion (9) crudo en la URL.
        Assert.DoesNotContain("/9", targetUrl);
    }

    [Fact]
    public async Task ResolveManyAsync_PartialCreditNotePostingStuckNotification_ReturnsReservaUrlViaInvoice()
    {
        // El aviso guarda el Id interno de la Invoice (la NC), no el de la reserva: el resolver atraviesa
        // Invoice.Reserva para llegar al PublicId.
        await using var ctx = NewDbContext();

        var reservaPublicId = Guid.NewGuid();
        ctx.Reservas.Add(new Reserva
        {
            Id = 2, PublicId = reservaPublicId, NumeroReserva = "F-2", Name = "Test", AdultCount = 1,
        });
        ctx.Invoices.Add(new Invoice { Id = 55, ReservaId = 2 });
        await ctx.SaveChangesAsync();

        var notification = new Notification
        {
            Id = 400,
            UserId = "admin-1",
            Message = "aviso",
            Type = "Error",
            Priority = "Normal",
            RelatedEntityType = NotificationRelatedEntityTypes.PartialCreditNotePostingStuck,
            RelatedEntityId = 55,
        };

        var resolver = new NotificationTargetUrlResolver(ctx);

        var result = await resolver.ResolveManyAsync(new[] { notification }, CancellationToken.None);

        Assert.True(result.TryGetValue(400, out var targetUrl));
        Assert.Equal($"/reservas/{reservaPublicId}", targetUrl);
        // T-5: nunca el id interno de la NC (55) crudo en la URL.
        Assert.DoesNotContain("/55", targetUrl);
    }

    [Fact]
    public async Task ResolveManyAsync_PartialCreditNotePostingStuckNotification_InvoiceWithoutReserva_ReturnsNothing()
    {
        // Invoice.ReservaId es nullable: una NC que no esta ligada a ninguna reserva no tiene a donde
        // navegar. El aviso se comporta igual que un tipo no reconocido (sin destino), no un error.
        await using var ctx = NewDbContext();

        ctx.Invoices.Add(new Invoice { Id = 66, ReservaId = null });
        await ctx.SaveChangesAsync();

        var notification = new Notification
        {
            Id = 500,
            UserId = "admin-1",
            Message = "aviso",
            Type = "Error",
            Priority = "Normal",
            RelatedEntityType = NotificationRelatedEntityTypes.PartialCreditNotePostingStuck,
            RelatedEntityId = 66,
        };

        var resolver = new NotificationTargetUrlResolver(ctx);

        var result = await resolver.ResolveManyAsync(new[] { notification }, CancellationToken.None);

        Assert.False(result.ContainsKey(500));
    }

    [Fact]
    public async Task ResolveManyAsync_OtherNotificationType_ReturnsNothing()
    {
        await using var ctx = NewDbContext();

        var notification = new Notification
        {
            Id = 600,
            UserId = "admin-1",
            Message = "aviso",
            Type = "Error",
            Priority = "Normal",
            // Tipo real del sistema (bridge FC1.3 roto) A PROPOSITO sin entrada en el resolver: no hay
            // pantalla en el front hoy para navegar (ver el comentario en
            // PartialCreditNoteBridgeReconciliationJob). Antes este test usaba el tipo "Reserva", pero ese
            // tipo pasó a resolver destino (2026-08-22) — dejaba de probar lo que decía probar.
            RelatedEntityType = "PartialCreditNoteBridgeReconciliationFailed",
            RelatedEntityId = 7,
        };

        var resolver = new NotificationTargetUrlResolver(ctx);

        var result = await resolver.ResolveManyAsync(new[] { notification }, CancellationToken.None);

        Assert.False(result.ContainsKey(600)); // sin destino conocido -> la fila navega igual que hoy (nada).
    }
}
