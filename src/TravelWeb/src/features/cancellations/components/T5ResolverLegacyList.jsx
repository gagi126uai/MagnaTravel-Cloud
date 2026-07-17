/**
 * Sub-estado "trabado" de T5 (spec `docs/ux/2026-07-17-t5-resolver-devoluciones-viejas.md`): lista
 * de devoluciones VIEJAS pendientes de resolver — servicios que se cancelaron ANTES de que el
 * sistema guardara a qué factura correspondía cada uno. Reemplaza al viejo formulario "a ciegas"
 * (`t5-resolver-legacy`), que solo servía para UN servicio pendiente y mezclaba montos de monedas
 * distintas (el bug real que destapó Gastón: hotel en dólares + excursión en pesos, mismo operador).
 *
 * Vive DENTRO del sub-estado BLOCKED de `PartialCreditNoteEmissionPanel`. Cada fila de la lista
 * (un servicio cancelado) avanza sola por sus propios pasos: sin resolver → resuelta → emitiendo →
 * emitida/rechazada — sin esperar a que las demás terminen.
 */

import { useEffect, useRef, useState } from "react";
import { AlertTriangle, CheckCircle2, Eye, Loader2, RefreshCw, Send } from "lucide-react";
import ConfirmModal from "../../../components/ConfirmModal";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { formatCurrency } from "../../../lib/utils";
import { getApiErrorMessage } from "../../../lib/errors";
import { cancellationsApi } from "../api/cancellationsApi";
import { t5ErrorMessage } from "../lib/partialCreditNoteEmissionLogic";
import {
  T5_ROW_STATE,
  buildEmitPayloadForLine,
  buildInvoiceHelpText,
  buildInvoicePlaceholder,
  buildEmptyCurrencyMessage,
  buildResolvePayload,
  buildResolvedRowText,
  buildResolverFormHeading,
  buildResolverLegacyHeaderText,
  canSaveResolverRow,
  filterInvoicesByCurrency,
  resolveRowSaveErrorMessage,
  resolveRowState,
  anyLineIsEmitting,
} from "../lib/t5ResolverLegacyLogic";

// Texto explicativo EXACTO de la spec §1 punto 2 — excepción puntual a "nada de aclaraciones"
// porque este es un caso raro de datos viejos, pedido explícito de Gastón (P2=A).
const TEXTO_EXPLICATIVO =
  "Estos servicios se cancelaron cuando el sistema todavía no guardaba a qué factura correspondía " +
  "cada uno. Decinos de qué factura sale cada devolución y por cuánto, y el sistema la emite.";

// Mismo texto del "¿Seguro?" que usa el caso normal (spec 2026-07-15, NO se rediseña acá): cada
// devolución resuelta se emite con este flujo, fila por fila.
const CONFIRM_EMIT_MESSAGE =
  "Se va a emitir la nota de crédito en AFIP por la devolución del servicio anulado. Una vez emitida no se puede deshacer.";

/**
 * Props:
 *   - cancellationPublicId: GUID del BookingCancellation (para los PATCH/POST de resolver/emitir).
 *   - lines: PartialCreditNoteEmissionSummaryDto.Lines — una fila por servicio cancelado pendiente.
 *   - activeSaleInvoices: facturas activas de la reserva, ya armadas por getActiveSaleInvoices
 *     (número + moneda + monto en el label). Se filtran acá por la moneda de cada fila.
 *   - canEmit: permiso cobranzas.invoice_annul — sin él, la lista es de solo lectura (spec §5).
 *   - refresh: función del panel padre para releer la cancelación (silent=true no muestra el spinner grande).
 *   - onChanged: notifica al padre (ReservaDetailPage) que algo de plata cambió, para refrescar la reserva.
 */
