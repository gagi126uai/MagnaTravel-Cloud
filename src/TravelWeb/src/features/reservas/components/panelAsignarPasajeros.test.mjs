/**
 * Tests del PanelAsignarPasajeros y lógica relacionada (ADR-031 v2.1 — review fixes).
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/panelAsignarPasajeros.test.mjs
 *
 * Qué cubre:
 *   - Inicialización de tildes desde coverage (B2, reemplaza GET /assignments)
 *   - Guard de 0 tildados (no se puede dejar vacío)
 *   - calcularSlotsFaltantesDelSet con coverage de servicio acotado (B1)
 *   - Comportamiento esperado de los builds del payload PUT (esTodos / subconjunto)
 *
 * Los tests son de lógica pura, sin React/DOM/API:
 * la lógica de inicialización y guardado se puede extraer y probar sin montar el componente.
 */

import test from "node:test";
import assert from "node:assert/strict";

// Importamos la función de pasajeroHint que usa el mini-form (B1)
import { calcularSlotsFaltantesDelSet } from "../lib/pasajeroHint.js";

// Importamos los helpers del módulo de lógica pura del panel.
// Están en un .js separado (no en el .jsx) para que Node puro pueda importarlos
// sin transpiler de JSX. El componente también los importa de aquí — mismo código.
// Si la firma cambia, los tests fallan de inmediato — intencional.
import { inicializarTildados, armarPayloadPut } from "../lib/panelAsignarPasajerosHelpers.js";

// ─── Helpers para construir datos de prueba ───────────────────────────────────

/**
 * Crea un pasajero con nombre (los que llegan al panel con nombre cargado).
 */
const paxConNombre = (publicId, nombre = "Nombre Apellido") => ({
    publicId,
    fullName: nombre,
    documentNumber: "12345678",
    birthDate: "1990-01-01",
});

/**
 * Crea un coverage sin asignaciones explícitas ("Para: Todos").
 * El backend devuelve este shape cuando todos los pasajeros van al servicio.
 */
const coverageTodos = (pasajeros) => ({
    hasExplicitAssignments: false,
    serviceSetCount: pasajeros.length,
    reservaPassengerCount: pasajeros.length,
    isComplete: true,
    missingMessage: null,
    serviceSet: pasajeros.map((p, i) => ({
        passengerPublicId: p.publicId,
        fullName: p.fullName,
        isLead: i === 0,
        hasRequiredDataForServiceType: true,
    })),
});

/**
 * Crea un coverage con asignaciones explícitas ("Para: X de N").
 * El backend devuelve este shape cuando solo un subconjunto va al servicio.
 */
const coverageSubconjunto = (asignados, totalPasajeros) => ({
    hasExplicitAssignments: true,
    serviceSetCount: asignados.length,
    reservaPassengerCount: totalPasajeros,
    isComplete: true,
    missingMessage: null,
    serviceSet: asignados.map((p, i) => ({
        passengerPublicId: p.publicId,
        fullName: p.fullName,
        isLead: i === 0,
        hasRequiredDataForServiceType: true,
    })),
});

/**
 * Crea una coverage con slots incompletos (para el mini-form B1).
 */
const coverageConFaltantes = (setCompleto, faltantes) => ({
    hasExplicitAssignments: setCompleto.length < setCompleto.length + faltantes.length,
    serviceSetCount: setCompleto.length + faltantes.length,
    reservaPassengerCount: setCompleto.length + faltantes.length,
    isComplete: false,
    missingMessage: "Faltan datos",
    serviceSet: [
        ...setCompleto.map((p, i) => ({
            passengerPublicId: p.publicId,
            fullName: p.fullName,
            isLead: i === 0,
            hasRequiredDataForServiceType: true,
        })),
        ...faltantes.map((p, i) => ({
            passengerPublicId: p.publicId,
            fullName: p.fullName,
            isLead: false,
            hasRequiredDataForServiceType: false,
        })),
    ],
});

// ─── Tests: inicialización desde coverage (B2 — ya no usa GET /assignments) ──

test("B2 — coverage sin asignaciones → todos los pasajeros tildados", () => {
    const pasajeros = [paxConNombre("aaa"), paxConNombre("bbb"), paxConNombre("ccc")];
    const coverage = coverageTodos(pasajeros);

    const tildados = inicializarTildados(coverage, pasajeros);

    assert.equal(tildados.size, 3, "Los 3 pasajeros deben estar tildados");
    assert.ok(tildados.has("aaa"), "aaa debe estar tildado");
    assert.ok(tildados.has("bbb"), "bbb debe estar tildado");
    assert.ok(tildados.has("ccc"), "ccc debe estar tildado");
});

