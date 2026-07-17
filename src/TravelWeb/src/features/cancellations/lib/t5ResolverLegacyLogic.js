/**
 * Lógica PURA del sub-estado "trabado" de T5 (spec `docs/ux/2026-07-17-t5-resolver-devoluciones-viejas.md`):
 * resolver devoluciones VIEJAS de servicios que se cancelaron ANTES de que el sistema guardara a qué factura
 * correspondía cada uno. Antes había un único formulario "a ciegas" (`t5-resolver-legacy`); ahora el backend manda
 * una LISTA de renglones pendientes (`PartialCreditNoteEmissionSummaryDto.Lines`) y esta pantalla resuelve uno por
 * uno, cada uno contra su propia factura y su propia moneda.
 *
 * Vive separada del componente (mismo criterio que `partialCreditNoteEmissionLogic.js` / `facturaDestinoLogic.js`)
 * para poder testear los cálculos y los textos sin montar nada de React.
 */

import { formatCurrency } from "../../../lib/utils.js";
import { getApiErrorMessage, SPANISH_NETWORK_GENERIC } from "../../../lib/errors.js";

// Estado visual de UNA FILA de la lista — no confundir con T5_STATE (el estado del panel entero,
// definido en partialCreditNoteEmissionLogic.js). Cada fila avanza sola por estos pasos, sin
// esperar a que las demás terminen: por eso el mismo servicio puede tener una fila "emitida" al
// lado de otra todavía "sin resolver".
export const T5_ROW_STATE = Object.freeze({
  UNRESOLVED: "unresolved", // todavía no se eligió factura, monto ni motivo
  RESOLVED: "resolved", // ya tiene factura y monto confirmados, lista para pedir la emisión
  EMITTING: "emitting", // se pidió emitir y la nota de crédito de ESTA fila está "Pending" en ARCA
  ISSUED: "issued", // la nota de crédito de esta fila salió con CAE
  REJECTED: "rejected", // ARCA rechazó la nota de crédito de esta fila
});

/**
 * Deriva el estado visual de una fila a partir del PartialCreditNoteEmissionLineDto que manda el
 * backend. El campo que manda es `creditNoteStatus`: mientras sea null, todavía no se pidió
 * emitir (usamos `isResolved` para distinguir "sin factura elegida" de "ya lista para emitir").
 *
 * @param {{isResolved?: boolean, creditNoteStatus?: string|null}} line
 * @returns {string} uno de T5_ROW_STATE
 */
export function resolveRowState(line) {
  if (line?.creditNoteStatus === "Succeeded") return T5_ROW_STATE.ISSUED;
  if (line?.creditNoteStatus === "Failed") return T5_ROW_STATE.REJECTED;
  if (line?.creditNoteStatus === "Pending") return T5_ROW_STATE.EMITTING;
  return line?.isResolved ? T5_ROW_STATE.RESOLVED : T5_ROW_STATE.UNRESOLVED;
}

/**
 * true si ALGUNA fila de la lista está "emitiendo" (Pending en ARCA). El panel usa esto para
 * decidir si tiene que hacer polling silencioso cada pocos segundos (mismo patrón que el estado
 * PROCESSING del panel entero, pero acá puede haber una fila emitiendo mientras otra ya quedó
 * emitida y una tercera todavía ni se resolvió).
 *
 * @param {Array<object>} lines
 * @returns {boolean}
 */
export function anyLineIsEmitting(lines) {
  return (Array.isArray(lines) ? lines : []).some((line) => resolveRowState(line) === T5_ROW_STATE.EMITTING);
}

/**
 * true cuando TODAS las filas de la lista ya tienen su nota de crédito emitida con éxito. En ese
 * momento no queda nada pendiente de este sub-estado (el panel entero puede desaparecer, igual que
 * el caso normal cuando llega a "emitida" — spec §3, último párrafo).
 *
 * @param {Array<object>} lines
 * @returns {boolean}
 */
export function allLinesIssued(lines) {
  const list = Array.isArray(lines) ? lines : [];
  return list.length > 0 && list.every((line) => resolveRowState(line) === T5_ROW_STATE.ISSUED);
}

