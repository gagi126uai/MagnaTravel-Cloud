import { useState } from "react";
import { User, Mail, Phone, XCircle, Search, Loader2 } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import AfipSearchModal from "../../../components/AfipSearchModal";

export function CustomerFormModal({ isOpen, onClose, customer, onSave }) {
    const [formData, setFormData] = useState({
        fullName: customer?.fullName || "",
        taxId: customer?.taxId || "",
        email: customer?.email || "",
        phone: customer?.phone || "",
        documentNumber: customer?.documentNumber || "",
        address: customer?.address || "",
        notes: customer?.notes || "",
        creditLimit: customer?.creditLimit || 0,
        isActive: customer?.isActive ?? true,
        taxConditionId: customer?.taxConditionId || 5, // Default Conf. Final
    });
    const [isAfipModalOpen, setIsAfipModalOpen] = useState(false);

    if (!isOpen) return null;

    const handleAfipSelect = (persona) => {
        setFormData(prev => ({
            ...prev,
            fullName: persona.razonSocial || `${persona.apellido || ''} ${persona.nombre || ''}`.trim(),
            taxConditionId: persona.taxConditionId || prev.taxConditionId,
            taxId: persona.id || prev.taxId
        }));
        setIsAfipModalOpen(false);
        showSuccess("Datos de AFIP aplicados.");
    };

    const handleSubmit = (e) => {
        e.preventDefault();
        onSave(formData, customer?.id);
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4 animate-in fade-in duration-200">
            <div className="w-full max-w-lg rounded-xl border bg-card p-0 shadow-2xl max-h-[90vh] overflow-y-auto scale-100 animate-in zoom-in-95 duration-200">
                {/* Modal Header */}
                <div className="px-6 py-4 border-b bg-slate-50/50 dark:bg-slate-900/50 flex items-center justify-between">
                    <div>
                        <h3 className="text-lg font-bold text-slate-900 dark:text-white">
                            {customer ? "Editar Cliente" : "Nuevo Cliente"}
                        </h3>
                        <p className="text-sm text-muted-foreground">
                            {customer ? "Modificar datos del cliente" : "Registrar un nuevo cliente en el sistema"}
                        </p>
                    </div>
                    <button
                        onClick={onClose}
                        className="text-slate-400 hover:text-slate-500 transition-colors"
                    >
                        <XCircle className="h-5 w-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit}>
                    <div className="p-6 space-y-4">
                        <div className="grid gap-4 sm:grid-cols-2">
                            <div className="col-span-2 space-y-1.5">
                                <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Nombre Completo <span className="text-red-500">*</span></label>
                                <div className="relative">
                                    <User className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                    <input
                                        type="text"
                                        required
                                        value={formData.fullName}
                                        onChange={(e) => setFormData({ ...formData, fullName: e.target.value })}
                                        className="w-full rounded-md border border-input bg-background dark:bg-slate-950 pl-9 pr-3 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                                        placeholder="Ej. Juan Pérez"
                                    />
                                </div>
                            </div>

                            <div className="space-y-1.5">
                                <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Documento / Pasaporte</label>
                                <div className="flex gap-1">
                                    <input
                                        type="text"
                                        value={formData.documentNumber}
                                        onChange={(e) => setFormData({ ...formData, documentNumber: e.target.value })}
                                        className="w-full rounded-md border border-input bg-background dark:bg-slate-950 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500"
                                    />
                                </div>
                            </div>
                            <div className="space-y-1.5">
                                <div className="flex items-center justify-between mb-1.5">
                                    <label className="text-sm font-medium text-slate-700 dark:text-slate-300">CUIT / Documento</label>
                                    <button
                                        type="button"
                                        onClick={() => setIsAfipModalOpen(true)}
                                        className="text-[10px] font-bold uppercase tracking-wider text-indigo-600 hover:text-indigo-700 flex items-center gap-1 bg-indigo-50 dark:bg-indigo-900/30 px-2 py-0.5 rounded transition-all"
                                    >
                                        <Search className="h-3 w-3" />
                                        Consultar AFIP
                                    </button>
                                </div>
                                <div className="relative">
                                    <FileText className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                    <input
                                        type="text"
                                        placeholder="CUIT, CUIL o DNI"
                                        className="w-full rounded-lg border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-800 py-2 pl-10 pr-4 text-sm outline-none focus:ring-2 focus:ring-indigo-500 transition-all font-mono"
                                        value={formData.taxId}
                                        onChange={(e) => setFormData({ ...formData, taxId: e.target.value })}
                                    />
                                </div>
                            </div>

                            <div className="space-y-1.5">
                                <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Condición AFIP <span className="text-red-500">*</span></label>
                                <select
                                    value={formData.taxConditionId}
                                    onChange={(e) => setFormData({ ...formData, taxConditionId: parseInt(e.target.value) })}
                                    className="w-full rounded-md border border-input bg-background dark:bg-slate-950 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500"
                                >
                                    <option value={1}>Responsable Inscripto</option>
                                    <option value={6}>Monotributo</option>
                                    <option value={4}>Exento</option>
                                    <option value={5}>Consumidor Final</option>
                                </select>
                            </div>

                            <div className="col-span-2 grid sm:grid-cols-2 gap-4">
                                <div className="space-y-1.5">
                                    <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
                                    <div className="relative">
                                        <Mail className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                        <input
                                            type="email"
                                            value={formData.email}
                                            onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                                            className="w-full rounded-md border border-input bg-background dark:bg-slate-950 pl-9 pr-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500"
                                        />
                                    </div>
                                </div>
                                <div className="space-y-1.5">
                                    <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Teléfono</label>
                                    <div className="relative">
                                        <Phone className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                        <input
                                            type="text"
                                            value={formData.phone}
                                            onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                                            className="w-full rounded-md border border-input bg-background dark:bg-slate-950 pl-9 pr-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500"
                                        />
                                    </div>
                                </div>
                            </div>

                            <div className="col-span-2 space-y-1.5">
                                <label className="text-sm font-medium text-slate-700 dark:text-slate-300">Dirección</label>
                                <input
                                    type="text"
                                    value={formData.address}
                                    onChange={(e) => setFormData({ ...formData, address: e.target.value })}
                                    className="w-full rounded-md border border-input bg-background dark:bg-slate-950 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500"
                                />
                            </div>
                        </div>
                    </div>

                    <div className="flex gap-3 px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50">
                        <button
                            type="button"
                            onClick={onClose}
                            className="flex-1 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            className="flex-1 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 shadow-sm transition-colors"
                        >
                            {customer ? "Guardar Cambios" : "Crear Cliente"}
                        </button>
                    </div>
                </form>
            </div>

            <AfipSearchModal 
                isOpen={isAfipModalOpen}
                onClose={() => setIsAfipModalOpen(false)}
                onSelect={handleAfipSelect}
                initialQuery={formData.taxId || formData.fullName}
            />
        </div>
    );
}
