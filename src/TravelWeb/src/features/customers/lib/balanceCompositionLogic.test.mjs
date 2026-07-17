/**
 * Tests de la lógica pura de la "foto de saldo" de la cuenta del cliente (Tanda D2,
 * 2026-07-16).
 *
 * Cómo correr: node --test src/features/customers/lib/balanceCompositionLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  construirFotoDeSaldo,
  debeMostrarBotonUsarSaldo,
} from "./balanceCompositionLogic.js";

// ============================================================================
// Estado "vacio": el cliente no tiene ningún dato de composición
// ============================================================================

test("composicion vacía → estado 'vacio', sin filas", () => {
  const foto = construirFotoDeSaldo([]);
  assert.strictEqual(foto.estado, "vacio");
  assert.deepEqual(foto.monedas, []);
  assert.deepEqual(foto.filas, []);
});

test("composicion null/undefined → estado 'vacio' (nunca revienta)", () => {
  assert.strictEqual(construirFotoDeSaldo(null).estado, "vacio");
  assert.strictEqual(construirFotoDeSaldo(undefined).estado, "vacio");
});

// ============================================================================
// Estado "alDia": todo en cero en todas las monedas presentes
// ============================================================================

test("todos los componentes en 0 en la única moneda → estado 'alDia'", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 0, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 0 },
  ]);
  assert.strictEqual(foto.estado, "alDia");
});

test("saldo en 0 pero con crédito a favor > 0 → NO es 'alDia' (hay algo que mostrar)", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 0, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 500, saldo: -500 },
  ]);
  assert.strictEqual(foto.estado, "conDatos");
});

// ============================================================================
// Estado "conDatos": arma filas y saldo por moneda
// ============================================================================

test("'Facturado sin cobrar' y 'SALDO' están SIEMPRE presentes", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 180000, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 180000 },
  ]);
  const claves = foto.filas.map((f) => f.clave);
  assert.ok(claves.includes("facturadoSinCobrar"));
  assert.ok(foto.saldoPorMoneda.ARS);
});

test("'Multas abiertas' NO aparece si ninguna moneda tiene multa", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 100, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 100 },
  ]);
  assert.ok(!foto.filas.some((f) => f.clave === "multasAbiertas"));
});

test("'Multas abiertas' SÍ aparece si al menos una moneda tiene multa > 0", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 180000, multasAbiertas: 20000, multasEnTramite: 0, creditoAFavor: 0, saldo: 200000 },
    { currency: "USD", facturadoSinCobrar: 300, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 50, saldo: 250 },
  ]);
  const filaMultas = foto.filas.find((f) => f.clave === "multasAbiertas");
  assert.ok(filaMultas, "La fila debe existir porque ARS tiene multa");
  // La celda de USD (que no tiene multa) queda en blanco, no en $0 ni oculta.
  assert.strictEqual(filaMultas.porMoneda.USD.montoTexto, "—");
  assert.notStrictEqual(filaMultas.porMoneda.ARS.montoTexto, "—");
});

test("'(incluye $X en trámite)' solo aparece en la moneda que tiene parte sin comprobante", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 0, multasAbiertas: 25000, multasEnTramite: 5000, creditoAFavor: 0, saldo: 25000 },
  ]);
  const filaMultas = foto.filas.find((f) => f.clave === "multasAbiertas");
  assert.ok(filaMultas.notaTramitePorMoneda.ARS.includes("trámite"));
});

test("'Crédito a favor' NO aparece si nadie tiene crédito", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 100, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 100 },
  ]);
  assert.ok(!foto.filas.some((f) => f.clave === "creditoAFavor"));
});

test("'Crédito a favor' se muestra con signo negativo (resta)", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 100000, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 15000, saldo: 85000 },
  ]);
  const filaCredito = foto.filas.find((f) => f.clave === "creditoAFavor");
  assert.ok(filaCredito.porMoneda.ARS.montoTexto.startsWith("−"));
});

test("SALDO positivo → tono rose, etiqueta 'debe'", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 100, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 100 },
  ]);
  assert.strictEqual(foto.saldoPorMoneda.ARS.tono, "rose");
  assert.strictEqual(foto.saldoPorMoneda.ARS.etiqueta, "debe");
});

test("SALDO negativo → tono emerald, etiqueta 'a favor'", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 0, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 500, saldo: -500 },
  ]);
  assert.strictEqual(foto.saldoPorMoneda.ARS.tono, "emerald");
  assert.strictEqual(foto.saldoPorMoneda.ARS.etiqueta, "a favor");
});

test("dos monedas nunca se mezclan: cada una tiene su propia fila de saldo", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 180000, multasAbiertas: 20000, multasEnTramite: 0, creditoAFavor: 15000, saldo: 185000 },
    { currency: "USD", facturadoSinCobrar: 300, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 50, saldo: 250 },
  ]);
  assert.deepEqual(foto.monedas, ["ARS", "USD"]);
  assert.strictEqual(foto.saldoPorMoneda.ARS.monto, 185000);
  assert.strictEqual(foto.saldoPorMoneda.USD.monto, 250);
});

// ============================================================================
// Crédito no aplicado (fix de revisión 2026-07-17, spec §7.3): nota chica bajo
// "Crédito a favor" — reemplaza al viejo cartel suelto "CRÉDITO NO APLICADO".
// ============================================================================

test("con crédito no aplicado, la fila 'Crédito a favor' trae la nota chica en esa moneda", () => {
  const foto = construirFotoDeSaldo(
    [{ currency: "ARS", facturadoSinCobrar: 100000, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 15000, saldo: 85000 }],
    [{ currency: "ARS", amount: 5000 }]
  );
  const filaCredito = foto.filas.find((f) => f.clave === "creditoAFavor");
  assert.ok(filaCredito, "La fila 'Crédito a favor' debe existir");
  assert.ok(filaCredito.notaNoAplicadoPorMoneda.ARS.includes("sin aplicar"));
});

test("sin crédito no aplicado (0 o ausente), la nota queda null y no se inventa texto", () => {
  const foto = construirFotoDeSaldo(
    [{ currency: "ARS", facturadoSinCobrar: 100000, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 15000, saldo: 85000 }],
    []
  );
  const filaCredito = foto.filas.find((f) => f.clave === "creditoAFavor");
  assert.strictEqual(filaCredito.notaNoAplicadoPorMoneda.ARS, null);
});

test("crédito a favor consumible en 0, pero con crédito NO aplicado > 0 → la fila 'Crédito a favor' igual aparece", () => {
  const foto = construirFotoDeSaldo(
    [{ currency: "ARS", facturadoSinCobrar: 50000, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 50000 }],
    [{ currency: "ARS", amount: 2000 }]
  );
  const filaCredito = foto.filas.find((f) => f.clave === "creditoAFavor");
  assert.ok(filaCredito, "La fila debe existir aunque el pool consumible esté en 0, porque hay algo sin aplicar");
  assert.ok(filaCredito.notaNoAplicadoPorMoneda.ARS.includes("sin aplicar"));
});

test("todo en 0 pero con crédito no aplicado > 0 → el estado NO es 'alDia' (hay algo que mostrar)", () => {
  const foto = construirFotoDeSaldo(
    [{ currency: "ARS", facturadoSinCobrar: 0, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 0 }],
    [{ currency: "ARS", amount: 300 }]
  );
  assert.strictEqual(foto.estado, "conDatos");
});

test("todo en 0 y sin crédito no aplicado → sigue siendo 'alDia' (no rompe el caso normal)", () => {
  const foto = construirFotoDeSaldo(
    [{ currency: "ARS", facturadoSinCobrar: 0, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 0, saldo: 0 }],
    []
  );
  assert.strictEqual(foto.estado, "alDia");
});

test("unappliedCreditByCurrency ausente (caller viejo) no revienta", () => {
  const foto = construirFotoDeSaldo([
    { currency: "ARS", facturadoSinCobrar: 100000, multasAbiertas: 0, multasEnTramite: 0, creditoAFavor: 15000, saldo: 85000 },
  ]);
  const filaCredito = foto.filas.find((f) => f.clave === "creditoAFavor");
  assert.strictEqual(filaCredito.notaNoAplicadoPorMoneda.ARS, null);
});

// ============================================================================
// debeMostrarBotonUsarSaldo
// ============================================================================

test("debeMostrarBotonUsarSaldo - con crédito y permiso → true", () => {
  assert.strictEqual(debeMostrarBotonUsarSaldo({ creditoAFavor: 500, canUsarSaldo: true }), true);
});

test("debeMostrarBotonUsarSaldo - sin crédito → false aunque tenga permiso", () => {
  assert.strictEqual(debeMostrarBotonUsarSaldo({ creditoAFavor: 0, canUsarSaldo: true }), false);
});

test("debeMostrarBotonUsarSaldo - con crédito pero sin permiso → false", () => {
  assert.strictEqual(debeMostrarBotonUsarSaldo({ creditoAFavor: 500, canUsarSaldo: false }), false);
});
