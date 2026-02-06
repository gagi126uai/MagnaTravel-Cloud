import { useState, useEffect } from "react";
import { X, User, Calendar, FileText, CheckCircle2, Search } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";

export default function CreateFileModal({ isOpen, onClose, onSuccess }) {
    if (!isOpen) return null;

    const [bgOpacity, setBgOpacity] = useState("opacity-0");
    const [scale, setScale] = useState("scale-95 opacity-0");

    // Form State
    const [formData, setFormData] = useState({
        payerId: "",
        startDate: "",
        isBudget: true // true = Presupuesto, false = Reserva Confirmada
    });

    const [loading, setLoading] = useState(false);
    const [customers, setCustomers] = useState([]);
    const [searchTerm, setSearchTerm] = useState("");

    useEffect(() => {
        // Animation entrance
        setTimeout(() => {
            setBgOpacity("opacity-100");
            setScale("scale-100 opacity-100");
        }, 10);
        loadCustomers();
    }, []);

    const loadCustomers = async () => {
        try {
            const data = await api.get("/customers");
            setCustomers(data);
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
            await api.post("/travelfiles", {
                name: "", // Backend will auto-generate
                payerId: formData.payerId ? parseInt(formData.payerId) : null,
                startDate: formData.startDate ? new Date(formData.startDate).toISOString() : null,
                status: formData.isBudget ? 'Presupuesto' : 'Reservado'
            });
            showSuccess("Expediente creado exitosamente");
            onSuccess();
            handleClose();
        } catch (error) {
            showError(error.response?.data?.message || "Error al crear el expediente");
        } finally {
            setLoading(false);
        }
    };

    // Filter customers for the search dropdown
    const filteredCustomers = customers.filter(c =>
        c.fullName.toLowerCase().includes(searchTerm.toLowerCase()) ||
        c.taxId?.includes(searchTerm)
    );

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 sm:p-6 text-slate-800 dark:text-slate-100">
            {/* Backdrop */}
            <div
                className={`fixed inset-0 bg-black/60 backdrop-blur-sm transition-opacity duration-300 ${bgOpacity}`}
                onClick={handleClose}
            />

            {/* Modal Content */}
            <div className={`relative w-full max-w-2xl bg-white dark:bg-slate-900 rounded-2xl shadow-2xl border border-slate-200 dark:border-slate-800 overflow-hidden transition-all duration-300 transform ${scale}`}>

                {/* Header */}
                <div className="flex items-center justify-between px-6 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-900/50">
                    <div>
                        <h2 className="text-xl font-bold bg-gradient-to-r from-indigo-600 to-violet-600 bg-clip-text text-transparent">
                            Nuevo Expediente
                        </h2>
                        <p className="text-sm text-slate-500 dark:text-slate-400">
                            Comienza a planificar un nuevo viaje
                        </p>
                    </div>
                    <button
                        onClick={handleClose}
                        className="p-2 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-full transition-colors text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
                    >
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="p-6 space-y-8">

                    {/* Step 1: File Type Selection */}
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                        <label
                            className={`cursor-pointer group relative flex flex-col items-center justify-center p-6 rounded-xl border-2 transition-all duration-200 ${formData.isBudget
                                ? 'border-indigo-500 bg-indigo-50/50 dark:bg-indigo-900/10'
                                : 'border-slate-200 dark:border-slate-700 hover:border-indigo-200 dark:hover:border-indigo-800 bg-white dark:bg-slate-900 hover:bg-slate-50 dark:hover:bg-slate-800/50'}`}
                        >
                            <input
                                type="radio"
                                name="fileType"
                                className="sr-only"
                                checked={formData.isBudget}
                                onChange={() => setFormData({ ...formData, isBudget: true })}
                            />
                            <div className={`h-12 w-12 rounded-full flex items-center justify-center mb-3 transition-colors ${formData.isBudget ? 'bg-indigo-100 text-indigo-600 dark:bg-indigo-900/50 dark:text-indigo-300' : 'bg-slate-100 text-slate-400 dark:bg-slate-800'}`}>
                                <FileText className="h-6 w-6" />
                            </div>
                            <span className={`font-semibold mb-1 ${formData.isBudget ? 'text-indigo-700 dark:text-indigo-300' : 'text-slate-600 dark:text-slate-300'}`}>Presupuesto</span>
                            <span className="text-xs text-center text-slate-500 dark:text-slate-400">Borrador inicial, cotizaciones y propuestas.</span>

                            {formData.isBudget && (
                                <div className="absolute top-3 right-3 text-indigo-500">
                                    <CheckCircle2 className="h-5 w-5 fill-indigo-100 dark:fill-indigo-900" />
                                </div>
                            )}
                        </label>

                        <label
                            className={`cursor-pointer group relative flex flex-col items-center justify-center p-6 rounded-xl border-2 transition-all duration-200 ${!formData.isBudget
                                ? 'border-emerald-500 bg-emerald-50/50 dark:bg-emerald-900/10'
                                : 'border-slate-200 dark:border-slate-700 hover:border-emerald-200 dark:hover:border-emerald-800 bg-white dark:bg-slate-900 hover:bg-slate-50 dark:hover:bg-slate-800/50'}`}
                        >
                            <input
                                type="radio"
                                name="fileType"
                                className="sr-only"
                                checked={!formData.isBudget}
                                onChange={() => setFormData({ ...formData, isBudget: false })}
                            />
                            <div className={`h-12 w-12 rounded-full flex items-center justify-center mb-3 transition-colors ${!formData.isBudget ? 'bg-emerald-100 text-emerald-600 dark:bg-emerald-900/50 dark:text-emerald-300' : 'bg-slate-100 text-slate-400 dark:bg-slate-800'}`}>
                                <CheckCircle2 className="h-6 w-6" />
                            </div>
                            <span className={`font-semibold mb-1 ${!formData.isBudget ? 'text-emerald-700 dark:text-emerald-300' : 'text-slate-600 dark:text-slate-300'}`}>Reserva Confirmada</span>
                            <span className="text-xs text-center text-slate-500 dark:text-slate-400">Viaje en firme, listo para operar y cobrar.</span>

                            {!formData.isBudget && (
                                <div className="absolute top-3 right-3 text-emerald-500">
                                    <CheckCircle2 className="h-5 w-5 fill-emerald-100 dark:fill-emerald-900" />
                                </div>
                            )}
                        </label>
                    </div>

                    {/* Step 2: Basic Info */}
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-6">

                        {/* Customer Selection */}
                        <div className="sm:col-span-2 space-y-2">
                            <label className="text-sm font-medium flex items-center gap-2 text-slate-700 dark:text-slate-300">
                                <User className="h-4 w-4 text-slate-400" />
                                Cliente Principal <span className="text-red-500">*</span>
                            </label>
                            <div className="relative">
                                <select
                                    value={formData.payerId}
                                    onChange={(e) => setFormData({ ...formData, payerId: e.target.value })}
                                    className="w-full px-4 py-2.5 rounded-lg border border-slate-300 dark:border-slate-700 bg-slate-50 dark:bg-slate-800/50 focus:bg-white dark:focus:bg-slate-800 focus:ring-2 focus:ring-indigo-500 focus:border-transparent transition-all outline-none appearance-none"
                                >
                                    <option value="">Seleccionar cliente...</option>
                                    {customers.map(c => (
                                        <option key={c.id} value={c.id}>{c.fullName}</option>
                                    ))}
                                </select>
                                <div className="absolute inset-y-0 right-0 flex items-center pr-3 pointer-events-none text-slate-500">
                                    <Search className="h-4 w-4" />
                                </div>
                            </div>
                            <p className="text-xs text-slate-500">Se usará para facturación y contacto.</p>
                        </div>

                        {/* Start Date */}
                        <div className="sm:col-span-2 space-y-2">
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
                    </div>

                    {/* Footer Actions */}
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
                            className={`px-6 py-2.5 rounded-lg text-sm font-bold text-white shadow-lg shadow-indigo-500/20 transition-all hover:scale-[1.02] active:scale-[0.98] ${loading
                                ? 'bg-indigo-400 cursor-not-allowed'
                                : 'bg-indigo-600 hover:bg-indigo-700'
                                }`}
                        >
                            {loading ? (
                                <span className="flex items-center gap-2">
                                    <div className="h-4 w-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />
                                    Creando...
                                </span>
                            ) : (
                                "Crear Expediente"
                            )}
                        </button>
                    </div>

                </form>
            </div>
        </div>
    );
}
