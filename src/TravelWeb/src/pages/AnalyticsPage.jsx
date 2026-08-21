import { useEffect, useState, useCallback } from "react";
import { LineChart, Line, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid } from "recharts";
import { api } from "../api";
import { showError } from "../alerts";
import { formatCurrency } from "../lib/utils";
import { hasPermission } from "../auth";
import { CurrencyBadge } from "../components/ui/CurrencyBadge";
import { construirLineasKpiPorMoneda } from "../lib/dashboardKpiCurrency";
import { armarSeriesRitmoCobrosPagos } from "../features/dashboard/lib/cashflowRhythmSeries";
import {
    armarRankingVendedoresPorMoneda,
    armarRankingDestinosPorMoneda,
    armarComparativaInteranualPorMoneda,
} from "../lib/analyticsByCurrency";
import {
    TrendingUp, TrendingDown, Users, MapPin, Wallet, BarChart3,
    Calendar, ArrowUpRight, ArrowDownRight, Loader2, RefreshCw,
    Trophy, Target, DollarSign, Activity
} from "lucide-react";

// Mismos colores que la tarjeta "Ritmo de cobros y pagos" del dashboard
// (CashflowRhythmCard.jsx) — un solo criterio de color para cobros/pagos en
// toda la app, no lo reinventamos acá.
const COLOR_COBROS = "#1D4ED8";
const COLOR_PAGOS = "#B45309";

// Funciones puras de formato compartidas entre AnalyticsPage y sus sub-componentes
// (ranking de vendedores, tarjetas de moneda, comparativa interanual) — no dependen
// de props ni de estado, así que viven a nivel de módulo en vez de recrearse en cada render.
const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
const fmtPct = (n) => `${(n || 0).toFixed(1)}%`;

