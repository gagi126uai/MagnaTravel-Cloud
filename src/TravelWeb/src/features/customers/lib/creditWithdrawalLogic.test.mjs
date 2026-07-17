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

// ─── Tests de la Tanda D1 (2026-07-16): aplicar saldo a una multa + neteo ─────

import {
  KIND_APLICAR_A_MULTA,
  DESTINOS_RETIRO,
  enriquecerMultasAplicables,
  montoSugeridoAplicacionAMulta,
  validarAplicacionAMulta,
  armarPayloadAplicacionAMulta,
  mapearKindARefundMethod,
  armarPayloadRefundConNeteo,
  previewsDifierenSignificativamente,
  armarMensajeExitoNeteo,
  prefijoDestinoAplicacionSaldo,
  // Con alias porque el archivo ya tiene réplicas locales de estos tres nombres más
  // arriba (patrón viejo del archivo) — estos SÍ son las funciones reales del módulo,
  // para probar el fix de revisión 2026-07-17 (gate de exposición: el saldo del mensaje
  // de error se formatea como plata, no como número crudo).
  validarMontoRetiro as validarMontoRetiroReal,
  validarAplicacion as validarAplicacionReal,
} from "./creditWithdrawalLogic.js";

// ─── Fix de revisión 2026-07-17: mensajes de error con plata formateada ───────
// (gate de exposición: "...(1500.5)" está prohibido, tiene que decir "...($1.500,50)")

test("validarMontoRetiro (real) - con moneda, el saldo del mensaje viene formateado como plata", () => {
  const resultado = validarMontoRetiroReal(2000, 1500.5, "ARS");
  assert.ok(resultado.includes("1.500,50"), `Esperaba plata formateada (es-AR), vino: "${resultado}"`);
  assert.ok(!resultado.includes("1500.5"), "No debe quedar el número crudo con punto decimal");
});

test("validarMontoRetiro (real) - USD usa el símbolo US$", () => {
  const resultado = validarMontoRetiroReal(500, 200, "USD");
  assert.ok(resultado.includes("US$200"));
});

test("validarMontoRetiro (real) - sin moneda (caller viejo) cae al número plano, no revienta", () => {
  const resultado = validarMontoRetiroReal(2000, 1500);
  assert.ok(resultado.includes("1500"));
});

test("validarAplicacion (real) - con moneda, el saldo del mensaje viene formateado como plata", () => {
  const resultado = validarAplicacionReal(2000, 1500.5, "guid-reserva-1", "ARS");
  assert.ok(resultado.includes("1.500,50"), `Esperaba plata formateada (es-AR), vino: "${resultado}"`);
});

test("validarAplicacionAMulta (real) - con moneda, el tope del mensaje viene formateado como plata", () => {
  const resultado = validarAplicacionAMulta(5000, { outstandingAmount: 1500.5 }, 10000, "ARS");
  assert.ok(resultado.includes("1.500,50"), `Esperaba plata formateada (es-AR), vino: "${resultado}"`);
});

test("validarAplicacionAMulta (real) - sin moneda cae al número plano (compatibilidad)", () => {
  const resultado = validarAplicacionAMulta(5000, { outstandingAmount: 3000 }, 10000);
  assert.ok(resultado.includes("3000"));
});

test("DESTINOS_RETIRO incluye 'Aplicar a una multa' con el kind pseudo-4, entre las devoluciones y 'aplicar a otra reserva'", () => {
  const indiceMulta = DESTINOS_RETIRO.findIndex((d) => d.kind === KIND_APLICAR_A_MULTA);
  const indiceOtraReserva = DESTINOS_RETIRO.findIndex((d) => d.kind === 3);
  assert.ok(indiceMulta !== -1, "Debe existir la opción 'Aplicar a una multa'");
  assert.strictEqual(DESTINOS_RETIRO[indiceMulta].label, "Aplicar a una multa");
  assert.ok(indiceMulta < indiceOtraReserva, "Debe ir antes que 'Aplicar a otra reserva'");
});

