import { useEffect, useState, useCallback } from "react";
import { api } from "../api";
import { showError } from "../alerts";
import {
    TrendingUp, TrendingDown, Users, MapPin, Wallet, BarChart3,
    Calendar, ArrowUpRight, ArrowDownRight, Loader2, RefreshCw,
    Trophy, Target, DollarSign, Activity
} from "lucide-react";

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

    const fmt = (n) => `$${(n || 0).toLocaleString("es-AR", { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
    const fmtPct = (n) => `${(n || 0).toFixed(1)}%`;

    if (loading) {
        return (
            <div className="flex items-center justify-center h-[60vh]">
                <div className="flex flex-col items-center gap-4">
                    <div className="relative">
                        <div className="absolute inset-0 bg-indigo-500/20 rounded-full animate-ping"></div>
                        <div className="relative p-4 bg-indigo-600 rounded-full text-white shadow-xl shadow-indigo-200 dark:shadow-none">
                            <Loader2 className="w-8 h-8 animate-spin" />
                        </div>
                    </div>
                    <p className="text-sm font-semibold text-slate-500 dark:text-slate-400 tracking-wider uppercase">Cargando Analíticas...</p>
                </div>
            </div>
        );
    }

    const maxSales = sellers.length > 0 ? Math.max(...sellers.map(s => s.totalSales)) : 1;
    const maxDestRevenue = destinations.length > 0 ? Math.max(...destinations.map(d => d.totalRevenue)) : 1;

    // YoY chart data
    const currentYearMax = yoy?.currentYear ? Math.max(...yoy.currentYear.map(m => m.sales), 1) : 1;
    const previousYearMax = yoy?.previousYear ? Math.max(...yoy.previousYear.map(m => m.sales), 1) : 1;
    const yoyMax = Math.max(currentYearMax, previousYearMax);

    return (
        <div className="space-y-8 pb-12">
            {/* Header */}
            <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                <div>
                    <h1 className="text-2xl font-black text-slate-900 dark:text-white tracking-tight">Business Intelligence</h1>
                    <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">Analíticas avanzadas de tu operación</p>
                </div>
                <button
                    onClick={loadData}
                    className="flex items-center gap-2 px-4 py-2.5 bg-slate-900 dark:bg-white text-white dark:text-slate-900 rounded-xl text-sm font-bold shadow-lg hover:opacity-90 transition-all"
                >
                    <RefreshCw className="w-4 h-4" /> Actualizar
                </button>
            </div>

            {/* Summary Cards */}
            {cashflow && yoy && (
                <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
                    <SummaryCard
                        title="Balance Actual"
                        value={fmt(cashflow.currentBalance)}
                        icon={Wallet}
                        color="indigo"
                    />
                    <SummaryCard
                        title="Proyección 30d"
                        value={fmt(cashflow.projectedBalance30)}
                        icon={Target}
                        color="emerald"
                        trend={cashflow.projectedBalance30 > cashflow.currentBalance ? "up" : "down"}
                    />
                    <SummaryCard
                        title="Proyección 90d"
                        value={fmt(cashflow.projectedBalance90)}
                        icon={Activity}
                        color="violet"
                        trend={cashflow.projectedBalance90 > cashflow.currentBalance ? "up" : "down"}
                    />
                    <SummaryCard
                        title="Crecimiento Interanual"
                        value={fmtPct(yoy.growthPercent)}
                        icon={yoy.growthPercent >= 0 ? TrendingUp : TrendingDown}
                        color={yoy.growthPercent >= 0 ? "emerald" : "rose"}
                        subtitle={`${fmt(yoy.currentYearTotal)} vs ${fmt(yoy.previousYearTotal)}`}
                    />
                </div>
            )}

            {/* Tab Navigation */}
            <div className="flex gap-2 bg-slate-100 dark:bg-slate-800/50 p-1 rounded-xl w-fit">
                {[
                    { id: "sellers", label: "Vendedores", icon: Users },
                    { id: "destinations", label: "Destinos", icon: MapPin },
                    { id: "cashflow", label: "Flujo de Caja", icon: DollarSign },
                    { id: "yoy", label: "Interanual", icon: BarChart3 },
                ].map(tab => (
                    <button
                        key={tab.id}
                        onClick={() => setActiveTab(tab.id)}
                        className={`flex items-center gap-2 px-4 py-2 rounded-lg text-xs font-bold transition-all ${activeTab === tab.id
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
                    <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                        <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800 flex items-center justify-between">
                            <div className="flex items-center gap-3">
                                <div className="p-2 rounded-xl bg-amber-50 dark:bg-amber-900/20 text-amber-600">
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
                            ) : sellers.map((s, idx) => (
                                <div key={s.userId} className="px-6 py-4 flex items-center gap-4 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                                    <div className={`w-8 h-8 rounded-full flex items-center justify-center text-xs font-black text-white ${idx === 0 ? "bg-amber-500" : idx === 1 ? "bg-slate-400" : idx === 2 ? "bg-orange-600" : "bg-slate-300"
                                        }`}>
                                        {idx + 1}
                                    </div>
                                    <div className="flex-1 min-w-0">
                                        <div className="flex items-center justify-between mb-1">
                                            <span className="text-sm font-bold text-slate-900 dark:text-white truncate">{s.sellerName}</span>
                                            <span className="text-sm font-black text-slate-900 dark:text-white ml-2">{fmt(s.totalSales)}</span>
                                        </div>
                                        <div className="flex items-center gap-3">
                                            <div className="flex-1 h-2 bg-slate-100 dark:bg-slate-800 rounded-full overflow-hidden">
                                                <div
                                                    className="h-full bg-gradient-to-r from-indigo-500 to-violet-500 rounded-full transition-all duration-700"
                                                    style={{ width: `${(s.totalSales / maxSales) * 100}%` }}
                                                ></div>
                                            </div>
                                            <span className="text-[10px] font-bold text-slate-400 w-16 text-right">{s.filesCreated} files</span>
                                            <span className={`text-[10px] font-black px-1.5 py-0.5 rounded ${s.marginPercent > 15 ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20" : "bg-amber-50 text-amber-600 dark:bg-amber-900/20"
                                                }`}>
                                                {fmtPct(s.marginPercent)} mrg
                                            </span>
                                        </div>
                                    </div>
                                </div>
                            ))}
                        </div>
                    </div>
                </div>
            )}

            {/* ===== DESTINATIONS TAB ===== */}
            {activeTab === "destinations" && (
                <div className="animate-in fade-in duration-300 space-y-6">
                    <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 overflow-hidden shadow-sm">
                        <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3">
                            <div className="p-2 rounded-xl bg-sky-50 dark:bg-sky-900/20 text-sky-600">
                                <MapPin className="w-5 h-5" />
                            </div>
                            <div>
                                <h2 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider">Destinos Más Populares</h2>
                                <p className="text-xs text-slate-400 mt-0.5">Agrupados por hotel, paquete y aéreo</p>
                            </div>
                        </div>
                        <div className="p-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                            {destinations.length === 0 ? (
                                <div className="col-span-full py-12 text-center text-sm text-slate-400">No hay datos de destinos disponibles.</div>
                            ) : destinations.map((d, idx) => (
                                <div key={d.destination} className="group relative bg-gradient-to-br from-slate-50 to-white dark:from-slate-800/50 dark:to-slate-900 rounded-xl p-5 border border-slate-100 dark:border-slate-800 hover:shadow-md hover:border-indigo-200 dark:hover:border-indigo-800 transition-all">
                                    {idx < 3 && (
                                        <div className="absolute -top-2 -right-2 w-6 h-6 rounded-full bg-indigo-600 text-white text-[10px] font-black flex items-center justify-center shadow-lg">
                                            {idx + 1}
                                        </div>
                                    )}
                                    <div className="text-lg font-black text-slate-900 dark:text-white mb-3 capitalize">
                                        {d.destination.toLowerCase()}
                                    </div>
                                    <div className="h-2 bg-slate-100 dark:bg-slate-700 rounded-full overflow-hidden mb-4">
                                        <div
                                            className="h-full bg-gradient-to-r from-sky-400 to-indigo-500 rounded-full transition-all duration-700"
                                            style={{ width: `${(d.totalRevenue / maxDestRevenue) * 100}%` }}
                                        ></div>
                                    </div>
                                    <div className="grid grid-cols-2 gap-2 text-xs">
                                        <div>
                                            <span className="text-slate-400 block">Revenue</span>
                                            <span className="font-black text-slate-900 dark:text-white">{fmt(d.totalRevenue)}</span>
                                        </div>
                                        <div>
                                            <span className="text-slate-400 block">Margen</span>
                                            <span className="font-black text-emerald-600">{fmt(d.margin)}</span>
                                        </div>
                                        <div>
                                            <span className="text-slate-400 block">Bookings</span>
                                            <span className="font-bold text-slate-700 dark:text-slate-300">{d.bookingCount}</span>
                                        </div>
                                        <div>
                                            <span className="text-slate-400 block">Pasajeros</span>
                                            <span className="font-bold text-slate-700 dark:text-slate-300">{d.passengerCount}</span>
                                        </div>
                                    </div>
                                </div>
                            ))}
                        </div>
                    </div>
                </div>
            )}

            {/* ===== CASHFLOW TAB (CSS chart) ===== */}
            {activeTab === "cashflow" && cashflow && (
                <div className="animate-in fade-in duration-300 space-y-6">
                    {/* Projection KPIs */}
                    <div className="grid grid-cols-3 gap-4">
                        {[
                            { label: "30 días", val: cashflow.projectedBalance30 },
                            { label: "60 días", val: cashflow.projectedBalance60 },
                            { label: "90 días", val: cashflow.projectedBalance90 },
                        ].map(p => (
                            <div key={p.label} className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 p-5 text-center">
                                <div className="text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">Proyección {p.label}</div>
                                <div className={`text-xl font-black ${p.val >= 0 ? "text-emerald-600" : "text-rose-600"}`}>{fmt(p.val)}</div>
                            </div>
                        ))}
                    </div>

                    {/* Historical chart (last 30 days as bars) */}
                    <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 p-6 shadow-sm">
                        <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider mb-6 flex items-center gap-2">
                            <Activity className="w-4 h-4 text-indigo-500" />
                            Flujo de Caja (Últimos 30 días)
                        </h3>
                        <div className="flex items-end gap-[2px] h-40 overflow-x-auto">
                            {cashflow.historical.map((day, idx) => {
                                const maxVal = Math.max(...cashflow.historical.map(d => Math.max(d.cashIn, d.cashOut)), 1);
                                const inHeight = (day.cashIn / maxVal) * 100;
                                const outHeight = (day.cashOut / maxVal) * 100;
                                return (
                                    <div key={idx} className="flex-1 min-w-[8px] flex flex-col items-center gap-[1px] group relative">
                                        <div
                                            className="w-full bg-emerald-400/80 dark:bg-emerald-500/60 rounded-t transition-all hover:bg-emerald-500"
                                            style={{ height: `${inHeight}%`, minHeight: day.cashIn > 0 ? "2px" : "0" }}
                                            title={`Ingreso: ${fmt(day.cashIn)}`}
                                        ></div>
                                        <div
                                            className="w-full bg-rose-400/80 dark:bg-rose-500/60 rounded-b transition-all hover:bg-rose-500"
                                            style={{ height: `${outHeight}%`, minHeight: day.cashOut > 0 ? "2px" : "0" }}
                                            title={`Egreso: ${fmt(day.cashOut)}`}
                                        ></div>
                                    </div>
                                );
                            })}
                        </div>
                        <div className="flex justify-between mt-3 text-[9px] font-bold text-slate-400">
                            <span>{new Date(cashflow.historical[0]?.date).toLocaleDateString("es-AR", { day: "2-digit", month: "short" })}</span>
                            <div className="flex gap-4">
                                <span className="flex items-center gap-1"><span className="w-2 h-2 rounded-full bg-emerald-400"></span> Ingresos</span>
                                <span className="flex items-center gap-1"><span className="w-2 h-2 rounded-full bg-rose-400"></span> Egresos</span>
                            </div>
                            <span>Hoy</span>
                        </div>
                    </div>
                </div>
            )}

            {/* ===== YOY TAB ===== */}
            {activeTab === "yoy" && yoy && (
                <div className="animate-in fade-in duration-300 space-y-6">
                    <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 p-6 shadow-sm">
                        <div className="flex items-center justify-between mb-6">
                            <h3 className="text-sm font-black text-slate-900 dark:text-white uppercase tracking-wider flex items-center gap-2">
                                <BarChart3 className="w-4 h-4 text-violet-500" />
                                Comparativa Interanual
                            </h3>
                            <div className={`flex items-center gap-1 text-sm font-black px-3 py-1 rounded-full ${yoy.growthPercent >= 0
                                    ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20"
                                    : "bg-rose-50 text-rose-600 dark:bg-rose-900/20"
                                }`}>
                                {yoy.growthPercent >= 0 ? <ArrowUpRight className="w-4 h-4" /> : <ArrowDownRight className="w-4 h-4" />}
                                {fmtPct(Math.abs(yoy.growthPercent))}
                            </div>
                        </div>

                        {/* Totals */}
                        <div className="grid grid-cols-2 gap-4 mb-6">
                            <div className="bg-indigo-50 dark:bg-indigo-900/10 rounded-xl p-4">
                                <div className="text-[10px] font-bold text-indigo-400 uppercase tracking-wider">{new Date().getFullYear()}</div>
                                <div className="text-xl font-black text-indigo-600 dark:text-indigo-400">{fmt(yoy.currentYearTotal)}</div>
                            </div>
                            <div className="bg-slate-50 dark:bg-slate-800/50 rounded-xl p-4">
                                <div className="text-[10px] font-bold text-slate-400 uppercase tracking-wider">{new Date().getFullYear() - 1}</div>
                                <div className="text-xl font-black text-slate-600 dark:text-slate-400">{fmt(yoy.previousYearTotal)}</div>
                            </div>
                        </div>

                        {/* Monthly bars */}
                        <div className="space-y-3">
                            {yoy.currentYear.map((month, idx) => {
                                const prev = yoy.previousYear[idx];
                                const currPct = yoyMax > 0 ? (month.sales / yoyMax) * 100 : 0;
                                const prevPct = yoyMax > 0 ? (prev.sales / yoyMax) * 100 : 0;
                                const monthGrowth = prev.sales > 0 ? ((month.sales - prev.sales) / prev.sales) * 100 : 0;
                                return (
                                    <div key={month.month} className="group">
                                        <div className="flex items-center gap-3">
                                            <span className="text-[10px] font-black text-slate-400 w-8 text-right uppercase">{month.month}</span>
                                            <div className="flex-1 space-y-1">
                                                <div className="flex items-center gap-2">
                                                    <div className="flex-1 h-3 bg-slate-100 dark:bg-slate-800 rounded-full overflow-hidden">
                                                        <div
                                                            className="h-full bg-gradient-to-r from-indigo-500 to-violet-500 rounded-full transition-all duration-700"
                                                            style={{ width: `${currPct}%` }}
                                                        ></div>
                                                    </div>
                                                    <span className="text-[9px] font-bold text-slate-500 w-20 text-right">{fmt(month.sales)}</span>
                                                </div>
                                                <div className="flex items-center gap-2">
                                                    <div className="flex-1 h-2 bg-slate-100 dark:bg-slate-800 rounded-full overflow-hidden">
                                                        <div
                                                            className="h-full bg-slate-300 dark:bg-slate-600 rounded-full transition-all duration-700"
                                                            style={{ width: `${prevPct}%` }}
                                                        ></div>
                                                    </div>
                                                    <span className="text-[9px] font-bold text-slate-400 w-20 text-right">{fmt(prev.sales)}</span>
                                                </div>
                                            </div>
                                            <span className={`text-[9px] font-black w-12 text-right ${monthGrowth >= 0 ? "text-emerald-500" : "text-rose-500"}`}>
                                                {monthGrowth !== 0 ? `${monthGrowth > 0 ? "+" : ""}${monthGrowth.toFixed(0)}%` : "—"}
                                            </span>
                                        </div>
                                    </div>
                                );
                            })}
                        </div>
                        <div className="flex gap-6 mt-4 pt-4 border-t border-slate-100 dark:border-slate-800">
                            <span className="flex items-center gap-2 text-[10px] font-bold text-slate-400">
                                <span className="w-3 h-2 rounded bg-gradient-to-r from-indigo-500 to-violet-500"></span>
                                {new Date().getFullYear()}
                            </span>
                            <span className="flex items-center gap-2 text-[10px] font-bold text-slate-400">
                                <span className="w-3 h-2 rounded bg-slate-300 dark:bg-slate-600"></span>
                                {new Date().getFullYear() - 1}
                            </span>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}

function SummaryCard({ title, value, icon: Icon, color, trend, subtitle }) {
    const colorMap = {
        indigo: "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400",
        emerald: "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-400",
        violet: "bg-violet-50 text-violet-600 dark:bg-violet-900/20 dark:text-violet-400",
        rose: "bg-rose-50 text-rose-600 dark:bg-rose-900/20 dark:text-rose-400",
    };

    return (
        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 p-5 shadow-sm hover:shadow-md transition-shadow">
            <div className="flex items-center justify-between mb-3">
                <div className={`p-2 rounded-xl ${colorMap[color]}`}>
                    <Icon className="w-5 h-5" />
                </div>
                {trend && (
                    <div className={`flex items-center gap-0.5 text-xs font-black ${trend === "up" ? "text-emerald-500" : "text-rose-500"}`}>
                        {trend === "up" ? <ArrowUpRight className="w-3.5 h-3.5" /> : <ArrowDownRight className="w-3.5 h-3.5" />}
                    </div>
                )}
            </div>
            <div className="text-xl font-black text-slate-900 dark:text-white">{value}</div>
            <div className="text-[10px] font-bold text-slate-400 uppercase tracking-wider mt-1">{title}</div>
            {subtitle && <div className="text-[9px] text-slate-400 mt-0.5">{subtitle}</div>}
        </div>
    );
}
