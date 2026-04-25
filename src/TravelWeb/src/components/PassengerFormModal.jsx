import { useEffect, useState } from "react";
import { BadgeCheck, CalendarDays, FileText, Globe2, Loader2, Mail, Phone, Save, Search, StickyNote, User, X } from "lucide-react";
import { useDebounce } from "../hooks/useDebounce";
import { api } from "../api";
import { showError, showSuccess, showWarning } from "../alerts";
import { getApiErrorMessage } from "../lib/errors";
import { getPublicId } from "../lib/publicIds";

const inputClass = "w-full rounded-xl border border-slate-200 bg-slate-50 p-2.5 text-sm text-slate-900 transition-colors focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-600 dark:bg-slate-700 dark:text-white dark:focus:border-indigo-400";
const labelClass = "mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300";
const panelClass = "rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-700 dark:bg-slate-900/50";

const emptyPassengerForm = {
    fullName: "",
    documentType: "DNI",
    documentNumber: "",
    birthDate: "",
    nationality: "",
    phone: "",
    email: "",
    gender: "M",
    notes: "",
};

function SectionTitle({ icon: Icon, children }) {
    return (
        <h4 className="mb-3 flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
            <Icon className="h-4 w-4 text-indigo-500" />
            {children}
        </h4>
    );
}

