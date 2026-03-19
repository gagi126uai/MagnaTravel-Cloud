import { useState } from "react";
import { User, Mail, Phone, XCircle, Search, Loader2 } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";

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
    const [loadingFiscal, setLoadingFiscal] = useState(false);

    if (!isOpen) return null;

    const handleFiscalLookup = async (field) => {
        const idToSearch = field === 'taxId' ? formData.taxId : formData.documentNumber;
        if (!idToSearch || idToSearch.length < 7) {
            showError("Ingrese un número válido para consultar");
            return;
        }

        setLoadingFiscal(true);
        try {
            const result = await api.get(`/fiscal/persona/${idToSearch}`);
            
            let fullResultName = "";
            if (result.razonSocial) {
                fullResultName = result.razonSocial;
            } else if (result.nombre || result.apellido) {
                fullResultName = `${result.apellido || ''} ${result.nombre || ''}`.trim();
            }

            if (fullResultName) {
                // Map tax condition
                let tcId = 5; // Default
                if (result.taxCondition === "Monotributo") tcId = 6;
                else if (result.taxCondition === "Responsable Inscripto") tcId = 1;
                else if (result.taxCondition === "Exento") tcId = 4;

                setFormData(prev => ({
                    ...prev,
                    fullName: fullResultName,
                    taxConditionId: tcId,
                    taxId: result.id || prev.taxId
                }));
                showSuccess("Datos de AFIP obtenidos (CUIT: " + result.id + ")");
            }
        } catch (error) {
            console.error(error);
            showError(error.response?.data || "No se pudo obtener información fiscal");
        } finally {
            setLoadingFiscal(false);
        }
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
                                    <button
                                        type="button"
                                        onClick={() => handleFiscalLookup('documentNumber')}
                                        disabled={loadingFiscal || !formData.documentNumber}
                                        className="px-2 bg-slate-100 dark:bg-slate-800 border rounded hover:bg-slate-200 transition-colors"
                                    >
                                        {loadingFiscal ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
                                    </button>
                                </div>
                            </div>
                            <div className="space-y-1.5">
                                <label className="text-sm font-medium text-slate-700 dark:text-slate-300">CUIT / ID Fiscal</label>
                                <div className="flex gap-1">
                                    <input
                                        type="text"
                                        value={formData.taxId}
                                        placeholder="Ej. 20-12345678-9"
                                        onChange={(e) => setFormData({ ...formData, taxId: e.target.value })}
                                        className="w-full rounded-md border border-input bg-background dark:bg-slate-950 px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500"
                                    />
                                    <button
                                        type="button"
                                        onClick={() => handleFiscalLookup('taxId')}
                                        disabled={loadingFiscal || !formData.taxId}
                                        className="px-2 bg-slate-100 dark:bg-slate-800 border rounded hover:bg-slate-200 transition-colors"
                                    >
                                        {loadingFiscal ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Search className="w-3.5 h-3.5" />}
                                    </button>
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
        </div>
    );
}
