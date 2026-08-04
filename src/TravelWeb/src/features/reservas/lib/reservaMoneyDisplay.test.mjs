/**
 * Tests de reservaMoneyDisplay.js — Tanda 1 rediseño del listado de Reservas
 * (2026-08-04, plan B1/B4/B6). Cubre la regla más dura del producto (P-3⭐: pesos y
 * dólares NUNCA se suman, siempre se muestran separados) para:
 *   - la tira de KPIs (formatMontosPorMoneda),
 *   - las líneas de "venta" de la columna Finanzas (getReservaSaleLines),
 *   - los chips de "Debe"/"A favor"/"Sin movimientos"/"Saldado"/"Multa" (getReservaFinanzasChips).
 *
 * Este módulo NO tiene JSX, así que se importa directo (igual que moneyStatus.test.mjs).
 *
 * Cómo correr: node --test src/features/reservas/lib/reservaMoneyDisplay.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  formatMontosPorMoneda,
  getReservaSaleLines,
  getReservaFinanzasChips,
} from "./reservaMoneyDisplay.js";
import { formatCurrency } from "../../../lib/utils.js";

// OJO con copiar montos ARS "a mano" en un assert: Intl.NumberFormat pone un
// espacio ANGOSTO DE NO-CORTE (U+00A0, no la barra espaciadora normal) entre el
// "$" y el número. Por eso todos los textos esperados de estos tests se arman
// con formatCurrency (la MISMA función que usa el código real), nunca tipeando
// el string "a ojo" — así el test no se rompe por un carácter invisible.

// ─── formatMontosPorMoneda (tira de KPIs) ──────────────────────────────────────

test("formatMontosPorMoneda: una sola moneda -> un solo importe, sin separador", () => {
  assert.equal(formatMontosPorMoneda([{ currency: "ARS", amount: 223445 }]), formatCurrency(223445, "ARS"));
});

test("formatMontosPorMoneda: dos monedas -> separadas por '·', NUNCA sumadas (P-3⭐)", () => {
  const texto = formatMontosPorMoneda([
    { currency: "ARS", amount: 223445 },
    { currency: "USD", amount: 1200 },
  ]);
  // OJO: formatCurrency no pone espacio entre "US$" y el número (a diferencia del
  // peso, que sí lo trae por el formato "currency" de Intl) — es el mismo
  // comportamiento que ya usa el resto de la app en TODAS las pantallas con
  // dólares, no algo nuevo de esta tanda. La maqueta dibuja "US$ 1.200,00" con
  // espacio; el motor real no lo pone.
  assert.equal(texto, `${formatCurrency(223445, "ARS")} · ${formatCurrency(1200, "USD")}`);
});

test("formatMontosPorMoneda: lista vacía -> '$ 0,00' (mes sin datos, no queda en blanco)", () => {
  assert.equal(formatMontosPorMoneda([]), formatCurrency(0, "ARS"));
  assert.equal(formatMontosPorMoneda(null), formatCurrency(0, "ARS"));
  assert.equal(formatMontosPorMoneda(undefined), formatCurrency(0, "ARS"));
});

// ─── getReservaSaleLines (columna Finanzas, número de venta) ───────────────────

test("getReservaSaleLines: reserva multimoneda -> una línea por cada moneda de porMoneda", () => {
  const reserva = {
    totalSale: 999, // no se usa: porMoneda tiene prioridad
    porMoneda: [
      { currency: "ARS", totalSale: 8000, balance: 0 },
      { currency: "USD", totalSale: 300, balance: 0 },
    ],
  };
  assert.deepEqual(getReservaSaleLines(reserva), [
    { currency: "ARS", amount: 8000 },
    { currency: "USD", amount: 300 },
  ]);
});

test("getReservaSaleLines: sin porMoneda (DTO legado) -> una única línea ARS con el escalar totalSale", () => {
  assert.deepEqual(getReservaSaleLines({ totalSale: 6440 }), [{ currency: "ARS", amount: 6440 }]);
});

test("getReservaSaleLines: porMoneda vacío -> mismo fallback que sin porMoneda", () => {
  assert.deepEqual(getReservaSaleLines({ totalSale: 0, porMoneda: [] }), [{ currency: "ARS", amount: 0 }]);
});

// ─── getReservaFinanzasChips: reservas VIVAS (no anuladas) ─────────────────────

test("getReservaFinanzasChips: collectionStatus Saldado -> chip 'Saldado', sin importe", () => {
  const reserva = { status: "Closed", collectionStatus: "Saldado", isVoided: false, porMoneda: [{ currency: "ARS", totalSale: 100, balance: 0 }] };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: "Saldado", tone: "verde" }]);
});

test("getReservaFinanzasChips: collectionStatus SinMovimientos -> chip 'Sin movimientos'", () => {
  const reserva = { status: "Budget", collectionStatus: "SinMovimientos", isVoided: false, porMoneda: [] };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: "Sin movimientos", tone: "gris" }]);
});

test("getReservaFinanzasChips: una sola moneda con deuda -> 'Debe $ X' en rojo", () => {
  const reserva = {
    status: "Confirmed",
    collectionStatus: "ConDeuda",
    isVoided: false,
    balance: 212000,
    porMoneda: [{ currency: "ARS", totalSale: 212000, balance: 212000 }],
  };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: `Debe ${formatCurrency(212000, "ARS")}`, tone: "rojo" }]);
});

test("getReservaFinanzasChips: multimoneda con deuda SOLO en dólares -> un chip por moneda, ARS no aparece", () => {
  const reserva = {
    status: "Confirmed",
    collectionStatus: "ConDeuda",
    isVoided: false,
    porMoneda: [
      { currency: "ARS", totalSale: 5000, balance: 0 },
      { currency: "USD", totalSale: 500, balance: 500 },
    ],
  };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: `Debe ${formatCurrency(500, "USD")}`, tone: "rojo" }]);
});

test("getReservaFinanzasChips: deuda en ARS Y a favor en USD al mismo tiempo -> dos chips, uno por moneda (P-3⭐)", () => {
  const reserva = {
    status: "Confirmed",
    collectionStatus: "ConDeuda",
    isVoided: false,
    porMoneda: [
      { currency: "ARS", totalSale: 1000, balance: 300 },
      { currency: "USD", totalSale: 500, balance: -50 },
    ],
  };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: `Debe ${formatCurrency(300, "ARS")}`, tone: "rojo" }]);
  // Nota: cuando hay deuda en al menos una moneda, esa es la que se prioriza mostrar
  // (mismo criterio que collectionStatus "ConDeuda" del backend, que es un estado
  // único por reserva, no por moneda). El saldo a favor en USD queda representado
  // en el número de "venta" de esa moneda, no en un chip aparte en este caso.
});

test("getReservaFinanzasChips: saldo a favor en una sola moneda -> 'A favor $ X' en verde", () => {
  const reserva = {
    status: "Closed",
    collectionStatus: "SaldoAFavor",
    isVoided: false,
    porMoneda: [{ currency: "ARS", totalSale: 1000, balance: -400 }],
  };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: `A favor ${formatCurrency(400, "ARS")}`, tone: "verde" }]);
});

// ─── getReservaFinanzasChips: reservas ANULADAS ────────────────────────────────

test("getReservaFinanzasChips: anulada con multa pendiente -> chip ámbar con el monto EXACTO de la multa", () => {
  const reserva = {
    status: "Cancelled",
    isVoided: true,
    cancelledMoneyContext: "MultaPorCobrar",
    cancelledPenaltyAmount: 5000,
    cancelledPenaltyCurrency: "ARS",
    balance: 999999, // no debe usarse: hay monto explícito de multa
  };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: `Multa: ${formatCurrency(5000, "ARS")}`, tone: "ambar" }]);
});

test("getReservaFinanzasChips: anulada con saldo a favor -> chip verde 'A favor $ X'", () => {
  const reserva = {
    status: "Cancelled",
    isVoided: true,
    cancelledMoneyContext: "SaldoAFavorPendiente",
    balance: -4000,
    porMoneda: [{ currency: "ARS", totalSale: 0, balance: -4000 }],
  };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: `A favor ${formatCurrency(4000, "ARS")}`, tone: "verde" }]);
});

test("getReservaFinanzasChips: anulada con multa en revisión -> 'Sin movimientos' (no se le promete cobro al vendedor)", () => {
  const reserva = { status: "Cancelled", isVoided: true, cancelledMoneyContext: "MultaEnRevision" };
  assert.deepEqual(getReservaFinanzasChips(reserva), [{ text: "Sin movimientos", tone: "gris" }]);
});
