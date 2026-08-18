/**
 * Mini-formulario en línea para cargar o completar un pasajero de la reserva.
 *
 * Se usa como "red de seguridad" al intentar resolver/emitir un servicio
 * cuando faltan datos de pasajeros: el formulario aparece debajo del servicio
 * (NUNCA en una ventana flotante, conforme a la guía UX 2026-06-15).
 *
 * También se usa en PassengerList para cargar y editar pasajeros — ahí es
 * donde vive TODO lo que antes hacía el modal PassengerFormModal (jubilado el
 * 2026-08-18, spec T5 sección 3): lupa AFIP, histórico de pasajeros de la
 * agencia y la sección "+ Más detalles" con fecha de nacimiento, vencimientos,
 * nacionalidad, contacto y notas. Esas funciones extra solo se activan con la
 * prop `conFuncionesCompletas` — sin ella, este componente se comporta EXACTO
 * igual que antes (uso en ServiceList.jsx, que no debe cambiar ni de aspecto
 * ni de comportamiento).
 *
 * Qué pide según el tipo de servicio (siempre, con o sin conFuncionesCompletas):
 *   - Aéreo: nombre + tipo + número de documento (todos obligatorios).
 *   - Hotel / Traslado: solo nombre (documento opcional en este paso).
 *   - Asistencia: nombre + documento + fecha de nacimiento.
 *   - Paquete / Genérico: solo nombre.
 *   - Sin contexto de servicio (ej: desde PassengerList): nombre + documento.
 *
 * Props:
 *   reservaId              — publicId de la reserva (para POST /passengers o PUT /passengers/:id)
 *   passengerToEdit        — objeto pasajero existente (para editar); null → crear nuevo
 *   slotLabel              — etiqueta del slot: "Adulto 1", "Menor 2", "Titular", etc.
 *   mode                   — "flight" | "hotel" | "transfer" | "assistance" | "package" | "generic" | "full"
 *                            "full" = pide todos los campos base (nombre + documento)
 *   conFuncionesCompletas  — (default false) prende lupa AFIP, histórico de pasajeros de la
 *                            agencia (solo al crear) y la sección "+ Más detalles". Solo
 *                            PassengerList la pasa en true.
 *   existingPassengers     — pasajeros ya cargados en la reserva (para el aviso de duplicado
 *                            del histórico). Solo se usa con conFuncionesCompletas.
 *   onGuardado             — callback(pasajeroGuardado) — se llama tras guardar exitosamente
 *   onCancelar             — callback() — se llama cuando el usuario cancela
 */

import { useEffect, useRef, useState } from "react";
import { Loader2, Save, X, User, Search, ChevronDown, ChevronUp } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess, showWarning } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { ayudaNumeroDocumento } from "../../../lib/documentoAyuda.js";
import { Button } from "../../../components/ui/button";
import { useDebounce } from "../../../hooks/useDebounce";
import { DropdownHistorico } from "./DropdownHistorico";
import {
    cumpleUmbralBusqueda,
    construirUrlBusquedaHistorica,
    mapearSugerenciaAlForm,
    esDuplicadoEnReserva,
} from "../lib/pasajeroSearchLogic.js";
import { debeAbrirMasDetallesPorDefecto, construirPayloadPasajero } from "../lib/pasajeroInlineFormLogic.js";

// Tipos de documento aceptados por el backend (mismo listado que tenía el modal).
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
// Solo mira los campos "base" (nombre/documento/fecha) — los de "+ Más detalles"
// son todos opcionales, el motor nunca los exige para guardar.
function esFormularioValido(form, mode) {
    const campos = camposRequeridosPorMode(mode);

    if (campos.nombre && form.fullName.trim().length < 2) return false;
    if (campos.documento && !form.documentNumber.trim()) return false;
    if (campos.fecha && !form.birthDate) return false;

    return true;
}

// Clases CSS reutilizables. h-10 = 40px de alto (regla B.3, mismo alto que el resto del sistema).
const inputClass = "h-10 rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white";
const textareaClass = "rounded-[10px] border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white";
const labelClass = "mb-1 block text-[11px] font-semibold text-slate-600 dark:text-slate-400";

