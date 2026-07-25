/**
 * Tests de las traducciones del Historial de la reserva (hallazgo #5 del barrido de
 * PROD 2026-07-24): el método de pago no debe aparecer crudo/en inglés en el historial,
 * y el resumen de un Alta de Pago tiene que armarse con los campos ESTRUCTURADOS del
 * DTO (amount/currency/paymentMethod), no parseando `event.details` (ver el bloqueante
 * de reviewer documentado en reservaTimelineText.js: el diff de auditoría de un Alta
 * NUNCA llega a tener las líneas "• **Importe**"/"• **Método**" en la práctica — los
 * fixtures viejos de este archivo probaban un formato que el motor jamás emite).
 *
 * Corren con Node puro sin bundler: node --test src/lib/reservaTimelineText.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { traducirMetodoEnLineaHistorial, resumenAltaDePagoHistorial } from "./reservaTimelineText.js";
import { formatCurrency } from "./utils.js";

// Intl.NumberFormat("es-AR", { style: "currency" }) pone un espacio NO separable
// (U+00A0) entre el símbolo "$" y el número — por eso los importes en ARS se arman acá
// con formatCurrency (la misma función que usa el código real) en vez de tipear el
// string a mano, para no depender de acertarle a ese carácter invisible.

// ─── traducirMetodoEnLineaHistorial: Modificación (de X a Y) — rama que SÍ se ve hoy ──

test("línea de Modificación con ambos valores crudos en inglés → traduce los dos", () => {
  const resultado = traducirMetodoEnLineaHistorial("• Método: de *Cash* a **Transfer**");
  assert.equal(resultado, "• Método: de *Efectivo* a **Transferencia**");
});

test("línea de Modificación con método 'Other' → 'Otro medio', NUNCA el token crudo", () => {
  const resultado = traducirMetodoEnLineaHistorial("• Método: de *Transfer* a **Other**");
  assert.equal(resultado, "• Método: de *Transferencia* a **Otro medio**");
  assert.equal(resultado.includes("Other"), false);
});

test("línea de Modificación con token totalmente desconocido → 'Otro medio', no revienta", () => {
  const resultado = traducirMetodoEnLineaHistorial("• Método: de *CryptoWallet* a **Transfer**");
  assert.equal(resultado, "• Método: de *Otro medio* a **Transferencia**");
  assert.equal(resultado.includes("CryptoWallet"), false);
});

// ─── traducirMetodoEnLineaHistorial: Alta/Baja — rama hoy inalcanzable en la práctica,
// pero se testea directo para que siga funcionando el día que se arregle el bug del
// diff de auditoría (ver comentario largo en reservaTimelineText.js) ─────────────────

test("línea de Alta con método crudo en inglés → traduce el valor (rama legacy, cubierta igual)", () => {
  const resultado = traducirMetodoEnLineaHistorial("• **Método**: Transfer");
  assert.equal(resultado, "• **Método**: Transferencia");
});

test("línea de Alta con método 'Other' → 'Otro medio', NUNCA el token crudo", () => {
  const resultado = traducirMetodoEnLineaHistorial("• **Método**: Other");
  assert.equal(resultado, "• **Método**: Otro medio");
  assert.equal(resultado.includes("Other"), false);
});

// ─── traducirMetodoEnLineaHistorial: líneas que NO son de Método ───────────────

test("línea de Importe → se devuelve intacta, no la toca", () => {
  const linea = "• **Importe**: $150.000,00";
  assert.equal(traducirMetodoEnLineaHistorial(linea), linea);
});

test("línea de Estado → se devuelve intacta, no la toca", () => {
  const linea = "• Estado: de *Solicitado* a **Confirmado**";
  assert.equal(traducirMetodoEnLineaHistorial(linea), linea);
});

test("línea vacía o null → no revienta", () => {
  assert.equal(traducirMetodoEnLineaHistorial(""), "");
  assert.equal(traducirMetodoEnLineaHistorial(null), null);
  assert.equal(traducirMetodoEnLineaHistorial(undefined), undefined);
});

// ─── resumenAltaDePagoHistorial: camino REAL (campos estructurados del DTO) ────────
//
// Esta es la forma que el motor manda de verdad para un Alta de Pago — `details` viene
// con el texto genérico "Modificaciones en campos técnicos." (o directamente null), y
// el dato real está en amount/currency/paymentMethod al nivel del evento (camelCase,
// como llega después de camelize() en ReservaTimeline.jsx).

test("Alta de Pago con campos estructurados (amount+currency+paymentMethod) → arma la frase real", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    amount: 150000,
    currency: "ARS",
    paymentMethod: "Transfer",
    details: "Modificaciones en campos técnicos.",
  };
  const resultado = resumenAltaDePagoHistorial(event);
  assert.equal(resultado, `Cobro registrado: ${formatCurrency(150000, "ARS")} — Transferencia`);
});

test("Alta de Pago en USD → usa el símbolo US$ correcto", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    amount: 300,
    currency: "USD",
    paymentMethod: "Cash",
    details: null,
  };
  const resultado = resumenAltaDePagoHistorial(event);
  assert.equal(resultado, "Cobro registrado: US$300,00 — Efectivo");
});

test("Alta de Pago con paymentMethod 'Other' → 'Otro medio', la salida NUNCA contiene el token crudo", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    amount: 50000,
    currency: "ARS",
    paymentMethod: "Other",
    details: "Modificaciones en campos técnicos.",
  };
  const resultado = resumenAltaDePagoHistorial(event);
  assert.equal(resultado, `Cobro registrado: ${formatCurrency(50000, "ARS")} — Otro medio`);
  assert.equal(resultado.includes("Other"), false);
});

test("Alta de Pago con paymentMethod totalmente desconocido → 'Otro medio', no se filtra el token", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    amount: 10000,
    currency: "ARS",
    paymentMethod: "CryptoWallet",
    details: null,
  };
  const resultado = resumenAltaDePagoHistorial(event);
  assert.equal(resultado, `Cobro registrado: ${formatCurrency(10000, "ARS")} — Otro medio`);
  assert.equal(resultado.includes("CryptoWallet"), false);
});

test("Alta de Pago SIN amount (paymentMethod solo) → fallback sin monto, no rompe", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    amount: null,
    currency: null,
    paymentMethod: "Transfer",
    details: "Modificaciones en campos técnicos.",
  };
  const resultado = resumenAltaDePagoHistorial(event);
  assert.equal(resultado, "Cobro registrado — Transferencia");
});

test("Alta de Pago SIN paymentMethod (amount solo) → fallback sin método", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    amount: 75000,
    currency: "ARS",
    paymentMethod: null,
    details: null,
  };
  const resultado = resumenAltaDePagoHistorial(event);
  assert.equal(resultado, `Cobro registrado: ${formatCurrency(75000, "ARS")}`);
});

// ─── resumenAltaDePagoHistorial: último recurso legacy (sin campos estructurados) ──

test("Alta de Pago SIN campos estructurados (evento viejo) → cae al parseo legacy de details", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    amount: undefined,
    currency: undefined,
    paymentMethod: undefined,
    details: "• **Importe**: $150.000,00\n• **Método**: Transfer",
  };
  const resultado = resumenAltaDePagoHistorial(event);
  assert.equal(resultado, "Cobro registrado: $150.000,00 — Transferencia");
});

test("Alta de Pago sin estructurados NI details → devuelve null, no revienta", () => {
  const event = { eventType: "Create", relatedEntityType: "Payment", details: null };
  assert.equal(resumenAltaDePagoHistorial(event), null);
});

// ─── resumenAltaDePagoHistorial: casos que NO corresponden ─────────────────────

test("evento que no es Payment → devuelve null (no arma resumen)", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "ServicioReserva",
    amount: 1000,
    paymentMethod: "Transfer",
  };
  assert.equal(resumenAltaDePagoHistorial(event), null);
});

test("evento de Modificación de un Pago (no Alta) → devuelve null aunque traiga los campos estructurados", () => {
  const event = {
    eventType: "Update",
    relatedEntityType: "Payment",
    amount: 150000,
    currency: "ARS",
    paymentMethod: "Transfer",
  };
  assert.equal(resumenAltaDePagoHistorial(event), null);
});

test("event null/undefined → devuelve null, no revienta", () => {
  assert.equal(resumenAltaDePagoHistorial(null), null);
  assert.equal(resumenAltaDePagoHistorial(undefined), null);
});
