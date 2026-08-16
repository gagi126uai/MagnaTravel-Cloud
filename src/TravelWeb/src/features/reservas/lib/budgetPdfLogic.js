/**
 * Reglas puras de "Por persona / Total del viaje", la elección de formato que ahora se
 * pregunta EN EL MOMENTO de tocar "Emitir PDF" / "Enviar por WhatsApp" (decisión del dueño,
 * 2026-08-16 — reemplaza al interruptor que antes vivía suelto en la cabecera, spec vieja
 * docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md §3).
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

/** Valor del query param `pricing` que espera GET /reservas/{id}/budget-pdf. */
export function queryParamPricingParaModo(modo) {
  return modo === MODO_PRECIO_PRESUPUESTO.Total ? "total" : "porPersona";
}

/** Valor booleano que espera el body de POST /messages/budget (campo `porPersona`). */
export function porPersonaBooleanParaModo(modo) {
  return modo !== MODO_PRECIO_PRESUPUESTO.Total;
}
