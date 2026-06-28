/**
 * Solapa "Facturación" de la cuenta corriente del cliente.
 *
 * Responsabilidades de este componente:
 *   1. Cargar TODOS los comprobantes del cliente (pageSize=500, una sola solicitud).
 *   2. Mantener los filtros activos y aplicarlos client-side vía aplicarFiltros().
 *   3. Mostrar la barra de filtros FacturacionFilters + la grilla de resultados.
 *   4. Exponer la acción "Ver PDF" de cada comprobante a través de la prop onVerFactura.
 *
 * Por qué filtra client-side:
 *   El endpoint GET /customers/{id}/account/invoices solo soporta el param `search`.
 *   Los demás filtros (fechas, tipo, estado, moneda, número) se aplican sobre los datos
 *   ya cargados. El volumen de facturas por cliente es bajo → aceptable.
 *   TODO: cuando el backend agregue params de filtrado, mover a server-side.
 *
 * Diseñado para ser LEVANTABLE: FacturacionFilters y aplicarFiltros() no tienen
 * dependencia del cliente concreto, se pueden reusar en la pantalla global de Facturación.
 *
 * Props:
 *   - customerPublicId: string — publicId del cliente
 *   - onVerFactura(invoice): función para abrir el PDF del comprobante (manejada en el padre)
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { Eye, Loader2, Receipt, RefreshCw } from "lucide-react";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import {
  DataGrid,
  DataGridActionCell,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { FacturacionFilters } from "./FacturacionFilters";
import {
  aplicarFiltros,
  calcularPeriodoPorDefecto,
  formatTipoComprobante,
  resolverEstadoFiscal,
  OPCIONES_ESTADO_FILTRO,
} from "../lib/facturacionFilters";

/** Chip de estado fiscal ARCA. Nunca muestra el código interno: siempre texto español. */
function ChipEstadoFiscal({ invoice }) {
  if (invoice.annulmentStatus === "Pending") {
    return (
      <span
        className="rounded-full px-2 py-0.5 text-[10px] font-bold uppercase bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400"
        role="status"
        aria-live="polite"
      >
        Anulando…
      </span>
    );
  }

  const estado = resolverEstadoFiscal(invoice);
  const estilos = {
    aprobado: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400",
    rechazado: "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-400",
    en_proceso: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300",
  };
  const etiquetas = { aprobado: "Aprobado", rechazado: "Rechazado", en_proceso: "En proceso" };

  return (
    <span className={`rounded-full px-2 py-0.5 text-[10px] font-bold uppercase ${estilos[estado] ?? estilos.en_proceso}`}>
      {etiquetas[estado] ?? "En proceso"}
    </span>
  );
}

/** Formatea número de comprobante en estilo "00001-00012345". */
function formatNumeroComprobante(invoice) {
  const pdv = String(invoice.puntoDeVenta ?? 0).padStart(5, "0");
  const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
  return `${pdv}-${num}`;
}

