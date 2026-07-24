/**
 * Tests de lógica pura de la Tanda 7 (spec docs/ux/2026-07-20-t5-a-t9-contrato-pantalla-motor.md):
 * "Anular un servicio: pre-chequeo + candado de voucher/sin-cliente con camino servido".
 *
 * Obra "anular sin factura" (2026-07-23, decisión del dueño): el freno R1 (pago al operador
 * sin factura) DEJÓ de bloquear "anular servicio" — resolverBloqueoAnularServicio() sigue
 * siendo un passthrough genérico del `canCancel` del backend (por eso el test con un motivo
 * "R1" de ejemplo sigue siendo válido, ya no representa un caso real que el backend mande) y
 * resolverRechazoAnularServicio() ya no ofrece el botón "Emitir factura" para ese código en
 * ningún caller (ver la sección de abajo).
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
// Texto de ejemplo para probar el passthrough genérico de resolverBloqueoAnularServicio: ya
// no es un motivo real que el backend mande (el freno R1 de "anular servicio" se eliminó),
// pero la función no le presta atención al contenido, solo lo repite tal cual venga.
const TEXTO_R1 =
  "No se puede anular este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el reembolso con el operador antes de anular el servicio.";
const TEXTO_SIN_CLIENTE =
  "No se puede anular este servicio: la reserva tiene una factura emitida pero no tiene un cliente asignado para facturarle la nota de crédito. Asigná un cliente a la reserva antes de anular.";
// Texto REAL que manda hoy el backend cuando "bajar el estado" choca con el freno de plata
// (obra 2026-07-23): ya no pide "emitir factura", orienta a resolver el reembolso primero.
const TEXTO_GESTIONAR_REEMBOLSO_BAJAR_ESTADO =
  "No se puede bajar el estado de este servicio todavía: ya tiene pagos al operador por esta reserva " +
  "que todavía no están resueltos. Gestioná primero el reembolso con el operador (o cancelá el " +
  "servicio) antes de cambiar su estado.";

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

// Obra "anular sin factura" (2026-07-23): el freno R1 dejó de bloquear "anular servicio", así
// que el POST cancel-service ya no manda este code — pero sigue siendo un código conocido
// (lo mandan "reasignar operador" y "bajar estado", ver el test de más abajo) y ya no tiene
// botón: el motor orienta a "gestioná el reembolso con el operador", no a emitir factura.
test("code CANCEL_SERVICE_UNANCHORED_OPERATOR_REFUND (R1) → código conocido, SIN botón (el freno de 'anular servicio' se eliminó)", () => {
  const error = { status: 409, payload: { code: CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA, message: TEXTO_R1 } };

  const resultado = resolverRechazoAnularServicio(error);

  assert.equal(resultado.codigoConocido, true);
  assert.equal(resultado.boton, null);
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

// ─── P1 "circuito proveedor" (2026-07-21): mismo código, un caller que sigue bloqueando ──
//
// Bajar el workflowStatus de un servicio con pagos al operador ya cobrados también pasa
// por el candado de plata (ahora los 10 endpoints Update/UpdateStatus de los 5 tipos
// devuelven el MISMO code que "anular servicio" solía mandar). ServiceInlineCard.jsx
// (editor de Hotel/Vuelo/Traslado/Paquete/Asistencia) reusa esta misma función para el
// cartel de rechazo — este test documenta que el 409 del PUT de edición se resuelve igual
// que el 409 de cancel-service: código conocido, SIN botón (obra 2026-07-23, el mensaje ya
// no pide "emitir factura").
test("editor de servicio (PUT/PATCH de edición) con code R1 → código conocido, SIN botón (ya no pide factura)", () => {
  const error = {
    status: 409,
    payload: {
      code: CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA,
      message: TEXTO_GESTIONAR_REEMBOLSO_BAJAR_ESTADO,
    },
  };

  const resultado = resolverRechazoAnularServicio(error);

  assert.equal(resultado.codigoConocido, true);
  assert.equal(resultado.boton, null);
});