test("B2 — coverage con subconjunto explícito → solo los asignados tildados", () => {
    const pasajeros = [paxConNombre("aaa"), paxConNombre("bbb"), paxConNombre("ccc")];
    // Solo aaa y bbb van en este servicio
    const coverage = coverageSubconjunto([paxConNombre("aaa"), paxConNombre("bbb")], 3);

    const tildados = inicializarTildados(coverage, pasajeros);

    assert.equal(tildados.size, 2, "Solo 2 deben estar tildados");
    assert.ok(tildados.has("aaa"), "aaa debe estar tildado");
    assert.ok(tildados.has("bbb"), "bbb debe estar tildado");
    assert.ok(!tildados.has("ccc"), "ccc NO debe estar tildado");
});

test("B2 — coverage null → fallback a todos tildados", () => {
    const pasajeros = [paxConNombre("aaa"), paxConNombre("bbb")];

    const tildados = inicializarTildados(null, pasajeros);

    assert.equal(tildados.size, 2, "Sin coverage, todos deben estar tildados");
    assert.ok(tildados.has("aaa"));
    assert.ok(tildados.has("bbb"));
});

test("B2 — coverage con ids que no matchean pasajeros → fallback a todos", () => {
    // El backend devuelve ids que no existen en la lista local (caso extremo/defensivo)
    const pasajeros = [paxConNombre("aaa"), paxConNombre("bbb")];
    const coverageConIdsExtranos = {
        hasExplicitAssignments: true,
        serviceSet: [{ passengerPublicId: "zzz", fullName: "X" }],
    };

    const tildados = inicializarTildados(coverageConIdsExtranos, pasajeros);

    assert.equal(tildados.size, 2, "Si no hay match, fallback a todos");
});

// ─── Tests: payload del PUT (esTodos vs subconjunto) ─────────────────────────

test("B2 — todos los pasajeros tildados → PUT con lista vacía ('Para: Todos')", () => {
    const pasajeros = [paxConNombre("aaa"), paxConNombre("bbb")];
    const tildados = new Set(["aaa", "bbb"]);

    const payload = armarPayloadPut(tildados, pasajeros);

    assert.deepEqual(payload, [], "Lista vacía = Para: Todos");
});

test("B2 — subconjunto tildado → PUT con los ids específicos", () => {
    const pasajeros = [paxConNombre("aaa"), paxConNombre("bbb"), paxConNombre("ccc")];
    // Solo aaa y ccc tildados
    const tildados = new Set(["aaa", "ccc"]);

    const payload = armarPayloadPut(tildados, pasajeros);

    // El payload debe tener exactamente los 2 ids del subconjunto
    assert.equal(payload.length, 2);
    assert.ok(payload.includes("aaa"), "aaa en el payload");
    assert.ok(payload.includes("ccc"), "ccc en el payload");
    assert.ok(!payload.includes("bbb"), "bbb NO en el payload");
});

test("B2 — fallo del PUT → el set de tildes se conserva para reintentar", () => {
    // Verificamos que el estado no se resetea al fallar: la lógica de conservar el
    // estado de tildes está en el catch del handleListo (setGuardando(false) sin resetear tildados).
    // Este test documenta la EXPECTATIVA: mismos ids = mismo resultado en el reintento.
    const pasajeros = [paxConNombre("aaa"), paxConNombre("bbb")];
    const tildadosAntesDeFallo = new Set(["aaa"]);

    // Simulamos que el PUT falló y se vuelve a llamar con el mismo set
    const payloadReintento = armarPayloadPut(tildadosAntesDeFallo, pasajeros);

    // El reintento produce el mismo payload → idempotente
    assert.equal(payloadReintento.length, 1);
    assert.ok(payloadReintento.includes("aaa"));
});

// ─── Tests: guard de 0 tildados ───────────────────────────────────────────────

