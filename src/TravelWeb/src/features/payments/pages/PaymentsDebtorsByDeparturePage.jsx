/**
 * Solapa "Viajan pronto y deben" de Cobranzas (spec firmada 2026-08-06, §4.2).
 *
 * Lista PASIVA (2026-07-08): la fila entera lleva a la ficha de la reserva, sin
 * botones de cobrar acá. Están TODOS los que deben, ordenados por fecha de salida
 * (lo calcula y ordena el motor — el front no resta fechas ni ordena por su cuenta).
 */
import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Search } from "lucide-react";
import { api } from "../../../api";
import { useDebounce } from "../../../hooks/useDebounce";
import { formatDate } from "../../../lib/utils";
import { formatMontosPorMoneda } from "../../reservas/lib/reservaMoneyDisplay";
import { SkeletonTableRow } from "../../../components/ui/skeleton";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListLoadErrorState } from "../../../components/ui/ListLoadErrorState";

export default function PaymentsDebtorsByDeparturePage() {
    const [search, setSearch] = useState("");
    const debouncedSearch = useDebounce(search, 300);
    const [data, setData] = useState({ items: [], totalsPending: [] });
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);

    const cargar = useCallback(async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const params = new URLSearchParams();
            if (debouncedSearch.trim()) params.set("search", debouncedSearch.trim());
            const response = await api.get(`/payments/debtors-by-departure?${params.toString()}`);
            setData({ items: response?.items || [], totalsPending: response?.totalsPending || [] });
        } catch {
            setData({ items: [], totalsPending: [] });
            setLoadError(true);
        } finally {
            setLoading(false);
        }
    }, [debouncedSearch]);

    useEffect(() => {
        cargar();
    }, [cargar]);

    // Fix 2026-08-07: con la lista vacía, formatMontosPorMoneda([]) cae a "$ 0,00" — un
    // total que no significa nada ("nadie debe" no es lo mismo que "falta cobrar $0").
    // Solo mostramos la franja de totales cuando hay al menos una reserva en la lista.
    const hayDeudores = !loading && !loadError && data.items.length > 0;

    return (
        <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800 sm:flex-row sm:items-center sm:justify-between">
                <div className="text-sm font-semibold text-slate-700 dark:text-slate-200">
                    {hayDeudores ? `Falta cobrar: ${formatMontosPorMoneda(data.totalsPending)}` : null}
                </div>
                <div className="relative w-full sm:max-w-xs">
                    <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                    <input
                        type="text"
                        value={search}
                        onChange={(event) => setSearch(event.target.value)}
                        placeholder="Buscar cliente…"
                        className="w-full rounded-lg border border-slate-200 bg-white py-2 pl-10 pr-3 text-sm dark:border-slate-700 dark:bg-slate-950 dark:text-white"
                    />
                </div>
            </div>

            {loading ? (
                // Renglones sueltos: este contenedor ya es una tarjeta (rounded-2xl border
                // arriba). <SkeletonTable> trae la suya propia — tarjeta-adentro-de-tarjeta.
                Array.from({ length: 5 }).map((_, index) => <SkeletonTableRow key={index} cols={4} />)
            ) : loadError ? (
                <div className="p-6">
                    <ListLoadErrorState message="No se pudieron cargar las reservas con deuda." onRetry={cargar} />
                </div>
            ) : data.items.length === 0 ? (
                <ListEmptyState title="Ninguna reserva que salga pronto tiene saldo pendiente." />
            ) : (
                <div className="divide-y divide-slate-100 dark:divide-slate-800">
                    {data.items.map((item) => (
                        <Link
                            key={item.reservaPublicId}
                            to={`/reservas/${item.reservaPublicId}`}
                            className={`flex flex-col gap-2 px-6 py-4 hover:bg-slate-50 dark:hover:bg-slate-800/50 sm:flex-row sm:items-center sm:justify-between ${item.isPastDue ? "bg-rose-50/60 dark:bg-rose-950/10" : ""}`}
                            data-testid="debtor-row"
                            data-past-due={item.isPastDue ? "true" : "false"}
                        >
                            <div className="min-w-0 sm:w-40 sm:shrink-0">
                                <div className={`text-sm font-semibold ${item.isPastDue ? "text-rose-600 dark:text-rose-400" : "text-slate-700 dark:text-slate-200"}`}>
                                    {item.departureCountdownText || "—"}
                                </div>
                                <div className="text-xs text-slate-400">{formatDate(item.departureDate)}</div>
                            </div>
                            <div className="min-w-0 flex-1">
                                <div className="truncate font-semibold text-slate-900 dark:text-white">
                                    {item.numeroReserva} · {item.reservaName}
                                </div>
                                <div className="truncate text-xs text-slate-500 dark:text-slate-400">{item.customerName}</div>
                                {item.isPastDue && item.pastDueText && (
                                    <div className="mt-0.5 text-xs font-semibold text-rose-600 dark:text-rose-400">{item.pastDueText}</div>
                                )}
                            </div>
                            <div className="sm:w-40 sm:shrink-0 sm:text-right">
                                <div className="text-xs uppercase tracking-wider text-slate-400">Total</div>
                                <div className="text-sm font-medium text-slate-700 dark:text-slate-200">{formatMontosPorMoneda(item.total)}</div>
                            </div>
                            <div className="sm:w-40 sm:shrink-0 sm:text-right">
                                <div className="text-xs uppercase tracking-wider text-slate-400">Falta</div>
                                <div className="text-sm font-bold text-slate-900 dark:text-white">{formatMontosPorMoneda(item.pending)}</div>
                            </div>
                        </Link>
                    ))}
                </div>
            )}
        </div>
    );
}
