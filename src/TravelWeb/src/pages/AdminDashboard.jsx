import { useEffect, useState } from "react";
import { api } from "../api";
import { useNavigate } from "react-router-dom";
import {
    FileText,
    Clock,
    Briefcase,
    DollarSign,
    TrendingUp,
    Calendar,
    AlertCircle,
    ArrowRight,
    Plane,
    PieChart,
    BarChart3,
    Wallet
} from "lucide-react";
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle
} from "../components/ui/card";
import {
    BarChart,
    Bar,
    XAxis,
    YAxis,
    CartesianGrid,
    Tooltip,
    ResponsiveContainer,
    AreaChart,
    Area,
    PieChart as RePieChart,
    Pie,
    Cell,
    Legend
} from "recharts";

import { DolarBnaTira } from "../components/DolarBnaTira";
import { CurrencyBadge } from "../components/ui/CurrencyBadge";
import { DashboardSkeleton } from "../components/ui/skeleton";
import { getPublicId } from "../lib/publicIds";
import { construirLineasKpiConCompatibilidad } from "../lib/dashboardKpiCurrency";
import { statusConfig } from "../features/reservas/components/ReservaStatusBadge";
import { formatCurrency, formatDate } from "../lib/utils";

export default function DashboardPage() {
    const [dashboard, setDashboard] = useState(null);
    const [loading, setLoading] = useState(true);
    const navigate = useNavigate();

    const loadDashboard = async () => {
        try {
            const data = await api.get("/reports/dashboard");
            setDashboard(data);
        } catch (error) {
            console.log("Error loading dashboard:", error.message);
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        loadDashboard();
        const interval = setInterval(loadDashboard, 300000);
        return () => clearInterval(interval);
    }, []);

    if (loading) {
        return <DashboardSkeleton />;
    }

    if (!dashboard) {
        return (
            <div className="text-center py-12">
                <p className="text-muted-foreground">No se pudieron cargar las métricas.</p>
            </div>
        );
    }

    // Prepare data for charts. Los labels son los nombres del refactor de ciclo
    // de vida (Confirmada / En viaje / Finalizada). Los keys del backend siguen
    // siendo budgets/reserved/operational/closed/cancelled por compatibilidad con
    // el endpoint de dashboard.
    const statusData = [
        { name: 'Presupuesto', value: dashboard.distribucionEstados?.budgets ?? dashboard.distribucionEstados?.Budgets ?? 0, color: '#94a3b8' }, // Slate-400
        { name: 'Confirmada', value: dashboard.distribucionEstados?.reserved ?? dashboard.distribucionEstados?.Reserved ?? 0, color: '#f59e0b' }, // Amber-500
        { name: 'En viaje', value: dashboard.distribucionEstados?.operational ?? dashboard.distribucionEstados?.Operational ?? 0, color: '#10b981' }, // Emerald-500
        { name: 'Finalizada', value: dashboard.distribucionEstados?.closed ?? dashboard.distribucionEstados?.Closed ?? 0, color: '#6366f1' }, // Indigo-500
        // Vocabulario firmado: "Anular" = sin efecto (el estado Cancelled se muestra "Anulada", igual que la pestania de Reservas).
        { name: 'Anulada', value: dashboard.distribucionEstados?.cancelled ?? dashboard.distribucionEstados?.Cancelled ?? 0, color: '#ef4444' }, // Red-500
    ].filter(item => item.value > 0);

    // ADR-021 Capa 6: dashboard.porMoneda trae los mismos totales pero SEPARADOS por
    // moneda (nunca mezclados) — ver DashboardByCurrencyDto en IReportService.cs. Los
    // escalares de arriba (dashboard.ventasDelMes, etc.) son compat vieja y hoy siempre
    // coinciden con el único ítem ARS de cada lista.
    const porMoneda = dashboard.porMoneda || null;

    return (
        <div className="space-y-8 animate-in fade-in duration-500">
            {/* Header */}
            <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
                <div>
                    <h2 className="text-3xl font-bold tracking-tight text-slate-900 dark:text-white">Dashboard</h2>
                    <p className="text-muted-foreground mt-1">
                        Cómo viene tu agencia de un vistazo.
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    <button
                        type="button"
                        onClick={() => navigate("/reservas?create=1")}
                        className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-bold text-white shadow-lg shadow-indigo-500/20 transition-colors hover:bg-indigo-700"
                    >
                        <FileText className="h-4 w-4" />
                        Nuevo presupuesto
                    </button>
                    <button
                        type="button"
                        onClick={() => navigate("/crm")}
                        className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-bold text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800"
                    >
                        <Briefcase className="h-4 w-4" />
                        Posibles clientes
                    </button>
                </div>
            </div>

            {/* Tira fina del dólar Banco Nación (spec firmada docs/ux/specs/2026-08-06-dolar-en-dashboard.md,
                2026-08-05): reemplaza a la tarjeta grande BnaUsdSellerRateCard, que el dueño
                desaprobó por completo ("es feo"). Un solo dólar: el "para facturar" no se pinta
                acá (ya vive precargado en las pantallas de facturar); el dato sigue viajando en
                el DTO del dashboard sin usarse en esta tira. */}
            <DolarBnaTira rate={dashboard.bnaUsdSellerRate} />

            {/* KPI Cards */}
            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
                <KpiCard
                    title="Ventas del Mes"
                    lineasPorMoneda={construirLineasKpiConCompatibilidad(porMoneda?.ventasDelMes, dashboard.ventasDelMes)}
                    icon={TrendingUp}
                    color="text-indigo-600 dark:text-indigo-400"
                    bg="bg-indigo-50 dark:bg-indigo-900/10"
                    trend="Ingresos brutos"
                />
                <KpiCard
                    title="Margen Bruto"
                    // BL-2 (revisión 2026-07-27): `DashboardByCurrencyDto.margenBruto` ya viene
                    // del backend con el mismo contrato que ventasDelMes/cobrosDelMes ([{amount,
                    // currency}]) — se pinta por moneda igual que las otras tarjetas. El fallback
                    // a `valorSinMoneda` (número pelado, SIN cartelito de moneda) queda SOLO para
                    // cuando `porMoneda` no vino en absoluto (deploy viejo en caché, sin ningún
                    // desglose por moneda todavía) — no afirmamos una moneda que no podemos
                    // garantizar en ese caso.
                    lineasPorMoneda={porMoneda ? construirLineasKpiConCompatibilidad(porMoneda.margenBruto, dashboard.margenBruto) : null}
                    valorSinMoneda={dashboard.margenBruto}
                    icon={PieChart}
                    color="text-emerald-600 dark:text-emerald-400"
                    bg="bg-emerald-50 dark:bg-emerald-900/10"
                    trend="Beneficio neto"
                />
                <KpiCard
                    title="Cobros Clientes"
                    lineasPorMoneda={construirLineasKpiConCompatibilidad(porMoneda?.cobrosDelMes, dashboard.cobrosDelMes)}
                    icon={Wallet}
                    color="text-blue-600 dark:text-blue-400"
                    bg="bg-blue-50 dark:bg-blue-900/10"
                    trend="Ingresos de caja"
                />
                <KpiCard
                    title="Saldo Pendiente"
                    // H16 (barrido E2E 2026-07-25): saldoPendiente puede dar negativo EN UNA
                    // MONEDA PUNTUAL cuando el saldo a favor de los clientes en esa moneda supera
                    // lo que efectivamente deben — no es un error. negativoEsSaldoAFavor hace que
                    // esa línea se muestre en positivo con la leyenda "A favor de clientes"
                    // (BL-3: esa leyenda se pinta POR LÍNEA adentro de KpiCard, no una sola vez
                    // al pie de la tarjeta — ver comentario dentro de KpiCard).
                    lineasPorMoneda={construirLineasKpiConCompatibilidad(porMoneda?.saldoPendiente, dashboard.saldoPendiente, { negativoEsSaldoAFavor: true })}
                    icon={AlertCircle}
                    color="text-rose-600 dark:text-rose-400"
                    bg="bg-rose-50 dark:bg-rose-900/10"
                    trend="Por cobrar"
                />
            </div>

            {/* Charts Section */}
            <div className="grid gap-6 lg:grid-cols-7">
                {/* Main Trends Chart */}
                <Card className="lg:col-span-4 shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2">
                            <BarChart3 className="h-5 w-5 text-slate-500" />
                            Rendimiento Semestral
                        </CardTitle>
                        <CardDescription>Comparativa de Ventas vs Costos (Últimos 6 meses)</CardDescription>
                    </CardHeader>
                    <CardContent className="pl-0">
                        <div className="h-[300px] w-full">
                            <ResponsiveContainer width="100%" height="100%">
                                <BarChart data={dashboard.tendenciaHistorica} margin={{ top: 10, right: 30, left: 0, bottom: 0 }}>
                                    <CartesianGrid strokeDasharray="3 3" vertical={false} stroke="#e2e8f0" />
                                    <XAxis
                                        dataKey="month"
                                        stroke="#64748b"
                                        fontSize={12}
                                        tickLine={false}
                                        axisLine={false}
                                    />
                                    <YAxis
                                        stroke="#64748b"
                                        fontSize={12}
                                        tickLine={false}
                                        axisLine={false}
                                        tickFormatter={(value) => `$${value / 1000}k`}
                                    />
                                    <Tooltip
                                        cursor={{ fill: '#f1f5f9' }}
                                        contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                                        formatter={(value) => [`$${value.toLocaleString()}`, undefined]}
                                    />
                                    <Legend wrapperStyle={{ paddingTop: '20px' }} />
                                    <Bar dataKey="sales" name="Ventas" fill="#6366f1" radius={[4, 4, 0, 0]} barSize={30} />
                                    <Bar dataKey="costs" name="Costos" fill="#94a3b8" radius={[4, 4, 0, 0]} barSize={30} />
                                </BarChart>
                            </ResponsiveContainer>
                        </div>
                    </CardContent>
                </Card>

                {/* Status Distribution */}
                <Card className="lg:col-span-3 shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2">
                            <PieChart className="h-5 w-5 text-slate-500" />
                            Estado de Reservas
                        </CardTitle>
                        <CardDescription>Distribución actual de reservas activas</CardDescription>
                    </CardHeader>
                    <CardContent>
                        <div className="h-[300px] w-full flex items-center justify-center">
                            {statusData.length > 0 ? (
                                <ResponsiveContainer width="100%" height="100%">
                                    <RePieChart>
                                        <Pie
                                            data={statusData}
                                            cx="50%"
                                            cy="50%"
                                            innerRadius={60}
                                            outerRadius={80}
                                            paddingAngle={5}
                                            dataKey="value"
                                        >
                                            {statusData.map((entry, index) => (
                                                <Cell key={`cell-${index}`} fill={entry.color} strokeWidth={0} />
                                            ))}
                                        </Pie>
                                        <Tooltip
                                            contentStyle={{ borderRadius: '8px', border: 'none', boxShadow: '0 4px 6px -1px rgb(0 0 0 / 0.1)' }}
                                        />
                                        <Legend
                                            layout="vertical"
                                            verticalAlign="middle"
                                            align="right"
                                            wrapperStyle={{ paddingLeft: '20px' }}
                                        />
                                    </RePieChart>
                                </ResponsiveContainer>
                            ) : (
                                <div className="text-center text-muted-foreground p-8">
                                    <Clock className="h-10 w-10 mx-auto mb-2 opacity-20" />
                                    No hay datos suficientes
                                </div>
                            )}
                        </div>
                    </CardContent>
                </Card>
            </div>

            {/* Operational Lists */}
            <div className="grid gap-6 md:grid-cols-2">
                {/* Pending Balances */}
                <Card className="shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2 text-rose-600 dark:text-rose-400">
                            <AlertCircle className="h-5 w-5" />
                            Cobros Pendientes
                        </CardTitle>
                        <CardDescription>Prioridad de gestión de cobranza</CardDescription>
                    </CardHeader>
                    <CardContent>
                        <div className="space-y-4">
                            {dashboard.reservasPendientes?.length > 0 ? (
                                dashboard.reservasPendientes.map((reserva) => (
                                    <div
                                        key={getPublicId(reserva)}
                                        className="flex items-center justify-between p-3 rounded-lg bg-rose-50/50 hover:bg-rose-100/50 dark:bg-rose-900/10 dark:hover:bg-rose-900/20 cursor-pointer transition-colors border border-rose-100 dark:border-rose-900/20"
                                        onClick={() => navigate(`/reservas/${getPublicId(reserva)}`)}
                                    >
                                        <div className="flex gap-3 items-center">
                                            <div className="bg-rose-100 dark:bg-rose-900/30 p-2 rounded-full">
                                                <DollarSign className="h-4 w-4 text-rose-600" />
                                            </div>
                                            <div>
                                                <div className="font-medium text-slate-800 dark:text-slate-200">{reserva.name}</div>
                                                <div className="text-xs text-rose-600/80 font-medium">{reserva.numeroReserva}</div>
                                            </div>
                                        </div>
                                        <div className="text-right">
                                            <div className="font-bold text-rose-700 dark:text-rose-400">
                                                {/* H16: toLocaleString() sin locale fijo dependía del navegador y
                                                    mostraba "$9205" sin separador de miles. formatCurrency() es el
                                                    helper único es-AR (T-4); reserva.currency respeta la moneda
                                                    real de esa reserva puntual (puede no ser ARS). */}
                                                {formatCurrency(reserva.balance, reserva.currency || "ARS")}
                                            </div>
                                            <div className="text-[10px] text-muted-foreground uppercase">Pendiente</div>
                                        </div>
                                    </div>
                                ))
                            ) : (
                                <EmptyState message="No hay saldos pendientes" />
                            )}
                        </div>
                    </CardContent>
                </Card>

                {/* Upcoming Trips */}
                <Card className="shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2 text-blue-600 dark:text-blue-400">
                            <Plane className="h-5 w-5" />
                            Próximas Salidas
                        </CardTitle>
                        <CardDescription>Viajes iniciando en los próximos 7 días</CardDescription>
                    </CardHeader>
                    <CardContent>
                        <div className="space-y-4">
                            {dashboard.proximosViajes?.length > 0 ? (
                                dashboard.proximosViajes.map((trip) => (
                                    <div
                                        key={getPublicId(trip)}
                                        className="flex items-center justify-between p-3 rounded-lg bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/50 dark:hover:bg-slate-800 cursor-pointer transition-colors border border-slate-100 dark:border-slate-800"
                                        onClick={() => navigate(`/reservas/${getPublicId(trip)}`)}
                                    >
                                        <div className="flex gap-3 items-center">
                                            <div className="bg-blue-100 dark:bg-blue-900/30 p-2 rounded-full">
                                                <Calendar className="h-4 w-4 text-blue-600" />
                                            </div>
                                            <div>
                                                <div className="font-medium text-slate-800 dark:text-slate-200">{trip.name}</div>
                                                <div className="text-xs text-muted-foreground">{trip.numeroReserva}</div>
                                            </div>
                                        </div>
                                        <div className="text-right">
                                            <div className="font-medium text-blue-600 dark:text-blue-400">
                                                {formatDate(trip.startDate)}
                                            </div>
                                            <BadgeStatus status={trip.status} />
                                        </div>
                                    </div>
                                ))
                            ) : (
                                <EmptyState message="No hay salidas próximas" />
                            )}
                        </div>
                    </CardContent>
                </Card>
            </div>
        </div>
    );
}

