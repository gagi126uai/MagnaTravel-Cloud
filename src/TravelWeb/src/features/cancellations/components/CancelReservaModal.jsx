/**
 * Modal de un solo paso para cancelar una reserva.
 *
 * El usuario ve solo el campo de motivo y el botón "Cancelar reserva".
 * Por detrás se hacen 2 llamadas en secuencia (draft + confirm) de forma
 * transparente. Si el draft falla, no se sigue. El usuario ve un loading.
 *
 * La clasificación de penalidad se fija siempre en "operator_pass_through"
 * (int 0 = OperatorPenaltyPassThrough): la agencia no emite ningún cargo
 * propio ni nota de débito. Solo se emite la nota de crédito en AFIP/ARCA.
 *
 * Errores 409 manejados:
 *   - INV-152 (draft): reserva con múltiples operadores → mensaje humano claro.
 *   - CONCURRENT_EDIT (confirm): alguien editó al mismo tiempo → banner.
 *   - Genérico 409: se muestra el mensaje de la API.
 *
 * Props:
 *   - reserva: objeto de la reserva (necesita publicId, numeroReserva, customerName).
 *   - isOpen: booleano de visibilidad.
 *   - onClose: callback de cierre.
 *   - onCancelled: callback luego de confirmar exitosamente.
 */

import { useState, useEffect } from "react";
import { X, AlertTriangle, Info, Loader2, Ban } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { buildPenaltyClassificationPayload } from "../lib/penaltyPayload";

