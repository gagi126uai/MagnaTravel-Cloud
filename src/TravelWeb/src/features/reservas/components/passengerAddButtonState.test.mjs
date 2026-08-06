/**
 * Tests de lógica pura para los 3 estados del botón "Agregar Pasajero" de PassengerList.jsx
 * (Frente 0, 2026-08-06 — refina la decisión 2026-06-17 / candado C1 spec 2026-07-22).
 *
 * Regla de negocio:
 *   1) Candado activo (reserva Confirmada sin autorización viva) + todavía falta algún nombre
 *      declarado por cargar -> el botón sigue encendido y dice "Completar pasajero" (decisión
 *      17/06 intacta: eso es completar, no altera nada).
 *   2) Candado activo + los N declarados YA TODOS cargados -> el botón queda travado con
 *      candadito. El SI/NO de este estado lo manda el backend (capabilities.canAddPassenger),
 *      la pantalla solo lo lee (T-13).
 *   3) Sin candado -> "Agregar Pasajero" de siempre, sin ningún cartel especial.
 *
 * Réplica de las constantes derivadas que calcula PassengerList.jsx (mismo cálculo, sin JSX/React
 * para poder testear en Node puro, igual criterio que candadoEdicionC1.test.mjs).
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/passengerAddButtonState.test.mjs
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

// ─── Réplica del cálculo del botón "Agregar Pasajero" (PassengerList.jsx) ────

function estadoBotonAgregarPasajero({ reserva, cargados, totalDeclarado, canAddPassenger }) {
    const candadoDeEdicionActivo = tieneCandadoDeEdicionActivo(reserva);
    const bloqueadoPorCandado = canAddPassenger != null && canAddPassenger.allowed === false;
    const titulo = candadoDeEdicionActivo && cargados < totalDeclarado
        ? "Completar pasajero"
        : "Agregar Pasajero";
    return { bloqueadoPorCandado, titulo };
}

// ─────────────────────────────────────────────────────────────────────────────
// Estado 1: candado activo + declarados incompletos -> "Completar pasajero", habilitado.
// ─────────────────────────────────────────────────────────────────────────────

test("Estado 1: Confirmed sin autorización + 1 de 2 cargados -> habilitado, dice Completar pasajero", () => {
    const resultado = estadoBotonAgregarPasajero({
        reserva: { status: "Confirmed", hasLiveEditAuthorization: false },
        cargados: 1,
        totalDeclarado: 2,
        canAddPassenger: { allowed: true, reason: null },
    });
    assert.equal(resultado.bloqueadoPorCandado, false);
    assert.equal(resultado.titulo, "Completar pasajero");
});

// ─────────────────────────────────────────────────────────────────────────────
// Estado 2: candado activo + declarados completos -> travado, manda el backend.
// ─────────────────────────────────────────────────────────────────────────────

test("Estado 2: Confirmed sin autorización + 1 de 1 cargados + backend dice No -> travado con candadito", () => {
    const resultado = estadoBotonAgregarPasajero({
        reserva: { status: "Confirmed", hasLiveEditAuthorization: false },
        cargados: 1,
        totalDeclarado: 1,
        canAddPassenger: { allowed: false, reason: "La reserva declara 1 pasajero y ya están todos cargados. Para agregar uno más, destrabá la reserva." },
    });
    assert.equal(resultado.bloqueadoPorCandado, true);
});

test("Estado 2: con autorización viva (destrabada) el mismo roster completo NO traba el botón", () => {
    // El backend ya no manda allowed=false cuando hay autorización viva (candado apagado): la
    // pantalla confía en esa verdad, no recalcula el candado por su cuenta para este caso.
    const resultado = estadoBotonAgregarPasajero({
        reserva: { status: "Confirmed", hasLiveEditAuthorization: true },
        cargados: 1,
        totalDeclarado: 1,
        canAddPassenger: { allowed: true, reason: null },
    });
    assert.equal(resultado.bloqueadoPorCandado, false);
});

// ─────────────────────────────────────────────────────────────────────────────
// Estado 3: sin candado -> "Agregar Pasajero" de siempre, sin importar el roster.
// ─────────────────────────────────────────────────────────────────────────────

test("Estado 3: InManagement (nunca hay candado) + roster completo -> Agregar Pasajero, sin travar", () => {
    const resultado = estadoBotonAgregarPasajero({
        reserva: { status: "InManagement", hasLiveEditAuthorization: false },
        cargados: 1,
        totalDeclarado: 1,
        canAddPassenger: { allowed: true, reason: null },
    });
    assert.equal(resultado.bloqueadoPorCandado, false);
    assert.equal(resultado.titulo, "Agregar Pasajero");
});

// ─────────────────────────────────────────────────────────────────────────────
// Degradación elegante: sin capability del backend (DTO viejo), nunca se traba de más.
// ─────────────────────────────────────────────────────────────────────────────

test("Degradación: canAddPassenger null/undefined (DTO sin la capacidad) -> nunca bloqueado", () => {
    const resultado = estadoBotonAgregarPasajero({
        reserva: { status: "Confirmed", hasLiveEditAuthorization: false },
        cargados: 1,
        totalDeclarado: 1,
        canAddPassenger: null,
    });
    assert.equal(resultado.bloqueadoPorCandado, false);
});