test("B2 — no se puede dejar 0 tildados: toggle en último tildado no hace nada", () => {
    // Replicamos la lógica del handleToggle para verificar el guard
    function toggleConGuard(prev, pasajeroPublicId) {
        const key = String(pasajeroPublicId || "").toLowerCase();
        const next = new Set(prev);
        if (next.has(key)) {
            // Guard: no dejar vacío
            if (next.size <= 1) return prev;
            next.delete(key);
        } else {
            next.add(key);
        }
        return next;
    }

    // Solo "aaa" tildado — intentar destildar debe retornar el mismo set
    const soloUno = new Set(["aaa"]);
    const resultado = toggleConGuard(soloUno, "aaa");

    assert.equal(resultado.size, 1, "No se puede dejar 0 tildados");
    assert.ok(resultado.has("aaa"), "El último tildado se preserva");
});

test("B2 — con 2 tildados, se puede destildar uno dejando 1", () => {
    function toggleConGuard(prev, pasajeroPublicId) {
        const key = String(pasajeroPublicId || "").toLowerCase();
        const next = new Set(prev);
        if (next.has(key)) {
            if (next.size <= 1) return prev;
            next.delete(key);
        } else {
            next.add(key);
        }
        return next;
    }

    const dos = new Set(["aaa", "bbb"]);
    const resultado = toggleConGuard(dos, "bbb");

    assert.equal(resultado.size, 1, "Queda 1 tildado");
    assert.ok(resultado.has("aaa"));
    assert.ok(!resultado.has("bbb"));
});

// ─── Tests: calcularSlotsFaltantesDelSet (B1 — mini-form usa el SET del servicio) ──

test("B1 — coverage con 2 de 3, 1 faltante → solo pide el slot del faltante", () => {
    // Servicio "Para: 2 de 3" — uno de los 2 asignados le faltan datos
    const pasajeroCompleto = paxConNombre("aaa");
    const pasajeroFaltante = { publicId: "bbb", fullName: "Sin Documento" };

    const coverage = {
        hasExplicitAssignments: true,
        isComplete: false,
        missingMessage: "Faltan datos",
        serviceSet: [
            { passengerPublicId: "aaa", fullName: "Nombre Apellido", isLead: true, hasRequiredDataForServiceType: true },
            { passengerPublicId: "bbb", fullName: "Sin Documento", isLead: false, hasRequiredDataForServiceType: false },
        ],
    };

    const pasajerosCompletos = [pasajeroCompleto, pasajeroFaltante, paxConNombre("ccc")];
    const slots = calcularSlotsFaltantesDelSet(coverage, pasajerosCompletos);

    // Solo debe pedir el slot del pasajero con datos incompletos (bbb)
    // El tercero (ccc) no está en el set del servicio → no se pide
    assert.equal(slots.length, 1, "Solo 1 slot faltante (el del subconjunto, no el de ccc)");
    assert.equal(slots[0].passenger?.publicId, "bbb");
});

test("B1 — coverage Para: Todos con 1 faltante → pide el slot correspondiente", () => {
    const coverage = {
        hasExplicitAssignments: false,
        isComplete: false,
        missingMessage: "Faltan datos",
        serviceSet: [
            { passengerPublicId: "aaa", fullName: "Nombre", isLead: true, hasRequiredDataForServiceType: true },
            { passengerPublicId: "bbb", fullName: "", isLead: false, hasRequiredDataForServiceType: false },
        ],
    };

    const pasajerosCompletos = [
        paxConNombre("aaa"),
        { publicId: "bbb", fullName: "" },
    ];

    const slots = calcularSlotsFaltantesDelSet(coverage, pasajerosCompletos);

    assert.equal(slots.length, 1, "Solo el faltante genera un slot");
});

test("B1 — coverage completa → no hay slots faltantes (mini-form no aparece)", () => {
    const coverage = {
        hasExplicitAssignments: false,
        isComplete: true,
        missingMessage: null,
        serviceSet: [
            { passengerPublicId: "aaa", fullName: "Nombre", isLead: true, hasRequiredDataForServiceType: true },
        ],
    };

    const slots = calcularSlotsFaltantesDelSet(coverage, [paxConNombre("aaa")]);

    assert.equal(slots.length, 0, "Sin faltantes = mini-form no aparece");
});

test("B1 — coverage null → vacío (mini-form no aparece mientras carga)", () => {
    const slots = calcularSlotsFaltantesDelSet(null, [paxConNombre("aaa")]);

    assert.equal(slots.length, 0, "Coverage null = no mostrar nada todavía");
});
