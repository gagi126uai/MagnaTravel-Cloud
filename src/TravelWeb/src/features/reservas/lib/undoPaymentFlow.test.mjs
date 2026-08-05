/**
 * Tests de "Deshacer cobro" (BUG 2, prueba integral 2026-08-05): ruteo por estado de
 * la reserva (DELETE vs /annul) y texto del diálogo de confirmación en criollo.
 *
 * Corren con Node puro sin bundler: node --test src/features/reservas/lib/undoPaymentFlow.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  requiereAnularConRastro,
  resolverRutaDeshacerCobro,
  construirConfirmacionDeshacerCobro,
  resolverMensajeErrorDeshacerCobro,
} from "./undoPaymentFlow.js";
import { formatCurrency } from "../../../lib/utils.js";

// ─── requiereAnularConRastro / resolverRutaDeshacerCobro ───────────────────────

test("estados vivos (InManagement/Confirmed/Traveling) → DELETE normal, no requiere anular", () => {
  for (const status of ["Quotation", "Budget", "InManagement", "Confirmed", "Traveling"]) {
    assert.equal(requiereAnularConRastro(status), false, `status=${status}`);
  }
});

test("los 4 estados terminales (Closed/Cancelled/Lost/PendingOperatorRefund) → requieren anular con rastro", () => {
  for (const status of ["Closed", "Cancelled", "Lost", "PendingOperatorRefund"]) {
    assert.equal(requiereAnularConRastro(status), true, `status=${status}`);
  }
});

test("status null/undefined/desconocido → no requiere anular (degrada al DELETE normal, no revienta)", () => {
  assert.equal(requiereAnularConRastro(null), false);
  assert.equal(requiereAnularConRastro(undefined), false);
  assert.equal(requiereAnularConRastro("EstadoQueNoExisteTodavia"), false);
});

test("Finalizada (Closed) → POST /payments/{id}/annul", () => {
  const resultado = resolverRutaDeshacerCobro("abc-123", "Closed");
  assert.deepEqual(resultado, { metodo: "post", ruta: "/payments/abc-123/annul" });
});

test("Anulada (Cancelled) → POST /payments/{id}/annul", () => {
  const resultado = resolverRutaDeshacerCobro("abc-123", "Cancelled");
  assert.deepEqual(resultado, { metodo: "post", ruta: "/payments/abc-123/annul" });
});

test("Perdida (Lost) → POST /payments/{id}/annul", () => {
  const resultado = resolverRutaDeshacerCobro("abc-123", "Lost");
  assert.deepEqual(resultado, { metodo: "post", ruta: "/payments/abc-123/annul" });
});

test("Esperando reembolso (PendingOperatorRefund) → POST /payments/{id}/annul", () => {
  const resultado = resolverRutaDeshacerCobro("abc-123", "PendingOperatorRefund");
  assert.deepEqual(resultado, { metodo: "post", ruta: "/payments/abc-123/annul" });
});

test("En gestión / Confirmada / En viaje → DELETE /payments/{id} (sin sufijo)", () => {
  for (const status of ["InManagement", "Confirmed", "Traveling"]) {
    const resultado = resolverRutaDeshacerCobro("abc-123", status);
    assert.deepEqual(resultado, { metodo: "delete", ruta: "/payments/abc-123" }, `status=${status}`);
  }
});

// ─── construirConfirmacionDeshacerCobro ─────────────────────────────────────────

test("título fijo, no menciona 'Eliminar' en ningún campo", () => {
  const dialogo = construirConfirmacionDeshacerCobro({ amount: 5000, currency: "ARS" });

  assert.equal(dialogo.title, "¿Deshacer este cobro?");
  for (const campo of ["title", "message", "confirmText", "cancelText"]) {
    assert.equal(
      /eliminar/i.test(dialogo[campo]),
      false,
      `el campo "${campo}" no debe mencionar "eliminar": "${dialogo[campo]}"`
    );
  }
});

test("mensaje incluye el monto formateado y explica el efecto en la plata (P-14)", () => {
  const dialogo = construirConfirmacionDeshacerCobro({ amount: 5000, currency: "ARS" });

  assert.equal(
    dialogo.message,
    `El cobro de ${formatCurrency(5000, "ARS")} se va a deshacer y el saldo de la reserva se recalcula. Queda registrado en el historial.`
  );
});

test("mensaje en dólares usa el símbolo US$ (mismo formatCurrency de siempre)", () => {
  const dialogo = construirConfirmacionDeshacerCobro({ amount: 300, currency: "USD" });

  assert.match(dialogo.message, /US\$300,00/);
});

test("botones: 'Deshacer cobro' / 'No, volver'", () => {
  const dialogo = construirConfirmacionDeshacerCobro({ amount: 5000, currency: "ARS" });

  assert.equal(dialogo.confirmText, "Deshacer cobro");
  assert.equal(dialogo.cancelText, "No, volver");
});

test("type 'warning' (no 'danger'): ya no se presenta como un borrado", () => {
  const dialogo = construirConfirmacionDeshacerCobro({ amount: 5000, currency: "ARS" });

  assert.equal(dialogo.type, "warning");
});

test("payment sin amount/currency (degradación) → no revienta, usa $0,00", () => {
  const dialogo = construirConfirmacionDeshacerCobro({});
  assert.match(dialogo.message, /\$\s?0,00/);

  assert.doesNotThrow(() => construirConfirmacionDeshacerCobro(null));
  assert.doesNotThrow(() => construirConfirmacionDeshacerCobro(undefined));
});

// ─── resolverMensajeErrorDeshacerCobro (F5, review 2026-08-05) ─────────────────
//
// DELETE /payments/{id} y POST /payments/{id}/annul devuelven 403/404 SIN CUERPO
// (el 403 lo arma el middleware de autorización de ASP.NET por [Authorize(Roles=
// "Admin")], sin body; el 404 es un `return NotFound()` liso). El mapeo GLOBAL de
// errores (lib/errors.js) los trataría como "bare statusText" y mostraría el
// genérico de RED — mentira acá. Estos tests fijan el mapeo LOCAL de esta acción.

test("status 403 (no-admin) → mensaje de permiso en criollo", () => {
  assert.equal(
    resolverMensajeErrorDeshacerCobro(403),
    "No tenés permiso para deshacer cobros. Pedíselo a un administrador."
  );
});

test("status 404 (cobro ya no existe) → mensaje de 'no encontrado' en criollo", () => {
  assert.equal(
    resolverMensajeErrorDeshacerCobro(404),
    "No se encontró el cobro. Actualizá la página."
  );
});

test("cualquier otro status (ej. 409 del motor) → null, el caller usa el mensaje real del motor (P-13)", () => {
  assert.equal(resolverMensajeErrorDeshacerCobro(409), null);
  assert.equal(resolverMensajeErrorDeshacerCobro(500), null);
  assert.equal(resolverMensajeErrorDeshacerCobro(400), null);
});

test("status null/undefined (error sin status, ej. corte de red real) → null, no revienta", () => {
  assert.equal(resolverMensajeErrorDeshacerCobro(null), null);
  assert.equal(resolverMensajeErrorDeshacerCobro(undefined), null);
});
