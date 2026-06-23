/**
 * Wrapper de llamadas HTTP del modulo de cancelaciones.
 *
 * Centraliza los endpoints para que ningun componente o hook construya URLs
 * directamente. Sigue el mismo patron que creditNoteReconciliationApi.js.
 *
 * Endpoints que cubre:
 *   - POST   /api/cancellations               (draft)
 *   - PATCH  /api/cancellations/:id/confirm   (confirmar, emite NC)
 *   - PATCH  /api/cancellations/:id/abort     (descartar borrador)
 *   - PATCH  /api/cancellations/:id/confirm-penalty (diferida, emite ND)
 *   - GET    /api/cancellations/:id           (estado actual)
 *   - GET    /api/cancellations/debit-notes/pending (bandeja back-office)
 */

import { api } from "../../../api";

export const cancellationsApi = {
  /**
   * T-1: crea un BookingCancellation en estado Drafted.
   * El agente todavia puede arrepentirse (abort) antes de confirmar.
   *
   * @param {string} reservaPublicId - GUID de la reserva a cancelar.
   * @param {string} reason - Motivo de cancelacion (10-1000 chars).
   * @returns {Promise<BookingCancellationDto>}
   */
  draft: (reservaPublicId, reason) =>
    api.post("/cancellations", { reservaPublicId, reason }),

  /**
   * T0: confirma la cancelacion. Dispara la emision de la nota de credito en AFIP/ARCA (async).
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @param {object} payload - Shape de ConfirmCancellationRequest.
   *   - snapshotData: datos fiscales congelados (condiciones tributarias, moneda, TC).
   *   - isAdminOverride: bool, si el admin fuerza con un approval previo.
   *   - overrideReason: string opc, requerido si isAdminOverride=true.
   *   - approvalRequestPublicId: GUID opc, requerido si isAdminOverride=true.
   *   - penaltyConceptKind: INT (0=OperatorPenaltyPassThrough, 1=AgencyManagementFee, 2=AgencyCancellationFee) | null
   *   - penaltyStatus: INT (0=Estimated, 1=Confirmed) | null
   *   - debitNotePurpose: INT (0=PenaltyOrCancellationCharge) | null
   *   - confirmedPenaltyAmount: number | null
   *
   * NOTA: el backend NO tiene JsonStringEnumConverter (Program.cs solo tiene ReferenceHandler).
   * Los enums se mandan como INT, no como strings.
   * @returns {Promise<BookingCancellationDto>}
   */
  confirm: (publicId, payload) =>
    api.patch(`/cancellations/${publicId}/confirm`, payload),

  /**
   * Descarta un borrador (Drafted → Aborted). Irreversible pero seguro: todavia
   * no se toco AFIP/ARCA.
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @param {string} reason - Motivo del descarte (min 5 chars).
   * @returns {Promise<BookingCancellationDto>}
   */
  abort: (publicId, reason) =>
    api.patch(`/cancellations/${publicId}/abort`, { reason }),

  /**
   * ADR-014: confirmacion DIFERIDA de la penalidad propia de la agencia.
   * Se llama DIAS despues de la cancelacion, cuando el operador confirma el monto.
   * Dispara la emision de la Nota de Debito.
   *
   * Requiere permiso cancellations.classify_agency_penalty (resuelto server-side).
   * El backend puede devolver 409 requiresApproval si aplica 4-eyes.
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @param {object} payload - Shape de ConfirmPenaltyRequest.
   *   - conceptKind: INT (1=AgencyManagementFee, 2=AgencyCancellationFee) | null
   *   - confirmedPenaltyAmount: number (obligatorio > 0)
   *   - operatorConfirmationDate: string ISO date (obligatorio)
   *   - debitNotePurpose: null (MVP usa PenaltyOrCancellationCharge)
   *   - supportingDocumentReference: string opc (referencia al mail/PDF del operador)
   *   - overrideReason: string opc (para 4-eyes)
   *   - approvalRequestPublicId: GUID opc (para 4-eyes)
   * @returns {Promise<BookingCancellationDto>}
   */
  confirmPenalty: (publicId, payload) =>
    api.patch(`/cancellations/${publicId}/confirm-penalty`, payload),

  /**
   * ADR-025: cancela UN servicio dentro de una reserva activa.
   * La reserva sigue viva; solo ese servicio queda con workflowStatus="Cancelado".
   * Baja el saldo del cliente y la deuda del operador en la misma operación.
   * NO emite NC automática (decisión #3 ADR-025 — queda en revisión manual).
   *
   * Puede devolver 409 cuando hay factura con CAE viva o voucher emitido que bloquea
   * la cancelación fiscal. En ese caso el backend trae un mensaje descriptivo en el body.
   *
   * @param {object} payload - CancelServiceRequest
   *   - reservaPublicId: string GUID
   *   - serviceTable: "Generic"|"Flight"|"Hotel"|"Transfer"|"Package"|"Assistance"
   *   - servicePublicId: string GUID
   *   - reason: string (10-1000 chars)
   * @returns {Promise<CancelServiceResultDto>}
   *   - reservaPublicId, servicePublicId, serviceTable
   *   - cancelledServicesCount, totalServicesWithSupplierCount
   */
  cancelService: (payload) =>
    api.post("/cancellations/cancel-service", payload),

  /**
   * ADR-025: bandeja back-office de cancelaciones de servicio con NC pendiente de revision.
   * Aparece cuando se cancela un servicio y queda una nota de credito sin emitir
   * (flujo NC parcial congelado hasta firma del contador — ADR-025 decision #3).
   *
   * HOY esta lista viene VACIA casi siempre. Es correcto y esperado:
   * el flujo que la llena (NC parcial) esta congelado hasta que el contador firme.
   * El empty state de la bandeja lo explica al usuario.
   *
   * Requiere permiso cobranzas.view_all (back-office).
   *
   * @returns {Promise<PendingCreditNoteReviewDto[]>}
   *   Cada item: { bookingCancellationPublicId, reservaPublicId, reservaNumero,
   *                clienteNombre, status, enteredReviewAt, creditNoteAmount, creditNoteCurrency }
   */
  pendingCreditNoteReview: () =>
    api.get("/cancellations/pending-credit-note-review"),

  /**
   * Lectura del estado actual de un BookingCancellation.
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @returns {Promise<BookingCancellationDto>}
   */
  getByPublicId: (publicId) =>
    api.get(`/cancellations/${publicId}`),

  /**
   * ADR-013/ADR-014: bandeja back-office de cancelaciones con NC emitida pero sin ND.
   * La NC salio con CAE pero la Nota de Debito quedo pendiente, fallida o nunca se creo.
   *
   * Requiere permiso cobranzas.invoice_annul (mismo que el annul de facturas).
   *
   * @returns {Promise<CancellationDebitNotePendingDto[]>}
   */
  getPendingDebitNotes: () =>
    api.get("/cancellations/debit-notes/pending"),

  /**
   * Busca la fila de multa operador pendiente para una reserva concreta.
   *
   * LIMITACION CONOCIDA: el backend no tiene un endpoint by-reserva; esta funcion
   * llama a la bandeja completa y filtra client-side por reservaNumero.
   * Es valido en MVP porque la bandeja es chica (casos abiertos en un momento dado).
   *
   * Si el backend agrega un campo `reservaPublicId` a CancellationDebitNotePendingDto
   * o un endpoint GET /cancellations/debit-notes/pending?reservaPublicId=... ,
   * reemplazar esta funcion por esa llamada directa.
   *
   * @param {string} reservaNumero - Numero de reserva (string, ej. "2025-0042").
   * @returns {Promise<CancellationDebitNotePendingDto|null>} - La fila si se encontro, null si no.
   */
  async getPendingDebitNoteByReservaNumero(reservaNumero) {
    const items = await api.get("/cancellations/debit-notes/pending");
    const found = (items || []).find((item) => item.reservaNumero === reservaNumero);
    return found ?? null;
  },
};

