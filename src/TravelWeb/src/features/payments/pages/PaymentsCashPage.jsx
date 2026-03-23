import { Filter, Loader2, Search } from "lucide-react";
import { isAdmin } from "../../../auth";
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
    handleCreateManualMovement,
    handleUpdateManualMovement,
    handleDeleteManualMovement,
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
      <div className="flex flex-col md:flex-row md:items-end justify-between gap-4">
        <div>
          <h2 className="text-xl font-semibold text-slate-900 dark:text-white">Caja</h2>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Dinero que ingreso o salio realmente: cobranzas, pagos a proveedores y ajustes manuales.
          </p>
        </div>

        <div className="flex flex-col lg:flex-row gap-3">
          <div className="relative min-w-[220px]">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <input
              type="text"
              placeholder="Buscar referencia, proveedor o reserva..."
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="pl-9 pr-4 py-2 w-full text-sm bg-slate-50 dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl focus:ring-2 focus:ring-slate-200 transition-shadow dark:text-white"
            />
          </div>
          <div className="relative">
            <Filter className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <select
              value={directionFilter}
              onChange={(event) => setDirectionFilter(event.target.value)}
              className="pl-9 pr-8 py-2 text-sm bg-slate-50 dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl dark:text-white"
            >
              <option value="all">Ingresos y egresos</option>
              <option value="income">Solo ingresos</option>
              <option value="expense">Solo egresos</option>
            </select>
          </div>
          <select
            value={sourceFilter}
            onChange={(event) => setSourceFilter(event.target.value)}
            className="px-3 py-2 text-sm bg-slate-50 dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl dark:text-white"
          >
            <option value="all">Todos los origenes</option>
            <option value="CustomerPayment">Cobranzas</option>
            <option value="SupplierPayment">Pagos a proveedores</option>
            <option value="ManualAdjustment">Ajustes manuales</option>
          </select>
        </div>
      </div>

      <FinanceMetricsGrid
        items={[
          { label: "Ingresos del mes", value: summary?.cashInThisMonth || 0 },
          { label: "Egresos del mes", value: summary?.cashOutThisMonth || 0 },
          { label: "Resultado de caja del mes", value: summary?.netCashThisMonth || 0 },
        ]}
      />

      <MovementsTab
        movements={movements}
        isAdmin={adminUser}
        onCreateManualMovement={handleCreateManualMovement}
        onUpdateManualMovement={handleUpdateManualMovement}
        onDeleteManualMovement={handleDeleteManualMovement}
        showHeader={false}
      />
    </div>
  );
}
