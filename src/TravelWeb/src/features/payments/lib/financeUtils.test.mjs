/**
 * Tests de formatDate() de financeUtils.js — bug "fechas corridas un día"
 * (dueño, 2026-07-16), sumado al alcance porque este es el módulo de PLATA:
 * acá un día corrido es grave (puede cambiar a qué mes/cobranza pertenece un
 * movimiento en Cobranzas, Facturación o el panel de Pagos).
 *
 * Este archivo importa el módulo REAL (financeUtils.js no tiene JSX). formatDate()
 * ahora delega en la formatDate() central de utils.js — se testea el contrato
 * completo igual, para blindar el comportamiento visible a quien use este módulo.
 *
 * Cómo correr: node --test src/features/payments/lib/financeUtils.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { buildMovementRowKey, formatDate } from "./financeUtils.js";

// ─── Caso 1: fecha-solo-día (ej. item.startDate de un viaje en Cobranzas) ────

test("formatDate: fecha-solo-día 'YYYY-MM-DD' → mismo día, sin corrimiento", () => {
    assert.equal(formatDate("2026-05-23"), "23/05/2026");
});

test("formatDate: medianoche UTC con 'Z' → mismo día calendario del string", () => {
    assert.equal(formatDate("2026-05-23T00:00:00Z"), "23/05/2026");
});

test("formatDate: medianoche UTC con milisegundos '.000Z' → mismo día", () => {
    assert.equal(formatDate("2026-05-23T00:00:00.000Z"), "23/05/2026");
});

// ─── Caso 2: instante real con hora (ej. invoice.createdAt) — sin cambios ────

test("formatDate: timestamp real con hora → se sigue mostrando en hora local", () => {
    const esperado = new Date("2026-05-23T14:30:00Z").toLocaleDateString("es-AR", {
        day: "2-digit", month: "2-digit", year: "numeric",
    });
    assert.equal(formatDate("2026-05-23T14:30:00Z"), esperado);
});

// ─── Casos vacíos ────────────────────────────────────────────────────────────

test("formatDate: null/undefined/cadena vacía → '-'", () => {
    assert.equal(formatDate(null), "-");
    assert.equal(formatDate(undefined), "-");
    assert.equal(formatDate(""), "-");
});

// ─── Casos borde de calendario (mismos que utils.test.mjs) ──────────────────

test("formatDate: fin de mes (31 de mayo, medianoche UTC) → no salta a junio", () => {
    assert.equal(formatDate("2026-05-31T00:00:00Z"), "31/05/2026");
});

test("formatDate: fin de año (31 de diciembre, medianoche UTC) → no salta al año siguiente", () => {
    assert.equal(formatDate("2026-12-31T00:00:00Z"), "31/12/2026");
});

test("formatDate: 1 de enero (medianoche UTC) → no retrocede al 31 de diciembre anterior", () => {
    // Caso exacto del bug original: en UTC-3 el 1/1 00:00 UTC cae el 31/12 a las 21:00 local.
    assert.equal(formatDate("2026-01-01T00:00:00Z"), "01/01/2026");
});

test("formatDate: 29 de febrero en año bisiesto (medianoche UTC) → se muestra correctamente", () => {
    assert.equal(formatDate("2028-02-29T00:00:00Z"), "29/02/2028");
});

// ─── options explícito: se preserva el comportamiento viejo (compatibilidad) ─

test("formatDate: con options explícito → usa Intl.DateTimeFormat, no el string-split", () => {
    // Ningún call site actual pasa options, pero si alguno lo hiciera en el futuro,
    // respetamos el comportamiento anterior (Intl vía toLocaleDateString) en vez de
    // forzar el formato fijo DD/MM/AAAA de la función central.
    const resultado = formatDate("2026-05-23T14:30:00Z", { day: "numeric", month: "long", year: "numeric" });
    assert.equal(resultado, new Date("2026-05-23T14:30:00Z").toLocaleDateString("es-AR", {
        day: "numeric", month: "long", year: "numeric",
    }));
});

// ─── Ida y vuelta — caso real de Cobranzas/Panel de pagos ───────────────────

test("formatDate: ida y vuelta — la fecha de salida de un viaje se muestra sin corrimiento", () => {
    // Simula item.startDate tal cual lo devuelve el backend en Cobranzas/InvoicingTab/PaymentsHomePage.
    const fechaDeSalidaDelViaje = "2026-05-23";
    const respuestaSimuladaDelBackend = `${fechaDeSalidaDelViaje}T00:00:00Z`;
    assert.equal(formatDate(respuestaSimuladaDelBackend), "23/05/2026");
});

// ─────────────────────────────────────────────────────────────────────────────
// H4 (2026-07-25): buildMovementRowKey — keys únicas en la lista de Caja
// (MovementsTab + su versión mobile). Bug: un movimiento manual y su
// contra-asiento comparten sourceType+sourcePublicId (misma fila fantasma al
// filtrar, porque React confundía las dos filas con la misma key).
// ─────────────────────────────────────────────────────────────────────────────

test("buildMovementRowKey: movimiento manual normal → key estable con sus datos", () => {
    const movimiento = { sourceType: "ManualAdjustment", sourcePublicId: "abc-123", direction: "Income" };
    assert.equal(buildMovementRowKey(movimiento, 0), "ManualAdjustment-abc-123-Income-0");
});

test("buildMovementRowKey: manual + su contra-asiento (mismo sourcePublicId) → keys DISTINTAS", () => {
    // Caso real del bug: la reversa comparte sourceType y sourcePublicId con el original,
    // pero el backend SIEMPRE invierte el Direction (ver CashLedgerEntryFactory.Reverse).
    const original = { sourceType: "ManualAdjustment", sourcePublicId: "abc-123", direction: "Income" };
    const contraAsiento = { sourceType: "ManualAdjustment", sourcePublicId: "abc-123", direction: "Expense" };
    const keyOriginal = buildMovementRowKey(original, 0);
    const keyContraAsiento = buildMovementRowKey(contraAsiento, 1);
    assert.notEqual(keyOriginal, keyContraAsiento, "las dos filas de un mismo movimiento manual no pueden compartir key");
});

test("buildMovementRowKey: dos movimientos distintos con distinto sourcePublicId → keys distintas", () => {
    const movimientoA = { sourceType: "CustomerPayment", sourcePublicId: "pago-1", direction: "Income" };
    const movimientoB = { sourceType: "CustomerPayment", sourcePublicId: "pago-2", direction: "Income" };
    assert.notEqual(buildMovementRowKey(movimientoA, 0), buildMovementRowKey(movimientoB, 1));
});

test("buildMovementRowKey: el índice desempata como último recurso si todo lo demás coincide", () => {
    // Escenario hipotético (no debería pasar con los datos reales de hoy), pero la key
    // tiene que seguir siendo única para que React nunca confunda dos filas.
    const movimiento = { sourceType: "ManualAdjustment", sourcePublicId: "abc-123", direction: "Income" };
    assert.notEqual(buildMovementRowKey(movimiento, 0), buildMovementRowKey(movimiento, 1));
});