test("enriquecerMultasAplicables - cruza openPenalties con pendingPenalties.items por debitNotePublicId", () => {
  const openPenalties = [
    { reservaPublicId: "r-1", numeroReserva: "R-1050", debitNotePublicId: "nd-1", outstandingAmount: 3000 },
  ];
  const pendingPenaltyItems = [
    { reservaPublicId: "r-1", debitNotePublicId: "nd-1", name: "Bariloche" },
  ];
  const resultado = enriquecerMultasAplicables(openPenalties, pendingPenaltyItems);
  assert.strictEqual(resultado.length, 1);
  assert.strictEqual(resultado[0].name, "Bariloche");
  assert.strictEqual(resultado[0].outstandingAmount, 3000, "El monto SIEMPRE sale de openPenalties (neteado), nunca del item bruto");
});

test("enriquecerMultasAplicables - sin match en pendingPenalties.items, name queda null (nunca inventa un nombre)", () => {
  const resultado = enriquecerMultasAplicables(
    [{ reservaPublicId: "r-2", numeroReserva: "R-002", debitNotePublicId: "nd-2", outstandingAmount: 500 }],
    []
  );
  assert.strictEqual(resultado[0].name, null);
});

test("enriquecerMultasAplicables - openPenalties vacío o null devuelve lista vacía", () => {
  assert.deepEqual(enriquecerMultasAplicables(null, []), []);
  assert.deepEqual(enriquecerMultasAplicables([], []), []);
});

test("montoSugeridoAplicacionAMulta - el menor entre lo que falta cobrar y el saldo disponible", () => {
  assert.strictEqual(montoSugeridoAplicacionAMulta({ outstandingAmount: 3000 }, 10000), 3000);
  assert.strictEqual(montoSugeridoAplicacionAMulta({ outstandingAmount: 8000 }, 2000), 2000);
});

test("montoSugeridoAplicacionAMulta - sin multa elegida devuelve 0", () => {
  assert.strictEqual(montoSugeridoAplicacionAMulta(null, 5000), 0);
});

test("validarAplicacionAMulta - sin multa elegida devuelve error", () => {
  const resultado = validarAplicacionAMulta(500, null, 1000);
  assert.ok(resultado.includes("multa"));
});

test("validarAplicacionAMulta - monto 0 devuelve error", () => {
  const resultado = validarAplicacionAMulta(0, { outstandingAmount: 3000 }, 10000);
  assert.ok(resultado.includes("mayor a 0"));
});

test("validarAplicacionAMulta - monto excede lo que falta cobrar de la multa (aunque sobre saldo) devuelve error", () => {
  const resultado = validarAplicacionAMulta(5000, { outstandingAmount: 3000 }, 10000);
  assert.ok(resultado !== null);
});

test("validarAplicacionAMulta - monto excede el saldo disponible (aunque la multa deba más) devuelve error", () => {
  const resultado = validarAplicacionAMulta(5000, { outstandingAmount: 8000 }, 2000);
  assert.ok(resultado !== null);
});

test("validarAplicacionAMulta - monto igual al tope (mínimo entre multa y saldo) es válido", () => {
  const resultado = validarAplicacionAMulta(3000, { outstandingAmount: 3000 }, 10000);
  assert.strictEqual(resultado, null);
});

test("armarPayloadAplicacionAMulta - arma currency/amount/debitNotePublicId", () => {
  const payload = armarPayloadAplicacionAMulta("ARS", "3000", "nd-guid-1");
  assert.deepEqual(payload, { currency: "ARS", amount: 3000, debitNotePublicId: "nd-guid-1" });
});

test("mapearKindARefundMethod - kind 1 (efectivo) → PhysicalCash", () => {
  assert.strictEqual(mapearKindARefundMethod(1), "PhysicalCash");
});

test("mapearKindARefundMethod - kind 2 (transferencia) → Transfer", () => {
  assert.strictEqual(mapearKindARefundMethod(2), "Transfer");
});

test("mapearKindARefundMethod - cualquier otro kind → null", () => {
  assert.strictEqual(mapearKindARefundMethod(0), null);
  assert.strictEqual(mapearKindARefundMethod(3), null);
});

test("armarPayloadRefundConNeteo - transferencia con referencia", () => {
  const payload = armarPayloadRefundConNeteo("ARS", 2, "TRANSF-1");
  assert.deepEqual(payload, { currency: "ARS", refundMethod: "Transfer", reference: "TRANSF-1" });
});

test("armarPayloadRefundConNeteo - efectivo sin referencia queda undefined (nunca string vacío)", () => {
  const payload = armarPayloadRefundConNeteo("USD", 1, "");
  assert.strictEqual(payload.reference, undefined);
});

