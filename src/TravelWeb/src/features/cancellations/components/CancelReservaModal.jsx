/**
 * Modal de 2 pasos para cancelar una reserva.
 *
 * Paso 1 — Borrador (draft):
 *   El agente ingresa el motivo. Banner azul "todavia no se cancela nada".
 *   Al avanzar se llama POST /api/cancellations (crea el draft).
 *   El agente puede descartar el draft en cualquier momento (abort).
 *
 * Paso 2 — Confirmar:
 *   Muestra datos fiscales read-only del snapshot.
 *   Muestra la pregunta de la penalidad (4 opciones en lenguaje de agencia).
 *   La opcion "la agencia cobra un cargo propio" solo aparece si el usuario
 *   tiene permiso cancellations.classify_agency_penalty Y el flag
 *   enableCancellationDebitNote esta ON (ambas condiciones).
 *   Al confirmar: PATCH /api/cancellations/:id/confirm. Emite NC (async).
 *
 * Errores 409 manejados:
 *   - requiresApproval → abre RequestApprovalModal (patron ya existente).
 *   - CONCURRENT_EDIT → banner "alguien mas modifico, recarga".
 *   - INV-ADR014-PERM / INV-ADR013-PERM → "no tenes permiso para esto".
 *   - reclasificacion bloqueada → "ya se emitio, habla con administracion".
 *   - INV-152 (paso 1) → banner "multiples operadores, gestionar manualmente".
 *
 * Props:
 *   - reserva: objeto de la reserva (necesita publicId, numeroReserva, customerName).
 *   - isOpen: booleano de visibilidad.
 *   - onClose: callback de cierre.
 *   - onCancelled: callback luego de confirmar exitosamente la cancelacion.
 *   - canClassifyAgencyPenalty: si el usuario tiene el permiso elevado.
 *   - enableCancellationDebitNote: si el flag operativo esta ON.
 */

import { useState, useEffect } from "react";
import { X, AlertTriangle, Info, AlertCircle, Loader2, Ban } from "lucide-react";
import { api } from "../../../api";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";
import {
  buildPenaltyClassificationPayload,
  buildSnapshotData,
} from "../lib/penaltyPayload";

// ============================================================================
// Las constantes CONCEPT_KIND, PENALTY_STATUS, DEBIT_NOTE_PURPOSE y
// EXCHANGE_RATE_SOURCE, y las funciones buildPenaltyClassificationPayload y
// buildSnapshotData vienen de lib/penaltyPayload.js (importadas arriba).
// Se extrajeron para poder testearlas con node:test sin montar React.
// ============================================================================

// Opciones de la pregunta de penalidad, en lenguaje de mostrador.
// Orden y copy definidos por el ux-ui-travel-retail (flujo_cancelacion_ux.md).
const PENALTY_OPTIONS = [
  {
    value: "none",
    label: "No pierde nada",
    description: "Se le devuelve todo al cliente. El reembolso queda a cargo del operador.",
  },
  {
    // DEFAULT conservador: el operador descuenta pero la agencia no emite nada propio.
    value: "operator_pass_through",
    label: "El operador le descuenta una penalidad",
    description: "La agencia no emite ningun cargo propio. El cliente recibe menos reembolso.",
    isDefault: true,
  },
  {
    // Solo visible si canClassifyAgencyPenalty && enableCancellationDebitNote.
    // La agencia cobra su propio cargo (genera ND fiscal).
    value: "agency_charge",
    label: "La agencia le cobra un cargo",
    description: "La agencia emite un cargo propio (nota de debito). Solo usar si es un ingreso propio de la agencia.",
    requiresAgencyPermission: true,
  },
  {
    value: "insurance",
    label: "Tiene seguro / cobertura",
    description: "El caso pasa a revision manual del back-office.",
  },
];

// Tipo de concepto propio de la agencia: "Cargo de gestion" o "Cargo de cancelacion".
// Mapeados a los valores del enum CancellationConceptKind del backend.
const AGENCY_CONCEPT_OPTIONS = [
  { value: "AgencyManagementFee", label: "Cargo de gestion" },
  { value: "AgencyCancellationFee", label: "Cargo de cancelacion" },
];