export function T5ResolverLegacyList({ cancellationPublicId, lines, activeSaleInvoices, canEmit, refresh, onChanged }) {
  const { title, progress } = buildResolverLegacyHeaderText(lines);

  // Mientras CUALQUIER fila esté "emitiendo" (Pending en ARCA), refrescamos solo cada 5 segundos
  // para detectar cuándo pasa a emitida/rechazada — mismo patrón que el polling del estado
  // PROCESSING del panel entero, pero acoplado a las filas en vez de al panel completo (acá puede
  // haber una fila emitiendo mientras otra sigue sin resolver).
  useEffect(() => {
    if (!anyLineIsEmitting(lines)) return undefined;
    const intervalId = window.setInterval(() => refresh({ silent: true }), 5000);
    return () => window.clearInterval(intervalId);
  }, [lines, refresh]);

  return (
    <div className="mt-3 space-y-3" data-testid="t5-resolver-list">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h4 className="text-sm font-black text-slate-900 dark:text-white">{title}</h4>
        <span className="text-xs font-bold text-amber-700 dark:text-amber-300">{progress}</span>
      </div>
      <p className="text-xs text-slate-600 dark:text-slate-300">{TEXTO_EXPLICATIVO}</p>

      <ul className="divide-y divide-amber-200 overflow-hidden rounded-xl border border-amber-200 bg-white/70 dark:divide-amber-900/40 dark:border-amber-800 dark:bg-slate-900/60">
        {lines.map((line, index) => (
          <T5ResolverLegacyRow
            key={line.linePublicId}
            index={index}
            line={line}
            invoicesForCurrency={filterInvoicesByCurrency(activeSaleInvoices, line.currency)}
            cancellationPublicId={cancellationPublicId}
            canEmit={canEmit}
            refresh={refresh}
            onChanged={onChanged}
          />
        ))}
      </ul>
    </div>
  );
}

