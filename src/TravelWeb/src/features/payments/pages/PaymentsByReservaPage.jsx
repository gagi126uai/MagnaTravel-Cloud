import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { ChevronDown, ChevronRight, ExternalLink, Loader2, Search } from "lucide-react";
import { api } from "../../../api";
import { useDebounce } from "../../../hooks/useDebounce";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { MonthNavigator, monthToBounds } from "../../../components/ui/MonthNavigator";
import { useMovements } from "../../movements/hooks/useMovements";
import MovementsTimeline from "../../movements/components/MovementsTimeline";
import { formatDate } from "../../../lib/utils";

// B1.15 Fase D'.B (2026-05-11): pestaña "Por reserva" de Cobranza y Facturación.
// Lista reservas con saldo financiero y expand inline → MovementsTimeline
// filtrado por reservaId. Para Vendedor: solo sus reservas (filter mine del
// endpoint /api/reservas). Para Admin/Colaborador: todas.
//
// Filtro de mes: patron canónico del módulo financiero (igual que Facturación).
// Default: mes actual. El mes filtra por createdFrom/createdTo en /api/reservas.
// Si activeMonth es null, no se envían filtros de fecha (historial completo).

const STATUS_FILTER_OPTIONS = [
  { value: "active", label: "Activas" },
  { value: "all", label: "Todas" },
  { value: "settled", label: "Pagadas" },
  { value: "overdue", label: "Con deuda vencida" },
];


