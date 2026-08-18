import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, ChevronDown, Clock3, Eye, Loader2, RefreshCw, Send, XCircle } from "lucide-react";
import ConfirmModal from "../../../components/ConfirmModal";
import { Button } from "../../../components/ui/button";
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

/**
 * Texto + ícono + color de la FILA COLAPSADA (spec T5 2026-08-18, sección 1.1). Es una
 * función aparte (no JSX) para poder leer de un vistazo los 6 textos posibles sin tener
 * que buscarlos adentro del componente — cada estado tiene exactamente un renglón fijo,
 * nunca dos textos compitiendo por el mismo lugar.
 */
function resolverFilaColapsada({ state, summary, creditNote }) {
  if (state === T5_STATE.SUCCEEDED) {
    return {
      variante: "verde",
      Icono: CheckCircle2,
      texto: `Devolución emitida${creditNote?.numeroComprobante ? ` · ${creditNote.numeroComprobante}` : ""}`,
    };
  }
  if (state === T5_STATE.PROCESSING) {
    return {
      variante: "ambar",
      Icono: Loader2,
      iconoGirando: true,
      texto: "Estamos emitiendo la devolución en ARCA. En un rato tenés el resultado.",
    };
  }
  if (state === T5_STATE.FAILED) {
    return { variante: "rojo", Icono: XCircle, texto: "ARCA rechazó la devolución." };
  }
  if (state === T5_STATE.BLOCKED) {
    if (summary?.requiresAccountantSignoffForRi) {
      return { variante: "ambar", Icono: AlertTriangle, texto: "Esta devolución necesita la firma de un contador." };
    }
    const cantidadLineas = summary?.lines?.length ?? 0;
    if (cantidadLineas > 0) {
      return {
        variante: "ambar",
        Icono: AlertTriangle,
        texto: cantidadLineas === 1
          ? "Hay 1 devolución vieja por resolver antes de emitir."
          : `Hay ${cantidadLineas} devoluciones viejas por resolver antes de emitir.`,
      };
    }
    return { variante: "ambar", Icono: AlertTriangle, texto: "Falta elegir o validar la factura correspondiente." };
  }
  // T5_STATE.READY — pide confirmar y emitir.
  return {
    variante: "ambar",
    Icono: AlertTriangle,
    texto: `Tenés una devolución pendiente de ${money(summary?.amountToCredit, summary?.targetInvoiceCurrency)} al cliente.`,
  };
}

const ESTILOS_FILA_POR_VARIANTE = {
  ambar: "border-amber-200 bg-amber-50 text-amber-900 hover:bg-amber-100 dark:border-amber-900/50 dark:bg-amber-950/20 dark:text-amber-100 dark:hover:bg-amber-950/40",
  verde: "border-emerald-200 bg-emerald-50 text-emerald-900 hover:bg-emerald-100 dark:border-emerald-900/50 dark:bg-emerald-950/20 dark:text-emerald-100 dark:hover:bg-emerald-950/40",
  rojo: "border-rose-200 bg-rose-50 text-rose-900 hover:bg-rose-100 dark:border-rose-900/50 dark:bg-rose-950/20 dark:text-rose-100 dark:hover:bg-rose-950/40",
};

