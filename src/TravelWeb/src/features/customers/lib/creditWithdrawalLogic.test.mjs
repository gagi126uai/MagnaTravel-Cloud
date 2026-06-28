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
 *   - validarAplicacion (kind 3): sin reserva destino, monto inválido, monto excede saldo
 *   - armarPayloadAplicacion (kind 3): payload para POST /customers/{id}/credit/apply
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

// ─── Tests de validarAplicacion (kind 3) ──────────────────────────────────────

// Replica local de la función (mismo patrón que el resto del archivo)
function validarAplicacion(monto, saldoDisponible, targetReservaPublicId) {
  if (!targetReservaPublicId) {
    return "Elegí una reserva destino antes de confirmar.";
  }
  const montoNum = parseFloat(monto);
  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }
  if (montoNum > saldoDisponible) {
    return `El monto no puede superar el saldo disponible (${saldoDisponible}).`;
  }
  return null;
}

test("validarAplicacion - sin reserva destino devuelve error aunque el monto sea válido", () => {
  const resultado = validarAplicacion(500, 1000, null);
  assert.ok(resultado !== null, "Debe haber error");
  assert.ok(resultado.includes("reserva"), "El mensaje debe mencionar 'reserva'");
});

test("validarAplicacion - sin reserva y sin monto: prioriza error de reserva", () => {
  const resultado = validarAplicacion("", 1000, null);
  assert.ok(resultado !== null);
  // El error de "reserva faltante" debe aparecer antes que el de monto
  assert.ok(resultado.includes("reserva"));
});

test("validarAplicacion - con reserva pero monto 0 devuelve error de monto", () => {
  const resultado = validarAplicacion(0, 1000, "guid-reserva-123");
  assert.ok(resultado !== null);
  assert.ok(resultado.includes("mayor a 0"));
});

test("validarAplicacion - con reserva pero monto vacío devuelve error de monto", () => {
  const resultado = validarAplicacion("", 1000, "guid-reserva-123");
  assert.ok(resultado !== null);
  assert.ok(resultado.includes("mayor a 0"));
});

test("validarAplicacion - monto excede saldo disponible devuelve error con el saldo", () => {
  const resultado = validarAplicacion(1500, 1000, "guid-reserva-123");
  assert.ok(resultado !== null);
  assert.ok(resultado.includes("1000"));
});

test("validarAplicacion - monto igual al saldo disponible es válido", () => {
  const resultado = validarAplicacion(1000, 1000, "guid-reserva-123");
  assert.strictEqual(resultado, null);
});

test("validarAplicacion - monto menor al saldo disponible es válido", () => {
  const resultado = validarAplicacion(500, 1000, "guid-reserva-123");
  assert.strictEqual(resultado, null);
});

test("validarAplicacion - string numérico como monto funciona igual que número", () => {
  const resultado = validarAplicacion("450.50", 1000, "guid-reserva-123");
  assert.strictEqual(resultado, null);
});

// ─── Tests de armarPayloadAplicacion (kind 3) ─────────────────────────────────

function armarPayloadAplicacion(currency, amount, targetReservaPublicId) {
  return {
    currency,
    amount: parseFloat(amount),
    targetReservaPublicId,
  };
}

test("armarPayloadAplicacion - incluye currency, amount y targetReservaPublicId", () => {
  const payload = armarPayloadAplicacion("ARS", 500, "guid-reserva-abc");
  assert.strictEqual(payload.currency, "ARS");
  assert.strictEqual(payload.amount, 500);
  assert.strictEqual(payload.targetReservaPublicId, "guid-reserva-abc");
});

test("armarPayloadAplicacion - convierte amount de string a número", () => {
  const payload = armarPayloadAplicacion("USD", "250.75", "guid-reserva-xyz");
  assert.strictEqual(typeof payload.amount, "number");
  assert.strictEqual(payload.amount, 250.75);
});

test("armarPayloadAplicacion - funciona para USD", () => {
  const payload = armarPayloadAplicacion("USD", 100, "guid-reserva-usd");
  assert.strictEqual(payload.currency, "USD");
  assert.strictEqual(payload.amount, 100);
});

test("armarPayloadAplicacion - payload tiene exactamente 3 campos (currency, amount, targetReservaPublicId)", () => {
  const payload = armarPayloadAplicacion("ARS", 300, "guid-reserva-444");
  const campos = Object.keys(payload);
  assert.strictEqual(campos.length, 3, "El payload no debe tener campos extra");
  assert.ok(campos.includes("currency"));
  assert.ok(campos.includes("amount"));
  assert.ok(campos.includes("targetReservaPublicId"));
});

