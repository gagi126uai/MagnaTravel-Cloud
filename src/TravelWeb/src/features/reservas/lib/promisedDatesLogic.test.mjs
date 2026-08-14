/**
 * Tests de lógica pura de "fecha prometida al cliente" (ADR-053, spec UX 2026-08-13).
 *
 * Cómo correr: node --test src/features/reservas/lib/promisedDatesLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { hayDiscrepanciaFechaPrometida, tieneFechaPrometidaCargada } from "./promisedDatesLogic.js";

// ─── hayDiscrepanciaFechaPrometida (P8, marca ámbar cuando no coincide) ────────

test("Sin discrepancia: prometida y calculada son el mismo día (aunque con hora distinta)", () => {
    const resultado = hayDiscrepanciaFechaPrometida({
        startDate: "2027-02-10T00:00:00Z",
        endDate: "2027-02-15T00:00:00Z",
        promisedStartDate: "2027-02-10T03:00:00Z",
        promisedEndDate: "2027-02-15T00:00:00Z",
    });
    assert.equal(resultado, false);
});

test("Discrepancia: la salida prometida es dos días después de la calculada", () => {
    const resultado = hayDiscrepanciaFechaPrometida({
        startDate: "2027-02-10T00:00:00Z",
        endDate: "2027-02-15T00:00:00Z",
        promisedStartDate: "2027-02-12T00:00:00Z",
        promisedEndDate: "2027-02-17T00:00:00Z",
    });
    assert.equal(resultado, true);
});

test("Discrepancia: solo el regreso prometido difiere (la salida coincide)", () => {
    const resultado = hayDiscrepanciaFechaPrometida({
        startDate: "2027-02-10T00:00:00Z",
        endDate: "2027-02-15T00:00:00Z",
        promisedStartDate: "2027-02-10T00:00:00Z",
        promisedEndDate: "2027-02-20T00:00:00Z",
    });
    assert.equal(resultado, true);
});

test("Sin discrepancia: no hay ninguna fecha prometida cargada", () => {
    const resultado = hayDiscrepanciaFechaPrometida({
        startDate: "2027-02-10T00:00:00Z",
        endDate: "2027-02-15T00:00:00Z",
        promisedStartDate: null,
        promisedEndDate: null,
    });
    assert.equal(resultado, false);
});

test("Sin discrepancia: hay prometida pero todavía no hay NADA calculado (reserva sin servicios)", () => {
    // Suposición propia (documentada en promisedDatesLogic.js): sin fecha calculada
    // no hay nada con qué comparar, así que no se marca ámbar apenas se carga la
    // primera fecha prometida de una reserva que recién arranca.
    const resultado = hayDiscrepanciaFechaPrometida({
        startDate: null,
        endDate: null,
        promisedStartDate: "2027-02-12T00:00:00Z",
        promisedEndDate: "2027-02-17T00:00:00Z",
    });
    assert.equal(resultado, false);
});

test("Sin discrepancia: solo se cargó la fecha de salida prometida y coincide", () => {
    const resultado = hayDiscrepanciaFechaPrometida({
        startDate: "2027-02-10T00:00:00Z",
        endDate: "2027-02-15T00:00:00Z",
        promisedStartDate: "2027-02-10T00:00:00Z",
        promisedEndDate: null,
    });
    assert.equal(resultado, false);
});

// ─── tieneFechaPrometidaCargada (decide link "+" vs renglón ya cargado) ────────

test("Tiene fecha prometida cargada: solo la salida", () => {
    assert.equal(tieneFechaPrometidaCargada({ promisedStartDate: "2027-02-10T00:00:00Z", promisedEndDate: null }), true);
});

test("Tiene fecha prometida cargada: solo el regreso", () => {
    assert.equal(tieneFechaPrometidaCargada({ promisedStartDate: null, promisedEndDate: "2027-02-15T00:00:00Z" }), true);
});

test("No tiene fecha prometida cargada: las dos vacías", () => {
    assert.equal(tieneFechaPrometidaCargada({ promisedStartDate: null, promisedEndDate: null }), false);
});

test("No tiene fecha prometida cargada: reserva sin el campo (DTO viejo)", () => {
    assert.equal(tieneFechaPrometidaCargada({}), false);
});
