/**
 * Tests de lógica pura para el bug fix del 2026-07-03:
 *
 * El banner "Se editaron precios o costos de esta reserva..." (ReservaDetailPage.jsx)
 * y el badge ámbar "Con cambios" (ReservaHeader.jsx) se renderizaban mirando SOLO
 * `reserva.hasUnacknowledgedChanges`, sin mirar el estado de la reserva.
 *
 * El backend puede dejar ese flag en true por error en reservas Anuladas
 * (status "Cancelled") o Esperando reembolso (status "PendingOperatorRefund")
 * — bug de backend que se arregla aparte. Mientras tanto, el frente NO debe
 * mostrar un cartel que invite a "confirmar un cambio" sobre un viaje que ya
 * quedó sin efecto.
 *
 * Fix: además del flag, se exige que el estado sea "vivo"
 * (InManagement/Confirmed/Traveling) — mismo criterio que usa la campanita
 * de avisos del backend. Ver helper real `isReservaEnEstadoVivo` en
 * ReservaStatusBadge.jsx (no se puede importar el .jsx directo porque el
 * test runner de node no transpila JSX, así que acá replicamos el mismo
 * conjunto de estados para verificar el comportamiento).
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/bannerConCambiosEstadoVivo.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica del helper isReservaEnEstadoVivo (ver ReservaStatusBadge.jsx) ────

const LIVE_RESERVA_STATUSES = new Set(['InManagement', 'Confirmed', 'Traveling']);

function isReservaEnEstadoVivo(status) {
    return LIVE_RESERVA_STATUSES.has(status);
}

// ─── Réplica de la condición que decide si se muestra el banner/badge ────────
// (misma condición usada en ReservaDetailPage.jsx y ReservaHeader.jsx)

function debeMostrarCartelCambios(reserva) {
    return Boolean(reserva.hasUnacknowledgedChanges) && isReservaEnEstadoVivo(reserva.status);
}

test("isReservaEnEstadoVivo: InManagement, Confirmed y Traveling son estados vivos", () => {
    assert.equal(isReservaEnEstadoVivo("InManagement"), true);
    assert.equal(isReservaEnEstadoVivo("Confirmed"), true);
    assert.equal(isReservaEnEstadoVivo("Traveling"), true);
});

test("isReservaEnEstadoVivo: estados de borrador NO son vivos", () => {
    assert.equal(isReservaEnEstadoVivo("Quotation"), false);
    assert.equal(isReservaEnEstadoVivo("Budget"), false);
});

test("isReservaEnEstadoVivo: estados terminales NO son vivos", () => {
    assert.equal(isReservaEnEstadoVivo("Closed"), false);
    assert.equal(isReservaEnEstadoVivo("Lost"), false);
    assert.equal(isReservaEnEstadoVivo("Cancelled"), false);
    assert.equal(isReservaEnEstadoVivo("PendingOperatorRefund"), false);
    assert.equal(isReservaEnEstadoVivo("Archived"), false);
});

test("isReservaEnEstadoVivo: status desconocido o vacío no revienta y da false", () => {
    assert.equal(isReservaEnEstadoVivo(undefined), false);
    assert.equal(isReservaEnEstadoVivo(null), false);
    assert.equal(isReservaEnEstadoVivo("EstadoQueNoExiste"), false);
});

test("Bug 2026-07-03: reserva Anulada con flag en true (bug backend) NO muestra el cartel", () => {
    const reserva = { status: "Cancelled", hasUnacknowledgedChanges: true };
    assert.equal(debeMostrarCartelCambios(reserva), false);
});

test("Bug 2026-07-03: reserva Esperando reembolso con flag en true (bug backend) NO muestra el cartel", () => {
    const reserva = { status: "PendingOperatorRefund", hasUnacknowledgedChanges: true };
    assert.equal(debeMostrarCartelCambios(reserva), false);
});

test("Reserva viva (InManagement) con flag en true SÍ muestra el cartel", () => {
    const reserva = { status: "InManagement", hasUnacknowledgedChanges: true };
    assert.equal(debeMostrarCartelCambios(reserva), true);
});

test("Reserva viva (Confirmed) con flag en true SÍ muestra el cartel", () => {
    const reserva = { status: "Confirmed", hasUnacknowledgedChanges: true };
    assert.equal(debeMostrarCartelCambios(reserva), true);
});

test("Reserva viva (Traveling) con flag en true SÍ muestra el cartel", () => {
    const reserva = { status: "Traveling", hasUnacknowledgedChanges: true };
    assert.equal(debeMostrarCartelCambios(reserva), true);
});

test("Reserva viva con flag en false NO muestra el cartel (comportamiento sin cambios)", () => {
    const reserva = { status: "Confirmed", hasUnacknowledgedChanges: false };
    assert.equal(debeMostrarCartelCambios(reserva), false);
});

test("Reserva en borrador (Budget) con flag en true NO muestra el cartel", () => {
    // No debería pasar en la práctica (los cambios de precio solo se marcan en reservas
    // vivas), pero cubrimos el caso por las dudas.
    const reserva = { status: "Budget", hasUnacknowledgedChanges: true };
    assert.equal(debeMostrarCartelCambios(reserva), false);
});

test("Reserva Cerrada (Closed) con flag en true NO muestra el cartel", () => {
    const reserva = { status: "Closed", hasUnacknowledgedChanges: true };
    assert.equal(debeMostrarCartelCambios(reserva), false);
});
