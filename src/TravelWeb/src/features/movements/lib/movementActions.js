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
 * @returns {string[]} Array de action keys. Vacio = sin acciones.
 */
export function getMovementActions(kind, status) {
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

  // payment, credit_note_reversal, o cualquier otro: sin acciones por ahora.
  return [];
}
