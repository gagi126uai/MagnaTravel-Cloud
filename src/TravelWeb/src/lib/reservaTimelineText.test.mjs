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

import {
  traducirMetodoEnLineaHistorial,
  resumenAltaDePagoHistorial,
  describirEventoHistorial,
  agruparEventosPorDia,
  horaDeEvento,
} from "./reservaTimelineText.js";
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

/* ═══════════════════════════════════════════════════════════════════════════
 * describirEventoHistorial — Tanda 4 (rediseño de fichas, 2026-08-04)
 * ═══════════════════════════════════════════════════════════════════════════ */

test("describirEventoHistorial: Alta de Pago con actor humano → cobró, punto verde, monto en detalle armado aparte", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    actor: "Maite",
    amount: 50000,
    currency: "ARS",
    paymentMethod: "Cash",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.colorPunto, "verde");
  assert.equal(d.actor, "Maite");
  assert.equal(d.esCobro, true);
  assert.equal(d.montoTexto, formatCurrency(50000, "ARS"));
  assert.equal(d.frase, null);
  assert.equal(d.detalle, "Forma de pago: Efectivo");
});

test("describirEventoHistorial: Pago con monto NEGATIVO (reversa de NC / multa deshecha) → NO es cobro: punto rojo, frase 'Se descontó' y monto en POSITIVO (bloqueante review 2026-08-04)", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    actor: "Sistema",
    amount: -140000,
    currency: "ARS",
    paymentMethod: "Transfer",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.esCobro, false, "un monto que sale jamás se presenta como cobro");
  assert.equal(d.colorPunto, "rojo");
  assert.equal(d.frase, `Se descontó un cobro de ${formatCurrency(140000, "ARS")}.`);
  assert.equal(d.frase.includes("-"), false, "la plata que sale va con su palabra, no con un signo");
  assert.equal(d.detalle, "Forma de pago: Transferencia");
});

test("describirEventoHistorial: Alta de Pago con actor 'Sistema' → actor null (frase impersonal la arma el componente)", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "Payment",
    actor: "Sistema",
    amount: 1000,
    currency: "ARS",
    paymentMethod: "Cash",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.actor, null);
  assert.equal(d.esCobro, true);
});

test("describirEventoHistorial: cambio de Estado de la Reserva → frase natural con los dos labels traducidos", () => {
  const event = {
    eventType: "Update",
    relatedEntityType: "Reserva",
    actor: "Maite",
    details: "• Estado: de *InManagement* a **Confirmed**",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "La reserva pasó de En gestión a Confirmada.");
  assert.equal(d.actor, null, "el actor no va al principio de esta frase especial");
  assert.equal(d.detalle, "La hizo Maite.");
});

test("describirEventoHistorial: cambio de Estado sin actor humano → sin línea 'La hizo'", () => {
  const event = {
    eventType: "Update",
    relatedEntityType: "Reserva",
    actor: "Sistema",
    details: "• Estado: de *Quotation* a **Budget**",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "La reserva pasó de Cotización a Presupuesto.");
  assert.equal(d.detalle, null);
});

test("describirEventoHistorial: anulación (SoftDelete) de un traslado, con actor → frase + punto rojo", () => {
  const event = {
    eventType: "SoftDelete",
    relatedEntityType: "TransferBooking",
    actor: "Maite",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.colorPunto, "rojo");
  assert.equal(d.actor, "Maite");
  assert.equal(d.frase, "anuló el traslado.");
});

test("describirEventoHistorial: anulación sin actor humano → frase impersonal 'Se anuló...'", () => {
  const event = { eventType: "SoftDelete", relatedEntityType: "TransferBooking", actor: null };
  const d = describirEventoHistorial(event);
  assert.equal(d.actor, null);
  assert.equal(d.frase, "Se anuló el traslado.");
});

test("describirEventoHistorial: alta genérica de la reserva → 'creó la reserva.'", () => {
  const event = { eventType: "Create", relatedEntityType: "Reserva", actor: "Maite" };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "creó la reserva.");
  assert.equal(d.colorPunto, "neutro");
});

test("describirEventoHistorial: Update sobre Invoice → punto índigo", () => {
  const event = { eventType: "Update", relatedEntityType: "Invoice", actor: "Maite", details: "• Notas: de *N/A* a **algo**" };
  const d = describirEventoHistorial(event);
  assert.equal(d.colorPunto, "indigo");
  assert.equal(d.frase, "modificó la factura.");
});

