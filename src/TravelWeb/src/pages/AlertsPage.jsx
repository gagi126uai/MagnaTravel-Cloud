import { useNavigate } from "react-router-dom";
import { useAlerts } from "../contexts/AlertsContext";
import { format } from "date-fns";
import { es } from "date-fns/locale";
import { AlertTriangle, TrendingUp, Phone, Calendar, ArrowRight, CheckCircle } from "lucide-react";

export default function AlertsPage() {
    const { alerts, loading } = useAlerts();
    const navigate = useNavigate();

    if (loading) return <div className="p-8 text-center text-gray-500">Cargando alertas...</div>;

    const hasAlerts = alerts.TotalCount > 0;

    return (
        <div className="max-w-7xl mx-auto p-4 sm:p-6 lg:p-8">
            <h1 className="text-3xl font-bold text-gray-900 dark:text-white mb-2">Centro de Alertas</h1>
            <p className="text-gray-500 dark:text-slate-400 mb-8">
                Visualizá de forma rápida los temas que requieren tu atención inmediata.
            </p>

            {!hasAlerts && (
                <div className="bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-xl p-8 text-center">
                    <CheckCircle className="w-12 h-12 text-green-500 mx-auto mb-3" />
                    <h3 className="text-lg font-medium text-green-800 dark:text-green-300">¡Todo al día!</h3>
                    <p className="text-green-600 dark:text-green-400">No hay alertas pendientes por el momento.</p>
                </div>
            )}

            <div className="grid grid-cols-1 lg:grid-cols-2 gap-8">
                {/* URGENT TRIPS */}
                {alerts.UrgentTrips?.length > 0 && (
                    <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-red-200 dark:border-red-900 overflow-hidden">
                        <div className="p-4 border-b border-red-100 dark:border-red-900/50 bg-red-50 dark:bg-red-900/20 flex justify-between items-center">
                            <div className="flex items-center gap-2">
                                <AlertTriangle className="w-5 h-5 text-red-600 dark:text-red-400" />
                                <h2 className="font-semibold text-red-800 dark:text-red-300">Viajes Próximos Impagos</h2>
                            </div>
                            <span className="bg-red-200 dark:bg-red-800 text-red-800 dark:text-red-200 text-xs px-2 py-1 rounded-full font-medium">
                                {alerts.UrgentTrips.length}
                            </span>
                        </div>
                        <div className="divide-y divide-gray-100 dark:divide-slate-700">
                            {alerts.UrgentTrips.map(trip => (
                                <div key={trip.id} className="p-4 hover:bg-gray-50 dark:hover:bg-slate-700/50 transition-colors">
                                    <div className="flex justify-between items-start mb-2">
                                        <div>
                                            <p className="font-medium text-gray-900 dark:text-white flex items-center gap-2">
                                                {trip.name}
                                                <span className="text-xs bg-gray-100 dark:bg-slate-700 text-gray-600 dark:text-slate-300 px-1.5 py-0.5 rounded border border-gray-200 dark:border-slate-600">
                                                    #{trip.numeroReserva}
                                                </span>
                                            </p>
                                            <p className="text-sm text-gray-500 dark:text-slate-400">{trip.payerName}</p>
                                        </div>
                                        <div className="text-right">
                                            <span className="block text-sm font-bold text-red-600 dark:text-red-400">
                                                Debe: ${trip.balance?.toLocaleString()}
                                            </span>
                                            <div className="flex items-center justify-end text-xs text-amber-600 dark:text-amber-400 mt-1 gap-1">
                                                <Calendar className="w-3 h-3" />
                                                Salida: {format(new Date(trip.startDate), "d MMM", { locale: es })}
                                            </div>
                                        </div>
                                    </div>
                                    <button
                                        onClick={() => navigate(`/reservas/${trip.id}`)}
                                        className="w-full mt-2 text-sm text-center py-1.5 rounded border border-gray-200 dark:border-slate-700 text-gray-600 dark:text-slate-300 hover:bg-white dark:hover:bg-slate-600 hover:border-blue-300 dark:hover:border-blue-500 hover:text-blue-600 dark:hover:text-blue-400 transition-all flex items-center justify-center gap-1 group"
                                    >
                                        Ver Reserva <ArrowRight className="w-3 h-3 group-hover:translate-x-1 transition-transform" />
                                    </button>
                                </div>
                            ))}
                        </div>
                    </div>
                )}

                {/* SUPPLIER DEBTS */}
                {alerts.SupplierDebts?.length > 0 && (
                    <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-amber-200 dark:border-amber-900 overflow-hidden">
                        <div className="p-4 border-b border-amber-100 dark:border-amber-900/50 bg-amber-50 dark:bg-amber-900/20 flex justify-between items-center">
                            <div className="flex items-center gap-2">
                                <TrendingUp className="w-5 h-5 text-amber-600 dark:text-amber-400" />
                                <h2 className="font-semibold text-amber-800 dark:text-amber-300">Deuda con Proveedores</h2>
                            </div>
                            <span className="bg-amber-200 dark:bg-amber-800 text-amber-800 dark:text-amber-200 text-xs px-2 py-1 rounded-full font-medium">
                                {alerts.SupplierDebts.length}
                            </span>
                        </div>
                        <div className="divide-y divide-gray-100 dark:divide-slate-700">
                            {alerts.SupplierDebts.map(sup => (
                                <div key={sup.id} className="p-4 hover:bg-gray-50 dark:hover:bg-slate-700/50 transition-colors">
                                    <div className="flex justify-between items-center">
                                        <div>
                                            <p className="font-medium text-gray-900 dark:text-white">{sup.name}</p>
                                            {sup.phone && (
                                                <p className="text-xs text-gray-500 dark:text-slate-400 flex items-center gap-1 mt-0.5">
                                                    <Phone className="w-3 h-3" /> {sup.phone}
                                                </p>
                                            )}
                                        </div>
                                        <div className="text-right">
                                            <span className="text-sm font-bold text-red-600 dark:text-red-400 block">
                                                ${sup.currentBalance?.toLocaleString()}
                                            </span>
                                            <button
                                                onClick={() => navigate(`/suppliers/${sup.id}/account`)}
                                                className="text-xs text-blue-600 dark:text-blue-400 hover:underline mt-1"
                                            >
                                                Ver Cuenta
                                            </button>
                                        </div>
                                    </div>
                                </div>
                            ))}
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
}
