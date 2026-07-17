/**
 * Tests de formatTripDate() — bug "fechas corridas un día" (dueño, 2026-07-16).
 *
 * formatTripDate() muestra la salida/regreso del viaje en el header de la reserva.
 * Antes usaba new Date(value).toLocaleDateString() en hora LOCAL, y como el backend
 * guarda esas fechas como medianoche UTC, en Argentina (UTC-3) el día se corría uno
 * hacia atrás (ej: salida real 23/05, la pantalla mostraba 22/05).
 *
 * ReservaHeader.jsx tiene JSX (no se puede importar directo en node --test), así que
 * copiamos la función — mismo patrón que reprogramarViajeModal.test.mjs.
 * Si cambia allá, actualizar acá.
 *
 * Cómo correr: node --test src/features/reservas/components/reservaHeaderTripDate.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de ReservaHeader.jsx ─────────────────────────────────

function formatTripDate(value) {
    if (!value) return null;
    const soloFecha = String(value).split("T")[0];
    const partes = soloFecha.split("-");
    if (partes.length !== 3) return null;
    const [anio, mes, dia] = partes;
    if (!anio || !mes || !dia) return null;
    return `${dia}/${mes}/${anio}`;
}

// ─── Tests ─────────────────────────────────────────────────────────────────

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

test("formatTripDate: null → null (el caller muestra 'sin cargar')", () => {
    assert.equal(formatTripDate(null), null);
});

test("formatTripDate: cadena vacía → null", () => {
    assert.equal(formatTripDate(""), null);
});

test("formatTripDate: texto sin forma de fecha (sin guiones) → null", () => {
    // La función valida FORMA (3 partes separadas por "-"), no que sean números — mismo
    // criterio liviano que formatearFechaLegible en ReprogramarViajeModal. Un texto con
    // dos guiones (ej. "no-es-fecha") pasaría el chequeo de forma; eso es aceptable
    // porque el dato siempre viene del backend en formato ISO real, nunca de un texto libre.
    assert.equal(formatTripDate("textoInvalido"), null);
});
