/**
 * Bloque "Multa pendiente de cobro" de la cuenta corriente del cliente (spec 2026-07-15,
 * `docs/ux/2026-07-15-multas-en-cuenta-del-cliente.md`, sección "SPEC APROBADA PARA IMPLEMENTAR").
 *
 * QUÉ RESUELVE: antes, cuando a un cliente se le cobraba una multa por anular un viaje, esa
 * plata no aparecía en ningún lado de su cuenta corriente — la pantalla escondía las reservas
 * anuladas, y la multa solo vive ahí. Este bloque junta las multas de TODAS las reservas
 * anuladas del cliente en un solo lugar, espejo visual del circuito que ya funciona en la
 * cuenta del operador (SupplierExtractoSection.jsx, aprobado 2026-07-01): un recuadro por
 * moneda arriba + una lista de filas clickeables abajo.
 *
 * Se usa en CustomerAccountPage.jsx, en el carril de arriba (entre el encabezado y las
 * solapas), PRIMERO en ese carril — es deuda del cliente, lo más "urgente" ahí.
 *
 * La identidad de cada fila abre la reserva y una ND aprobada agrega la accion Cobrar.
 *
 * Se dibuja SOLO si el cliente tiene al menos una multa pendiente — si `pendingPenalties`
 * viene vacío o ausente, el componente no renderiza nada (ni título, ni "no hay multas"),
 * igual que el circuito del operador cuando no tiene anulaciones.
 *
 * El front NO recalcula montos ni suma monedas: toda la lógica de qué mostrar (textos de
 * los chips, número grande vs. segunda línea ámbar) vive en pendingPenaltiesLogic.js,
 * separada de este JSX para poder testearla con node:test.
 */
import { Link } from "react-router-dom";
import { AlertTriangle, ChevronRight, DollarSign } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import {
  armarRecuadroMultaPorMoneda,
  debeMostrarBloqueMultasPendientes,
  textoChipEstadoMulta,
} from "../lib/pendingPenaltiesLogic";

// Paleta de color por "tono" (rose = deuda firme / amber = todavía sin comprobante),
// compartida entre el número grande de los recuadros y el chip de cada fila.
const TONO_TEXTO = {
  rose: "text-rose-600 dark:text-rose-400",
  amber: "text-amber-600 dark:text-amber-400",
};

const TONO_CHIP = {
  rose: "bg-rose-100 text-rose-700 dark:bg-rose-950/30 dark:text-rose-300",
  amber: "bg-amber-100 text-amber-700 dark:bg-amber-950/30 dark:text-amber-300",
};

export function MultaPendienteDeCobroBlock({ pendingPenalties, onCobrar, canCobrar = false }) {
  // Cargando / sin datos todavía: el bloque simplemente no aparece (no parpadea con un
  // "$0" falso). Como CustomerAccountPage solo pasa `overview?.pendingPenalties` una vez
  // que el overview cargó, mientras carga este prop viene undefined y ya cae acá.
  if (!debeMostrarBloqueMultasPendientes(pendingPenalties)) return null;

  const { items, totalsByCurrency = [] } = pendingPenalties;

  return (
    <div
      className="overflow-hidden rounded-xl border border-amber-200 bg-amber-50/30 shadow-sm dark:border-amber-900/40 dark:bg-amber-950/10"
      data-testid="bloque-multas-pendientes"
    >
      <div className="flex items-center gap-2 border-b border-amber-100 px-6 py-4 dark:border-amber-900/30">
        <AlertTriangle className="h-5 w-5 text-amber-500" aria-hidden="true" />
        <div>
          <h2 className="text-sm font-bold text-amber-800 dark:text-amber-300">Multa pendiente de cobro</h2>
          <p className="text-xs text-amber-700/80 dark:text-amber-300/70">Desglose incluido en el total “Debe” de la cuenta.</p>
        </div>
      </div>

      {/* Un recuadro por moneda que tenga multas — regla dura multimoneda: nunca se
          suma ARS + USD en un solo número. */}
      <div className="flex flex-wrap gap-4 px-6 py-4">
        {totalsByCurrency.map((total) => (
          <RecuadroMultaPorMoneda key={total.currency} total={total} />
        ))}
      </div>

      <div className="divide-y divide-amber-100 border-t border-amber-100 dark:divide-amber-900/20 dark:border-amber-900/30">
        {items.map((item) => (
          <FilaMultaPendiente
            key={item.debitNotePublicId || `${item.reservaPublicId}-${item.amount}-${item.currency}`}
            item={item}
            onCobrar={onCobrar}
            canCobrar={canCobrar}
          />
        ))}
      </div>
    </div>
  );
}

