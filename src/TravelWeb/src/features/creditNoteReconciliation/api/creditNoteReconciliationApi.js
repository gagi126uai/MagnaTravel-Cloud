import { api } from "../../../api";

/**
 * FC1.3 Fase 3 (ADR-010, 2026-05-29): wrapper de los endpoints de la bandeja de
 * reconciliacion de NC parciales con recibos vivos.
 *
 * Centraliza las llamadas a la API para que la pagina y el hook no dupliquen
 * la construccion de URLs ni los nombres de los campos del backend.
 */

export const creditNoteReconciliationApi = {
  /**
   * Lista los casos de reconciliacion con filtros y paginacion.
   *
   * @param {Object} params - Parametros de la query.
   * @param {string} params.status - "pending" | "resolved" | "all"
   * @param {number|null} params.year - Anio del filtro mensual (null = todo el historial).
   * @param {number|null} params.month - Mes 1..12 del filtro mensual (null = todo el historial).
   * @param {number} params.page - Pagina actual (base 1).
   * @param {number} params.pageSize - Cantidad de items por pagina (25/50/100).
   * @returns {Promise<PagedResponse<PartialCreditNoteReconciliationDto>>}
   */
  list: ({ status = "pending", year = null, month = null, page = 1, pageSize = 25 } = {}) => {
    // Construimos los query params solo con los campos que tienen valor, para no
    // mandar "year=null" como string al backend.
    const params = new URLSearchParams();
    params.append("status", status);
    params.append("page", String(page));
    params.append("pageSize", String(pageSize));
    // Filtro mensual: van juntos o ninguno (el backend lo valida igual).
    if (year != null && month != null) {
      params.append("year", String(year));
      params.append("month", String(month));
    }
    return api.get(`/credit-note-reconciliation?${params.toString()}`);
  },

  /**
   * Marca un caso como resuelto manualmente.
   *
   * El backend puede devolver 409 (Conflict) con { message } si:
   *   - el caso ya esta resuelto,
   *   - no se cumplio la regla de cuatro ojos,
   *   - hay recibos vivos y no vienen notas.
   * En esos casos el frontend muestra el message del backend (no un fallback generico).
   *
   * @param {string} publicId - GUID del caso.
   * @param {string|null} notes - Notas del cierre (obligatorias si hay recibos vivos).
   */
  resolve: (publicId, notes) =>
    api.post(`/credit-note-reconciliation/${publicId}/resolve`, { notes: notes || null }),

  /**
   * Anula el recibo de un pago (endpoint existente, reutilizado por esta bandeja).
   * OJO: segun el rol del usuario, este endpoint puede devolver 409 con requiresApproval
   * en vez de anular directo (dispara un workflow de aprobacion). Ese caso se maneja
   * en la UI como un mensaje informativo, no como error fatal.
   *
   * @param {string} paymentPublicId - GUID del PAYMENT (no del recibo directo).
   */
  voidReceipt: (paymentPublicId) =>
    api.post(`/payments/${paymentPublicId}/receipt/void`, { reason: null }),
};

// Labels para mostrar el estado del caso de forma amigable.
export const RECONCILIATION_STATUS_LABELS = {
  Pending: { label: "Pendiente", color: "amber" },
  Resolved: { label: "Resuelto", color: "emerald" },
};

// Labels para el estado vigente de cada recibo individual.
export const RECEIPT_STATUS_LABELS = {
  Issued: { label: "Vivo", color: "rose" },
  Voided: { label: "Anulado", color: "slate" },
};
