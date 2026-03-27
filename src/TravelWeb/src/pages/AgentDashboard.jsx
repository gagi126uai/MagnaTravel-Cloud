import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Briefcase, Calendar, FileText, TrendingUp } from "lucide-react";
import { api } from "../api";
import { useAuthState } from "../auth";
import { BnaUsdSellerRateCard } from "../components/BnaUsdSellerRateCard";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "../components/ui/card";
import { DashboardSkeleton } from "../components/ui/skeleton";
import { getPublicId } from "../lib/publicIds";

export default function AgentDashboard() {
    const { user } = useAuthState();
    const [dashboard, setDashboard] = useState(null);
    const [loading, setLoading] = useState(true);
    const navigate = useNavigate();

    const loadDashboard = async () => {
        try {
            setDashboard(await api.get("/reports/dashboard"));
        } catch (error) {
            console.error("Error loading agent dashboard:", error.message);
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        loadDashboard();
        const interval = setInterval(loadDashboard, 300000);
        return () => clearInterval(interval);
    }, []);

    if (loading) return <DashboardSkeleton />;

    if (!dashboard) {
        return (
            <div className="py-12 text-center">
                <p className="text-muted-foreground">No se pudieron cargar tus metricas.</p>
            </div>
        );
    }

    return (
        <div className="space-y-8 animate-in fade-in duration-500">
            <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
                <div>
                    <h2 className="text-3xl font-bold tracking-tight text-slate-900 dark:text-white">Hola, {user?.name || "Asesor"}</h2>
                    <p className="mt-1 text-muted-foreground">Este es tu resumen operativo del dia.</p>
                </div>
                <div className="flex flex-wrap gap-2">
                    <button type="button" onClick={() => navigate("/quotes?create=1")} className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-bold text-white shadow-lg shadow-indigo-500/20 transition-colors hover:bg-indigo-700">
                        <FileText className="h-4 w-4" />
                        Nueva cotizacion
                    </button>
                    <button type="button" onClick={() => navigate("/crm")} className="inline-flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2.5 text-sm font-bold text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800">
                        <Briefcase className="h-4 w-4" />
                        Posibles clientes
                    </button>
                </div>
            </div>

            <BnaUsdSellerRateCard rate={dashboard.bnaUsdSellerRate} />

            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
                <KpiCard title="Ventas personales" value={dashboard.ventasDelMes || 0} icon={TrendingUp} color="text-indigo-600 dark:text-indigo-400" bg="bg-indigo-50 dark:bg-indigo-900/10" />
                <KpiCard title="Proximas salidas" value={dashboard.proximosViajes?.length || 0} icon={Calendar} color="text-emerald-600 dark:text-emerald-400" bg="bg-emerald-50 dark:bg-emerald-900/10" isCurrency={false} />
                <KpiCard title="Posibles clientes activos" value={dashboard.activePotentialCustomers || 0} icon={Briefcase} color="text-amber-600 dark:text-amber-400" bg="bg-amber-50 dark:bg-amber-900/10" isCurrency={false} />
            </div>

            <div className="grid gap-6 md:grid-cols-2">
                <Card className="shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2 text-blue-600 dark:text-blue-400">
                            <Calendar className="h-5 w-5" />
                            Mis proximas salidas
                        </CardTitle>
                        <CardDescription>Viajes proximos a salir</CardDescription>
                    </CardHeader>
                    <CardContent>
                        <div className="space-y-4">
                            {dashboard.proximosViajes?.length > 0 ? dashboard.proximosViajes.map((trip) => (
                                <div key={getPublicId(trip)} className="flex cursor-pointer items-center justify-between rounded-lg border border-slate-100 bg-slate-50 p-3 transition-colors hover:bg-slate-100 dark:border-slate-800 dark:bg-slate-800/50 dark:hover:bg-slate-800" onClick={() => navigate(`/reservas/${getPublicId(trip)}`)}>
                                    <div>
                                        <div className="font-medium text-slate-800 dark:text-slate-200">{trip.name}</div>
                                        <div className="text-xs text-muted-foreground">{trip.numeroReserva}</div>
                                    </div>
                                    <div className="text-right">
                                        <div className="font-medium text-blue-600 dark:text-blue-400">{new Date(trip.startDate).toLocaleDateString()}</div>
                                        <span className="rounded-full bg-slate-200 px-2 py-0.5 text-[10px] font-semibold text-slate-700">{trip.status}</span>
                                    </div>
                                </div>
                            )) : <EmptyState message="No tienes salidas proximas" />}
                        </div>
                    </CardContent>
                </Card>

                <Card className="shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2 text-indigo-600 dark:text-indigo-400">
                            <Briefcase className="h-5 w-5" />
                            Accesos rapidos
                        </CardTitle>
                        <CardDescription>Entradas directas para seguir vendiendo</CardDescription>
                    </CardHeader>
                    <CardContent className="space-y-3">
                        <ActionButton label="Nueva cotizacion" onClick={() => navigate("/quotes?create=1")} icon={FileText} />
                        <ActionButton label="Posibles clientes" onClick={() => navigate("/crm")} icon={Briefcase} />
                        <ActionButton label="Clientes" onClick={() => navigate("/customers")} icon={Calendar} />
                    </CardContent>
                </Card>
            </div>
        </div>
    );
}

function KpiCard({ title, value, icon: Icon, color, bg, isCurrency = true }) {
    return (
        <Card className={`border-none shadow-sm ${bg} transition-all hover:scale-[1.02] cursor-default`}>
            <CardContent className="p-6">
                <div className="flex items-center justify-between">
                    <p className={`text-sm font-medium ${color} opacity-80`}>{title}</p>
                    <Icon className={`h-4 w-4 ${color}`} />
                </div>
                <div className={`mt-2 text-3xl font-bold ${color}`}>{isCurrency ? `$${value?.toLocaleString() || "0"}` : value}</div>
            </CardContent>
        </Card>
    );
}

function ActionButton({ label, onClick, icon: Icon }) {
    return (
        <button type="button" onClick={onClick} className="flex w-full items-center justify-between rounded-2xl border border-slate-200 bg-white px-4 py-3 text-left text-sm font-bold text-slate-700 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:hover:bg-slate-800">
            <span>{label}</span>
            <Icon className="h-4 w-4 text-indigo-500" />
        </button>
    );
}

function EmptyState({ message }) {
    return (
        <div className="flex flex-col items-center py-8 text-center text-muted-foreground">
            <div className="mb-3 rounded-full bg-slate-100 p-3 dark:bg-slate-800">
                <FileText className="h-6 w-6 opacity-30" />
            </div>
            <p className="text-sm">{message}</p>
        </div>
    );
}
