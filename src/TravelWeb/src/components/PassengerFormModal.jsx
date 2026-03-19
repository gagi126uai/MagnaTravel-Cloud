import { useState, useEffect } from "react";
import { X, Save, User, Search, Loader2 } from "lucide-react";
import { useDebounce } from "../hooks/useDebounce";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";

// Clases reutilizables
const inputClass = "w-full rounded-lg border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white focus:ring-blue-500 focus:border-blue-500 transition-colors";
const labelClass = "block text-sm font-medium text-gray-700 dark:text-slate-300 mb-1";

export default function PassengerFormModal({ isOpen, onClose, reservaId, onSuccess, passengerToEdit }) {
    const [formData, setFormData] = useState({
        fullName: "",
        documentType: "DNI",
        documentNumber: "",
        birthDate: "",
        nationality: "",
        phone: "",
        email: "",
        gender: "M",
        notes: ""
    });
    const [afipResults, setAfipResults] = useState([]);
    const [loadingAfip, setLoadingAfip] = useState(false);
    const [searchingField, setSearchingField] = useState(null); // 'name' or 'document'
    const [loading, setLoading] = useState(false);
    
    // Flag to prevent searching right after selecting a result
    const [justSelected, setJustSelected] = useState(false);

    const debouncedName = useDebounce(formData.fullName, 500);
    const debouncedDocument = useDebounce(formData.documentNumber, 500);

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
                showError("No se encontraron resultados en AFIP.");
            }
        } catch (error) {
            console.error(error);
            showError("Error al consultar AFIP.");
        } finally {
            setLoadingAfip(false);
        }
    };

    // Auto-search for Area: Name
    useEffect(() => {
        if (!isOpen) return;
        if (justSelected) {
             setJustSelected(false); // Reset flag
             return;
        }

        if (debouncedName && debouncedName.length >= 3) {
            // Only auto-search if not currently viewing results for document
            if (searchingField !== 'document') {
                 handleAfipSearch(debouncedName, 'name');
            }
        } else if (!debouncedName || debouncedName.length < 3) {
            if (searchingField === 'name') setAfipResults([]);
        }
    }, [debouncedName, isOpen]);

    // Auto-search for Area: Document
    useEffect(() => {
        if (!isOpen) return;
        if (justSelected) {
            setJustSelected(false); // Reset flag
            return;
        }

        if (debouncedDocument && debouncedDocument.length >= 3) {
            if (searchingField !== 'name') {
                 handleAfipSearch(debouncedDocument, 'document');
            }
        } else if (!debouncedDocument || debouncedDocument.length < 3) {
            if (searchingField === 'document') setAfipResults([]);
        }
    }, [debouncedDocument, isOpen]);

    const handleAfipSelect = (persona) => {
        setFormData(prev => ({
            ...prev,
            fullName: persona.razonSocial || `${persona.apellido || ''} ${persona.nombre || ''}`.trim(),
            documentNumber: persona.id || prev.documentNumber
        }));
        setAfipResults([]);
        setSearchingField(null);
        setJustSelected(true); // Prevent immediate re-trigger
        showSuccess("Datos de AFIP aplicados.");
    };

    useEffect(() => {
        if (isOpen) {
            if (passengerToEdit) {
                setFormData({
                    ...passengerToEdit,
                    birthDate: passengerToEdit.birthDate ? passengerToEdit.birthDate.split('T')[0] : ""
                });
            } else {
                setFormData({
                    fullName: "", documentType: "DNI", documentNumber: "",
                    birthDate: "", nationality: "", phone: "", email: "", gender: "M", notes: ""
                });
            }
        }
    }, [isOpen, passengerToEdit]);

    const handleSubmit = async (e) => {
        e.preventDefault();
        setLoading(true);

        const payload = { ...formData };
        if (!payload.birthDate) payload.birthDate = null;
        if (payload.nationality === "") payload.nationality = null;
        if (payload.phone === "") payload.phone = null;
        if (payload.email === "") payload.email = null;

        try {
            if (passengerToEdit) {
                await api.put(`/reservas/passengers/${passengerToEdit.id}`, payload);
                showSuccess("Pasajero actualizado");
            } else {
                await api.post(`/reservas/${reservaId}/passengers`, payload);
                showSuccess("Pasajero agregado");
            }
            onSuccess();
            onClose();
        } catch (error) {
            console.error(error);
            showError("Error al guardar pasajero: " + (error.response?.data || error.message));
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4">
            <div className="bg-white dark:bg-slate-800 rounded-xl shadow-2xl w-full max-w-2xl overflow-hidden animate-in fade-in zoom-in-95 duration-200">

                {/* Header */}
                <div className="bg-gray-50 dark:bg-slate-900 border-b border-gray-100 dark:border-slate-700 px-6 py-4 flex justify-between items-center">
                    <h3 className="text-lg font-semibold text-gray-800 dark:text-white flex items-center gap-2">
                        <User className="w-5 h-5 text-blue-600 dark:text-blue-400" />
                        {passengerToEdit ? "Editar Pasajero" : "Nuevo Pasajero"}
                    </h3>
                    <button onClick={onClose} className="text-gray-400 hover:text-gray-600 dark:text-slate-400 dark:hover:text-slate-200 transition-colors">
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {/* Body */}
                <div className="p-6">
                    <form onSubmit={handleSubmit} className="grid grid-cols-1 md:grid-cols-2 gap-4">

                        {/* Name - Full Width */}
                        <div className="md:col-span-2 relative">
                            <label className={labelClass}>Nombre Completo *</label>
                            <div className="relative">
                                <input
                                    required
                                    type="text"
                                    className={`${inputClass} pr-10`}
                                    placeholder="Nombre del pasajero"
                                    value={formData.fullName || ""}
                                    onChange={(e) => {
                                        setFormData({ ...formData, fullName: e.target.value });
                                        if (searchingField === 'name') setSearchingField(null);
                                    }}
                                />
                                <button
                                    type="button"
                                    onClick={() => handleAfipSearch(formData.fullName, 'name')}
                                    className="absolute right-2 top-1.5 p-1 text-gray-400 hover:text-blue-600 transition-colors"
                                    title="Buscar en AFIP"
                                >
                                    {loadingAfip && searchingField === 'name' ? <Loader2 className="h-4 w-4 animate-spin text-blue-500" /> : <Search className="h-4 w-4" />}
                                </button>

                                {afipResults.length > 0 && searchingField === 'name' && (
                                    <div className="absolute left-0 right-0 z-[100] mt-1 w-full bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-lg shadow-xl overflow-hidden animate-in fade-in slide-in-from-top-2 duration-200">
                                        <div className="px-3 py-1.5 bg-gray-50 dark:bg-slate-800 border-b border-gray-100 dark:border-slate-700 flex justify-between items-center">
                                            <span className="text-[10px] font-bold text-gray-500 uppercase">Sugerencias AFIP</span>
                                            <button onClick={() => { setAfipResults([]); setSearchingField(null); }} className="text-gray-400 hover:text-gray-600"><X className="h-3 w-3" /></button>
                                        </div>
                                        <div className="max-h-40 overflow-y-auto">
                                            {afipResults.map((p, idx) => (
                                                <button
                                                    key={idx}
                                                    type="button"
                                                    onClick={() => handleAfipSelect(p)}
                                                    className="w-full text-left px-4 py-2 hover:bg-blue-50 dark:hover:bg-blue-900/40 border-b last:border-0 border-gray-50 dark:border-slate-800 transition-colors group"
                                                >
                                                    <div className="font-medium text-sm text-gray-900 dark:text-white group-hover:text-blue-600 truncate">
                                                        {p.razonSocial || `${p.apellido} ${p.nombre}`}
                                                    </div>
                                                    <div className="text-[10px] text-gray-500">{p.id} • {p.taxCondition}</div>
                                                </button>
                                            ))}
                                        </div>
                                    </div>
                                )}
                            </div>
                        </div>

                        <div>
                            <label className={labelClass}>Tipo Documento</label>
                            <select
                                className={inputClass}
                                value={formData.documentType}
                                onChange={(e) => setFormData({ ...formData, documentType: e.target.value })}
                            >
                                <option value="DNI">DNI</option>
                                <option value="Pasaporte">Pasaporte</option>
                                <option value="Cedula">Cédula</option>
                                <option value="Otro">Otro</option>
                            </select>
                        </div>
                        <div>
                            <label className={labelClass}>Número de Documento</label>
                            <div className="relative">
                                <Search className="absolute left-3 top-3 h-4 w-4 text-gray-400" />
                                <input
                                    type="text"
                                    className={`${inputClass} pl-10 pr-10`}
                                    placeholder="DNI o CUIT"
                                    value={formData.documentNumber || ""}
                                    onChange={(e) => {
                                        setFormData({ ...formData, documentNumber: e.target.value });
                                        if (searchingField === 'document') setSearchingField(null);
                                    }}
                                    required
                                />
                                <button
                                    type="button"
                                    onClick={() => handleAfipSearch(formData.documentNumber, 'document')}
                                    className="absolute right-2 top-2 p-1 text-gray-400 hover:text-blue-600 transition-colors"
                                    title="Buscar en AFIP"
                                >
                                    {loadingAfip && searchingField === 'document' ? <Loader2 className="h-4 w-4 animate-spin text-blue-500" /> : <Search className="h-4 w-4" />}
                                </button>

                                {afipResults.length > 0 && searchingField === 'document' && (
                                    <div className="absolute left-0 right-0 z-[100] mt-1 w-full bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-lg shadow-xl overflow-hidden animate-in fade-in slide-in-from-top-2 duration-200">
                                        <div className="px-3 py-1.5 bg-gray-50 dark:bg-slate-800 border-b border-gray-100 dark:border-slate-700 flex justify-between items-center">
                                            <span className="text-[10px] font-bold text-gray-500 uppercase">Sugerencias AFIP</span>
                                            <button onClick={() => { setAfipResults([]); setSearchingField(null); }} className="text-gray-400 hover:text-gray-600"><X className="h-3 w-3" /></button>
                                        </div>
                                        <div className="max-h-40 overflow-y-auto">
                                            {afipResults.map((p, idx) => (
                                                <button
                                                    key={idx}
                                                    type="button"
                                                    onClick={() => handleAfipSelect(p)}
                                                    className="w-full text-left px-4 py-2 hover:bg-blue-50 dark:hover:bg-blue-900/40 border-b last:border-0 border-gray-50 dark:border-slate-800 transition-colors group"
                                                >
                                                    <div className="font-medium text-sm text-gray-900 dark:text-white group-hover:text-blue-600 truncate">
                                                        {p.razonSocial || `${p.apellido} ${p.nombre}`}
                                                    </div>
                                                    <div className="text-[10px] text-gray-500">{p.id} • {p.taxCondition}</div>
                                                </button>
                                            ))}
                                        </div>
                                    </div>
                                )}
                            </div>
                        </div>

                        {/* Personal Info */}
                        <div>
                            <label className={labelClass}>Fecha Nacimiento</label>
                            <input
                                type="date"
                                className={inputClass}
                                value={formData.birthDate}
                                onChange={(e) => setFormData({ ...formData, birthDate: e.target.value })}
                            />
                        </div>
                        <div>
                            <label className={labelClass}>Nacionalidad</label>
                            <input
                                type="text"
                                className={inputClass}
                                placeholder="Ej: Argentina"
                                value={formData.nationality || ""}
                                onChange={(e) => setFormData({ ...formData, nationality: e.target.value })}
                            />
                        </div>

                        <div>
                            <label className={labelClass}>Género</label>
                            <select
                                className={inputClass}
                                value={formData.gender || "M"}
                                onChange={(e) => setFormData({ ...formData, gender: e.target.value })}
                            >
                                <option value="M">Masculino</option>
                                <option value="F">Femenino</option>
                                <option value="X">Otro</option>
                            </select>
                        </div>

                        {/* Contact */}
                        <div>
                            <label className={labelClass}>Teléfono</label>
                            <input
                                type="tel"
                                className={inputClass}
                                placeholder="+54 9 11..."
                                value={formData.phone || ""}
                                onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                            />
                        </div>

                        <div className="md:col-span-2">
                            <label className={labelClass}>Email</label>
                            <input
                                type="email"
                                className={inputClass}
                                placeholder="correo@ejemplo.com"
                                value={formData.email || ""}
                                onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                            />
                        </div>

                        {/* Notes */}
                        <div className="md:col-span-2">
                            <label className={labelClass}>Notas Adicionales</label>
                            <textarea
                                rows={2}
                                className={inputClass}
                                placeholder="Preferencias alimenticias, asistencia especial..."
                                value={formData.notes || ""}
                                onChange={(e) => setFormData({ ...formData, notes: e.target.value })}
                            />
                        </div>

                        {/* Footer Actions */}
                        <div className="md:col-span-2 flex justify-end gap-3 mt-4 pt-4 border-t border-gray-100 dark:border-slate-700">
                            <button
                                type="button"
                                onClick={onClose}
                                className="px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-200 bg-white dark:bg-slate-700 border border-gray-300 dark:border-slate-600 rounded-lg hover:bg-gray-50 dark:hover:bg-slate-600 transition-colors"
                            >
                                Cancelar
                            </button>
                            <button
                                type="submit"
                                disabled={loading}
                                className="px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700 focus:ring-4 focus:ring-blue-300 transition-colors flex items-center gap-2"
                            >
                                <Save className="w-4 h-4" />
                                {loading ? "Guardando..." : "Guardar Pasajero"}
                            </button>
                        </div>

                    </form>
                </div>
            </div>
        </div>
    );
}