// ============================================================================
// Labels y helpers de presentacion
// ============================================================================

/**
 * Estado de la ND en texto legible para el agente (no siglas).
 *
 * Incluye:
 *   - Valores reales del enum DebitNoteStatus del backend (Pending/Issued/Failed/ManualReview/NotApplicable).
 *   - Pseudo-estados del backend que la bandeja proyecta sin corresponder al enum:
 *       "ConfirmedWithoutDebitNote" (ADR-014 §3.8): penalidad confirmada pero ND nunca se creo.
 *       "EstimatedPendingConfirmation" (ADR-014 M-B2): CASO DOMINANTE — penalidad estimada,
 *         NC ya emitida, esperando que el agente confirme el monto definitivo del operador.
 *         El boton de esta fila abre ConfirmPenaltyModal (igual que ConfirmedWithoutDebitNote).
 */
export const DEBIT_NOTE_STATUS_LABELS = {
  Pending: { label: "En proceso", color: "amber" },
  Issued: { label: "Emitida", color: "emerald" },
  Failed: { label: "Error AFIP/ARCA", color: "rose" },
  ManualReview: { label: "En revision manual", color: "slate" },
  NotApplicable: { label: "No aplica", color: "slate" },
  // Pseudo-estado: penalidad confirmada pero la ND nunca llego a crearse.
  ConfirmedWithoutDebitNote: { label: "Cargo pendiente de emision", color: "orange" },
  // Pseudo-estado: CASO DOMINANTE — penalidad estimada, el agente puede confirmar el monto.
  // La NC ya salio con CAE. La ND se emitira cuando el agente confirme el monto definitivo.
  EstimatedPendingConfirmation: { label: "Estimada — pendiente de confirmar monto", color: "amber" },
};
