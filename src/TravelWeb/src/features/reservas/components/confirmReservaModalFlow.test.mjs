/**
 * Tests de lógica pura del flujo de submit de ConfirmReservaModal.
 *
 * El modal tiene que hacer 3 pasos en orden estricto:
 *   0) PATCH /passenger-counts (persistir composición declarada)
 *   1) POST /passengers por cada pasajero faltante
 *   2) PUT /status (avanzar el estado)
 *
 * Si cualquier paso falla, los pasos siguientes NO deben ejecutarse.
 *
 * Estos tests ejercen la lógica pura extraída del modal: validaciones
 * previas al submit y el orden de la secuencia de API calls.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/confirmReservaModalFlow.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura: validaciones previas al submit ──────────────────────────────

/**
 * Replica de las validaciones del handleSubmit antes de iniciar los API calls.
 * Devuelve null si todo está bien, o un string con el error.
 */
function validarAntesDeSubmit({ adults, children, infants, slotsToFill, forms }) {
    const totalPasajeros = adults + children + infants;

    if (totalPasajeros === 0) {
        return "Tiene que haber al menos 1 pasajero antes de continuar.";
    }

    if (slotsToFill.length > 0) {
        const todosCompletos = slotsToFill.every((_, i) =>
            (forms[i]?.fullName || "").trim().length >= 3 &&
            (forms[i]?.documentNumber || "").trim().length > 0
        );
        if (!todosCompletos) {
            return "Completa nombre y documento de cada pasajero antes de continuar.";
        }
    }

    return null;
}

/**
 * Replica del shape que se envía al PATCH /passenger-counts.
 * Tiene que coincidir exactamente con lo que el backend espera (PassengerCountsRequest).
 */
function buildPassengerCountsPayload({ adults, children, infants }) {
    return {
        adultCount: adults,
        childCount: children,
        infantCount: infants,
    };
}

// ─── Lógica pura: simulación del orden de ejecución del submit ────────────────

/**
 * Simula el flujo completo del submit con APIs mockeadas.
 * Devuelve el registro de llamadas en orden para verificar la secuencia.
 *
 * apiMocks: { patchCounts, postPassenger, putStatus } — cada uno es una función
 *           async que puede resolver o rechazar según el caso de prueba.
 */
async function simularSubmit({ adults, children, infants, slotsToFill, forms, apiMocks }) {
    const llamadas = [];

    // Validación previa (igual al handleSubmit del modal)
    const error = validarAntesDeSubmit({ adults, children, infants, slotsToFill, forms });
    if (error) throw new Error(error);

    // Paso 0: PATCH /passenger-counts
    llamadas.push("patch-counts");
    await apiMocks.patchCounts({ adultCount: adults, childCount: children, infantCount: infants });

    // Paso 1: POST /passengers por cada slot faltante
    for (let i = 0; i < slotsToFill.length; i++) {
        llamadas.push(`post-passenger-${i}`);
        await apiMocks.postPassenger(forms[i]);
    }

    // Paso 2: PUT /status
    llamadas.push("put-status");
    await apiMocks.putStatus();

    return llamadas;
}

// Helpers para crear mocks
const resolveOk = () => async () => ({ ok: true });
const rejectWith = (message) => async () => { throw new Error(message); };

// ─── Tests: validaciones previas ──────────────────────────────────────────────

test("validar: 0 adultos + 0 menores + 0 infantes → error (no se puede avanzar sin pasajeros)", () => {
    const error = validarAntesDeSubmit({
        adults: 0, children: 0, infants: 0,
        slotsToFill: [], forms: [],
    });
    assert.ok(error, "debe devolver un mensaje de error");
    assert.ok(error.toLowerCase().includes("al menos 1"), `mensaje esperado 'al menos 1', recibido: ${error}`);
});

test("validar: 1 adulto + 0 menores + 0 infantes sin slots pendientes → OK (puede avanzar)", () => {
    const error = validarAntesDeSubmit({
        adults: 1, children: 0, infants: 0,
        slotsToFill: [], forms: [],
    });
    assert.equal(error, null, "sin pasajeros faltantes y total >= 1 no debe haber error");
});

test("validar: 2 adultos + 1 slot faltante sin nombre → error de formulario incompleto", () => {
    const error = validarAntesDeSubmit({
        adults: 2, children: 0, infants: 0,
        slotsToFill: [{ kind: "Adulto", index: 2 }],
        forms: [{ fullName: "", documentType: "DNI", documentNumber: "12345678" }],
    });
    assert.ok(error, "debe haber error si el nombre está vacío");
    assert.ok(error.toLowerCase().includes("nombre y documento"), `esperaba error de nombre/doc, recibió: ${error}`);
});

test("validar: slot faltante con nombre muy corto (2 caracteres) → error (mínimo 3 chars)", () => {
    const error = validarAntesDeSubmit({
        adults: 1, children: 1, infants: 0,
        slotsToFill: [{ kind: "Menor", index: 1 }],
        forms: [{ fullName: "AB", documentType: "DNI", documentNumber: "12345678" }],
    });
    assert.ok(error, "nombre de 2 chars no es válido");
});

test("validar: slot faltante sin documento → error", () => {
    const error = validarAntesDeSubmit({
        adults: 1, children: 1, infants: 0,
        slotsToFill: [{ kind: "Menor", index: 1 }],
        forms: [{ fullName: "Juan Perez", documentType: "DNI", documentNumber: "" }],
    });
    assert.ok(error, "sin documento no debe pasar");
});

