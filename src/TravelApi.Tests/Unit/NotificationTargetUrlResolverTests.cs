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
/// (2026-08-19, "descalce devolución-caja") <see cref="NotificationTargetUrlResolver"/> arma el link de la
/// campanita SIN una columna nueva en <c>Notification</c> — deriva la URL de <c>RelatedEntityType</c>/
/// <c>RelatedEntityId</c> (ya persistidos) al leer/enviar el aviso. Estos tests cubren el unico tipo de
/// aviso que hoy resuelve destino (CashLedgerRefundReconciliation) y el comportamiento "sin destino" para
/// el resto (T-5: nunca un id crudo, y el resto de avisos se comporta EXACTAMENTE igual que hoy).
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

    [Fact]
    public async Task ResolveManyAsync_OtherNotificationType_ReturnsNothing()
    {
        await using var ctx = NewDbContext();

        var notification = new Notification
        {
            Id = 200,
            UserId = "admin-1",
            Message = "confirmada con cambios",
            Type = "Warning",
            Priority = "Normal",
            RelatedEntityType = NotificationRelatedEntityTypes.Reserva,
            RelatedEntityId = 7,
        };

        var resolver = new NotificationTargetUrlResolver(ctx);

        var result = await resolver.ResolveManyAsync(new[] { notification }, CancellationToken.None);

        Assert.False(result.ContainsKey(200)); // sin destino conocido -> la fila navega igual que hoy (nada).
    }
}