/** Recuadro de UNA moneda: número grande (firme o "sin comprobante") + segunda línea ámbar opcional. */
function RecuadroMultaPorMoneda({ total }) {
  const recuadro = armarRecuadroMultaPorMoneda(total);
  return (
    <div
      className="min-w-[160px] rounded-lg bg-white/70 px-4 py-3 dark:bg-slate-900/40"
      data-testid={`recuadro-multa-${recuadro.currency}`}
    >
      <div className="text-[11px] font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
        En {recuadro.currency === "USD" ? "US$" : "$"}
      </div>
      <div className={`mt-0.5 text-xl font-bold ${TONO_TEXTO[recuadro.colorMontoGrande]}`}>
        {recuadro.montoGrandeTexto}
        {recuadro.etiquetaMontoGrande && (
          <span className="ml-1.5 text-[10px] font-semibold uppercase tracking-wide align-middle">
            {recuadro.etiquetaMontoGrande}
          </span>
        )}
      </div>
      {recuadro.segundaLineaAmbar && (
        <div className="mt-0.5 text-xs font-semibold text-amber-600 dark:text-amber-400">
          {recuadro.segundaLineaAmbar}
        </div>
      )}
    </div>
  );
}

/**
 * Fila de UNA multa: el detalle navega a la reserva y el botón de cobro queda como
 * control hermano, sin anidar un botón interactivo dentro del link.
 */
function FilaMultaPendiente({ item, onCobrar, canCobrar }) {
  const chip = textoChipEstadoMulta(item.status);
  const isCollectable = item.status === "pendingCollection" && Boolean(item.debitNotePublicId);
  return (
    <div
      className="flex items-center justify-between gap-3 px-6 py-3 hover:bg-amber-100/40 dark:hover:bg-amber-900/10 transition-colors"
      data-testid={`fila-multa-pendiente-${item.reservaPublicId}`}
    >
      <Link
        to={`/reservas/${item.reservaPublicId}`}
        className="min-w-0 truncate text-sm font-semibold text-slate-900 hover:text-indigo-600 dark:text-white dark:hover:text-indigo-300"
      >
        {item.numeroReserva} · {item.name}
        {item.documentRef && <span className="ml-2 text-xs font-normal text-slate-500">ND {item.documentRef}</span>}
      </Link>
      <span className="flex flex-shrink-0 items-center gap-3">
        <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
          {formatCurrency(item.amount, item.currency)}
        </span>
        <span
          className={`inline-flex items-center rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide ${TONO_CHIP[chip.tono]}`}
        >
          {chip.texto}
        </span>
        {isCollectable && canCobrar ? (
          <button type="button" onClick={() => onCobrar?.(item)} className="inline-flex items-center gap-1 rounded-md bg-indigo-600 px-2.5 py-1.5 text-xs font-semibold text-white hover:bg-indigo-700">
            <DollarSign className="h-3.5 w-3.5" aria-hidden="true" /> Cobrar
          </button>
        ) : <ChevronRight className="h-4 w-4 text-slate-400" aria-hidden="true" />}
      </span>
    </div>
  );
}