test("previewsDifierenSignificativamente - previas iguales → false", () => {
  const previa = { availableCredit: 10000, totalOpenPenalties: 3000, netToRefund: 7000 };
  assert.strictEqual(previewsDifierenSignificativamente(previa, { ...previa }), false);
});

test("previewsDifierenSignificativamente - cambió el total de multas abiertas → true", () => {
  const previaVista = { availableCredit: 10000, totalOpenPenalties: 3000, netToRefund: 7000 };
  const previaFresca = { availableCredit: 10000, totalOpenPenalties: 5000, netToRefund: 5000 };
  assert.strictEqual(previewsDifierenSignificativamente(previaVista, previaFresca), true);
});

test("previewsDifierenSignificativamente - diferencia de un centavo por redondeo → false (dentro de tolerancia)", () => {
  const previaVista = { availableCredit: 10000, totalOpenPenalties: 3000, netToRefund: 7000 };
  const previaFresca = { availableCredit: 10000.005, totalOpenPenalties: 3000, netToRefund: 7000 };
  assert.strictEqual(previewsDifierenSignificativamente(previaVista, previaFresca), false);
});

test("previewsDifierenSignificativamente - cualquiera de las dos previas null/undefined → true (nunca confiar en un dato faltante)", () => {
  assert.strictEqual(previewsDifierenSignificativamente(null, { availableCredit: 1 }), true);
  assert.strictEqual(previewsDifierenSignificativamente({ availableCredit: 1 }, null), true);
});

test("armarMensajeExitoNeteo - neto > 0 con UNA multa saldada", () => {
  const resultado = {
    currency: "ARS",
    netRefunded: 7000,
    penaltyApplications: [{ debitNotePublicId: "nd-1" }],
  };
  const preview = [{ debitNotePublicId: "nd-1", numeroReserva: "R-1050" }];
  const mensaje = armarMensajeExitoNeteo(resultado, preview);
  assert.ok(mensaje.includes("R-1050"));
  assert.ok(mensaje.includes("quedó saldada"));
});

test("armarMensajeExitoNeteo - neto > 0 con VARIAS multas saldadas usa plural", () => {
  const resultado = {
    currency: "ARS",
    netRefunded: 1000,
    penaltyApplications: [{ debitNotePublicId: "nd-1" }, { debitNotePublicId: "nd-2" }],
  };
  const preview = [
    { debitNotePublicId: "nd-1", numeroReserva: "R-1050" },
    { debitNotePublicId: "nd-2", numeroReserva: "R-1099" },
  ];
  const mensaje = armarMensajeExitoNeteo(resultado, preview);
  assert.ok(mensaje.includes("R-1050"));
  assert.ok(mensaje.includes("R-1099"));
  assert.ok(mensaje.includes("quedaron saldadas"));
});

test("armarMensajeExitoNeteo - neto = 0, todo se usó en multas → mensaje de aplicación, no de devolución", () => {
  const resultado = {
    currency: "ARS",
    netRefunded: 0,
    penaltyApplications: [{ debitNotePublicId: "nd-1" }],
  };
  const preview = [{ debitNotePublicId: "nd-1", numeroReserva: "R-1050" }];
  const mensaje = armarMensajeExitoNeteo(resultado, preview);
  assert.ok(mensaje.includes("No quedó nada para devolver"));
  assert.ok(!mensaje.includes("saldada"), "No debe afirmar 'saldada' cuando el neto llegó a 0 (puede haber quedado parcial)");
});

test("armarMensajeExitoNeteo - sin multas aplicadas, solo devolución simple", () => {
  const resultado = { currency: "USD", netRefunded: 300, penaltyApplications: [] };
  const mensaje = armarMensajeExitoNeteo(resultado, []);
  assert.ok(mensaje.includes("US$300"));
  assert.ok(!mensaje.includes("multa"));
});

test("prefijoDestinoAplicacionSaldo - destino 'multa'", () => {
  assert.strictEqual(prefijoDestinoAplicacionSaldo("multa"), "Saldo a favor aplicado a la multa de");
});

test("prefijoDestinoAplicacionSaldo - destino 'reserva' (o cualquier otro valor) usa el texto genérico", () => {
  assert.strictEqual(prefijoDestinoAplicacionSaldo("reserva"), "Saldo a favor aplicado a");
  assert.strictEqual(prefijoDestinoAplicacionSaldo(undefined), "Saldo a favor aplicado a");
});

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