export function PasajeroInlineForm({
    reservaId,
    passengerToEdit,
    slotLabel,
    mode = "full",
    conFuncionesCompletas = false,
    existingPassengers = [],
    onGuardado,
    onCancelar,
}) {
    // Inicializamos el form con los datos del pasajero existente si estamos editando,
    // o con un form vacío si estamos creando uno nuevo. Los campos de "+ Más detalles"
    // se guardan acá siempre (aunque no se rendericen sin conFuncionesCompletas): así
    // no hay dos fuentes de verdad distintas para el mismo pasajero.
    const [form, setForm] = useState(() => ({
        fullName: passengerToEdit?.fullName || "",
        // Al EDITAR se respeta el tipo guardado tal cual, aunque esté vacío: si arrancáramos en "DNI"
        // para un pasajero viejo sin tipo cargado, el formulario inventaría un par "DNI + número de
        // pasaporte" que nadie eligió (y el motor, con razón, lo rechazaría). Al CREAR sí arranca en DNI,
        // que es el caso normal.
        documentType: passengerToEdit ? (passengerToEdit.documentType || "") : "DNI",
        documentNumber: passengerToEdit?.documentNumber || "",
        birthDate: passengerToEdit?.birthDate
            ? passengerToEdit.birthDate.split("T")[0]
            : "",
        passportExpiry: passengerToEdit?.passportExpiry ? passengerToEdit.passportExpiry.split("T")[0] : "",
        documentExpiry: passengerToEdit?.documentExpiry ? passengerToEdit.documentExpiry.split("T")[0] : "",
        nationality: passengerToEdit?.nationality || "",
        gender: passengerToEdit?.gender || "M",
        phone: passengerToEdit?.phone || "",
        email: passengerToEdit?.email || "",
        notes: passengerToEdit?.notes || "",
    }));
    const [guardando, setGuardando] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    // "+ Más detalles": plegado por defecto, salvo que el pasajero editado ya tenga
    // algún dato ahí adentro (P5=A de la spec: nada de esconder datos que ya existen).
    const [mostrarDetalles, setMostrarDetalles] = useState(() => debeAbrirMasDetallesPorDefecto(passengerToEdit));

    // ─── Histórico (base propia de pasajeros de la agencia) — solo al CREAR ──────
    // Portado tal cual del extinto PassengerFormModal (mismo debounce, mismo umbral).
    const [sugerenciasHistoricas, setSugerenciasHistoricas] = useState([]);
    const [cargandoHistoricos, setCargandoHistoricos] = useState(false);
    // "name" o "document" según qué campo disparó la búsqueda activa.
    const [campoConDropdown, setCampoConDropdown] = useState(null);
    // Evita que el debounce redispare la búsqueda justo después de elegir una sugerencia.
    const eligioSugerencia = useRef(false);

    // ─── Padrón AFIP (búsqueda manual por botón + automática al tipear al crear) ─
    const [afipResults, setAfipResults] = useState([]);
    const [loadingAfip, setLoadingAfip] = useState(false);

    const campos = camposRequeridosPorMode(mode);
    const formularioListo = esFormularioValido(form, mode);
    const passengerPublicId = passengerToEdit ? getPublicId(passengerToEdit) : null;
    const esAlta = !passengerToEdit;

    // Debounce de 400ms para no disparar requests en cada tecla (mismo valor que el modal).
    const debouncedFullName = useDebounce(form.fullName, 400);
    const debouncedDocumentNumber = useDebounce(form.documentNumber, 400);

    const updateField = (field, value) => {
        setForm(prev => ({ ...prev, [field]: value }));
        // Si el usuario corrige algo, limpiamos el error anterior para no confundir.
        if (errorGuardar) setErrorGuardar(null);
    };

    // ─── Búsqueda en la BASE PROPIA (histórico) ──────────────────────────────────

    const buscarHistorico = async (campo, formActual) => {
        setCargandoHistoricos(true);
        setCampoConDropdown(campo);
        try {
            const url = construirUrlBusquedaHistorica(campo, formActual);
            const resultados = await api.get(url);
            setSugerenciasHistoricas(resultados || []);
        } catch (error) {
            // Error silencioso: la búsqueda histórica es una ayuda opcional. Si falla,
            // el usuario puede seguir completando el form a mano.
            console.warn("Búsqueda de pasajeros históricos no disponible:", error);
            setSugerenciasHistoricas([]);
            setCampoConDropdown(null);
        } finally {
            setCargandoHistoricos(false);
        }
    };

    const cerrarDropdownHistorico = () => {
        setSugerenciasHistoricas([]);
        setCargandoHistoricos(false);
        setCampoConDropdown(null);
    };

    const handleElegirHistorico = (sugerencia) => {
        // Regla de dedup: si ya está cargado en esta reserva, avisamos sin bloquear
        // (2026-06-23) y no autocompletamos para no pisar lo que hay.
        if (esDuplicadoEnReserva(sugerencia, existingPassengers)) {
            showWarning(
                `Este pasajero ya está cargado en la reserva (${sugerencia.documentType} ${sugerencia.documentNumber}).`,
                "Pasajero duplicado"
            );
            cerrarDropdownHistorico();
            return;
        }

        const camposAutocompletados = mapearSugerenciaAlForm(sugerencia);
        setForm(prev => ({ ...prev, ...camposAutocompletados }));
        cerrarDropdownHistorico();
        setAfipResults([]);
        eligioSugerencia.current = true;
    };

    // Búsqueda histórica al tipear NOMBRE — solo al crear, solo con funciones completas.
    useEffect(() => {
        if (!conFuncionesCompletas || !esAlta) return;

        if (eligioSugerencia.current) {
            eligioSugerencia.current = false;
            return;
        }

        if (cumpleUmbralBusqueda(debouncedFullName)) {
            buscarHistorico("name", form);
        } else if (campoConDropdown === "name") {
            cerrarDropdownHistorico();
        }
        // form se excluye de las deps a propósito: solo queremos reaccionar al debounce
        // del campo nombre, no a cada tecleo de otros campos del formulario.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [debouncedFullName, conFuncionesCompletas, esAlta]);

    // Búsqueda histórica + AFIP automática al tipear DOCUMENTO — solo al crear.
    useEffect(() => {
        if (!conFuncionesCompletas || !esAlta) return;

        if (eligioSugerencia.current) return; // el reset lo hace el effect de nombre

        if (cumpleUmbralBusqueda(debouncedDocumentNumber)) {
            buscarHistorico("document", form);
            handleAfipSearch(debouncedDocumentNumber);
        } else {
            if (campoConDropdown === "document") cerrarDropdownHistorico();
            setAfipResults([]);
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [debouncedDocumentNumber, conFuncionesCompletas, esAlta]);

    // ─── AFIP: búsqueda manual (botón lupa) + automática (arriba) ────────────────

    const handleAfipSearch = async (query) => {
        if (!query) return;
        if (query.length < 3) {
            showWarning("Escribí al menos 3 caracteres.", "Padrón AFIP");
            return;
        }

        setLoadingAfip(true);
        try {
            const genderParam = form.gender ? `&gender=${form.gender}` : "";
            const data = await api.get(`/fiscal/search?q=${encodeURIComponent(query)}${genderParam}`);
            setAfipResults(data || []);
            if (!data || data.length === 0) {
                showWarning("No se encontraron resultados con ese documento.", "Padrón AFIP");
            }
        } catch (error) {
            console.error(error);
            showWarning(getApiErrorMessage(error, "Servicio no disponible temporalmente"), "Servicio AFIP");
        } finally {
            setLoadingAfip(false);
        }
    };

    const handleAfipSelect = (persona) => {
        setForm(prev => ({
            ...prev,
            fullName: persona.razonSocial || `${persona.apellido || ""} ${persona.nombre || ""}`.trim(),
            documentNumber: persona.id || prev.documentNumber,
        }));
        setAfipResults([]);
        eligioSugerencia.current = true;
        showSuccess("Datos de AFIP aplicados.");
    };

    // ─── Guardar ──────────────────────────────────────────────────────────────

    const handleGuardar = async () => {
        if (!formularioListo || guardando) return;

        setGuardando(true);
        setErrorGuardar(null);

        const payload = construirPayloadPasajero({ form, conFuncionesCompletas, passengerToEdit });

        try {
            let pasajeroGuardado;
            if (passengerPublicId) {
                // Editar pasajero existente: PUT /reservas/passengers/:id
                pasajeroGuardado = await api.put(`/reservas/passengers/${passengerPublicId}`, payload);
            } else {
                // Crear pasajero nuevo: POST /reservas/:reservaId/passengers
                pasajeroGuardado = await api.post(`/reservas/${reservaId}/passengers`, payload);
            }

            // Aviso NO bloqueante del motor (hoy: pasaporte vencido). El pasajero SE GUARDA
            // igual —por eso primero va el guardado y después el aviso—; portado del modal,
            // aplica con y sin conFuncionesCompletas (es inofensivo, antes el inline lo perdía).
            if (pasajeroGuardado?.warning) {
                showWarning(pasajeroGuardado.warning);
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

    return (
        <div
            className="rounded-[10px] border border-amber-200 bg-amber-50 p-4 dark:border-amber-800/40 dark:bg-amber-950/20"
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
                {/* Nombre y apellido: siempre requerido. Envuelto en relative para poder
                    colgar el dropdown de histórico debajo, solo con funciones completas. */}
                <div className="relative flex-1 min-w-[160px]">
                    <input
                        type="text"
                        aria-label={`Nombre y apellido — ${slotLabel || "Pasajero"}`}
                        placeholder="Nombre y apellido"
                        value={form.fullName}
                        onChange={e => updateField("fullName", e.target.value)}
                        className={`w-full ${inputClass}`}
                        autoFocus
                        aria-autocomplete={conFuncionesCompletas ? "list" : undefined}
                        aria-expanded={conFuncionesCompletas ? campoConDropdown === "name" : undefined}
                    />
                    {conFuncionesCompletas && esAlta && campoConDropdown === "name" && (
                        <DropdownHistorico
                            sugerencias={sugerenciasHistoricas}
                            cargando={cargandoHistoricos}
                            onElegir={handleElegirHistorico}
                            onCerrar={cerrarDropdownHistorico}
                        />
                    )}
                </div>

                {/* Tipo de documento: solo cuando el mode lo requiere.
                    F2 (deuda 31/07): al editar un pasajero viejo sin tipo cargado, form.documentType
                    arranca en "" (a propósito, ver el comentario de más arriba) — pero como
                    DOC_TYPES no tenía ninguna opción con value="", el <select> quedaba en blanco,
                    sin ninguna opción marcada (confuso: parece un campo roto, no uno vacío).
                    Agregamos "Sin tipo" como opción explícita para ese caso. Elegirla a mano no
                    cambia nada (sigue mandando "" al guardar, que el motor interpreta como "no
                    tocar el tipo guardado" — paridad vacío=no-tocar, sin relajar esa regla). */}
                {campos.documento && (
                    <select
                        aria-label="Tipo de documento"
                        value={form.documentType}
                        onChange={e => updateField("documentType", e.target.value)}
                        className={`w-28 ${inputClass}`}
                    >
                        {!form.documentType && <option value="">Sin tipo</option>}
                        {DOC_TYPES.map(d => (
                            <option key={d.value} value={d.value}>{d.label}</option>
                        ))}
                    </select>
                )}

                {/* Número de documento: solo cuando el mode lo requiere. Con funciones
                    completas suma la lupa AFIP + el histórico + el dropdown AFIP, todos
                    colgando de este mismo campo (igual que en el modal viejo). */}
                {campos.documento && (
                    <div className="relative w-36">
                        <input
                            type="text"
                            aria-label="Número de documento"
                            // La ayuda depende del tipo elegido arriba (ver documentoAyuda.js).
                            placeholder={ayudaNumeroDocumento(form.documentType)}
                            value={form.documentNumber}
                            onChange={e => updateField("documentNumber", e.target.value)}
                            className={`w-full ${conFuncionesCompletas ? "pr-9" : ""} ${inputClass}`}
                            aria-autocomplete={conFuncionesCompletas ? "list" : undefined}
                            aria-expanded={conFuncionesCompletas ? campoConDropdown === "document" : undefined}
                        />

                        {/* Lupa AFIP: búsqueda manual (crear y editar). */}
                        {conFuncionesCompletas && (
                            <button
                                type="button"
                                onClick={() => handleAfipSearch(form.documentNumber)}
                                className="absolute right-1.5 top-1/2 -translate-y-1/2 rounded-[8px] p-1 text-slate-400 transition-colors hover:bg-blue-50 hover:text-blue-600 dark:hover:bg-blue-900/40"
                                title="Buscar en AFIP"
                                aria-label="Buscar en el padrón de AFIP"
                            >
                                {loadingAfip
                                    ? <Loader2 className="h-3.5 w-3.5 animate-spin text-blue-500" aria-hidden="true" />
                                    : <Search className="h-3.5 w-3.5" aria-hidden="true" />
                                }
                            </button>
                        )}

                        {/* Histórico bajo el campo documento — solo al crear. */}
                        {conFuncionesCompletas && esAlta && campoConDropdown === "document" && (
                            <DropdownHistorico
                                sugerencias={sugerenciasHistoricas}
                                cargando={cargandoHistoricos}
                                onElegir={handleElegirHistorico}
                                onCerrar={cerrarDropdownHistorico}
                            />
                        )}

                        {/* Dropdown AFIP: resultados de la búsqueda manual/automática. */}
                        {conFuncionesCompletas && afipResults.length > 0 && (
                            <div className="absolute left-0 right-0 z-[100] mt-1 overflow-hidden rounded-[10px] border border-slate-200 bg-white shadow-xl dark:border-slate-700 dark:bg-slate-800">
                                <div className="flex items-center justify-between border-b border-slate-100 bg-slate-50 px-3 py-2 dark:border-slate-700 dark:bg-slate-900/50">
                                    <span className="text-[11px] font-bold uppercase tracking-wider text-slate-500">Sugerencias AFIP</span>
                                    <button type="button" onClick={() => setAfipResults([])} className="text-slate-400 hover:text-slate-600" aria-label="Cerrar sugerencias de AFIP">
                                        <X className="h-3.5 w-3.5" />
                                    </button>
                                </div>
                                <div className="max-h-44 overflow-y-auto">
                                    {afipResults.map((persona, index) => (
                                        <button
                                            key={`${persona.id || "afip"}-${index}`}
                                            type="button"
                                            onClick={() => handleAfipSelect(persona)}
                                            className="group w-full border-b border-slate-50 px-4 py-2 text-left transition-colors last:border-0 hover:bg-blue-50 dark:border-slate-700 dark:hover:bg-blue-900/30"
                                        >
                                            <div className="truncate text-sm font-semibold text-slate-900 group-hover:text-blue-600 dark:text-white">
                                                {persona.razonSocial || `${persona.apellido || ""} ${persona.nombre || ""}`.trim()}
                                            </div>
                                            <div className="text-[11px] text-slate-500">{persona.id} - {persona.taxCondition}</div>
                                        </button>
                                    ))}
                                </div>
                            </div>
                        )}
                    </div>
                )}

                {/* Fecha de nacimiento: solo para asistencia (fuera de "+ Más detalles" —
                    ahí ya está pedida como parte de los campos base del servicio). */}
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
                <Button
                    type="button"
                    onClick={handleGuardar}
                    disabled={!formularioListo || guardando}
                    data-testid={`btn-guardar-pasajero-${slotLabel || "nuevo"}`}
                    aria-label="Guardar pasajero"
                    className="gap-1.5"
                >
                    {guardando
                        ? <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
                        : <Save className="h-4 w-4" aria-hidden="true" />
                    }
                    {guardando ? "Guardando..." : "Guardar"}
                </Button>

                {/* Botón Cancelar */}
                {onCancelar && (
                    <Button
                        type="button"
                        variant="outline"
                        onClick={onCancelar}
                        disabled={guardando}
                        aria-label="Cancelar carga de pasajero"
                        className="gap-1.5"
                    >
                        <X className="h-4 w-4" aria-hidden="true" />
                        Cancelar
                    </Button>
                )}
            </div>

            {/* "+ Más detalles": solo con funciones completas. Mismo molde que
                HotelInlineForm.jsx (mismo texto, mismo ícono, plegado por defecto salvo
                que ya haya datos cargados — ver debeAbrirMasDetallesPorDefecto). */}
            {conFuncionesCompletas && (
                <div className="mt-3">
                    <button
                        type="button"
                        onClick={() => setMostrarDetalles(prev => !prev)}
                        className="flex items-center gap-1 text-xs font-semibold text-primary hover:opacity-80 transition-colors"
                        data-testid={`pasajero-mas-detalles-toggle-${slotLabel || "nuevo"}`}
                        aria-expanded={mostrarDetalles}
                    >
                        {mostrarDetalles ? <ChevronUp className="h-3.5 w-3.5" aria-hidden="true" /> : <ChevronDown className="h-3.5 w-3.5" aria-hidden="true" />}
                        {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                    </button>

                    {mostrarDetalles && (
                        <div className="mt-2 grid grid-cols-1 gap-2 rounded-[10px] border border-amber-200/60 bg-white/60 p-3 dark:border-amber-800/30 dark:bg-slate-900/20 sm:grid-cols-2">
                            <div>
                                <label className={labelClass} htmlFor={`pax-birthdate-${slotLabel || "nuevo"}`}>Fecha de nacimiento</label>
                                <input
                                    id={`pax-birthdate-${slotLabel || "nuevo"}`}
                                    type="date"
                                    className={`w-full ${inputClass}`}
                                    value={form.birthDate}
                                    onChange={e => updateField("birthDate", e.target.value)}
                                />
                            </div>
                            <div>
                                <label className={labelClass} htmlFor={`pax-passport-expiry-${slotLabel || "nuevo"}`}>Vencimiento del pasaporte</label>
                                <input
                                    id={`pax-passport-expiry-${slotLabel || "nuevo"}`}
                                    type="date"
                                    className={`w-full ${inputClass}`}
                                    value={form.passportExpiry}
                                    onChange={e => updateField("passportExpiry", e.target.value)}
                                />
                            </div>
                            {/* Vencimiento DNI: solo si el tipo de documento elegido es DNI —
                                mismo criterio que tenía el modal (spec 2026-08-03). */}
                            {form.documentType === "DNI" && (
                                <div>
                                    <label className={labelClass} htmlFor={`pax-dni-expiry-${slotLabel || "nuevo"}`}>Vencimiento DNI</label>
                                    <input
                                        id={`pax-dni-expiry-${slotLabel || "nuevo"}`}
                                        type="date"
                                        className={`w-full ${inputClass}`}
                                        value={form.documentExpiry}
                                        onChange={e => updateField("documentExpiry", e.target.value)}
                                        data-testid="input-vencimiento-dni-inline"
                                    />
                                </div>
                            )}
                            <div>
                                <label className={labelClass} htmlFor={`pax-nationality-${slotLabel || "nuevo"}`}>Nacionalidad</label>
                                <input
                                    id={`pax-nationality-${slotLabel || "nuevo"}`}
                                    type="text"
                                    className={`w-full ${inputClass}`}
                                    placeholder="Ej: Argentina"
                                    value={form.nationality}
                                    onChange={e => updateField("nationality", e.target.value)}
                                />
                            </div>
                            <div>
                                <label className={labelClass} htmlFor={`pax-gender-${slotLabel || "nuevo"}`}>Género</label>
                                <select
                                    id={`pax-gender-${slotLabel || "nuevo"}`}
                                    className={`w-full ${inputClass}`}
                                    value={form.gender}
                                    onChange={e => updateField("gender", e.target.value)}
                                >
                                    <option value="M">Masculino</option>
                                    <option value="F">Femenino</option>
                                    <option value="X">Otro</option>
                                </select>
                            </div>
                            <div>
                                <label className={labelClass} htmlFor={`pax-phone-${slotLabel || "nuevo"}`}>Teléfono</label>
                                <input
                                    id={`pax-phone-${slotLabel || "nuevo"}`}
                                    type="tel"
                                    className={`w-full ${inputClass}`}
                                    placeholder="+54 9 11..."
                                    value={form.phone}
                                    onChange={e => updateField("phone", e.target.value)}
                                />
                            </div>
                            <div>
                                <label className={labelClass} htmlFor={`pax-email-${slotLabel || "nuevo"}`}>Email</label>
                                <input
                                    id={`pax-email-${slotLabel || "nuevo"}`}
                                    type="email"
                                    className={`w-full ${inputClass}`}
                                    placeholder="correo@ejemplo.com"
                                    value={form.email}
                                    onChange={e => updateField("email", e.target.value)}
                                />
                            </div>
                            <div className="sm:col-span-2">
                                <label className={labelClass} htmlFor={`pax-notes-${slotLabel || "nuevo"}`}>Notas</label>
                                <textarea
                                    id={`pax-notes-${slotLabel || "nuevo"}`}
                                    rows={2}
                                    className={`w-full ${textareaClass}`}
                                    placeholder="Preferencias alimenticias, asistencia especial..."
                                    value={form.notes}
                                    onChange={e => updateField("notes", e.target.value)}
                                />
                            </div>
                        </div>
                    )}
                </div>
            )}

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
