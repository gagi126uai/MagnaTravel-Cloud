import { test } from "node:test";
import assert from "node:assert/strict";
import {
  SUPPLIER_PENALTY_BEHAVIOR,
  OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR,
  valorSelectDesdePenaltyBehavior,
  penaltyBehaviorDesdeValorSelect,
} from "./supplierPenaltyBehavior.js";

test("SUPPLIER_PENALTY_BEHAVIOR: Unknown=0, RarelyCharges=1, UsuallyCharges=2 (verificado en SupplierPenaltyBehavior.cs)", () => {
  assert.equal(SUPPLIER_PENALTY_BEHAVIOR.Unknown, 0);
  assert.equal(SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges, 1);
  assert.equal(SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges, 2);
});

test("OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR: 3 opciones, con los textos exactos de la spec 2026-07-14", () => {
  assert.equal(OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR.length, 3);
  assert.deepEqual(
    OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR.map((opcion) => opcion.label),
    ["Casi nunca cobra multa", "Casi siempre cobra multa", "No se sabe / depende de la tarifa"]
  );
});

test("OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR: 'No se sabe / depende de la tarifa' es la última opción (el default)", () => {
  const ultima = OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR[OPCIONES_COMPORTAMIENTO_MULTA_OPERADOR.length - 1];
  assert.equal(ultima.value, SUPPLIER_PENALTY_BEHAVIOR.Unknown);
});

test("valorSelectDesdePenaltyBehavior: mapea los 3 valores del backend tal cual", () => {
  assert.equal(valorSelectDesdePenaltyBehavior(SUPPLIER_PENALTY_BEHAVIOR.Unknown), SUPPLIER_PENALTY_BEHAVIOR.Unknown);
  assert.equal(valorSelectDesdePenaltyBehavior(SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges), SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges);
  assert.equal(valorSelectDesdePenaltyBehavior(SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges), SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges);
});

test("valorSelectDesdePenaltyBehavior: null/undefined (DTO viejo sin el campo) → Unknown ('no se sabe'), nunca inventa una excepción", () => {
  assert.equal(valorSelectDesdePenaltyBehavior(null), SUPPLIER_PENALTY_BEHAVIOR.Unknown);
  assert.equal(valorSelectDesdePenaltyBehavior(undefined), SUPPLIER_PENALTY_BEHAVIOR.Unknown);
});

test("valorSelectDesdePenaltyBehavior: valor fuera de rango (defensivo) → Unknown", () => {
  assert.equal(valorSelectDesdePenaltyBehavior(99), SUPPLIER_PENALTY_BEHAVIOR.Unknown);
});

test("penaltyBehaviorDesdeValorSelect: acepta el STRING que entrega un <select> del DOM", () => {
  assert.equal(penaltyBehaviorDesdeValorSelect("1"), SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges);
  assert.equal(penaltyBehaviorDesdeValorSelect("2"), SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges);
  assert.equal(penaltyBehaviorDesdeValorSelect("0"), SUPPLIER_PENALTY_BEHAVIOR.Unknown);
});

test("penaltyBehaviorDesdeValorSelect: valor fuera de rango (defensivo) → Unknown", () => {
  assert.equal(penaltyBehaviorDesdeValorSelect("99"), SUPPLIER_PENALTY_BEHAVIOR.Unknown);
  assert.equal(penaltyBehaviorDesdeValorSelect(""), SUPPLIER_PENALTY_BEHAVIOR.Unknown);
});

test("round-trip: valorSelectDesdePenaltyBehavior + penaltyBehaviorDesdeValorSelect son inversas", () => {
  for (const original of [
    SUPPLIER_PENALTY_BEHAVIOR.Unknown,
    SUPPLIER_PENALTY_BEHAVIOR.RarelyCharges,
    SUPPLIER_PENALTY_BEHAVIOR.UsuallyCharges,
  ]) {
    const select = valorSelectDesdePenaltyBehavior(original);
    assert.equal(penaltyBehaviorDesdeValorSelect(select), original);
  }
});
