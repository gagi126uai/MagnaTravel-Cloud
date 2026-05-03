import { useState, useEffect } from "react";
import { User, Mail, Phone, XCircle, Search, Loader2, FileText } from "lucide-react";
import { useDebounce } from "../../../hooks/useDebounce";
import { api } from "../../../api";
import { showError, showSuccess, showWarning } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";


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
    const [afipResults, setAfipResults] = useState([]);
    const [loadingAfip, setLoadingAfip] = useState(false);
    const [searchingField, setSearchingField] = useState(null); // 'name' or 'taxId'
    const [similarMatches, setSimilarMatches] = useState([]);

    // Flag to prevent searching right after selecting a result
    const [justSelected, setJustSelected] = useState(false);

    const debouncedTaxId = useDebounce(formData.taxId, 500);
    const debouncedFullName = useDebounce(formData.fullName, 500);
    const debouncedDocumentNumber = useDebounce(formData.documentNumber, 500);

    useEffect(() => {
        if (!isOpen) return;
        if (customer) return; // No sugerir cuando se edita un cliente existente

        const fullName = (debouncedFullName || "").trim();
        const documentNumber = (debouncedDocumentNumber || "").trim();
        if (fullName.length < 3 && documentNumber.length < 3) {
            setSimilarMatches([]);
            return;
        }

        let cancelled = false;
        (async () => {
            try {
                const params = new URLSearchParams();
                if (fullName) params.set("fullName", fullName);
                if (documentNumber) params.set("documentNumber", documentNumber);
                params.set("take", "5");
                const matches = await api.get(`/customers/search-similar?${params.toString()}`);
                if (!cancelled) setSimilarMatches(Array.isArray(matches) ? matches : []);
            } catch {
                if (!cancelled) setSimilarMatches([]);
            }
        })();
        return () => { cancelled = true; };
    }, [debouncedFullName, debouncedDocumentNumber, isOpen, customer]);

    if (!isOpen) return null;

    const handleAfipSearch = async (query, field) => {
        if (!query) return;
        if (query.length < 3) {
            showError("Ingresá al menos 3 caracteres.");
            return;
        }
        setLoadingAfip(true);
        setSearchingField(field);
        try {
            const data = await api.get(`/fiscal/search?q=${encodeURIComponent(query)}`);
            setAfipResults(data);
            if (data.length === 0) {
                showWarning("No se encontraron resultados con ese CUIT/DNI.", "Padrón AFIP");
            }
        } catch (error) {
            console.error(error);
            const errorMsg = error.response?.data?.message || error.response?.data || "Servicio no disponible temporalmente";
            showWarning(typeof errorMsg === 'string' ? errorMsg : "Error al consultar AFIP.", "Servicio AFIP");
        } finally {
            setLoadingAfip(false);
        }
    };
    // Auto-search for Area: Tax ID
    useEffect(() => {
        if (!isOpen) return;
        if (justSelected) {
            setJustSelected(false); // Reset flag
            return;
        }

        // Only auto-search if we are creating a new customer
        if (!customer) {
            if (debouncedTaxId && debouncedTaxId.length >= 3) {
                if (searchingField !== 'name') {
                     handleAfipSearch(debouncedTaxId, 'taxId');
                }
            } else if (!debouncedTaxId || debouncedTaxId.length < 3) {
                if (searchingField === 'taxId') setAfipResults([]);
            }
        }
    }, [debouncedTaxId, isOpen, customer]);

    const handleAfipSelect = (persona) => {
        setFormData(prev => ({
            ...prev,
            fullName: persona.razonSocial || `${persona.apellido || ''} ${persona.nombre || ''}`.trim(),
            taxId: persona.id || prev.taxId,
            taxConditionId: persona.taxConditionId || prev.taxConditionId
        }));
        setAfipResults([]);
        setSearchingField(null);
        setJustSelected(true); // Prevent immediate re-trigger
        showSuccess("Datos de AFIP aplicados.");
    };

    const handleSubmit = (e) => {
        e.preventDefault();
        onSave(formData, getPublicId(customer));
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

                {!customer && similarMatches.length > 0 && (
                    <div className="px-6 pt-4">
                        <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 dark:border-amber-900/40 dark:bg-amber-950/30">
                            <div className="mb-2 flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-amber-800 dark:text-amber-300">
                                <Search className="h-3 w-3" /> Quizas te referis a un cliente que ya existe:
                            </div>
                            <div className="space-y-1">
                                {similarMatches.map((m) => (
                                    <button
                                        key={m.publicId}
                                        type="button"
                                        onClick={() => { onClose(); window.location.href = `/customers/${m.publicId}/account`; }}
                                        className="flex w-full items-center justify-between rounded border border-transparent bg-white/50 px-2 py-1.5 text-left text-xs hover:border-amber-300 hover:bg-white dark:bg-slate-900/40 dark:hover:bg-slate-900"
                                    >
                                        <div>
                                            <div className="font-bold text-slate-900 dark:text-white">{m.fullName}{!m.isActive ? <span className="ml-2 rounded bg-slate-200 px-1.5 py-0.5 text-[10px] font-bold text-slate-600 dark:bg-slate-700 dark:text-slate-300">archivado</span> : null}</div>
                                            <div className="text-[10px] text-slate-500">
                                                {m.documentType ? `${m.documentType} ` : ""}{m.documentNumber || ""} {m.phone ? `• ${m.phone}` : ""} {m.email ? `• ${m.email}` : ""}
                                            </div>
                                        </div>
                                        <span className="rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-bold text-amber-800 dark:bg-amber-900/40 dark:text-amber-300">{m.score}%</span>
                                    </button>
                                ))}
                            </div>
                        </div>
                    </div>
                )}

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
                                        className="w-full rounded-md border border-input bg-background dark:bg-slate-950 pl-9 pr-10 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
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
                                <label className="text-sm font-medium text-slate-700 dark:text-slate-300">CUIT / Documento</label>
                                <div className="relative">
                                    <input
                                        type="text"
                                        placeholder="CUIT, CUIL o DNI"
                                        className="w-full rounded-lg border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-800 py-2 pr-10 px-3 text-sm outline-none focus:ring-2 focus:ring-indigo-500 transition-all font-mono"
                                        value={formData.taxId}
                                        onChange={(e) => {
                                            setFormData({ ...formData, taxId: e.target.value });
                                            if (searchingField === 'taxId') setSearchingField(null);
                                        }}
                                    />
                                    <button
                                        type="button"
                                        onClick={() => handleAfipSearch(formData.taxId, 'taxId')}
                                        className="absolute right-2 top-2 p-1 text-slate-400 hover:text-indigo-600 transition-colors"
                                        title="Buscar en AFIP"
                                    >
                                        {loadingAfip && searchingField === 'taxId' ? <Loader2 className="h-4 w-4 animate-spin text-indigo-500" /> : <Search className="h-4 w-4" />}
                                    </button>

                                    {afipResults.length > 0 && searchingField === 'taxId' && (
                                        <div className="absolute left-0 right-0 z-[100] mt-1 w-full bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-lg shadow-xl overflow-hidden animate-in fade-in slide-in-from-top-2 duration-200">
                                            <div className="px-3 py-2 bg-slate-50 dark:bg-slate-800 border-b border-slate-100 dark:border-slate-700 flex justify-between items-center">
                                                <span className="text-[10px] font-bold text-slate-500 uppercase">Resultados AFIP</span>
                                                <button onClick={() => { setAfipResults([]); setSearchingField(null); }} className="text-slate-400 hover:text-slate-600"><XCircle className="h-3 w-3" /></button>
                                            </div>
                                            <div className="max-h-48 overflow-y-auto">
                                                {afipResults.map((p, idx) => (
                                                    <button
                                                        key={idx}
                                                        type="button"
                                                        onClick={() => handleAfipSelect(p)}
                                                        className="w-full text-left px-4 py-2 hover:bg-blue-50 dark:hover:bg-blue-900/40 border-b last:border-0 border-slate-50 dark:border-slate-800 transition-colors group"
                                                    >
                                                        <div className="font-medium text-sm text-slate-900 dark:text-white group-hover:text-indigo-600 truncate">
                                                            {p.razonSocial || `${p.apellido} ${p.nombre}`}
                                                        </div>
                                                        <div className="text-[10px] text-slate-500">{p.id} • {p.taxCondition}</div>
                                                    </button>
                                                ))}
                                            </div>
                                        </div>
                                    )}
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