export default function PaymentsByReservaPage() {
  const [search, setSearch] = useState("");
  const [view, setView] = useState("active");
  const [page, setPage] = useState(1);
  const [pageSize] = useState(25);

  // Filtro de mes — default: mes actual.
  // Backend acepta CreatedFrom/CreatedTo en /api/reservas (formato ISO YYYY-MM-DD).
  // null = sin filtro de fecha (historial completo).
  const [activeMonth, setActiveMonth] = useState(() => {
    const now = new Date();
    return new Date(now.getFullYear(), now.getMonth(), 1);
  });

  const debouncedSearch = useDebounce(search, 300);

  // Cuando activeMonth es null no se envían filtros de fecha al backend.
  const dateBounds = activeMonth ? monthToBounds(activeMonth) : null;
  const createdFrom = dateBounds?.from ?? null;
  const createdTo = dateBounds?.to ?? null;

  const [data, setData] = useState({ items: [], totalCount: 0, totalPages: 0 });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, view, createdFrom, createdTo]);

  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      setLoading(true);
      setError(null);
      try {
        const params = new URLSearchParams();
        params.set("view", view);
        if (debouncedSearch.trim()) params.set("search", debouncedSearch.trim());
        if (createdFrom) params.set("createdFrom", createdFrom);
        if (createdTo) params.set("createdTo", createdTo);
        params.set("page", String(page));
        params.set("pageSize", String(pageSize));
        const response = await api.get(`/reservas?${params.toString()}`);
        if (!cancelled) {
          setData({
            items: Array.isArray(response?.items) ? response.items : [],
            totalCount: response?.totalCount ?? 0,
            totalPages: response?.totalPages ?? 0,
          });
        }
      } catch (err) {
        if (!cancelled) setError(err);
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    load();
    return () => { cancelled = true; };
  }, [debouncedSearch, view, createdFrom, createdTo, page, pageSize]);

  // Strip de conteo inferior.
  const monthLabel = activeMonth
    ? activeMonth.toLocaleDateString("es-AR", { month: "long", year: "numeric" })
    : null;

  const handleShowAll = () => setActiveMonth(null);
  const handleBackToCurrentMonth = () => {
    const now = new Date();
    setActiveMonth(new Date(now.getFullYear(), now.getMonth(), 1));
  };

  return (
    <div className="space-y-6">
      <div className="rounded-2xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800">
          <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
            <div className="relative w-full lg:max-w-md">
              <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
              <input
                type="text"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="Buscar por reserva o cliente…"
                className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white pl-9 pr-3 py-1.5 text-sm"
              />
            </div>
            <MonthNavigator
              month={activeMonth}
              onChange={setActiveMonth}
              disabled={loading}
            />
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {STATUS_FILTER_OPTIONS.map((option) => (
              <button
                key={option.value}
                type="button"
                onClick={() => setView(option.value)}
                className={`rounded-full px-3 py-1 text-xs font-semibold transition-colors ${view === option.value ? "bg-indigo-600 text-white" : "bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"}`}
              >
                {option.label}
              </button>
            ))}
          </div>
        </div>

        {loading ? (
          <div className="flex items-center justify-center py-16 text-slate-400">
            <Loader2 className="h-6 w-6 animate-spin" />
          </div>
        ) : error ? (
          <div className="m-6 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-300">
            No se pudieron cargar las reservas.
          </div>
        ) : data.items.length === 0 ? (
          <div className="px-6 py-10 text-center text-sm text-slate-500">No hay reservas para esta vista.</div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {data.items.map((reserva) => (
              <ReservaRow key={reserva.publicId} reserva={reserva} />
            ))}
          </div>
        )}

        <div className="border-t border-slate-100 dark:border-slate-800">
          <PaginationFooter
            page={page}
            pageSize={pageSize}
            totalCount={data.totalCount}
            totalPages={data.totalPages}
            hasPreviousPage={page > 1}
            hasNextPage={page < data.totalPages}
            onPageChange={setPage}
            onPageSizeChange={() => {}}
          />
        </div>
        {/* Strip de conteo + acción historial completo */}
        {!loading && (
          <div className="flex flex-wrap items-center justify-between gap-2 border-t border-slate-100 px-6 py-2 dark:border-slate-800">
            <span className="text-xs text-slate-500 dark:text-slate-400">
              {activeMonth
                ? `Mostrando ${data.totalCount} reserva${data.totalCount !== 1 ? "s" : ""} de ${monthLabel}`
                : `Mostrando ${data.totalCount} reserva${data.totalCount !== 1 ? "s" : ""} en total`}
            </span>
            {activeMonth ? (
              <button
                type="button"
                onClick={handleShowAll}
                className="text-xs text-indigo-600 hover:underline dark:text-indigo-400"
                data-testid="by-reserva-show-all"
              >
                Ver historial completo &rarr;
              </button>
            ) : (
              <button
                type="button"
                onClick={handleBackToCurrentMonth}
                className="rounded-lg bg-indigo-600 px-3 py-1 text-xs font-semibold text-white hover:bg-indigo-700"
                data-testid="by-reserva-back-to-month"
              >
                Volver al mes actual
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function ReservaRow({ reserva }) {
  const [expanded, setExpanded] = useState(false);
  const Chevron = expanded ? ChevronDown : ChevronRight;

  const balanceColor = reserva.balance > 0
    ? "text-rose-600 dark:text-rose-400"
    : reserva.balance < 0
    ? "text-amber-600 dark:text-amber-400"  // sobrepago
    : "text-emerald-600 dark:text-emerald-400";

  return (
    <div>
      <button
        type="button"
        onClick={() => setExpanded((current) => !current)}
        className="flex w-full items-center gap-3 px-6 py-4 text-left hover:bg-slate-50 dark:hover:bg-slate-800/50"
      >
        <Chevron className="h-4 w-4 text-slate-400 flex-shrink-0" />
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="font-semibold text-slate-900 dark:text-white">{reserva.numeroReserva}</span>
            {reserva.isFullyPaid ? (
              <span className="rounded-full bg-emerald-100 px-2 py-0.5 text-[10px] font-black uppercase tracking-wider text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300">
                Pagada
              </span>
            ) : null}
            {reserva.hasOverdueDebt ? (
              <span className="rounded-full bg-rose-100 px-2 py-0.5 text-[10px] font-black uppercase tracking-wider text-rose-700 dark:bg-rose-900/30 dark:text-rose-300">
                Vencida con deuda
              </span>
            ) : null}
          </div>
          <div className="text-xs text-slate-500 dark:text-slate-400 mt-0.5 truncate">
            {/* reserva.startDate es la fecha de salida del viaje: día calendario elegido al
                cargar la reserva, guardado como medianoche UTC — misma familia que PaidAt.
                formatDate() no la corre un día por la zona horaria del navegador. */}
            {reserva.customerName || "Sin cliente"}{reserva.startDate ? ` · Salida ${formatDate(reserva.startDate)}` : ""}
          </div>
        </div>
        <div className="hidden sm:block text-right">
          <div className="text-xs text-slate-400">Venta total</div>
          <div className="text-sm font-medium text-slate-700 dark:text-slate-200">
            {reserva.totalSale?.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 0 })}
          </div>
        </div>
        <div className="text-right">
          <div className="text-xs text-slate-400">Saldo</div>
          <div className={`text-sm font-bold ${balanceColor}`}>
            {reserva.balance?.toLocaleString("es-AR", { style: "currency", currency: "ARS", minimumFractionDigits: 0 })}
          </div>
        </div>
        <Link
          to={`/reservas/${reserva.publicId}`}
          onClick={(event) => event.stopPropagation()}
          className="rounded p-2 text-slate-400 hover:bg-slate-100 hover:text-indigo-600 dark:hover:bg-slate-700 dark:hover:text-indigo-300"
          title="Ver reserva"
        >
          <ExternalLink className="h-4 w-4" />
        </Link>
      </button>

      {expanded ? <ReservaTimeline reservaId={reserva.publicId} /> : null}
    </div>
  );
}

function ReservaTimeline({ reservaId }) {
  const { items, loading, error } = useMovements({ reservaId });

  return (
    <div className="bg-slate-50/60 dark:bg-slate-800/30 border-t border-slate-100 dark:border-slate-800/50">
      {error ? (
        <div className="px-6 py-6 text-sm text-rose-600">No se pudieron cargar los movimientos.</div>
      ) : (
        <MovementsTimeline
          items={items}
          loading={loading}
          showReservaColumn={false}
          emptyText="Sin movimientos en esta reserva."
        />
      )}
    </div>
  );
}
