/**
 * Bandeja back-office: "Cargos de cancelacion pendientes".
 *
 * ADR-013/ADR-014: muestra las cancelaciones con nota de credito emitida
 * pero sin su nota de debito (cargo de la agencia). Puede ser porque:
 *   - La penalidad quedo como "Estimada" (CASO DOMINANTE): el agente no sabia
 *     el monto al cancelar → pseudostado "EstimatedPendingConfirmation". El agente
 *     confirma el monto ahora, cuando el operador se lo informo.
 *   - La ND quedo en estado "Pending" (encolada, todavia procesando en AFIP/ARCA).
 *   - La ND fallo ("Failed") — hay un error de AFIP/ARCA que requiere atencion.
 *   - La penalidad esta confirmada pero nunca se creo la ND
 *     (pseudo-estado "ConfirmedWithoutDebitNote" del backend).
 *
 * Desde esta bandeja el usuario puede abrir el modal de confirmacion
 * diferida (ConfirmPenaltyModal) para confirmar el monto o re-disparar la emision.
 *
 * Permiso requerido: cobranzas.invoice_annul (back-office fiscal).
 * Patron visual: clon de CreditNoteReconciliationInboxPage.
 *
 * Decision §10 UX: "En proceso" es una seccion de esta pagina (no un tab
 * separado en la ficha de reserva) — default del ux-ui-travel-retail.
 * Ajustable por Gaston si se prefiere tab en la ficha.
 */

import { useState, useCallback } from "react";
import { RefreshCw, ReceiptText } from "lucide-react";
import { cancellationsApi, DEBIT_NOTE_STATUS_LABELS } from "../api/cancellationsApi";
import ConfirmPenaltyModal from "../components/ConfirmPenaltyModal";
import { useDebitNotePendingList } from "../hooks/useDebitNotePendingList";