test("describirEventoHistorial: diff con N° de confirmación (Alta) → detalle 'N° de confirmación: X'", () => {
  const event = {
    eventType: "Create",
    relatedEntityType: "HotelBooking",
    actor: "Maite",
    details: "• **Confirmación**: CONF-123",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.detalle, "N° de confirmación: CONF-123");
});

test("describirEventoHistorial: diff con N° de confirmación (Modificación) → detalle con el valor NUEVO", () => {
  const event = {
    eventType: "Update",
    relatedEntityType: "HotelBooking",
    actor: "Maite",
    details: "• Confirmación: de *N/A* a **CONF-123**",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.detalle, "N° de confirmación: CONF-123");
});

/* ═══════════════════════════════════════════════════════════════════════════
 * describirEventoHistorial: eventType "StatusChange" — Tanda 3 (2026-08-18)
 * Contrato nuevo del backend: fromStatus/toStatus crudos + details con motivo
 * y/o "Autorizó: {nombre}" (ver TimelineService.BuildStatusChangeDetails).
 * ═══════════════════════════════════════════════════════════════════════════ */

test("describirEventoHistorial: StatusChange sin motivo ni autorizante, con actor humano → frase traducida + 'La hizo X.'", () => {
  const event = {
    eventType: "StatusChange",
    actor: "Maite",
    fromStatus: "InManagement",
    toStatus: "Confirmed",
    details: null,
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "La reserva pasó de En gestión a Confirmada.");
  assert.equal(d.actor, null, "el actor no va al principio de esta frase especial");
  assert.equal(d.detalle, "La hizo Maite.");
});

test("describirEventoHistorial: StatusChange con motivo → detalle 'Motivo: …' además de quién lo hizo", () => {
  const event = {
    eventType: "StatusChange",
    actor: "Maite",
    fromStatus: "Confirmed",
    toStatus: "Cancelled",
    details: "El cliente pidió cancelar el viaje.",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "La reserva pasó de Confirmada a Anulada.");
  assert.equal(d.detalle, "La hizo Maite. · Motivo: El cliente pidió cancelar el viaje.");
});

test("describirEventoHistorial: StatusChange con motivo y autorizante (reversión) → los tres datos encadenados", () => {
  const event = {
    eventType: "StatusChange",
    actor: "Maite",
    fromStatus: "Cancelled",
    toStatus: "Confirmed",
    details: "Se canceló por error de carga.\nAutorizó: Gastón",
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "La reserva pasó de Anulada a Confirmada.");
  assert.equal(
    d.detalle,
    "La hizo Maite. · Motivo: Se canceló por error de carga. · Autorizó: Gastón"
  );
});

test("describirEventoHistorial: StatusChange sin actor humano (job automático) ni motivo → detalle null", () => {
  const event = {
    eventType: "StatusChange",
    actor: "Sistema",
    fromStatus: "Confirmed",
    toStatus: "Traveling",
    details: null,
  };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "La reserva pasó de Confirmada a En viaje.");
  assert.equal(d.detalle, null);
});

test("describirEventoHistorial: StatusChange con status crudo no mapeado → NUNCA el código técnico en la frase", () => {
  const event = {
    eventType: "StatusChange",
    actor: "Sistema",
    fromStatus: "Confirmed",
    toStatus: "AlgoNuevoDelBackend",
    details: null,
  };
  const d = describirEventoHistorial(event);
  assert.ok(!d.frase.includes("AlgoNuevoDelBackend"), "nunca el código crudo en la frase");
  assert.equal(d.frase, "La reserva pasó de Confirmada a —.");
});

test("describirEventoHistorial: entidad desconocida (DTO nuevo que el front no mapeó) → 'un registro de la reserva', nunca el nombre técnico", () => {
  const event = { eventType: "Update", relatedEntityType: "AlgoNuevoDelBackend", actor: "Maite" };
  const d = describirEventoHistorial(event);
  assert.equal(d.frase, "modificó un registro de la reserva.");
  assert.ok(!d.frase.includes("AlgoNuevoDelBackend"), "nunca el nombre técnico crudo en la frase");
});

test("describirEventoHistorial: eventType desconocido → cae al verbo genérico 'modificó', no revienta", () => {
  const event = { eventType: "Restore", relatedEntityType: "Payment", actor: "Maite" };
  const d = describirEventoHistorial(event);
  // No es un Create de Payment (es "Restore"), así que no entra por la rama de cobro.
  assert.equal(d.esCobro, false);
  assert.equal(d.frase, "modificó el pago.");
});

