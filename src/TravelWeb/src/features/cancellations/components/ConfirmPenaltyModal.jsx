/**
 * Modal para confirmar la penalidad diferida de la agencia (ADR-014).
 *
 * Se usa DIAS DESPUES de la cancelacion, cuando el operador confirma el monto
 * definitivo de la penalidad propia de la agencia. Dispara la emision de la
 * Nota de Debito fiscal.
 *
 * Flujo:
 *   - El agente ingresa: monto confirmado, fecha de confirmacion del operador,
 *     tipo de concepto (Cargo de gestion / Cargo de cancelacion),
 *     y referencia documental opcional (link/referencia al mail o PDF del operador).
 *   - Si NO adjunta referencia documental, el backend puede requerir 4-eyes (aprobacion).
 *   - PATCH /api/cancellations/:id/confirm-penalty
 *
 * Errores 409 manejados:
 *   - requiresApproval → abre RequestApprovalModal.
 *   - CONCURRENT_EDIT → "alguien mas modifico, reintenta".
 *   - INV-ADR014-003 → "la penalidad ya fue confirmada / la ND ya esta en juego".
 *   - INV-ADR014-001 → "la nota de credito todavia no tiene CAE, esperá".
 *   - INV-ADR014-002 → "esta penalidad es del operador, no emite cargo propio".
 *
 * Props:
 *   - cancellationPublicId: GUID del BookingCancellation.
 *   - reservaNumero: numero de reserva para el header (legibilidad).
 *   - isOpen: booleano de visibilidad.
 *   - onClose: callback de cierre.
 *   - onConfirmed: callback luego de confirmar exitosamente.
 */

import { useState, useEffect } from "react";
import { X, AlertTriangle, AlertCircle, Loader2, FileCheck2 } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";

// Valor INT de CancellationConceptKind del backend.
// El backend NO tiene JsonStringEnumConverter (solo ReferenceHandler en Program.cs),
// por lo que espera int, no string.
// Fuente verificada: CancellationConceptKind.cs
const CONCEPT_KIND_INT = {
  AgencyManagementFee: 1,
  AgencyCancellationFee: 2,
};

const AGENCY_CONCEPT_OPTIONS = [
  { value: "AgencyManagementFee", label: "Cargo de gestion" },
  { value: "AgencyCancellationFee", label: "Cargo de cancelacion" },
];

// Fecha de hoy en formato YYYY-MM-DD para el campo date (max del input).
function getTodayDateString() {
  return new Date().toISOString().split("T")[0];
}

