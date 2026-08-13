/**
 * Reglas puras del interruptor "Por persona / Total del viaje" que acompaña a los botones
 * "Emitir PDF" / "Enviar por WhatsApp" de la cabecera de la reserva (spec
 * docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md, §3 — Gastón eligió la OPCIÓN A:
 * un chip interruptor pegado a la izquierda de "Emitir PDF", con el molde de chip B.5).
 *
 * Ojo con el contrato REAL del backend (verificado en ReservasController.cs/MessageDtos.cs,
 * no es el que suponía el brief original de esta tanda):
 *   - GET  /reservas/{id}/budget-pdf?pricing=porPersona|total  → el query param es un STRING
 *     ("pricing"), no un booleano. Cualquier valor que no sea "total" cae a "porPersona".
 *   - POST /messages/budget { reservaId, porPersona: bool }     → acá SÍ es un booleano.
 * Dos formas DISTINTAS de mandar la misma idea a dos endpoints distintos — por eso este
 * módulo separa "qué modo eligió el vendedor" (un string con dos valores posibles) de "cómo
 * se lo mando a cada endpoint", así ningún componente tiene que acordarse de las dos formas
 * ni arriesgarse a mandarle el booleano al que espera texto (o viceversa).
 *
 * Archivo `.js` PURO (sin JSX) a propósito — se puede testear con `node --test` sin montar
 * React, mismo criterio que el resto de los helpers de esta carpeta.
 */

export const MODO_PRECIO_PRESUPUESTO = {
  PorPersona: "porPersona",
  Total: "total",
};

const ETIQUETA_CHIP_POR_MODO = {
  [MODO_PRECIO_PRESUPUESTO.PorPersona]: "Por persona ⇄",
  [MODO_PRECIO_PRESUPUESTO.Total]: "Total del viaje ⇄",
};

/**
 * Texto que muestra el chip para el modo actual. Un modo desconocido (nunca debería pasar,
 * pero por las dudas) cae en "Por persona" — el default firmado, nunca deja el chip mudo.
 */
export function etiquetaChipPrecioPresupuesto(modo) {
  return ETIQUETA_CHIP_POR_MODO[modo] || ETIQUETA_CHIP_POR_MODO[MODO_PRECIO_PRESUPUESTO.PorPersona];
}

/**
 * Invierte el modo: un click sobre el chip pasa de "Por persona" a "Total del viaje" y
 * viceversa. Cualquier valor que no sea exactamente "total" se trata como "Por persona"
 * (mismo criterio defensivo que usa el backend con el query param `pricing`).
 */
export function alternarModoPrecioPresupuesto(modo) {
  return modo === MODO_PRECIO_PRESUPUESTO.Total
    ? MODO_PRECIO_PRESUPUESTO.PorPersona
    : MODO_PRECIO_PRESUPUESTO.Total;
}

/** Valor del query param `pricing` que espera GET /reservas/{id}/budget-pdf. */
export function queryParamPricingParaModo(modo) {
  return modo === MODO_PRECIO_PRESUPUESTO.Total ? "total" : "porPersona";
}

/** Valor booleano que espera el body de POST /messages/budget (campo `porPersona`). */
export function porPersonaBooleanParaModo(modo) {
  return modo !== MODO_PRECIO_PRESUPUESTO.Total;
}
