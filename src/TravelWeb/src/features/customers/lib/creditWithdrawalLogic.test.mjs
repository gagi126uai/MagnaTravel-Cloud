/**
 * Tests de lógica pura para el flujo "Usar saldo a favor del cliente".
 *
 * Testea las funciones exportadas de creditWithdrawalLogic.js.
 * Corren con Node puro sin bundler ni React.
 *
 * Cómo correr: node --test src/features/customers/lib/creditWithdrawalLogic.test.mjs
 *
 * Decisiones cubiertas:
 *   - validarMontoRetiro: monto 0, negativo, mayor al saldo, igual al saldo, parcial
 *   - formatearDescripcionEntry: ARS, USD, sin reserva de origen, con reserva
 *   - armarPayloadRetiro: kind 0 (KeptAsCredit), kind 1 (Efectivo), kind 2 (Transferencia)
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura replicada (sin import de módulo para correr con Node puro) ────
// Patrón idéntico al del resto de tests .mjs del proyecto.
// Si cambia la función en creditWithdrawalLogic.js, actualizar acá también.

function validarMontoRetiro(monto, saldoDisp) {
  const montoNum = parseFloat(monto);

  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }

  if (montoNum > saldoDisp) {
    return `El monto no puede superar el saldo disponible (${saldoDisp}).`;
  }

  return null;
}

function formatearDescripcionEntry(entry) {
  if (!entry) return "";

  const { remainingBalance, creditedAmount, currency, originReservaNumber } = entry;

  const simbolo = currency === "USD" ? "US$" : "$";

  const parteRemaining = `Quedan ${simbolo}${Number(remainingBalance || 0).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const parteDe        = `de ${simbolo}${Number(creditedAmount || 0).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const parteMoneda    = `· ${currency}`;
  const parteOrigen    = originReservaNumber ? `· origen: reserva ${originReservaNumber}` : "";

  return [parteRemaining, parteDe, parteMoneda, parteOrigen].filter(Boolean).join(" ");
}

function armarPayloadRetiro(kind, amount, extras = {}) {
  if (kind === 0) {
    return { kind: 0, amount: 0 };
  }

  const payload = {
    kind,
    amount: parseFloat(amount),
  };

  if (extras.reference) {
    payload.reference = extras.reference;
  }
  if (extras.paymentMethodOverride) {
    payload.paymentMethodOverride = extras.paymentMethodOverride;
  }

  return payload;
}

// ─── Tests de validarMontoRetiro ──────────────────────────────────────────────

test("validarMontoRetiro - monto vacío devuelve error", () => {
  const resultado = validarMontoRetiro("", 1000);
  assert.strictEqual(resultado, "El monto tiene que ser mayor a 0.");
});

test("validarMontoRetiro - monto 0 devuelve error", () => {
  const resultado = validarMontoRetiro(0, 1000);
  assert.strictEqual(resultado, "El monto tiene que ser mayor a 0.");
});

test("validarMontoRetiro - monto negativo devuelve error", () => {
  const resultado = validarMontoRetiro(-50, 1000);
  assert.strictEqual(resultado, "El monto tiene que ser mayor a 0.");
});

test("validarMontoRetiro - monto mayor al saldo disponible devuelve error con el saldo", () => {
  const resultado = validarMontoRetiro(1500, 1000);
  assert.ok(resultado !== null, "Tiene que haber error");
  assert.ok(resultado.includes("1000"), "El mensaje tiene que mencionar el saldo disponible");
});

test("validarMontoRetiro - monto igual al saldo disponible es válido", () => {
  const resultado = validarMontoRetiro(1000, 1000);
  assert.strictEqual(resultado, null);
});

test("validarMontoRetiro - monto parcial (menor al saldo) es válido", () => {
  const resultado = validarMontoRetiro(500, 1000);
  assert.strictEqual(resultado, null);
});

test("validarMontoRetiro - monto decimal válido", () => {
  const resultado = validarMontoRetiro(99.99, 100);
  assert.strictEqual(resultado, null);
});

test("validarMontoRetiro - string numérico válido (como viene del input HTML)", () => {
  const resultado = validarMontoRetiro("250.50", 500);
  assert.strictEqual(resultado, null);
});

// ─── Tests de formatearDescripcionEntry ───────────────────────────────────────

test("formatearDescripcionEntry - null devuelve string vacío", () => {
  const resultado = formatearDescripcionEntry(null);
  assert.strictEqual(resultado, "");
});

test("formatearDescripcionEntry - entry ARS con reserva de origen", () => {
  const entry = {
    remainingBalance: 1500,
    creditedAmount: 2000,
    currency: "ARS",
    originReservaNumber: "2024/001",
  };
  const resultado = formatearDescripcionEntry(entry);

  // Verifica que usa $ para ARS (no US$)
  assert.ok(resultado.includes("$1"), "Debe usar símbolo $");
  assert.ok(!resultado.includes("US$"), "No debe usar US$");
  assert.ok(resultado.includes("ARS"), "Debe mencionar la moneda");
  assert.ok(resultado.includes("2024/001"), "Debe mencionar el número de reserva");
});

test("formatearDescripcionEntry - entry USD con reserva de origen", () => {
  const entry = {
    remainingBalance: 200,
    creditedAmount: 500,
    currency: "USD",
    originReservaNumber: "2024/002",
  };
  const resultado = formatearDescripcionEntry(entry);

  // Verifica que usa US$ para USD (no $)
  assert.ok(resultado.includes("US$"), "Debe usar símbolo US$");
  assert.ok(resultado.includes("USD"), "Debe mencionar la moneda");
  assert.ok(resultado.includes("2024/002"), "Debe mencionar el número de reserva");
});

test("formatearDescripcionEntry - sin reserva de origen no muestra 'origen:'", () => {
  const entry = {
    remainingBalance: 300,
    creditedAmount: 300,
    currency: "ARS",
    originReservaNumber: null,
  };
  const resultado = formatearDescripcionEntry(entry);
  assert.ok(!resultado.includes("origen:"), "No debe incluir texto de origen cuando es null");
});

// ─── Tests de armarPayloadRetiro ──────────────────────────────────────────────

test("armarPayloadRetiro - KeptAsCredit (kind 0) siempre amount 0", () => {
  const payload = armarPayloadRetiro(0, 500);
  assert.strictEqual(payload.kind, 0);
  assert.strictEqual(payload.amount, 0);
});

test("armarPayloadRetiro - KeptAsCredit ignora el monto pasado", () => {
  const payload = armarPayloadRetiro(0, 9999);
  assert.strictEqual(payload.amount, 0, "Debe ser 0 aunque se pase 9999");
});

test("armarPayloadRetiro - PhysicalCash (kind 1) lleva el monto", () => {
  const payload = armarPayloadRetiro(1, 300);
  assert.strictEqual(payload.kind, 1);
  assert.strictEqual(payload.amount, 300);
});

test("armarPayloadRetiro - Transfer (kind 2) lleva el monto", () => {
  const payload = armarPayloadRetiro(2, 1200.50);
  assert.strictEqual(payload.kind, 2);
  assert.strictEqual(payload.amount, 1200.50);
});

test("armarPayloadRetiro - Transfer con referencia incluye el campo reference", () => {
  const payload = armarPayloadRetiro(2, 500, { reference: "TRANSF-001" });
  assert.strictEqual(payload.reference, "TRANSF-001");
});

test("armarPayloadRetiro - sin extras no incluye campos opcionales", () => {
  const payload = armarPayloadRetiro(1, 200);
  assert.ok(!("reference" in payload), "No debe incluir reference si no se pasa");
  assert.ok(!("paymentMethodOverride" in payload), "No debe incluir paymentMethodOverride si no se pasa");
});

test("armarPayloadRetiro - string numérico como monto se convierte a número", () => {
  const payload = armarPayloadRetiro(2, "450.75");
  assert.strictEqual(typeof payload.amount, "number");
  assert.strictEqual(payload.amount, 450.75);
});