export default function ConfirmPenaltyModal({
  cancellationPublicId,
  reservaNumero,
  isOpen,
  onClose,
  onConfirmed,
}) {
  const [conceptKind, setConceptKind] = useState("AgencyManagementFee");
  const [confirmedAmount, setConfirmedAmount] = useState("");
  const [operatorConfirmationDate, setOperatorConfirmationDate] = useState(getTodayDateString());
  const [supportingDocumentReference, setSupportingDocumentReference] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [conflictMessage, setConflictMessage] = useState(null);

  // 409 requiresApproval: abre el modal de solicitud de aprobacion.
  const [approvalContext, setApprovalContext] = useState(null);

  // Resetea el formulario cuando se abre el modal.
  // useEffect con [isOpen]: solo corre al cambiar la visibilidad.
  useEffect(() => {
    if (!isOpen) return;
    setConceptKind("AgencyManagementFee");
    setConfirmedAmount("");
    setOperatorConfirmationDate(getTodayDateString());
    setSupportingDocumentReference("");
    setConflictMessage(null);
    setApprovalContext(null);
  }, [isOpen]);

  if (!isOpen) return null;

  const amountValue = parseFloat(confirmedAmount) || 0;
  const amountOk = amountValue > 0;
  const dateOk = Boolean(operatorConfirmationDate);
  const canSubmit = amountOk && dateOk && !submitting;

  const handleSubmit = async () => {
    if (!canSubmit) return;

    setSubmitting(true);
    setConflictMessage(null);

    // conceptKind va como INT (el backend no tiene JsonStringEnumConverter).
    // El estado local "conceptKind" es el string de la UI ("AgencyManagementFee", etc).
    // Lo mapeamos al int correspondiente con CONCEPT_KIND_INT.
    const conceptKindInt = CONCEPT_KIND_INT[conceptKind] ?? CONCEPT_KIND_INT.AgencyManagementFee;

    const payload = {
      conceptKind: conceptKindInt,
      confirmedPenaltyAmount: amountValue,
      // El input type=date devuelve "YYYY-MM-DD". El backend espera un DateTime,
      // asi que agregamos la hora en UTC para que el parsing no falle.
      operatorConfirmationDate: operatorConfirmationDate + "T00:00:00Z",
      debitNotePurpose: null, // el backend usa PenaltyOrCancellationCharge por default
      supportingDocumentReference: supportingDocumentReference.trim() || null,
      overrideReason: null,
      approvalRequestPublicId: null,
    };

    try {
      await cancellationsApi.confirmPenalty(cancellationPublicId, payload);
      showSuccess(
        "El cargo de la agencia quedo en proceso de emision en AFIP/ARCA. Podes seguir el estado en la bandeja de cargos pendientes.",
        "Cargo confirmado"
      );
      onConfirmed();
    } catch (error) {
      const errorPayload = error?.payload;

      if (error?.status === 409 && errorPayload?.requiresApproval) {
        // Redirige al flujo de 4-eyes.
        setApprovalContext({
          requestType: errorPayload.requestType,
          entityType: errorPayload.entityType,
          entityId: errorPayload.entityId,
          entityLabel: `Cargo de cancelacion — Reserva #${reservaNumero}`,
        });
        setSubmitting(false);
        return;
      }

      if (error?.status === 409) {
        const code = errorPayload?.code || "";
        const invariantCode = errorPayload?.invariantCode || "";
        let humanMessage;

        if (code === "CONCURRENT_EDIT") {
          humanMessage = "Otro usuario modifico esta cancelacion al mismo tiempo. Reintentá en unos segundos.";
        } else if (invariantCode === "INV-ADR014-003") {
          humanMessage = "Este cargo ya fue confirmado o la nota de debito ya esta en proceso. Si hay un problema, habla con administracion.";
        } else if (invariantCode === "INV-ADR014-001") {
          humanMessage = "La nota de credito todavia no tiene CAE aprobado en AFIP/ARCA. Esperá que se procese y volvé a intentar.";
        } else if (invariantCode === "INV-ADR014-002") {
          humanMessage = "Esta penalidad corresponde al operador, no a la agencia. No se emite un cargo propio.";
        } else if (invariantCode === "INV-ADR014-PERM" || invariantCode === "INV-ADR013-PERM") {
          humanMessage = "No tenes permiso para confirmar un cargo propio de la agencia. Habla con un administrador.";
        } else {
          humanMessage = getApiErrorMessage(error, "No se pudo confirmar el cargo.");
        }

        setConflictMessage(humanMessage);
      } else if (error?.status === 400) {
        setConflictMessage(
          getApiErrorMessage(error, "La fecha de confirmacion es invalida. Verifica que no sea futura ni anterior a la cancelacion.")
        );
      } else {
        showError(getApiErrorMessage(error, "No se pudo confirmar el cargo de la agencia."));
      }

      setSubmitting(false);
    }
  };

  return (
    <>
      <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
        <div className="w-full max-w-md rounded-2xl border bg-white dark:bg-slate-900 shadow-2xl max-h-[90vh] overflow-y-auto">

          {/* ── Header ── */}
          <div className="px-6 py-4 border-b border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-900/80 flex items-center justify-between">
            <div className="flex items-center gap-3">
              <div className="rounded-lg bg-orange-100 p-2 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300">
                <FileCheck2 className="h-5 w-5" />
              </div>
              <div>
                <h2 className="text-lg font-bold text-slate-900 dark:text-white">Confirmar cargo de la agencia</h2>
                <p className="text-xs text-slate-500 dark:text-slate-400">Reserva #{reservaNumero}</p>
              </div>
            </div>
            <button
              type="button"
              onClick={onClose}
              disabled={submitting}
              className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 disabled:opacity-40"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          {/* ── Cuerpo ── */}
          <div className="p-6 space-y-5">

            {/* Error de conflicto 409 */}
            {conflictMessage && (
              <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2">
                <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                <span>{conflictMessage}</span>
              </div>
            )}

            {/* Advertencia de accion irreversible */}
            <div className="rounded-lg border border-orange-200 bg-orange-50 p-4 text-sm text-orange-800 dark:bg-orange-950/30 dark:border-orange-800 dark:text-orange-200 flex items-start gap-2">
              <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" />
              <div>
                Al confirmar se emite la <strong>nota de debito (cargo de la agencia)</strong> en AFIP/ARCA.
                Esta accion no se puede deshacer. Solo confirma si el operador ya te comunico el monto definitivo.
              </div>
            </div>

            {/* Tipo de concepto */}
            <div>
              <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5">
                Tipo de cargo
              </label>
              <div className="flex gap-4">
                {AGENCY_CONCEPT_OPTIONS.map((opt) => (
                  <label key={opt.value} className="flex items-center gap-1.5 text-sm cursor-pointer">
                    <input
                      type="radio"
                      name="concept-kind"
                      value={opt.value}
                      checked={conceptKind === opt.value}
                      onChange={() => setConceptKind(opt.value)}
                      className="accent-orange-600"
                    />
                    <span className="text-slate-700 dark:text-slate-200">{opt.label}</span>
                  </label>
                ))}
              </div>
            </div>

            {/* Monto confirmado */}
            <div>
              <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor="confirmed-amount">
                Monto confirmado por el operador (ARS) <span className="text-rose-500">*</span>
              </label>
              <input
                id="confirmed-amount"
                type="number"
                min="0.01"
                step="0.01"
                value={confirmedAmount}
                onChange={(e) => setConfirmedAmount(e.target.value)}
                placeholder="0.00"
                disabled={submitting}
                className={`w-full rounded-xl border px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 dark:bg-slate-800 dark:text-white disabled:opacity-50 ${
                  confirmedAmount && !amountOk ? "border-rose-400" : "border-slate-300 dark:border-slate-600"
                }`}
              />
              {confirmedAmount && !amountOk && (
                <div className="mt-1 text-xs text-rose-600">El monto debe ser mayor a cero.</div>
              )}
            </div>

            {/* Fecha de confirmacion del operador */}
            <div>
              <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor="operator-date">
                Fecha en que el operador confirmo el monto <span className="text-rose-500">*</span>
              </label>
              <input
                id="operator-date"
                type="date"
                value={operatorConfirmationDate}
                onChange={(e) => setOperatorConfirmationDate(e.target.value)}
                max={getTodayDateString()}
                disabled={submitting}
                className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 disabled:opacity-50"
              />
              <div className="mt-1 text-xs text-slate-400">
                Ingresa la fecha en que el operador te informo el monto (puede ser anterior a hoy).
              </div>
            </div>

            {/* Referencia documental (opcional) */}
            <div>
              <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor="supporting-doc">
                Referencia documental <span className="text-slate-400 font-normal">(opcional)</span>
              </label>
              <input
                id="supporting-doc"
                type="text"
                value={supportingDocumentReference}
                onChange={(e) => setSupportingDocumentReference(e.target.value)}
                maxLength={500}
                disabled={submitting}
                placeholder="Link al mail, numero de nota, referencia del PDF..."
                className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 disabled:opacity-50"
              />
              {/* Aviso: sin referencia documental el backend puede requerir 4-eyes. */}
              {!supportingDocumentReference.trim() && (
                <div className="mt-1.5 text-xs text-amber-700 dark:text-amber-300 flex items-start gap-1">
                  <AlertTriangle className="h-3 w-3 flex-shrink-0 mt-0.5" />
                  <span>
                    Sin respaldo documental puede requerirse autorizacion adicional del administrador.
                  </span>
                </div>
              )}
            </div>
          </div>

          {/* ── Footer ── */}
          <div className="px-6 py-4 border-t border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-900/80 flex flex-col-reverse sm:flex-row sm:justify-end gap-2">
            <button
              type="button"
              onClick={onClose}
              disabled={submitting}
              className="px-4 py-2.5 rounded-xl text-sm font-bold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors disabled:opacity-50"
            >
              Volver
            </button>
            <button
              type="button"
              onClick={handleSubmit}
              disabled={!canSubmit}
              data-testid="confirm-penalty-submit-btn"
              className="px-4 py-2.5 rounded-xl text-sm font-bold text-white bg-orange-600 hover:bg-orange-700 transition-colors disabled:opacity-50 flex items-center gap-2"
            >
              {submitting ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Confirmar y emitir cargo
            </button>
          </div>
        </div>
      </div>

      {/* Modal de solicitud de aprobacion 4-eyes */}
      <RequestApprovalModal
        isOpen={Boolean(approvalContext)}
        onClose={() => setApprovalContext(null)}
        onCreated={() => {
          setApprovalContext(null);
          showSuccess(
            "Solicitud enviada al administrador. El cargo quedara pendiente hasta que lo autoricen.",
            "Solicitud enviada"
          );
          onClose();
        }}
        requestType={approvalContext?.requestType}
        entityType={approvalContext?.entityType}
        entityId={approvalContext?.entityId}
        entityLabel={approvalContext?.entityLabel}
      />
    </>
  );
}
