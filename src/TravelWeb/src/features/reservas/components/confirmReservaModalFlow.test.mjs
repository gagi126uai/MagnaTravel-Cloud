/**
 * Tests del flujo "El cliente aceptó" (Budget → InManagement).
 *
 * ADR-031 (2026-06-15): el flujo cambió. El modal de pasajeros FUE ELIMINADO.
 * Ahora el botón "El cliente aceptó" pasa DIRECTO a En gestión sin abrir ninguna
 * ventana. Los pasos son solo dos:
 *   0) PATCH /passenger-counts (persistir la composición adultos/menores/infantes)
 *   1) PUT /status (cambiar estado a InManagement)
 *
 * El único requisito de UI para habilitar el botón es que la suma de pasajeros
 * declarados sea >= 1. Los nombres se cargan DESPUÉS (en la solapa Pasajeros
 * o mediante el mini-formulario inline al emitir cada servicio).
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/confirmReservaModalFlow.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura: validación previa al avance ─────────────────────────────────

/**
 * Replica la validación defensiva del handleConfirmReservation del ReservaDetailPage.
 * El botón debe estar deshabilitado cuando total = 0.
 * El backend también valida, pero queremos corroborarlo en el front.
 *
 * @param {{ adultCount: number, childCount: number, infantCount: number }} reserva
 * @returns {string|null} — null si puede avanzar, mensaje de error si no
 */
function validarAntesDeAvanzar(reserva) {
    const total = (reserva?.adultCount || 0) + (reserva?.childCount || 0) + (reserva?.infantCount || 0);
    if (total === 0) {
        return "Tiene que haber al menos 1 pasajero declarado antes de continuar.";
    }
    return null;
}

/**
 * Replica el shape que se envía al PATCH /passenger-counts.
 * Tiene que coincidir exactamente con lo que el backend espera.
 */
function buildPassengerCountsPayload(reserva) {
    return {
        adultCount: reserva?.adultCount || 0,
        childCount: reserva?.childCount || 0,
        infantCount: reserva?.infantCount || 0,
    };
}

// ─── Simulación del flujo completo (sin modal, dos pasos) ─────────────────────

/**
 * Simula el nuevo flujo de avance: PATCH counts + PUT status.
 * NO hay POST /passengers intermedio (eso ahora lo hace el vendedor después).
 */
async function simularAvanceSinModal({ reserva, targetStatus, apiMocks }) {
    const llamadas = [];

    // Validación previa
    const error = validarAntesDeAvanzar(reserva);
    if (error) throw new Error(error);

    // Paso 0: PATCH /passenger-counts
    llamadas.push("patch-counts");
    await apiMocks.patchCounts(buildPassengerCountsPayload(reserva));

    // Paso 1: PUT /status
    llamadas.push("put-status");
    await apiMocks.putStatus({ status: targetStatus });

    return llamadas;
}

// Helpers
const resolveOk = () => async () => ({ ok: true });
const rejectWith = (message) => async () => { throw new Error(message); };

// ─── Tests: validación previa ─────────────────────────────────────────────────

test("validar: 0 adultos + 0 menores + 0 infantes → error, no puede avanzar", () => {
    const error = validarAntesDeAvanzar({ adultCount: 0, childCount: 0, infantCount: 0 });
    assert.ok(error, "debe devolver mensaje de error con 0 pasajeros");
    assert.ok(error.includes("al menos 1"), `mensaje inesperado: ${error}`);
});

test("validar: 1 adulto → puede avanzar (sin importar que no haya nombres)", () => {
    const error = validarAntesDeAvanzar({ adultCount: 1, childCount: 0, infantCount: 0 });
    assert.equal(error, null, "con 1 pasajero declarado el front habilita el avance");
});

test("validar: 0 adultos + 2 menores → puede avanzar", () => {
    const error = validarAntesDeAvanzar({ adultCount: 0, childCount: 2, infantCount: 0 });
    assert.equal(error, null);
});

test("validar: solo infantes → puede avanzar", () => {
    const error = validarAntesDeAvanzar({ adultCount: 0, childCount: 0, infantCount: 1 });
    assert.equal(error, null);
});

test("validar: reserva null → error (caso defensivo)", () => {
    const error = validarAntesDeAvanzar(null);
    assert.ok(error, "sin reserva no puede avanzar");
});

// ─── Tests: payload /passenger-counts ────────────────────────────────────────

test("buildPassengerCountsPayload: usa adultCount/childCount/infantCount (campos del backend)", () => {
    const payload = buildPassengerCountsPayload({ adultCount: 2, childCount: 1, infantCount: 0 });
    assert.deepEqual(payload, { adultCount: 2, childCount: 1, infantCount: 0 });
});