export default function CancellationDebitNoteInboxPage() {
  const { items, loading, error, reload } = useDebitNotePendingList();

  // Estado para el modal de confirmacion diferida de la penalidad.
  // null = cerrado; objeto = { cancellationPublicId, reservaNumero }
  const [penaltyModalContext, setPenaltyModalContext] = useState(null);

  const handlePenaltyConfirmed = useCallback(() => {
    setPenaltyModalContext(null);
    // Recarga la bandeja para reflejar el nuevo estado.
    reload();
  }, [reload]);

  return (
    <>
      <div className="space-y-6">
        {/* ─ Header ─────────────────────────────────────────────────────────── */}
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-orange-100 p-2 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300">
            <ReceiptText className="h-5 w-5" />
          </div>
          <div>
            <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
              Cargos de cancelacion pendientes
            </h1>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Cancelaciones con nota de credito emitida pero con el cargo de la agencia (nota de debito)
              pendiente de confirmar el monto, en proceso, fallido, o sin emitir.
              Desde aca podés confirmar el monto o reintentar la emision.
            </p>
          </div>
        </div>

        {/* ─ Panel principal ─────────────────────────────────────────────────── */}
        <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
          {/* Toolbar */}
          <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
            <span className="text-sm font-semibold text-slate-600 dark:text-slate-300">
              {items.length > 0 ? `${items.length} caso(s)` : ""}
            </span>
            <button
              type="button"
              onClick={reload}
              disabled={loading}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
              aria-label="Refrescar bandeja"
              data-testid="refresh-debit-note-inbox"
            >
              <RefreshCw className={`h-3.5 w-3.5 ${loading ? "animate-spin" : ""}`} />
              Actualizar
            </button>
          </div>

          {/* Estados: loading / error / vacio / lista */}
          {loading ? (
            <div className="px-6 py-10 text-center text-sm text-slate-500">
              Cargando bandeja…
            </div>
          ) : error ? (
            <div className="px-6 py-10 text-center space-y-2">
              <p className="text-sm text-rose-600">No se pudo cargar la bandeja.</p>
              <button
                type="button"
                onClick={reload}
                className="text-xs text-indigo-600 hover:underline dark:text-indigo-400"
              >
                Reintentar
              </button>
            </div>
          ) : items.length === 0 ? (
            <div className="px-6 py-10 text-center text-sm text-slate-500" data-testid="empty-state">
              No hay cargos pendientes. Todas las notas de debito estan en orden.
            </div>
          ) : (
            <div className="divide-y divide-slate-100 dark:divide-slate-800">
              {items.map((row) => (
                <DebitNotePendingRow
                  key={row.bookingCancellationPublicId}
                  row={row}
                  onOpenConfirmPenalty={(cancellationPublicId, reservaNumero) =>
                    setPenaltyModalContext({ cancellationPublicId, reservaNumero })
                  }
                />
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Modal de confirmacion diferida de la penalidad.
          Vive en la pagina (no en cada fila) para evitar instanciar N modales. */}
      <ConfirmPenaltyModal
        isOpen={Boolean(penaltyModalContext)}
        cancellationPublicId={penaltyModalContext?.cancellationPublicId}
        reservaNumero={penaltyModalContext?.reservaNumero}
        onClose={() => setPenaltyModalContext(null)}
        onConfirmed={handlePenaltyConfirmed}
      />
    </>
  );
}

// ============================================================================
// Sub-componente: fila de la bandeja
// ============================================================================

/**
 * Fila de un caso en la bandeja de cargos pendientes.
 * Muestra: numero de reserva, estado de la ND, monto, fecha de cancelacion.
 *
 * El boton "Confirmar cargo" aparece en dos situaciones:
 *   1. "ConfirmedWithoutDebitNote": el agente clasifico un cargo propio al cancelar,
 *      dijo que era Confirmed, pero la ND nunca llego a crearse.
 *   2. "EstimatedPendingConfirmation": el CASO DOMINANTE — el agente dejo el monto
 *      como "Estimado" al cancelar (aun no sabia cuanto cobraba el operador).
 *      La NC ya se emitio. Ahora que el operador confirmo el monto, el agente puede
 *      entrar al ConfirmPenaltyModal para setear el monto definitivo y disparar la ND.
 */
function DebitNotePendingRow({ row, onOpenConfirmPenalty }) {
  const statusInfo = DEBIT_NOTE_STATUS_LABELS[row.debitNoteStatus] || {
    label: row.debitNoteStatus,
    color: "slate",
  };

  const colorClasses = {
    amber: "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
    emerald: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    rose: "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300",
    orange: "bg-orange-100 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300",
    slate: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400",
  };

  const statusClass = colorClasses[statusInfo.color] || colorClasses.slate;

  // El boton de confirmar se muestra en los dos pseudo-estados de la bandeja:
  //   - "ConfirmedWithoutDebitNote": penalidad confirmada pero ND nunca creada.
  //   - "EstimatedPendingConfirmation": CASO DOMINANTE — monto estimado, el agente
  //     ahora confirma el monto definitivo para disparar la ND.
  // Ambos pseudo-estados abren el mismo ConfirmPenaltyModal.
  const canConfirmPenalty =
    row.debitNoteStatus === "ConfirmedWithoutDebitNote" ||
    row.debitNoteStatus === "EstimatedPendingConfirmation";

  const formattedDate = row.confirmedAt
    ? new Date(row.confirmedAt).toLocaleDateString("es-AR", {
        day: "2-digit",
        month: "2-digit",
        year: "numeric",
      })
    : "—";

  const formattedAmount =
    row.penaltyAmount != null
      ? Number(row.penaltyAmount).toLocaleString("es-AR", {
          style: "currency",
          currency: row.penaltyCurrency || "ARS",
        })
      : "—";

  return (
    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 px-6 py-4">
      <div className="space-y-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-semibold text-slate-900 dark:text-white text-sm">
            Reserva #{row.reservaNumero}
          </span>
          <span className={`rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider ${statusClass}`}>
            {statusInfo.label}
          </span>
        </div>
        <div className="text-xs text-slate-500 dark:text-slate-400 flex flex-wrap gap-x-4 gap-y-0.5">
          <span>Monto: <strong>{formattedAmount}</strong></span>
          <span>Cancelacion: <strong>{formattedDate}</strong></span>
          {row.arcaErrorMessage && (
            <span className="text-rose-600 dark:text-rose-400">Error: {row.arcaErrorMessage}</span>
          )}
        </div>
      </div>

      {/* Accion: para filas con pseudo-estado que requieren confirmacion diferida.
          El label del boton cambia segun el estado para guiar al agente:
          - "EstimatedPendingConfirmation": el agente tiene que confirmar el MONTO (caso dominante).
          - "ConfirmedWithoutDebitNote": el agente tiene que re-emitir la ND (la penalidad ya era Confirmed). */}
      {canConfirmPenalty && (
        <button
          type="button"
          onClick={() => onOpenConfirmPenalty(row.bookingCancellationPublicId, row.reservaNumero)}
          data-testid={`confirm-penalty-row-${row.bookingCancellationPublicId}`}
          className="flex-shrink-0 rounded-xl border border-orange-300 bg-orange-50 px-3 py-2 text-xs font-bold text-orange-700 hover:bg-orange-100 dark:border-orange-700 dark:bg-orange-950/30 dark:text-orange-300 dark:hover:bg-orange-900/40 transition-colors"
        >
          {row.debitNoteStatus === "EstimatedPendingConfirmation"
            ? "Confirmar monto del operador"
            : "Confirmar cargo de la agencia"}
        </button>
      )}
    </div>
  );
}
