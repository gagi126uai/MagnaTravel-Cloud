/**
 * Bandeja back-office (PASIVA): "Cargos de cancelación pendientes".
 *
 * Spec "el paso de multa vive en la ficha" (2026-07-08): esta bandeja YA NO resuelve
 * nada acá. Antes cada fila tenía un botón que abría ConfirmPenaltyModal directo desde
 * la bandeja; ahora todo el paso de la multa (confirmar, reintentar la ND, corregir
 * monto/moneda) vive en la ficha de la reserva (ver OperatorPenaltyStepPanel). Esta
 * página solo AVISA qué reservas necesitan atención — cada fila es un link a su ficha.
 *
 * TODO (2026-07-08): ConfirmPenaltyModal (cargo PROPIO de la agencia, no la multa del
 * operador) queda sin usar en esta página pero el archivo NO se borra — su reubicación
 * como "cargo propio de la agencia" es una tanda aparte, todavía no diseñada.
 *
 * Permiso requerido: cobranzas.invoice_annul (back-office fiscal).
 */

import { Link } from "react-router-dom";
import { RefreshCw, ReceiptText, ChevronRight } from "lucide-react";
import { textoQueFalta, textoTiempoRelativo } from "../debitNoteInboxLogic";
import { useDebitNotePendingList } from "../hooks/useDebitNotePendingList";

export default function CancellationDebitNoteInboxPage() {
  const { items, loading, error, reload } = useDebitNotePendingList();

  return (
    <div className="space-y-6">
      {/* ─ Header ─────────────────────────────────────────────────────────── */}
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-orange-100 p-2 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300">
          <ReceiptText className="h-5 w-5" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
            Cargos de cancelación pendientes
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Reservas anuladas con una multa o un cargo pendiente. Tocá una para resolverla en su ficha.
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
            No hay multas ni cargos pendientes de cobrar.
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {items.map((row) => (
              <DebitNotePendingRow key={row.bookingCancellationPublicId} row={row} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

// ============================================================================
// Sub-componente: fila de la bandeja
// ============================================================================

/**
 * Fila de un caso en la bandeja pasiva. Ya no tiene botón de acción: la fila ENTERA
 * es un link a la ficha de la reserva (mismo patrón que CollectionsTab/InvoicingTab),
 * que es donde ahora vive todo el paso de la multa. Ser un <Link> real (en vez de un
 * div con onClick + navigate) le da gratis lo que un div clickeable no puede: abrir en
 * pestaña nueva con Ctrl/Cmd+click o click del medio, y foco/Enter nativos de teclado.
 */
function DebitNotePendingRow({ row }) {
  const queFalta = textoQueFalta(row.debitNoteStatus);
  const haceCuanto = textoTiempoRelativo(row.confirmedAt);

  const contenidoFila = (
    <>
      <div className="space-y-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-semibold text-slate-900 dark:text-white text-sm">
            Reserva #{row.reservaNumero}
          </span>
        </div>
        <div className="text-xs text-slate-500 dark:text-slate-400 flex flex-wrap gap-x-4 gap-y-0.5">
          <span className="font-medium text-orange-700 dark:text-orange-300">{queFalta}</span>
          <span>{haceCuanto}</span>
        </div>
      </div>

      <ChevronRight className="h-4 w-4 flex-shrink-0 text-slate-400" aria-hidden="true" />
    </>
  );

  // Guardia: si la fila no trae el GUID de la reserva (dato inconsistente del backend),
  // no armamos un link roto (to="/reservas/undefined") — se muestra igual, para no
  // esconder el caso, pero sin poder navegar.
  if (!row.reservaPublicId) {
    return (
      <div
        data-testid={`debit-note-row-${row.bookingCancellationPublicId}`}
        className="flex items-center justify-between gap-3 px-6 py-4"
      >
        {contenidoFila}
      </div>
    );
  }

  return (
    <Link
      to={`/reservas/${row.reservaPublicId}`}
      data-testid={`debit-note-row-${row.bookingCancellationPublicId}`}
      className="flex items-center justify-between gap-3 px-6 py-4 hover:bg-slate-50 dark:hover:bg-slate-800/60 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-400 focus-visible:ring-inset"
    >
      {contenidoFila}
    </Link>
  );
}
