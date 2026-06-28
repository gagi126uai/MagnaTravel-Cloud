/**
 * Barra de filtros para la lista de Facturación.
 *
 * Componente REUTILIZABLE: no tiene dependencia de ningún cliente concreto.
 * Cuando se cree la pantalla global de Facturación (módulo Ventas, spec sec.4 2026-06-28),
 * este componente se puede importar y usar sin modificaciones.
 *
 * Filtros que muestra:
 *   - Desde / Hasta (rango de fechas)
 *   - Tipo de comprobante (Todos / Factura A-B-C / NC A-B-C / ND A-B-C)
 *   - Estado (Todos / Aprobado / Rechazado / En proceso / Anulando)
 *   - Moneda (Todas / $ / US$) — filtra por invoice.currency ("ARS"/"USD")
 *   - Buscar por número (texto libre, busca en número formateado "00001-00012345")
 *
 * Props:
 *   - filters: { desde, hasta, tipo, estado, moneda, buscarNumero }
 *   - onChange(nuevosFiltros): se llama con el objeto completo actualizado al cambiar cualquier filtro
 *   - onReset(): limpia todos los filtros al período por defecto (últimos 90 días)
 *   - totalResultados: number — cuántos comprobantes pasan los filtros actuales (para mostrar "N comprobantes")
 *   - isLoading: boolean — muestra estado cargando mientras se trae la lista del backend
 */
import { Search, X } from "lucide-react";
import { OPCIONES_TIPO_FILTRO, OPCIONES_ESTADO_FILTRO } from "../lib/facturacionFilters";

const etiquetaSelect = "rounded-lg border border-slate-200 bg-white px-2 py-1.5 text-sm text-slate-700 focus:outline-none focus:ring-2 focus:ring-indigo-300 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200";
const etiquetaInput = "w-full rounded-xl border border-slate-200 bg-slate-50 py-1.5 pl-8 pr-3 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white";

export function FacturacionFilters({ filters, onChange, onReset, totalResultados, isLoading }) {
  /**
   * Actualiza un campo del objeto de filtros sin tocar los demás.
   * El parent recibe el objeto completo actualizado y puede reaccionar.
   */
  const actualizarFiltro = (campo, valor) => {
    onChange({ ...filters, [campo]: valor });
  };

  return (
    <div className="space-y-3" data-testid="facturacion-filters">
      {/* Fila 1: fechas + tipo */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <label
            htmlFor="filtro-desde"
            className="text-xs font-semibold text-slate-500 dark:text-slate-400 whitespace-nowrap"
          >
            Desde
          </label>
          <input
            id="filtro-desde"
            type="date"
            value={filters.desde || ""}
            onChange={(e) => actualizarFiltro("desde", e.target.value)}
            className={etiquetaSelect}
            data-testid="filtro-desde"
            aria-label="Fecha desde"
          />
        </div>

        <div className="flex items-center gap-2">
          <label
            htmlFor="filtro-hasta"
            className="text-xs font-semibold text-slate-500 dark:text-slate-400 whitespace-nowrap"
          >
            Hasta
          </label>
          <input
            id="filtro-hasta"
            type="date"
            value={filters.hasta || ""}
            onChange={(e) => actualizarFiltro("hasta", e.target.value)}
            className={etiquetaSelect}
            data-testid="filtro-hasta"
            aria-label="Fecha hasta"
          />
        </div>

        <div className="flex items-center gap-2">
          <label
            htmlFor="filtro-tipo"
            className="text-xs font-semibold text-slate-500 dark:text-slate-400 whitespace-nowrap"
          >
            Tipo
          </label>
          <select
            id="filtro-tipo"
            value={filters.tipo || ""}
            onChange={(e) => actualizarFiltro("tipo", e.target.value)}
            className={etiquetaSelect}
            data-testid="filtro-tipo"
          >
            {OPCIONES_TIPO_FILTRO.map((opcion) => (
              <option key={opcion.valor} value={opcion.valor}>
                {opcion.etiqueta}
              </option>
            ))}
          </select>
        </div>
      </div>

      {/* Fila 2: estado + moneda + búsqueda por número + botón reset */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex items-center gap-2">
          <label
            htmlFor="filtro-estado"
            className="text-xs font-semibold text-slate-500 dark:text-slate-400 whitespace-nowrap"
          >
            Estado
          </label>
          <select
            id="filtro-estado"
            value={filters.estado || ""}
            onChange={(e) => actualizarFiltro("estado", e.target.value)}
            className={etiquetaSelect}
            data-testid="filtro-estado"
          >
            {OPCIONES_ESTADO_FILTRO.map((opcion) => (
              <option key={opcion.valor} value={opcion.valor}>
                {opcion.etiqueta}
              </option>
            ))}
          </select>
        </div>

        {/* Filtro de moneda: filtra por invoice.currency ("ARS" / "USD").
            Regla multimoneda: un comprobante pertenece a una sola moneda; este filtro
            muestra exclusivamente los de la moneda elegida. */}
        <div className="flex items-center gap-2">
          <label
            htmlFor="filtro-moneda"
            className="text-xs font-semibold text-slate-500 dark:text-slate-400 whitespace-nowrap"
          >
            Moneda
          </label>
          <select
            id="filtro-moneda"
            value={filters.moneda || ""}
            onChange={(e) => actualizarFiltro("moneda", e.target.value)}
            className={etiquetaSelect}
            data-testid="filtro-moneda"
          >
            <option value="">Todas</option>
            <option value="ARS">$ (Pesos)</option>
            <option value="USD">US$ (Dólares)</option>
          </select>
        </div>

        {/* Búsqueda por número de comprobante (texto libre) */}
        <div className="relative flex-1 min-w-[200px]">
          <Search className="absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400" />
          <input
            type="text"
            placeholder="Buscar por número..."
            value={filters.buscarNumero || ""}
            onChange={(e) => actualizarFiltro("buscarNumero", e.target.value)}
            className={etiquetaInput}
            data-testid="filtro-numero"
            aria-label="Buscar por número de comprobante"
          />
        </div>

        {/* Botón para limpiar todos los filtros y volver al período por defecto */}
        <button
          type="button"
          onClick={onReset}
          className="flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300 dark:hover:bg-slate-800"
          data-testid="filtro-reset"
          aria-label="Limpiar filtros"
        >
          <X className="h-3.5 w-3.5" />
          Limpiar
        </button>

        {/* Contador de resultados */}
        <span className="ml-auto text-sm text-slate-500 dark:text-slate-400" data-testid="filtro-total-resultados">
          {isLoading ? "Cargando..." : `${totalResultados ?? 0} comprobante${(totalResultados ?? 0) !== 1 ? "s" : ""}`}
        </span>
      </div>
    </div>
  );
}
