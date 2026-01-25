import { useEffect, useState } from "react";
import { api } from "../api";
import { showError } from "../alerts";
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
    Plane
} from "lucide-react";
import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle
} from "../components/ui/card";
import { Button } from "../components/ui/button";

export default function DashboardPage() {
    const [dashboard, setDashboard] = useState(null);
    const [loading, setLoading] = useState(true);
    const navigate = useNavigate();

    useEffect(() => {
        loadDashboard();
    }, []);

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

    if (loading) {
        return (
            <div className="flex items-center justify-center h-64">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
            </div>
        );
    }

    if (!dashboard) {
        return (
            <div className="text-center py-12">
                <p className="text-muted-foreground">No se pudieron cargar las métricas.</p>
            </div>
        );
    }

    return (
        <div className="space-y-6">
            {/* Header */}
            <div className="flex flex-col gap-2 md:flex-row md:items-center md:justify-between">
                <div>
                    <h2 className="text-3xl font-bold tracking-tight">Dashboard</h2>
                    <p className="text-muted-foreground">
                        Resumen ejecutivo de la operación diaria.
                    </p>
                </div>
                <div className="flex items-center gap-2 rounded-full border bg-card px-4 py-1 text-xs font-semibold uppercase tracking-wider text-muted-foreground shadow-sm">
                    <div className="h-2 w-2 rounded-full bg-green-500 animate-pulse"></div>
                    En tiempo real
                </div>
            </div>

            {/* Main Stats */}
            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
                {/* Presupuestos */}
                <Card
                    className="cursor-pointer hover:shadow-md transition-shadow border-blue-200 bg-blue-50/50 dark:bg-blue-900/10 dark:border-blue-800"
                    onClick={() => navigate("/files")}
                >
                    <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                        <CardTitle className="text-sm font-medium text-blue-700 dark:text-blue-300">Presupuestos</CardTitle>
                        <FileText className="h-4 w-4 text-blue-600" />
                    </CardHeader>
                    <CardContent>
                        <div className="text-3xl font-bold text-blue-700 dark:text-blue-300">{dashboard.presupuestos}</div>
                        <p className="text-xs text-blue-600/70">Borradores sin confirmar</p>
                    </CardContent>
                </Card>

                {/* Reservados */}
                <Card
                    className="cursor-pointer hover:shadow-md transition-shadow border-amber-200 bg-amber-50/50 dark:bg-amber-900/10 dark:border-amber-800"
                    onClick={() => navigate("/files")}
                >
                    <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                        <CardTitle className="text-sm font-medium text-amber-700 dark:text-amber-300">Reservados</CardTitle>
                        <Clock className="h-4 w-4 text-amber-600" />
                    </CardHeader>
                    <CardContent>
                        <div className="text-3xl font-bold text-amber-700 dark:text-amber-300">{dashboard.reservados}</div>
                        <p className="text-xs text-amber-600/70">Ventas confirmadas</p>
                    </CardContent>
                </Card>

                {/* Operativos */}
                <Card
                    className="cursor-pointer hover:shadow-md transition-shadow border-emerald-200 bg-emerald-50/50 dark:bg-emerald-900/10 dark:border-emerald-800"
                    onClick={() => navigate("/files")}
                >
                    <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                        <CardTitle className="text-sm font-medium text-emerald-700 dark:text-emerald-300">Operativos</CardTitle>
                        <Briefcase className="h-4 w-4 text-emerald-600" />
                    </CardHeader>
                    <CardContent>
                        <div className="text-3xl font-bold text-emerald-700 dark:text-emerald-300">{dashboard.operativos}</div>
                        <p className="text-xs text-emerald-600/70">En proceso de viaje</p>
                    </CardContent>
                </Card>

                {/* Saldo Pendiente */}
                <Card className="border-rose-200 bg-rose-50/50 dark:bg-rose-900/10 dark:border-rose-800">
                    <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                        <CardTitle className="text-sm font-medium text-rose-700 dark:text-rose-300">Saldo Pendiente</CardTitle>
                        <AlertCircle className="h-4 w-4 text-rose-600" />
                    </CardHeader>
                    <CardContent>
                        <div className="text-3xl font-bold text-rose-700 dark:text-rose-300">${dashboard.saldoPendiente?.toLocaleString()}</div>
                        <p className="text-xs text-rose-600/70">Por cobrar en expedientes activos</p>
                    </CardContent>
                </Card>
            </div>

            {/* Financial Stats */}
            <div className="grid gap-4 md:grid-cols-2">
                <Card className="border-indigo-200 bg-gradient-to-br from-indigo-50 to-white dark:from-indigo-900/20 dark:to-slate-900 dark:border-indigo-800">
                    <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                        <CardTitle className="text-sm font-medium text-indigo-700 dark:text-indigo-300">Cobros del Mes</CardTitle>
                        <DollarSign className="h-4 w-4 text-indigo-600" />
                    </CardHeader>
                    <CardContent>
                        <div className="text-4xl font-bold text-indigo-700 dark:text-indigo-300">${dashboard.cobrosDelMes?.toLocaleString()}</div>
                        <p className="text-xs text-indigo-600/70 mt-1">Ingresado este mes</p>
                    </CardContent>
                </Card>

                <Card className="border-purple-200 bg-gradient-to-br from-purple-50 to-white dark:from-purple-900/20 dark:to-slate-900 dark:border-purple-800">
                    <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
                        <CardTitle className="text-sm font-medium text-purple-700 dark:text-purple-300">Ventas del Mes</CardTitle>
                        <TrendingUp className="h-4 w-4 text-purple-600" />
                    </CardHeader>
                    <CardContent>
                        <div className="text-4xl font-bold text-purple-700 dark:text-purple-300">${dashboard.ventasDelMes?.toLocaleString()}</div>
                        <p className="text-xs text-purple-600/70 mt-1">Total vendido (expedientes nuevos)</p>
                    </CardContent>
                </Card>
            </div>

            {/* Lists */}
            <div className="grid gap-6 lg:grid-cols-2">
                {/* Expedientes con Saldo Pendiente */}
                <Card>
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2">
                            <AlertCircle className="h-5 w-5 text-rose-500" />
                            Expedientes por Cobrar
                        </CardTitle>
                        <CardDescription>Top 5 con mayor saldo pendiente</CardDescription>
                    </CardHeader>
                    <CardContent>
                        {dashboard.expedientesPendientes?.length > 0 ? (
                            <div className="space-y-3">
                                {dashboard.expedientesPendientes.map((file) => (
                                    <div
                                        key={file.id}
                                        className="flex items-center justify-between p-3 rounded-lg bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/50 dark:hover:bg-slate-800 cursor-pointer transition-colors"
                                        onClick={() => navigate(`/files/${file.id}`)}
                                    >
                                        <div>
                                            <div className="font-medium">{file.name}</div>
                                            <div className="text-xs text-muted-foreground">{file.fileNumber}</div>
                                        </div>
                                        <div className="text-right">
                                            <div className="font-bold text-rose-600">${file.balance?.toLocaleString()}</div>
                                            <ArrowRight className="h-4 w-4 text-muted-foreground inline-block" />
                                        </div>
                                    </div>
                                ))}
                            </div>
                        ) : (
                            <div className="text-center py-8 text-muted-foreground">
                                <DollarSign className="h-8 w-8 mx-auto mb-2 opacity-30" />
                                <p>No hay expedientes con saldo pendiente</p>
                            </div>
                        )}
                    </CardContent>
                </Card>

                {/* Próximos Viajes */}
                <Card>
                    <CardHeader>
                        <CardTitle className="flex items-center gap-2">
                            <Plane className="h-5 w-5 text-blue-500" />
                            Próximos Viajes
                        </CardTitle>
                        <CardDescription>Salidas en los próximos 7 días</CardDescription>
                    </CardHeader>
                    <CardContent>
                        {dashboard.proximosViajes?.length > 0 ? (
                            <div className="space-y-3">
                                {dashboard.proximosViajes.map((trip) => (
                                    <div
                                        key={trip.id}
                                        className="flex items-center justify-between p-3 rounded-lg bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/50 dark:hover:bg-slate-800 cursor-pointer transition-colors"
                                        onClick={() => navigate(`/files/${trip.id}`)}
                                    >
                                        <div>
                                            <div className="font-medium">{trip.name}</div>
                                            <div className="text-xs text-muted-foreground">{trip.fileNumber}</div>
                                        </div>
                                        <div className="text-right">
                                            <div className="flex items-center gap-1 text-blue-600 font-medium">
                                                <Calendar className="h-3 w-3" />
                                                {new Date(trip.startDate).toLocaleDateString()}
                                            </div>
                                        </div>
                                    </div>
                                ))}
                            </div>
                        ) : (
                            <div className="text-center py-8 text-muted-foreground">
                                <Calendar className="h-8 w-8 mx-auto mb-2 opacity-30" />
                                <p>No hay viajes próximos en los siguientes 7 días</p>
                            </div>
                        )}
                    </CardContent>
                </Card>
            </div>
        </div>
    );
}