/**
 * Título + contador de la lista (spec §1, punto 1). Singular cuando hay una sola devolución
 * pendiente: "Falta resolver 1 devolución de un servicio cancelado" (en vez de "Faltan resolver 1
 * devoluciones..."). El contador de la derecha ("X de N listas") cuenta como "lista" cualquier fila
 * que ya tiene factura y monto confirmados, esté o no ya emitida — así el back-office ve el avance
 * apenas guarda una fila, sin esperar a que termine de emitirse.
 *
 * @param {Array<object>} lines
 * @returns {{title: string, progress: string}}
 */
export function buildResolverLegacyHeaderText(lines) {
  const list = Array.isArray(lines) ? lines : [];
  const total = list.length;
  const resolvedCount = list.filter((line) => resolveRowState(line) !== T5_ROW_STATE.UNRESOLVED).length;

  const title = total === 1
    ? "Falta resolver 1 devolución de un servicio cancelado"
    : `Faltan resolver ${total} devoluciones de servicios cancelados`;

  return { title, progress: `${resolvedCount} de ${total} listas` };
}

// Regla dura de multimoneda (2026-06-09): en este producto SOLO existen pesos y dólares. El nombre
// en criollo de cada uno se usa en los tres textos de abajo (placeholder, ayuda del desplegable y
// cartel de "no hay factura de esa moneda").
function nombreDeLaMoneda(currency) {
  return currency === "USD" ? "dólares" : "pesos";
}

/** Placeholder del desplegable de facturas, spec §2 punto 2: "Elegí la factura en dólares…" / "...en pesos…". */
export function buildInvoicePlaceholder(currency) {
  return `Elegí la factura en ${nombreDeLaMoneda(currency)}…`;
}

/** Línea de ayuda EXACTA debajo del desplegable de facturas (spec §2 punto 2). */
export function buildInvoiceHelpText(currency) {
  return `Solo aparecen facturas en ${nombreDeLaMoneda(currency)}: la moneda la manda la factura.`;
}

/** Cartel neutro EXACTO cuando la reserva no tiene ninguna factura activa de la moneda del servicio (spec §4). */
export function buildEmptyCurrencyMessage(currency) {
  return `No encontramos una factura en ${nombreDeLaMoneda(currency)} en esta reserva. Revisá que la factura de este servicio exista antes de emitir la devolución.`;
}

/**
 * Filtra las facturas activas de la reserva (ya armadas por `getActiveSaleInvoices`, con el label
 * "Factura B 0001-00012345 — US$ 900") para quedarnos solo con las de la moneda de ESTE servicio.
 * Regla dura multimoneda: un servicio en dólares SOLO se resuelve contra una factura en dólares.
 *
 * @param {Array<{currency: string}>} activeSaleInvoices - de getActiveSaleInvoices(reserva.invoices)
 * @param {string} currency - moneda del renglón que se está resolviendo (line.currency)
 * @returns {Array<object>}
 */
export function filterInvoicesByCurrency(activeSaleInvoices, currency) {
  return (Array.isArray(activeSaleInvoices) ? activeSaleInvoices : [])
    .filter((invoice) => invoice.currency === currency);
}

/**
 * Encabezado de solo lectura del formulario en línea (spec §2 punto 1): ancla el nombre del
 * servicio para que nunca se mezcle con otro renglón pendiente (el bug real que destapó Gastón).
 *
 * @param {{serviceName: string, currency: string, suggestedAmount: number}} line
 * @returns {string}
 */
export function buildResolverFormHeading(line) {
  return `Resolver la devolución de: ${line?.serviceName ?? ""} — ${formatCurrency(line?.suggestedAmount, line?.currency)}`;
}

/**
 * Texto de la fila ya resuelta (spec §3): "Resuelto ✓ · {factura} · {monto}".
 *
 * @param {{targetInvoiceLabel?: string, confirmedGrossCreditAmount?: number, currency: string}} line
 * @returns {string}
 */
export function buildResolvedRowText(line) {
  return `Resuelto ✓  ·  ${line?.targetInvoiceLabel ?? ""}  ·  ${formatCurrency(line?.confirmedGrossCreditAmount, line?.currency)}`;
}

