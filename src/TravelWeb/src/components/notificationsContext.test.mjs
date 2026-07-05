/**
 * Contrato del contexto UNIFICADO de notificaciones (AlertsContext, Tanda 5 2026-07-05).
 *
 * Antes había dos lectores de /notifications (la campanita con su propio fetch + SignalR, y la página con poll sin
 * SignalR) → badge y página se desincronizaban. Ahora todo vive en AlertsContext. Estos tests fijan la LÓGICA PURA
 * del contexto (sin DOM ni red), replicando los helpers del provider. Si la lógica cambia allá, actualizar acá.
 *
 * Cubre:
 *   - unreadCount = largo de la lista (el server ya devuelve solo los VIVOS).
 *   - markAsRead optimista: saca el aviso de la lista al instante.
 *   - markAllAsRead optimista: vacía la lista.
 *   - dedup de SignalR: un aviso que ya está no se duplica al recibirlo por realtime.
 *   - semántica "vivo" (contrato con el backend): ResolvedAt null && !IsRead && !IsDismissed.
 *   - convención de clave de resolución "{RelatedEntityType}:{RelatedEntityId}".
 *
 * Correr: node --test src/components/notificationsContext.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Helpers copiados de AlertsContext.jsx (lógica pura) ─────────────────────

/** unreadCount del contexto: la lista ya son solo los vivos, así que es su largo. */
function unreadCountOf(notifications) {
    return notifications.length;
}

/** markAsRead optimista: saca el aviso de la lista (el POST corre aparte). */
function removeById(notifications, id) {
    return notifications.filter((n) => n.id !== id);
}

/** markAllAsRead optimista: vacía la lista. */
function clearAll() {
    return [];
}

/** Recepción por SignalR: prepende salvo que el id ya esté (evita duplicar poll + realtime). */
function onReceiveNotification(prev, notification) {
    if (notification?.id && prev.some((n) => n.id === notification.id)) {
        return prev;
    }
    return [notification, ...prev];
}

// ─── Contrato con el backend: "vivo" y clave de resolución ───────────────────

/** Un aviso "vivo" (lo que /notifications devuelve): causa vigente y no visto. Espejo del filtro server-side. */
function isLive(n) {
    return n.resolvedAt == null && !n.isRead && !n.isDismissed;
}

/** Clave de resolución por defecto: "{RelatedEntityType}:{RelatedEntityId}" (null si falta alguno). */
function resolutionKeyForEntity(relatedEntityType, relatedEntityId) {
    if (!relatedEntityType || relatedEntityId == null) return null;
    return `${relatedEntityType}:${relatedEntityId}`;
}

// ─── Tests: unreadCount ──────────────────────────────────────────────────────

test("unreadCount: es el largo de la lista de vivos", () => {
    assert.equal(unreadCountOf([]), 0);
    assert.equal(unreadCountOf([{ id: 1 }, { id: 2 }, { id: 3 }]), 3);
});

// ─── Tests: markAsRead optimista ─────────────────────────────────────────────

test("markAsRead optimista: saca el aviso de la lista", () => {
    const notifs = [{ id: 1 }, { id: 2 }, { id: 3 }];
    const next = removeById(notifs, 2);
    assert.deepEqual(next.map((n) => n.id), [1, 3]);
});

test("markAsRead optimista: id inexistente no cambia la lista", () => {
    const notifs = [{ id: 1 }, { id: 2 }];
    const next = removeById(notifs, 99);
    assert.equal(next.length, 2);
});

// ─── Tests: markAllAsRead optimista ──────────────────────────────────────────

test("markAllAsRead optimista: vacía la lista", () => {
    assert.deepEqual(clearAll(), []);
});

// ─── Tests: dedup de SignalR ─────────────────────────────────────────────────

test("SignalR: un aviso nuevo se prepone a la lista", () => {
    const prev = [{ id: 1 }];
    const next = onReceiveNotification(prev, { id: 2 });
    assert.deepEqual(next.map((n) => n.id), [2, 1]);
});

test("SignalR: un aviso con id ya presente NO se duplica", () => {
    const prev = [{ id: 1 }, { id: 2 }];
    const next = onReceiveNotification(prev, { id: 2 });
    // Misma referencia y sin duplicar
    assert.equal(next, prev);
    assert.equal(next.length, 2);
});

test("SignalR: aviso sin id se agrega igual (no podemos deduplicar sin id)", () => {
    const prev = [{ id: 1 }];
    const next = onReceiveNotification(prev, { message: "sin id" });
    assert.equal(next.length, 2);
});

// ─── Tests: contrato "vivo" con el backend ───────────────────────────────────

test("isLive: causa vigente y no visto → vivo", () => {
    assert.equal(isLive({ resolvedAt: null, isRead: false, isDismissed: false }), true);
});

test("isLive: resuelto (causa muerta) → NO vivo aunque no esté leído", () => {
    assert.equal(isLive({ resolvedAt: "2026-07-05T00:00:00Z", isRead: false, isDismissed: false }), false);
});

test("isLive: leído o descartado → NO vivo (marcar visto setea ambos en el backend)", () => {
    assert.equal(isLive({ resolvedAt: null, isRead: true, isDismissed: true }), false);
});

// ─── Tests: convención de clave de resolución ────────────────────────────────

test("resolutionKey: '{tipo}:{id}' cuando ambos existen", () => {
    assert.equal(resolutionKeyForEntity("Invoice", 42), "Invoice:42");
    assert.equal(resolutionKeyForEntity("ReservaUnpaidDeparture", 7), "ReservaUnpaidDeparture:7");
});

test("resolutionKey: null si falta el tipo o el id", () => {
    assert.equal(resolutionKeyForEntity(null, 42), null);
    assert.equal(resolutionKeyForEntity("Invoice", null), null);
    assert.equal(resolutionKeyForEntity("", 1), null);
});
