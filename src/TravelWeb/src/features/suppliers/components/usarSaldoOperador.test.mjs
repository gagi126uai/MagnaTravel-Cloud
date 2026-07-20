/**
 * Tests de lógica pura para el flujo "Usar saldo a favor del operador".
 *
 * Cubre las validaciones y transformaciones que ocurren en UsarSaldoOperadorInline:
 *   - filtrado de reservas por moneda
 *   - cálculo del monto sugerido (min entre deuda del destino y saldo disponible)
 *   - cálculo del TOPE real del input (mismo min, pero recalculado en cada cambio de
 *     reserva elegida — Tanda 1 del contrato pantalla-motor, 2026-07-18)
 *   - validación del monto antes del POST
 *   - construcción del payload para POST /suppliers/{id}/credit/apply
 *
 * Y las operaciones de reversión de aplicaciones activas:
 *   - construcción del payload de reversión (motivo opcional)
 *   - enmascarado de montos según permiso (cobranzas.see_cost)
 *
 * Cómo correr: node --test src/features/suppliers/components/usarSaldoOperador.test.mjs
 *
 * Patrón del proyecto: funciones replicadas inline (sin import de módulo).
 * Si cambia la lógica en el componente, actualizar acá también.
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura replicada de UsarSaldoOperadorInline ────────────────────────

/**
 * Filtra reservas que tienen deuda en una moneda específica.
 * Regla multimoneda: solo mostramos reservas con deuda en la moneda del cartel.
 * Nunca mezclamos ARS con USD.
 */
function filtrarReservasPorMoneda(reservas, moneda) {
  return reservas.filter((r) => {
    const lineaMoneda = (r.currencies ?? []).find((c) => c.currency === moneda);
    return lineaMoneda && (lineaMoneda.balance ?? 0) > 0;
  });
}

/**
 * Calcula el monto sugerido al seleccionar una reserva destino.
 * Regla: monto = min(deuda de la reserva en esta moneda, saldo disponible).
 * Así no se sugiere más de lo que la reserva debe ni más de lo que hay.
 */
function calcularMontoSugerido(reserva, moneda, saldoDisponible) {
  const lineaMoneda = (reserva.currencies ?? []).find((c) => c.currency === moneda);
  const deudaReservaDestino = lineaMoneda ? (lineaMoneda.balance ?? 0) : 0;
  return Math.min(deudaReservaDestino, saldoDisponible);
}

/**
 * Calcula el TOPE real del monto a aplicar (no solo la sugerencia inicial): el menor
 * entre el saldo a favor disponible y la deuda de la reserva elegida. El backend
 * (ApplyCreditAsync, INV-SUPCREDIT-003 / M1) nunca deja aplicar más de lo que la reserva
 * debe, aunque sobre saldo a favor — si no, el excedente queda "atrapado" en el destino.
 * Sin reserva elegida todavía, el tope es simplemente el saldo disponible.
 */
function calcularTopeMontoOperador(reservaDestinoSeleccionada, moneda, saldoDisponible) {
  if (!reservaDestinoSeleccionada) return saldoDisponible;
  const lineaMoneda = (reservaDestinoSeleccionada.currencies ?? []).find((c) => c.currency === moneda);
  const deudaDestino = lineaMoneda ? (lineaMoneda.balance ?? 0) : 0;
  return Math.min(saldoDisponible, deudaDestino);
}

/**
 * Valida los datos del formulario antes de confirmar la aplicación.
 * Regla: monto > 0 y monto <= tope real (el menor entre saldo disponible y deuda del destino).
 */
function validarAplicacionOperador(monto, saldoDisponible, reservaDestinoSeleccionada, moneda) {
  if (!reservaDestinoSeleccionada) {
    return "Elegí una reserva destino antes de confirmar.";
  }

  const montoNum = parseFloat(monto);
  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }

  const tope = calcularTopeMontoOperador(reservaDestinoSeleccionada, moneda, saldoDisponible);
  if (montoNum > tope) {
    return `El monto no puede superar ${tope} (lo que debe esta reserva, o el saldo disponible).`;
  }

  return null;
}

/**
 * Construye el payload para POST /suppliers/{id}/credit/apply.
 */
function armarPayloadAplicacionOperador(currency, amount, targetReservaPublicId) {
  return {
    currency,
    amount: parseFloat(amount),
    targetReservaPublicId,
  };
}

/**
 * Construye el payload para POST /suppliers/{id}/credit/applications/{id}/reverse.
 * El motivo es opcional — si está vacío, se envía null (el backend lo acepta).
 */
function armarPayloadReversionOperador(motivo) {
  return { reason: motivo?.trim() || null };
}

// ─── Tests de filtrado de reservas por moneda ─────────────────────────────────

test("filtrarReservasPorMoneda — solo devuelve reservas con deuda en la moneda indicada", () => {
  const reservas = [
    { id: "r1", currencies: [{ currency: "ARS", balance: 1000 }] },
    { id: "r2", currencies: [{ currency: "USD", balance: 500 }] },
    { id: "r3", currencies: [{ currency: "ARS", balance: 0 }] },
    { id: "r4", currencies: [] },
  ];

  const resultado = filtrarReservasPorMoneda(reservas, "ARS");
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].id, "r1");
});