/* ═══════════════════════════════════════════════════════════════════════════
 * agruparEventosPorDia / horaDeEvento — Tanda 4
 * ═══════════════════════════════════════════════════════════════════════════ */

// "Ahora" fijo para que los tests de Hoy/Ayer no dependan del reloj real:
// 2026-08-04T15:00:00Z = 04/08/2026 12:00 en Argentina (UTC-3).
const AHORA_FIJO = "2026-08-04T15:00:00Z";

test("agruparEventosPorDia: eventos del mismo día de hoy → un solo grupo 'Hoy — dd/mm/aaaa'", () => {
  const eventos = [
    { timestamp: "2026-08-04T17:32:00Z" }, // 14:32 ART, mismo día que AHORA_FIJO
    { timestamp: "2026-08-04T13:05:00Z" }, // 10:05 ART, mismo día
  ];
  const grupos = agruparEventosPorDia(eventos, new Date(AHORA_FIJO));
  assert.equal(grupos.length, 1);
  assert.equal(grupos[0].etiqueta, "Hoy — 04/08/2026");
  assert.equal(grupos[0].eventos.length, 2);
});

test("agruparEventosPorDia: un evento de ayer → grupo 'Ayer — dd/mm/aaaa'", () => {
  const eventos = [{ timestamp: "2026-08-03T14:05:00Z" }]; // 11:05 ART del 03/08
  const grupos = agruparEventosPorDia(eventos, new Date(AHORA_FIJO));
  assert.equal(grupos.length, 1);
  assert.equal(grupos[0].etiqueta, "Ayer — 03/08/2026");
});

test("agruparEventosPorDia: un evento de hace varios días → etiqueta con el nombre del día", () => {
  const eventos = [{ timestamp: "2026-07-25T05:07:00Z" }]; // 02:07 ART del 25/07 (sábado real de 2026)
  const grupos = agruparEventosPorDia(eventos, new Date(AHORA_FIJO));
  assert.equal(grupos.length, 1);
  assert.equal(grupos[0].etiqueta, "Sábado 25/07/2026");
});

test("agruparEventosPorDia: eventos de tres días distintos (ya ordenados del más nuevo al más viejo) → tres grupos en ese orden", () => {
  const eventos = [
    { timestamp: "2026-08-04T17:32:00Z" }, // Hoy
    { timestamp: "2026-08-03T14:05:00Z" }, // Ayer
    { timestamp: "2026-07-25T05:07:00Z" }, // Sábado 25/07/2026
  ];
  const grupos = agruparEventosPorDia(eventos, new Date(AHORA_FIJO));
  assert.deepEqual(
    grupos.map((g) => g.etiqueta),
    ["Hoy — 04/08/2026", "Ayer — 03/08/2026", "Sábado 25/07/2026"]
  );
  grupos.forEach((g) => assert.equal(g.eventos.length, 1));
});

test("agruparEventosPorDia: no reordena — dos eventos del mismo día quedan en el orden en que llegaron", () => {
  const primero = { timestamp: "2026-08-04T17:32:00Z", title: "más nuevo" };
  const segundo = { timestamp: "2026-08-04T13:05:00Z", title: "más viejo" };
  const grupos = agruparEventosPorDia([primero, segundo], new Date(AHORA_FIJO));
  assert.equal(grupos[0].eventos[0].title, "más nuevo");
  assert.equal(grupos[0].eventos[1].title, "más viejo");
});

test("agruparEventosPorDia: lista vacía → devuelve array vacío", () => {
  assert.deepEqual(agruparEventosPorDia([], new Date(AHORA_FIJO)), []);
});

test("agruparEventosPorDia: sin eventos (undefined) → devuelve array vacío, no revienta", () => {
  assert.deepEqual(agruparEventosPorDia(undefined, new Date(AHORA_FIJO)), []);
});

test("horaDeEvento: convierte el timestamp UTC a HH:mm de Argentina", () => {
  assert.equal(horaDeEvento("2026-08-04T17:32:00Z"), "14:32");
});

test("horaDeEvento: rellena con cero a la izquierda (hora y minuto de un solo dígito)", () => {
  // 2026-08-04T04:05:00Z → 01:05 ART
  assert.equal(horaDeEvento("2026-08-04T04:05:00Z"), "01:05");
});
