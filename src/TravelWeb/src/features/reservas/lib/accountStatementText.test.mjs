/**
 * Tests de la frase de cierre del extracto de cuenta (Tanda 4, 2026-08-04,
 * maqueta sección 9).
 *
 * Los montos esperados se arman llamando a formatCurrency() en vez de
 * tipearlos a mano: Intl.NumberFormat("es-AR", ...) mete un espacio DURO
 * (U+00A0, no el espacio normal U+0020) entre "$" y el número — tipear el
 * string a mano rompía el test aunque se viera idéntico al loguearlo.
 *
 * Cómo correr:
 *   node --test src/features/reservas/lib/accountStatementText.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { construirFraseResumenSaldos } from "./accountStatementText.js";
import { formatCurrency } from "../../../lib/utils.js";

test("un bloque en pesos con deuda → 'Este cliente debe $X.'", () => {
  const frase = construirFraseResumenSaldos([{ currency: "ARS", closingBalance: 72000 }]);
  assert.equal(frase, `Este cliente debe ${formatCurrency(72000, "ARS")}.`);
});

test("un bloque en pesos saldado en cero → 'no debe nada en pesos'", () => {
  const frase = construirFraseResumenSaldos([{ currency: "ARS", closingBalance: 0 }]);
  assert.equal(frase, "Este cliente no debe nada en pesos.");
});

test("un bloque con saldo negativo (a favor del cliente) → 'tiene $X a favor'", () => {
  const frase = construirFraseResumenSaldos([{ currency: "ARS", closingBalance: -4000 }]);
  assert.equal(frase, `Este cliente tiene ${formatCurrency(4000, "ARS")} a favor.`);
});

test("dos monedas: saldado en pesos + deuda en dólares → frase del ejemplo de la maqueta", () => {
  const frase = construirFraseResumenSaldos([
    { currency: "ARS", closingBalance: 0 },
    { currency: "USD", closingBalance: 300 },
  ]);
  assert.equal(frase, `Este cliente no debe nada en pesos y debe ${formatCurrency(300, "USD")}.`);
});

test("dos monedas, ambas con plata en juego → se unen con 'y' (sin coma, son solo dos)", () => {
  const frase = construirFraseResumenSaldos([
    { currency: "ARS", closingBalance: 1000 },
    { currency: "USD", closingBalance: -50 },
  ]);
  assert.equal(
    frase,
    `Este cliente debe ${formatCurrency(1000, "ARS")} y tiene ${formatCurrency(50, "USD")} a favor.`
  );
});

test("moneda desconocida (no ARS/USD) saldada en cero → usa el código ISO tal cual, no inventa un nombre", () => {
  // Se prueba con saldo CERO a propósito: formatCurrency() no tiene un formato dedicado
  // para monedas fuera de ARS/USD (cae a un formato legacy en dólares) — este test
  // solo verifica el NOMBRE de la moneda en la frase, no el formato de un monto.
  const frase = construirFraseResumenSaldos([{ currency: "EUR", closingBalance: 0 }]);
  assert.equal(frase, "Este cliente no debe nada en EUR.");
});

test("closingBalance null/undefined → se trata como 0 (saldado), no revienta", () => {
  const frase = construirFraseResumenSaldos([{ currency: "ARS", closingBalance: null }]);
  assert.equal(frase, "Este cliente no debe nada en pesos.");
});

test("lista vacía → devuelve cadena vacía, no revienta", () => {
  assert.equal(construirFraseResumenSaldos([]), "");
});

test("bloques undefined → devuelve cadena vacía, no revienta (degradación elegante)", () => {
  assert.equal(construirFraseResumenSaldos(undefined), "");
});