/**
 * Panel de devolución de un servicio CANCELADO (T5, cancelación parcial vía nota de
 * crédito). Vive arriba de la ficha de reserva, en la tira de avisos accionables — se
 * muestra solo cuando hay una cancelación con devolución pendiente/emitida/rechazada.
 *
 * Spec 2026-08-18 (T5 compacto): antes era un panel siempre abierto; ahora es una fila
 * de una sola línea (ícono + texto + flechita) que se expande INLINE al tocarla — nunca
 * ventana (P-5). Los 5 estados posibles (pendiente/procesando/emitida/falló/trabada)
 * comparten el mismo mecanismo de apertura (P1=A, P2=A: toda la fila es clickeable y
 * las acciones viven siempre detrás de abrirla).
 */
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
  // Reemplaza al viejo `dismissed` (que escondía el panel entero hasta recargar la página):
  // ahora la fila NUNCA desaparece sola (la devolución pendiente sigue viva aunque el
  // vendedor la cierre), solo se PLIEGA/DESPLIEGA. "Volver" adentro de la expansión ahora
  // hace `setOpen(false)` en vez de esconder todo el aviso.
  const [open, setOpen] = useState(false);
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
    setOpen(false);
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

  // Texto EXACTO firmado 2026-07-15 (P4=A) — corrección de texto de la spec 2026-08-18: el
  // panel decía antes "El plazo informativo... esto no impide emitir", que NO es lo que
  // Gastón firmó. Solo aplica al estado READY (la spec no lo repite en los otros 4 estados).
  const deadlineText = useMemo(() => {
    if (!summary) return "";
    if (summary.rg4540DeadlinePassed) {
      return "Pasaron más de 15 días desde que se canceló el servicio. Se puede emitir igual, pero convendría consultarlo con un contador antes de seguir.";
    }
    const days = Number(summary.rg4540DaysRemaining ?? 0);
    return `Quedan ${days} ${days === 1 ? "día" : "días"} para emitir esta devolución sin trámites extra ante ARCA (vence el ${date(summary.rg4540DeadlineAt)}).`;
  }, [summary]);

  // CINTURÓN de estado (front): en Presupuesto todavía no hubo ninguna cancelación formal de
  // servicio en el sentido de T5 — el gate de fondo (que esta devolución ni exista mientras la
  // reserva sea Presupuesto) lo pone el backend en otra tanda. Esto es solo una segunda barrera
  // acá, por si algún dato viejo llegara a colar: no mostramos nada de T5 en Presupuesto.
  if (reserva?.status === "Budget") return null;

  if (loading || !cancellation) return null;

  const fila = resolverFilaColapsada({ state, summary, creditNote });
  const Icono = fila.Icono;

  return (
    <section className="rounded-[10px]" data-testid={`t5-panel-${state}`} aria-label="Devolución por servicio cancelado">
      {/* Fila colapsada — P1=A: TODA la fila es clickeable (no solo la flechita), mismo
          gesto que ya usa el resto de la app para abrir cosas en línea. */}
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-controls="t5-detalle-expandido"
        data-testid="t5-fila-colapsada"
        className={`flex h-10 w-full items-center justify-between gap-3 rounded-[10px] border px-4 text-sm font-semibold transition-colors ${ESTILOS_FILA_POR_VARIANTE[fila.variante]}`}
      >
        <span className="flex min-w-0 items-center gap-2">
          <Icono className={`h-4 w-4 shrink-0 ${fila.iconoGirando ? "animate-spin" : ""}`} aria-hidden="true" />
          <span className="truncate text-left">{fila.texto}</span>
        </span>
        <ChevronDown className={`h-4 w-4 shrink-0 transition-transform ${open ? "rotate-180" : ""}`} aria-hidden="true" />
      </button>

      {/* Expansión — P2=A: las acciones (Confirmar y emitir / Ver PDF / Enviar / Reintentar /
          lista de trabas) viven SOLO acá adentro, nunca ya visibles en la fila colapsada. */}
      {open && (
        <div
          id="t5-detalle-expandido"
          data-testid="t5-detalle-expandido"
          className={`mt-1 space-y-3 rounded-[10px] border-x border-b p-4 text-sm ${ESTILOS_FILA_POR_VARIANTE[fila.variante]}`}
        >
          {state === T5_STATE.READY && (
            <>
              {/* El resumen "Monto / Factura / Saldo antes / TC" es de UNA sola factura destino (campos
                  legacy del backend, mantenidos por compatibilidad). Con la lista de renglones nueva
                  (2+ servicios, potencialmente en monedas distintas) no aplica acá: cada fila de la
                  lista ya lleva su propia factura y su propio monto. */}
              <div className="grid gap-x-8 gap-y-1 sm:grid-cols-2">
                <p><span className="text-slate-500 dark:text-slate-400">Monto a devolver:</span> <strong>{money(summary?.amountToCredit, summary?.targetInvoiceCurrency)}</strong></p>
                <p><span className="text-slate-500 dark:text-slate-400">Factura:</span> <strong>{summary?.targetInvoiceLabel || "Pendiente de resolver"}</strong></p>
                <p><span className="text-slate-500 dark:text-slate-400">Saldo antes:</span> <strong>{summary?.remainingBeforeThisEmission == null ? "—" : money(summary.remainingBeforeThisEmission, summary.targetInvoiceCurrency)}</strong></p>
                {summary?.targetInvoiceExchangeRate ? <p><span className="text-slate-500 dark:text-slate-400">Dólar de la factura:</span> <strong>{summary.targetInvoiceExchangeRate} (el de la factura, no se cambia)</strong></p> : null}
              </div>
              <p className="flex items-center gap-1 text-xs text-slate-600 dark:text-slate-300"><Clock3 className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />{deadlineText}</p>
            </>
          )}

          {state === T5_STATE.PROCESSING && (
            <p className="text-sm">Podés seguir usando la reserva; este estado se actualiza solo.</p>
          )}

          {state === T5_STATE.SUCCEEDED && (
            <p className="text-sm">Nota de crédito emitida{creditNote?.numeroComprobante ? ` · ${creditNote.numeroComprobante}` : ""}.</p>
          )}

          {state === T5_STATE.FAILED && (
            <p className="text-sm">Motivo de ARCA: «{creditNote?.arcaErrorMessage || "Revisá los datos e intentá nuevamente."}»</p>
          )}

          {state === T5_STATE.BLOCKED && (
            summary?.requiresAccountantSignoffForRi ? (
              <p className="text-sm">Esta devolución necesita la firma de un contador antes de emitirse.</p>
            ) : summary?.lines?.length > 0 ? (
              <T5ResolverLegacyList
                cancellationPublicId={cancellation.publicId}
                lines={summary.lines}
                activeSaleInvoices={activeSaleInvoices}
                canEmit={canEmit}
                refresh={refresh}
                onChanged={onChanged}
              />
            ) : (
              <p className="text-sm">Falta elegir o validar la factura correspondiente. Volvé a cancelar el servicio seleccionando una factura, o pedí revisión de este caso anterior.</p>
            )
          )}

          {guardMessage && <p role="alert" data-testid="t5-guard-message" className="text-sm font-semibold text-rose-700 dark:text-rose-300">{guardMessage}</p>}

          <div className="flex flex-wrap justify-end gap-2">
            {state === T5_STATE.SUCCEEDED && (
              <>
                <Button type="button" variant="outline" onClick={pdf} disabled={pdfLoading || !creditNote?.publicId} className="gap-2"><Eye className="h-4 w-4" />Ver PDF</Button>
                <Button type="button" onClick={send} disabled={sending || !creditNote?.publicId} className="gap-2"><Send className="h-4 w-4" />{sending ? "Enviando..." : "Enviar al cliente"}</Button>
              </>
            )}

            {(state === T5_STATE.READY || state === T5_STATE.FAILED) && (
              canEmit ? (
                <>
                  <Button type="button" variant="ghost" onClick={() => setOpen(false)}>Volver</Button>
                  <Button type="button" onClick={() => setConfirmOpen(true)} data-testid={state === T5_STATE.FAILED ? "t5-retry" : "t5-emit"} className="gap-2"><RefreshCw className="h-4 w-4" />{state === T5_STATE.FAILED ? "Reintentar" : "Confirmar y emitir"}</Button>
                </>
              ) : (
                <p className="text-sm text-slate-600 dark:text-slate-300">No tenés permiso para emitir; un responsable de facturación debe completar este paso.</p>
              )
            )}
          </div>
        </div>
      )}

      <ConfirmModal
        isOpen={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        onConfirm={emit}
        title="¿Seguro?"
        message="Se va a emitir la nota de crédito en ARCA por la devolución del servicio cancelado. Una vez emitida no se puede deshacer."
        confirmText="Sí, emitir"
        cancelText="Volver"
        type="warning"
        isLoading={submitting}
      />
    </section>
  );
}