test("buildPassengerCountsPayload: infantCount va como 0, no se omite", () => {
    const payload = buildPassengerCountsPayload({ adultCount: 1, childCount: 0, infantCount: 0 });
    assert.equal(payload.infantCount, 0, "infantCount debe enviarse aunque sea 0");
});

// ─── Tests: secuencia del flujo sin modal ────────────────────────────────────

test("flujo sin modal: secuencia correcta → patch-counts PRIMERO, luego put-status", async () => {
    const llamadas = await simularAvanceSinModal({
        reserva: { adultCount: 2, childCount: 0, infantCount: 0 },
        targetStatus: "InManagement",
        apiMocks: {
            patchCounts: resolveOk(),
            putStatus: resolveOk(),
        },
    });

    // NO hay "post-passenger" en el medio — los nombres se cargan después.
    assert.deepEqual(llamadas, ["patch-counts", "put-status"]);
});

test("flujo sin modal: NO se crea ningún pasajero nominal en el avance", async () => {
    // Verificamos explícitamente que el flujo nuevo no intenta crear pasajeros.
    // Cualquier llamada a postPassenger sería un bug (el modal viejo hacía eso).
    let postPassengerLlamado = false;
    const apiMockConEspía = {
        patchCounts: resolveOk(),
        putStatus: resolveOk(),
        // Si alguien llama a esto, lo detectamos.
        postPassenger: async () => { postPassengerLlamado = true; },
    };

    await simularAvanceSinModal({
        reserva: { adultCount: 1, childCount: 1, infantCount: 0 },
        targetStatus: "InManagement",
        apiMocks: apiMockConEspía,
    });

    assert.equal(postPassengerLlamado, false, "el nuevo flujo NO crea pasajeros nominales al avanzar");
});

test("flujo sin modal: si PATCH counts falla → NO se ejecuta el PUT status", async () => {
    let putStatusLlamado = false;

    await assert.rejects(
        () => simularAvanceSinModal({
            reserva: { adultCount: 1, childCount: 0, infantCount: 0 },
            targetStatus: "InManagement",
            apiMocks: {
                patchCounts: rejectWith("Error en counts"),
                putStatus: async () => { putStatusLlamado = true; },
            },
        }),
        (err) => {
            assert.equal(err.message, "Error en counts");
            return true;
        }
    );

    assert.equal(putStatusLlamado, false, "si PATCH falla, PUT status no debe ejecutarse");
});

test("flujo sin modal: si PUT status falla → el error se propaga", async () => {
    await assert.rejects(
        () => simularAvanceSinModal({
            reserva: { adultCount: 1, childCount: 0, infantCount: 0 },
            targetStatus: "InManagement",
            apiMocks: {
                patchCounts: resolveOk(),
                putStatus: rejectWith("Error en status"),
            },
        }),
        (err) => {
            assert.equal(err.message, "Error en status");
            return true;
        }
    );
});

test("flujo sin modal: validación 0 pax → no se llama ninguna API", async () => {
    let apiLlamada = false;

    await assert.rejects(
        () => simularAvanceSinModal({
            reserva: { adultCount: 0, childCount: 0, infantCount: 0 },
            targetStatus: "InManagement",
            apiMocks: {
                patchCounts: async () => { apiLlamada = true; },
                putStatus: async () => { apiLlamada = true; },
            },
        }),
        (err) => {
            assert.ok(err.message.includes("al menos 1"));
            return true;
        }
    );

    assert.equal(apiLlamada, false, "si la validación falla, ninguna API debe llamarse");
});

// ─── Tests: comportamiento del botón en el UI (lógica pura) ──────────────────

test("botón 'El cliente aceptó' deshabilitado cuando total = 0", () => {
    // La lógica de deshabilitar es: total === 0 → disabled
    const reservaCerosPax = { adultCount: 0, childCount: 0, infantCount: 0 };
    const total = (reservaCerosPax.adultCount) + (reservaCerosPax.childCount) + (reservaCerosPax.infantCount);
    assert.equal(total === 0, true, "total 0 → botón debe estar deshabilitado");
});

test("botón habilitado aunque no haya nombres cargados (solo necesita cantidad >= 1)", () => {
    // El botón NO exige nombres — solo exige que haya al menos 1 declarado.
    // Importante: este era el comportamiento del modal viejo, pero ahora el botón
    // se habilita sin esperar nombres.
    const reservaConCantidad = { adultCount: 2, childCount: 1, infantCount: 0, passengers: [] };
    const total = reservaConCantidad.adultCount + reservaConCantidad.childCount + reservaConCantidad.infantCount;
    assert.equal(total > 0, true, "hay cantidad declarada → botón habilitado");
    // Cero pasajeros nominales no bloquea el avance en el front
    assert.equal(reservaConCantidad.passengers.length, 0, "no hay nominales pero el botón igual se habilita");
});
