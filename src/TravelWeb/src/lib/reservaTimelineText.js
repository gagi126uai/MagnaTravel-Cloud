/**
 * Traducciones puntuales para el Historial de la reserva (tab "Historial" de la ficha,
 * componente ReservaTimeline.jsx) — hallazgo #5 del barrido de PROD 2026-07-24.
 *
 * El backend (TimelineService.cs) arma cada línea del historial en español ("Alta de
 * un Pago", "• **Importe**: $150,00"), pero el VALOR del campo Método de pago es el
 * token crudo que guardó el formulario de cobro en su momento — puede venir en inglés
 * legado ("Transfer") si el pago es viejo, o directamente ser un valor que el frontend
 * no reconoce (ej. "Other"). Ese es el único lugar donde puede colarse texto crudo/en
 * inglés en el historial: el resto de los textos ya vienen en criollo.
 */

import { traducirMetodoPago } from "../features/customers/lib/paymentHelpers.js";
import { formatCurrency } from "./utils.js";

// Formato que arma TimelineService.cs para Modificación de un campo (Update/SoftDelete):
//   "• Método: de *Transfer* a **Transferencia**"
// Este SÍ se llega a ver en pantalla hoy: AppDbContext.OnBeforeSaveChanges guarda los
// cambios de Update/SoftDelete en formato {Old, New}, que es justo lo que
// TimelineService.GetTimelineAsync espera para deserializar.
const REGEX_METODO_MODIFICACION = /^(• Método: de \*)(.+?)(\* a \*\*)(.+?)(\*\*)$/;

// Formato que TimelineService.cs arma (en el código) para Alta/Eliminación de un campo:
//   "• **Método**: Transfer"
// OJO — rama hoy INALCANZABLE desde la pantalla real (bug preexistente, no se toca en
// esta tanda, ver TimelineService.cs líneas ~148-159): AppDbContext.OnBeforeSaveChanges
// guarda los cambios de "Create"/"Delete" en formato PLANO {"Campo": valor} (sin envolver
// en {Old, New}), pero TimelineService SIEMPRE intenta deserializar esperando {Old, New}
// — eso hace que CUALQUIER Alta o Eliminación (de cualquier entidad, no solo Pago) tire
// una excepción de parseo y caiga en el catch genérico ("Modificaciones en campos
// técnicos."), así que la línea "• **Método**: X" nunca llega a construirse hoy. Se
// mantiene esta rama (con test directo más abajo, ver reservaTimelineText.test.mjs)
// porque el día que se arregle ese bug compartido — reportado aparte, es más grande que
// este módulo — el formato va a volver a aparecer y esta traducción tiene que seguir
// funcionando sin que nadie tenga que acordarse de tocar este archivo.
const REGEX_METODO_ALTA_O_BAJA = /^(• \*\*Método\*\*: )(.+)$/;

// Texto que se muestra cuando el método de pago no se puede traducir (viene vacío,
// es "Other"/"Otro", o es un token nuevo que el frontend todavía no mapeó). NUNCA se
// muestra el token crudo del backend — eso sería jerga técnica/inglés en una pantalla
// en español (regla del gate de exposición de datos).
const METODO_DESCONOCIDO = "Otro medio";

/**
 * Traduce un método de pago crudo del backend, garantizando que el resultado NUNCA sea
 * el token técnico original: si `traducirMetodoPago` no lo reconoce (devuelve ""), se
 * usa el texto genérico "Otro medio" en vez del string crudo.
 *
 * @param {string} metodoCrudo
 * @returns {string}
 */
function traducirMetodoSinCrudo(metodoCrudo) {
  return traducirMetodoPago(metodoCrudo) || METODO_DESCONOCIDO;
}

/**
 * Traduce el valor del método de pago DENTRO de una línea de detalle del historial,
 * si esa línea es sobre el campo Método. Cualquier otra línea (Importe, Estado, etc.)
 * se devuelve tal cual, sin tocar nada.
 *
 * @param {string} linea - una línea de `event.details` (ya separado por "\n" en el componente)
 * @returns {string}
 */