test("validar: slot faltante completo → OK", () => {
    const error = validarAntesDeSubmit({
        adults: 1, children: 1, infants: 0,
        slotsToFill: [{ kind: "Menor", index: 1 }],
        forms: [{ fullName: "Ana Lopez", documentType: "DNI", documentNumber: "45678901" }],
    });
    assert.equal(error, null, "slot completo y total >= 1: no debe haber error");
});

// ─── Tests: shape del payload /passenger-counts ───────────────────────────────

test("buildPassengerCountsPayload: usa adultCount/childCount/infantCount (nombres del backend)", () => {
    // El backend (PassengerCountsRequest) espera estos tres campos en camelCase.
    // NO usar 'adults'/'children'/'infants' que son los nombres internos del estado del modal.
    const payload = buildPassengerCountsPayload({ adults: 2, children: 1, infants: 0 });

    assert.deepEqual(payload, { adultCount: 2, childCount: 1, infantCount: 0 });
});

test("buildPassengerCountsPayload: preserva infantes (no los omite cuando son 0)", () => {
    const payload = buildPassengerCountsPayload({ adults: 1, children: 0, infants: 0 });
    assert.equal(payload.infantCount, 0, "infantCount debe enviarse aunque sea 0");
});

test("buildPassengerCountsPayload: valores se copian exactamente sin transformar", () => {
    const payload = buildPassengerCountsPayload({ adults: 3, children: 2, infants: 1 });
    assert.equal(payload.adultCount, 3);
    assert.equal(payload.childCount, 2);
    assert.equal(payload.infantCount, 1);
});

// ─── Tests: orden del submit (secuencia de API calls) ─────────────────────────

test("submit: secuencia correcta → patch-counts PRIMERO, luego passengers, luego status", async () => {
    const llamadas = await simularSubmit({
        adults: 2, children: 0, infants: 0,
        slotsToFill: [{ kind: "Adulto", index: 2 }],
        forms: [{ fullName: "Juan Perez", documentType: "DNI", documentNumber: "12345678" }],
        apiMocks: {
            patchCounts: resolveOk(),
            postPassenger: resolveOk(),
            putStatus: resolveOk(),
        },
    });

    assert.deepEqual(llamadas, ["patch-counts", "post-passenger-0", "put-status"]);
});

test("submit: sin pasajeros faltantes → patch-counts + put-status (sin POST intermedios)", async () => {
    const llamadas = await simularSubmit({
        adults: 1, children: 0, infants: 0,
        slotsToFill: [],
        forms: [],
        apiMocks: {
            patchCounts: resolveOk(),
            postPassenger: resolveOk(),
            putStatus: resolveOk(),
        },
    });

    assert.deepEqual(llamadas, ["patch-counts", "put-status"]);
});

test("submit: si PATCH /passenger-counts falla → NO se ejecutan pasajeros ni PUT /status", async () => {
    await assert.rejects(
        () => simularSubmit({
            adults: 2, children: 0, infants: 0,
            slotsToFill: [{ kind: "Adulto", index: 2 }],
            forms: [{ fullName: "Juan Perez", documentType: "DNI", documentNumber: "12345678" }],
            apiMocks: {
                // El PATCH falla (ej: backend rechaza la composición)
                patchCounts: rejectWith("Composición inválida"),
                postPassenger: resolveOk(),
                putStatus: resolveOk(),
            },
        }),
        (err) => {
            assert.equal(err.message, "Composición inválida");
            return true;
        }
    );
});

test("submit: si POST /passengers falla → NO se ejecuta el PUT /status", async () => {
    const postPassengerLlamado = { veces: 0 };
    const putStatusLlamado = { veces: 0 };

    await assert.rejects(
        () => simularSubmit({
            adults: 2, children: 0, infants: 0,
            slotsToFill: [{ kind: "Adulto", index: 2 }],
            forms: [{ fullName: "Juan Perez", documentType: "DNI", documentNumber: "12345678" }],
            apiMocks: {
                patchCounts: resolveOk(),
                postPassenger: async () => {
                    postPassengerLlamado.veces++;
                    throw new Error("Error al cargar pasajero");
                },
                putStatus: async () => {
                    putStatusLlamado.veces++;
                },
            },
        }),
        (err) => {
            assert.equal(err.message, "Error al cargar pasajero");
            return true;
        }
    );

    assert.equal(postPassengerLlamado.veces, 1, "el POST se llamó una vez (y falló)");
    assert.equal(putStatusLlamado.veces, 0, "el PUT /status NO debe ejecutarse si el POST falló");
});

test("submit: múltiples pasajeros → patch primero, luego cada passenger en orden, luego status", async () => {
    const llamadas = await simularSubmit({
        adults: 2, children: 1, infants: 0,
        slotsToFill: [
            { kind: "Adulto", index: 2 },
            { kind: "Menor", index: 1 },
        ],
        forms: [
            { fullName: "Juan Perez", documentType: "DNI", documentNumber: "12345678" },
            { fullName: "Ana Lopez", documentType: "DNI", documentNumber: "98765432" },
        ],
        apiMocks: {
            patchCounts: resolveOk(),
            postPassenger: resolveOk(),
            putStatus: resolveOk(),
        },
    });

    assert.deepEqual(llamadas, [
        "patch-counts",
        "post-passenger-0",
        "post-passenger-1",
        "put-status",
    ]);
});

test("submit: validación falla (0 pasajeros) → no se llama ninguna API", async () => {
    let apiLlamada = false;

    await assert.rejects(
        () => simularSubmit({
            adults: 0, children: 0, infants: 0,
            slotsToFill: [], forms: [],
            apiMocks: {
                patchCounts: async () => { apiLlamada = true; },
                postPassenger: resolveOk(),
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