// Estado de la penalidad de la agencia: estimada (no emite ND ahora) o confirmada (emite ND).
// Default conservador: estimada.
const PENALTY_STATUS_OPTIONS = [
  {
    value: "Estimated",
    label: "Todavia no confirme el monto exacto (lo confirmo despues)",
    description: "No se emite ningun cargo fiscal ahora. Podras confirmarlo mas adelante desde la bandeja.",
    isDefault: true,
  },
  {
    value: "Confirmed",
    label: "Ya confirme el monto exacto con el operador",
    description: "Se emite la nota de debito al confirmar. Esta accion no se puede deshacer.",
    isWarning: true,
  },
];

export default function CancelReservaModal({
  reserva,
  isOpen,
  onClose,
  onCancelled,
  canClassifyAgencyPenalty,
  enableCancellationDebitNote,
}) {
  // ─── Estado del flujo de 2 pasos ──────────────────────────────────────────
  // step: "draft" (paso 1) | "confirm" (paso 2, una vez creado el draft)
  const [step, setStep] = useState("draft");

  // ─── Paso 1: motivo ───────────────────────────────────────────────────────
  const [reason, setReason] = useState("");
  const [draftingLoading, setDraftingLoading] = useState(false);

  // El draft creado en el paso 1. Lo usamos en el paso 2 para confirmar.
  const [draft, setDraft] = useState(null);

  // ─── Paso 2: datos fiscales y penalidad ───────────────────────────────────
  const [afipSettings, setAfipSettings] = useState(null);
  const [loadingSettings, setLoadingSettings] = useState(false);
  const [settingsError, setSettingsError] = useState(null);

  const [selectedPenaltyOption, setSelectedPenaltyOption] = useState("operator_pass_through");
  const [agencyConceptKind, setAgencyConceptKind] = useState("AgencyManagementFee");
  const [agencyPenaltyStatus, setAgencyPenaltyStatus] = useState("Estimated");
  const [agencyPenaltyAmount, setAgencyPenaltyAmount] = useState("");

  const [confirmingLoading, setConfirmingLoading] = useState(false);

  // ─── Manejo de 409 requiresApproval (patron identico a CreditNoteReconciliationInboxPage) ──
  const [approvalContext, setApprovalContext] = useState(null);

  // ─── Error de 409 genericos (CONCURRENT_EDIT, permisos, etc.) ─────────────
  const [conflictMessage, setConflictMessage] = useState(null);

  // Resetea el formulario completo al cerrar o al abrir de nuevo.
  // useEffect con [isOpen]: corre cuando cambia la visibilidad del modal.
  useEffect(() => {
    if (!isOpen) return;
    setStep("draft");
    setReason("");
    setDraft(null);
    setAfipSettings(null);
    setSettingsError(null);
    setSelectedPenaltyOption("operator_pass_through");
    setAgencyConceptKind("AgencyManagementFee");
    setAgencyPenaltyStatus("Estimated");
    setAgencyPenaltyAmount("");
    setConflictMessage(null);
    setApprovalContext(null);
  }, [isOpen]);

  // Cuando se crea el draft (paso 1 → paso 2), cargamos /afip/settings
  // para pre-llenar los datos fiscales del snapshot.
  // useEffect con [step, draft]: solo corre cuando el paso cambia a "confirm".
  useEffect(() => {
    if (step !== "confirm" || afipSettings !== null) return;

    let cancelled = false;
    setLoadingSettings(true);
    setSettingsError(null);

    (async () => {
      try {
        const data = await api.get("/afip/settings");
        if (!cancelled) setAfipSettings(data);
      } catch {
        if (!cancelled) setSettingsError("No se pudieron cargar los datos fiscales. Podras completarlos manualmente.");
      } finally {
        if (!cancelled) setLoadingSettings(false);
      }
    })();

    return () => { cancelled = true; };
  }, [step, afipSettings]);

  if (!isOpen) return null;

  // ─── PASO 1: crear el draft ───────────────────────────────────────────────

  const handleDraft = async () => {
    const trimmedReason = reason.trim();
    if (trimmedReason.length < 10) {
      showError("El motivo debe tener al menos 10 caracteres.");
      return;
    }
    setDraftingLoading(true);
    setConflictMessage(null);
    try {
      const createdDraft = await cancellationsApi.draft(reserva.publicId, trimmedReason);
      setDraft(createdDraft);
      setStep("confirm");
    } catch (error) {
      // 409: reserva ya tiene cancelacion activa, flag apagado, multi-operador, etc.
      if (error?.status === 409) {
        const errorPayload = error?.payload;

        // INV-152: la reserva tiene servicios de mas de un operador — caso no soportado todavia.
        // Se muestra un mensaje humano en lugar del generico para que el agente sepa que hacer.
        if (errorPayload?.invariantCode === "INV-152") {
          setConflictMessage(
            "Esta reserva tiene servicios de más de un operador. Por ahora la cancelación de reservas con varios operadores no está disponible desde acá. Gestionala manualmente o pedile ayuda a un administrador."
          );
        } else {
          setConflictMessage(
            getApiErrorMessage(error, "No se pudo iniciar la cancelacion. Recarga la pagina y volvé a intentar.")
          );
        }
      } else {
        showError(getApiErrorMessage(error, "No se pudo iniciar la cancelacion."));
      }
    } finally {
      setDraftingLoading(false);
    }
  };

  // ─── Descartar el borrador (abort) ────────────────────────────────────────

  const handleAbort = async () => {
    if (!draft) {
      onClose();
      return;
    }
    try {
      await cancellationsApi.abort(draft.publicId, "Cancelado por el agente desde el modal.");
    } catch {
      // Si el abort falla, cerramos igual. El draft queda en la BD pero es inocuo.
    }
    onClose();
  };

  // ─── PASO 2: confirmar la cancelacion ─────────────────────────────────────

  const handleConfirm = async () => {
    setConfirmingLoading(true);
    setConflictMessage(null);

    // Construimos el payload de clasificacion de penalidad segun la opcion elegida.
    // Los parametros se pasan explicitamente (la funcion es pura, no cierra sobre estado).
    const penaltyClassification = buildPenaltyClassificationPayload(
      selectedPenaltyOption,
      agencyConceptKind,
      agencyPenaltyStatus,
      agencyPenaltyAmount
    );

    // El snapshot fiscal viene de /afip/settings.
    // Si no cargaron los settings, la funcion usa fallbacks conservadores.
    const snapshotData = buildSnapshotData(afipSettings);

    const payload = {
      snapshotData,
      isAdminOverride: false,
      overrideReason: null,
      approvalRequestPublicId: null,
      ...penaltyClassification,
    };

    try {
      await cancellationsApi.confirm(draft.publicId, payload);

      const successMessage = buildSuccessMessage();
      showSuccess(successMessage, "Cancelacion iniciada");
      onCancelled();
    } catch (error) {
      const errorPayload = error?.payload;

      if (error?.status === 409 && errorPayload?.requiresApproval) {
        // Abre el modal de solicitud de aprobacion (patron 4-eyes, igual que InvoicesTab).
        setApprovalContext({
          requestType: errorPayload.requestType,
          entityType: errorPayload.entityType,
          entityId: errorPayload.entityId,
          entityLabel: `Cancelacion Reserva #${reserva.numeroReserva}`,
        });
        setConfirmingLoading(false);
        return;
      }

      if (error?.status === 409) {
        const code = errorPayload?.code || "";
        let humanMessage;

        if (code === "CONCURRENT_EDIT") {
          humanMessage = "Otro usuario modifico esta cancelacion al mismo tiempo. Recarga la pagina y volvé a intentar.";
        } else if (
          errorPayload?.invariantCode === "INV-ADR014-PERM" ||
          errorPayload?.invariantCode === "INV-ADR013-PERM"
        ) {
          humanMessage = "No tenes permiso para clasificar un cargo propio de la agencia. Habla con un administrador.";
        } else if (
          errorPayload?.invariantCode === "INV-ADR014-003" ||
          // Reclasificacion bloqueada porque la ND ya esta emitida.
          errorPayload?.message?.includes("ya esta en juego")
        ) {
          humanMessage = "Esta accion ya se proceso. Si hay un problema, habla con administracion.";
        } else {
          humanMessage = getApiErrorMessage(error, "No se pudo confirmar la cancelacion.");
        }

        setConflictMessage(humanMessage);
      } else {
        showError(getApiErrorMessage(error, "No se pudo confirmar la cancelacion."));
      }

      setConfirmingLoading(false);
    }
  };

  // ─── Nota sobre buildPenaltyClassificationPayload y buildSnapshotData ────────
  // Estas funciones fueron extraidas a lib/penaltyPayload.js para ser testeables.
  // Se importan arriba y se llaman en handleConfirm con los valores del estado local.

  /** Copy del toast de exito segun la opcion elegida (4 variantes del ux). */
  function buildSuccessMessage() {
    switch (selectedPenaltyOption) {
      case "none":
        return "Cancelacion confirmada. La nota de credito quedo en proceso en AFIP/ARCA.";
      case "operator_pass_through":
        return "Cancelacion confirmada. La nota de credito quedo en proceso. El reembolso lo gestiona el operador.";
      case "agency_charge":
        return agencyPenaltyStatus === "Confirmed"
          ? "Cancelacion confirmada. La nota de credito y el cargo de la agencia quedaron en proceso en AFIP/ARCA."
          : "Cancelacion confirmada. La nota de credito quedo en proceso. Podras confirmar el cargo de la agencia cuando el operador te informe el monto exacto.";
      case "insurance":
        return "Cancelacion confirmada. El caso paso a revision manual del back-office.";
      default:
        return "Cancelacion confirmada. La nota de credito quedo en proceso en AFIP/ARCA.";
    }
  }

  // ─── Logica de validacion del paso 2 ──────────────────────────────────────

  const agencyChargeAmountOk =
    selectedPenaltyOption !== "agency_charge" ||
    agencyPenaltyStatus !== "Confirmed" ||
    (parseFloat(agencyPenaltyAmount) > 0);

  const canConfirm = !loadingSettings && agencyChargeAmountOk;

  // ─── Opciones de penalidad visibles segun permiso y flag ──────────────────

  const visiblePenaltyOptions = PENALTY_OPTIONS.filter((opt) => {
    if (!opt.requiresAgencyPermission) return true;
    // La opcion "cargo propio" solo se muestra si el usuario tiene el permiso Y el flag esta ON.
    // Ocultamos (no deshabilitamos) para no confundir al agente sin permiso.
    return canClassifyAgencyPenalty && enableCancellationDebitNote;
  });

  // ─── Render ───────────────────────────────────────────────────────────────

  return (
    <>
      <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
        <div className="w-full max-w-lg rounded-2xl border bg-white dark:bg-slate-900 shadow-2xl max-h-[90vh] overflow-y-auto">

          {/* ── Header del modal ── */}
          <div className="px-6 py-4 border-b border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-900/80 flex items-center justify-between">
            <div className="flex items-center gap-3">
              <div className="rounded-lg bg-rose-100 p-2 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300">
                <Ban className="h-5 w-5" />
              </div>
              <div>
                <h2 className="text-lg font-bold text-slate-900 dark:text-white">Cancelar reserva</h2>
                <p className="text-xs text-slate-500 dark:text-slate-400">
                  #{reserva.numeroReserva} — {reserva.customerName}
                  {step === "confirm" && <span className="ml-2 text-indigo-500 font-semibold">· Paso 2 de 2</span>}
                </p>
              </div>
            </div>
            {/* "Volver" en lugar de "Cancelar" para no confundir con "Cancelar reserva" (regla del glosario). */}
            <button
              type="button"
              onClick={step === "confirm" ? handleAbort : onClose}
              disabled={draftingLoading || confirmingLoading}
              className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 disabled:opacity-40"
              title="Volver sin cancelar la reserva"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          {/* ── Cuerpo del modal ── */}
          <div className="p-6 space-y-5">

            {/* Error de conflicto 409 */}
            {conflictMessage && (
              <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2">
                <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />
                <span>{conflictMessage}</span>
              </div>
            )}

            {step === "draft" && (
              <StepDraft
                reason={reason}
                onReasonChange={setReason}
                isLoading={draftingLoading}
              />
            )}

            {step === "confirm" && (
              <StepConfirm
                draft={draft}
                afipSettings={afipSettings}
                loadingSettings={loadingSettings}
                settingsError={settingsError}
                selectedPenaltyOption={selectedPenaltyOption}
                onPenaltyOptionChange={(value) => {
                  setSelectedPenaltyOption(value);
                  setConflictMessage(null);
                }}
                visiblePenaltyOptions={visiblePenaltyOptions}
                agencyConceptKind={agencyConceptKind}
                onAgencyConceptKindChange={setAgencyConceptKind}
                agencyPenaltyStatus={agencyPenaltyStatus}
                onAgencyPenaltyStatusChange={setAgencyPenaltyStatus}
                agencyPenaltyAmount={agencyPenaltyAmount}
                onAgencyPenaltyAmountChange={setAgencyPenaltyAmount}
              />
            )}
          </div>

          {/* ── Footer con acciones ── */}
          <div className="px-6 py-4 border-t border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-900/80 flex flex-col-reverse sm:flex-row sm:justify-end gap-2">
            {step === "draft" && (
              <>
                <button
                  type="button"
                  onClick={onClose}
                  disabled={draftingLoading}
                  className="px-4 py-2.5 rounded-xl text-sm font-bold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors disabled:opacity-50"
                >
                  Volver
                </button>
                <button
                  type="button"
                  onClick={handleDraft}
                  disabled={draftingLoading || reason.trim().length < 10}
                  className="px-4 py-2.5 rounded-xl text-sm font-bold text-white bg-rose-600 hover:bg-rose-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                >
                  {draftingLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                  Continuar
                </button>
              </>
            )}

            {step === "confirm" && (
              <>
                {/* "Descartar cancelacion" = abortar el draft (no se cancelara nada). */}
                <button
                  type="button"
                  onClick={handleAbort}
                  disabled={confirmingLoading}
                  className="px-4 py-2.5 rounded-xl text-sm font-bold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors disabled:opacity-50"
                >
                  Descartar cancelacion
                </button>
                <button
                  type="button"
                  onClick={handleConfirm}
                  disabled={confirmingLoading || !canConfirm}
                  data-testid="cancel-reserva-confirm-btn"
                  className={`px-4 py-2.5 rounded-xl text-sm font-bold text-white transition-colors disabled:opacity-50 flex items-center gap-2 ${
                    selectedPenaltyOption === "agency_charge" && agencyPenaltyStatus === "Confirmed"
                      ? "bg-orange-600 hover:bg-orange-700"
                      : "bg-rose-600 hover:bg-rose-700"
                  }`}
                >
                  {confirmingLoading ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                  {selectedPenaltyOption === "agency_charge" && agencyPenaltyStatus === "Confirmed"
                    ? "Confirmar y emitir cargo"
                    : "Confirmar cancelacion"
                  }
                </button>
              </>
            )}
          </div>
        </div>
      </div>

      {/* Modal de solicitud de aprobacion 4-eyes.
          Se monta fuera del modal principal para evitar z-index anidado.
          onCreated cierra el modal de approval y muestra mensaje al agente. */}
      <RequestApprovalModal
        isOpen={Boolean(approvalContext)}
        onClose={() => setApprovalContext(null)}
        onCreated={() => {
          setApprovalContext(null);
          showSuccess(
            "Solicitud enviada al administrador. La cancelacion quedara pendiente hasta que la autorice.",
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

// ============================================================================
// Sub-componente: Paso 1 — Motivo
// ============================================================================

/**
 * Formulario del primer paso: ingreso del motivo de la cancelacion.
 * Banner azul informativo: "todavia no se cancela nada".
 */
function StepDraft({ reason, onReasonChange, isLoading }) {
  const reasonTrimmed = reason.trim();
  const charsLeft = 1000 - reason.length;
  const tooShort = reason.length > 0 && reasonTrimmed.length < 10;

  return (
    <div className="space-y-4">
      {/* Banner informativo: el agente todavia puede arrepentirse. */}
      <div className="rounded-lg border border-sky-200 bg-sky-50 p-4 text-sm text-sky-800 dark:bg-sky-950/30 dark:border-sky-800 dark:text-sky-200 flex items-start gap-2">
        <Info className="h-4 w-4 flex-shrink-0 mt-0.5" />
        <div>
          <strong className="font-bold">Todavia no se cancela nada.</strong>
          {" "}En este paso solo registras el motivo. En el siguiente paso vas a confirmar los datos fiscales y recien ahi se procesa la cancelacion.
        </div>
      </div>

      <div>
        <label className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5" htmlFor="cancel-reason">
          Motivo de la cancelacion <span className="text-rose-500">*</span>
        </label>
        <textarea
          id="cancel-reason"
          value={reason}
          onChange={(e) => onReasonChange(e.target.value)}
          rows={4}
          maxLength={1000}
          disabled={isLoading}
          placeholder="Por ejemplo: el cliente cambio de planes por motivos personales..."
          data-testid="cancel-reason-textarea"
          className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-rose-400 disabled:opacity-50"
        />
        <div className="mt-1 flex justify-between text-xs text-slate-400">
          {tooShort ? (
            <span className="text-rose-600 font-semibold">Minimo 10 caracteres.</span>
          ) : (
            <span />
          )}
          <span>{charsLeft} restantes</span>
        </div>
      </div>
    </div>
  );
}

// ============================================================================
// Sub-componente: Paso 2 — Confirmar con datos fiscales y pregunta de penalidad
// ============================================================================

/**
 * Formulario del segundo paso: confirmacion con snapshot fiscal y eleccion de penalidad.
 * Los datos fiscales se muestran como read-only (vienen de /afip/settings).
 * La pregunta de penalidad tiene 4 opciones en lenguaje de agencia.
 */
function StepConfirm({
  draft,
  afipSettings,
  loadingSettings,
  settingsError,
  selectedPenaltyOption,
  onPenaltyOptionChange,
  visiblePenaltyOptions,
  agencyConceptKind,
  onAgencyConceptKindChange,
  agencyPenaltyStatus,
  onAgencyPenaltyStatusChange,
  agencyPenaltyAmount,
  onAgencyPenaltyAmountChange,
}) {
  const isAgencyCharge = selectedPenaltyOption === "agency_charge";
  const isConfirmedAmount = agencyPenaltyStatus === "Confirmed";

  return (
    <div className="space-y-5">
      {/* Banner de confirmacion: desde este punto si se cancela. */}
      <div className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2">
        <AlertCircle className="h-4 w-4 flex-shrink-0 mt-0.5" />
        <div>
          <strong className="font-bold">Al confirmar se emite la nota de credito en AFIP/ARCA</strong> (puede tardar unos minutos).
          La nota de credito le devuelve/revierte la venta al cliente.
        </div>
      </div>

      {/* Datos fiscales del snapshot (read-only) */}
      <div>
        <div className="text-xs font-bold uppercase tracking-wider text-slate-500 mb-2">Datos fiscales del comprobante</div>
        {loadingSettings ? (
          <div className="flex items-center gap-2 text-sm text-slate-500 py-2">
            <Loader2 className="h-4 w-4 animate-spin" />
            Cargando datos fiscales...
          </div>
        ) : (
          <div className="rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/50 divide-y divide-slate-100 dark:divide-slate-700 text-sm">
            {settingsError && (
              <div className="px-4 py-2 text-amber-700 dark:text-amber-300 text-xs">{settingsError}</div>
            )}
            <div className="px-4 py-2.5 flex justify-between">
              <span className="text-slate-500">Condicion fiscal agencia</span>
              <span className="font-semibold text-slate-800 dark:text-slate-100">{afipSettings?.taxCondition || "—"}</span>
            </div>
            <div className="px-4 py-2.5 flex justify-between">
              <span className="text-slate-500">Moneda</span>
              <span className="font-semibold text-slate-800 dark:text-slate-100">Pesos (ARS)</span>
            </div>
            <div className="px-4 py-2.5 flex justify-between">
              <span className="text-slate-500">Factura origen</span>
              <span className="font-semibold text-slate-800 dark:text-slate-100">
                {draft?.originatingInvoicePublicId ? "vinculada" : "—"}
              </span>
            </div>
          </div>
        )}
      </div>

      {/* Pregunta de la penalidad */}
      <div>
        <div className="text-xs font-bold uppercase tracking-wider text-slate-500 mb-2">
          ¿Que pasa con la penalidad?
        </div>
        <div className="space-y-2">
          {visiblePenaltyOptions.map((option) => (
            <label
              key={option.value}
              className={`flex items-start gap-3 rounded-xl border p-3.5 cursor-pointer transition-all ${
                selectedPenaltyOption === option.value
                  ? "border-indigo-400 bg-indigo-50 dark:border-indigo-600 dark:bg-indigo-950/30"
                  : "border-slate-200 bg-white hover:border-slate-300 dark:border-slate-700 dark:bg-slate-900/50"
              }`}
            >
              <input
                type="radio"
                name="penalty-option"
                value={option.value}
                checked={selectedPenaltyOption === option.value}
                onChange={() => onPenaltyOptionChange(option.value)}
                className="mt-0.5 accent-indigo-600"
                data-testid={`penalty-option-${option.value}`}
              />
              <div>
                <div className="text-sm font-semibold text-slate-900 dark:text-white">
                  {option.label}
                  {option.isDefault && (
                    <span className="ml-2 text-[10px] font-bold uppercase tracking-wider text-slate-400">
                      recomendado
                    </span>
                  )}
                </div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">{option.description}</div>
              </div>
            </label>
          ))}
        </div>
      </div>

      {/* Sub-bloque del cargo propio de la agencia (solo si eligio esa opcion) */}
      {isAgencyCharge && (
        <AgencyChargeSubForm
          agencyConceptKind={agencyConceptKind}
          onAgencyConceptKindChange={onAgencyConceptKindChange}
          agencyPenaltyStatus={agencyPenaltyStatus}
          onAgencyPenaltyStatusChange={onAgencyPenaltyStatusChange}
          agencyPenaltyAmount={agencyPenaltyAmount}
          onAgencyPenaltyAmountChange={onAgencyPenaltyAmountChange}
        />
      )}

      {/* Banner amber: el reembolso no es inmediato — lo paga el operador. */}
      <div className="rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-xs text-amber-800 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200 flex items-start gap-2">
        <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" />
        <div>
          <strong>El reembolso al cliente lo gestiona el operador.</strong>
          {" "}La nota de credito revierte el comprobante fiscal, pero la devolucion de la plata depende de lo que el operador te transfiera. Podras registrar ese ingreso en la bandeja de operadores.
        </div>
      </div>
    </div>
  );
}

// ============================================================================
// Sub-componente: formulario del cargo propio de la agencia
// ============================================================================

/**
 * Sub-formulario que aparece cuando el agente elige "La agencia le cobra un cargo".
 * Permite especificar: tipo (Cargo de gestion / Cargo de cancelacion),
 * estado del monto (estimado / confirmado) y monto si esta confirmado.
 */
function AgencyChargeSubForm({
  agencyConceptKind,
  onAgencyConceptKindChange,
  agencyPenaltyStatus,
  onAgencyPenaltyStatusChange,
  agencyPenaltyAmount,
  onAgencyPenaltyAmountChange,
}) {
  const isConfirmed = agencyPenaltyStatus === "Confirmed";
  const amountValue = parseFloat(agencyPenaltyAmount) || 0;
  const amountInvalid = isConfirmed && amountValue <= 0;

  return (
    <div className="rounded-xl border border-orange-200 bg-orange-50 dark:border-orange-800 dark:bg-orange-950/20 p-4 space-y-4">
      <div className="text-xs font-bold uppercase tracking-wider text-orange-700 dark:text-orange-300">
        Detalle del cargo de la agencia
      </div>

      {/* Tipo de concepto: Cargo de gestion o Cargo de cancelacion */}
      <div>
        <label className="block text-xs font-semibold text-slate-600 dark:text-slate-300 mb-1.5">
          Tipo de cargo
        </label>
        <div className="flex gap-3">
          {AGENCY_CONCEPT_OPTIONS.map((opt) => (
            <label key={opt.value} className="flex items-center gap-1.5 text-sm cursor-pointer">
              <input
                type="radio"
                name="agency-concept"
                value={opt.value}
                checked={agencyConceptKind === opt.value}
                onChange={() => onAgencyConceptKindChange(opt.value)}
                className="accent-orange-600"
              />
              <span className="text-slate-700 dark:text-slate-200">{opt.label}</span>
            </label>
          ))}
        </div>
      </div>

      {/* Estado del monto: estimado (no emite ahora) o confirmado (emite ND) */}
      <div>
        <label className="block text-xs font-semibold text-slate-600 dark:text-slate-300 mb-1.5">
          ¿Ya sabe el monto exacto?
        </label>
        <div className="space-y-2">
          {PENALTY_STATUS_OPTIONS.map((opt) => (
            <label
              key={opt.value}
              className={`flex items-start gap-2.5 rounded-lg border p-3 cursor-pointer transition-all text-sm ${
                agencyPenaltyStatus === opt.value
                  ? "border-orange-400 bg-orange-100 dark:border-orange-600 dark:bg-orange-950/40"
                  : "border-slate-200 bg-white hover:border-slate-300 dark:border-slate-700 dark:bg-slate-900/50"
              }`}
            >
              <input
                type="radio"
                name="penalty-status"
                value={opt.value}
                checked={agencyPenaltyStatus === opt.value}
                onChange={() => onAgencyPenaltyStatusChange(opt.value)}
                className="mt-0.5 accent-orange-600"
                data-testid={`penalty-status-${opt.value.toLowerCase()}`}
              />
              <div>
                <div className="font-semibold text-slate-800 dark:text-slate-100">{opt.label}</div>
                <div className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">{opt.description}</div>
              </div>
            </label>
          ))}
        </div>
      </div>

      {/* Monto: solo se pide si el agente eligio "ya confirmado" */}
      {isConfirmed && (
        <div>
          <label className="block text-xs font-semibold text-slate-600 dark:text-slate-300 mb-1.5" htmlFor="agency-amount">
            Monto del cargo (ARS) <span className="text-rose-500">*</span>
          </label>
          <input
            id="agency-amount"
            type="number"
            min="0.01"
            step="0.01"
            value={agencyPenaltyAmount}
            onChange={(e) => onAgencyPenaltyAmountChange(e.target.value)}
            placeholder="0.00"
            data-testid="agency-penalty-amount"
            className={`w-full rounded-lg border px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-orange-400 dark:bg-slate-800 dark:text-white ${
              amountInvalid ? "border-rose-400" : "border-slate-300 dark:border-slate-600"
            }`}
          />
          {amountInvalid && (
            <div className="mt-1 text-xs text-rose-600">El monto debe ser mayor a cero.</div>
          )}

          {/* Advertencia importante: una vez emitida la ND no se puede deshacer. */}
          <div className="mt-2 rounded-lg border border-rose-200 bg-rose-50 dark:bg-rose-950/30 dark:border-rose-800 px-3 py-2 text-xs text-rose-700 dark:text-rose-300 flex items-start gap-1.5">
            <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0 mt-0.5" />
            <span>
              Al confirmar se emite el cargo de la agencia (nota de debito) en AFIP/ARCA.
              Esta accion no se puede deshacer. Solo confirma si ya tenes el aval del operador.
            </span>
          </div>
        </div>
      )}
    </div>
  );
}
