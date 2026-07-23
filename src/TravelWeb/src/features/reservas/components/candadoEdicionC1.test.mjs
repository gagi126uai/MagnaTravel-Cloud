/**
 * Tests de lógica pura para el candado C1 de edición de la reserva
 * (spec docs/ux/2026-07-22-candado-coherente-y-duplicados.md, 2026-07-23).
 *
 * Regla de negocio: con la reserva en un estado con candado (Confirmed/Traveling/Closed)
 * Y SIN una autorización de edición viva (hasLiveEditAuthorization), los botones de
 * edición (Editar fechas, Reprogramar viaje, Agregar/Editar/Anular servicio, Anular
 * varios servicios, Confirmar costo, Editar/Eliminar pasajero) tienen que verse
 * "gris + candadito" y abrir la ventana de destrabar en vez de ejecutar la acción.
 *
 * También fija la regla de convivencia (§1.7 de la spec): con el candado de RESERVA
 * activo, el freno FISCAL por servicio (voucher vivo / pago sin factura / etc.) todavía
 * NO se evalúa — "un candado a la vez por botón". Recién se evalúa cuando se destraba.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/candadoEdicionC1.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de tieneCandadoDeEdicionActivo (ReservaStatusBadge.jsx) ──────────

const LOCKED_STATUSES = new Set(["Confirmed", "Traveling", "Closed"]);

function isStatusLocked(status) {
    return LOCKED_STATUSES.has(status);
}

function tieneCandadoDeEdicionActivo(reserva) {
    return isStatusLocked(reserva?.status) && !(reserva?.hasLiveEditAuthorization ?? false);
}

// ─── Réplica de la convivencia de candados en el tacho de ServiceList (§1.7) ──

/**
 * Con la reserva bloqueada (candadoDeEdicionActivo), el freno fiscal por servicio
 * (bloqueoAnular) NO se evalúa todavía: el candado de reserva manda primero.
 */
function debeEvaluarFrenoFiscal({ esConfirmado, candadoDeEdicionActivo }) {
    return esConfirmado && !candadoDeEdicionActivo;
}

// ─────────────────────────────────────────────────────────────────────────────
// Candado de reserva: matriz de estados
// ─────────────────────────────────────────────────────────────────────────────

test("C1 candado: Confirmed sin autorización viva → candado activo", () => {
    assert.equal(tieneCandadoDeEdicionActivo({ status: "Confirmed", hasLiveEditAuthorization: false }), true);
});

test("C1 candado: Confirmed CON autorización viva → candado apagado (destrabada)", () => {
    assert.equal(tieneCandadoDeEdicionActivo({ status: "Confirmed", hasLiveEditAuthorization: true }), false);
});

test("C1 candado: Confirmed sin el campo hasLiveEditAuthorization (DTO viejo) → candado activo por default", () => {
    assert.equal(tieneCandadoDeEdicionActivo({ status: "Confirmed" }), true);
});

test("C1 candado: Quotation/Budget/InManagement → nunca candado (no son estados con candado)", () => {
    assert.equal(tieneCandadoDeEdicionActivo({ status: "Quotation" }), false);
    assert.equal(tieneCandadoDeEdicionActivo({ status: "Budget" }), false);
    assert.equal(tieneCandadoDeEdicionActivo({ status: "InManagement" }), false);
});

test("C1 candado: Lost/Cancelled → nunca candado (estados terminales sin candado; los botones ya están escondidos por capability)", () => {
    assert.equal(tieneCandadoDeEdicionActivo({ status: "Lost" }), false);
    assert.equal(tieneCandadoDeEdicionActivo({ status: "Cancelled" }), false);
});

test("C1 candado: reserva null/undefined → no rompe, candado apagado", () => {
    assert.equal(tieneCandadoDeEdicionActivo(null), false);
    assert.equal(tieneCandadoDeEdicionActivo(undefined), false);
});

// ─────────────────────────────────────────────────────────────────────────────
// Convivencia con el freno fiscal del tacho (§1.7 de la spec)
// ─────────────────────────────────────────────────────────────────────────────

test("§1.7 convivencia: reserva bloqueada → NO se evalúa el freno fiscal (manda el candado de reserva)", () => {
    assert.equal(debeEvaluarFrenoFiscal({ esConfirmado: true, candadoDeEdicionActivo: true }), false);
});

test("§1.7 convivencia: reserva destrabada + servicio confirmado → SÍ se evalúa el freno fiscal", () => {
    assert.equal(debeEvaluarFrenoFiscal({ esConfirmado: true, candadoDeEdicionActivo: false }), true);
});

test("§1.7 convivencia: servicio no confirmado (Borrar, no Anular) → nunca hay freno fiscal que evaluar", () => {
    assert.equal(debeEvaluarFrenoFiscal({ esConfirmado: false, candadoDeEdicionActivo: false }), false);
    assert.equal(debeEvaluarFrenoFiscal({ esConfirmado: false, candadoDeEdicionActivo: true }), false);
});
