using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Implementación default de <see cref="INotificationTargetUrlResolver"/> (2026-08-19, extendida
/// 2026-08-22). Reconoce un puñado de tipos de aviso (ver los métodos privados de abajo, uno por tipo);
/// cualquier otro tipo de aviso queda afuera del diccionario devuelto (el caller lo trata como "sin
/// destino", igual que siempre — comportamiento sin cambios para los avisos que no están en la lista).
///
/// <para><b>Cómo arma cada URL</b>: SIEMPRE a partir de un <c>PublicId</c> (GUID), nunca del id interno
/// que trae <c>Notification.RelatedEntityId</c> (T-5 — un id de base cruda no se expone al navegador). Por
/// eso cada tipo hace una consulta corta para traducir "id interno de la entidad que originó el aviso" →
/// "PublicId de la pantalla a la que hay que navegar".</para>
///
/// <para><b>Por qué un método por tipo y no un diccionario genérico</b>: cada tipo de aviso resuelve su
/// URL con una entidad y un join distintos (a veces la entidad relacionada YA es la reserva; a veces hay
/// que atravesar una <c>BookingCancellation</c> o una <c>Invoice</c> para llegar a ella). Forzarlos a un
/// único query genérico solo escondería esa diferencia de negocio detrás de código más difícil de leer.</para>
///
/// <para><b>Deuda conocida, anotada (revisión seguridad 2026-08-19, no bloqueante)</b>: la pantalla destino
/// de <see cref="NotificationRelatedEntityTypes.CashLedgerRefundReconciliation"/>
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

        await ResolveCashLedgerRefundReconciliationAsync(notifications, result, ct);
        await ResolveDirectReservaLinksAsync(notifications, result, ct);
        await ResolvePartialCreditNoteReviewPendingAsync(notifications, result, ct);
        await ResolvePartialCreditNotePostingStuckAsync(notifications, result, ct);

        return result;
    }

    /// <summary>
    /// Descalce caja-vs-devolución de un operador. La entidad relacionada es una <c>BookingCancellation</c>;
    /// el destino es la ficha del Supplier de esa cancelación, en la solapa de reembolsos.
    /// </summary>
    private async Task ResolveCashLedgerRefundReconciliationAsync(
        IReadOnlyList<Notification> notifications, Dictionary<int, string> result, CancellationToken ct)
    {
        var cashLedgerNotifications = notifications
            .Where(n => n.RelatedEntityType == NotificationRelatedEntityTypes.CashLedgerRefundReconciliation
                     && n.RelatedEntityId.HasValue)
            .ToList();

        if (cashLedgerNotifications.Count == 0)
            return;

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
    }

    /// <summary>
    /// (2026-08-22) Avisos donde <c>RelatedEntityId</c> YA es el Id interno de la <c>Reserva</c> misma
    /// ("sale pronto y debe" del monitor financiero, y "confirmada con cambios" / errores de resolución de
    /// servicio que comparten el tipo <see cref="NotificationRelatedEntityTypes.Reserva"/>). Estos son los
    /// más simples: una sola consulta a <c>Reservas</c> para traducir Id interno → PublicId.
    /// </summary>
    private async Task ResolveDirectReservaLinksAsync(
        IReadOnlyList<Notification> notifications, Dictionary<int, string> result, CancellationToken ct)
    {
        var reservaNotifications = notifications
            .Where(n => n.RelatedEntityId.HasValue &&
                        (n.RelatedEntityType == NotificationRelatedEntityTypes.ReservaUnpaidDeparture
                      || n.RelatedEntityType == NotificationRelatedEntityTypes.Reserva))
            .ToList();

        if (reservaNotifications.Count == 0)
            return;

        var reservaIds = reservaNotifications.Select(n => n.RelatedEntityId!.Value).Distinct().ToList();

        var publicIdByReservaId = await _db.Reservas
            .AsNoTracking()
            .Where(r => reservaIds.Contains(r.Id))
            .Select(r => new { r.Id, r.PublicId })
            .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct);

        foreach (var notification in reservaNotifications)
        {
            if (publicIdByReservaId.TryGetValue(notification.RelatedEntityId!.Value, out var reservaPublicId))
            {
                result[notification.Id] = $"/reservas/{reservaPublicId}";
            }
        }
    }

    /// <summary>
    /// (2026-08-22) Cancelación parcial trabada en revisión manual. <c>RelatedEntityId</c> es el Id interno
    /// de la <c>BookingCancellation</c>; el mensaje le pide al admin "entrar y confirmar la devolución", así
    /// que el destino es la reserva dueña de esa cancelación (toda <c>BookingCancellation</c> tiene reserva
    /// obligatoria — no hace falta contemplar el caso sin reserva).
    /// </summary>
    private async Task ResolvePartialCreditNoteReviewPendingAsync(
        IReadOnlyList<Notification> notifications, Dictionary<int, string> result, CancellationToken ct)
    {
        var reviewPendingNotifications = notifications
            .Where(n => n.RelatedEntityType == NotificationRelatedEntityTypes.PartialCreditNoteReviewPending
                     && n.RelatedEntityId.HasValue)
            .ToList();

        if (reviewPendingNotifications.Count == 0)
            return;

        var bookingCancellationIds = reviewPendingNotifications
            .Select(n => n.RelatedEntityId!.Value)
            .Distinct()
            .ToList();

        var reservaPublicIdByBookingCancellationId = await _db.BookingCancellations
            .AsNoTracking()
            .Where(bc => bookingCancellationIds.Contains(bc.Id))
            .Select(bc => new { bc.Id, bc.Reserva.PublicId })
            .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct);

        foreach (var notification in reviewPendingNotifications)
        {
            if (reservaPublicIdByBookingCancellationId.TryGetValue(notification.RelatedEntityId!.Value, out var reservaPublicId))
            {
                result[notification.Id] = $"/reservas/{reservaPublicId}";
            }
        }
    }

    /// <summary>
    /// (2026-08-22) Nota de crédito parcial trabada sin confirmación de ARCA. <c>RelatedEntityId</c> es el
    /// Id interno de la <c>Invoice</c> (la NC); a diferencia de <c>BookingCancellation</c>, una <c>Invoice</c>
    /// puede no tener reserva asociada (<c>Invoice.ReservaId</c> es nullable) — en ese caso el aviso queda
    /// sin destino, igual que cualquier tipo no reconocido.
    /// </summary>
    private async Task ResolvePartialCreditNotePostingStuckAsync(
        IReadOnlyList<Notification> notifications, Dictionary<int, string> result, CancellationToken ct)
    {
        var postingStuckNotifications = notifications
            .Where(n => n.RelatedEntityType == NotificationRelatedEntityTypes.PartialCreditNotePostingStuck
                     && n.RelatedEntityId.HasValue)
            .ToList();

        if (postingStuckNotifications.Count == 0)
            return;

        var creditNoteIds = postingStuckNotifications
            .Select(n => n.RelatedEntityId!.Value)
            .Distinct()
            .ToList();

        var reservaPublicIdByCreditNoteId = await _db.Invoices
            .AsNoTracking()
            .Where(inv => creditNoteIds.Contains(inv.Id) && inv.ReservaId.HasValue)
            .Select(inv => new { inv.Id, inv.Reserva!.PublicId })
            .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct);

        foreach (var notification in postingStuckNotifications)
        {
            if (reservaPublicIdByCreditNoteId.TryGetValue(notification.RelatedEntityId!.Value, out var reservaPublicId))
            {
                result[notification.Id] = $"/reservas/{reservaPublicId}";
            }
        }
    }
}
