/**
 * Tests del flujo "El cliente aceptó" (Budget → InManagement).
 *
 * ADR-031 (2026-06-15): el flujo cambió. El modal de pasajeros FUE ELIMINADO.
 * Ahora el botón "El cliente aceptó" pasa DIRECTO a En gestión sin abrir ninguna
 * ventana. Los pasos son solo dos:
 *   0) PATCH /passenger-counts (persistir la composición adultos/menores/infantes)
 *   1) PUT /status (cambiar estado a InManagement)
 *
 * H7 (2026-07-25, decisión firmada de Gastón): el requisito de UI para habilitar
 * el botón CAMBIÓ. Antes alcanzaba con la CANTIDAD declarada (>= 1); ahora hace
 * falta que el TITULAR (primer pasajero) tenga el NOMBRE cargado — mismo criterio
 * que ya usa el motor para confirmar hotel/traslado (calcularHintHotelTraslado).
 * Antes de este fix se podía avanzar con pasajeros "fantasma" sin nombre y recién
 * chocaba más adelante al confirmar un servicio con el operador.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/confirmReservaModalFlow.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { calcularHintHotelTraslado } from "../lib/pasajeroHint.js";

// ─── Lógica pura: validación previa al avance ─────────────────────────────────

/**
 * Replica la validación defensiva del handleConfirmReservation del ReservaDetailPage.
 * El botón debe estar deshabilitado cuando falta el titular con nombre — se apoya
 * en la MISMA función pura que usa ReservaHeader, para que front y "el mismo front"
 * nunca diverjan entre sí. El backend también re-valida, esto es la capa de UI.
 *
 * @param {{ passengers?: object[] }} reserva
 * @returns {string|null} — null si puede avanzar, mensaje de error si no
 */
function validarAntesDeAvanzar(reserva) {
    const { faltaTitular } = calcularHintHotelTraslado(reserva?.passengers);
    if (faltaTitular) {
        return "Tiene que haber un pasajero titular con el nombre cargado antes de continuar.";
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

// ─── Tests: validación previa (H7: titular CON NOMBRE, no solo cantidad) ──────

test("validar: sin pasajeros cargados → error, no puede avanzar", () => {
    const error = validarAntesDeAvanzar({ passengers: [] });
    assert.ok(error, "debe devolver mensaje de error sin pasajeros");
    assert.ok(error.includes("titular"), `mensaje inesperado: ${error}`);
});

test("validar: titular CON nombre → puede avanzar", () => {
    const error = validarAntesDeAvanzar({ passengers: [{ fullName: "Juan Perez" }] });
    assert.equal(error, null, "con el titular nombrado el front habilita el avance");
});

test("validar: titular SIN nombre (pasajero fantasma) → error, no puede avanzar", () => {
    // Este es el caso concreto que reportó el barrido E2E: antes bastaba con haber
    // declarado la CANTIDAD, aunque el titular no tuviera el nombre cargado.
    const error = validarAntesDeAvanzar({ passengers: [{ fullName: "" }] });
    assert.ok(error, "titular sin nombre no puede avanzar, aunque haya un registro de pasajero");
});

test("validar: hay más de un pasajero pero el titular (primero) no tiene nombre → error", () => {
    // Solo importa el PRIMER pasajero de la lista (el titular) — mismo criterio
    // que calcularHintHotelTraslado usa para hotel/traslado en toda la app.
    const error = validarAntesDeAvanzar({
        passengers: [{ fullName: "" }, { fullName: "Acompañante Con Nombre" }],
    });
    assert.ok(error, "el nombre de un acompañante no reemplaza al del titular");
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
        reserva: { adultCount: 2, childCount: 0, infantCount: 0, passengers: [{ fullName: "Juan Perez" }] },
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
        reserva: { adultCount: 1, childCount: 1, infantCount: 0, passengers: [{ fullName: "Juan Perez" }] },
        targetStatus: "InManagement",
        apiMocks: apiMockConEspía,
    });

    assert.equal(postPassengerLlamado, false, "el nuevo flujo NO crea pasajeros nominales al avanzar");
});

test("flujo sin modal: si PATCH counts falla → NO se ejecuta el PUT status", async () => {
    let putStatusLlamado = false;

    await assert.rejects(
        () => simularAvanceSinModal({
            reserva: { adultCount: 1, childCount: 0, infantCount: 0, passengers: [{ fullName: "Juan Perez" }] },
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
            reserva: { adultCount: 1, childCount: 0, infantCount: 0, passengers: [{ fullName: "Juan Perez" }] },
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

test("flujo sin modal: validación sin titular con nombre → no se llama ninguna API", async () => {
    let apiLlamada = false;

    await assert.rejects(
        () => simularAvanceSinModal({
            reserva: { adultCount: 0, childCount: 0, infantCount: 0, passengers: [] },
            targetStatus: "InManagement",
            apiMocks: {
                patchCounts: async () => { apiLlamada = true; },
                putStatus: async () => { apiLlamada = true; },
            },
        }),
        (err) => {
            assert.ok(err.message.includes("titular"));
            return true;
        }
    );

    assert.equal(apiLlamada, false, "si la validación falla, ninguna API debe llamarse");
});

// ─── Tests: comportamiento del botón en el UI (lógica pura) ──────────────────

test("botón 'El cliente aceptó' deshabilitado sin pasajeros cargados", () => {
    const { faltaTitular } = calcularHintHotelTraslado([]);
    assert.equal(faltaTitular, true, "sin pasajeros → botón debe estar deshabilitado");
});

test("botón deshabilitado si hay cantidad declarada pero el titular no tiene nombre", () => {
    // H7: este es el bug que arregla — antes esta reserva HABILITABA el botón
    // (bastaba con adultCount+childCount+infantCount >= 1). Ahora no alcanza.
    const { faltaTitular } = calcularHintHotelTraslado([{ fullName: "" }]);
    assert.equal(faltaTitular, true, "cantidad declarada sin nombre del titular → sigue deshabilitado");
});

test("botón habilitado cuando el titular tiene nombre cargado", () => {
    const { faltaTitular } = calcularHintHotelTraslado([{ fullName: "Juan Perez" }]);
    assert.equal(faltaTitular, false, "titular con nombre → botón habilitado");
});