export function FacturacionClienteTab({ customerPublicId, onVerFactura }) {
  // Lista COMPLETA de comprobantes cargada del backend (sin filtros)
  const [todosLosComprobantes, setTodosLosComprobantes] = useState([]);
  const [cargando, setCargando] = useState(true);
  const [error, setError] = useState(null);

  // Estado de filtros activos.
  // El período por defecto es los últimos 90 días (decisión UX P13=A, 2026-06-28).
  const [filters, setFilters] = useState(() => ({
    ...calcularPeriodoPorDefecto(),
    tipo: "",
    estado: "",
    moneda: "",
    buscarNumero: "",
  }));

  /**
   * Carga todos los comprobantes del cliente en una sola request.
   * pageSize=500: para el volumen típico de un cliente esto trae todo.
   * Si un cliente tuviera más de 500 comprobantes, habría que paginar múltiples requests,
   * pero eso es un caso edge que no aplica hoy.
   */
  const cargarComprobantes = useCallback(async () => {
    setCargando(true);
    setError(null);
    try {
      const params = new URLSearchParams({
        page: "1",
        pageSize: "500",
        sortBy: "createdAt",
        sortDir: "asc",
      });
      const response = await api.get(`/customers/${customerPublicId}/account/invoices?${params.toString()}`);
      setTodosLosComprobantes(response?.items ?? []);
    } catch (err) {
      setError(getApiErrorMessage(err) || "No se pudieron cargar los comprobantes.");
    } finally {
      setCargando(false);
    }
  }, [customerPublicId]);

  // Carga al montar el componente y cuando cambia el cliente
  useEffect(() => {
    cargarComprobantes();
  }, [cargarComprobantes]);

  /**
   * Lista filtrada: se recalcula solo cuando cambia la lista completa o los filtros.
   * Es costosa si la lista es grande, por eso useMemo evita recalcular en cada render.
   */
  const comprobantesFiltrados = useMemo(
    () => aplicarFiltros(todosLosComprobantes, filters),
    [todosLosComprobantes, filters]
  );

  /** Resetea todos los filtros al período por defecto (últimos 90 días). */
  const handleReset = () => {
    setFilters({
      ...calcularPeriodoPorDefecto(),
      tipo: "",
      estado: "",
      moneda: "",
      buscarNumero: "",
    });
  };

  if (cargando) {
    return (
      <div className="flex items-center justify-center gap-2 py-12 text-sm text-slate-400">
        <Loader2 className="h-5 w-5 animate-spin" />
        Cargando comprobantes...
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex flex-col items-center gap-3 py-12 text-center">
        <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>
        <button
          type="button"
          onClick={cargarComprobantes}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
        >
          <RefreshCw className="h-3.5 w-3.5" />
          Reintentar
        </button>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Barra de filtros — reutilizable en la pantalla global de Facturación */}
      <div className="rounded-xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <FacturacionFilters
          filters={filters}
          onChange={setFilters}
          onReset={handleReset}
          totalResultados={comprobantesFiltrados.length}
          isLoading={cargando}
        />
      </div>

      {/* Tabla de comprobantes (desktop).
          Regla multimoneda: cada comprobante muestra su propia moneda (badge + importe formateado).
          El filtro de Moneda en la barra superior ya separa; aquí solo mostramos lo que queda. */}
      <DataGrid density="compact" minWidth="960px">
        <DataGridHeader>
          <DataGridHeaderRow>
            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
            <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
            <DataGridHeaderCell>Tipo</DataGridHeaderCell>
            <DataGridHeaderCell>Moneda</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Importe</DataGridHeaderCell>
            <DataGridHeaderCell align="center">Estado</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Acción</DataGridHeaderCell>
          </DataGridHeaderRow>
        </DataGridHeader>
        <DataGridBody>
          {comprobantesFiltrados.length === 0 ? (
            <DataGridEmptyState
              colSpan={7}
              title={
                todosLosComprobantes.length === 0
                  ? "No hay comprobantes para mostrar."
                  : "Ningún comprobante coincide con los filtros."
              }
            />
          ) : (
            comprobantesFiltrados.map((invoice) => {
              // El backend ahora provee invoice.currency ("ARS" / "USD").
              // Si no viene (caso legacy), se asume ARS como fallback defensivo.
              const moneda = invoice.currency ?? "ARS";
              return (
                <DataGridRow key={getPublicId(invoice)}>
                  <DataGridCell>{formatDate(invoice.createdAt)}</DataGridCell>
                  <DataGridCell className="font-mono font-semibold text-slate-900 dark:text-white">
                    {formatNumeroComprobante(invoice)}
                  </DataGridCell>
                  <DataGridCell>
                    <div className="flex items-center gap-2">
                      <Receipt className="h-4 w-4 text-indigo-400 flex-shrink-0" />
                      <span>{formatTipoComprobante(invoice.tipoComprobante)}</span>
                    </div>
                  </DataGridCell>
                  <DataGridCell>
                    <CurrencyBadge currency={moneda} />
                  </DataGridCell>
                  <DataGridCell align="right" className="font-semibold text-slate-900 dark:text-white">
                    {formatCurrency(invoice.importeTotal, moneda)}
                  </DataGridCell>
                  <DataGridCell align="center">
                    <ChipEstadoFiscal invoice={invoice} />
                  </DataGridCell>
                  <DataGridActionCell>
                    <button
                      type="button"
                      onClick={() => onVerFactura(invoice)}
                      className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 dark:hover:text-white"
                      data-testid={`ver-factura-${getPublicId(invoice)}`}
                    >
                      <Eye className="h-4 w-4" />
                      Ver
                    </button>
                  </DataGridActionCell>
                </DataGridRow>
              );
            })
          )}
        </DataGridBody>
      </DataGrid>

      {/* Cards mobile */}
      {comprobantesFiltrados.length === 0 ? (
        <ListEmptyState
          title={
            todosLosComprobantes.length === 0
              ? "No hay comprobantes para mostrar."
              : "Ningún comprobante coincide con los filtros."
          }
          className="md:hidden rounded-xl border border-dashed border-slate-200 dark:border-slate-800"
        />
      ) : (
        <MobileRecordList>
          {comprobantesFiltrados.map((invoice) => {
            const moneda = invoice.currency ?? "ARS";
            return (
              <MobileRecordCard
                key={getPublicId(invoice)}
                statusSlot={<ChipEstadoFiscal invoice={invoice} />}
                accentSlot={<CurrencyBadge currency={moneda} />}
                title={formatNumeroComprobante(invoice)}
                subtitle={formatTipoComprobante(invoice.tipoComprobante)}
                meta={
                  <>
                    <div className="text-xs text-slate-500 dark:text-slate-400">
                      Fecha {formatDate(invoice.createdAt)}
                    </div>
                    <div className="text-xs text-slate-500 dark:text-slate-400">
                      Importe {formatCurrency(invoice.importeTotal, moneda)}
                    </div>
                  </>
                }
                footerActions={
                  <button
                    type="button"
                    onClick={() => onVerFactura(invoice)}
                    className="inline-flex rounded-lg border border-slate-200 px-3 py-2 text-xs font-semibold text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:hover:bg-slate-800"
                  >
                    Ver
                  </button>
                }
              />
            );
          })}
        </MobileRecordList>
      )}
    </div>
  );
}
