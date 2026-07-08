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
 *   - GET    /api/cancellations/by-reserva/:reservaPublicId (cancelacion vigente de una reserva)
 *   - GET    /api/cancellations/debit-notes/pending (bandeja back-office)
 *   - POST   /api/cancellations/:id/retry-credit-notes (ADR-042: reintenta NC faltantes de anulacion multi-factura)
 *   - POST   /api/cancellations/:id/retry-debit-note (reintenta la ND de la multa trabada/fallida)
 *   - PATCH  /api/cancellations/:id/correct-penalty (corrige monto/moneda de la multa trabada)
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
   * ADR-014 (read-model): obtiene la cancelacion VIGENTE de una reserva por el GUID de la
   * RESERVA. Reemplaza al viejo getPendingDebitNoteByReservaNumero, que buscaba en la bandeja
   * back-office de NDs pendientes y dejaba afuera el caso pass-through (multa del operador
   * estimada, ND aun no aplicable).
   *
   * El DTO devuelto trae canConfirmPenalty + confirmPenaltyBlockedReason, que el panel de
   * "Confirmar multa del operador" usa para decidir si habilitar el flujo o avisar el motivo.
   *
   * @param {string} reservaPublicId - GUID de la reserva.
   * @returns {Promise<BookingCancellationDto>} - La cancelacion vigente.
   *   Lanza error con status 404 si la reserva no tiene cancelacion no-abortada.
   */
  getByReserva: (reservaPublicId) =>
    api.get(`/cancellations/by-reserva/${reservaPublicId}`),

  /**
   * 2026-06-28: cierra el paso de multa del operador SIN emitir nota de débito.
   * Se usa cuando el operador NO cobró ninguna penalidad por la anulación.
   *
   * Requiere permiso cancellations.classify_agency_penalty (igual que confirmPenalty).
   * El body exige un motivo obligatorio (5..500 caracteres).
   *
   * Tras el éxito, el DTO devuelto tendrá:
   *   CanConfirmPenalty = false
   *   ConfirmPenaltyBlockedReason = "OperatorPenaltyWaived"
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @param {string} reason   - Motivo del cierre sin multa (5..500 chars).
   * @returns {Promise<BookingCancellationDto>}
   */
  waivePenalty: (publicId, reason) =>
    api.patch(`/cancellations/${publicId}/waive-penalty`, { reason }),

  /**
   * 2026-06-28: deshace un cierre "sin multa" previamente registrado.
   * Solo disponible para administradores — el backend devuelve 403 para no-Admin.
   * Reabre el paso pendiente de multa: CanConfirmPenalty vuelve a true.
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @param {string} reason   - Motivo del deshacer (5..500 chars).
   * @returns {Promise<BookingCancellationDto>}
   */
  revertWaive: (publicId, reason) =>
    api.patch(`/cancellations/${publicId}/revert-waive`, { reason }),

  /**
   * Anula una reserva en firme que tiene cobros pero sin factura emitida.
   * Los pagos cobrados al cliente quedan como saldo a favor, reutilizables en otra reserva.
   * Corresponde al caso "PaymentsToCredit" del discriminador cancellationCase del DTO.
   *
   * Endpoint: POST /api/reservas/{reservaPublicId}/annul-with-credit { reason }.
   *
   * Errores posibles:
   *   - 400: motivo invalido (< 10 caracteres o vacio). El front valida primero, pero el
   *          backend tambien lo controla server-side.
   *   - 403: sin permiso para anular la reserva.
   *   - 404: reserva no encontrada.
   *   - 409: precondicion de negocio no cumplida (estado no firme, factura CAE viva,
   *          sin cobros, reserva sin pagador). El body trae un mensaje descriptivo.
   *
   * @param {string} reservaPublicId - GUID de la reserva a anular.
   * @param {string} reason - Motivo de la anulacion (>=10 caracteres, validado server-side).
   * @returns {Promise<ReservaDto>} - La reserva actualizada con estado terminal (Cancelled).
   */
  annulWithCredit: (reservaPublicId, reason) =>
    api.post(`/reservas/${reservaPublicId}/annul-with-credit`, { reason }),

  /**
   * ADR-042 (2026-07-01): reintenta SOLO las notas de crédito faltantes de una anulación
   * multi-factura que quedó a medias (una NC salió y otra no). Idempotente: no re-emite la
   * NC que ya salió ni duplica. Mismo permiso que anular (reservas.cancel + ownership) —
   * reintentar no es "deshacer", es completar lo que ya se autorizó al confirmar.
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @returns {Promise<BookingCancellationDto>} - El BC actualizado (CreditNotes con los nuevos Pending).
   */
  retryCreditNotes: (publicId) =>
    api.post(`/cancellations/${publicId}/retry-credit-notes`),

  /**
   * Spec "el paso de multa vive en la ficha" (2026-07-08): reintenta la emisión de la
   * Nota de Débito de la multa del operador cuando quedó "Failed" (falló en AFIP/ARCA)
   * o "ConfirmedNoDebitNote" (la multa está confirmada pero la ND nunca llegó a crearse).
   * Mismo botón sirve para los dos casos — el backend decide qué corresponde según el
   * estado real de operatorPenaltySituation.
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @returns {Promise<BookingCancellationDto>}
   */
  retryDebitNote: (publicId) =>
    api.post(`/cancellations/${publicId}/retry-debit-note`),

  /**
   * Spec "el paso de multa vive en la ficha" (2026-07-08): corrige el monto y la moneda
   * de una multa que quedó trabada en revisión manual (operatorPenaltySituation.state ===
   * "DebitNoteNeedsAmountCurrency"). Se usa cuando el monto o la moneda originales eran
   * incorrectos y por eso la Nota de Débito no pudo terminar de emitirse.
   *
   * Errores posibles: 400 (datos invalidos, el body trae {message}); 409 con
   * {invariantCode} (regla de negocio) o {code:"CONCURRENT_EDIT"} (otro usuario lo tocó
   * al mismo tiempo).
   *
   * @param {string} publicId - GUID del BookingCancellation.
   * @param {object} payload - { amount: number, currency: "ARS"|"USD", reason: string }
   * @returns {Promise<BookingCancellationDto>}
   */
  correctPenalty: (publicId, payload) =>
    api.patch(`/cancellations/${publicId}/correct-penalty`, payload),
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