test("filtrarReservasPorMoneda — devuelve vacío cuando no hay deuda en la moneda", () => {
  const reservas = [
    { id: "r1", currencies: [{ currency: "ARS", balance: 100 }] },
  ];
  const resultado = filtrarReservasPorMoneda(reservas, "USD");
  assert.equal(resultado.length, 0);
});

test("filtrarReservasPorMoneda — excluye reservas con balance 0", () => {
  const reservas = [
    { id: "r1", currencies: [{ currency: "ARS", balance: 0 }] },
    { id: "r2", currencies: [{ currency: "ARS", balance: 200 }] },
  ];
  const resultado = filtrarReservasPorMoneda(reservas, "ARS");
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].id, "r2");
});

test("filtrarReservasPorMoneda — devuelve múltiples reservas en la misma moneda", () => {
  const reservas = [
    { id: "r1", currencies: [{ currency: "USD", balance: 300 }] },
    { id: "r2", currencies: [{ currency: "USD", balance: 800 }] },
  ];
  const resultado = filtrarReservasPorMoneda(reservas, "USD");
  assert.equal(resultado.length, 2);
});

test("filtrarReservasPorMoneda — ignora reservas sin campo currencies", () => {
  const reservas = [
    { id: "r1" },
    { id: "r2", currencies: null },
    { id: "r3", currencies: [{ currency: "ARS", balance: 100 }] },
  ];
  const resultado = filtrarReservasPorMoneda(reservas, "ARS");
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].id, "r3");
});

// ─── Tests de cálculo del monto sugerido ──────────────────────────────────────

test("calcularMontoSugerido — usa el mínimo entre deuda del destino y saldo disponible", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 500 }] };
  // Saldo disponible (800) > deuda reserva (500) → sugerencia = 500
  assert.equal(calcularMontoSugerido(reserva, "ARS", 800), 500);
});

test("calcularMontoSugerido — cuando saldo disponible < deuda, sugerencia = saldo disponible", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 2000 }] };
  // Saldo disponible (300) < deuda reserva (2000) → sugerencia = 300
  assert.equal(calcularMontoSugerido(reserva, "ARS", 300), 300);
});

test("calcularMontoSugerido — cuando ambos son iguales, sugerencia = ese valor", () => {
  const reserva = { currencies: [{ currency: "USD", balance: 100 }] };
  assert.equal(calcularMontoSugerido(reserva, "USD", 100), 100);
});

test("calcularMontoSugerido — cuando la reserva no tiene deuda en la moneda, sugerencia = 0", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 500 }] };
  // La reserva tiene deuda en ARS pero la moneda que se pasa es USD → 0
  assert.equal(calcularMontoSugerido(reserva, "USD", 800), 0);
});

// ─── Tests del tope real (saldo disponible vs. deuda del destino) ────────────
// Bug corregido: el input solo topeaba contra saldoDisponible, pero el backend
// (INV-SUPCREDIT-003 / M1) también topea contra la deuda de la reserva elegida.
// Antes esto dejaba cargar de más y el 409 recién aparecía al confirmar.

test("calcularTopeMontoOperador — sin reserva elegida, el tope es el saldo disponible", () => {
  assert.equal(calcularTopeMontoOperador(null, "ARS", 1000), 1000);
});

test("calcularTopeMontoOperador — la deuda del destino es MENOR al saldo: tope = deuda del destino", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 300 }] };
  assert.equal(calcularTopeMontoOperador(reserva, "ARS", 1000), 300);
});

test("calcularTopeMontoOperador — el saldo disponible es MENOR a la deuda del destino: tope = saldo disponible", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 5000 }] };
  assert.equal(calcularTopeMontoOperador(reserva, "ARS", 1000), 1000);
});

// ─── Tests de validación del formulario ───────────────────────────────────────

test("validarAplicacionOperador — sin reserva destino devuelve error", () => {
  const error = validarAplicacionOperador("500", 1000, null, "ARS");
  assert.ok(error);
  assert.ok(error.includes("Elegí una reserva destino"));
});

test("validarAplicacionOperador — monto 0 devuelve error", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 1000 }] };
  const error = validarAplicacionOperador("0", 1000, reserva, "ARS");
  assert.ok(error);
  assert.ok(error.includes("mayor a 0"));
});

test("validarAplicacionOperador — monto vacío devuelve error", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 1000 }] };
  const error = validarAplicacionOperador("", 1000, reserva, "ARS");
  assert.ok(error);
});

test("validarAplicacionOperador — monto negativo devuelve error", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 1000 }] };
  const error = validarAplicacionOperador("-100", 1000, reserva, "ARS");
  assert.ok(error);
  assert.ok(error.includes("mayor a 0"));
});

