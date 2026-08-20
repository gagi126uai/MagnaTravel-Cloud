using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Implementación default de <see cref="INotificationTargetUrlResolver"/> (2026-08-19). Hoy SOLO reconoce
/// el aviso de <see cref="NotificationRelatedEntityTypes.CashLedgerRefundReconciliation"/> (el descalce
/// caja-vs-devolución de <c>CashLedgerRefundReconciliationJob</c>); cualquier otro tipo de aviso queda
/// afuera del diccionario devuelto (el caller lo trata como "sin destino", igual que siempre).
///
/// <para><b>Cómo arma la URL</b>: <c>Notification.RelatedEntityId</c> para este tipo de aviso es el Id
/// INTERNO de la <c>BookingCancellation</c> divergente (nunca se expone tal cual — T-5). Acá se resuelve el
/// Supplier de esa cancelación y se arma <c>/suppliers/{supplierPublicId}/account?tab=reembolsos</c>, que
/// SÍ es seguro de exponer (solo lleva el PublicId).</para>
///
/// <para><b>Deuda conocida, anotada (revisión seguridad 2026-08-19, no bloqueante)</b>: la pantalla destino
/// (<c>GET /suppliers/{id}/account</c>) exige <c>proveedores.view</c>, que NO forma parte del módulo
/// "Tesoreria" seedeado (<c>Permissions.cs</c>) — un rol armado SOLO con
/// <c>tesoreria.supplier_payments</c> (sin <c>proveedores.view</c>) recibe un aviso con un link que le da
/// 403 al hacer click. No es una fuga de datos (falla cerrado, el permiso real lo sigue frenando en el
/// servidor), pero es una mala experiencia. No se resuelve acá porque el fix correcto (¿el rol Tesorería
/// debería incluir <c>proveedores.view</c> por default? ¿el resolver debería chequear el permiso del
/// destinatario antes de armar el link?) es una decisión de producto/permisos, no un ajuste de este
/// resolver. Queda para cuando se defina esa política.</para>
/// </summary>
public sealed class NotificationTargetUrlResolver : INotificationTargetUrlResolver
{
    private readonly AppDbContext _db;

    public NotificationTargetUrlResolver(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<int, string>> ResolveManyAsync(
        IReadOnlyList<Notification> notifications, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();

        var cashLedgerNotifications = notifications
            .Where(n => n.RelatedEntityType == NotificationRelatedEntityTypes.CashLedgerRefundReconciliation
                     && n.RelatedEntityId.HasValue)
            .ToList();

        if (cashLedgerNotifications.Count == 0)
            return result;

        var bookingCancellationIds = cashLedgerNotifications
            .Select(n => n.RelatedEntityId!.Value)
            .Distinct()
            .ToList();

        var supplierPublicIdByBookingCancellationId = await _db.BookingCancellations
            .AsNoTracking()
            .Where(bc => bookingCancellationIds.Contains(bc.Id))
            .Select(bc => new { bc.Id, bc.Supplier.PublicId })
            .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct);

        foreach (var notification in cashLedgerNotifications)
        {
            if (supplierPublicIdByBookingCancellationId.TryGetValue(notification.RelatedEntityId!.Value, out var supplierPublicId))
            {
                result[notification.Id] = $"/suppliers/{supplierPublicId}/account?tab=reembolsos";
            }
        }

        return result;
    }
}
