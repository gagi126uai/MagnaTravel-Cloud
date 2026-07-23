import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, Clock3, Eye, Loader2, RefreshCw, Send } from "lucide-react";
import ConfirmModal from "../../../components/ConfirmModal";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors.js";
import { formatDate } from "../../../lib/utils";
import { cancellationsApi } from "../api/cancellationsApi";
import { T5ResolverLegacyList } from "./T5ResolverLegacyList";
import {
  T5_STATE,
  getActiveSaleInvoices,
  getLatestPartialCreditNote,
  resolvePartialCreditNoteEmissionState,
  t5ErrorMessage,
} from "../lib/partialCreditNoteEmissionLogic";

const money = (amount, currency = "ARS") => new Intl.NumberFormat("es-AR", {
  style: "currency",
  currency: currency || "ARS",
  maximumFractionDigits: 2,
}).format(Number(amount || 0));

// fix 2026-07-22 (barrida del bug "fechas corridas un día"): antes esto convertía a hora
// local del navegador con toLocaleDateString("es-AR") sin fijar zona horaria — el plazo
// RG 4540 (rg4540DeadlineAt más abajo) podía mostrar un día distinto al real según dónde
// esté el navegador/servidor. formatDate() central fija Argentina explícito. Ver lib/utils.js.
const date = (value) => value ? formatDate(value) : "—";