/**
 * Habilita el botón "Guardar esta devolución" (spec §2 punto 5): hace falta factura elegida, monto
 * mayor a cero y motivo cargado. El mínimo de 10 caracteres del motivo replica la validación real
 * del backend (`ResolvePartialCreditNoteRequest.Reason`, MinLength 10) — la validación de acá es
 * solo para la UX (habilitar/deshabilitar el botón); la que manda siempre es la del servidor.
 *
 * @param {{targetInvoicePublicId: string, amount: string|number, reason: string}} form
 * @returns {boolean}
 */
export function canSaveResolverRow({ targetInvoicePublicId, amount, reason }) {
  const numericAmount = Number(amount);
  const reasonLength = (reason ?? "").trim().length;
  return Boolean(targetInvoicePublicId) && Number.isFinite(numericAmount) && numericAmount > 0 && reasonLength >= 10;
}

/**
 * Arma el body EXACTO de `PATCH /cancellations/{id}/resolve-partial-credit-note`
 * (`ResolvePartialCreditNoteRequest`). Siempre manda `bookingCancellationLinePublicId`: con varios
 * servicios pendientes al mismo tiempo (el caso real de Gastón: hotel USD + excursión ARS) el
 * backend necesita saber CUÁL renglón se está resolviendo para no tocar los demás.
 *
 * @param {{linePublicId: string}} line
 * @param {{targetInvoicePublicId: string, confirmedGrossCreditAmount: string|number, reason: string}} form
 * @returns {{targetInvoicePublicId: string, confirmedGrossCreditAmount: number, reason: string, bookingCancellationLinePublicId: string}}
 */
export function buildResolvePayload(line, { targetInvoicePublicId, confirmedGrossCreditAmount, reason }) {
  return {
    targetInvoicePublicId,
    confirmedGrossCreditAmount: Number(confirmedGrossCreditAmount),
    reason: (reason ?? "").trim(),
    bookingCancellationLinePublicId: line.linePublicId,
  };
}

/**
 * Mensaje de error al GUARDAR una fila (spec §5, "Error del server al guardar (recuperable)" vs.
 * "Error de guarda del backend"). Distingue los dos casos que pide la spec:
 *   - El backend rechazó por una regla de negocio real (tope de saldo, moneda incoherente, otra NC
 *     en vuelo, etc.): esos mensajes YA vienen limpios en criollo desde `BusinessInvariantViolationException`
 *     (ver `ResolvePartialCreditNoteAsync` en el backend) — se muestran tal cual.
 *   - Falla de red/transporte pura (sin body útil del servidor): acá SIEMPRE devolvemos el texto
 *     EXACTO que pide la spec para este formulario en particular ("No se pudo guardar...").
 *
 * Retoque post-review (2026-07-17, mismo criterio que `t5ErrorMessage`): solo confiamos en el
 * mensaje "libre" cuando viene del BODY real del servidor (`error.payload`). Sin `payload` (falla
 * de red pura, timeout, texto crudo de la librería HTTP que ni siquiera esté en la lista de
 * genéricos conocidos de lib/errors.js), vamos directo al texto fijo de la spec — nunca leemos
 * `error.message` a ciegas, que podría traer algo técnico en inglés sin traducir.
 *
 * @param {unknown} error
 * @returns {string}
 */
export function resolveRowSaveErrorMessage(error) {
  if (!error?.payload) {
    return "No se pudo guardar. Revisá la conexión y probá de nuevo.";
  }
  const backendMessage = getApiErrorMessage(error, "");
  if (!backendMessage || backendMessage === SPANISH_NETWORK_GENERIC) {
    return "No se pudo guardar. Revisá la conexión y probá de nuevo.";
  }
  return backendMessage;
}

/**
 * Arma el body de `POST /cancellations/{id}/emit-partial-credit-note` para emitir la devolución de
 * ESTA fila puntual. Como cada devolución vieja puede resolver a una factura distinta (una NC por
 * moneda, nunca mezcladas), siempre hay que decirle al backend cuál factura se está emitiendo.
 *
 * @param {{targetInvoicePublicId: string}} line
 * @returns {{targetInvoicePublicId: string}}
 */
export function buildEmitPayloadForLine(line) {
  return { targetInvoicePublicId: line.targetInvoicePublicId };
}
