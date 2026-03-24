import { useState } from "react";
import { Filter, Loader2, Search } from "lucide-react";
import PaymentModal from "../../../components/PaymentModal";
import { FinanceMetricsGrid } from "../components/FinanceMetricsGrid";
import { CollectionsTab } from "../components/CollectionsTab";
import { useCollections } from "../hooks/useCollections";

export default function PaymentsCollectionsPage() {
  const [selectedItem, setSelectedItem] = useState(null);
  const {
    loading,
    summary,
    items,
    searchTerm,
    setSearchTerm,
    urgencyFilter,
    setUrgencyFilter,
    loadData,
  } = useCollections();

  if (loading && items.length === 0) {
    return (
      <div className="flex justify-center items-center h-64 text-slate-400">
        <Loader2 className="w-8 h-8 animate-spin" />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col sm:flex-row justify-end gap-3">
        <div className="flex flex-col sm:flex-row gap-3">
          <div className="relative min-w-[240px]">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <input
              type="text"
              placeholder="Buscar reserva, cliente o responsable..."
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="pl-9 pr-4 py-2 w-full text-sm bg-slate-50 dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl focus:ring-2 focus:ring-slate-200 transition-shadow dark:text-white"
            />
          </div>
          <div className="relative">
            <Filter className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <select
              value={urgencyFilter}
              onChange={(event) => setUrgencyFilter(event.target.value)}
              className="pl-9 pr-8 py-2 text-sm bg-slate-50 dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl dark:text-white"
            >
              <option value="all">Todas</option>
              <option value="urgent">Solo urgentes</option>
              <option value="blocked">Solo bloqueadas</option>
            </select>
          </div>
        </div>
      </div>

      <FinanceMetricsGrid
        items={[
          { label: "Saldo pendiente de cobro", value: summary?.pendingAmount || 0 },
          { label: "Cobrado este mes", value: summary?.collectedThisMonth || 0 },
          { label: "Reservas urgentes", value: summary?.urgentReservationsCount || 0, isCount: true },
          { label: "Saldo urgente", value: summary?.urgentPendingAmount || 0 },
          { label: "Bloquean operativo", value: summary?.blockedOperationalCount || 0, isCount: true },
          { label: "Bloquean voucher", value: summary?.blockedVoucherCount || 0, isCount: true },
        ]}
      />

      <CollectionsTab items={items} onPay={setSelectedItem} />

      <PaymentModal
        isOpen={Boolean(selectedItem)}
        onClose={() => setSelectedItem(null)}
        reservaId={selectedItem?.reservaPublicId || selectedItem?.reservaId}
        maxAmount={selectedItem?.balance}
        onSuccess={async () => {
          setSelectedItem(null);
          await loadData();
        }}
      />
    </div>
  );
}