/**
 * Tarjeta de un indicador numérico del dashboard (ventas, cobros, saldo pendiente, etc).
 *
 * Multimoneda (fix B3, revisión 2026-07-27): recibe `lineasPorMoneda` (armado por
 * `construirLineasKpiConCompatibilidad`, ver `lib/dashboardKpiCurrency.js`) y pinta UNA
 * LÍNEA POR MONEDA con su propio cartelito $/US$ — regla P-3, nunca un número que mezcle
 * pesos y dólares. Antes esta tarjeta recibía un único `value` ya sumado entre monedas y
 * le pegaba el formato "ARS" encima, aunque el total real mezclara ARS+USD.
 *
 * `valorSinMoneda` es la excepción: se usa SOLO cuando `lineasPorMoneda` viene `null`
 * (hoy, solo Margen Bruto en un deploy viejo sin `porMoneda` en absoluto) — se muestra
 * el número pelado, sin cartelito de moneda, para no afirmar algo que no se puede
 * garantizar.
 */
function KpiCard({ title, lineasPorMoneda, valorSinMoneda, icon: Icon, color, bg, trend }) {
    return (
        <Card className={`border-none shadow-sm ${bg} transition-all hover:scale-[1.02] cursor-default`}>
            <CardContent className="p-6">
                <div className="flex items-center justify-between space-y-0">
                    <p className={`text-sm font-medium ${color} opacity-80`}>{title}</p>
                    <Icon className={`h-4 w-4 ${color}`} />
                </div>
                {lineasPorMoneda ? (
                    <div className="mt-2 space-y-2">
                        {lineasPorMoneda.map((linea) => (
                            <div key={linea.currency}>
                                <div className="flex items-center gap-1.5">
                                    <CurrencyBadge currency={linea.currency} size="sm" />
                                    <span className={`text-2xl font-bold ${color}`}>
                                        {formatCurrency(linea.monto, linea.currency, { withSymbol: false })}
                                    </span>
                                </div>
                                {/* BL-3 (revisión 2026-07-27): la leyenda va POR LÍNEA, no una sola
                                    al pie de la tarjeta. Con ARS a favor del cliente y USD en deuda
                                    AL MISMO TIEMPO, una sola leyenda compartida no dejaba saber cuál
                                    moneda era cuál (P-3: monedas jamás mezcladas, ni siquiera en el
                                    texto de apoyo). */}
                                {linea.esSaldoAFavor ? (
                                    <p className={`text-xs ${color} font-semibold`}>A favor de clientes</p>
                                ) : trend ? (
                                    <p className={`text-xs ${color} opacity-70`}>{trend}</p>
                                ) : null}
                            </div>
                        ))}
                    </div>
                ) : (
                    <>
                        <div className="mt-2">
                            {/* Margen Bruto en deploy viejo sin porMoneda: número pelado, sin
                                cartelito de moneda (ver comentario en el call site). */}
                            <span className={`text-3xl font-bold ${color}`}>
                                {(Number(valorSinMoneda) || 0).toLocaleString("es-AR")}
                            </span>
                        </div>
                        {trend ? <p className={`text-xs ${color} mt-1 opacity-70`}>{trend}</p> : null}
                    </>
                )}
            </CardContent>
        </Card>
    );
}

