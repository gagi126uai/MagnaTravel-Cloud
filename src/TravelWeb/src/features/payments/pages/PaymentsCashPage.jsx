/**
 * Página de Caja (arqueo de caja).
 *
 * Muestra 3 métricas (Ingresos / Egresos / Resultado del mes) y la lista de movimientos.
 * Multimoneda (2026-06-11): las 3 métricas muestran pesos y dólares por separado cuando
 * el backend entrega datos por moneda. Los movimientos llevan su cartelito $/US$.
 * El filtro "Moneda" siempre está visible (decisión D, 2026-06-11).
 */
import { Filter, Loader2, Search } from "lucide-react";
import { isAdmin } from "../../../auth";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { FinanceMetricsGrid } from "../components/FinanceMetricsGrid";
import { MovementsTab } from "../components/MovementsTab";
import { useCash } from "../hooks/useCash";
import { useState } from "react";

/**
 * Construye el shape que espera FinanceMetricsGrid para cada métrica.
 *
 * Contrato real del backend /treasury/cash-summary (A-2, 2026-06-11):
 *   summary.cashByCurrency = [{currency, cashInThisMonth, cashOutThisMonth, netCashThisMonth}]
 *   NO "porMoneda", NO "cashIn"/"cashOut" sin sufijo.
 *
 * - Si cashByCurrency tiene 2+ filas → modo multimoneda (tarjetas con dos líneas).
 * - Si tiene ≤1 fila o no existe → modo mono-moneda (tarjetas planas, idéntico al original).
 */
function buildMetricItems(summary) {
  if (!summary) {
    return [
      { label: "Ingresos del mes", value: 0 },
      { label: "Egresos del mes", value: 0 },
      { label: "Resultado de caja del mes", value: 0 },
    ];
  }

  // Contrato real del backend: cashByCurrency (NO porMoneda)
  const cashByCurrency = Array.isArray(summary.cashByCurrency) ? summary.cashByCurrency : null;

  if (cashByCurrency && cashByCurrency.length > 1) {
    // Modo multimoneda: desdoblar cada tarjeta en una línea por moneda.
    // Los campos reales son cashInThisMonth, cashOutThisMonth, netCashThisMonth.
    return [
      {
        label: "Ingresos del mes",
        valuesByCurrency: cashByCurrency.map((pm) => ({
          currency: pm.currency,
          value: pm.cashInThisMonth ?? 0,
        })),
      },
      {
        label: "Egresos del mes",
        valuesByCurrency: cashByCurrency.map((pm) => ({
          currency: pm.currency,
          value: pm.cashOutThisMonth ?? 0,
        })),
      },
      {
        label: "Resultado de caja del mes",
        valuesByCurrency: cashByCurrency.map((pm) => ({
          currency: pm.currency,
          // Usamos netCashThisMonth del backend (ya calculado), no recalcular en frontend.
          // Regla ①: nunca mezclar monedas, cada fila es su propia moneda.
          value: pm.netCashThisMonth ?? 0,
        })),
      },
    ];
  }

  // Modo mono-moneda: escalar plano del summary, igual que antes.
  return [
    { label: "Ingresos del mes", value: summary.cashInThisMonth || 0 },
    { label: "Egresos del mes", value: summary.cashOutThisMonth || 0 },
    { label: "Resultado de caja del mes", value: summary.netCashThisMonth || 0 },
  ];
}

export default function PaymentsCashPage() {
  const adminUser = isAdmin();

  // Filtro de moneda: siempre visible (decisión D, Gastón 2026-06-11).
  // El filtrado se hace client-side sobre el array de movimientos ya cargado.
  const [currencyFilter, setCurrencyFilter] = useState("all");

  const {
    loading,
    summary,
    movements,
    searchTerm,
    setSearchTerm,
    directionFilter,
    setDirectionFilter,
    sourceFilter,
    setSourceFilter,
    page,
    pageSize,
    totalCount,
    totalPages,
    hasPreviousPage,
    hasNextPage,
    setPage,
    setPageSize,
    handleCreateManualMovement,
    handleUpdateManualMovement,
    handleDeleteManualMovement,
    databaseUnavailable,
  } = useCash();

  // Filtro de moneda client-side: "all" muestra todo; "ARS" o "USD" filtra por currency del movimiento.
  const movimientosFiltrados = currencyFilter === "all"
    ? movements
    : movements.filter((m) => (m.currency || "ARS") === currencyFilter);

  if (loading && movements.length === 0) {
    return (
      <div className="flex justify-center items-center h-64 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin" />
      </div>
    );
  }

  const metricItems = buildMetricItems(summary);

  return (
    <div className="space-y-6">
      <ListToolbar
        searchSlot={
          <div className="relative min-w-[220px]">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <input
              type="text"
              placeholder="Buscar referencia, proveedor o reserva..."
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
            />
          </div>
        }
        filterSlot={
          <>
            <div className="relative">
              <Filter className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
              <select
                value={directionFilter}
                onChange={(event) => setDirectionFilter(event.target.value)}
                className="rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-8 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
              >
                <option value="all">Ingresos y egresos</option>
                <option value="income">Solo ingresos</option>
                <option value="expense">Solo egresos</option>
              </select>
            </div>
            <select
              value={sourceFilter}
              onChange={(event) => setSourceFilter(event.target.value)}
              className="rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
            >
              <option value="all">Todos los origenes</option>
              <option value="CustomerPayment">Cobranzas</option>
              <option value="SupplierPayment">Pagos a proveedores</option>
              <option value="ManualAdjustment">Ajustes manuales</option>
            </select>

            {/* Filtro Moneda: siempre visible (decisión D, Gastón 2026-06-11).
                Filtra la lista de movimientos por la moneda del movimiento (client-side). */}
            <select
              value={currencyFilter}
              onChange={(event) => setCurrencyFilter(event.target.value)}
              className="rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
              data-testid="filtro-moneda-caja"
              aria-label="Filtrar por moneda"
            >
              <option value="all">Todas las monedas</option>
              <option value="ARS">$ Pesos</option>
              <option value="USD">US$ Dólares</option>
            </select>
          </>
        }
      />

      {/* Las 3 métricas: con soporte bi-moneda cuando el summary lo entrega */}
      <FinanceMetricsGrid items={metricItems} />

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <MovementsTab
            movements={movimientosFiltrados}
            isAdmin={adminUser}
            onCreateManualMovement={handleCreateManualMovement}
            onUpdateManualMovement={handleUpdateManualMovement}
            onDeleteManualMovement={handleDeleteManualMovement}
            showHeader={false}
          />
          <PaginationFooter
            page={page}
            pageSize={pageSize}
            totalCount={totalCount}
            totalPages={totalPages}
            hasPreviousPage={hasPreviousPage}
            hasNextPage={hasNextPage}
            onPageChange={setPage}
            onPageSizeChange={setPageSize}
          />
        </>
      )}
    </div>
  );
}