// ─── Tests del monto sugerido kind 3 cliente (debt-by-reserva) ───────────────
// Valida la regla: monto sugerido = min(deuda de la reserva en esa moneda, saldo disponible).
// Esta lógica vive en el click handler del listbox de UsarSaldoAFavorInline.

/**
 * Simula el cálculo del monto sugerido al seleccionar una reserva en el picker
 * de "Aplicar a otra reserva" (kind 3) cuando el nuevo endpoint está cablebado.
 */
function calcularMontoSugeridoCliente(reserva, moneda, saldoDisponible) {
  const lineaDeuda = (reserva.debtByCurrency ?? []).find((c) => c.currency === moneda);
  const deudaEnMoneda = lineaDeuda?.amount ?? null;
  if (deudaEnMoneda != null && deudaEnMoneda > 0) {
    return Math.min(deudaEnMoneda, saldoDisponible);
  }
  // Si no hay deuda disponible (endpoint legacy / shape vieja), dejamos el saldo completo
  return saldoDisponible;
}

test("monto sugerido cliente - deuda < saldo disponible → sugerencia = deuda", () => {
  const reserva = {
    reservaPublicId: "r-1",
    numeroReserva: "R-001",
    debtByCurrency: [{ currency: "ARS", amount: 500 }],
  };
  const resultado = calcularMontoSugeridoCliente(reserva, "ARS", 1000);
  assert.strictEqual(resultado, 500);
});

test("monto sugerido cliente - deuda > saldo disponible → sugerencia = saldo disponible", () => {
  const reserva = {
    reservaPublicId: "r-2",
    numeroReserva: "R-002",
    debtByCurrency: [{ currency: "ARS", amount: 2000 }],
  };
  const resultado = calcularMontoSugeridoCliente(reserva, "ARS", 800);
  assert.strictEqual(resultado, 800);
});

test("monto sugerido cliente - deuda igual al saldo disponible → sugerencia = ese valor", () => {
  const reserva = {
    reservaPublicId: "r-3",
    debtByCurrency: [{ currency: "USD", amount: 300 }],
  };
  const resultado = calcularMontoSugeridoCliente(reserva, "USD", 300);
  assert.strictEqual(resultado, 300);
});

test("monto sugerido cliente - sin deuda en la moneda → fallback al saldo disponible", () => {
  const reserva = {
    reservaPublicId: "r-4",
    debtByCurrency: [{ currency: "ARS", amount: 500 }],
  };
  // La reserva tiene deuda en ARS pero se pregunta por USD
  const resultado = calcularMontoSugeridoCliente(reserva, "USD", 1000);
  // Sin lineaDeuda para USD, cae al fallback (saldoDisponible)
  assert.strictEqual(resultado, 1000);
});

test("monto sugerido cliente - debtByCurrency vacío → fallback al saldo disponible", () => {
  const reserva = {
    reservaPublicId: "r-5",
    debtByCurrency: [],
  };
  const resultado = calcularMontoSugeridoCliente(reserva, "ARS", 500);
  assert.strictEqual(resultado, 500);
});

// ─── Test B1 del picker cliente: el ID de reserva viaja correctamente ──────────
// Verifica que al armar el payload se usa reservaPublicId (no publicId ni undefined).

test("armarPayloadAplicacion - usa reservaPublicId del DTO del nuevo endpoint (no publicId)", () => {
  // Shape real del nuevo endpoint GET /customers/{id}/account/debt-by-reserva
  const reservaDelNuevoEndpoint = {
    reservaPublicId: "guid-real-123",
    numeroReserva: "R-0045",
    fileName: "Viaje a Bariloche",
    debtByCurrency: [{ currency: "ARS", amount: 1500 }],
  };

  // Simulamos lo que hace el componente al confirmar kind 3:
  const targetReservaPublicId =
    reservaDelNuevoEndpoint.reservaPublicId ??
    // getPublicId fallback (no aplica acá, pero simula la lógica real)
    reservaDelNuevoEndpoint.publicId;

  const payload = armarPayloadAplicacion("ARS", 500, targetReservaPublicId);

  assert.strictEqual(payload.targetReservaPublicId, "guid-real-123",
    "El guid tiene que viajar en el payload (no null ni undefined)");
  assert.notStrictEqual(payload.targetReservaPublicId, null);
  assert.notStrictEqual(payload.targetReservaPublicId, undefined);
});
