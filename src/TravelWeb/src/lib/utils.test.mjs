/**
 * Tests de formatDate() — bug "fechas corridas un día" (reportado por el dueño 2026-07-16).
 *
 * Este archivo importa el módulo REAL (utils.js no tiene JSX, así que a diferencia de
 * otros tests .mjs del proyecto no hace falta copiar la lógica). Mismo patrón que
 * moneyStatus.test.mjs.
 *
 * Reporte del bug: el dueño cargó "23/05/2026" en un input de fecha y la pantalla le
 * mostró "22/05/2026". Causa raíz: el backend guarda las fechas-solo-día (sin hora,
 * ej. fecha de salida de un viaje) como medianoche UTC. formatDate() las pasaba por
 * new Date(...).toLocaleDateString() en hora LOCAL del navegador — en Argentina
 * (UTC-3) la medianoche UTC del día 23 cae a las 21:00 del día 22, así que se veía
 * el día anterior.
 *
 * Cómo correr: node --test src/lib/utils.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { formatDate } from "./utils.js";

// ─── Caso 1: fecha-solo-día cruda del input (sin pasar por el backend) ───────

test("formatDate: fecha-solo-día 'YYYY-MM-DD' → mismo día, sin corrimiento", () => {
    assert.equal(formatDate("2026-05-23"), "23/05/2026");
});

// ─── Caso 2: medianoche UTC devuelta por el backend (distintas variantes) ────

test("formatDate: medianoche UTC con 'Z' → mismo día calendario del string", () => {
    assert.equal(formatDate("2026-05-23T00:00:00Z"), "23/05/2026");
});

test("formatDate: medianoche UTC con milisegundos '.000Z' → mismo día", () => {
    assert.equal(formatDate("2026-05-23T00:00:00.000Z"), "23/05/2026");
});

test("formatDate: medianoche UTC con 7 dígitos de fracción (round-trip .NET) → mismo día", () => {
    // System.Text.Json puede serializar con hasta 7 dígitos de fracción de segundo.
    assert.equal(formatDate("2026-05-23T00:00:00.0000000Z"), "23/05/2026");
});

test("formatDate: medianoche SIN sufijo Z (Kind=Unspecified) → mismo día", () => {
    assert.equal(formatDate("2026-05-23T00:00:00"), "23/05/2026");
});

// ─── Caso 3: instante real con hora (NO debe tocarse — sigue en hora local) ──

test("formatDate: timestamp real con hora → se sigue mostrando en hora local, sin cambios", () => {
    // createdAt de una factura, por ejemplo: tiene una hora real, no es medianoche.
    // El comportamiento acá es el de SIEMPRE (new Date().toLocaleDateString()).
    const esperado = new Date("2026-05-23T14:30:00Z").toLocaleDateString("es-AR", {
        day: "2-digit", month: "2-digit", year: "numeric",
    });
    assert.equal(formatDate("2026-05-23T14:30:00Z"), esperado);
});

test("formatDate: instante con offset explícito distinto de Z → comportamiento local (no es fecha-solo-día)", () => {
    const esperado = new Date("2026-05-23T10:00:00-03:00").toLocaleDateString("es-AR", {
        day: "2-digit", month: "2-digit", year: "numeric",
    });
    assert.equal(formatDate("2026-05-23T10:00:00-03:00"), esperado);
});

// ─── Casos vacíos / nulos ──────────────────────────────────────────────────

test("formatDate: null → '-'", () => {
    assert.equal(formatDate(null), "-");
});

test("formatDate: undefined → '-'", () => {
    assert.equal(formatDate(undefined), "-");
});

test("formatDate: cadena vacía → '-'", () => {
    assert.equal(formatDate(""), "-");
});

// ─── Casos borde de calendario pedidos explícitamente por el dueño ──────────
// (fin de mes, fin de año, 29/02 bisiesto — no solo el día, también mes y año)

test("formatDate: fin de mes (31 de mayo, medianoche UTC) → no salta a junio", () => {
    assert.equal(formatDate("2026-05-31T00:00:00Z"), "31/05/2026");
});

test("formatDate: fin de año (31 de diciembre, medianoche UTC) → no salta al año siguiente", () => {
    assert.equal(formatDate("2026-12-31T00:00:00Z"), "31/12/2026");
});

test("formatDate: 1 de enero (medianoche UTC) → no retrocede al 31 de diciembre anterior", () => {
    // Este es EXACTAMENTE el caso que rompía antes del fix: en UTC-3 el 1/1 00:00 UTC
    // cae el 31/12 a las 21:00 local — el bug original mostraba el año anterior.
    assert.equal(formatDate("2026-01-01T00:00:00Z"), "01/01/2026");
});

test("formatDate: 29 de febrero en año bisiesto (medianoche UTC) → se muestra correctamente", () => {
    assert.equal(formatDate("2028-02-29T00:00:00Z"), "29/02/2028");
});

// ─── Ida y vuelta: fecha del input → ISO simulado del backend → formatDate ──

test("formatDate: ida y vuelta completa — el día que el usuario tipeó es el día que ve", () => {
    // Simula: el usuario carga "23/05/2026" en el input (value="2026-05-23"),
    // el backend lo guarda y lo devuelve como medianoche UTC.
    const valorDelInput = "2026-05-23";
    const respuestaSimuladaDelBackend = `${valorDelInput}T00:00:00Z`;
    assert.equal(formatDate(respuestaSimuladaDelBackend), "23/05/2026");
});
