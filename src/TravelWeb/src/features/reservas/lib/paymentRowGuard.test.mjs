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
// Vocabulario (2026-08-05, regla firmada del dueño, BUG 2 "Deshacer cobro"): "eliminar"/
// "borrar" quedan PROHIBIDOS en textos de plata — el motor actualizó estos dos mensajes
// (PaymentCapabilityPolicy.DeleteBlockedBy*Reason) para decir "deshacer", mismo verbo que
// el botón que el usuario tocó. El de EDITAR no cambió: editar sigue siendo "editar".
const TEXTO_RECIBO_VIGENTE_ELIMINAR =
  "Este cobro no se puede deshacer porque tiene un comprobante vigente. Anulá primero el comprobante.";
const TEXTO_FACTURA_CAE_EDITAR =
  "No se puede editar el pago porque está vinculado a una factura emitida (CAE). Generá una nota de crédito si corresponde.";
const TEXTO_FACTURA_CAE_ELIMINAR =
  "Este cobro no se puede deshacer porque está vinculado a una factura. Generá una nota de crédito si corresponde.";
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

// ─── FIX BLOQUEANTE (review 2026-08-05): editarVisible=false (Editar oculto en los 4
//     terminales) — el motivo mostrado debe hablar SIEMPRE del botón que se ve
//     realmente en pantalla (Deshacer), nunca de Editar, que ni siquiera está ────────

test("terminal + recibo emitido (Editar oculto) → el motivo es el de Deshacer, NO el de Editar (antes: bug, hablaba de 'editar el pago')", () => {
  // Caso F1(a) del reviewer: en un estado terminal, Editar está oculto (editarVisible=
  // false) porque no tiene ningún camino válido ahí. El backend igual manda canEdit
  // bloqueado (recibo emitido bloquea los dos) — pero como Editar no se renderiza, ese
  // motivo no debe aparecer: el candado 🔒 tiene que explicar por qué "Deshacer" (el
  // único botón visible) está gris.
  const resultado = resolverBloqueoFilaCobro(
    {
      canEdit: { allowed: false, reason: TEXTO_RECIBO_EMITIDO_EDITAR },
      canDelete: { allowed: false, reason: TEXTO_RECIBO_VIGENTE_ELIMINAR },
    },
    { editarVisible: false }
  );

  assert.equal(resultado.eliminarBloqueado, true);
  assert.equal(
    resultado.motivo,
    TEXTO_RECIBO_VIGENTE_ELIMINAR,
    "el motivo debe ser el de Deshacer (canDelete), porque Editar no está en pantalla"
  );
  assert.notEqual(resultado.motivo, TEXTO_RECIBO_EMITIDO_EDITAR, "nunca debe colarse el motivo de un botón oculto");
});

test("terminal + recibo solo-anulado (Editar oculto, Deshacer habilitado) → SIN motivo (no se pinta un candado huérfano)", () => {
  // Caso F1(b) del reviewer: un recibo SOLO anulado bloquea Editar (canEdit.allowed=
  // false) pero NO bloquea Deshacer (canDelete.allowed=true, regla C28). Con Editar
  // oculto, no queda ningún botón bloqueado que justifique el renglón 🔒 — antes se
  // mostraba igual, hablando de un botón que ni se ve. Ahora no se pinta nada.
  const resultado = resolverBloqueoFilaCobro(
    {
      canEdit: { allowed: false, reason: TEXTO_RECIBO_ANULADO_EDITAR },
      canDelete: { allowed: true, reason: null },
    },
    { editarVisible: false }
  );

  assert.equal(resultado.eliminarBloqueado, false);
  assert.equal(resultado.motivo, null, "Deshacer está habilitado y Editar está oculto: no hay nada que explicar");
});

test("terminal + ningún candado fiscal (canEdit/canDelete allowed=true) → sin motivo, igual que siempre", () => {
  const resultado = resolverBloqueoFilaCobro(
    { canEdit: { allowed: true, reason: null }, canDelete: { allowed: true, reason: null } },
    { editarVisible: false }
  );

  assert.equal(resultado.motivo, null);
});

test("editarVisible por defecto (sin pasar la opción) → se comporta exactamente igual que antes del fix", () => {
  // Mismo fixture que el primer test de "recibo emitido" de arriba, pero sin pasar
  // opciones — confirma que los call sites viejos (si quedara alguno) no cambian.
  const resultado = resolverBloqueoFilaCobro({
    canEdit: { allowed: false, reason: TEXTO_RECIBO_EMITIDO_EDITAR },
    canDelete: { allowed: false, reason: TEXTO_RECIBO_VIGENTE_ELIMINAR },
  });

  assert.equal(resultado.motivo, TEXTO_RECIBO_EMITIDO_EDITAR);
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