export function traducirMetodoEnLineaHistorial(linea) {
  if (!linea) return linea;

  const matchAltaOBaja = linea.match(REGEX_METODO_ALTA_O_BAJA);
  if (matchAltaOBaja) {
    const [, prefijo, valorCrudo] = matchAltaOBaja;
    return `${prefijo}${traducirMetodoSinCrudo(valorCrudo)}`;
  }

  const matchModificacion = linea.match(REGEX_METODO_MODIFICACION);
  if (matchModificacion) {
    const [, inicio, valorViejo, medio, valorNuevo, fin] = matchModificacion;
    return `${inicio}${traducirMetodoSinCrudo(valorViejo)}${medio}${traducirMetodoSinCrudo(valorNuevo)}${fin}`;
  }

  return linea;
}

/**
 * Lee el monto y el método de un evento de Alta de Pago, priorizando SIEMPRE los campos
 * estructurados del DTO (`event.amount` / `event.currency` / `event.paymentMethod`) por
 * sobre el parseo de `event.details`.
 *
 * Por qué NO se puede confiar en `event.details` para esto (bloqueante de reviewer,
 * 2026-07-24): por el mismo bug documentado arriba de REGEX_METODO_ALTA_O_BAJA, un Alta
 * de Pago NUNCA llega a tener la línea "• **Importe**: $X" en `details` — cae siempre en
 * el texto genérico "Modificaciones en campos técnicos.". Por eso el backend agregó los
 * tres campos sueltos al DTO (`TimelineEventDto.Amount/Currency/PaymentMethod`), leídos
 * directo de la tabla `Payment` (no del diff de auditoría) — son la fuente confiable.
 *
 * El parseo de `event.details` queda solo como último recurso legacy, por si algún día
 * llega un evento sin estos campos estructurados (ej. una versión vieja cacheada).
 *
 * @param {object} event
 * @returns {{montoTexto: string|null, metodoTexto: string|null}}
 */
function leerMontoYMetodoDePago(event) {
  // Camino principal: campos estructurados del DTO — SIEMPRE se priorizan si vino
  // alguno de los dos (aunque el otro falte, es más confiable que el texto libre).
  if (event.amount != null || event.paymentMethod != null) {
    return {
      montoTexto: event.amount != null ? formatCurrency(event.amount, event.currency || "ARS") : null,
      metodoTexto: event.paymentMethod != null ? traducirMetodoSinCrudo(event.paymentMethod) : null,
    };
  }

  // Último recurso legacy: parsear las líneas de `details` (formato viejo, ver arriba).
  if (!event.details) return { montoTexto: null, metodoTexto: null };

  const lineas = event.details.split("\n");
  const lineaImporte = lineas.find((linea) => linea.includes("**Importe**"));
  const importeCrudo = lineaImporte ? lineaImporte.replace(/^• \*\*Importe\*\*: /, "").trim() : null;

  const lineaMetodo = lineas.find((linea) => linea.includes("**Método**"));
  const metodoCrudo = lineaMetodo ? lineaMetodo.replace(/^• \*\*Método\*\*: /, "").trim() : null;

  return {
    montoTexto: importeCrudo,
    metodoTexto: metodoCrudo ? traducirMetodoSinCrudo(metodoCrudo) : null,
  };
}

/**
 * Arma un resumen corto en criollo para un evento de Alta de Pago del historial
 * ("Cobro registrado: $150.000,00 — Transferencia"), en vez de que el vendedor tenga
 * que leer la lista de bullets técnica campo por campo (que además, para un Alta, hoy
 * ni siquiera se arma bien — ver `leerMontoYMetodoDePago`).
 *
 * Nota de robustez (si el dato no llega): si el backend no manda ni importe ni método
 * para este evento puntual, la función arma la frase con lo que SÍ tiene, o devuelve
 * null si no hay nada — nunca rompe ni inventa un monto.
 *
 * @param {{eventType?: string, relatedEntityType?: string, amount?: number|null,
 *   currency?: string|null, paymentMethod?: string|null, details?: string|null}} event
 * @returns {string|null} la frase resumen, o null si el evento no es un Alta de Pago
 *   o no hay ningún dato de importe/método para armar nada.
 */
export function resumenAltaDePagoHistorial(event) {
  if (!event || event.eventType !== "Create" || event.relatedEntityType !== "Payment") return null;

  const { montoTexto, metodoTexto } = leerMontoYMetodoDePago(event);

  if (!montoTexto && !metodoTexto) return null;
  if (montoTexto && metodoTexto) return `Cobro registrado: ${montoTexto} — ${metodoTexto}`;
  if (montoTexto) return `Cobro registrado: ${montoTexto}`;
  return `Cobro registrado — ${metodoTexto}`;
}
