import { Filter, Loader2, Search } from "lucide-react";
import { isAdmin } from "../../../auth";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { FinanceMetricsGrid } from "../components/FinanceMetricsGrid";
import { MovementsTab } from "../components/MovementsTab";
import { useCash } from "../hooks/useCash";

export default function PaymentsCashPage() {
  const adminUser = isAdmin();
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

  if (loading && movements.length === 0) {
    return (
      <div className="flex justify-center items-center h-64 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin" />
      </div>
    );
  }

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
          </>
        }
      />

      <FinanceMetricsGrid
        items={[
          { label: "Ingresos del mes", value: summary?.cashInThisMonth || 0 },
          { label: "Egresos del mes", value: summary?.cashOutThisMonth || 0 },
          { label: "Resultado de caja del mes", value: summary?.netCashThisMonth || 0 },
        ]}
      />

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <MovementsTab
            movements={movements}
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
