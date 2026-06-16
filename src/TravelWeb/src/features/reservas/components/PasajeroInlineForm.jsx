/**
 * Mini-formulario en línea para cargar o completar un pasajero de la reserva.
 *
 * Se usa como "red de seguridad" al intentar resolver/emitir un servicio
 * cuando faltan datos de pasajeros: el formulario aparece debajo del servicio
 * (NUNCA en una ventana flotante, conforme a la guía UX 2026-06-15).
 *
 * También se usa en PassengerList para cargar pasajeros vacíos uno por uno.
 *
 * Qué pide según el tipo de servicio:
 *   - Aéreo: nombre + tipo + número de documento (todos obligatorios).
 *   - Hotel / Traslado: solo nombre (documento opcional en este paso).
 *   - Asistencia: nombre + documento + fecha de nacimiento.
 *   - Paquete / Genérico: solo nombre.
 *   - Sin contexto de servicio (ej: desde PassengerList): nombre + documento.
 *
 * Props:
 *   reservaId          — publicId de la reserva (para POST /passengers o PUT /passengers/:id)
 *   passengerToEdit    — objeto pasajero existente (para editar); null → crear nuevo
 *   slotLabel          — etiqueta del slot: "Adulto 1", "Menor 2", "Titular", etc.
 *   mode               — "flight" | "hotel" | "transfer" | "assistance" | "package" | "generic" | "full"
 *                        "full" = pide todos los campos (igual que PassengerFormModal pero en línea)
 *   onGuardado         — callback(pasajeroGuardado) — se llama tras guardar exitosamente
 *   onCancelar         — callback() — se llama cuando el usuario cancela
 */

import { useState } from "react";
import { Loader2, Save, X, User } from "lucide-react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";

// Tipos de documento aceptados por el backend (mismo listado que el modal completo).
const DOC_TYPES = [
    { value: "DNI", label: "DNI" },
    { value: "Pasaporte", label: "Pasaporte" },
    { value: "Cedula", label: "Cédula" },
    { value: "Otro", label: "Otro" },
];

// Determina qué campos hay que mostrar según el tipo de servicio.
// Estas reglas replican las del backend para habilitar/deshabilitar el botón de resolución.
function camposRequeridosPorMode(mode) {
    switch (mode) {
        case "flight":
            return { nombre: true, documento: true, fecha: false };
        case "hotel":
        case "transfer":
            // Solo nombre del titular. Documento es opcional en este paso.
            return { nombre: true, documento: false, fecha: false };
        case "assistance":
            return { nombre: true, documento: true, fecha: true };
        case "package":
        case "generic":
            return { nombre: true, documento: false, fecha: false };
        case "full":
        default:
            return { nombre: true, documento: true, fecha: false };
    }
}

// Valida si el formulario está listo para guardar según el modo.
function esFormularioValido(form, mode) {
    const campos = camposRequeridosPorMode(mode);

    if (campos.nombre && form.fullName.trim().length < 2) return false;
    if (campos.documento && !form.documentNumber.trim()) return false;
    if (campos.fecha && !form.birthDate) return false;

    return true;
}

