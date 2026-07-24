/**
 * Tests de lógica pura de la Tanda 3 (spec docs/ux/2026-07-20-t3-t4-contrato-pantalla-motor.md):
 * el mapa código → texto criollo del panel "Anular reserva".
 *
 * Obra "anular sin factura" (2026-07-23, decisión del dueño): el freno de plata R1 a nivel
 * reserva (ANNUL_CREDIT_UNANCHORED_OPERATOR_REFUND) se eliminó del backend — dejó de bloquear,
 * así que el botón "Emitir factura" que ofrecía se retiró (mismo criterio que
 * serviceCancellationGuard.test.mjs para el R1 de "anular servicio", PR-6: se ajustan solo los
 * casos del code muerto, sin relajar el resto de la tabla).
 *
 * Corre con Node puro sin bundler: node --test src/features/cancellations/lib/anularReservaRechazoLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  resolverTextoRechazoAnularReserva,
  CODIGO_RECHAZO_ANULAR_RESERVA_CON_FRENO_DE_PLATA,
} from "./anularReservaRechazoLogic.js";

const TEXTO_NEUTRO_ANNUL_WITH_CREDIT =
  "No se pudo anular la reserva. Probá de nuevo; si el problema sigue, contactá a administración.";
const TEXTO_NEUTRO_DRAFT =
  "No se pudo iniciar la anulación. Probá de nuevo; si el problema sigue, contactá a administración.";
const TEXTO_NEUTRO_CONFIRM =
  "No se pudo confirmar la anulación. Probá de nuevo; si el problema sigue, contactá a administración.";

// ─── Códigos del camino draft()/confirm() (viajan en payload.invariantCode) ──────────────

test("INV-152 (draft) → texto rescatado del componente muerto, con 'anulación' no 'cancelación'", () => {
  const error = { status: 409, payload: { code: "business_invariant_violation", invariantCode: "INV-152" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_DRAFT);

  assert.match(resultado.texto, /varios operadores/);
  assert.equal(resultado.texto.includes("cancelación"), false);
});

test("INV-081 (draft) → ya hay una anulación en curso", () => {
  const error = { status: 409, payload: { code: "business_invariant_violation", invariantCode: "INV-081" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_DRAFT);

  assert.equal(
    resultado.texto,
    "Esta reserva ya tiene una anulación en curso. Actualizá la página para ver en qué quedó."
  );
});

test("INV-100 en draft() → la factura ya fue anulada con NC, no queda nada por anular", () => {
  const error = { status: 409, payload: { code: "business_invariant_violation", invariantCode: "INV-100" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_DRAFT);

  assert.equal(
    resultado.texto,
    "La factura de esta reserva ya fue anulada con una nota de crédito. No queda nada más para anular."
  );
});

test("INV-100 en confirm() → mismo texto, aparece en los dos puntos según la spec", () => {
  const error = { status: 409, payload: { code: "business_invariant_violation", invariantCode: "INV-100" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_CONFIRM);

  assert.equal(
    resultado.texto,
    "La factura de esta reserva ya fue anulada con una nota de crédito. No queda nada más para anular."
  );
});

test("INV-093 (confirm) → la anulación cambió de estado mientras el panel estaba abierto", () => {
  const error = { status: 409, payload: { code: "business_invariant_violation", invariantCode: "INV-093" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_CONFIRM);

  assert.equal(
    resultado.texto,
    "Esta anulación cambió de estado mientras la tenías abierta. Actualizá la página para ver cómo sigue."
  );
});

// ─── Códigos del camino annul-with-credit (viajan en payload.code) ───────────────────────

test("ANNUL_CREDIT_NOT_FIRM_STATE → la reserva todavía no está En gestión ni Confirmada", () => {
  const error = { status: 409, payload: { code: "ANNUL_CREDIT_NOT_FIRM_STATE", message: "texto interno del backend" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_ANNUL_WITH_CREDIT);

  assert.match(resultado.texto, /En gestión ni Confirmada/);
  // Nunca se filtra el `message` crudo del backend cuando el código está mapeado.
  assert.equal(resultado.texto.includes("texto interno del backend"), false);
});

test("ANNUL_CREDIT_LIVE_INVOICE → avisa que hay que reabrir el panel para el camino con NC", () => {
  const error = { status: 409, payload: { code: "ANNUL_CREDIT_LIVE_INVOICE" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_ANNUL_WITH_CREDIT);

  assert.match(resultado.texto, /camino con nota de crédito/);
  assert.match(resultado.texto, /Cerrá este panel y volvé a abrir/);
});

test("ANNUL_CREDIT_NO_PAYER → falta un cliente pagador asignado a la reserva", () => {
  const error = { status: 409, payload: { code: "ANNUL_CREDIT_NO_PAYER" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_ANNUL_WITH_CREDIT);

  assert.match(resultado.texto, /no hay a quién devolverle el saldo a favor/);
  assert.match(resultado.texto, /Asigná un cliente pagador/);
});

// Obra "anular sin factura" (2026-07-23, decisión del dueño): el backend eliminó este candado
// (AnnulWithPaymentsToCreditAsync ya no lo lanza) — anular la reserva entera con plata pagada
// al operador sin factura ya NO rechaza. El código queda como red de seguridad: si de todos
// modos llegara (versión vieja cacheada), se sigue mostrando el texto tal cual, SIN botón
// (P-13) — la función ya no tiene ningún campo de botón que ofrecer.
test("ANNUL_CREDIT_UNANCHORED_OPERATOR_REFUND (freno de plata R1, code muerto) → sigue mostrando el texto, sin ningún botón", () => {
  const error = { status: 409, payload: { code: CODIGO_RECHAZO_ANULAR_RESERVA_CON_FRENO_DE_PLATA } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_ANNUL_WITH_CREDIT);

  assert.match(resultado.texto, /pagaste al operador/);
  assert.equal(resultado.mostrarBotonEmitirFactura, undefined, "la función ya no expone ningún campo de botón");
  assert.deepEqual(Object.keys(resultado), ["texto"], "el resultado solo trae `texto`, ningún indicador de botón");
});

// ─── invariantCode tiene prioridad sobre el `code` genérico del camino con NC ────────────

test("invariantCode presente → se usa aunque `code` sea el genérico 'business_invariant_violation'", () => {
  const error = { status: 409, payload: { code: "business_invariant_violation", invariantCode: "INV-081" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_DRAFT);

  assert.match(resultado.texto, /anulación en curso/);
});

// ─── Fallback neutro: código desconocido o ausente ───────────────────────────────────────

test("código no catalogado → cae al texto neutro de ese punto del panel, nunca al texto crudo", () => {
  const error = { status: 409, payload: { code: "ALGO_QUE_TODAVIA_NO_EXISTE", message: "detalle técnico interno" } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_ANNUL_WITH_CREDIT);

  assert.equal(resultado.texto, TEXTO_NEUTRO_ANNUL_WITH_CREDIT);
});

test("409 sin ningún código en el body → cae al texto neutro", () => {
  const error = { status: 409, payload: { message: "Ocurrió un error." } };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_DRAFT);

  assert.equal(resultado.texto, TEXTO_NEUTRO_DRAFT);
});

test("409 sin body (payload undefined) → cae al texto neutro, no revienta", () => {
  const error = { status: 409 };

  const resultado = resolverTextoRechazoAnularReserva(error, TEXTO_NEUTRO_CONFIRM);

  assert.equal(resultado.texto, TEXTO_NEUTRO_CONFIRM);
});

test("error null/undefined no rompe la función y cae al texto neutro", () => {
  assert.equal(resolverTextoRechazoAnularReserva(null, "neutro").texto, "neutro");
  assert.equal(resolverTextoRechazoAnularReserva(undefined, "neutro").texto, "neutro");
});

test("CODIGO_RECHAZO_ANULAR_RESERVA_CON_FRENO_DE_PLATA queda anclado al valor real del backend", () => {
  // Ancla el nombre exportado al valor que define AnnulWithCreditRejectedException.Codes.UnanchoredOperatorRefund
  // en el backend (src/TravelApi.Domain/Exceptions/AnnulWithCreditRejectedException.cs). Si alguien
  // cambia el string en un solo lado, este test lo detecta.
  assert.equal(CODIGO_RECHAZO_ANULAR_RESERVA_CON_FRENO_DE_PLATA, "ANNUL_CREDIT_UNANCHORED_OPERATOR_REFUND");
});
