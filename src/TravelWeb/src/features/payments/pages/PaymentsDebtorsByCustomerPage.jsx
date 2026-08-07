/**
 * Solapa "Deuda por cliente" de Cobranzas (spec firmada 2026-08-06, §4.3).
 *
 * Lista PASIVA de los clientes que deben (cruzando TODAS sus reservas): la fila lleva
 * a la ficha del cliente que ya existe (extracto firmado 2026-07-16), no se duplica
 * ninguna pantalla acá. El que no debe nada no aparece.
 *
 * Orden por primera salida (D2, spec 2026-08-06 §7): con pesos y dólares mezclados no
 * existe un "mayor" sin sumar monedas (prohibido, P-3), así que ordenamos por la salida
 * más próxima — mismo criterio que ya usa "Viajan pronto y deben".
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

export default function PaymentsDebtorsByCustomerPage() {
    const [search, setSearch] = useState("");
    const debouncedSearch = useDebounce(search, 300);
    const [data, setData] = useState({ items: [], totalsDebt: [] });
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);

    const cargar = useCallback(async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const params = new URLSearchParams();
            if (debouncedSearch.trim()) params.set("search", debouncedSearch.trim());
            const response = await api.get(`/payments/debtors-by-customer?${params.toString()}`);
            setData({ items: response?.items || [], totalsDebt: response?.totalsDebt || [] });
        } catch {
            setData({ items: [], totalsDebt: [] });
            setLoadError(true);
        } finally {
            setLoading(false);
        }
    }, [debouncedSearch]);

    useEffect(() => {
        cargar();
    }, [cargar]);

    // Fix 2026-08-07: con la lista vacía, formatMontosPorMoneda([]) cae a "$ 0,00" — un
    // total que no significa nada ("nadie debe" no es lo mismo que "deben cero pesos").
    // Solo mostramos la franja de totales cuando hay al menos un cliente en la lista.
    const hayDeudores = !loading && !loadError && data.items.length > 0;

    return (
        <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            <div className="flex flex-col gap-3 border-b border-slate-100 px-6 py-4 dark:border-slate-800 sm:flex-row sm:items-center sm:justify-between">
                <div className="text-sm font-semibold text-slate-700 dark:text-slate-200">
                    {hayDeudores ? `Te deben: ${formatMontosPorMoneda(data.totalsDebt)}` : null}
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
                    <ListLoadErrorState message="No se pudo cargar la deuda por cliente." onRetry={cargar} />
                </div>
            ) : data.items.length === 0 ? (
                <ListEmptyState title="Ningún cliente tiene saldo pendiente." />
            ) : (
                <div className="divide-y divide-slate-100 dark:divide-slate-800">
                    {data.items.map((item, index) => (
                        <RowCliente key={item.customerPublicId || `sin-cliente-${index}`} item={item} />
                    ))}
                </div>
            )}
        </div>
    );
}

// Extraído aparte solo para que el JSX principal no se llene de condicionales: un
// cliente "consumidor final" (sin ficha propia) no tiene a dónde linkear, así que esa
// fila queda sin navegación en vez de romper con un link a "undefined".
function RowCliente({ item }) {
    const contenido = (
        <>
            <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                    {item.hasPastDue && (
                        <>
                            {/* El color solo no alcanza (WCAG 1.4.1): un lector de pantalla
                                no "ve" el punto rojo, así que agregamos el texto aparte. */}
                            <span className="h-2 w-2 shrink-0 rounded-full bg-rose-500" aria-hidden="true" />
                            <span className="sr-only">Vencido</span>
                        </>
                    )}
                    <span className="truncate font-semibold text-slate-900 dark:text-white">{item.customerName}</span>
                </div>
                <div className="text-xs text-slate-500 dark:text-slate-400">
                    {item.reservationsWithDebt} reserva{item.reservationsWithDebt === 1 ? "" : "s"} con deuda
                </div>
            </div>
            <div className="sm:w-48 sm:shrink-0 sm:text-right">
                <div className="text-xs uppercase tracking-wider text-slate-400">Debe</div>
                <div className="text-sm font-bold text-slate-900 dark:text-white">{formatMontosPorMoneda(item.debt)}</div>
            </div>
            <div className="sm:w-32 sm:shrink-0 sm:text-right">
                <div className="text-xs uppercase tracking-wider text-slate-400">Primera salida</div>
                {/* Fix 2026-08-07: la maqueta pide la FECHA (dd/mm/aaaa), no la cuenta
                    regresiva — esa ya se usa en "Viajan pronto y deben". */}
                <div className="text-sm text-slate-600 dark:text-slate-300">{formatDate(item.firstDeparture)}</div>
            </div>
        </>
    );

    if (!item.customerPublicId) {
        return (
            <div
                className="flex flex-col gap-2 px-6 py-4 sm:flex-row sm:items-center sm:justify-between"
                data-testid="debtor-row"
                data-past-due={item.hasPastDue ? "true" : "false"}
            >
                {contenido}
            </div>
        );
    }

    return (
        <Link
            to={`/customers/${item.customerPublicId}/account`}
            className="flex flex-col gap-2 px-6 py-4 hover:bg-slate-50 dark:hover:bg-slate-800/50 sm:flex-row sm:items-center sm:justify-between"
            data-testid="debtor-row"
            data-past-due={item.hasPastDue ? "true" : "false"}
        >
            {contenido}
        </Link>
    );
}
