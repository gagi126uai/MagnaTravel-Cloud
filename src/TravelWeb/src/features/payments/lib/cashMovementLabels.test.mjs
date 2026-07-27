/**
 * Tests de los mapas criollo del Libro de Caja (hallazgos menores, firma 2026-07-27).
 *
 * Cómo correr: node --test src/features/payments/lib/cashMovementLabels.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  esCategoriaDeSistema,
  mapearCategoriaMovimiento,
  mapearMetodoMovimiento,
} from "./cashMovementLabels.js";

// ─── esCategoriaDeSistema / mapearCategoriaMovimiento ────────────────────────────

test("ClientCreditWithdrawal -> es de sistema, se traduce a 'Devolución de saldo al cliente'", () => {
  assert.equal(esCategoriaDeSistema("ClientCreditWithdrawal"), true);
  assert.equal(mapearCategoriaMovimiento("ClientCreditWithdrawal"), "Devolución de saldo al cliente");
});

test("ClientCreditReversal -> es de sistema, se traduce a 'Contra-asiento de devolución'", () => {
  assert.equal(esCategoriaDeSistema("ClientCreditReversal"), true);
  assert.equal(mapearCategoriaMovimiento("ClientCreditReversal"), "Contra-asiento de devolución");
});

// Fix bloqueante de data-exposure (2026-07-27): faltaba esta TERCERA categoría de
// sistema — las filas de reembolso del operador (ManualCashMovementBuilder.cs:119,
// BuildIncomeForRefund) mostraban "OperatorRefund" crudo Y editable en el modal.
test("OperatorRefund -> es de sistema, se traduce a 'Devolución recibida del operador'", () => {
  assert.equal(esCategoriaDeSistema("OperatorRefund"), true);
  assert.equal(mapearCategoriaMovimiento("OperatorRefund"), "Devolución recibida del operador");
  assert.notEqual(mapearCategoriaMovimiento("OperatorRefund"), "OperatorRefund");
});

test("categoría manual (texto libre del usuario) -> NO es de sistema, se devuelve tal cual", () => {
  assert.equal(esCategoriaDeSistema("Ajuste de caja chica"), false);
  assert.equal(mapearCategoriaMovimiento("Ajuste de caja chica"), "Ajuste de caja chica");
});

test("categoría null/undefined/vacía -> no rompe", () => {
  assert.equal(esCategoriaDeSistema(null), false);
  assert.equal(esCategoriaDeSistema(undefined), false);
  assert.equal(mapearCategoriaMovimiento(null), "");
  assert.equal(mapearCategoriaMovimiento(undefined), "");
  assert.equal(mapearCategoriaMovimiento(""), "");
});

// Test-guardia (barato, pedido por el reviewer): lista EXPLÍCITA de las 3 categorías que
// hoy genera el motor sin intervención del usuario (ManualCashMovementBuilder.cs, los
// dos builders: BuildExpenseForWithdrawal + BuildIncomeForRefund). Si el motor suma una
// categoría de sistema nueva algún día, este test NO la conoce todavía — hay que sumarla
// acá Y a CATEGORIAS_DE_SISTEMA (si no, ese hallazgo de "token crudo editable" se repite).
test("test-guardia: las 3 categorías que arma el motor hoy están TODAS mapeadas como de sistema", () => {
  const categoriasQueArmaElMotor = ["ClientCreditWithdrawal", "ClientCreditReversal", "OperatorRefund"];

  for (const categoria of categoriasQueArmaElMotor) {
    assert.equal(esCategoriaDeSistema(categoria), true, `"${categoria}" debería ser de sistema`);
    assert.notEqual(
      mapearCategoriaMovimiento(categoria),
      categoria,
      `"${categoria}" no debería mostrarse crudo`
    );
  }
});

// ─── mapearMetodoMovimiento ───────────────────────────────────────────────────────

test("Cash -> Efectivo", () => {
  assert.equal(mapearMetodoMovimiento("Cash"), "Efectivo");
});

test("Transfer -> Transferencia", () => {
  assert.equal(mapearMetodoMovimiento("Transfer"), "Transferencia");
});

test("Card -> Tarjeta (reusa traducirMetodoPago, cubre un caso que el mapa viejo no tenía)", () => {
  assert.equal(mapearMetodoMovimiento("Card"), "Tarjeta");
});

test("método ya en español (cargado a mano) -> se devuelve tal cual", () => {
  assert.equal(mapearMetodoMovimiento("Cheque"), "Cheque");
  assert.equal(mapearMetodoMovimiento("Transferencia"), "Transferencia");
});

test("método libre no reconocido (texto tipeado a mano en un ajuste manual) -> se muestra tal cual, NUNCA vacío (a diferencia de traducirMetodoPago a secas)", () => {
  assert.equal(mapearMetodoMovimiento("MercadoPago"), "MercadoPago");
  assert.equal(mapearMetodoMovimiento("Transfer-BBVA"), "Transfer-BBVA");
});

test("fix bloqueante (review): 'Other'/'Otro' (en cualquier capitalización conocida) -> 'Otro', NUNCA el token crudo en inglés ni una celda vacía", () => {
  assert.equal(mapearMetodoMovimiento("Other"), "Otro");
  assert.equal(mapearMetodoMovimiento("Otro"), "Otro");
  assert.equal(mapearMetodoMovimiento("other"), "Otro");
  assert.equal(mapearMetodoMovimiento("otro"), "Otro");
  assert.notEqual(mapearMetodoMovimiento("Other"), "Other");
  assert.notEqual(mapearMetodoMovimiento("Other"), "");
});

test("método null/undefined/vacío -> no rompe", () => {
  assert.equal(mapearMetodoMovimiento(null), "");
  assert.equal(mapearMetodoMovimiento(undefined), "");
  assert.equal(mapearMetodoMovimiento(""), "");
});