function EmptyState({ message }) {
    return (
        <div className="text-center py-8 text-muted-foreground flex flex-col items-center">
            <div className="bg-slate-100 dark:bg-slate-800 p-3 rounded-full mb-3">
                <Briefcase className="h-6 w-6 opacity-30" />
            </div>
            <p className="text-sm">{message}</p>
        </div>
    );
}

function BadgeStatus({ status }) {
    // Obra 6 (firma de Gastón 2026-07-27): antes este mapa local estaba INCOMPLETO (le
    // faltaban Quotation/InManagement/Lost, entre otros) y el fallback mostraba la clave
    // cruda del backend ("Quotation" en vez de "Cotización") — jerga técnica en inglés
    // que un administrador no tiene por qué entender. Ahora reusa el mismo `statusConfig`
    // canónico que ya pinta el estado en el listado y la ficha de Reservas
    // (`ReservaStatusBadge.jsx`) — un solo mapa, sin divergencia posible entre pantallas.
    const cfg = statusConfig[status];
    const label = cfg ? cfg.label : "—";
    // Fallback neutro (nunca la clave cruda) para un status que todavía no está en el
    // mapa canónico — no debería pasar en producción, pero si pasa, no debe filtrar jerga.
    const className = cfg
        ? cfg.color
        : "bg-slate-100 text-slate-500 border-slate-200 dark:bg-slate-800/60 dark:text-slate-400 dark:border-slate-700";

    return (
        <span className={`text-[10px] font-semibold px-2 py-0.5 rounded-full border ${className}`}>
            {label}
        </span>
    );
}
