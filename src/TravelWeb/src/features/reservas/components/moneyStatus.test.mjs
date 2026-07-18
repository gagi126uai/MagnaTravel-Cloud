/**
 * Tests de contrato para getMoneyStatus (Tanda 6, saneamiento 2026-07-05).
 *
 * Este archivo importa el módulo REAL (moneyStatus.js no tiene JSX, así que a
 * diferencia de otros tests .mjs del proyecto no hace falta copiar la lógica).
 *
 * Cubre la matriz completa de casos: reservas anuladas (con y sin
 * cancelledMoneyContext explícito) y reservas vivas (con y sin collectionStatus).
 *
 * Cómo correr: node --test src/features/reservas/components/moneyStatus.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { getMoneyStatus, isReservaAnulada } from "../moneyStatus.js";

// ─── isReservaAnulada ────────────────────────────────────────────────────────

test("isReservaAnulada: Cancelled y PendingOperatorRefund son anuladas (fallback por string, sin isVoided)", () => {
  assert.equal(isReservaAnulada("Cancelled"), true);
  assert.equal(isReservaAnulada("PendingOperatorRefund"), true);
});

test("isReservaAnulada: el resto de los estados no son anulados", () => {
  assert.equal(isReservaAnulada("Quotation"), false);
  assert.equal(isReservaAnulada("Confirmed"), false);
  assert.equal(isReservaAnulada("Closed"), false);
  assert.equal(isReservaAnulada(undefined), false);
});

// ─── ADR-048 T4/B3 (2026-07-17): isReservaAnulada lee reserva.isVoided del backend ──
// Fuente única del par Cancelled + PendingOperatorRefund (EstadoReserva.IsVoidedStatus).
// El front YA NO decide "anulada" comparando el string de estado cuando el DTO trae
// el booleano explícito — solo cae al fallback por nombre para DTOs viejos sin el campo.

test("isReservaAnulada: reserva.isVoided=true manda, sin importar el string de estado", () => {
  // Caso hipotético (defensivo): si el backend algún día agrega un tercer estado "sin
  // efecto", el front lo reconoce YA por el booleano, sin esperar un deploy que le
  // enseñe el nombre nuevo del estado.
  assert.equal(isReservaAnulada({ status: "AlgunEstadoFuturo", isVoided: true }), true);
});

test("isReservaAnulada: reserva.isVoided=false manda, aunque el status legacy sea Cancelled", () => {
  assert.equal(isReservaAnulada({ status: "Cancelled", isVoided: false }), false);
});

test("isReservaAnulada: reserva.isVoided=true con status Cancelled/PendingOperatorRefund reales (caso normal)", () => {
  assert.equal(isReservaAnulada({ status: "Cancelled", isVoided: true }), true);
  assert.equal(isReservaAnulada({ status: "PendingOperatorRefund", isVoided: true }), true);
});

test("isReservaAnulada: DTO SIN isVoided (legacy, ej. fila de cuenta corriente) cae al fallback por estado", () => {
  assert.equal(isReservaAnulada({ status: "Cancelled" }), true);
  assert.equal(isReservaAnulada({ status: "PendingOperatorRefund" }), true);
  assert.equal(isReservaAnulada({ status: "Confirmed" }), false);
});

test("isReservaAnulada: reserva null/undefined no revienta", () => {
  assert.equal(isReservaAnulada(null), false);
  assert.equal(isReservaAnulada(undefined), false);
});

// ─── Reservas ANULADAS con cancelledMoneyContext explícito ─────────────────

test("Anulada + SaldoAFavorPendiente: label 'Saldo a favor', tono success", () => {
  const reserva = { status: "Cancelled", cancelledMoneyContext: "SaldoAFavorPendiente", balance: -5000 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "saldoAFavorAnulada");
  assert.equal(result.label, "Saldo a favor");
  assert.equal(result.tone, "success");
});

test("Anulada + MultaPorCobrar: label de multa, tono warning (nunca 'Debe')", () => {
  const reserva = { status: "PendingOperatorRefund", cancelledMoneyContext: "MultaPorCobrar", balance: 8000 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "multaPorCobrar");
  assert.equal(result.label, "Multa por anulación pendiente de cobro");
  assert.equal(result.tone, "warning");
  assert.notEqual(result.label, "Debe");
});

// ─── Tanda "multa fantasma" (2026-07-06): monto de la multa vs. fallback a balance ──

test("Anulada + MultaPorCobrar CON cancelledPenaltyAmount/Currency: el monto es el de LA MULTA, no el balance", () => {
  // El balance de la reserva (8000) puede incluir otra plata mezclada — el monto a
  // mostrar tiene que ser el de la multa (3500 USD), tomado de los campos nuevos.
  const reserva = {
    status: "Cancelled",
    cancelledMoneyContext: "MultaPorCobrar",
    balance: 8000,
    cancelledPenaltyAmount: 3500,
    cancelledPenaltyCurrency: "USD",
  };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "multaPorCobrar");
  assert.equal(result.amount, 3500);
  assert.equal(result.amountCurrency, "USD");
});

test("Anulada + MultaPorCobrar SIN cancelledPenaltyAmount/Currency (DTO legacy/cacheado): fallback al balance", () => {
  const reserva = {
    status: "Cancelled",
    cancelledMoneyContext: "MultaPorCobrar",
    balance: 8000,
    porMoneda: [{ currency: "ARS" }],
  };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "multaPorCobrar");
  assert.equal(result.amount, 8000);
  assert.equal(result.amountCurrency, "ARS");
});

test("Anulada + MultaEnRevision: NUNCA se muestra cartel (la Nota de Débito falló o está en revisión manual)", () => {
  const reserva = { status: "Cancelled", cancelledMoneyContext: "MultaEnRevision", balance: 8000 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
  assert.equal(result.label, null);
});

test("Anulada + token DESCONOCIDO (backend nuevo, front viejo): kind none, jamás el token crudo como label", () => {
  // Si el backend agrega un contexto nuevo que este front todavía no conoce, NO debemos
  // mostrar el token crudo ni inventar un cartel: se cae al fallback seguro (balance >= 0 -> none).
  const reserva = { status: "Cancelled", cancelledMoneyContext: "AlgoNuevo", balance: 4000 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
  assert.equal(result.label, null);
  assert.notEqual(result.label, "AlgoNuevo");
});

test("Anulada + Inconsistente: NUNCA se muestra nada al usuario (lo maneja el vigía interno)", () => {
  const reserva = { status: "Cancelled", cancelledMoneyContext: "Inconsistente", balance: 3000 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
  assert.equal(result.label, null);
});

test("Anulada sin cancelledMoneyContext (null explícito): no muestra nada", () => {
  const reserva = { status: "Cancelled", cancelledMoneyContext: null, balance: 0 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
  assert.equal(result.label, null);
});

// ─── Spec P2=A FIRMADA (2026-07-17): anulada SIN plata en juego → sin chip de Pago ──
// ReservaStatusChips.jsx solo pinta un chip cuando getMoneyStatus devuelve un "kind"
// que reconoce (saldoAFavorAnulada/multaPorCobrar/etc); kind:"none" = no hay <span> de
// Pago en la cabecera. Este test fija ese contrato para la reserva "anulada limpia".

test("P2=A: anulada sin saldo a favor NI multa (balance 0, isVoided true) → kind 'none' → sin chip de Pago", () => {
  const reserva = {
    status: "Cancelled",
    isVoided: true,
    cancelledMoneyContext: null,
    balance: 0,
  };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
  assert.equal(result.label, null);
  // Nunca "Sin movimientos": esa es justo la mentira que la regla 4 del modelo prohíbe.
  assert.notEqual(result.label, "Sin movimientos");
});

test("P2=A: PendingOperatorRefund limpio (sin plata) también da kind 'none'", () => {
  const reserva = {
    status: "PendingOperatorRefund",
    isVoided: true,
    cancelledMoneyContext: null,
    balance: 0,
  };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
});

// ─── B3: el circuito de anulación se lee vía isVoided, no vía comparación de string ──

test("B3: reserva con isVoided=true y contexto de multa entra a la rama de anulación aunque el status sea uno nuevo", () => {
  const reserva = {
    status: "AlgunEstadoFuturo",
    isVoided: true,
    cancelledMoneyContext: "MultaPorCobrar",
    cancelledPenaltyAmount: 1000,
    cancelledPenaltyCurrency: "ARS",
  };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "multaPorCobrar");
  assert.equal(result.label, "Multa por anulación pendiente de cobro");
});

// ─── Reservas ANULADAS sin el campo (DTO legacy, ej. cuenta del cliente) ───

test("Anulada sin campo cancelledMoneyContext + balance negativo: fallback seguro a 'Saldo a favor'", () => {
  const reserva = { status: "Cancelled", balance: -1200 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "saldoAFavorAnulada");
  assert.equal(result.label, "Saldo a favor");
});

test("Anulada sin campo cancelledMoneyContext + balance positivo: NO afirma nada (podría ser multa o dato roto)", () => {
  const reserva = { status: "Cancelled", balance: 1200 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
  assert.equal(result.label, null);
});

test("Anulada sin campo cancelledMoneyContext + balance ~0 (resto de redondeo): no muestra nada", () => {
  const reserva = { status: "PendingOperatorRefund", balance: 0.005 };
  const result = getMoneyStatus(reserva);
  assert.equal(result.kind, "none");
});

// ─── Reservas VIVAS con collectionStatus explícito ─────────────────────────

test("Vivo + SinMovimientos: 'Sin movimientos', neutro", () => {
  const result = getMoneyStatus({ status: "InManagement", collectionStatus: "SinMovimientos", balance: 0 });
  assert.equal(result.kind, "sinMovimientos");
  assert.equal(result.label, "Sin movimientos");
  assert.equal(result.tone, "neutral");
});

test("Vivo + Saldado: 'Pagada', success", () => {
  const result = getMoneyStatus({ status: "Confirmed", collectionStatus: "Saldado", balance: 0 });
  assert.equal(result.kind, "pagada");
  assert.equal(result.label, "Pagada");
  assert.equal(result.tone, "success");
});

test("Vivo + SaldoAFavor: 'A favor', success (NUNCA rojo)", () => {
  const result = getMoneyStatus({ status: "Confirmed", collectionStatus: "SaldoAFavor", balance: -300 });
  assert.equal(result.kind, "saldoAFavor");
  assert.equal(result.label, "A favor");
  assert.equal(result.tone, "success");
});

test("Vivo + ConDeuda genérico (no Confirmed, sin vencer): 'Debe', danger", () => {
  const result = getMoneyStatus({ status: "InManagement", collectionStatus: "ConDeuda", balance: 1000 });
  assert.equal(result.kind, "debe");
  assert.equal(result.label, "Debe");
  assert.equal(result.tone, "danger");
});

test("Vivo + ConDeuda + Confirmed dentro de la ventana de aviso: 'Debe — no viaja'", () => {
  const result = getMoneyStatus({
    status: "Confirmed",
    collectionStatus: "ConDeuda",
    isWithinUnpaidAlertWindow: true,
    balance: 1000,
  });
  assert.equal(result.kind, "debeNoViaja");
  assert.equal(result.label, "Debe — no viaja");
});

test("Vivo + ConDeuda + Confirmed FUERA de la ventana de aviso: 'Debe' genérico", () => {
  const result = getMoneyStatus({
    status: "Confirmed",
    collectionStatus: "ConDeuda",
    isWithinUnpaidAlertWindow: false,
    balance: 1000,
  });
  assert.equal(result.kind, "debe");
});

test("Vivo + hasOverdueDebt: 'Vencida con deuda' pisa a 'Debe' y a 'Debe — no viaja'", () => {
  const result = getMoneyStatus({
    status: "Closed",
    collectionStatus: "ConDeuda",
    hasOverdueDebt: true,
    balance: 5000,
  });
  assert.equal(result.kind, "vencidaConDeuda");
  assert.equal(result.label, "Vencida con deuda");
  assert.equal(result.tone, "danger");
});

// ─── Reservas VIVAS sin collectionStatus (DTO legacy, ej. cuenta del cliente) ──

test("Vivo sin collectionStatus + balance > 0: 'Debe' (fallback seguro por signo)", () => {
  const result = getMoneyStatus({ status: "InManagement", balance: 2500 });
  assert.equal(result.kind, "debe");
  assert.equal(result.label, "Debe");
});

test("Vivo sin collectionStatus + balance < 0: 'A favor'", () => {
  const result = getMoneyStatus({ status: "Confirmed", balance: -900 });
  assert.equal(result.kind, "saldoAFavor");
  assert.equal(result.label, "A favor");
});

test("Vivo sin collectionStatus + balance == 0: 'Sin movimientos' (NUNCA afirma 'Pagada' sin que el backend lo confirme)", () => {
  const result = getMoneyStatus({ status: "Confirmed", balance: 0 });
  assert.equal(result.kind, "sinMovimientos");
  assert.notEqual(result.label, "Pagada");
});

test("Vivo sin collectionStatus + hasOverdueDebt + balance > 0: sigue detectando 'Vencida con deuda'", () => {
  const result = getMoneyStatus({ status: "Closed", balance: 4000, hasOverdueDebt: true });
  assert.equal(result.kind, "vencidaConDeuda");
});

// ─── Caso borde: reserva null/undefined ────────────────────────────────────

test("getMoneyStatus(null) no revienta: devuelve 'none'", () => {
  assert.deepEqual(getMoneyStatus(null), { kind: "none", label: null, tone: "neutral" });
});

test("getMoneyStatus(undefined) no revienta: devuelve 'none'", () => {
  assert.deepEqual(getMoneyStatus(undefined), { kind: "none", label: null, tone: "neutral" });
});

// ─── Gate anti-fuga: ningún token crudo del backend se filtra como label ───

test("Ningún label devuelto es un token crudo del backend (camelCase/PascalCase sin traducir)", () => {
  const casosAnulados = [
    { status: "Cancelled", cancelledMoneyContext: "SaldoAFavorPendiente" },
    { status: "Cancelled", cancelledMoneyContext: "MultaPorCobrar" },
    { status: "Cancelled", cancelledMoneyContext: "MultaEnRevision" },
    { status: "Cancelled", cancelledMoneyContext: "Inconsistente" },
  ];
  const casosVivos = [
    { status: "Confirmed", collectionStatus: "SinMovimientos" },
    { status: "Confirmed", collectionStatus: "Saldado" },
    { status: "Confirmed", collectionStatus: "SaldoAFavor" },
    { status: "Confirmed", collectionStatus: "ConDeuda" },
  ];
  const tokensCrudos = [
    "SaldoAFavorPendiente",
    "MultaPorCobrar",
    "MultaEnRevision",
    "Inconsistente",
    "SinMovimientos",
    "Saldado",
    "SaldoAFavor",
    "ConDeuda",
  ];

  [...casosAnulados, ...casosVivos].forEach((reserva) => {
    const { label } = getMoneyStatus(reserva);
    if (label !== null) {
      assert.ok(!tokensCrudos.includes(label), `El label "${label}" es un token crudo sin traducir`);
    }
  });
});
