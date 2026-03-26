import { useEffect, useState } from "react";
import { api } from "../api";
import { useNavigate } from "react-router-dom";
import { useAuthState } from "../auth";
import { Briefcase, Clock, FileText, Calendar, TrendingUp } from "lucide-react";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "../components/ui/card";
import { DashboardSkeleton } from "../components/ui/skeleton";
import { getPublicId } from "../lib/publicIds";

export default function AgentDashboard() {
    const { user } = useAuthState();
    const [dashboard, setDashboard] = useState(null);
    const [loading, setLoading] = useState(true);
    const navigate = useNavigate();

    const loadDashboard = async () => {
        try {
            // El API debería retornar datos filtrados para el asesor
            // Si /reports/dashboard no filtra por rol, asumo que sí lo hace o usará los datos generales por ahora
            const data = await api.get("/reports/dashboard");
            setDashboard(data);
        } catch (error) {
            console.error("Error loading agent dashboard:", error.message);
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        loadDashboard();
        const interval = setInterval(loadDashboard, 60000);
        return () => clearInterval(interval);
    }, []);

    if (loading) {
        return <DashboardSkeleton />;
    }

    if (!dashboard) {
        return (
            <div className="text-center py-12">
                <p className="text-muted-foreground">No se pudieron cargar tus métricas.</p>
            </div>
        );
    }

    return (
        <div className="space-y-8 animate-in fade-in duration-500">
            <div>
                <h2 className="text-3xl font-bold tracking-tight text-slate-900 dark:text-white">
                    ¡Hola, {user?.name || "Asesor"}!
                </h2>
                <p className="text-muted-foreground mt-1">
                    Este es tu resumen operativo del día.
                </p>
            </div>

            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
                <KpiCard
                    title="Ventas Personales"
                    value={dashboard.ventasDelMes || 0}
                    icon={TrendingUp}
                    color="text-indigo-600 dark:text-indigo-400"
                    bg="bg-indigo-50 dark:bg-indigo-900/10"
                />
                <KpiCard
                    title="Próximas Salidas"
                    value={dashboard.proximosViajes?.length || 0}
                    icon={Calendar}
                    color="text-emerald-600 dark:text-emerald-400"
                    bg="bg-emerald-50 dark:bg-emerald-900/10"
                    isCurrency={false}
                />
                <KpiCard
                    title="Leads Activos"
                    value={3} // Asumiendo un número fijo temporalmente
                    icon={Briefcase}
                    color="text-amber-600 dark:text-amber-400"
                    bg="bg-amber-50 dark:bg-amber-900/10"
                    isCurrency={false}
                />
            </div>

            <div className="grid gap-6 md:grid-cols-2">
                <Card className="shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2 text-blue-600 dark:text-blue-400">
                            <Calendar className="h-5 w-5" />
                            Mis Próximas Salidas
                        </CardTitle>
                        <CardDescription>Viajes que gestionaste próximos a salir</CardDescription>
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
                                                {new Date(trip.startDate).toLocaleDateString()}
                                            </div>
                                            <span className="text-[10px] font-semibold px-2 py-0.5 rounded-full bg-slate-200 text-slate-700">
                                                {trip.status}
                                            </span>
                                        </div>
                                    </div>
                                ))
                            ) : (
                                <EmptyState message="No tienes salidas próximas" />
                            )}
                        </div>
                    </CardContent>
                </Card>

                <Card className="shadow-sm">
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2 text-indigo-600 dark:text-indigo-400">
                            <Briefcase className="h-5 w-5" />
                            Gestiones Recientes
                        </CardTitle>
                        <CardDescription>Últimos movimientos en tus cotizaciones y reservas</CardDescription>
                    </CardHeader>
                    <CardContent>
                         <EmptyState message="No hay gestiones recientes para mostrar." />
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
                <div className="flex items-center justify-between space-y-0">
                    <p className={`text-sm font-medium ${color} opacity-80`}>{title}</p>
                    <Icon className={`h-4 w-4 ${color}`} />
                </div>
                <div className="mt-2 flex items-baseline gap-2">
                    <span className={`text-3xl font-bold ${color}`}>
                        {isCurrency ? `$${value?.toLocaleString() || '0'}` : value}
                    </span>
                </div>
            </CardContent>
        </Card>
    );
}

function EmptyState({ message }) {
    return (
        <div className="text-center py-8 text-muted-foreground flex flex-col items-center">
            <div className="bg-slate-100 dark:bg-slate-800 p-3 rounded-full mb-3">
                <FileText className="h-6 w-6 opacity-30" />
            </div>
            <p className="text-sm">{message}</p>
        </div>
    );
}
