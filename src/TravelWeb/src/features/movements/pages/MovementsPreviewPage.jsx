import { useState } from "react";
import { Activity, RefreshCw } from "lucide-react";
import { useMovements } from "../hooks/useMovements";
import MovementsTimeline from "../components/MovementsTimeline";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";

// B1.15 Fase D' (2026-05-11): pantalla preview de la vista unificada de
// movimientos. NO reemplaza nada del flow actual — coexiste para smoke
// aislado del endpoint + componente nuevos. Eventualmente sera la base de
// Cobranza y Facturacion v2 (Fase D'.B).
const KIND_OPTIONS = [
  { value: "payment", label: "Cobros" },
  { value: "invoice", label: "Facturas" },
  { value: "credit_note", label: "Notas de crédito" },
  { value: "credit_note_reversal", label: "Reversiones NC" },
];

export default function MovementsPreviewPage() {
  const [search, setSearch] = useState("");
  const [selectedKinds, setSelectedKinds] = useState(KIND_OPTIONS.map((k) => k.value));

  const {
    items,
    totalCount,
    totalPages,
    page,
    pageSize,
    setPage,
    setPageSize,
    loading,
    error,
    reload,
  } = useMovements({
    search: search.trim() || null,
    kinds: selectedKinds.length === KIND_OPTIONS.length ? null : selectedKinds,
  });

  const toggleKind = (value) => {
    setSelectedKinds((current) =>
      current.includes(value) ? current.filter((k) => k !== value) : [...current, value]
    );
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-indigo-100 p-2 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
            <Activity className="h-5 w-5" />
          </div>
          <div>
            <h1 className="text-2xl font-bold text-slate-900 dark:text-white">Movimientos</h1>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Vista cronológica unificada de cobros, facturas, NCs y reversiones (preview Fase D').
            </p>
          </div>
        </div>
        <button
          type="button"
          onClick={reload}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 dark:border-slate-600 px-3 py-1.5 text-xs font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800"
        >
          <RefreshCw className="h-3.5 w-3.5" />
          Refrescar
        </button>
      </div>

      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800 lg:flex-row lg:items-center lg:justify-between">
          <input
            type="text"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            placeholder="Buscar por reserva, cliente, referencia…"
            className="w-full lg:max-w-md rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-1.5 text-sm"
          />
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
          </div>
        </div>

        {error ? (
          <div className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-900/20 px-4 py-3 m-6 text-sm text-rose-700 dark:text-rose-300">
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
