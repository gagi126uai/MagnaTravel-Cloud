import { useState } from "react";
import { Filter, Loader2, Search } from "lucide-react";
import PaymentModal from "../../../components/PaymentModal";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
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
    page,
    pageSize,
    totalCount,
    totalPages,
    hasPreviousPage,
    hasNextPage,
    setPage,
    setPageSize,
    databaseUnavailable,
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
      <ListToolbar
        searchSlot={
          <div className="relative min-w-[240px]">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <input
              type="text"
              placeholder="Buscar reserva, cliente o responsable..."
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
            />
          </div>
        }
        filterSlot={
          <div className="relative">
            <Filter className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <select
              value={urgencyFilter}
              onChange={(event) => setUrgencyFilter(event.target.value)}
              className="rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-8 text-sm dark:border-slate-800 dark:bg-slate-900 dark:text-white"
            >
              <option value="all">Todas</option>
              <option value="urgent">Solo urgentes</option>
              <option value="blocked">Solo bloqueadas</option>
            </select>
          </div>
        }
      />

      {/* F4-7 (2026-06-26): "Cobrado este mes" usa collectedThisMonthByCurrency cuando
          el backend lo expone con más de una moneda (multimoneda). En mono-moneda (o DTO
          viejo sin el campo), cae al escalar collectedThisMonth para compatibilidad.
          La línea chica de "saldo a favor aplicado" aparece cuando el backend expone
          creditApplicationsByCurrency (campo aún no disponible → línea no se muestra). */}
      <FinanceMetricsGrid
        items={[
          { label: "Saldo pendiente de cobro", value: summary?.pendingAmount || 0 },
          (() => {
            const byCurrency = summary?.collectedThisMonthByCurrency;
            // Bug fix #2 (2026-06-26): era `length > 1` → omitía meses con una sola moneda extranjera
            // (ej: solo USD), caía al escalar ARS y mostraba "$ 3.400" en vez de "US$ 3.400".
            // Regla: usar porMoneda siempre que haya al menos una entrada; escalar solo si vacío/ausente.
            const tieneMonedas = Array.isArray(byCurrency) && byCurrency.length >= 1;

            // Bug fix #1 (2026-06-26): mapear `amount → value` para creditApplicationsByCurrency,
            // igual que se hace para collectedThisMonthByCurrency (shape del backend: { currency, amount }).
            // FinanceMetricsGrid siempre lee `pm.value` — sin el mapeo la línea chica nunca aparecía.
            const creditByCurrency = (summary?.creditApplicationsThisMonthByCurrency ?? [])
              .map((pm) => ({ currency: pm.currency, value: pm.amount }));

            return {
              label: "Cobrado este mes",
              testId: "kpi-cobrado-mes",
              // Con porMoneda: mostrar cada moneda real por separado (nunca sumar ARS + USD).
              // Sin porMoneda: escalar de compatibilidad (DTO viejo sin el campo).
              ...(tieneMonedas
                ? { valuesByCurrency: byCurrency.map((pm) => ({ currency: pm.currency, value: pm.amount })) }
                : { value: summary?.collectedThisMonth || 0 }),
              creditApplicationsByCurrency: creditByCurrency,
            };
          })(),
          { label: "Reservas urgentes", value: summary?.urgentReservationsCount || 0, isCount: true },
          { label: "Saldo urgente", value: summary?.urgentPendingAmount || 0 },
          { label: "Bloquean operativo", value: summary?.blockedOperationalCount || 0, isCount: true },
          { label: "Bloquean voucher", value: summary?.blockedVoucherCount || 0, isCount: true },
        ]}
      />

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : (
        <>
          <CollectionsTab items={items} onPay={setSelectedItem} />
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

      {/*
        ADR-035: le pasamos monedaPrincipal y porMoneda del item de la worklist.
        El CollectionsTab ya llama onPay(item) con el objeto completo; selectedItem
        tiene todos los campos del CollectionWorkItemDto (camelCase por la API).
      */}
      <PaymentModal
        isOpen={Boolean(selectedItem)}
        onClose={() => setSelectedItem(null)}
        reservaId={selectedItem?.reservaPublicId}
        maxAmount={selectedItem?.balance}
        monedaPrincipal={selectedItem?.monedaPrincipal}
        porMoneda={selectedItem?.porMoneda}
        onSuccess={async () => {
          setSelectedItem(null);
          await loadData();
        }}
      />
    </div>
  );
}