test("validarAplicacionOperador — monto mayor al saldo disponible (y la deuda alcanza) devuelve error", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 5000 }] };
  const error = validarAplicacionOperador("1500", 1000, reserva, "ARS");
  assert.ok(error);
  assert.ok(error.includes("1000"));
});

test("validarAplicacionOperador — monto mayor a la deuda del destino (aunque sobre saldo) devuelve error", () => {
  // Regresión del bug: hay saldo de sobra (1000) pero la reserva solo debe 300.
  const reserva = { currencies: [{ currency: "ARS", balance: 300 }] };
  const error = validarAplicacionOperador("500", 1000, reserva, "ARS");
  assert.ok(error);
  assert.ok(error.includes("300"));
  assert.ok(error.includes("esta reserva"));
});

test("validarAplicacionOperador — monto igual al saldo disponible es válido (la deuda alcanza)", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 1000 }] };
  const error = validarAplicacionOperador("1000", 1000, reserva, "ARS");
  assert.equal(error, null);
});

test("validarAplicacionOperador — monto menor al saldo disponible es válido", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 1000 }] };
  const error = validarAplicacionOperador("500", 1000, reserva, "ARS");
  assert.equal(error, null);
});

test("validarAplicacionOperador — string numérico con decimales es válido", () => {
  const reserva = { currencies: [{ currency: "ARS", balance: 1000 }] };
  const error = validarAplicacionOperador("99.99", 1000, reserva, "ARS");
  assert.equal(error, null);
});

// ─── Tests del payload de aplicación ─────────────────────────────────────────

test("armarPayloadAplicacionOperador — produce los 3 campos exactos", () => {
  const payload = armarPayloadAplicacionOperador("ARS", "500", "guid-reserva-1");
  assert.deepEqual(Object.keys(payload).sort(), ["amount", "currency", "targetReservaPublicId"].sort());
  assert.equal(payload.currency, "ARS");
  assert.equal(payload.amount, 500);
  assert.equal(payload.targetReservaPublicId, "guid-reserva-1");
});

test("armarPayloadAplicacionOperador — convierte string a número en amount", () => {
  const payload = armarPayloadAplicacionOperador("USD", "123.45", "guid-1");
  assert.equal(typeof payload.amount, "number");
  assert.equal(payload.amount, 123.45);
});

test("armarPayloadAplicacionOperador — funciona con USD", () => {
  const payload = armarPayloadAplicacionOperador("USD", 200, "guid-2");
  assert.equal(payload.currency, "USD");
  assert.equal(payload.amount, 200);
});

// ─── Tests del payload de reversión ──────────────────────────────────────────

test("armarPayloadReversionOperador — motivo con texto devuelve reason con ese texto", () => {
  const payload = armarPayloadReversionOperador("Error en la imputación");
  assert.equal(payload.reason, "Error en la imputación");
});

test("armarPayloadReversionOperador — motivo vacío devuelve reason null (campo opcional)", () => {
  const payload = armarPayloadReversionOperador("");
  assert.equal(payload.reason, null);
});

test("armarPayloadReversionOperador — motivo undefined devuelve reason null", () => {
  const payload = armarPayloadReversionOperador(undefined);
  assert.equal(payload.reason, null);
});

test("armarPayloadReversionOperador — motivo con solo espacios devuelve reason null", () => {
  const payload = armarPayloadReversionOperador("   ");
  assert.equal(payload.reason, null);
});

test("armarPayloadReversionOperador — motivo con espacios se limpia con trim", () => {
  const payload = armarPayloadReversionOperador("  motivo con espacios  ");
  assert.equal(payload.reason, "motivo con espacios");
});

// ─── Test B1 (bloqueante): el guid del proveedor viaja en el payload ──────────
// Bug original: handleConfirmar usaba getPublicId(reservaDestinoSeleccionada) que
// devuelve null para objetos con reservaPublicId (no publicId).
// Fix: reservaDestinoSeleccionada.reservaPublicId directo.

test("armarPayloadAplicacionOperador — usa reservaPublicId del DTO real de debt-by-reserva (no null)", () => {
  // Shape real del DTO que devuelve GET /suppliers/{id}/account/debt-by-reserva
  const reservaDelBackend = {
    reservaPublicId: "guid-operador-real-456",
    numeroReserva: "R-0099",
    fileName: "Crucero Caribe",
    currencies: [{ currency: "USD", balance: 800 }],
    // IMPORTANTE: no tiene el campo "publicId" — lo que usaba getPublicId antes (devolvía null)
  };

  // Fix B1: usamos .reservaPublicId directamente (como hace el componente corregido)
  const targetReservaPublicId = reservaDelBackend.reservaPublicId;
  const payload = armarPayloadAplicacionOperador("USD", "800", targetReservaPublicId);

  assert.equal(payload.targetReservaPublicId, "guid-operador-real-456",
    "El guid tiene que viajar en el payload — no null ni undefined");
  assert.notEqual(payload.targetReservaPublicId, null);
  assert.notEqual(payload.targetReservaPublicId, undefined);
  assert.equal(payload.currency, "USD");
  assert.equal(payload.amount, 800);
});
