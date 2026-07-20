/**
 * Tests de lógica pura de la Tanda 6 (spec docs/ux/2026-07-20-t5-a-t9-contrato-pantalla-motor.md):
 * "Editar/Eliminar cobro mira el PAGO, no solo la reserva".
 *
 * Corren con Node puro sin bundler: node --test src/features/reservas/lib/paymentRowGuard.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { resolverBloqueoFilaCobro } from "./paymentRowGuard.js";

const TEXTO_RECIBO_EMITIDO_EDITAR =
  "No se puede editar el pago porque tiene un recibo emitido. Anulá el recibo y registrá un nuevo pago.";
const TEXTO_RECIBO_VIGENTE_ELIMINAR =
  "No se puede eliminar el pago porque tiene un comprobante vigente. Anulá primero el comprobante.";
const TEXTO_FACTURA_CAE_EDITAR =
  "No se puede editar el pago porque está vinculado a una factura emitida (CAE). Generá una nota de crédito si corresponde.";
const TEXTO_FACTURA_CAE_ELIMINAR =
  "No se puede eliminar el pago porque está vinculado a una factura. Generá una nota de crédito si corresponde.";
const TEXTO_RECIBO_ANULADO_EDITAR =
  "No se puede editar el pago porque tiene un recibo anulado que debe preservarse para auditoría.";

// ─── Caso normal: sin bloqueo ────────────────────────────────────────────────

test("payment sin canEdit/canDelete (DTO viejo) → sin bloqueo extra, degradación elegante", () => {
  const resultado = resolverBloqueoFilaCobro({ id: 1, amount: 100 });

  assert.equal(resultado.editarBloqueado, false);
  assert.equal(resultado.eliminarBloqueado, false);
  assert.equal(resultado.motivo, null);
});

test("payment con canEdit/canDelete allowed=true → sin bloqueo, sin motivo", () => {
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: true, reason: null },
    canDelete: { allowed: true, reason: null },
  });

  assert.equal(resultado.editarBloqueado, false);
  assert.equal(resultado.eliminarBloqueado, false);
  assert.equal(resultado.motivo, null);
});

// ─── Recibo emitido: bloquea Editar y Eliminar, gana el motivo de Editar ─────

test("recibo emitido → Editar y Eliminar bloqueados, motivo = el de Editar (texto real del backend)", () => {
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: false, reason: TEXTO_RECIBO_EMITIDO_EDITAR },
    canDelete: { allowed: false, reason: TEXTO_RECIBO_VIGENTE_ELIMINAR },
  });

  assert.equal(resultado.editarBloqueado, true);
  assert.equal(resultado.eliminarBloqueado, true);
  assert.equal(resultado.motivo, TEXTO_RECIBO_EMITIDO_EDITAR, "el motivo de Editar gana cuando ambos están bloqueados");
});

// ─── Factura con CAE vivo: bloquea Editar y Eliminar, gana el motivo de Editar ─

test("factura con CAE vivo → Editar y Eliminar bloqueados, motivo = el de Editar", () => {
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: false, reason: TEXTO_FACTURA_CAE_EDITAR },
    canDelete: { allowed: false, reason: TEXTO_FACTURA_CAE_ELIMINAR },
  });

  assert.equal(resultado.editarBloqueado, true);
  assert.equal(resultado.eliminarBloqueado, true);
  assert.equal(resultado.motivo, TEXTO_FACTURA_CAE_EDITAR);
});

// ─── Recibo anulado: solo bloquea Editar (el backend no lo bloquea para Eliminar) ─

test("recibo anulado → solo Editar bloqueado, Eliminar permitido por el backend", () => {
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: false, reason: TEXTO_RECIBO_ANULADO_EDITAR },
    canDelete: { allowed: true, reason: null },
  });

  assert.equal(resultado.editarBloqueado, true);
  assert.equal(resultado.eliminarBloqueado, false);
  assert.equal(resultado.motivo, TEXTO_RECIBO_ANULADO_EDITAR);
});

// ─── Caso borde: solo Eliminar bloqueado (no ocurre hoy en el backend real, pero la
//     lógica pura debe cubrirlo sin asumir que Editar siempre se evalúa primero) ────

test("solo Eliminar bloqueado (Editar permitido) → motivo = el de Eliminar", () => {
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: true, reason: null },
    canDelete: { allowed: false, reason: TEXTO_RECIBO_VIGENTE_ELIMINAR },
  });

  assert.equal(resultado.editarBloqueado, false);
  assert.equal(resultado.eliminarBloqueado, true);
  assert.equal(resultado.motivo, TEXTO_RECIBO_VIGENTE_ELIMINAR);
});

// ─── Degradación parcial: solo uno de los dos campos viene en el DTO ─────────

test("payment con canEdit pero sin canDelete (DTO parcial) → eliminarBloqueado=false, no explota", () => {
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: false, reason: TEXTO_RECIBO_EMITIDO_EDITAR },
  });

  assert.equal(resultado.editarBloqueado, true);
  assert.equal(resultado.eliminarBloqueado, false);
  assert.equal(resultado.motivo, TEXTO_RECIBO_EMITIDO_EDITAR);
});

test("payment null/undefined → no explota, sin bloqueo", () => {
  assert.deepEqual(resolverBloqueoFilaCobro(null), {
    editarBloqueado: false,
    eliminarBloqueado: false,
    motivo: null,
  });
  assert.deepEqual(resolverBloqueoFilaCobro(undefined), {
    editarBloqueado: false,
    eliminarBloqueado: false,
    motivo: null,
  });
});
