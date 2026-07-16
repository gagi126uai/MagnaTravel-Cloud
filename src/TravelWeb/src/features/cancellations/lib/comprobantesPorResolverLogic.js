/**
 * Lógica PURA del monitor pasivo "Comprobantes por resolver" (ADR-044 T4, spec
 * `docs/ux/2026-07-10-t4-multas-pantallas.md`, sección 3).
 *
 * Decisión de Gastón (2026-07-10, ADR-044 decisión final #2): la pantalla "Pendientes
 * con AFIP" se desarma. La resolución de cada cosa vive en la ficha (ya hecho
 * 2026-07-08); acá queda SOLO una lista para MIRAR (nunca botones de acción — cada
 * fila es un link a la ficha) que FUNDE dos bandejas que antes eran solapas separadas:
 *   - "Cargos de cancelación pendientes" (multas/NDs, ver debitNoteInboxLogic.js).
 *   - "Notas de crédito por revisar" (NC parcial en revisión manual, ADR-025 §3).
 *
 * "Recibos por regularizar" NO se fusiona acá: tiene botones de acción reales (no es
 * solo para mirar), así que queda como pantalla aparte dentro de Facturación.
 */

import { textoQueFalta, textoTiempoRelativo } from "../debitNoteInboxLogic.js";

/**
 * Traduce el `status` de una fila de "NC por revisar" a un texto en criollo de "qué
 * falta" — mismo criterio de voz que `textoQueFalta` (nunca el string técnico crudo).
 *
 * Hoy la bandeja de origen (PendingCreditNoteReviewDto) no expone una fecha de
 * vencimiento (RG 4540) para poder sumar el aviso "vence en X días" que pide la spec
 * (sección 3.2) — ver el hallazgo reportado en la entrega de esta tanda. Mientras ese
 * campo no exista, esta función devuelve el texto fijo de "falta revisar", sin inventar
 * una cuenta regresiva que no está respaldada por datos reales.
 *
 * @param {string} status
 * @returns {string}
 */
export function textoQueFaltaNotaCredito(status) {
  if (status === "RequiresManualReview") return "Falta que alguien la revise";
  return "Falta confirmar y emitir la devolución";
}

/**
 * Funde las dos listas de origen en una sola lista pasiva, ordenada, lista para
 * mostrar en "Comprobantes por resolver". Cada fila trae exactamente lo que la
 * columna "Comprobante" necesita mostrar — nunca el DTO crudo del backend.
 *
 * @param {Array<object>} itemsMultas - CancellationDebitNotePendingDto[] (GET /cancellations/debit-notes/pending)
 * @param {Array<object>} itemsNotasCredito - PendingCreditNoteReviewDto[] (GET /cancellations/pending-credit-note-review)
 * @returns {Array<{ key: string, comprobante: string, reservaPublicId: string|null, reservaNumero: string, queFalta: string, haceCuanto: string }>}
 */
export function fusionarComprobantesPorResolver(itemsMultas, itemsNotasCredito) {
  const filasMultas = (Array.isArray(itemsMultas) ? itemsMultas : []).map((row) => ({
    key: `multa-${row.bookingCancellationPublicId}`,
    comprobante: "Multa · cargo al cliente",
    reservaPublicId: row.reservaPublicId ?? null,
    reservaNumero: row.reservaNumero,
    queFalta: textoQueFalta(row.debitNoteStatus),
    haceCuanto: textoTiempoRelativo(row.confirmedAt),
  }));

  const filasNotasCredito = (Array.isArray(itemsNotasCredito) ? itemsNotasCredito : []).map((row) => ({
    key: `nc-${row.bookingCancellationPublicId}`,
    comprobante: "DEVOLUCIÓN · SERVICIO ANULADO",
    reservaPublicId: row.reservaPublicId ?? null,
    reservaNumero: row.reservaNumero,
    queFalta: textoQueFaltaNotaCredito(row.status),
    haceCuanto: textoTiempoRelativo(row.enteredReviewAt),
  }));

  // Orden fijo: multas primero, NC después (mismo orden que el mockup de la spec).
  // Dentro de cada tipo se conserva el orden que ya trae el backend.
  return [...filasMultas, ...filasNotasCredito];
}