export function PasajeroInlineForm({ reservaId, passengerToEdit, slotLabel, mode = "full", onGuardado, onCancelar }) {
    // Inicializamos el form con los datos del pasajero existente si estamos editando,
    // o con un form vacío si estamos creando uno nuevo.
    const [form, setForm] = useState(() => ({
        fullName: passengerToEdit?.fullName || "",
        documentType: passengerToEdit?.documentType || "DNI",
        documentNumber: passengerToEdit?.documentNumber || "",
        birthDate: passengerToEdit?.birthDate
            ? passengerToEdit.birthDate.split("T")[0]
            : "",
    }));
    const [guardando, setGuardando] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    const campos = camposRequeridosPorMode(mode);
    const formularioListo = esFormularioValido(form, mode);
    const passengerPublicId = passengerToEdit ? getPublicId(passengerToEdit) : null;

    const updateField = (field, value) => {
        setForm(prev => ({ ...prev, [field]: value }));
        // Si el usuario corrige algo, limpiamos el error anterior para no confundir.
        if (errorGuardar) setErrorGuardar(null);
    };

    const handleGuardar = async () => {
        if (!formularioListo || guardando) return;

        setGuardando(true);
        setErrorGuardar(null);

        // Armamos el payload con los campos del formulario.
        // Los opcionales van como null si están vacíos (el backend los acepta).
        const payload = {
            fullName: form.fullName.trim(),
            documentType: form.documentType,
            documentNumber: form.documentNumber.trim() || null,
            birthDate: form.birthDate || null,
            // Campos que no pedimos en el inline: el backend los acepta como null.
            nationality: passengerToEdit?.nationality || null,
            phone: passengerToEdit?.phone || null,
            email: passengerToEdit?.email || null,
            gender: passengerToEdit?.gender || null,
            notes: passengerToEdit?.notes || null,
        };

        try {
            let pasajeroGuardado;
            if (passengerPublicId) {
                // Editar pasajero existente: PUT /reservas/passengers/:id
                pasajeroGuardado = await api.put(`/reservas/passengers/${passengerPublicId}`, payload);
            } else {
                // Crear pasajero nuevo: POST /reservas/:reservaId/passengers
                pasajeroGuardado = await api.post(`/reservas/${reservaId}/passengers`, payload);
            }
            onGuardado?.(pasajeroGuardado);
        } catch (error) {
            // Si falla, mostramos el error en línea (no con toast) para que el usuario
            // no pierda lo que escribió. Regla UX: no perder datos en error recuperable.
            const mensajeError = getApiErrorMessage(error, "No se pudo guardar el pasajero. Intentá de nuevo.");
            setErrorGuardar(mensajeError);
            showError(mensajeError);
        } finally {
            setGuardando(false);
        }
    };

    const inputClass = "rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white";

    return (
        <div
            className="rounded-xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-800/40 dark:bg-amber-950/20"
            data-testid={`pasajero-inline-form-${slotLabel || "nuevo"}`}
        >
            {/* Etiqueta del slot: "Adulto 1", "Titular", etc. */}
            <div className="mb-3 flex items-center gap-2">
                <User className="h-4 w-4 text-amber-600 dark:text-amber-400" aria-hidden="true" />
                <span className="text-xs font-bold uppercase tracking-wider text-amber-700 dark:text-amber-300">
                    {slotLabel || "Pasajero"}
                </span>
            </div>

            <div className="flex flex-wrap gap-2">
                {/* Nombre y apellido: siempre requerido */}
                <input
                    type="text"
                    aria-label={`Nombre y apellido — ${slotLabel || "Pasajero"}`}
                    placeholder="Nombre y apellido"
                    value={form.fullName}
                    onChange={e => updateField("fullName", e.target.value)}
                    className={`flex-1 min-w-[160px] ${inputClass}`}
                    autoFocus
                />

                {/* Tipo de documento: solo cuando el mode lo requiere */}
                {campos.documento && (
                    <select
                        aria-label="Tipo de documento"
                        value={form.documentType}
                        onChange={e => updateField("documentType", e.target.value)}
                        className={`w-28 ${inputClass}`}
                    >
                        {DOC_TYPES.map(d => (
                            <option key={d.value} value={d.value}>{d.label}</option>
                        ))}
                    </select>
                )}

                {/* Número de documento: solo cuando el mode lo requiere */}
                {campos.documento && (
                    <input
                        type="text"
                        aria-label="Número de documento"
                        placeholder="N° documento"
                        value={form.documentNumber}
                        onChange={e => updateField("documentNumber", e.target.value)}
                        className={`w-36 ${inputClass}`}
                    />
                )}

                {/* Fecha de nacimiento: solo para asistencia */}
                {campos.fecha && (
                    <input
                        type="date"
                        aria-label="Fecha de nacimiento"
                        value={form.birthDate}
                        onChange={e => updateField("birthDate", e.target.value)}
                        className={`w-40 ${inputClass}`}
                    />
                )}

                {/* Botón Guardar */}
                <button
                    type="button"
                    onClick={handleGuardar}
                    disabled={!formularioListo || guardando}
                    data-testid={`btn-guardar-pasajero-${slotLabel || "nuevo"}`}
                    aria-label="Guardar pasajero"
                    className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-2 text-sm font-bold text-white transition-colors hover:bg-indigo-700 disabled:opacity-50"
                >
                    {guardando
                        ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
                        : <Save className="h-4 w-4" aria-hidden="true" />
                    }
                    {guardando ? "Guardando..." : "Guardar"}
                </button>

                {/* Botón Cancelar */}
                {onCancelar && (
                    <button
                        type="button"
                        onClick={onCancelar}
                        disabled={guardando}
                        aria-label="Cancelar carga de pasajero"
                        className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-2 text-sm font-bold text-slate-600 transition-colors hover:bg-slate-100 disabled:opacity-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        <X className="h-4 w-4" aria-hidden="true" />
                        Cancelar
                    </button>
                )}
            </div>

            {/* Error inline: se muestra debajo del formulario si el guardado falla.
                No se usa toast para no perder los datos que el usuario escribió. */}
            {errorGuardar && (
                <p
                    role="alert"
                    className="mt-2 text-xs font-semibold text-rose-700 dark:text-rose-400"
                >
                    {errorGuardar}
                </p>
            )}
        </div>
    );
}