export default function AnalyticsPage() {
    const [sellers, setSellers] = useState([]);
    const [destinations, setDestinations] = useState([]);
    const [cashflow, setCashflow] = useState(null);
    const [yoy, setYoy] = useState(null);
    const [loading, setLoading] = useState(true);
    const [activeTab, setActiveTab] = useState("sellers");

    const loadData = useCallback(async () => {
        setLoading(true);
        try {
            const [sellersRes, destinationsRes, cashflowRes, yoyRes] = await Promise.all([
                api.get("/reports/sellers"),
                api.get("/reports/destinations"),
                api.get("/reports/cashflow?days=90"),
                api.get("/reports/yoy"),
            ]);
            setSellers(sellersRes || []);
            setDestinations(destinationsRes || []);
            setCashflow(cashflowRes);
            setYoy(yoyRes);
        } catch (err) {
            showError("Error al cargar analíticas");
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { loadData(); }, [loadData]);

    // "Pagos a operadores" es informacion de costo (F-14): sin este permiso el backend
    // ya manda CashOutByCurrency vacio para cada dia, y el grafico de flujo de caja
    // omite esa serie entera en vez de graficar un "$0" enganoso.
    const puedeVerCostos = hasPermission("cobranzas.see_cost");

    if (loading) {
        return (
            <div className="flex items-center justify-center h-[60vh]">
                <div className="flex flex-col items-center gap-4">
                    <div className="relative">
                        <div className="absolute inset-0 bg-blue-500/20 rounded-full animate-ping"></div>
                        <div className="relative p-4 bg-blue-600 rounded-full text-white shadow-xl shadow-blue-200 dark:shadow-none">
                            <Loader2 className="w-8 h-8 animate-spin" />
                        </div>
                    </div>
                    <p className="text-sm font-semibold text-slate-500 dark:text-slate-400 tracking-wider uppercase">Cargando Analíticas...</p>
                </div>
            </div>
        );
    }

    // Regla 554 de la guía UX: los rankings/comparativas van separados por moneda,
    // nunca sumados. Estas 3 funciones puras deciden si hay más de una moneda o si
    // la pantalla se ve igual que siempre (una sola moneda) — ver lib/analyticsByCurrency.js.
    const rankingVendedores = armarRankingVendedoresPorMoneda(sellers);
    const rankingDestinos = armarRankingDestinosPorMoneda(destinations);
    const comparativaInteranual = yoy ? armarComparativaInteranualPorMoneda(yoy) : null;

    return (
        <div className="space-y-8 pb-12">

            {/* Summary Cards */}
            {cashflow && yoy && (
                <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
                    {/* P-3: el saldo nunca se suma entre monedas — una línea por moneda con su
                        CurrencyBadge, mismo patrón que MoneyKpiGrid.jsx del dashboard nuevo. Sin
                        flecha de tendencia: con más de una moneda no hay UNA sola dirección para
                        mostrar (podés estar mejorando en pesos y empeorando en dólares a la vez). */}
                    <MoneyByCurrencyCard
                        title="Balance Actual"
                        lineas={construirLineasKpiPorMoneda(cashflow.currentBalanceByCurrency)}
                        icon={Wallet}
                        color="blue"
                    />
                    <MoneyByCurrencyCard
                        title="Proyección 30d"
                        lineas={construirLineasKpiPorMoneda(cashflow.projectedBalance30ByCurrency)}
                        icon={Target}
                        color="emerald"
                    />
                    <MoneyByCurrencyCard
                        title="Proyección 90d"
                        lineas={construirLineasKpiPorMoneda(cashflow.projectedBalance90ByCurrency)}
                        icon={Activity}
                        color="violet"
                    />
                    {/* Regla 554: con más de una moneda, el crecimiento va una línea por moneda
                        (nunca un solo % que mezcle pesos y dólares). Con una sola moneda queda
                        EXACTAMENTE como antes de esta obra. */}
                    {comparativaInteranual?.hayMasDeUnaMoneda ? (
                        <GrowthByCurrencyCard bloques={comparativaInteranual.bloques} />
                    ) : (
                        <SummaryCard
                            title="Crecimiento Interanual"
                            value={fmtPct(yoy.growthPercent)}
                            icon={yoy.growthPercent >= 0 ? TrendingUp : TrendingDown}
                            color={yoy.growthPercent >= 0 ? "emerald" : "rose"}
                            subtitle={`${fmt(yoy.currentYearTotal)} vs ${fmt(yoy.previousYearTotal)}`}
                        />
                    )}
                </div>
            )}

            {/* Tab Navigation */}
            <div className="flex gap-2 bg-slate-100 dark:bg-slate-800/50 p-1 rounded-[10px] w-fit">
                {[
                    { id: "sellers", label: "Vendedores", icon: Users },
                    { id: "destinations", label: "Destinos", icon: MapPin },
                    { id: "cashflow", label: "Flujo de Caja", icon: DollarSign },
                    { id: "yoy", label: "Interanual", icon: BarChart3 },
                ].map(tab => (
                    <button
                        key={tab.id}
                        onClick={() => setActiveTab(tab.id)}
                        className={`flex items-center gap-2 px-4 py-2 rounded-[10px] text-xs font-bold transition-all ${activeTab === tab.id
                                ? "bg-white dark:bg-slate-700 text-slate-900 dark:text-white shadow-sm"
                                : "text-slate-500 hover:text-slate-700 dark:text-slate-400"
                            }`}
                    >
                        <tab.icon className="w-3.5 h-3.5" />
                        <span className="hidden sm:inline">{tab.label}</span>
                    </button>
                ))}
            </div>

            {/* ===== SELLERS TAB ===== */}
            {activeTab === "sellers" && (
                <div className="animate-in fade-in duration-300 space-y-6">
                    <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                        <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800 flex items-center justify-between">
                            <div className="flex items-center gap-3">
                                <div className="p-2 rounded-[10px] bg-amber-50 dark:bg-amber-900/20 text-amber-600">
                                    <Trophy className="w-5 h-5" />
                                </div>
                                <div>
                                    <h2 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider">Ranking de Vendedores</h2>
                                    <p className="text-xs text-slate-400 mt-0.5">Ordenado por volumen de ventas</p>
                                </div>
                            </div>
                        </div>
                        <div className="divide-y divide-slate-50 dark:divide-slate-800/50">
                            {sellers.length === 0 ? (
                                <div className="px-6 py-12 text-center text-sm text-slate-400">No hay datos de vendedores disponibles.</div>
                            ) : (
                                // Regla 554: con más de una moneda, un bloque de ranking por moneda (con
                                // su título arriba); con una sola moneda, un único bloque sin título —
                                // se ve exactamente como antes de esta obra.
                                rankingVendedores.bloques.map((bloque) => (
                                    <div key={bloque.currency}>
                                        {rankingVendedores.hayMasDeUnaMoneda && (
                                            <div className="px-6 pt-4 pb-1 flex items-center gap-1.5">
                                                <CurrencyBadge currency={bloque.currency} size="sm" />
                                            </div>
                                        )}
                                        {bloque.vendedores.map((v, idx) => (
                                            <div key={v.userId} className="px-6 py-4 flex items-center gap-4 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                                                <div className={`w-8 h-8 rounded-full flex items-center justify-center text-xs font-black text-white ${idx === 0 ? "bg-amber-500" : idx === 1 ? "bg-slate-400" : idx === 2 ? "bg-orange-600" : "bg-slate-300"
                                                    }`}>
                                                    {idx + 1}
                                                </div>
                                                <div className="flex-1 min-w-0">
                                                    <div className="flex items-center justify-between mb-1">
                                                        <span className="text-sm font-bold text-slate-900 dark:text-white truncate">{v.sellerName}</span>
                                                        {/* Multi-moneda: el monto usa el símbolo real de SU moneda (US$/$) — con una
                                                            sola moneda queda el mismo formato genérico de siempre (fmt), sin tocar. */}
                                                        <span className="text-sm font-black text-slate-900 dark:text-white ml-2">
                                                            {rankingVendedores.hayMasDeUnaMoneda ? formatCurrency(v.monto, bloque.currency) : fmt(v.monto)}
                                                        </span>
                                                    </div>
                                                    <div className="flex items-center gap-3">
                                                        <div className="flex-1 h-2 bg-slate-100 dark:bg-slate-800 rounded-full overflow-hidden">
                                                            <div
                                                                className="h-full bg-gradient-to-r from-blue-500 to-violet-500 rounded-full transition-all duration-700"
                                                                style={{ width: `${(v.monto / bloque.maxMonto) * 100}%` }}
                                                            ></div>
                                                        </div>
                                                        {/* filesCreated es un conteo global (todas las monedas): con más de una
                                                            moneda la lib ya lo manda null (bloqueante de review, no se repite en
                                                            cada bloque de moneda para no contarlo doble). */}
                                                        {v.filesCreated != null && (
                                                            <span className="text-[11px] font-bold text-slate-400 w-16 text-right">{v.filesCreated} files</span>
                                                        )}
                                                        {/* F-14: sin permiso de costo el backend enmascara el margen (lista vacía por
                                                            moneda, o el escalar legacy en 0 con una sola moneda) — se oculta el badge
                                                            en vez de mostrar un "0%" que podría confundirse con un dato real. */}
                                                        {puedeVerCostos && v.margenPercent != null && (
                                                            <span className={`text-[11px] font-black px-1.5 py-0.5 rounded ${v.margenPercent > 15 ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20" : "bg-amber-50 text-amber-600 dark:bg-amber-900/20"
                                                                }`}>
                                                                {fmtPct(v.margenPercent)} mrg
                                                            </span>
                                                        )}
                                                    </div>
                                                </div>
                                            </div>
                                        ))}
                                    </div>
                                ))
                            )}
                        </div>
                    </div>
                </div>
            )}

            {/* ===== DESTINATIONS TAB ===== */}
            {activeTab === "destinations" && (
                <div className="animate-in fade-in duration-300 space-y-6">
                    <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                        <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3">
                            <div className="p-2 rounded-[10px] bg-sky-50 dark:bg-sky-900/20 text-sky-600">
                                <MapPin className="w-5 h-5" />
                            </div>
                            <div>
                                <h2 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider">Destinos Más Populares</h2>
                                <p className="text-xs text-slate-400 mt-0.5">Agrupados por hotel, paquete y aéreo</p>
                            </div>
                        </div>
                        {destinations.length === 0 ? (
                            <div className="p-6 text-center text-sm text-slate-400">No hay datos de destinos disponibles.</div>
                        ) : (
                            // Regla 554: con más de una moneda, una grilla de destinos por moneda (con
                            // su título arriba); con una sola moneda, una única grilla sin título.
                            rankingDestinos.bloques.map((bloque) => (
                                <div key={bloque.currency} className="p-6">
                                    {rankingDestinos.hayMasDeUnaMoneda && (
                                        <div className="mb-3 flex items-center gap-1.5">
                                            <CurrencyBadge currency={bloque.currency} size="sm" />
                                        </div>
                                    )}
                                    <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                                        {bloque.destinos.map((d, idx) => (
                                            <div key={d.destination} className="group relative bg-gradient-to-br from-slate-50 to-white dark:from-slate-800/50 dark:to-slate-900 rounded-[10px] p-5 border border-slate-100 dark:border-slate-800 hover:shadow-md hover:border-blue-200 dark:hover:border-blue-800 transition-all">
                                                {idx < 3 && (
                                                    <div className="absolute -top-2 -right-2 w-6 h-6 rounded-full bg-blue-600 text-white text-[11px] font-black flex items-center justify-center shadow-lg">
                                                        {idx + 1}
                                                    </div>
                                                )}
                                                <div className="text-lg font-black text-slate-900 dark:text-white mb-3 capitalize">
                                                    {d.destination.toLowerCase()}
                                                </div>
                                                <div className="h-2 bg-slate-100 dark:bg-slate-700 rounded-full overflow-hidden mb-4">
                                                    <div
                                                        className="h-full bg-gradient-to-r from-sky-400 to-blue-500 rounded-full transition-all duration-700"
                                                        style={{ width: `${(d.monto / bloque.maxMonto) * 100}%` }}
                                                    ></div>
                                                </div>
                                                <div className="grid grid-cols-2 gap-2 text-xs">
                                                    <div>
                                                        <span className="text-slate-400 block">Revenue</span>
                                                        {/* Multi-moneda: símbolo real de SU moneda (US$/$) — una sola moneda queda
                                                            con el mismo formato genérico de siempre (fmt), sin tocar. */}
                                                        <span className="font-black text-slate-900 dark:text-white">
                                                            {rankingDestinos.hayMasDeUnaMoneda ? formatCurrency(d.monto, bloque.currency) : fmt(d.monto)}
                                                        </span>
                                                    </div>
                                                    {/* F-14: sin permiso de costo, margenMonto queda null (lista vacía por moneda,
                                                        o legacy en 0 con una sola moneda) — se oculta en vez de mostrar "$0". */}
                                                    {puedeVerCostos && d.margenMonto != null && (
                                                        <div>
                                                            <span className="text-slate-400 block">Margen</span>
                                                            <span className="font-black text-emerald-600">
                                                                {rankingDestinos.hayMasDeUnaMoneda ? formatCurrency(d.margenMonto, bloque.currency) : fmt(d.margenMonto)}
                                                            </span>
                                                        </div>
                                                    )}
                                                    {/* bookingCount/passengerCount son conteos globales (todas las monedas): con
                                                        más de una moneda la lib ya los manda null, mismo motivo que filesCreated
                                                        en vendedores (no contar el mismo booking dos veces). */}
                                                    {d.bookingCount != null && (
                                                        <div>
                                                            <span className="text-slate-400 block">Bookings</span>
                                                            <span className="font-bold text-slate-700 dark:text-slate-300">{d.bookingCount}</span>
                                                        </div>
                                                    )}
                                                    {d.passengerCount != null && (
                                                        <div>
                                                            <span className="text-slate-400 block">Pasajeros</span>
                                                            <span className="font-bold text-slate-700 dark:text-slate-300">{d.passengerCount}</span>
                                                        </div>
                                                    )}
                                                </div>
                                            </div>
                                        ))}
                                    </div>
                                </div>
                            ))
                        )}
                    </div>
                </div>
            )}

            {/* ===== CASHFLOW TAB (por moneda, P-3) ===== */}
            {activeTab === "cashflow" && cashflow && (
                <div className="animate-in fade-in duration-300 space-y-6">
                    {/* Projection KPIs — una línea por moneda, nunca un solo número sumado */}
                    <div className="grid grid-cols-3 gap-4">
                        {[
                            { label: "30 días", lineas: construirLineasKpiPorMoneda(cashflow.projectedBalance30ByCurrency) },
                            { label: "60 días", lineas: construirLineasKpiPorMoneda(cashflow.projectedBalance60ByCurrency) },
                            { label: "90 días", lineas: construirLineasKpiPorMoneda(cashflow.projectedBalance90ByCurrency) },
                        ].map(p => (
                            <div key={p.label} className="bg-white dark:bg-slate-900 rounded-[10px] border border-slate-200 dark:border-slate-800 p-5 text-center">
                                <div className="text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">Proyección {p.label}</div>
                                <div className="space-y-1">
                                    {p.lineas.map((linea) => (
                                        <div key={linea.currency} className="flex items-center justify-center gap-1.5">
                                            <CurrencyBadge currency={linea.currency} size="sm" />
                                            <span className={`text-xl font-black ${linea.monto >= 0 ? "text-emerald-600" : "text-rose-600"}`}>
                                                {formatCurrency(linea.monto, linea.currency, { withSymbol: false })}
                                            </span>
                                        </div>
                                    ))}
                                </div>
                            </div>
                        ))}
                    </div>

                    {/* Cobros y pagos por moneda: mismo patrón visual que la tarjeta "Ritmo de
                        cobros y pagos" del dashboard (CashflowRhythmCard.jsx), reusando su misma
                        lib de series (cashflowRhythmSeries.js) — 30 días reales + tendencia a 90. */}
                    <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 p-6 shadow-sm">
                        <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider mb-1 flex items-center gap-2">
                            <Activity className="w-4 h-4 text-blue-500" />
                            Flujo de Caja
                        </h3>
                        <p className="text-xs text-slate-400 mb-6">Últimos 30 días reales + tendencia a 90 días, por moneda</p>
                        <CashflowByCurrencyChart cashflow={cashflow} puedeVerCostos={puedeVerCostos} />
                    </div>
                </div>
            )}

            {/* ===== YOY TAB ===== */}
            {activeTab === "yoy" && yoy && comparativaInteranual && (
                <div className="animate-in fade-in duration-300 space-y-6">
                    {/* Regla 554: con más de una moneda, una tarjeta de comparativa por moneda (con
                        su título arriba, mismo tratamiento que recibió el gráfico de Flujo de Caja
                        el 19/08); con una sola moneda queda una única tarjeta, sin título. */}
                    {comparativaInteranual.bloques.map((bloque) => (
                        <YoyBlockCard
                            key={bloque.currency}
                            bloque={bloque}
                            mostrarTituloMoneda={comparativaInteranual.hayMasDeUnaMoneda}
                        />
                    ))}
                </div>
            )}
        </div>
    );
}

function SummaryCard({ title, value, icon: Icon, color, trend, subtitle }) {
    const colorMap = {
        blue: "bg-blue-50 text-blue-600 dark:bg-blue-900/20 dark:text-blue-400",
        emerald: "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400",
        violet: "bg-violet-50 text-violet-600 dark:bg-violet-900/20 dark:text-violet-400",
        rose: "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400",
    };

    return (
        <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 p-5 shadow-sm hover:shadow-md transition-shadow">
            <div className="flex items-center justify-between mb-3">
                <div className={`p-2 rounded-[10px] ${colorMap[color]}`}>
                    <Icon className="w-5 h-5" />
                </div>
                {trend && (
                    <div className={`flex items-center gap-0.5 text-xs font-black ${trend === "up" ? "text-emerald-500" : "text-rose-500"}`}>
                        {trend === "up" ? <ArrowUpRight className="w-3.5 h-3.5" /> : <ArrowDownRight className="w-3.5 h-3.5" />}
                    </div>
                )}
            </div>
            <div className="text-xl font-black text-slate-900 dark:text-white">{value}</div>
            <div className="text-[11px] font-bold text-slate-400 uppercase tracking-wider mt-1">{title}</div>
            {subtitle && <div className="text-[11px] text-slate-400 mt-0.5">{subtitle}</div>}
        </div>
    );
}

/**
 * Tarjeta "Crecimiento Interanual" cuando hay MÁS DE UNA moneda (regla 554): una
 * línea por moneda, cada una con su propia flecha/color y su "$actual vs $anterior"
 * — nunca un solo % que mezcle el crecimiento en pesos con el crecimiento en dólares.
 * `bloques` viene de `armarComparativaInteranualPorMoneda` (lib/analyticsByCurrency.js).
 */
function GrowthByCurrencyCard({ bloques }) {
    return (
        <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 p-5 shadow-sm hover:shadow-md transition-shadow">
            <div className="space-y-2">
                {bloques.map((bloque) => {
                    const esPositivo = bloque.crecimientoPercent >= 0;
                    const Icon = esPositivo ? TrendingUp : TrendingDown;
                    const colorTexto = esPositivo ? "text-emerald-600" : "text-rose-600";
                    return (
                        <div key={bloque.currency} className="flex items-center gap-2">
                            <div className={`p-1.5 rounded-[8px] ${esPositivo ? "bg-emerald-50 dark:bg-emerald-900/20" : "bg-rose-50 dark:bg-rose-900/20"}`}>
                                <Icon className={`w-4 h-4 ${colorTexto}`} />
                            </div>
                            <div>
                                <div className="flex items-center gap-1.5">
                                    <CurrencyBadge currency={bloque.currency} size="sm" />
                                    <span className={`text-lg font-black ${colorTexto}`}>{fmtPct(bloque.crecimientoPercent)}</span>
                                </div>
                                {/* Este componente SOLO se usa en el camino multi-moneda (ver AnalyticsPage,
                                    el caso de una sola moneda sigue usando SummaryCard con fmt) — acá el
                                    símbolo real de la moneda ($/US$) es obligatorio, nunca el "$" genérico. */}
                                <div className="text-[11px] text-slate-400">
                                    {formatCurrency(bloque.totalActual, bloque.currency)} vs {formatCurrency(bloque.totalAnterior, bloque.currency)}
                                </div>
                            </div>
                        </div>
                    );
                })}
            </div>
            <div className="text-[11px] font-bold text-slate-400 uppercase tracking-wider mt-2">Crecimiento Interanual</div>
        </div>
    );
}

/**
 * Tarjeta de la solapa "Interanual": comparativa mes a mes de UNA moneda contra el
 * año anterior. Con más de una moneda, `AnalyticsPage` renderiza una de estas por
 * cada moneda (`mostrarTituloMoneda=true` agrega el `CurrencyBadge` como título);
 * con una sola moneda se renderiza una única vez, sin título — igual que antes de
 * esta obra. `bloque` viene de `armarComparativaInteranualPorMoneda`.
 */
function YoyBlockCard({ bloque, mostrarTituloMoneda }) {
    const esPositivo = bloque.crecimientoPercent >= 0;
    const anioActual = new Date().getFullYear();
    const anioAnterior = anioActual - 1;
    // Con más de una moneda hay que usar el símbolo real (US$/$) porque el bloque
    // puede ser dólares; con una sola moneda se preserva el "$" genérico de siempre.
    const formatearMonto = (monto) => (mostrarTituloMoneda ? formatCurrency(monto, bloque.currency) : fmt(monto));

    return (
        <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 p-6 shadow-sm">
            <div className="flex items-center justify-between mb-6">
                <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider flex items-center gap-2">
                    <BarChart3 className="w-4 h-4 text-violet-500" />
                    Comparativa Interanual
                    {mostrarTituloMoneda && <CurrencyBadge currency={bloque.currency} size="sm" />}
                </h3>
                <div className={`flex items-center gap-1 text-sm font-black px-3 py-1 rounded-full ${esPositivo
                        ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20"
                        : "bg-rose-50 text-rose-600 dark:bg-rose-900/20"
                    }`}>
                    {esPositivo ? <ArrowUpRight className="w-4 h-4" /> : <ArrowDownRight className="w-4 h-4" />}
                    {fmtPct(Math.abs(bloque.crecimientoPercent))}
                </div>
            </div>

            {/* Totals */}
            <div className="grid grid-cols-2 gap-4 mb-6">
                <div className="bg-blue-50 dark:bg-blue-900/10 rounded-[10px] p-4">
                    <div className="text-[11px] font-bold text-blue-400 uppercase tracking-wider">{anioActual}</div>
                    <div className="text-xl font-black text-blue-600 dark:text-blue-400">{formatearMonto(bloque.totalActual)}</div>
                </div>
                <div className="bg-slate-50 dark:bg-slate-800/50 rounded-[10px] p-4">
                    <div className="text-[11px] font-bold text-slate-400 uppercase tracking-wider">{anioAnterior}</div>
                    <div className="text-xl font-black text-slate-600 dark:text-slate-400">{formatearMonto(bloque.totalAnterior)}</div>
                </div>
            </div>

            {/* Monthly bars */}
            <div className="space-y-3">
                {bloque.meses.map((mes) => {
                    const currPct = bloque.maxMonto > 0 ? (mes.actual / bloque.maxMonto) * 100 : 0;
                    const prevPct = bloque.maxMonto > 0 ? (mes.anterior / bloque.maxMonto) * 100 : 0;
                    const monthGrowth = mes.anterior > 0 ? ((mes.actual - mes.anterior) / mes.anterior) * 100 : 0;
                    return (
                        <div key={mes.month} className="group">
                            <div className="flex items-center gap-3">
                                <span className="text-[11px] font-black text-slate-400 w-8 text-right uppercase">{mes.month}</span>
                                <div className="flex-1 space-y-1">
                                    <div className="flex items-center gap-2">
                                        <div className="flex-1 h-3 bg-slate-100 dark:bg-slate-800 rounded-full overflow-hidden">
                                            <div
                                                className="h-full bg-gradient-to-r from-blue-500 to-violet-500 rounded-full transition-all duration-700"
                                                style={{ width: `${currPct}%` }}
                                            ></div>
                                        </div>
                                        <span className="text-[11px] font-bold text-slate-500 w-20 text-right">{formatearMonto(mes.actual)}</span>
                                    </div>
                                    <div className="flex items-center gap-2">
                                        <div className="flex-1 h-2 bg-slate-100 dark:bg-slate-800 rounded-full overflow-hidden">
                                            <div
                                                className="h-full bg-slate-300 dark:bg-slate-600 rounded-full transition-all duration-700"
                                                style={{ width: `${prevPct}%` }}
                                            ></div>
                                        </div>
                                        <span className="text-[11px] font-bold text-slate-400 w-20 text-right">{formatearMonto(mes.anterior)}</span>
                                    </div>
                                </div>
                                <span className={`text-[11px] font-black w-12 text-right ${monthGrowth >= 0 ? "text-emerald-500" : "text-rose-500"}`}>
                                    {monthGrowth !== 0 ? `${monthGrowth > 0 ? "+" : ""}${monthGrowth.toFixed(0)}%` : "—"}
                                </span>
                            </div>
                        </div>
                    );
                })}
            </div>
            <div className="flex gap-6 mt-4 pt-4 border-t border-slate-100 dark:border-slate-800">
                <span className="flex items-center gap-2 text-[11px] font-bold text-slate-400">
                    <span className="w-3 h-2 rounded bg-gradient-to-r from-blue-500 to-violet-500"></span>
                    {anioActual}
                </span>
                <span className="flex items-center gap-2 text-[11px] font-bold text-slate-400">
                    <span className="w-3 h-2 rounded bg-slate-300 dark:bg-slate-600"></span>
                    {anioAnterior}
                </span>
            </div>
        </div>
    );
}

/**
 * Tarjeta de saldo multimoneda (P-3): mismo molde visual que SummaryCard, pero en vez
 * de un número único muestra una línea por moneda con su CurrencyBadge — igual que
 * MoneyKpiCard del dashboard nuevo (features/dashboard/components/MoneyKpiGrid.jsx).
 * `lineas` ya viene armada por construirLineasKpiPorMoneda (lib/dashboardKpiCurrency.js).
 */
function MoneyByCurrencyCard({ title, lineas, icon: Icon, color }) {
    const colorMap = {
        blue: "bg-blue-50 text-blue-600 dark:bg-blue-900/20 dark:text-blue-400",
        emerald: "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400",
        violet: "bg-violet-50 text-violet-600 dark:bg-violet-900/20 dark:text-violet-400",
    };

    return (
        <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 p-5 shadow-sm hover:shadow-md transition-shadow">
            <div className={`inline-flex p-2 rounded-[10px] mb-3 ${colorMap[color]}`}>
                <Icon className="w-5 h-5" />
            </div>
            <div className="space-y-1">
                {lineas.map((linea) => (
                    <div key={linea.currency} className="flex items-center gap-1.5">
                        <CurrencyBadge currency={linea.currency} size="sm" />
                        <span className="text-lg font-black text-slate-900 dark:text-white">
                            {formatCurrency(linea.monto, linea.currency, { withSymbol: false })}
                        </span>
                    </div>
                ))}
            </div>
            <div className="text-[11px] font-bold text-slate-400 uppercase tracking-wider mt-2">{title}</div>
        </div>
    );
}

/**
 * Grafico de cobros/pagos por moneda de la solapa "Flujo de Caja". Reusa
 * `armarSeriesRitmoCobrosPagos` (misma lib pura que la tarjeta "Ritmo de cobros y
 * pagos" del dashboard, cashflowRhythmSeries.js) para no reinventar el cálculo de
 * series por moneda ni el eje Hoy/+30/+60/+90.
 *
 * F-14: la línea de "pagos" (costo — plata que sale a operadores) solo se dibuja si
 * `puedeVerCostos` es true. Sin el permiso `cobranzas.see_cost`, el backend ya manda
 * `cashOutByCurrency` vacío en cada día — graficar esa serie igual mostraría un "$0"
 * como si fuera el dato real, en vez de reconocer que está oculto.
 */
function CashflowByCurrencyChart({ cashflow, puedeVerCostos }) {
    const { hayMovimiento, monedas, ejeXTicks } = armarSeriesRitmoCobrosPagos(cashflow);

    if (!hayMovimiento) {
        return <p className="py-10 text-center text-sm text-slate-400">Todavía no hay movimientos para graficar.</p>;
    }

    return (
        <div className="space-y-6">
            {monedas.map((serie) => (
                <div key={serie.currency}>
                    <div className="mb-1 flex items-center gap-1.5">
                        <CurrencyBadge currency={serie.currency} size="sm" />
                    </div>
                    <div className="h-[160px] w-full">
                        <ResponsiveContainer width="100%" height="100%">
                            <LineChart data={serie.puntos} margin={{ top: 4, right: 8, left: 8, bottom: 0 }}>
                                <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#E2E8F0" />
                                <XAxis
                                    dataKey="x"
                                    type="number"
                                    domain={["dataMin", "dataMax"]}
                                    ticks={ejeXTicks.map((tick) => tick.x)}
                                    tickFormatter={(valor) => ejeXTicks.find((tick) => tick.x === valor)?.etiqueta ?? ""}
                                    stroke="#64748B"
                                    fontSize={11}
                                    tickLine={false}
                                    axisLine={false}
                                />
                                <YAxis hide />
                                <Tooltip
                                    formatter={(valor, nombre) => [
                                        formatCurrency(valor, serie.currency),
                                        nombre === "cobros" ? "Cobros" : "Pagos a operadores",
                                    ]}
                                    labelFormatter={(valor) => ejeXTicks.find((tick) => tick.x === valor)?.etiqueta ?? ""}
                                    contentStyle={{ borderRadius: "8px", border: "1px solid #E2E8F0", fontSize: "12px" }}
                                />
                                <Line type="monotone" dataKey="cobros" stroke={COLOR_COBROS} strokeWidth={2} dot={false} />
                                {puedeVerCostos ? (
                                    <Line type="monotone" dataKey="pagos" stroke={COLOR_PAGOS} strokeWidth={2} dot={false} />
                                ) : null}
                            </LineChart>
                        </ResponsiveContainer>
                    </div>
                </div>
            ))}

            <div className="flex items-center gap-4 text-[11px] font-semibold text-slate-500">
                <span className="flex items-center gap-1.5">
                    <span className="h-2 w-2 rounded-full" style={{ backgroundColor: COLOR_COBROS }} aria-hidden="true" />
                    Cobros
                </span>
                {puedeVerCostos ? (
                    <span className="flex items-center gap-1.5">
                        <span className="h-2 w-2 rounded-full" style={{ backgroundColor: COLOR_PAGOS }} aria-hidden="true" />
                        Pagos a operadores
                    </span>
                ) : null}
            </div>
        </div>
    );
}