export default function CancelReservaModal({
  reserva,
  isOpen,
  onClose,
  onCancelled,
}) {
  const [reason, setReason] = useState("");

  // `processing` cubre las 2 llamadas internas (draft + confirm) como un único loading.
  const [processing, setProcessing] = useState(false);

  // Mensaje de error contextual para errores 409 (conflictos, invariantes de negocio).
  // Se muestra en un banner dentro del modal, no en un toast, para que el usuario lo vea.
  const [conflictMessage, setConflictMessage] = useState(null);

  // Resetea el estado completo cuando el modal se abre de nuevo.
  // useEffect con [isOpen]: corre cada vez que cambia la visibilidad.
  useEffect(() => {
    if (!isOpen) return;
    setReason("");
    setProcessing(false);
    setConflictMessage(null);
  }, [isOpen]);

  if (!isOpen) return null;

  // ─── Lógica principal ──────────────────────────────────────────────────────

  const handleCancelar = async () => {
    const trimmedReason = reason.trim();
    if (trimmedReason.length < 10) {
      showError("El motivo debe tener al menos 10 caracteres.");
      return;
    }

    setProcessing(true);
    setConflictMessage(null);

    // PASO 1: crear el borrador (draft). Si falla acá no seguimos.
    let draft;
    try {
      draft = await cancellationsApi.draft(reserva.publicId, trimmedReason);
    } catch (error) {
      if (error?.status === 409) {
        const errorPayload = error?.payload;

        // INV-152: la reserva tiene servicios de más de un operador.
        // Caso no soportado todavía — mensaje claro para que el agente sepa qué hacer.
        if (errorPayload?.invariantCode === "INV-152") {
          setConflictMessage(
            "Esta reserva tiene servicios de más de un operador. Por ahora la cancelación de reservas con varios operadores no está disponible desde acá. Gestionala manualmente o pedile ayuda a un administrador."
          );
        } else {
          setConflictMessage(
            getApiErrorMessage(error, "No se pudo iniciar la cancelación. Recargá la página y volvé a intentar.")
          );
        }
      } else {
        showError(getApiErrorMessage(error, "No se pudo iniciar la cancelación."));
      }
      setProcessing(false);
      return;
    }

    // PASO 2: confirmar la cancelación. Emite la nota de crédito en AFIP/ARCA (async).
    //
    // Clasificación de penalidad: siempre "operator_pass_through" (int 0).
    // Esto significa: la agencia NO emite ningún cargo propio ni nota de débito.
    // Solo se emite la nota de crédito. Es la opción más neutra y segura para el mostrador.
    // Si en el futuro se reactiva el cobro de cargo propio, se pasa "agency_charge" aquí.
    const penaltyClassification = buildPenaltyClassificationPayload(
      "operator_pass_through",
      null,
      null,
      null
    );

    // Tanda B (2026-07-16): ya no armamos ni mandamos "snapshotData". El backend resuelve
    // las condiciones fiscales y el tipo de cambio SOLO, directo de la base (agencia,
    // operador, cliente y la factura original) — el campo que se mandaba antes lo
    // adivinaba el frontend y quedó IGNORADO server-side.
    const payload = {
      isAdminOverride: false,
      overrideReason: null,
      approvalRequestPublicId: null,
      ...penaltyClassification,
    };

    try {
      await cancellationsApi.confirm(draft.publicId, payload);

      // Mensaje simple sin jerga fiscal. La NC se genera en background en AFIP/ARCA.
      showSuccess("Reserva cancelada. La nota de crédito se está generando.", "Cancelación confirmada");
      onCancelled();
    } catch (error) {
      if (error?.status === 409) {
        const errorPayload = error?.payload;
        const code = errorPayload?.code || "";

        let humanMessage;
        if (code === "CONCURRENT_EDIT") {
          humanMessage = "Otro usuario modificó esta cancelación al mismo tiempo. Recargá la página y volvé a intentar.";
        } else {
          humanMessage = getApiErrorMessage(error, "No se pudo confirmar la cancelación. Volvé a intentar.");
        }

        setConflictMessage(humanMessage);
      } else {
        showError(getApiErrorMessage(error, "No se pudo confirmar la cancelación."));
      }

      setProcessing(false);
    }
  };

  // ─── Render ───────────────────────────────────────────────────────────────

  const reasonTrimmed = reason.trim();
  const charsLeft = 1000 - reason.length;
  const tooShort = reason.length > 0 && reasonTrimmed.length < 10;
  const canSubmit = !processing && reasonTrimmed.length >= 10;

  return (
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
              </p>
            </div>
          </div>
          {/* "Volver" en lugar de "Cancelar" para no confundir con "Cancelar reserva". */}
          <button
            type="button"
            onClick={onClose}
            disabled={processing}
            className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 disabled:opacity-40"
            title="Volver sin cancelar la reserva"
            aria-label="Volver sin cancelar la reserva"
          >
            <X className="h-5 w-5" />
          </button>
        </div>

        {/* ── Cuerpo del modal ── */}
        <div className="p-6 space-y-5">

          {/* Error de conflicto 409 */}
          {conflictMessage && (
            <div
              role="alert"
              className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2"
            >
              <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
              <span>{conflictMessage}</span>
            </div>
          )}

          {/* Banner informativo: el reembolso al cliente lo gestiona el operador. */}
          {/* Esto es útil para el mostrador porque a veces el cliente pregunta en el momento. */}
          <div className="rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-xs text-amber-800 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200 flex items-start gap-2">
            <Info className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
            <span>
              <strong>El reembolso al cliente lo gestiona el operador.</strong>
              {" "}Al cancelar se emite la nota de crédito en AFIP/ARCA.
            </span>
          </div>

          {/* Campo de motivo */}
          <div>
            <label
              className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
              htmlFor="cancel-reason"
            >
              Motivo de la cancelación <span className="text-rose-500" aria-hidden="true">*</span>
            </label>
            <textarea
              id="cancel-reason"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              rows={4}
              maxLength={1000}
              disabled={processing}
              placeholder="Por ejemplo: el cliente cambió de planes por motivos personales..."
              data-testid="cancel-reason-textarea"
              aria-describedby="cancel-reason-hint"
              className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-rose-400 disabled:opacity-50"
            />
            <div id="cancel-reason-hint" className="mt-1 flex justify-between text-xs text-slate-400">
              {tooShort ? (
                <span className="text-rose-600 font-semibold" role="alert">Mínimo 10 caracteres.</span>
              ) : (
                <span />
              )}
              <span>{charsLeft} restantes</span>
            </div>
          </div>
        </div>

        {/* ── Footer con acciones ── */}
        <div className="px-6 py-4 border-t border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-900/80 flex flex-col-reverse sm:flex-row sm:justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            disabled={processing}
            className="px-4 py-2.5 rounded-xl text-sm font-bold text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors disabled:opacity-50"
          >
            Volver
          </button>
          <button
            type="button"
            onClick={handleCancelar}
            disabled={!canSubmit}
            data-testid="cancel-reserva-confirm-btn"
            className="px-4 py-2.5 rounded-xl text-sm font-bold text-white bg-rose-600 hover:bg-rose-700 transition-colors disabled:opacity-50 flex items-center gap-2"
          >
            {processing && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
            {processing ? "Cancelando..." : "Cancelar reserva"}
          </button>
        </div>
      </div>
    </div>
  );
}
