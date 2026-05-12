// B1.15 Fase D'.B (2026-05-11): logica de acciones disponibles por kind+status.
//
// Separado del componente para ser testeable con node:test sin DOM.
// La fuente de verdad de la matriz esta aqui; MovementsTimeline la consume.
//
// Acciones posibles:
//   "view_pdf"     — abrir PDF en nueva pestaña.
//   "download_pdf" — descargar PDF.
//   "annul"        — anular factura (genera NC). Solo para "invoice".
//   "retry"        — reintentar emision AFIP.
//   "void_receipt" — anular comprobante interno de cobro. Solo para "payment"
//                    cuando receiptStatus === "Issued".
//
// Restriccion de negocio:
//   - Las NC (credit_note) NO se anulan desde la UI; ya son resultado de anulacion.
//   - Las ND (debit_note) NO se anulan desde la UI; se contra-asientan con otra NC
//     o ND segun normativa ARCA/RG-1415. Solo se puede reintentar si fue rechazada.
//   - credit_note y debit_note comparten la misma matriz de acciones.

/**
 * Devuelve la lista de acciones disponibles para un movimiento.
 *
 * @param {string} kind  - "invoice" | "credit_note" | "debit_note" | "payment" | "credit_note_reversal"
 * @param {string} status - "Approved" | "Rejected" | "InProgress" | "Annulled" | "Paid" | "Pending" | "Cancelled"
 * @param {object} [opts] - Opciones adicionales para condiciones dependientes del item.
 * @param {string|null} [opts.receiptStatus] - "Issued" | "Voided" | null. Solo relevante para payment.
 * @returns {string[]} Array de action keys. Vacio = sin acciones.
 */
export function getMovementActions(kind, status, opts = {}) {
  if (kind === "invoice") {
    switch (status) {
      case "Approved":
        return ["view_pdf", "download_pdf", "annul"];
      case "Rejected":
        return ["retry"];
      case "InProgress":
      case "Annulled":
      default:
        return [];
    }
  }

  // credit_note y debit_note comparten la misma matriz: solo ver/descargar PDF
  // cuando estan aprobadas, reintentar si fueron rechazadas.
  // Las ND no se pueden anular desde la UI (restriccion fiscal ARCA).
  if (kind === "credit_note" || kind === "debit_note") {
    switch (status) {
      case "Approved":
        return ["view_pdf", "download_pdf"];
      case "Rejected":
        return ["retry"];
      case "InProgress":
      case "Annulled":
      default:
        return [];
    }
  }

  if (kind === "payment") {
    // "void_receipt" disponible solo cuando hay un comprobante emitido (no anulado).
    if (opts.receiptStatus === "Issued") {
      return ["void_receipt"];
    }
    return [];
  }

  // credit_note_reversal u otro: sin acciones.
  return [];
}
