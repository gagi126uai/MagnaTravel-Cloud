/**
 * Tests de formatTripDate()/toDateInputValue() — bug "fechas corridas un día"
 * (dueño, 2026-07-16) + ADR-053 (2026-08-13, movida a este archivo puro para que
 * el test importe la función REAL en vez de copiarla a mano).
 *
 * Cómo correr: node --test src/features/reservas/lib/tripDateFormat.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { formatTripDate, toDateInputValue } from "./tripDateFormat.js";

// ─── formatTripDate ────────────────────────────────────────────────────────────

test("formatTripDate: medianoche UTC con Z → mismo día calendario", () => {
    assert.equal(formatTripDate("2026-05-23T00:00:00Z"), "23/05/2026");
});

test("formatTripDate: fecha-solo-día sin hora → mismo día", () => {
    assert.equal(formatTripDate("2026-05-23"), "23/05/2026");
});

test("formatTripDate: fin de mes (31/05) → no salta a junio", () => {
    assert.equal(formatTripDate("2026-05-31T00:00:00Z"), "31/05/2026");
});

test("formatTripDate: fin de año (31/12) → no salta al año siguiente", () => {
    assert.equal(formatTripDate("2026-12-31T00:00:00Z"), "31/12/2026");
});

test("formatTripDate: 1 de enero → no retrocede al 31/12 del año anterior", () => {
    // Caso exacto del bug original: en UTC-3 esto caía el 31/12 a las 21:00.
    assert.equal(formatTripDate("2026-01-01T00:00:00Z"), "01/01/2026");
});

test("formatTripDate: 29 de febrero bisiesto → se muestra correctamente", () => {
    assert.equal(formatTripDate("2028-02-29T00:00:00Z"), "29/02/2028");
});

test("formatTripDate: null → null (el caller muestra su propio texto de vacío)", () => {
    assert.equal(formatTripDate(null), null);
});

test("formatTripDate: cadena vacía → null", () => {
    assert.equal(formatTripDate(""), null);
});

test("formatTripDate: texto sin forma de fecha → null", () => {
    assert.equal(formatTripDate("textoInvalido"), null);
});

test("formatTripDate: fecha con mes/día de un solo dígito sin cero → null (formato estricto)", () => {
    // El regex exige 4-2-2 dígitos exactos; "2026-5-3" no matchea. El backend
    // siempre manda ISO con ceros, así que esto no pasa en el camino real.
    assert.equal(formatTripDate("2026-5-3"), null);
});

// ─── toDateInputValue ──────────────────────────────────────────────────────────

test("toDateInputValue: medianoche UTC con Z → yyyy-MM-dd para el <input type=date>", () => {
    assert.equal(toDateInputValue("2026-05-23T00:00:00Z"), "2026-05-23");
});

test("toDateInputValue: null → cadena vacía (input queda sin pre-rellenar)", () => {
    assert.equal(toDateInputValue(null), "");
});

test("toDateInputValue: texto sin forma de fecha → cadena vacía", () => {
    assert.equal(toDateInputValue("no-es-fecha"), "");
});
