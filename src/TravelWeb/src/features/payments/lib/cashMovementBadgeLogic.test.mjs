/**
 * Tests del badge "Reemplazado"/"Anulado" del Libro de Caja (Obra 2, firma 2026-07-27).
 *
 * Cómo correr: node --test src/features/payments/lib/cashMovementBadgeLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  obtenerEstadoBadgeMovimiento,
  debeApagarBotonesMovimiento,
  esMovimientoDeReembolsoOperador,
  construirMotivoReembolsoOperadorApagado,
} from "./cashMovementBadgeLogic.js";

// ─── obtenerEstadoBadgeMovimiento ────────────────────────────────────────────────

test("isReplaced=true -> badge 'Reemplazado' con su motivo propio y estado='reemplazado'", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isReplaced: true, isAnnulled: false });
  assert.deepEqual(resultado, {
    etiqueta: "Reemplazado",
    estado: "reemplazado",
    motivoBotonesApagados: "Fue reemplazado por una edición, no se puede editar ni anular.",
  });
});

test("isReplaced=true e isAnnulled=true (el reemplazo se implementa como anular+crear puertas adentro) -> gana 'Reemplazado'", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isReplaced: true, isAnnulled: true });
  assert.equal(resultado.etiqueta, "Reemplazado");
});

test("isAnnulled=true sin isReplaced -> sigue siendo 'Anulado' como hoy, estado='anulado'", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isReplaced: false, isAnnulled: true });
  assert.deepEqual(resultado, {
    etiqueta: "Anulado",
    estado: "anulado",
    motivoBotonesApagados: "Ya está anulado, no se puede editar ni anular de nuevo.",
  });
});

test("ni reemplazado ni anulado -> no hay badge (null)", () => {
  assert.equal(obtenerEstadoBadgeMovimiento({ isReplaced: false, isAnnulled: false }), null);
});

test("fila vieja sin isReplaced en el DTO (undefined) y sin isAnnulled -> comportamiento actual, sin badge", () => {
  assert.equal(obtenerEstadoBadgeMovimiento({ isAnnulled: false }), null);
});

test("fila vieja sin isReplaced en el DTO (undefined) pero isAnnulled=true -> comportamiento actual, badge 'Anulado' (fallback)", () => {
  const resultado = obtenerEstadoBadgeMovimiento({ isAnnulled: true });
  assert.equal(resultado.etiqueta, "Anulado");
});

test("movement null/undefined -> no rompe, sin badge", () => {
  assert.equal(obtenerEstadoBadgeMovimiento(null), null);
  assert.equal(obtenerEstadoBadgeMovimiento(undefined), null);
});

// ─── debeApagarBotonesMovimiento ─────────────────────────────────────────────────

test("isReplaced=true -> apaga los botones", () => {
  assert.equal(debeApagarBotonesMovimiento({ isReplaced: true }), true);
});

test("isAnnulled=true -> apaga los botones", () => {
  assert.equal(debeApagarBotonesMovimiento({ isAnnulled: true }), true);
});

test("ninguno de los dos -> botones habilitados", () => {
  assert.equal(debeApagarBotonesMovimiento({ isReplaced: false, isAnnulled: false }), false);
});

test("movement null/undefined -> botones habilitados, no rompe", () => {
  assert.equal(debeApagarBotonesMovimiento(null), false);
  assert.equal(debeApagarBotonesMovimiento(undefined), false);
});

// ─── Bloque 3 "descalce devolución-caja" (2026-08-19): freno para category=OperatorRefund ──

test("esMovimientoDeReembolsoOperador: category='OperatorRefund' -> true", () => {
  assert.equal(esMovimientoDeReembolsoOperador({ category: "OperatorRefund" }), true);
});

test("esMovimientoDeReembolsoOperador: cualquier otra categoría -> false", () => {
  assert.equal(esMovimientoDeReembolsoOperador({ category: "ManualAdjustment" }), false);
  assert.equal(esMovimientoDeReembolsoOperador({ category: "SupplierPayment" }), false);
});

test("esMovimientoDeReembolsoOperador: movement null/undefined -> false, no rompe", () => {
  assert.equal(esMovimientoDeReembolsoOperador(null), false);
  assert.equal(esMovimientoDeReembolsoOperador(undefined), false);
});

test("debeApagarBotonesMovimiento: category='OperatorRefund' apaga los botones aunque no esté reemplazado ni anulado", () => {
  const movimiento = { isReplaced: false, isAnnulled: false, category: "OperatorRefund" };
  assert.equal(debeApagarBotonesMovimiento(movimiento), true);
});

test("debeApagarBotonesMovimiento: OperatorRefund se suma a las causas existentes, no las reemplaza", () => {
  // Un movimiento reemplazado sigue apagando los botones aunque NO sea de reembolso.
  assert.equal(debeApagarBotonesMovimiento({ isReplaced: true, category: "ManualAdjustment" }), true);
});

test("construirMotivoReembolsoOperadorApagado: texto EXACTO de la spec con el número de reserva interpolado", () => {
  const texto = construirMotivoReembolsoOperadorApagado({ numeroReserva: "F-2026-1189" });
  assert.equal(
    texto,
    "Atado a la devolución recibida del operador de la reserva F-2026-1189. Se corrige desde " +
      "el circuito de la devolución (solapa Reembolsos), no acá."
  );
});

test("construirMotivoReembolsoOperadorApagado: sin numeroReserva no rompe (string vacío en su lugar)", () => {
  const texto = construirMotivoReembolsoOperadorApagado({});
  assert.match(texto, /de la reserva \. Se corrige/);
});
