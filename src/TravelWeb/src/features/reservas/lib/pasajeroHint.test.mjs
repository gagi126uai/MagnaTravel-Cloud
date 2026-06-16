/**
 * Tests de la lógica pura de hints de pasajeros por tipo de servicio (ADR-031).
 *
 * Estas funciones calculan si un servicio puede ser resuelto/emitido
 * según los datos de los pasajeros ya cargados en la reserva.
 * Son pura lógica de negocio sin UI: ideales para tests unitarios.
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/pasajeroHint.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// Importamos las funciones a testear.
// En ESM Node 20+ se puede importar archivos .js directamente.
import {
    calcularHintAereo,
    calcularHintHotelTraslado,
    calcularHintAsistencia,
    calcularHintPaqueteGenerico,
    calcularHintPorTipo,
    calcularSlotsFaltantesDelSet,
    calcularSugerenciaComposicion,
    calcularTotalPasajerosDeclarados,
} from "./pasajeroHint.js";

// ─── Helpers para construir pasajeros de prueba ───────────────────────────────

const paxConTodo = (n = 1) => ({
    fullName: `Pasajero ${n}`,
    documentType: "DNI",
    documentNumber: `${10000000 + n}`,
    birthDate: "1990-01-01",
});

const paxSinDocumento = (n = 1) => ({
    fullName: `Pasajero ${n}`,
    documentType: "DNI",
    documentNumber: "",
    birthDate: "1990-01-01",
});

const paxSinNombre = (n = 1) => ({
    fullName: "",
    documentType: "DNI",
    documentNumber: `${10000000 + n}`,
    birthDate: "1990-01-01",
});

const paxSinFecha = (n = 1) => ({
    fullName: `Pasajero ${n}`,
    documentType: "DNI",
    documentNumber: `${10000000 + n}`,
    birthDate: null,
});

// Reserva "tipo": 2 adultos, 1 menor, 0 infantes
const reserva2A1M = { adultCount: 2, childCount: 1, infantCount: 0 };
const reserva1A = { adultCount: 1, childCount: 0, infantCount: 0 };
const reservaSinDeclarar = { adultCount: 0, childCount: 0, infantCount: 0 };

// ─── Tests: calcularTotalPasajerosDeclarados ──────────────────────────────────

test("calcularTotalPasajerosDeclarados: suma adultos + menores + infantes", () => {
    const total = calcularTotalPasajerosDeclarados({ adultCount: 2, childCount: 1, infantCount: 1 });
    assert.equal(total, 4);
});

test("calcularTotalPasajerosDeclarados: reserva vacía → 0", () => {
    assert.equal(calcularTotalPasajerosDeclarados({}), 0);
});

test("calcularTotalPasajerosDeclarados: reserva null → 0", () => {
    assert.equal(calcularTotalPasajerosDeclarados(null), 0);
});

// ─── Tests: calcularHintAereo ─────────────────────────────────────────────────

test("Aéreo: sin pasajeros declarados → no listo", () => {
    const hint = calcularHintAereo([], 0);
    assert.equal(hint.listo, false);
});

test("Aéreo: 1 pasajero declarado con nombre y documento → listo", () => {
    const hint = calcularHintAereo([paxConTodo(1)], 1);
    assert.equal(hint.listo, true);
    assert.equal(hint.faltanNombres, 0);
    assert.equal(hint.faltanDocumentos, 0);
});

test("Aéreo: 2 declarados, 1 cargado con todo → no listo (falta 1 nombre)", () => {
    const hint = calcularHintAereo([paxConTodo(1)], 2);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltanNombres, 1);
});

test("Aéreo: 2 declarados, 2 cargados, 1 sin documento → no listo", () => {
    const hint = calcularHintAereo([paxConTodo(1), paxSinDocumento(2)], 2);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltanDocumentos, 1);
    assert.equal(hint.faltanNombres, 0);
});

test("Aéreo: 2 declarados, 2 cargados con todo → listo", () => {
    const hint = calcularHintAereo([paxConTodo(1), paxConTodo(2)], 2);
    assert.equal(hint.listo, true);
});

test("Aéreo: lista vacía con 1 declarado → no listo (falta 1 nombre)", () => {
    const hint = calcularHintAereo([], 1);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltanNombres, 1);
    assert.equal(hint.faltanDocumentos, 1);
});

// ─── Tests: calcularHintHotelTraslado ────────────────────────────────────────

test("Hotel: sin pasajeros → no listo (falta titular)", () => {
    const hint = calcularHintHotelTraslado([]);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltaTitular, true);
});

test("Hotel: titular con nombre → listo (no importan el resto)", () => {
    // Solo el primer pasajero importa para hotel/traslado
    const hint = calcularHintHotelTraslado([paxConTodo(1), paxSinNombre(2)]);
    assert.equal(hint.listo, true);
    assert.equal(hint.faltaTitular, false);
});

test("Hotel: titular sin nombre → no listo", () => {
    const hint = calcularHintHotelTraslado([paxSinNombre(1), paxConTodo(2)]);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltaTitular, true);
});

test("Traslado: titular con nombre → listo (misma lógica que hotel)", () => {
    const hint = calcularHintHotelTraslado([paxConTodo(1)]);
    assert.equal(hint.listo, true);
});

// ─── Tests: calcularHintAsistencia ───────────────────────────────────────────

test("Asistencia: 1 declarado con todo → listo", () => {
    const hint = calcularHintAsistencia([paxConTodo(1)], 1);
    assert.equal(hint.listo, true);
    assert.equal(hint.faltanFechas, 0);
});

test("Asistencia: 1 declarado sin fecha → no listo", () => {
    const hint = calcularHintAsistencia([paxSinFecha(1)], 1);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltanFechas, 1);
});

test("Asistencia: 0 declarados → no listo", () => {
    const hint = calcularHintAsistencia([], 0);
    assert.equal(hint.listo, false);
});

test("Asistencia: 2 declarados, 1 sin documento, 1 con todo → no listo", () => {
    const hint = calcularHintAsistencia([paxConTodo(1), paxSinDocumento(2)], 2);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltanDocumentos, 1);
});

// ─── Tests: calcularHintPaqueteGenerico ──────────────────────────────────────

test("Paquete: 2 declarados, 2 con nombre → listo (no necesita documento)", () => {
    // Paquete y genérico solo piden fullName
    const hint = calcularHintPaqueteGenerico([paxSinDocumento(1), paxSinDocumento(2)], 2);
    assert.equal(hint.listo, true);
});

test("Paquete: 2 declarados, 1 sin nombre → no listo", () => {
    const hint = calcularHintPaqueteGenerico([paxConTodo(1), paxSinNombre(2)], 2);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltanNombres, 1);
});

test("Genérico: 0 declarados → no listo", () => {
    const hint = calcularHintPaqueteGenerico([], 0);
    assert.equal(hint.listo, false);
});

// ─── Tests: calcularHintPorTipo (punto de entrada unificado) ─────────────────

test("calcularHintPorTipo flight: delega a calcularHintAereo", () => {
    const hint = calcularHintPorTipo("flight", [paxConTodo(1)], reserva1A);
    assert.equal(hint.listo, true);
});

test("calcularHintPorTipo hotel: delega a calcularHintHotelTraslado", () => {
    const hint = calcularHintPorTipo("hotel", [paxConTodo(1)], reserva1A);
    assert.equal(hint.listo, true);
    assert.ok("faltaTitular" in hint, "debe tener faltaTitular");
});

test("calcularHintPorTipo transfer: misma lógica que hotel", () => {
    const hint = calcularHintPorTipo("transfer", [], reserva1A);
    assert.equal(hint.listo, false);
    assert.equal(hint.faltaTitular, true);
});

test("calcularHintPorTipo assistance: delega a calcularHintAsistencia", () => {
    const hint = calcularHintPorTipo("assistance", [paxSinFecha(1)], reserva1A);
    assert.equal(hint.listo, false);
    assert.ok("faltanFechas" in hint);
});

test("calcularHintPorTipo package: delega a calcularHintPaqueteGenerico", () => {
    const hint = calcularHintPorTipo("package", [paxSinDocumento(1)], reserva1A);
    // Paquete solo pide nombre; sin documento igual pasa
    assert.equal(hint.listo, true);
});

test("calcularHintPorTipo generic: igual a package", () => {
    const hint = calcularHintPorTipo("generic", [paxSinNombre(1)], reserva1A);
    assert.equal(hint.listo, false);
});

test("calcularHintPorTipo tipo desconocido → no listo (conservador)", () => {
    const hint = calcularHintPorTipo("xxxxx", [paxConTodo(1)], reserva1A);
    assert.equal(hint.listo, false);
});

// ─── Tests: calcularSlotsFaltantesDelSet (Pieza B — ADR-031 v2.1) ─────────────

// Helper: construye un ServiceNominalCoverageDto de prueba.
function coverageCompleta(pasajeros) {
    return {
        isComplete: true,
        missingMessage: null,
        serviceSet: pasajeros.map((p, i) => ({
            passengerPublicId: p.publicId,
            fullName: p.fullName,
            isLead: i === 0,
            hasRequiredDataForServiceType: true,
        })),
    };
}

function coverageIncompleta(pasajeros, completos) {
    // completos: índices (0-based) de los que tienen los datos requeridos
    return {
        isComplete: false,
        missingMessage: "Faltan datos de pasajeros",
        serviceSet: pasajeros.map((p, i) => ({
            passengerPublicId: p.publicId,
            fullName: p.fullName,
            isLead: i === 0,
            hasRequiredDataForServiceType: completos.includes(i),
        })),
    };
}

// Pasajeros con publicId para los tests de SET
const paxA = { publicId: "aa-111", fullName: "Ana Lopez", documentNumber: "11111111" };
const paxB = { publicId: "bb-222", fullName: "Bruno Castro", documentNumber: "22222222" };
const paxC = { publicId: "cc-333", fullName: "", documentNumber: "" };

test("calcularSlotsFaltantesDelSet: coverage null → lista vacía", () => {
    const slots = calcularSlotsFaltantesDelSet(null, [paxA, paxB]);
    assert.equal(slots.length, 0);
});

test("calcularSlotsFaltantesDelSet: coverage completa → sin slots", () => {
    const coverage = coverageCompleta([paxA, paxB]);
    const slots = calcularSlotsFaltantesDelSet(coverage, [paxA, paxB]);
    assert.equal(slots.length, 0);
});

test("calcularSlotsFaltantesDelSet: 2 en el set, 1 sin datos → 1 slot faltante", () => {
    // Solo el pasajero B falta datos (index 1)
    const coverage = coverageIncompleta([paxA, paxB], [0]);
    const slots = calcularSlotsFaltantesDelSet(coverage, [paxA, paxB]);
    assert.equal(slots.length, 1);
    // El slot faltante es el pasajero B (Bruno Castro) — no el titular
    assert.equal(slots[0].slot, "Bruno Castro");
});

test("calcularSlotsFaltantesDelSet: titular faltante → slot es 'Titular'", () => {
    // El pasajero A (isLead=true, index=0) no tiene datos
    const coverage = coverageIncompleta([paxA, paxB], [1]);
    const slots = calcularSlotsFaltantesDelSet(coverage, [paxA, paxB]);
    assert.equal(slots.length, 1);
    assert.equal(slots[0].slot, "Titular");
});

test("calcularSlotsFaltantesDelSet: todos los del set faltan datos → todos los slots", () => {
    const coverage = coverageIncompleta([paxA, paxB], []);
    const slots = calcularSlotsFaltantesDelSet(coverage, [paxA, paxB]);
    assert.equal(slots.length, 2);
});

test("calcularSlotsFaltantesDelSet: el passenger completo del slot viene del array de la reserva", () => {
    // paxA está en el set pero falta datos.
    // El objeto completo (con publicId, documentNumber etc.) viene de pasajerosCompletos.
    const coverage = coverageIncompleta([paxA], []);
    const slots = calcularSlotsFaltantesDelSet(coverage, [paxA]);
    assert.equal(slots.length, 1);
    // El passenger en el slot debe ser el objeto completo (con documentNumber), no solo lo del coverage.
    assert.ok(slots[0].passenger, "debe tener el objeto pasajero completo");
    assert.equal(slots[0].passenger.publicId, paxA.publicId);
});

test("calcularSlotsFaltantesDelSet: pasajero sin nombre → etiqueta es 'Pasajero N'", () => {
    // paxC tiene fullName vacío → la etiqueta cae a "Pasajero 1" (no es titular: index > 0)
    const coverage = coverageIncompleta([paxA, paxC], [0]);
    const slots = calcularSlotsFaltantesDelSet(coverage, [paxA, paxC]);
    assert.equal(slots.length, 1);
    assert.ok(slots[0].slot.startsWith("Pasajero"), "etiqueta debe empezar con Pasajero");
});

// ─── Tests: calcularSugerenciaComposicion (Pieza C — ADR-031 v2.1) ────────────

const reserva1A1M = { adultCount: 1, childCount: 1, infantCount: 0 };
const reservaVacia = { adultCount: 0, childCount: 0, infantCount: 0 };

function readiness(adultos, menores, infantes, ambigua = false) {
    return {
        expectedAdults: adultos,
        expectedChildren: menores,
        expectedInfants: infantes,
        ambiguousComposition: ambigua,
    };
}

test("calcularSugerenciaComposicion: readiness null → null (sin sugerencia)", () => {
    assert.equal(calcularSugerenciaComposicion(null, reservaVacia), null);
});

test("calcularSugerenciaComposicion: todos en 0 → null (sin datos significativos)", () => {
    const sug = calcularSugerenciaComposicion(readiness(0, 0, 0), reservaVacia);
    assert.equal(sug, null);
});

test("calcularSugerenciaComposicion: sugerencia igual a la actual → null (ya coincide)", () => {
    // Reserva tiene 1 adulto + 1 menor, readiness sugiere lo mismo → sin franja
    const sug = calcularSugerenciaComposicion(readiness(1, 1, 0), reserva1A1M);
    assert.equal(sug, null);
});

test("calcularSugerenciaComposicion: sugerencia distinta → devuelve objeto con sugerida=true", () => {
    // Reserva tiene 1 adulto, readiness sugiere 2 adultos + 1 menor → hay sugerencia
    const sug = calcularSugerenciaComposicion(readiness(2, 1, 0), { adultCount: 1, childCount: 0, infantCount: 0 });
    assert.ok(sug, "debe devolver un objeto");
    assert.equal(sug.sugerida, true);
    assert.equal(sug.adultos, 2);
    assert.equal(sug.menores, 1);
    assert.equal(sug.infantes, 0);
});

test("calcularSugerenciaComposicion: reserva vacía (0/0/0) y sugerencia > 0 → franja aparece", () => {
    const sug = calcularSugerenciaComposicion(readiness(2, 0, 0), reservaVacia);
    assert.ok(sug, "debe devolver sugerencia");
    assert.equal(sug.adultos, 2);
});

test("calcularSugerenciaComposicion: ambiguousComposition se propaga al resultado", () => {
    const sug = calcularSugerenciaComposicion(readiness(2, 1, 0, true), reservaVacia);
    assert.ok(sug);
    assert.equal(sug.ambigua, true);
});

test("calcularSugerenciaComposicion: no ambigua → ambigua false", () => {
    const sug = calcularSugerenciaComposicion(readiness(2, 1, 0, false), reservaVacia);
    assert.ok(sug);
    assert.equal(sug.ambigua, false);
});