export default function PassengerFormModal({ isOpen, onClose, reservaId, onSuccess, passengerToEdit }) {
    const [formData, setFormData] = useState(emptyPassengerForm);
    const [afipResults, setAfipResults] = useState([]);
    const [loadingAfip, setLoadingAfip] = useState(false);
    const [searchingField, setSearchingField] = useState(null);
    const [loading, setLoading] = useState(false);
    const [justSelected, setJustSelected] = useState(false);

    const debouncedDocument = useDebounce(formData.documentNumber, 500);

    const updateField = (field, value) => {
        setFormData((current) => ({ ...current, [field]: value }));
    };

    const handleAfipSearch = async (query, field) => {
        if (!query) return;
        if (query.length < 3) {
            showWarning("Ingresa al menos 3 caracteres.", "Padron AFIP");
            return;
        }

        setLoadingAfip(true);
        setSearchingField(field);
        try {
            const genderParam = formData.gender ? `&gender=${formData.gender}` : "";
            const data = await api.get(`/fiscal/search?q=${encodeURIComponent(query)}${genderParam}`);
            setAfipResults(data || []);
            if (!data || data.length === 0) {
                showWarning("No se encontraron resultados con ese DNI.", "Padron AFIP");
            }
        } catch (error) {
            console.error(error);
            showWarning(getApiErrorMessage(error, "Servicio no disponible temporalmente"), "Servicio AFIP");
        } finally {
            setLoadingAfip(false);
        }
    };

    useEffect(() => {
        if (!isOpen) return;
        if (justSelected) {
            setJustSelected(false);
            return;
        }

        if (!passengerToEdit) {
            if (debouncedDocument && debouncedDocument.length >= 3) {
                if (searchingField !== "name") {
                    handleAfipSearch(debouncedDocument, "document");
                }
            } else if (!debouncedDocument || debouncedDocument.length < 3) {
                if (searchingField === "document") setAfipResults([]);
            }
        }
    }, [debouncedDocument, isOpen, passengerToEdit]);

    const handleAfipSelect = (persona) => {
        setFormData((current) => ({
            ...current,
            fullName: persona.razonSocial || `${persona.apellido || ""} ${persona.nombre || ""}`.trim(),
            documentNumber: persona.id || current.documentNumber,
        }));
        setAfipResults([]);
        setSearchingField(null);
        setJustSelected(true);
        showSuccess("Datos de AFIP aplicados.");
    };

    useEffect(() => {
        if (!isOpen) return;

        if (passengerToEdit) {
            setFormData({
                ...emptyPassengerForm,
                ...passengerToEdit,
                birthDate: passengerToEdit.birthDate ? passengerToEdit.birthDate.split("T")[0] : "",
            });
        } else {
            setFormData(emptyPassengerForm);
        }
        setAfipResults([]);
        setSearchingField(null);
    }, [isOpen, passengerToEdit]);

    const handleSubmit = async (event) => {
        event.preventDefault();
        setLoading(true);

        const payload = { ...formData };
        if (!payload.birthDate) payload.birthDate = null;
        if (payload.nationality === "") payload.nationality = null;
        if (payload.phone === "") payload.phone = null;
        if (payload.email === "") payload.email = null;
        if (payload.notes === "") payload.notes = null;

        try {
            const savedPassenger = passengerToEdit
                ? await api.put(`/reservas/passengers/${getPublicId(passengerToEdit)}`, payload)
                : await api.post(`/reservas/${reservaId}/passengers`, payload);

            showSuccess(passengerToEdit ? "Pasajero actualizado" : "Pasajero agregado");
            await onSuccess?.({ passenger: savedPassenger });
            onClose();
        } catch (error) {
            console.error(error);
            showError(`Error al guardar pasajero: ${getApiErrorMessage(error, "Error desconocido")}`);
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm">
            <div className="w-full max-w-3xl overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900">
                <div className="flex items-center justify-between border-b border-slate-200 bg-gradient-to-r from-indigo-500 to-sky-600 p-4 text-white dark:border-slate-700">
                    <h2 className="flex items-center gap-2 text-lg font-semibold">
                        <User className="h-5 w-5" />
                        {passengerToEdit ? "Editar Pasajero" : "Nuevo Pasajero"}
                    </h2>
                    <button type="button" onClick={onClose} className="rounded-lg p-2 text-white/80 transition-colors hover:bg-white/20 hover:text-white">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="max-h-[78vh] space-y-4 overflow-y-auto p-4">
                    <section className={panelClass}>
                        <SectionTitle icon={BadgeCheck}>Identidad</SectionTitle>
                        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                            <div className="md:col-span-2">
                                <label className={labelClass}>Nombre completo *</label>
                                <div className="relative">
                                    <User className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                                    <input
                                        required
                                        type="text"
                                        className={`${inputClass} pl-10`}
                                        placeholder="Nombre del pasajero"
                                        value={formData.fullName || ""}
                                        onChange={(event) => updateField("fullName", event.target.value)}
                                    />
                                </div>
                            </div>

                            <div>
                                <label className={labelClass}>Tipo documento</label>
                                <select className={inputClass} value={formData.documentType || "DNI"} onChange={(event) => updateField("documentType", event.target.value)}>
                                    <option value="DNI">DNI</option>
                                    <option value="Pasaporte">Pasaporte</option>
                                    <option value="Cedula">Cedula</option>
                                    <option value="Otro">Otro</option>
                                </select>
                            </div>

                            <div>
                                <label className={labelClass}>Numero de documento *</label>
                                <div className="relative">
                                    <FileText className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                                    <input
                                        required
                                        type="text"
                                        className={`${inputClass} pl-10 pr-10`}
                                        placeholder="DNI o CUIT"
                                        value={formData.documentNumber || ""}
                                        onChange={(event) => {
                                            updateField("documentNumber", event.target.value);
                                            if (searchingField === "document") setSearchingField(null);
                                        }}
                                    />
                                    <button
                                        type="button"
                                        onClick={() => handleAfipSearch(formData.documentNumber, "document")}
                                        className="absolute right-2 top-2 rounded-lg p-1 text-slate-400 transition-colors hover:bg-indigo-50 hover:text-indigo-600 dark:hover:bg-indigo-900/40"
                                        title="Buscar en AFIP"
                                    >
                                        {loadingAfip && searchingField === "document" ? <Loader2 className="h-4 w-4 animate-spin text-indigo-500" /> : <Search className="h-4 w-4" />}
                                    </button>

                                    {afipResults.length > 0 && searchingField === "document" ? (
                                        <div className="absolute left-0 right-0 z-[100] mt-1 overflow-hidden rounded-xl border border-slate-200 bg-white shadow-xl dark:border-slate-700 dark:bg-slate-800">
                                            <div className="flex items-center justify-between border-b border-slate-100 bg-slate-50 px-3 py-2 dark:border-slate-700 dark:bg-slate-900/50">
                                                <span className="text-[10px] font-bold uppercase tracking-wider text-slate-500">Sugerencias AFIP</span>
                                                <button type="button" onClick={() => { setAfipResults([]); setSearchingField(null); }} className="text-slate-400 hover:text-slate-600">
                                                    <X className="h-3.5 w-3.5" />
                                                </button>
                                            </div>
                                            <div className="max-h-44 overflow-y-auto">
                                                {afipResults.map((persona, index) => (
                                                    <button
                                                        key={`${persona.id || "afip"}-${index}`}
                                                        type="button"
                                                        onClick={() => handleAfipSelect(persona)}
                                                        className="group w-full border-b border-slate-50 px-4 py-2 text-left transition-colors last:border-0 hover:bg-indigo-50 dark:border-slate-700 dark:hover:bg-indigo-900/30"
                                                    >
                                                        <div className="truncate text-sm font-semibold text-slate-900 group-hover:text-indigo-600 dark:text-white">
                                                            {persona.razonSocial || `${persona.apellido || ""} ${persona.nombre || ""}`.trim()}
                                                        </div>
                                                        <div className="text-[10px] text-slate-500">{persona.id} - {persona.taxCondition}</div>
                                                    </button>
                                                ))}
                                            </div>
                                        </div>
                                    ) : null}
                                </div>
                            </div>
                        </div>
                    </section>

                    <section className={panelClass}>
                        <SectionTitle icon={CalendarDays}>Datos personales</SectionTitle>
                        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
                            <div>
                                <label className={labelClass}>Fecha nacimiento</label>
                                <input type="date" className={inputClass} value={formData.birthDate || ""} onChange={(event) => updateField("birthDate", event.target.value)} />
                            </div>
                            <div>
                                <label className={labelClass}>Nacionalidad</label>
                                <div className="relative">
                                    <Globe2 className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                                    <input className={`${inputClass} pl-10`} placeholder="Ej: Argentina" value={formData.nationality || ""} onChange={(event) => updateField("nationality", event.target.value)} />
                                </div>
                            </div>
                            <div>
                                <label className={labelClass}>Genero</label>
                                <select className={inputClass} value={formData.gender || "M"} onChange={(event) => updateField("gender", event.target.value)}>
                                    <option value="M">Masculino</option>
                                    <option value="F">Femenino</option>
                                    <option value="X">Otro</option>
                                </select>
                            </div>
                        </div>
                    </section>

                    <section className={panelClass}>
                        <SectionTitle icon={Phone}>Contacto</SectionTitle>
                        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                            <div>
                                <label className={labelClass}>Telefono</label>
                                <div className="relative">
                                    <Phone className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                                    <input type="tel" className={`${inputClass} pl-10`} placeholder="+54 9 11..." value={formData.phone || ""} onChange={(event) => updateField("phone", event.target.value)} />
                                </div>
                            </div>
                            <div>
                                <label className={labelClass}>Email</label>
                                <div className="relative">
                                    <Mail className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                                    <input type="email" className={`${inputClass} pl-10`} placeholder="correo@ejemplo.com" value={formData.email || ""} onChange={(event) => updateField("email", event.target.value)} />
                                </div>
                            </div>
                        </div>
                    </section>

                    <section className={panelClass}>
                        <SectionTitle icon={StickyNote}>Notas</SectionTitle>
                        <textarea
                            rows={3}
                            className={inputClass}
                            placeholder="Preferencias alimenticias, asistencia especial..."
                            value={formData.notes || ""}
                            onChange={(event) => updateField("notes", event.target.value)}
                        />
                    </section>

                    <div className="flex justify-end gap-3 border-t border-slate-200 pt-4 dark:border-slate-700">
                        <button type="button" onClick={onClose} className="rounded-xl px-5 py-2.5 text-sm text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">
                            Cancelar
                        </button>
                        <button type="submit" disabled={loading} className="flex items-center gap-2 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm text-white shadow-lg shadow-indigo-200 transition-colors hover:bg-indigo-700 disabled:opacity-50 dark:shadow-none">
                            {loading ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
                            {loading ? "Guardando..." : "Guardar Pasajero"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
