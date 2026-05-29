import { useState } from "react";
import { Link } from "react-router-dom";
import { AlertTriangle, ExternalLink, Trash2 } from "lucide-react";
import { creditNoteReconciliationApi } from "../api/creditNoteReconciliationApi";
import { ReconciliationStatusPill, ReceiptStatusPill } from "./ReconciliationStatusPill";
import { showError, showSuccess, showConfirm } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";

/**
 * FC1.3 Fase 3 (ADR-010): fila expandida de un caso de reconciliacion en la bandeja.
 *
 * Muestra toda la informacion del caso: NC parcial, factura original, recibos con su
 * estado vigente, y las acciones disponibles (anular recibo, cerrar caso).
 *
 * Props:
 *   caso: PartialCreditNoteReconciliationDto — datos del caso.
 *   onResolved: () => void — callback para refrescar la lista despues de una accion.
 *   onApprovalRequired: ({ requestType, entityType, entityId }) => void — callback
 *     que la pagina padre usa para abrir el RequestApprovalModal cuando anular un recibo
 *     devuelve 409 con requiresApproval=true (workflow de cuatro ojos).
 */
export default function ReconciliationRow({ caso, onResolved, onApprovalRequired }) {
  const [notes, setNotes] = useState("");
  const [busyResolve, setBusyResolve] = useState(false);
  // Mapa de publicId de payment en proceso de anulacion para deshabilitar ese boton.
  const [voidingReceiptIds, setVoidingReceiptIds] = useState(new Set());

  // Cuenta cuantos recibos estan vigentes (Issued = vivos = sin anular).
  const liveReceipts = caso.receipts.filter((r) => r.currentStatus === "Issued");
  const hasLiveReceipts = liveReceipts.length > 0;
  const totalReceipts = caso.receipts.length;
  const voidedCount = totalReceipts - liveReceipts.length;

  // Regla R4: si hay recibos vivos al intentar cerrar, las notas son obligatorias.
  // El backend tambien lo valida — esto es solo para UX (no mandar el request si ya sabemos que falla).
  const notesRequired = hasLiveReceipts;
  const notesEmpty = notes.trim().length === 0;
  const canResolve = !busyResolve && (!notesRequired || !notesEmpty);

  const openedAtFmt = new Date(caso.openedAt).toLocaleString("es-AR");
  const resolvedAtFmt = caso.resolvedAt ? new Date(caso.resolvedAt).toLocaleString("es-AR") : null;
  const isClosed = caso.status === "Resolved";

  // ─── Anular un recibo individual ─────────────────────────────────────────────

  const handleVoidReceipt = async (paymentPublicId, receiptNumber) => {
    const confirmed = await showConfirm({
      title: "Anular comprobante de pago",
      text: `¿Anulás el comprobante ${receiptNumber || paymentPublicId}? Esta acción queda registrada en auditoría.`,
      confirmText: "Sí, anular",
      confirmColor: "red",
    });
    if (!confirmed) return;

    // Marcamos este recibo como "en proceso" para deshabilitar su boton.
    setVoidingReceiptIds((prev) => new Set([...prev, paymentPublicId]));
    try {
      await creditNoteReconciliationApi.voidReceipt(paymentPublicId);
      showSuccess("Comprobante anulado. Refrescando...");
      // Refrescamos toda la lista para ver el currentStatus actualizado.
      onResolved?.();
    } catch (err) {
      const payload = err?.payload;

      // 409 con requiresApproval=true: el rol del usuario NO tiene permiso para anular
      // directo. El backend dispara un workflow de cuatro ojos. Abrimos el modal de
      // solicitud de aprobacion en vez de mostrar un error rojo (que confundiria al
      // usuario haciendole creer que algo fallo, cuando en realidad es el flujo normal
      // para su rol). Patron identico a useFinanceActions.handleVoidReceipt.
      if (err?.status === 409 && payload?.requiresApproval) {
        onApprovalRequired?.({
          requestType: payload.requestType,    // "ReceiptVoidance" u otro que mande el backend
          entityType: payload.entityType,      // "PaymentReceipt"
          entityId: payload.entityId,
        });
        return;
      }

      // Cualquier otro error (500, 422, red caida, etc.) si va como toast rojo.
      const message = getApiErrorMessage(err, "No se pudo anular el comprobante.");
      showError(message);
    } finally {
      setVoidingReceiptIds((prev) => {
        const next = new Set(prev);
        next.delete(paymentPublicId);
        return next;
      });
    }
  };

  // ─── Cerrar el caso ───────────────────────────────────────────────────────────

  const handleResolve = async () => {
    // Validacion de cliente: si hay recibos vivos y no hay notas, avisamos antes de llamar.
    if (notesRequired && notesEmpty) {
      showError("Hay recibos sin anular. Explicá por qué cerrás el caso igual (campo de notas obligatorio).");
      return;
    }

    setBusyResolve(true);
    try {
      await creditNoteReconciliationApi.resolve(caso.publicId, notes.trim() || null);
      showSuccess("Caso marcado como resuelto.");
      onResolved?.();
    } catch (err) {
      // El backend puede devolver 409 con { message } en varios escenarios (cuatro ojos,
      // notas insuficientes, concurrencia). Mostramos el mensaje del backend directamente.
      const message = getApiErrorMessage(err, "No se pudo cerrar el caso.");
      showError(message);
    } finally {
      setBusyResolve(false);
    }
  };

  return (
    <div
      className="px-6 py-5 space-y-4"
      data-testid="reconciliation-row"
      data-case-id={caso.publicId}
    >
      {/* ─ Header del caso ─────────────────────────────────────────────────── */}
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-semibold text-slate-900 dark:text-white">
              NC {caso.creditNoteNumber}
            </span>
            <span className="text-slate-400 dark:text-slate-500 text-sm">sobre factura</span>
            <span className="font-mono text-sm text-slate-700 dark:text-slate-300">
              {caso.originalInvoiceNumber}
            </span>
            <ReconciliationStatusPill status={caso.status} />
            {/* Badge cuando se cerro con recibos vivos — avisa al equipo que fue un cierre manual sucio */}
            {caso.closedWithLiveReceipts && (
              <span
                className="inline-flex items-center gap-1 rounded-full bg-orange-100 px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider text-orange-700 dark:bg-orange-900/30 dark:text-orange-300"
                title="Se cerro este caso con recibos que todavia estaban activos"
              >
                <AlertTriangle className="h-2.5 w-2.5" />
                Cerrado con recibos vivos
              </span>
            )}
            {/* Badge cuando se salteo la regla de cuatro ojos (admin con bypass) */}
            {caso.fourEyesBypassApplied && (
              <span className="inline-flex items-center rounded-full bg-violet-100 px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider text-violet-700 dark:bg-violet-900/30 dark:text-violet-300">
                Bypass 4 ojos
              </span>
            )}
          </div>

          {/* Nombre de la reserva, linkeable si existe el publicId */}
          {caso.reservaName && (
            <div className="text-sm text-slate-600 dark:text-slate-400">
              Reserva:{" "}
              {caso.reservaPublicId ? (
                <Link
                  to={`/reservas/${caso.reservaPublicId}`}
                  className="text-indigo-600 hover:underline dark:text-indigo-400"
                  data-testid="reserva-link"
                >
                  {caso.reservaName}
                  <ExternalLink className="inline ml-1 h-3 w-3" />
                </Link>
              ) : (
                <span>{caso.reservaName}</span>
              )}
            </div>
          )}

          <div className="text-xs text-slate-500 dark:text-slate-400">
            Abierto {openedAtFmt}
            {caso.openedByUserName && (
              <> por <span className="font-medium">{caso.openedByUserName}</span></>
            )}
          </div>

          {/* Monto fiscal acreditado — informativo, NO es lo que se devuelve en caja */}
          <div className="text-xs text-slate-500 dark:text-slate-400">
            Monto NC (informativo):{" "}
            <span className="font-medium text-slate-700 dark:text-slate-300">
              {caso.currency} {Number(caso.fiscalAmountCredited ?? 0).toLocaleString("es-AR", { minimumFractionDigits: 2 })}
            </span>
            {" "}— este monto es fiscal, la devolucion real se gestiona en Caja
          </div>
        </div>
      </div>

      {/* ─ Sublista de recibos ─────────────────────────────────────────────── */}
      <div className="rounded-lg border border-slate-100 dark:border-slate-800 overflow-hidden">
        <div className="px-3 py-2 bg-slate-50 dark:bg-slate-800/50 flex items-center justify-between">
          <span className="text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">
            Comprobantes de pago
          </span>
          {/* Resumen tipo "2 de 3 recibos ya anulados" */}
          <span
            className="text-xs text-slate-500 dark:text-slate-400"
            data-testid="receipts-summary"
          >
            {voidedCount} de {totalReceipts} anulado{voidedCount !== 1 ? "s" : ""}
          </span>
        </div>
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {caso.receipts.map((recibo) => (
            <ReceiptRow
              key={recibo.receiptId}
              recibo={recibo}
              isVoiding={voidingReceiptIds.has(recibo.paymentPublicId)}
              casoClosed={isClosed}
              onVoid={() => handleVoidReceipt(recibo.paymentPublicId, recibo.receiptNumber)}
            />
          ))}
          {caso.receipts.length === 0 && (
            <div className="px-3 py-3 text-xs text-slate-400 text-center">
              No hay recibos registrados en este caso.
            </div>
          )}
        </div>
      </div>

      {/* ─ Info de cierre (solo en casos resueltos) ────────────────────────── */}
      {isClosed && (
        <div className="rounded-lg bg-emerald-50 dark:bg-emerald-900/20 px-4 py-3 space-y-1">
          <div className="text-xs font-semibold text-emerald-700 dark:text-emerald-300 uppercase tracking-wider">
            Caso cerrado
          </div>
          <div className="text-xs text-slate-600 dark:text-slate-400">
            {resolvedAtFmt}{caso.resolvedByUserName && <> por <span className="font-medium">{caso.resolvedByUserName}</span></>}
          </div>
          {caso.resolutionNotes && (
            <div className="rounded bg-white dark:bg-slate-800/60 px-3 py-2 text-sm text-slate-700 dark:text-slate-300">
              {caso.resolutionNotes}
            </div>
          )}
        </div>
      )}

      {/* ─ Accion de cierre (solo en casos pendientes) ─────────────────────── */}
      {!isClosed && (
        <div className="space-y-2">
          <label
            htmlFor={`notes-${caso.publicId}`}
            className="block text-[10px] font-semibold uppercase tracking-wider text-slate-400"
          >
            {/* Si hay recibos vivos, las notas son obligatorias (regla R4 del backend) */}
            Notas del cierre
            {notesRequired && (
              <span className="ml-1 text-rose-500" title="Obligatorio cuando hay recibos sin anular">
                * obligatorio (hay recibos vivos)
              </span>
            )}
          </label>
          <textarea
            id={`notes-${caso.publicId}`}
            value={notes}
            onChange={(event) => setNotes(event.target.value)}
            rows={2}
            placeholder={
              notesRequired
                ? "Explicá por qué cerrás el caso con recibos todavía activos…"
                : "Nota opcional para auditoría…"
            }
            className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm"
            data-testid="resolve-notes-input"
          />
          <button
            type="button"
            onClick={handleResolve}
            disabled={!canResolve}
            className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-50 disabled:cursor-not-allowed"
            aria-label="Marcar caso como resuelto"
            data-testid="resolve-button"
          >
            {busyResolve ? "Cerrando…" : "Marcar resuelto"}
          </button>
        </div>
      )}
    </div>
  );
}