export function PartialCreditNoteEmissionPanel({ reserva, canEmit, onChanged }) {
  const reservaPublicId = reserva?.publicId;
  const [cancellation, setCancellation] = useState(null);
  const [loading, setLoading] = useState(true);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [guardMessage, setGuardMessage] = useState("");
  const [sending, setSending] = useState(false);
  // Spec T9 (2026-07-20): "Ver PDF" no tenía loading propio, a diferencia de "Enviar al cliente".
  // Lo agregamos para evitar doble clic mientras se pide el blob del PDF.
  const [pdfLoading, setPdfLoading] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const activeSaleInvoices = useMemo(() => getActiveSaleInvoices(reserva?.invoices), [reserva?.invoices]);

  const refresh = useCallback(async ({ silent = false } = {}) => {
    if (!reservaPublicId) return;
    if (!silent) setLoading(true);
    try {
      const current = await cancellationsApi.getByReserva(reservaPublicId);
      setCancellation(current?.partialCreditNoteEmission ? current : null);
    } catch (error) {
      if (error?.status === 404) setCancellation(null);
      else if (!silent) setGuardMessage("No se pudo consultar la devolución. Reintentá.");
    } finally {
      if (!silent) setLoading(false);
    }
  }, [reservaPublicId]);

  useEffect(() => {
    setDismissed(false);
    // Al cambiar de reserva, el cartel de error de la reserva anterior no
    // debe quedar pegado en la nueva.
    setGuardMessage("");
    refresh();
  }, [refresh]);

  const state = resolvePartialCreditNoteEmissionState(cancellation);
  const summary = cancellation?.partialCreditNoteEmission;
  const creditNote = getLatestPartialCreditNote(cancellation);

  useEffect(() => {
    if (state !== T5_STATE.PROCESSING) return undefined;
    const intervalId = window.setInterval(() => refresh({ silent: true }), 5000);
    return () => window.clearInterval(intervalId);
  }, [state, refresh]);

  const emit = async () => {
    setSubmitting(true);
    setGuardMessage("");
    try {
      const updated = await cancellationsApi.emitPartialCreditNote(cancellation.publicId);
      setCancellation(updated);
      setConfirmOpen(false);
      onChanged?.();
    } catch (error) {
      setGuardMessage(t5ErrorMessage(error));
      setConfirmOpen(false);
      await refresh({ silent: true });
    } finally {
      setSubmitting(false);
    }
  };

  const pdf = async () => {
    if (!creditNote?.publicId) return;
    // Spec T9 (2026-07-20): el error va al cartel `guardMessage` de la ficha, nunca a un toast —
    // un toast se pierde justo cuando el vendedor necesita leer con calma qué pasó y reintentar.
    setPdfLoading(true);
    setGuardMessage("");
    try {
      const response = await api.get(`/invoices/${creditNote.publicId}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
      window.setTimeout(() => window.URL.revokeObjectURL(url), 60000);
    } catch (error) {
      setGuardMessage(getApiErrorMessage(
        error,
        "No pudimos abrir la nota de crédito. Volvé a intentarlo apretando \"Ver PDF\" de nuevo.",
      ));
    } finally {
      setPdfLoading(false);
    }
  };

  const send = async () => {
    if (!creditNote?.publicId || !reserva?.customerPublicId) {
      // Mismo cartel que el resto de esta ficha (antes era toast) — coherencia: nada de toast acá,
      // ni para este pre-chequeo ni para el error real de más abajo.
      setGuardMessage("La reserva no tiene un cliente con contacto para enviar la devolución.");
      return;
    }
    setSending(true);
    setGuardMessage("");
    try {
      await cancellationsApi.sendPartialCreditNote(cancellation.publicId);
      // El toast de ÉXITO sí es el patrón normal de la app; la regla "nada de toast" es
      // específicamente para errores en fichas en línea, no para confirmaciones de éxito.
      showSuccess("Nota de crédito enviada al cliente.");
    } catch (error) {
      setGuardMessage(getApiErrorMessage(
        error,
        "No pudimos enviar la nota de crédito. Volvé a intentarlo apretando \"Enviar al cliente\" de nuevo.",
      ));
    } finally {
      setSending(false);
    }
  };

  const deadlineText = useMemo(() => {
    if (!summary) return "";
    if (summary.rg4540DeadlinePassed) return `El plazo informativo de 15 días venció el ${date(summary.rg4540DeadlineAt)}. Esto no impide emitir.`;
    const days = Number(summary.rg4540DaysRemaining ?? 0);
    return `Quedan ${days} ${days === 1 ? "día" : "días"} para el plazo informativo de ARCA (${date(summary.rg4540DeadlineAt)}).`;
  }, [summary]);

  if (loading || !cancellation || dismissed) return null;

  const panelClass = state === T5_STATE.SUCCEEDED
    ? "border-emerald-200 bg-emerald-50 dark:border-emerald-900/50 dark:bg-emerald-950/20"
    : state === T5_STATE.FAILED
      ? "border-rose-200 bg-rose-50 dark:border-rose-900/50 dark:bg-rose-950/20"
      : "border-amber-200 bg-amber-50 dark:border-amber-900/50 dark:bg-amber-950/20";

  return (
    <section className={`rounded-2xl border p-5 ${panelClass}`} data-testid={`t5-panel-${state}`} aria-label="Devolución por servicio anulado">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="space-y-2">
          <div className="flex items-center gap-2">
            {state === T5_STATE.SUCCEEDED ? <CheckCircle2 className="h-5 w-5 text-emerald-600" /> : state === T5_STATE.PROCESSING ? <Loader2 className="h-5 w-5 animate-spin text-amber-600" /> : <AlertTriangle className="h-5 w-5 text-amber-600" />}
            <h3 className="font-black text-slate-900 dark:text-white">DEVOLUCIÓN · SERVICIO ANULADO</h3>
          </div>

          {state === T5_STATE.SUCCEEDED ? (
            <p className="text-sm font-semibold text-emerald-800 dark:text-emerald-200">Nota de crédito emitida{creditNote?.numeroComprobante ? ` · ${creditNote.numeroComprobante}` : ""}.</p>
          ) : state === T5_STATE.PROCESSING ? (
            <p className="text-sm text-amber-800 dark:text-amber-200">Emitiendo la nota de crédito en ARCA. Podés seguir usando la reserva; este estado se actualiza solo.</p>
          ) : state === T5_STATE.FAILED ? (
            <p className="text-sm text-rose-800 dark:text-rose-200">ARCA rechazó la devolución. {creditNote?.arcaErrorMessage || "Revisá los datos e intentá nuevamente."}</p>
          ) : state === T5_STATE.BLOCKED ? (
            // Con la lista de renglones (spec 2026-07-17), el mensaje genérico de "trabado" solo hace
            // falta cuando el bloqueo NO es "hay que resolver factura/monto de servicios viejos" — es
            // decir, cuando falta la firma del contador (RI) o cuando por algún motivo el backend no
            // mandó ningún renglón (defensivo: no debería pasar, pero mejor avisar que quedar en blanco).
            summary?.requiresAccountantSignoffForRi ? (
              <p className="text-sm text-amber-800 dark:text-amber-200">La devolución necesita la firma de un contador antes de emitirse.</p>
            ) : !(summary?.lines?.length > 0) ? (
              <p className="text-sm text-amber-800 dark:text-amber-200">Falta elegir o validar la factura correspondiente. Volvé a cancelar el servicio seleccionando una factura, o pedí revisión de este caso anterior.</p>
            ) : null
          ) : (
            <p className="text-sm text-slate-700 dark:text-slate-300">Falta confirmar y emitir la devolución.</p>
          )}

          {/* El resumen "Monto / Factura / Saldo antes / TC" es de UNA sola factura destino (campos
              legacy del backend, mantenidos por compatibilidad). Con la lista de renglones nueva
              (2+ servicios, potencialmente en monedas distintas) NO se muestra acá: cada fila de la
              lista ya lleva su propia factura y su propio monto — mezclarlos en un resumen único
              violaría la regla dura "pesos y dólares nunca se suman" (2026-06-09). */}
          {state !== T5_STATE.SUCCEEDED && !(state === T5_STATE.BLOCKED && summary?.lines?.length > 0) && (
            <div className="grid gap-x-8 gap-y-1 text-sm sm:grid-cols-2">
              <p><span className="text-slate-500">Monto:</span> <strong>{money(summary?.amountToCredit, summary?.targetInvoiceCurrency)}</strong></p>
              <p><span className="text-slate-500">Factura:</span> <strong>{summary?.targetInvoiceLabel || "Pendiente de resolver"}</strong></p>
              <p><span className="text-slate-500">Saldo antes:</span> <strong>{summary?.remainingBeforeThisEmission == null ? "—" : money(summary.remainingBeforeThisEmission, summary.targetInvoiceCurrency)}</strong></p>
              {summary?.targetInvoiceExchangeRate ? <p><span className="text-slate-500">Tipo de cambio:</span> <strong>{summary.targetInvoiceExchangeRate} (el de la factura, no se cambia)</strong></p> : null}
            </div>
          )}

          {state !== T5_STATE.SUCCEEDED && <p className="flex items-center gap-1 text-xs text-slate-500"><Clock3 className="h-3.5 w-3.5" />{deadlineText}</p>}
          {guardMessage && <p role="alert" data-testid="t5-guard-message" className="text-sm font-semibold text-rose-700 dark:text-rose-300">{guardMessage}</p>}
          {state === T5_STATE.BLOCKED && !summary?.requiresAccountantSignoffForRi && summary?.lines?.length > 0 && (
            <T5ResolverLegacyList
              cancellationPublicId={cancellation.publicId}
              lines={summary.lines}
              activeSaleInvoices={activeSaleInvoices}
              canEmit={canEmit}
              refresh={refresh}
              onChanged={onChanged}
            />
          )}
        </div>

        <div className="flex shrink-0 flex-wrap gap-2">
          {state === T5_STATE.SUCCEEDED ? (
            <>
              <button type="button" onClick={pdf} disabled={pdfLoading || !creditNote?.publicId} className="inline-flex items-center gap-2 rounded-lg border border-emerald-300 px-3 py-2 text-sm font-bold text-emerald-800 disabled:opacity-50"><Eye className="h-4 w-4" />Ver PDF</button>
              <button type="button" onClick={send} disabled={sending || !creditNote?.publicId} className="inline-flex items-center gap-2 rounded-lg bg-emerald-700 px-3 py-2 text-sm font-bold text-white disabled:opacity-50"><Send className="h-4 w-4" />{sending ? "Enviando..." : "Enviar al cliente"}</button>
            </>
          ) : (state === T5_STATE.READY || state === T5_STATE.FAILED) && canEmit ? (
            <>
              <button type="button" onClick={() => setDismissed(true)} className="rounded-lg px-3 py-2 text-sm font-bold text-slate-600">Volver</button>
              <button type="button" onClick={() => setConfirmOpen(true)} data-testid={state === T5_STATE.FAILED ? "t5-retry" : "t5-emit"} className="inline-flex items-center gap-2 rounded-lg bg-amber-600 px-4 py-2 text-sm font-black text-white hover:bg-amber-700"><RefreshCw className="h-4 w-4" />{state === T5_STATE.FAILED ? "Reintentar" : "Confirmar y emitir"}</button>
            </>
          ) : state === T5_STATE.READY || state === T5_STATE.FAILED ? (
            <p className="max-w-xs text-sm text-slate-600 dark:text-slate-300">No tenés permiso para emitir; un responsable de facturación debe completar este paso.</p>
          ) : null}
        </div>
      </div>

      <ConfirmModal
        isOpen={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        onConfirm={emit}
        title="¿Seguro?"
        message="Se va a emitir la nota de crédito en AFIP por la devolución del servicio anulado. Una vez emitida no se puede deshacer."
        confirmText="Sí, emitir"
        cancelText="Volver"
        type="warning"
        isLoading={submitting}
      />
    </section>
  );
}
