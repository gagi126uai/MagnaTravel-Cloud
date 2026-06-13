/**
 * Bandeja back-office: "Notas de crédito por revisar".
 *
 * ADR-025 §3: cuando se cancela un servicio individual dentro de una reserva,
 * puede quedar una nota de crédito (NC) pendiente de emisión manual (el flujo
 * de NC parcial está congelado hasta la firma del contador). Esta bandeja lista
 * esas cancelaciones para que el equipo de back-office las atienda.
 *
 * IMPORTANTE: HOY la lista viene VACÍA casi siempre.
 * Eso es CORRECTO y ESPERADO, no un bug. El empty state lo explica.
 *
 * Permiso requerido: cobranzas.view_all (back-office, mismo dominio de cobranza).
 * Patrón visual: hermana de CancellationDebitNoteInboxPage.
 *
 * Se muestra dentro de "Cobranza y Facturación" como una sub-ruta gateada por permiso.
 */

import { useCallback } from "react";
import { RefreshCw, FileX2, ExternalLink } from "lucide-react";
import { useNavigate } from "react-router-dom";
import { usePendingCreditNoteReviewList } from "../hooks/usePendingCreditNoteReviewList";

export default function CancellationCreditNoteInboxPage() {
  const { items, loading, error, reload } = usePendingCreditNoteReviewList();
  const navigate = useNavigate();

  const handleVerReserva = useCallback((reservaPublicId) => {
    navigate(`/reservas/${reservaPublicId}`);
  }, [navigate]);

  return (
    <div className="space-y-6">
      {/* ─ Header ─────────────────────────────────────────────────────────── */}
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-cyan-100 p-2 text-cyan-700 dark:bg-cyan-900/30 dark:text-cyan-300">
          <FileX2 className="h-5 w-5" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">
            Notas de credito por revisar
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Cancelaciones de servicios individuales con nota de credito pendiente de emision.
            Requieren revision manual del equipo de facturacion.
          </p>
        </div>
      </div>

      {/* ─ Panel principal ─────────────────────────────────────────────────── */}
      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        {/* Toolbar */}
        <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
          <span className="text-sm font-semibold text-slate-600 dark:text-slate-300">
            {items.length > 0 ? `${items.length} caso(s) pendiente(s)` : ""}
          </span>
          <button
            type="button"
            onClick={reload}
            disabled={loading}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
            aria-label="Refrescar bandeja"
            data-testid="refresh-credit-note-review-inbox"
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
          // Empty state amable: HOY es el estado normal. La lista se llenará
          // cuando el flujo de NC parcial esté habilitado (firma del contador).
          <div
            className="px-6 py-12 text-center space-y-2"
            data-testid="empty-state-credit-note-review"
          >
            <FileX2 className="mx-auto h-10 w-10 text-slate-200 dark:text-slate-700" aria-hidden="true" />
            <p className="text-sm font-semibold text-slate-500 dark:text-slate-400">
              No hay notas de credito por revisar
            </p>
            <p className="text-xs text-slate-400 dark:text-slate-500 max-w-sm mx-auto">
              Cuando se cancelen servicios con NC pendiente de emision, van a aparecer aca.
              Por ahora la emision de NC parciales es manual — esta bandeja se usa como seguimiento.
            </p>
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {items.map((row) => (
              <CreditNoteReviewRow
                key={row.bookingCancellationPublicId}
                row={row}
                onVerReserva={() => handleVerReserva(row.reservaPublicId)}
              />
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
 * Fila de un caso en la bandeja de NC por revisar.
 *
 * Muestra: número de reserva, cliente, estado de la cancelación,
 * fecha que entró a revisión, monto con moneda.
 * El botón "Ver reserva" navega al detalle.
 *
 * Props:
 * - row: PendingCreditNoteReviewDto del backend
 * - onVerReserva: () => void — navega al detalle de la reserva
 */
function CreditNoteReviewRow({ row, onVerReserva }) {
  const fechaReview = row.enteredReviewAt
    ? new Date(row.enteredReviewAt).toLocaleDateString("es-AR", {
        day: "2-digit",
        month: "2-digit",
        year: "numeric",
      })
    : "—";

  // Formateamos el monto con su moneda (si existe).
  // Regla multimoneda: cada fila muestra su moneda propia — no sumamos entre monedas.
  const montoFormateado =
    row.creditNoteAmount != null
      ? Number(row.creditNoteAmount).toLocaleString("es-AR", {
          style: "currency",
          currency: row.creditNoteCurrency || "ARS",
        })
      : "—";

  return (
    <div
      className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 px-6 py-4"
      data-testid={`credit-note-review-row-${row.bookingCancellationPublicId}`}
    >
      <div className="space-y-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="font-semibold text-slate-900 dark:text-white text-sm">
            Reserva #{row.reservaNumero}
          </span>
          {/* Badge de estado: viene tal cual del backend como string */}
          <span className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider bg-cyan-100 text-cyan-700 dark:bg-cyan-900/30 dark:text-cyan-300">
            {row.status || "Pendiente"}
          </span>
        </div>
        <div className="text-xs text-slate-500 dark:text-slate-400 flex flex-wrap gap-x-4 gap-y-0.5">
          {row.clienteNombre && (
            <span>Cliente: <strong>{row.clienteNombre}</strong></span>
          )}
          <span>Monto NC: <strong>{montoFormateado}</strong></span>
          {row.enteredReviewAt && (
            <span>En revision desde: <strong>{fechaReview}</strong></span>
          )}
        </div>
      </div>

      <button
        type="button"
        onClick={onVerReserva}
        data-testid={`btn-ver-reserva-${row.bookingCancellationPublicId}`}
        className="flex-shrink-0 inline-flex items-center gap-1.5 rounded-xl border border-indigo-300 bg-indigo-50 px-3 py-2 text-xs font-bold text-indigo-700 hover:bg-indigo-100 dark:border-indigo-700 dark:bg-indigo-950/30 dark:text-indigo-300 dark:hover:bg-indigo-900/40 transition-colors"
      >
        <ExternalLink className="h-3.5 w-3.5" />
        Ver reserva
      </button>
    </div>
  );
}