/**
 * Fila individual de un recibo dentro del caso de reconciliacion.
 * Muestra numero, monto, estado vigente, y el boton de anulacion si corresponde.
 *
 * Props:
 *   recibo: PartialCreditNoteReconciliationReceiptDto
 *   isVoiding: boolean — true mientras se esta enviando la peticion de anulacion.
 *   casoClosed: boolean — si el caso ya esta resuelto, no se puede anular mas.
 *   onVoid: () => void — callback para disparar la anulacion.
 */
function ReceiptRow({ recibo, isVoiding, casoClosed, onVoid }) {
  const isLive = recibo.currentStatus === "Issued";
  const voidedAtFmt = recibo.voidedAt ? new Date(recibo.voidedAt).toLocaleString("es-AR") : null;

  return (
    <div
      className="flex flex-wrap items-center justify-between gap-2 px-3 py-2.5"
      data-testid="receipt-row"
      data-receipt-id={recibo.receiptId}
    >
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span className="font-mono text-slate-700 dark:text-slate-200">
          {recibo.receiptNumber || `Recibo #${recibo.receiptId}`}
        </span>
        <span className="text-slate-400">·</span>
        <span className="text-slate-600 dark:text-slate-300">
          {Number(recibo.amount ?? 0).toLocaleString("es-AR", { minimumFractionDigits: 2 })}
        </span>
        <ReceiptStatusPill status={recibo.currentStatus} />
        {voidedAtFmt && (
          <span className="text-xs text-slate-400 dark:text-slate-500">
            Anulado {voidedAtFmt}
            {recibo.voidedByUserName && <> por {recibo.voidedByUserName}</>}
          </span>
        )}
      </div>

      {/* Boton de anular — solo si el recibo esta vivo Y el caso no esta cerrado */}
      {isLive && !casoClosed && (
        <button
          type="button"
          onClick={onVoid}
          disabled={isVoiding}
          className="inline-flex items-center gap-1 rounded-lg border border-rose-300 px-2.5 py-1 text-xs font-semibold text-rose-600 hover:bg-rose-50 disabled:opacity-50 disabled:cursor-not-allowed dark:border-rose-700 dark:text-rose-400 dark:hover:bg-rose-900/20"
          aria-label={`Anular comprobante ${recibo.receiptNumber || recibo.receiptId}`}
          title="Anular este comprobante de pago"
          data-testid="void-receipt-button"
        >
          <Trash2 className="h-3.5 w-3.5" />
          {isVoiding ? "Anulando…" : "Anular"}
        </button>
      )}
    </div>
  );
}