/** Una fila de la lista: servicio + botón "Resolver", o su formulario en línea, o su estado de emisión. */
function T5ResolverLegacyRow({ index, line, invoicesForCurrency, cancellationPublicId, canEmit, refresh, onChanged }) {
  const rowState = resolveRowState(line);
  const [formOpen, setFormOpen] = useState(false);

  // Campos del formulario de resolver (spec §2). Se precargan al abrir la fila, nunca al montar el
  // componente: si el usuario abre y cierra varias veces, cada apertura arranca limpia.
  const [targetInvoicePublicId, setTargetInvoicePublicId] = useState("");
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState(null);
  // Patrón "sugerencia en amarillo" del tarifario (2026-06-05, ver HotelInlineForm.jsx): el monto
  // arranca marcado como sugerido (fondo amarillo) y deja de estarlo apenas el usuario lo toca —
  // NO comparamos el valor actual contra el sugerido, porque si el usuario borra y vuelve a
  // escribir el mismo número a mano, ya no es "la sugerencia del sistema" aunque coincida.
  const [amountIsSuggested, setAmountIsSuggested] = useState(true);

  const [confirmOpen, setConfirmOpen] = useState(false);
  const [emitting, setEmitting] = useState(false);
  const [sending, setSending] = useState(false);
  // Cartel INLINE y persistente para un rechazo del servidor al pedir emitir (ej. INV-T5-EMIT-
  // SIBLING-UNRESOLVED: "todavía hay servicios sin resolver que podrían corresponder a esta misma
  // factura"). Antes era un toast — se lee 4 segundos y desaparece; este mensaje es una guía
  // accionable (le dice al usuario que vaya a resolver OTRA fila primero), así que necesita quedar
  // a la vista hasta que el usuario reintente o cierre — mismo criterio que `guardMessage` en el
  // panel padre y `saveError` acá mismo, para el formulario de resolver.
  const [emitError, setEmitError] = useState(null);

  // Foco accesible (retoque post-review 2026-07-17): al abrir el formulario en línea, movemos el
  // foco al primer campo útil — el desplegable de factura si hay algo para elegir, o el cartel de
  // "no encontramos factura de esa moneda" si no hay nada (§4 de la spec). Sin esto, un usuario de
  // teclado/lector de pantalla que toca "Resolver" queda con el foco "perdido" en el botón viejo,
  // sin ninguna pista de que apareció contenido nuevo debajo.
  const invoiceSelectRef = useRef(null);
  const emptyCurrencyHeadingRef = useRef(null);
  useEffect(() => {
    if (!formOpen) return;
    if (invoicesForCurrency.length === 0) {
      emptyCurrencyHeadingRef.current?.focus();
    } else {
      invoiceSelectRef.current?.focus();
    }
  }, [formOpen, invoicesForCurrency.length]);

  const openForm = () => {
    // Con una sola factura de esa moneda, viene pre-elegida (spec §2 punto 2 + obra "factura
    // pre-elegida" 2026-07-16): el back-office no tiene que elegir algo que ya es obvio.
    setTargetInvoicePublicId(invoicesForCurrency.length === 1 ? invoicesForCurrency[0].publicId : "");
    setAmount(line.suggestedAmount != null ? String(line.suggestedAmount) : "");
    setAmountIsSuggested(true);
    setReason("");
    setSaveError(null);
    setFormOpen(true);
  };

  const handleGuardar = async () => {
    if (!canSaveResolverRow({ targetInvoicePublicId, amount, reason })) return;
    setSaving(true);
    setSaveError(null);
    try {
      const payload = buildResolvePayload(line, { targetInvoicePublicId, confirmedGrossCreditAmount: amount, reason });
      await cancellationsApi.resolvePartialCreditNote(cancellationPublicId, payload);
      setFormOpen(false);
      showSuccess("Devolución resuelta. Ya podés emitirla.");
      await refresh({ silent: true });
      onChanged?.();
    } catch (error) {
      // Ronda 2 (2026-06-06): ante un error recuperable, el formulario NO se cierra y ningún campo
      // se pierde — el usuario solo ve el cartel rojo y puede reintentar con el mismo botón.
      setSaveError(resolveRowSaveErrorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  const openConfirmEmit = () => {
    setEmitError(null);
    setConfirmOpen(true);
  };

  const handleConfirmEmit = async () => {
    setEmitting(true);
    try {
      await cancellationsApi.emitPartialCreditNote(cancellationPublicId, buildEmitPayloadForLine(line));
      setConfirmOpen(false);
      await refresh({ silent: true });
      onChanged?.();
    } catch (error) {
      setEmitError(t5ErrorMessage(error));
      setConfirmOpen(false);
      await refresh({ silent: true });
    } finally {
      setEmitting(false);
    }
  };

  const handleVerPdf = async () => {
    if (!line.creditNoteInvoicePublicId) return;
    try {
      const response = await api.get(`/invoices/${line.creditNoteInvoicePublicId}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
      window.setTimeout(() => window.URL.revokeObjectURL(url), 60000);
    } catch {
      showError("No se pudo abrir la nota de crédito.");
    }
  };

  const handleEnviar = async () => {
    setSending(true);
    try {
      await cancellationsApi.sendPartialCreditNote(cancellationPublicId);
      showSuccess("Nota de crédito enviada al cliente.");
    } catch (error) {
      // Nota de mantenimiento: hoy el endpoint de enviar exige que sea la ÚNICA nota de crédito
      // emitida de toda la cancelación (no distingue todavía cuál de varias filas se quiere
      // enviar). Con 2+ filas ya emitidas, esto puede rechazar con un mensaje del servidor — se
      // muestra tal cual (ya viene limpio, sin jerga), sin inventar un motivo genérico. Ver
      // informe de esta obra para el detalle de esta limitación conocida del backend.
      showError(getApiErrorMessage(error, "No se pudo enviar la nota de crédito. Intentá de nuevo."));
    } finally {
      setSending(false);
    }
  };

  const rowLabel = line.supplierName ? `${line.serviceName} · ${line.supplierName}` : line.serviceName;
  const fieldIdBase = `t5-resolver-${line.linePublicId}`;

  return (
    <li data-testid={`t5-resolver-row-${index}`} className="p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0">
          <p className="truncate text-sm font-bold text-slate-900 dark:text-white">{rowLabel}</p>
          {/* "Resuelto ✓ · factura · monto" queda a la vista en TODOS los estados posteriores a
              resolver (resuelta/emitiendo/emitida/rechazada) — no solo en el primer paso — para
              que el back-office siempre sepa contra qué factura quedó esta devolución. */}
          {rowState !== T5_ROW_STATE.UNRESOLVED && (
            <p data-testid="t5-resolver-saved" className="text-xs font-semibold text-emerald-700 dark:text-emerald-300">{buildResolvedRowText(line)}</p>
          )}
          {(rowState === T5_ROW_STATE.EMITTING || rowState === T5_ROW_STATE.ISSUED || rowState === T5_ROW_STATE.REJECTED) && (
            <RowStatusText line={line} rowState={rowState} />
          )}
          {emitError && (
            <p role="alert" data-testid={`t5-resolver-row-${index}-emit-error`} className="text-xs font-semibold text-rose-700 dark:text-rose-300">
              {emitError}
            </p>
          )}
        </div>

        <div className="flex shrink-0 items-center gap-2">
          {rowState === T5_ROW_STATE.UNRESOLVED && (
            <>
              <span className="text-sm font-bold text-slate-700 dark:text-slate-200">{formatCurrency(line.suggestedAmount, line.currency)}</span>
              {canEmit && (
                <button
                  type="button"
                  onClick={() => (formOpen ? setFormOpen(false) : openForm())}
                  data-testid={`t5-resolver-row-${index}-toggle`}
                  className="rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-black text-white hover:bg-amber-700"
                >
                  {formOpen ? "Cancelar" : "Resolver"}
                </button>
              )}
            </>
          )}

          {rowState === T5_ROW_STATE.RESOLVED && canEmit && (
            <button
              type="button"
              onClick={openConfirmEmit}
              data-testid={`t5-resolver-row-${index}-emit`}
              className="inline-flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-black text-white hover:bg-amber-700"
            >
              <RefreshCw className="h-3.5 w-3.5" />
              Emitir la devolución
            </button>
          )}

          {rowState === T5_ROW_STATE.REJECTED && canEmit && (
            <button
              type="button"
              onClick={openConfirmEmit}
              data-testid={`t5-resolver-row-${index}-retry`}
              className="inline-flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-black text-white hover:bg-amber-700"
            >
              <RefreshCw className="h-3.5 w-3.5" />
              Reintentar
            </button>
          )}

          {rowState === T5_ROW_STATE.ISSUED && (
            <>
              <button
                type="button"
                onClick={handleVerPdf}
                disabled={!line.creditNoteInvoicePublicId}
                className="inline-flex items-center gap-1.5 rounded-lg border border-emerald-300 px-3 py-1.5 text-xs font-bold text-emerald-800 disabled:opacity-50 dark:border-emerald-800 dark:text-emerald-200"
              >
                <Eye className="h-3.5 w-3.5" />
                Ver PDF
              </button>
              <button
                type="button"
                onClick={handleEnviar}
                disabled={sending}
                className="inline-flex items-center gap-1.5 rounded-lg bg-emerald-700 px-3 py-1.5 text-xs font-bold text-white disabled:opacity-50"
              >
                <Send className="h-3.5 w-3.5" />
                {sending ? "Enviando..." : "Enviar al cliente"}
              </button>
            </>
          )}
        </div>
      </div>

      {formOpen && rowState === T5_ROW_STATE.UNRESOLVED && (
        invoicesForCurrency.length === 0 ? (
          <div
            ref={emptyCurrencyHeadingRef}
            tabIndex={-1}
            data-testid="t5-resolver-empty-currency"
            className="mt-3 space-y-2 rounded-xl border border-slate-200 bg-slate-50 p-3 text-xs focus:outline-none focus:ring-2 focus:ring-amber-400 dark:border-slate-700 dark:bg-slate-800/60"
          >
            <p className="font-bold text-slate-700 dark:text-slate-200">{buildResolverFormHeading(line)}</p>
            <p className="text-slate-600 dark:text-slate-300">{buildEmptyCurrencyMessage(line.currency)}</p>
          </div>
        ) : (
          <div data-testid="t5-resolver-form" className="mt-3 space-y-3 rounded-xl border border-amber-200 bg-white p-3 dark:border-amber-800 dark:bg-slate-900">
            <p className="text-xs font-bold text-slate-700 dark:text-slate-200">{buildResolverFormHeading(line)}</p>

            {saveError && (
              <p role="alert" data-testid="t5-resolver-guard-message" className="text-xs font-semibold text-rose-700 dark:text-rose-300">
                {saveError}
              </p>
            )}

            <label className="block text-xs font-bold text-slate-600 dark:text-slate-300" htmlFor={`${fieldIdBase}-invoice`}>
              ¿De qué factura sale esta devolución?
              <select
                ref={invoiceSelectRef}
                id={`${fieldIdBase}-invoice`}
                value={targetInvoicePublicId}
                onChange={(e) => setTargetInvoicePublicId(e.target.value)}
                disabled={saving}
                className="mt-1 w-full rounded-lg border px-3 py-2 text-sm font-normal dark:border-slate-700 dark:bg-slate-800"
              >
                <option value="">{buildInvoicePlaceholder(line.currency)}</option>
                {invoicesForCurrency.map((invoice) => (
                  <option key={invoice.publicId} value={invoice.publicId}>{invoice.label}</option>
                ))}
              </select>
              <span className="mt-1 block font-normal text-slate-500 dark:text-slate-400">{buildInvoiceHelpText(line.currency)}</span>
            </label>

            <label className="block text-xs font-bold text-slate-600 dark:text-slate-300" htmlFor={`${fieldIdBase}-amount`}>
              ¿Cuánto se le devuelve al cliente?
              <div className="mt-1 flex items-center gap-2">
                <span className="text-sm font-bold text-slate-500">{line.currency === "USD" ? "US$" : "$"}</span>
                <input
                  id={`${fieldIdBase}-amount`}
                  type="number"
                  min="0.01"
                  step="0.01"
                  value={amount}
                  onChange={(e) => {
                    setAmount(e.target.value);
                    setAmountIsSuggested(false);
                  }}
                  disabled={saving}
                  // Retoque post-review (2026-07-17): un solo "dark:bg-*" según el estado, nunca los
                  // dos juntos — antes el fondo amarillo de "sugerido" (dark:bg-yellow-950/20)
                  // convivía en el mismo string con el dark:bg-slate-800 de base, y cuál ganaba
                  // dependía del orden de generación de clases de Tailwind (no garantizado).
                  className={`w-full rounded-lg border px-3 py-2 text-sm font-normal ${amountIsSuggested ? "border-yellow-400 bg-yellow-50 dark:border-yellow-700 dark:bg-yellow-950/20" : "border-slate-300 bg-white dark:border-slate-700 dark:bg-slate-800"}`}
                />
              </div>
              <span className="mt-1 block font-normal text-slate-500 dark:text-slate-400">
                Es el total de este servicio en la factura, con impuestos incluidos. Te lo sugerimos por el precio de venta; corregilo solo si en la factura fue otro número.
              </span>
            </label>

            <label className="block text-xs font-bold text-slate-600 dark:text-slate-300" htmlFor={`${fieldIdBase}-reason`}>
              ¿Por qué corresponde esta factura y este monto?
              <textarea
                id={`${fieldIdBase}-reason`}
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                disabled={saving}
                rows={2}
                maxLength={500}
                className="mt-1 w-full rounded-lg border px-3 py-2 text-sm font-normal dark:border-slate-700 dark:bg-slate-800"
              />
              <span className="mt-1 block font-normal text-slate-500 dark:text-slate-400">Queda registrado para explicar esta devolución.</span>
            </label>

            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={() => setFormOpen(false)}
                disabled={saving}
                className="rounded-lg px-3 py-2 text-xs font-bold text-slate-600 disabled:opacity-50 dark:text-slate-300"
              >
                Cancelar
              </button>
              <button
                type="button"
                onClick={handleGuardar}
                disabled={saving || !canSaveResolverRow({ targetInvoicePublicId, amount, reason })}
                data-testid={`t5-resolver-row-${index}-save`}
                className="rounded-lg bg-amber-600 px-4 py-2 text-xs font-black text-white disabled:opacity-50"
              >
                {saving ? "Guardando..." : "Guardar esta devolución"}
              </button>
            </div>
          </div>
        )
      )}

      <ConfirmModal
        isOpen={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        onConfirm={handleConfirmEmit}
        title="¿Seguro?"
        message={CONFIRM_EMIT_MESSAGE}
        confirmText="Sí, emitir"
        cancelText="Volver"
        type="warning"
        isLoading={emitting}
      />
    </li>
  );
}

/** Texto de la fila cuando ya se pidió emitir: emitiendo (ámbar) / emitida (verde) / rechazada (roja). */
function RowStatusText({ line, rowState }) {
  if (rowState === T5_ROW_STATE.EMITTING) {
    return (
      <p className="flex items-center gap-1.5 text-xs font-semibold text-amber-700 dark:text-amber-300">
        <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
        Emitiendo la nota de crédito en ARCA. Este estado se actualiza solo.
      </p>
    );
  }
  if (rowState === T5_ROW_STATE.ISSUED) {
    return (
      <p className="flex items-center gap-1.5 text-xs font-semibold text-emerald-700 dark:text-emerald-300">
        <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
        Nota de crédito emitida{line.creditNoteNumeroComprobante ? ` · ${line.creditNoteNumeroComprobante}` : ""}.
      </p>
    );
  }
  if (rowState === T5_ROW_STATE.REJECTED) {
    return (
      <p className="flex items-center gap-1.5 text-xs font-semibold text-rose-700 dark:text-rose-300" role="alert">
        <AlertTriangle className="h-3.5 w-3.5" aria-hidden="true" />
        ARCA rechazó la devolución. {line.creditNoteArcaErrorMessage || "Revisá los datos e intentá nuevamente."}
      </p>
    );
  }
  return null;
}
