import { useState, useEffect } from "react";
import { X, User, Calendar, Search } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { getPublicId } from "../lib/publicIds";

/**
 * Modal para crear una nueva reserva.
 *
 * Toda propuesta nueva nace como RESERVA-PRESUPUESTO (Budget).
 * Ya no existe un circuito paralelo para crear cotizaciones legacy.
 *
 * Solo campos imprescindibles: cliente y fecha de inicio (opcional).
 */
export default function CreateReservaModal({ isOpen, onClose, onSuccess, initialPayerId = "" }) {
    const [bgOpacity, setBgOpacity] = useState("opacity-0");
    const [scale, setScale] = useState("scale-95 opacity-0");

    const [formData, setFormData] = useState({
        payerId: "",
        startDate: "",
    });

    const [loading, setLoading] = useState(false);
    const [customers, setCustomers] = useState([]);
    const [searchTerm, setSearchTerm] = useState("");

    // useEffect con dep [isOpen]: carga los clientes y anima el modal cuando se abre.
    useEffect(() => {
        if (!isOpen) return undefined;

        setFormData({ payerId: initialPayerId || "", startDate: "" });
        setSearchTerm("");

        const timeoutId = setTimeout(() => {
            setBgOpacity("opacity-100");
            setScale("scale-100 opacity-100");
        }, 10);
        loadCustomers();

        return () => clearTimeout(timeoutId);
    }, [isOpen, initialPayerId]);

    const loadCustomers = async () => {
        try {
            const data = await api.get("/customers?page=1&pageSize=100&sortBy=fullName&sortDir=asc");
            setCustomers(data?.items || []);
        } catch (err) {
            console.error(err);
        }
    };

    const handleClose = () => {
        setBgOpacity("opacity-0");
        setScale("scale-95 opacity-0");
        setTimeout(onClose, 200);
    };

    const handleSubmit = async (e) => {
        e.preventDefault();

        if (!formData.payerId) {
            showError("Por favor selecciona un cliente principal");
            return;
        }

        setLoading(true);
        try {
            const res = await api.post("/reservas", {
                name: "",
                payerId: formData.payerId || null,
                startDate: formData.startDate ? new Date(formData.startDate).toISOString() : null,
            });
            showSuccess("Presupuesto creado");
            onSuccess(getPublicId(res));
            handleClose();
        } catch (error) {
            showError(error.message || "Error al crear la reserva");
        } finally {
            setLoading(false);
        }
    };

    const filteredCustomers = customers.filter(c =>
        c.fullName.toLowerCase().includes(searchTerm.toLowerCase()) ||
        c.taxId?.includes(searchTerm)
    );

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 sm:p-6 text-slate-800 dark:text-slate-100">
            <div className={`fixed inset-0 bg-black/60 backdrop-blur-sm transition-opacity duration-300 ${bgOpacity}`} />

            <div className={`relative w-full max-w-lg bg-white dark:bg-slate-900 rounded-2xl shadow-2xl border border-slate-200 dark:border-slate-800 overflow-hidden transition-all duration-300 transform ${scale}`}>

                {/* Header */}
                <div className="flex items-center justify-between px-6 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-900/50">
                    <div>
                        <h2 className="text-xl font-bold bg-gradient-to-r from-indigo-600 to-violet-600 bg-clip-text text-transparent">
                            Nuevo Presupuesto
                        </h2>
                        <p className="text-sm text-slate-500 dark:text-slate-400">
                            Crea la reserva en estado Presupuesto y carga sus servicios.
                        </p>
                    </div>
                    <button
                        onClick={handleClose}
                        className="p-2 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-full transition-colors text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
                    >
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="p-6 space-y-6">

                    {/* Seleccion del cliente */}
                    <div className="space-y-2">
                        <label className="text-sm font-medium flex items-center gap-2 text-slate-700 dark:text-slate-300">
                            <User className="h-4 w-4 text-slate-400" />
                            Cliente Principal <span className="text-red-500">*</span>
                        </label>

                        {/* Buscador rapido para filtrar el listado */}
                        <div className="relative mb-2">
                            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-3.5 w-3.5 text-slate-400" />
                            <input
                                type="text"
                                placeholder="Buscar cliente..."
                                value={searchTerm}
                                onChange={(e) => setSearchTerm(e.target.value)}
                                className="w-full pl-9 pr-3 py-2 rounded-lg border border-slate-300 dark:border-slate-700 bg-slate-50 dark:bg-slate-800/50 text-sm focus:ring-2 focus:ring-indigo-500 focus:border-transparent outline-none"
                            />
                        </div>

                        <div className="relative">
                            <select
                                value={formData.payerId}
                                onChange={(e) => setFormData({ ...formData, payerId: e.target.value })}
                                className="w-full px-4 py-2.5 rounded-lg border border-slate-300 dark:border-slate-700 bg-slate-50 dark:bg-slate-800/50 focus:bg-white dark:focus:bg-slate-800 focus:ring-2 focus:ring-indigo-500 focus:border-transparent transition-all outline-none appearance-none"
                            >
                                <option value="">Seleccionar cliente...</option>
                                {filteredCustomers.map(c => (
                                    <option key={getPublicId(c)} value={getPublicId(c)}>{c.fullName}</option>
                                ))}
                            </select>
                        </div>
                        <p className="text-xs text-slate-500">Se usara para facturacion y contacto.</p>
                    </div>

                    {/* Fecha de inicio (opcional) */}
                    <div className="space-y-2">
                        <label className="text-sm font-medium flex items-center gap-2 text-slate-700 dark:text-slate-300">
                            <Calendar className="h-4 w-4 text-slate-400" />
                            Fecha de Inicio (Opcional)
                        </label>
                        <input
                            type="date"
                            value={formData.startDate}
                            onChange={(e) => setFormData({ ...formData, startDate: e.target.value })}
                            className="w-full px-4 py-2.5 rounded-lg border border-slate-300 dark:border-slate-700 bg-slate-50 dark:bg-slate-800/50 focus:bg-white dark:focus:bg-slate-800 focus:ring-2 focus:ring-indigo-500 focus:border-transparent transition-all outline-none"
                        />
                    </div>

                    {/* Acciones */}
                    <div className="flex items-center justify-end gap-3 pt-4 border-t border-slate-100 dark:border-slate-800">
                        <button
                            type="button"
                            onClick={handleClose}
                            className="px-5 py-2.5 rounded-lg text-sm font-medium text-slate-600 dark:text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={loading}
                            className={`px-6 py-2.5 rounded-lg text-sm font-bold text-white shadow-lg shadow-indigo-500/20 transition-all hover:scale-[1.02] active:scale-[0.98] ${loading ? 'bg-indigo-400 cursor-not-allowed' : 'bg-indigo-600 hover:bg-indigo-700'}`}
                        >
                            {loading ? (
                                <span className="flex items-center gap-2">
                                    <div className="h-4 w-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />
                                    Creando...
                                </span>
                            ) : (
                                "Crear Presupuesto"
                            )}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
