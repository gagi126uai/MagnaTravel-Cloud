/**
 * "Comprobantes por resolver" — monitor PASIVO dentro de la pantalla de Facturación
 * (ADR-044 T4, spec `docs/ux/2026-07-10-t4-multas-pantallas.md` sección 3).
 *
 * Reemplaza la vieja pantalla "Pendientes con AFIP" (desarmada): funde las multas/
 * cargos pendientes y las notas de crédito por revisar en UNA sola lista para MIRAR.
 * Sin botones de acción propios — cada fila es un link a la ficha de la reserva,
 * que es donde ahora vive toda la resolución (spec "el paso de multa vive en la
 * ficha", 2026-07-08). Nunca muestra el texto crudo del error de AFIP.
 *
 * "Recibos por regularizar" NO vive acá: tiene acciones reales y queda como pestaña
 * aparte (ver FacturacionPage.jsx).
 */

import { Link } from "react-router-dom";
import { ChevronRight, Inbox, RefreshCw } from "lucide-react";
import { useDebitNotePendingList } from "../../cancellations/hooks/useDebitNotePendingList";
import { usePendingCreditNoteReviewList } from "../../cancellations/hooks/usePendingCreditNoteReviewList";
import { fusionarComprobantesPorResolver } from "../../cancellations/lib/comprobantesPorResolverLogic";

/**
 * Props:
 *   - puedeVerMultas: boolean — permiso `cobranzas.invoice_annul`. Sin él, no se
 *     llama al endpoint de multas (evita un 403 innecesario) y la lista fusionada
 *     muestra solo la parte de notas de crédito.
 *   - puedeVerNotasCredito: boolean — permiso `cobranzas.view_all` (fix F1,
 *     2026-07-10). Sin él, no se llama al endpoint de NC por revisar y la lista
 *     fusionada muestra solo la parte de multas. Antes de este fix se llamaba
 *     siempre, sin importar el permiso, y un Vendedor con solo invoice_annul recibía
 *     un 403 silencioso de este endpoint.
 */
export function ComprobantesPorResolverTab({ puedeVerMultas, puedeVerNotasCredito }) {
  const multas = useDebitNotePendingList(puedeVerMultas);
  const notasCredito = usePendingCreditNoteReviewList(puedeVerNotasCredito);

  const cargando = (puedeVerMultas && multas.loading) || (puedeVerNotasCredito && notasCredito.loading);
  const hayError = (puedeVerMultas && multas.error) || (puedeVerNotasCredito && notasCredito.error);

  const filas = fusionarComprobantesPorResolver(multas.items, notasCredito.items);

  const handleReload = () => {
    if (puedeVerMultas) multas.reload();
    if (puedeVerNotasCredito) notasCredito.reload();
  };

  return (
    <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900" data-testid="comprobantes-por-resolver">
      <div className="flex items-center justify-between border-b border-slate-100 px-6 py-4 dark:border-slate-800">
        <span className="text-sm font-semibold text-slate-600 dark:text-slate-300">
          {filas.length > 0 ? `${filas.length} caso(s)` : ""}
        </span>
        <button
          type="button"
          onClick={handleReload}
          disabled={cargando}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
          aria-label="Actualizar comprobantes por resolver"
          data-testid="comprobantes-por-resolver-refresh"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${cargando ? "animate-spin" : ""}`} />
          Actualizar
        </button>
      </div>

      {cargando ? (
        <div className="px-6 py-10 text-center text-sm text-slate-500">Cargando…</div>
      ) : hayError ? (
        <div className="px-6 py-10 text-center space-y-2">
          <p className="text-sm text-rose-600">No se pudo cargar la lista.</p>
          <button
            type="button"
            onClick={handleReload}
            className="text-xs text-indigo-600 hover:underline dark:text-indigo-400"
          >
            Reintentar
          </button>
        </div>
      ) : filas.length === 0 ? (
        <div className="px-6 py-12 text-center space-y-2" data-testid="comprobantes-por-resolver-empty">
          <Inbox className="mx-auto h-8 w-8 text-slate-200 dark:text-slate-700" aria-hidden="true" />
          <p className="text-sm font-semibold text-slate-500 dark:text-slate-400">
            No hay comprobantes por resolver.
          </p>
        </div>
      ) : (
        <div className="divide-y divide-slate-100 dark:divide-slate-800">
          {filas.map((fila) => (
            <FilaComprobantePorResolver key={fila.key} fila={fila} />
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Fila pasiva: link entero a la ficha, SIN botón propio (regla 2026-07-08). Mismo
 * criterio que DebitNotePendingRow — si falta el GUID de la reserva (dato
 * inconsistente), se muestra igual pero sin poder navegar (nunca se esconde el caso).
 */
function FilaComprobantePorResolver({ fila }) {
  const contenido = (
    <>
      <div className="space-y-1 min-w-0">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-xs font-bold uppercase tracking-wide text-slate-400">
            {fila.comprobante}
          </span>
          <span className="font-semibold text-slate-900 dark:text-white text-sm">
            Reserva #{fila.reservaNumero}
          </span>
        </div>
        <div className="text-xs text-slate-500 dark:text-slate-400 flex flex-wrap gap-x-4 gap-y-0.5">
          <span className="font-medium text-orange-700 dark:text-orange-300">{fila.queFalta}</span>
          <span>{fila.haceCuanto}</span>
        </div>
      </div>
      <ChevronRight className="h-4 w-4 flex-shrink-0 text-slate-400" aria-hidden="true" />
    </>
  );

  if (!fila.reservaPublicId) {
    return (
      <div data-testid={`comprobante-por-resolver-${fila.key}`} className="flex items-center justify-between gap-3 px-6 py-4">
        {contenido}
      </div>
    );
  }

  return (
    <Link
      to={`/reservas/${fila.reservaPublicId}`}
      data-testid={`comprobante-por-resolver-${fila.key}`}
      className="flex items-center justify-between gap-3 px-6 py-4 hover:bg-slate-50 dark:hover:bg-slate-800/60 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-400 focus-visible:ring-inset"
    >
      {contenido}
    </Link>
  );
}
