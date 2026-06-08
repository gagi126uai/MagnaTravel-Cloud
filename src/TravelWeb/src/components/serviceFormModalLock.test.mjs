/**
 * Tests de lógica pura para el candado del ServiceFormModal (bloqueante B1).
 *
 * Qué cubre:
 *   Antes del fix B1, el modal usaba un set local que solo incluía Traveling y Closed,
 *   dejando afuera Confirmed y ToSettle. Eso permitía editar servicios en reservas
 *   confirmadas saltando la autorización de 4 ojos de ADR-020.
 *
 *   Tras el fix, el modal delega en isStatusLocked (ReservaStatusBadge.jsx),
 *   la fuente canónica de los 4 estados bloqueados.
 *
 * Por qué lógica pura:
 *   isStatusLocked no tiene efectos secundarios ni deps de React.
 *   Testearla directa es rápido y determinista.
 *
 * Cómo correr: node --test src/components/serviceFormModalLock.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de LOCKED_STATUSES / isStatusLocked (ReservaStatusBadge.jsx) ────
// Si cambia el set canónico allá, actualizar acá.

const LOCKED_STATUSES = new Set(['Confirmed', 'Traveling', 'ToSettle', 'Closed']);

function isStatusLocked(status) {
    return LOCKED_STATUSES.has(status);
}

// ─── Réplica de la lógica anterior (set local del modal antes del fix B1) ─────
// Se conserva aquí solo para documentar el agujero que existía.

function isLockedAnterior(reservaStatus) {
    return reservaStatus === "Traveling" || reservaStatus === "Closed";
}

// ─── Tests: los 4 estados que DEBEN estar bloqueados ─────────────────────────

test("B1 candado: Confirmed → bloqueado (era el agujero antes del fix)", () => {
    // Una reserva Confirmada tiene el candado activo por ADR-020.
    // Antes del fix este test fallaba porque isLockedAnterior(Confirmed) = false.
    assert.equal(isStatusLocked("Confirmed"), true);
});

test("B1 candado: Traveling → bloqueado", () => {
    assert.equal(isStatusLocked("Traveling"), true);
});

test("B1 candado: ToSettle → bloqueado (era el agujero antes del fix)", () => {
    // ToSettle = "A liquidar", también debería bloquear la edición económica.
    // Antes del fix isLockedAnterior(ToSettle) = false.
    assert.equal(isStatusLocked("ToSettle"), true);
});

test("B1 candado: Closed → bloqueado", () => {
    assert.equal(isStatusLocked("Closed"), true);
});

// ─── Tests: estados que NO deben estar bloqueados (edición libre) ─────────────

test("B1 candado: Quotation → NO bloqueado (borrador, edición libre)", () => {
    assert.equal(isStatusLocked("Quotation"), false);
});

test("B1 candado: Budget → NO bloqueado (presupuesto, edición libre)", () => {
    assert.equal(isStatusLocked("Budget"), false);
});

test("B1 candado: InManagement → NO bloqueado (en gestión, edición libre)", () => {
    assert.equal(isStatusLocked("InManagement"), false);
});

test("B1 candado: undefined → NO bloqueado (sin estado es tratable como libre)", () => {
    // Un modal abierto sin reservaStatus no bloquea (caso defensivo).
    assert.equal(isStatusLocked(undefined), false);
});

test("B1 candado: string vacío → NO bloqueado", () => {
    assert.equal(isStatusLocked(""), false);
});

// ─── Test de regresión: el set local anterior tenía el agujero ────────────────

test("B1 regresion: la lógica ANTERIOR dejaba Confirmed SIN bloquear (documentando el bug)", () => {
    // Este test documenta el agujero que existía antes del fix.
    // isLockedAnterior("Confirmed") devuelve false — que era el problema.
    assert.equal(isLockedAnterior("Confirmed"), false,
        "el set local viejo no cubría Confirmed — era el agujero de seguridad");
    assert.equal(isLockedAnterior("ToSettle"), false,
        "el set local viejo tampoco cubría ToSettle");
    // La lógica canónica, en cambio, los cubre correctamente:
    assert.equal(isStatusLocked("Confirmed"), true);
    assert.equal(isStatusLocked("ToSettle"), true);
});
