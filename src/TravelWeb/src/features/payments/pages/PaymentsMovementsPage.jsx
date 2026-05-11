import { useState } from "react";
import { RefreshCw, Search } from "lucide-react";
import { useDebounce } from "../../../hooks/useDebounce";
import { useMovements } from "../../movements/hooks/useMovements";
import MovementsTimeline from "../../movements/components/MovementsTimeline";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";

// B1.15 Fase D'.B (2026-05-11): pestaña "Movimientos" — timeline cronológico
// global con filtros. Sirve para "ver todo lo que pasó hoy" o conciliar.

const KIND_OPTIONS = [
  { value: "payment", label: "Cobros" },
  { value: "invoice", label: "Facturas" },
  { value: "credit_note", label: "Notas de crédito" },
  { value: "credit_note_reversal", label: "Reversiones NC" },
];

export default function PaymentsMovementsPage() {
  const [search, setSearch] = useState("");
  const [selectedKinds, setSelectedKinds] = useState(KIND_OPTIONS.map((k) => k.value));
  const debouncedSearch = useDebounce(search, 300);

  const {
    items, totalCount, totalPages, page, pageSize, setPage, setPageSize,
    loading, error, reload,
  } = useMovements({
    search: debouncedSearch.trim() || null,
    kinds: selectedKinds.length === KIND_OPTIONS.length ? null : selectedKinds,
  });

  const toggleKind = (value) => {
    setSelectedKinds((current) =>
      current.includes(value) ? current.filter((k) => k !== value) : [...current, value]
    );
  };

  return (
    <div className="space-y-6">
      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800 lg:flex-row lg:items-center lg:justify-between">
          <div className="relative w-full lg:max-w-md">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <input
              type="text"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Buscar por reserva, cliente, referencia…"
              className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white pl-9 pr-3 py-1.5 text-sm"
            />
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {KIND_OPTIONS.map((option) => {
              const active = selectedKinds.includes(option.value);
              return (
                <button
                  key={option.value}
                  type="button"
                  onClick={() => toggleKind(option.value)}
                  className={`rounded-full px-3 py-1 text-xs font-semibold transition-colors ${active ? "bg-indigo-600 text-white" : "bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"}`}
                >
                  {option.label}
                </button>
              );
            })}
            <button
              type="button"
              onClick={reload}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800"
            >
              <RefreshCw className="h-3 w-3" />
              Refrescar
            </button>
          </div>
        </div>

        {error ? (
          <div className="m-6 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-300">
            No se pudieron cargar los movimientos.
          </div>
        ) : (
          <>
            <MovementsTimeline items={items} loading={loading} />
            <div className="border-t border-slate-100 dark:border-slate-800">
              <PaginationFooter
                page={page}
                pageSize={pageSize}
                totalCount={totalCount}
                totalPages={totalPages}
                hasPreviousPage={page > 1}
                hasNextPage={page < totalPages}
                onPageChange={setPage}
                onPageSizeChange={setPageSize}
              />
            </div>
          </>
        )}
      </div>
    </div>
  );
}
