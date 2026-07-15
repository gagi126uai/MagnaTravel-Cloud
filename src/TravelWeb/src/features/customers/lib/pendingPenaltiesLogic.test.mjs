/**
 * Tests de la lógica pura del bloque "Multa pendiente de cobro" de la cuenta del
 * cliente (spec 2026-07-15).
 *
 * Corren con: node --test src/features/customers/lib/pendingPenaltiesLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { formatCurrency } from "../../../lib/utils.js";
import {
  PENDING_PENALTY_STATUS,
  textoChipEstadoMulta,
  debeMostrarBloqueMultasPendientes,
  armarRecuadroMultaPorMoneda,
  textoContextoMultaAnuladaCuenta,
  resolverFilaReservaAnuladaCuenta,
} from "./pendingPenaltiesLogic.js";

// ============================================================================
// Sección 1: textoChipEstadoMulta — las tres voces exactas de la spec (§3)
// ============================================================================

test("pendingCollection → chip 'Pendiente de cobro' en rose", () => {
  assert.deepEqual(textoChipEstadoMulta(PENDING_PENALTY_STATUS.PENDING_COLLECTION), {
    texto: "Pendiente de cobro",
    tono: "rose",
  });
});

test("issuing → chip 'Comprobante en camino' en ámbar", () => {
  assert.deepEqual(textoChipEstadoMulta(PENDING_PENALTY_STATUS.ISSUING), {
    texto: "Comprobante en camino",
    tono: "amber",
  });
});

test("underReview → chip 'En revisión' en ámbar", () => {
  assert.deepEqual(textoChipEstadoMulta(PENDING_PENALTY_STATUS.UNDER_REVIEW), {
    texto: "En revisión",
    tono: "amber",
  });
});

test("status desconocido (dato futuro del backend) → cae al chip mas conservador, nunca texto crudo", () => {
  const chip = textoChipEstadoMulta("algoQueTodaviaNoExiste");
  assert.equal(chip.texto, "En revisión");
  assert.equal(chip.tono, "amber");
});

test("status undefined/null → mismo fallback conservador (no rompe)", () => {
  assert.equal(textoChipEstadoMulta(undefined).texto, "En revisión");
  assert.equal(textoChipEstadoMulta(null).texto, "En revisión");
});

// ============================================================================
// Sección 2: debeMostrarBloqueMultasPendientes — visibilidad del bloque (§4)
// ============================================================================

test("items vacío → el bloque NO se dibuja", () => {
  assert.equal(debeMostrarBloqueMultasPendientes({ items: [], totalsByCurrency: [] }), false);
});

test("pendingPenalties ausente (undefined) → el bloque NO se dibuja", () => {
  assert.equal(debeMostrarBloqueMultasPendientes(undefined), false);
});

test("pendingPenalties null → el bloque NO se dibuja", () => {
  assert.equal(debeMostrarBloqueMultasPendientes(null), false);
});

test("items con al menos una fila → el bloque SÍ se dibuja", () => {
  assert.equal(
    debeMostrarBloqueMultasPendientes({
      items: [{ reservaPublicId: "x", numeroReserva: "R-1042", name: "Cancún", amount: 45000, currency: "ARS", status: "pendingCollection" }],
      totalsByCurrency: [{ currency: "ARS", firmAmount: 45000, notYetIssuedAmount: 0 }],
    }),
    true
  );
});

// ============================================================================
// Sección 3: armarRecuadroMultaPorMoneda — número grande + segunda línea (§2)
// ============================================================================

test("solo deuda firme (sin nada pendiente de comprobante) → numero grande rojo, sin segunda linea", () => {
  const recuadro = armarRecuadroMultaPorMoneda({ currency: "ARS", firmAmount: 45000, notYetIssuedAmount: 0 });
  assert.equal(recuadro.currency, "ARS");
  assert.equal(recuadro.montoGrandeTexto, formatCurrency(45000, "ARS"));
  assert.equal(recuadro.colorMontoGrande, "rose");
  assert.equal(recuadro.etiquetaMontoGrande, null);
  assert.equal(recuadro.segundaLineaAmbar, null);
});

test("deuda firme + plata sin comprobante todavia → numero grande rojo + segunda linea ambar con el monto sin comprobante", () => {
  const recuadro = armarRecuadroMultaPorMoneda({ currency: "USD", firmAmount: 50, notYetIssuedAmount: 150 });
  assert.equal(recuadro.montoGrandeTexto, formatCurrency(50, "USD"));
  assert.equal(recuadro.colorMontoGrande, "rose");
  assert.equal(recuadro.segundaLineaAmbar, `· ${formatCurrency(150, "USD")} sin comprobante todavía`);
});

test("firmAmount en cero (todo sin comprobante todavia) → NO muestra $0 rojo enganioso: numero grande pasa a ambar con la etiqueta", () => {
  const recuadro = armarRecuadroMultaPorMoneda({ currency: "USD", firmAmount: 0, notYetIssuedAmount: 150 });
  assert.equal(recuadro.montoGrandeTexto, formatCurrency(150, "USD"));
  assert.equal(recuadro.colorMontoGrande, "amber");
  assert.equal(recuadro.etiquetaMontoGrande, "sin comprobante todavía");
  assert.equal(recuadro.segundaLineaAmbar, null, "no hay que repetir el monto en una segunda linea");
});

test("ambos en cero (caso defensivo, no deberia llegar del backend) → numero grande ambar en $0, sin romper", () => {
  const recuadro = armarRecuadroMultaPorMoneda({ currency: "ARS", firmAmount: 0, notYetIssuedAmount: 0 });
  assert.equal(recuadro.colorMontoGrande, "amber");
  assert.equal(recuadro.montoGrandeTexto, formatCurrency(0, "ARS"));
});

// ============================================================================
// Sección 4: textoContextoMultaAnuladaCuenta — voz de la solapa Reservas (§5)
// ============================================================================

test("MultaPorCobrar → 'multa por anulación · pendiente de cobro'", () => {
  assert.equal(textoContextoMultaAnuladaCuenta("MultaPorCobrar"), "multa por anulación · pendiente de cobro");
});

test("MultaEnRevision → 'multa por anulación · sin comprobante todavía'", () => {
  assert.equal(textoContextoMultaAnuladaCuenta("MultaEnRevision"), "multa por anulación · sin comprobante todavía");
});

test("SaldoAFavorPendiente → null (no es multa, la resuelve la logica de siempre)", () => {
  assert.equal(textoContextoMultaAnuladaCuenta("SaldoAFavorPendiente"), null);
});

test("Inconsistente → null (dato roto, no se etiqueta como multa)", () => {
  assert.equal(textoContextoMultaAnuladaCuenta("Inconsistente"), null);
});

test("sin contexto (null/undefined, reserva viva) → null", () => {
  assert.equal(textoContextoMultaAnuladaCuenta(null), null);
  assert.equal(textoContextoMultaAnuladaCuenta(undefined), null);
});

// ============================================================================
// Sección 5: resolverFilaReservaAnuladaCuenta — fix de revisión N1/N2/N3:
// qué monto (si corresponde alguno) muestra CADA fila de la solapa Reservas.
// ============================================================================

test("MultaPorCobrar con monto explicito en USD (multimoneda) → usa la moneda de la multa, NO el fallback de la pantalla", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: "MultaPorCobrar",
    cancelledPenaltyAmount: 150,
    cancelledPenaltyCurrency: "USD",
    balance: 150,
    moneyStatusKind: "multaPorCobrar",
    monedaFallback: "ARS", // la pantalla arrancaria en ARS si porMoneda[0] fuera ARS; no debe filtrarse acá
  });
  assert.equal(fila.texto, "multa por anulación · pendiente de cobro");
  assert.equal(fila.montoTexto, formatCurrency(150, "USD"));
  assert.equal(fila.tono, "amber");
});

test("MultaPorCobrar SIN cancelledPenaltyAmount (dato legacy) → NO inventa '$0,00': monto null, solo la voz (N2)", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: "MultaPorCobrar",
    cancelledPenaltyAmount: null,
    cancelledPenaltyCurrency: null,
    balance: 45000,
    moneyStatusKind: "multaPorCobrar",
    monedaFallback: "ARS",
  });
  assert.equal(fila.texto, "multa por anulación · pendiente de cobro");
  assert.equal(fila.montoTexto, null);
  assert.equal(fila.tono, "amber");
});

test("MultaPorCobrar con cancelledPenaltyAmount undefined (mismo caso que null) → tambien monto null", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: "MultaPorCobrar",
    cancelledPenaltyAmount: undefined,
    cancelledPenaltyCurrency: undefined,
    balance: 45000,
    moneyStatusKind: "multaPorCobrar",
    monedaFallback: "ARS",
  });
  assert.equal(fila.montoTexto, null);
});

test("MultaEnRevision → SIEMPRE monto null, aunque el balance de la reserva tenga un numero grande (N1: balance es legacy/neto, no se usa)", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: "MultaEnRevision",
    cancelledPenaltyAmount: null, // el backend nunca lo llena para este caso
    cancelledPenaltyCurrency: null,
    balance: 999999,
    moneyStatusKind: "none", // moneyStatus.js esconde este caso para el vendedor
    monedaFallback: "USD",
  });
  assert.equal(fila.texto, "multa por anulación · sin comprobante todavía");
  assert.equal(fila.montoTexto, null);
  assert.equal(fila.tono, "amber");
});

test("saldo a favor (sin contexto de multa, moneyStatusKind saldoAFavorAnulada) → usa el balance, en emerald", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: "SaldoAFavorPendiente",
    cancelledPenaltyAmount: null,
    cancelledPenaltyCurrency: null,
    balance: -5000, // negativo: el cliente pago de mas
    moneyStatusKind: "saldoAFavorAnulada",
    monedaFallback: "ARS",
  });
  assert.equal(fila.texto, "a favor");
  assert.equal(fila.montoTexto, formatCurrency(5000, "ARS"));
  assert.equal(fila.tono, "emerald");
});

test("kind desconocido (dato futuro que este archivo todavia no conoce) → neutral, sin texto ni monto, NUNCA 'a favor' por default (N3)", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: null,
    cancelledPenaltyAmount: null,
    cancelledPenaltyCurrency: null,
    balance: 100,
    moneyStatusKind: "unKindQueTodaviaNoExiste",
    monedaFallback: "ARS",
  });
  assert.equal(fila.texto, null);
  assert.equal(fila.montoTexto, null);
  assert.equal(fila.tono, "neutral");
});

test("Inconsistente (dato roto, sin ND que respalde la deuda) → neutral, no se afirma nada", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: "Inconsistente",
    cancelledPenaltyAmount: null,
    cancelledPenaltyCurrency: null,
    balance: 300,
    moneyStatusKind: "none",
    monedaFallback: "ARS",
  });
  assert.equal(fila.tono, "neutral");
});

test("reserva anulada sin nada pendiente (contexto null, kind 'none') → neutral", () => {
  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: null,
    cancelledPenaltyAmount: null,
    cancelledPenaltyCurrency: null,
    balance: 0,
    moneyStatusKind: "none",
    monedaFallback: "ARS",
  });
  assert.equal(fila.tono, "neutral");
  assert.equal(fila.montoTexto, null);
});
