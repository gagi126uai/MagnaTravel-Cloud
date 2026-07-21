/**
 * Tests de lógica pura de la Tanda 7 (spec docs/ux/2026-07-20-t5-a-t9-contrato-pantalla-motor.md):
 * "Anular un servicio: pre-chequeo + candado R1 con camino servido".
 *
 * Corren con Node puro sin bundler: node --test src/features/reservas/lib/serviceCancellationGuard.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  resolverBloqueoAnularServicio,
  resolverRechazoAnularServicio,
  CODIGO_RECHAZO_ANULAR_SERVICIO,
} from "./serviceCancellationGuard.js";

const TEXTO_VOUCHER =
  "No se puede anular este servicio: la reserva tiene vouchers emitidos. Anulá los vouchers primero si necesitás corregir datos.";
const TEXTO_R1 =
  "No se puede anular este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el reembolso con el operador antes de anular el servicio.";
const TEXTO_SIN_CLIENTE =
  "No se puede anular este servicio: la reserva tiene una factura emitida pero no tiene un cliente asignado para facturarle la nota de crédito. Asigná un cliente a la reserva antes de anular.";

// ─── resolverBloqueoAnularServicio: pre-chequeo (papelera gris + chip) ───────

test("servicio sin canCancel (DTO viejo) → sin bloqueo, degradación elegante", () => {
  const resultado = resolverBloqueoAnularServicio({ id: 1, name: "Hotel Maitei" });

  assert.equal(resultado.bloqueado, false);
  assert.equal(resultado.motivo, null);
});

test("servicio con canCancel null (todavía no calculado) → sin bloqueo", () => {
  const resultado = resolverBloqueoAnularServicio({ canCancel: null });

  assert.equal(resultado.bloqueado, false);
  assert.equal(resultado.motivo, null);
});

test("servicio con canCancel allowed=true → sin bloqueo, sin motivo", () => {
  const resultado = resolverBloqueoAnularServicio({ canCancel: { allowed: true, reason: null } });

  assert.equal(resultado.bloqueado, false);
  assert.equal(resultado.motivo, null);
});

test("servicio con voucher vivo (allowed=false) → bloqueado, motivo real del backend", () => {
  const resultado = resolverBloqueoAnularServicio({
    canCancel: { allowed: false, reason: TEXTO_VOUCHER },
  });

  assert.equal(resultado.bloqueado, true);
  assert.equal(resultado.motivo, TEXTO_VOUCHER);
});

test("servicio con R1 (pago al operador sin factura) → bloqueado, motivo real", () => {
  const resultado = resolverBloqueoAnularServicio({
    canCancel: { allowed: false, reason: TEXTO_R1 },
  });

  assert.equal(resultado.bloqueado, true);
  assert.equal(resultado.motivo, TEXTO_R1);
});

test("servicio null/undefined → no explota, sin bloqueo", () => {
  assert.deepEqual(resolverBloqueoAnularServicio(null), { bloqueado: false, motivo: null });
  assert.deepEqual(resolverBloqueoAnularServicio(undefined), { bloqueado: false, motivo: null });
});

// ─── resolverRechazoAnularServicio: modal post-409, botón por código ─────────

test("code CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND (R1) → botón 'Emitir factura'", () => {
  const error = { status: 409, payload: { code: CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA, message: TEXTO_R1 } };

  const resultado = resolverRechazoAnularServicio(error);

  assert.equal(resultado.codigoConocido, true);
  assert.equal(resultado.boton, "emitir-factura");
});

test("code CANCEL_SERVICE_VOUCHER_LIVE → botón 'Ver vouchers de la reserva'", () => {
  const error = { status: 409, payload: { code: CODIGO_RECHAZO_ANULAR_SERVICIO.VOUCHER_VIVO, message: TEXTO_VOUCHER } };

  const resultado = resolverRechazoAnularServicio(error);

  assert.equal(resultado.codigoConocido, true);
  assert.equal(resultado.boton, "ver-vouchers");
});

test("code CANCEL_SERVICE_NO_PAYER → código conocido, SIN botón (no hay pantalla para asignar cliente)", () => {
  const error = { status: 409, payload: { code: CODIGO_RECHAZO_ANULAR_SERVICIO.SIN_CLIENTE, message: TEXTO_SIN_CLIENTE } };

  const resultado = resolverRechazoAnularServicio(error);

  assert.equal(resultado.codigoConocido, true);
  assert.equal(resultado.boton, null);
});

test("409 sin code (carrera fuera de los 3 catalogados) → código NO conocido, cae al camino de respaldo", () => {
  const error = { status: 409, payload: { message: "Otro motivo cualquiera." } };

  const resultado = resolverRechazoAnularServicio(error);

  assert.equal(resultado.codigoConocido, false);
  assert.equal(resultado.boton, null);
});

test("error sin payload (network/parse) → código NO conocido, no explota", () => {
  assert.deepEqual(resolverRechazoAnularServicio({ status: 409 }), { codigoConocido: false, boton: null });
  assert.deepEqual(resolverRechazoAnularServicio(null), { codigoConocido: false, boton: null });
  assert.deepEqual(resolverRechazoAnularServicio(undefined), { codigoConocido: false, boton: null });
});

test("code desconocido (no catalogado) → código NO conocido, sin botón", () => {
  const error = { status: 409, payload: { code: "ALGO_QUE_NO_EXISTE_TODAVIA" } };

  const resultado = resolverRechazoAnularServicio(error);

  assert.equal(resultado.codigoConocido, false);
  assert.equal(resultado.boton, null);
});
