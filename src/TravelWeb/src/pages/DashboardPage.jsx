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

import { DashboardSkeleton } from "../components/ui/skeleton";

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
        // Polling cada 60s
        const interval = setInterval(loadDashboard, 60000);
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

    // Prepare data for charts
    const statusData = [
        { name: 'Presupuesto', value: dashboard.distribucionEstados?.budgets ?? dashboard.distribucionEstados?.Budgets ?? 0, color: '#94a3b8' }, // Slate-400
        { name: 'Reservado', value: dashboard.distribucionEstados?.reserved ?? dashboard.distribucionEstados?.Reserved ?? 0, color: '#f59e0b' }, // Amber-500
        { name: 'Operativo', value: dashboard.distribucionEstados?.operational ?? dashboard.distribucionEstados?.Operational ?? 0, color: '#10b981' }, // Emerald-500
        { name: 'Cerrado', value: dashboard.distribucionEstados?.closed ?? dashboard.distribucionEstados?.Closed ?? 0, color: '#6366f1' }, // Indigo-500
        { name: 'Cancelado', value: dashboard.distribucionEstados?.cancelled ?? dashboard.distribucionEstados?.Cancelled ?? 0, color: '#ef4444' }, // Red-500
    ].filter(item => item.value > 0);

    return (
        <div className="space-y-8 animate-in fade-in duration-500">
            {/* Header */}
            <div>
                <h2 className="text-3xl font-bold tracking-tight text-slate-900 dark:text-white">Dashboard</h2>
                <p className="text-muted-foreground mt-1">
                    Vista general del rendimiento de tu agencia.
                </p>
            </div>

            {/* KPI Cards */}
            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
                <KpiCard
                    title="Ventas del Mes"
                    value={dashboard.ventasDelMes}
                    icon={TrendingUp}
                    color="text-indigo-600 dark:text-indigo-400"
                    bg="bg-indigo-50 dark:bg-indigo-900/10"
                    trend="Ingresos brutos"
                />
                <KpiCard
                    title="Margen Bruto"
                    value={dashboard.margenBruto}
                    icon={PieChart}
                    color="text-emerald-600 dark:text-emerald-400"
                    bg="bg-emerald-50 dark:bg-emerald-900/10"
                    trend="Beneficio neto"
                />
                <KpiCard
                    title="Cobros Clientes"
                    value={dashboard.cobrosDelMes}
                    icon={Wallet}
                    color="text-blue-600 dark:text-blue-400"
                    bg="bg-blue-50 dark:bg-blue-900/10"
                    trend="Ingresos de caja"
                />
                <KpiCard
                    title="Saldo Pendiente"
                    value={dashboard.saldoPendiente}
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
                                        key={reserva.id}
                                        className="flex items-center justify-between p-3 rounded-lg bg-rose-50/50 hover:bg-rose-100/50 dark:bg-rose-900/10 dark:hover:bg-rose-900/20 cursor-pointer transition-colors border border-rose-100 dark:border-rose-900/20"
                                        onClick={() => navigate(`/reservas/${reserva.id}`)}
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
                                                ${reserva.balance?.toLocaleString()}
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
                                        key={trip.id}
                                        className="flex items-center justify-between p-3 rounded-lg bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/50 dark:hover:bg-slate-800 cursor-pointer transition-colors border border-slate-100 dark:border-slate-800"
                                        onClick={() => navigate(`/reservas/${trip.id}`)}
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
                                                {new Date(trip.startDate).toLocaleDateString()}
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

function KpiCard({ title, value, icon: Icon, color, bg, trend }) {
    return (
        <Card className={`border-none shadow-sm ${bg} transition-all hover:scale-[1.02] cursor-default`}>
            <CardContent className="p-6">
                <div className="flex items-center justify-between space-y-0">
                    <p className={`text-sm font-medium ${color} opacity-80`}>{title}</p>
                    <Icon className={`h-4 w-4 ${color}`} />
                </div>
                <div className="mt-2 flex items-baseline gap-2">
                    <span className={`text-3xl font-bold ${color}`}>
                        ${value?.toLocaleString() || '0'}
                    </span>
                </div>
                {trend && (
                    <p className={`text-xs ${color} mt-1 opacity-70`}>{trend}</p>
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
    const styles = {
        'Presupuesto': 'bg-slate-100 text-slate-600',
        'Reservado': 'bg-amber-100 text-amber-700',
        'Operativo': 'bg-emerald-100 text-emerald-700',
        'Cerrado': 'bg-indigo-100 text-indigo-700',
        'Cancelado': 'bg-red-100 text-red-700'
    };

    // Map English status if necessary or just default
    const mapStatus = {
        'Budget': 'Presupuesto',
        'Reserved': 'Reservado',
        'Operational': 'Operativo',
        'Closed': 'Cerrado',
        'Cancelled': 'Cancelado'
    }[status] || status;

    return (
        <span className={`text-[10px] font-semibold px-2 py-0.5 rounded-full ${styles[mapStatus] || 'bg-slate-100'}`}>
            {mapStatus}
        </span>
    );
}
