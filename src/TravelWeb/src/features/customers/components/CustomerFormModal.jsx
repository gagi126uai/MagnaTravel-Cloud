import { useState, useEffect } from "react";
import { User, Mail, Phone, XCircle, Search, Loader2 } from "lucide-react";
import { useDebounce } from "../../../hooks/useDebounce";
import { api } from "../../../api";
import { showSuccess, showWarning } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import {
    DOCUMENT_TYPE_OPTIONS,
    aplicarResultadoAfip,
    construirEstadoInicialDocumento,
    construirPayloadDocumento,
    esTipoDocumentoFiscal,
    tipoDocumentoTieneBusquedaAfip,
} from "../lib/customerDocumentLogic";

/**
 * Modal de alta/edición rápida de cliente desde el listado (`CustomersPage`).
 *
 * P1 (mockup A firmado, 2026-07-25): el documento es UN SOLO casillero — desplegable de
 * tipo (CUIT/CUIL/DNI/Pasaporte/Otro) + número + lupita AFIP para los tipos que están en
 * el padrón (CUIT/CUIL/DNI). Antes había dos casilleros sueltos ("Documento/Pasaporte" y
 * "CUIT/Documento") y el form nunca mandaba `documentType`, lo que dejaba MUERTO el guard
 * de duplicados del motor (hallazgo H3) — ver `customerDocumentLogic.js` para el mapeo.
 */
export function CustomerFormModal({ isOpen, onClose, customer, onSave }) {
    const [formData, setFormData] = useState(() => ({
        fullName: customer?.fullName || "",
        email: customer?.email || "",
        phone: customer?.phone || "",
        address: customer?.address || "",
        notes: customer?.notes || "",
        creditLimit: customer?.creditLimit || 0,
        isActive: customer?.isActive ?? true,
        taxConditionId: customer?.taxConditionId || 5, // Default Conf. Final
        ...construirEstadoInicialDocumento(customer),
    }));
    // Foto del casillero de documento tal como arrancó la ficha (B2, revisión 2026-07-27):
    // este modal se remonta desde cero cada vez que se abre (CustomersPage lo renderiza
    // detrás de `isModalOpen &&`), así que este useState de inicialización perezosa solo
    // corre UNA vez por apertura — sirve de punto de comparación para saber si el usuario
    // tocó el casillero antes de guardar (ver handleSubmit → construirPayloadDocumento).
    const [documentoInicial] = useState(() => construirEstadoInicialDocumento(customer));
    const [afipResults, setAfipResults] = useState([]);
    const [loadingAfip, setLoadingAfip] = useState(false);
    const [searchingField, setSearchingField] = useState(null); // 'name' or 'document'
    const [similarMatches, setSimilarMatches] = useState([]);

    // Flag to prevent searching right after selecting a result
    const [justSelected, setJustSelected] = useState(false);

    // Guardado: bloquea el doble submit y muestra el error del motor EN LÍNEA (P-6/P-7),
    // nunca en un toast que se va solo — importante acá porque este envío puede chocar
    // con el guard de duplicados (H3) o el CUIT inválido (H2), y el usuario necesita leer
    // el motivo con calma para corregir.
    const [saving, setSaving] = useState(false);
    const [errorGuardado, setErrorGuardado] = useState(null);

    const debouncedNumeroDocumento = useDebounce(formData.numeroDocumento, 500);
    const debouncedFullName = useDebounce(formData.fullName, 500);

    useEffect(() => {
        if (!isOpen) return;
        if (customer) return; // No sugerir cuando se edita un cliente existente

        const fullName = (debouncedFullName || "").trim();
        const documentNumber = (debouncedNumeroDocumento || "").trim();
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
    }, [debouncedFullName, debouncedNumeroDocumento, isOpen, customer]);

    if (!isOpen) return null;

    // esBusquedaManual: distingue el click de la lupita (el usuario pidió expresamente
    // buscar) de la búsqueda automática que dispara el useEffect de abajo mientras tipea.
    // Fix menor (revisión 2026-07-27): antes el aviso "No se encontraron resultados..."
    // salía en CADA pausa al tipear un DNI/CUIT que todavía no existe en el padrón — ruido
    // para un dato que el usuario puede estar recién completando. Ahora ese aviso solo se
    // muestra cuando la búsqueda la pidió el propio usuario con la lupita.
    const handleAfipSearch = async (query, field, { esBusquedaManual = false } = {}) => {
        if (!query) return;
        if (query.length < 3) {
            if (esBusquedaManual) showWarning("Ingresá al menos 3 caracteres.", "Padrón AFIP");
            return;
        }
        setLoadingAfip(true);
        setSearchingField(field);
        try {
            const data = await api.get(`/fiscal/search?q=${encodeURIComponent(query)}`);
            setAfipResults(data);
            if (data.length === 0 && esBusquedaManual) {
                showWarning("No se encontraron resultados con ese CUIT/DNI.", "Padrón AFIP");
            }
        } catch (error) {
            showWarning(getApiErrorMessage(error, "Servicio no disponible temporalmente"), "Servicio AFIP");
        } finally {
            setLoadingAfip(false);
        }
    };

    // Búsqueda automática en AFIP al tipear el número, SOLO para los tipos que están en
    // el padrón (CUIT/CUIL/DNI — ver tipoDocumentoTieneBusquedaAfip). Mismo criterio que
    // el botón de lupa: si el tipo elegido es Pasaporte/Otro, no tiene sentido consultarlo.
    useEffect(() => {
        if (!isOpen) return;
        if (justSelected) {
            setJustSelected(false); // Reset flag
            return;
        }
        if (customer) return; // Solo autocompleta en alta, no en edición

        if (!tipoDocumentoTieneBusquedaAfip(formData.tipoDocumento)) {
            if (searchingField === "document") setAfipResults([]);
            return;
        }

        if (debouncedNumeroDocumento && debouncedNumeroDocumento.length >= 3) {
            if (searchingField !== "name") {
                handleAfipSearch(debouncedNumeroDocumento, "document");
            }
        } else if (searchingField === "document") {
            setAfipResults([]);
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [debouncedNumeroDocumento, formData.tipoDocumento, isOpen, customer]);

    const handleAfipSelect = (persona) => {
        setFormData((prev) => ({
            ...prev,
            fullName: persona.razonSocial || `${persona.apellido || ""} ${persona.nombre || ""}`.trim(),
            // B1 (revisión 2026-07-27): lo que devuelve el padrón (persona.id) SIEMPRE es un
            // CUIT/CUIL de 11 dígitos, nunca un DNI — aplicarResultadoAfip sube el tipo a
            // CUIT si el casillero estaba en un tipo no fiscal (ver docstring del helper).
            ...aplicarResultadoAfip({ tipoDocumento: prev.tipoDocumento, numeroDocumento: prev.numeroDocumento }, persona),
            taxConditionId: persona.taxConditionId || prev.taxConditionId,
        }));
        setAfipResults([]);
        setSearchingField(null);
        setJustSelected(true); // Prevent immediate re-trigger
        showSuccess("Datos de AFIP aplicados.");
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        setErrorGuardado(null);

        // H12: validación propia en español. noValidate en el <form> apaga el cartelito
        // nativo del navegador (en inglés) para el "required" del Nombre completo — el
        // control queda 100% en React y el mensaje que ve el vendedor es el nuestro.
        if (!formData.fullName.trim()) {
            setErrorGuardado("El nombre completo es obligatorio.");
            return;
        }

        setSaving(true);
        try {
            const { tipoDocumento, numeroDocumento, ...resto } = formData;
            // B2 (revisión 2026-07-27): "tocado" se calcula comparando contra la FOTO de
            // cómo arrancó la ficha (documentoInicial), no con un flag manual — así no hay
            // forma de olvidarse de marcarlo en algún onChange nuevo que se agregue después.
            const documentoFueTocado =
                tipoDocumento !== documentoInicial.tipoDocumento || numeroDocumento !== documentoInicial.numeroDocumento;
            const payload = {
                ...resto,
                ...construirPayloadDocumento({ tipoDocumento, numeroDocumento, documentoFueTocado, clienteOriginal: customer }),
            };
            await onSave(payload, getPublicId(customer));
            // onSave (useCustomers.handleSaveCustomer) ya muestra el toast de éxito y
            // devuelve el resultado; si hubo error, lo lanza para que lo mostremos acá.
        } catch (error) {
            // La ficha queda abierta con todo lo cargado intacto + cartel rojo (P-7): acá
            // puede llegar el motivo real del motor (CUIT inválido, cliente duplicado).
            setErrorGuardado(getApiErrorMessage(error, "No se pudo guardar el cliente. Revisá la conexión y probá de nuevo."));
        } finally {
            setSaving(false);
        }
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

                {/* noValidate (H12, obra C1): el navegador cortaba el submit con SU propio
                    cartelito de validación en inglés por el "required" del Nombre completo.
                    Con noValidate, React controla la validación y el mensaje es el nuestro. */}
                <form onSubmit={handleSubmit} noValidate>
                    <div className="p-6 space-y-4">
                        <div className="grid gap-4 sm:grid-cols-2">
                            <div className="col-span-2 space-y-1.5">
                                {/* Menor d) del barrido de estándares (2026-07-27): label sin htmlFor —
                                    mismo patrón de a11y que ya se usa en el casillero de documento de
                                    más abajo. */}
                                <label htmlFor="customer-modal-fullName" className="text-sm font-medium text-slate-700 dark:text-slate-300">Nombre Completo <span className="text-red-500">*</span></label>
                                <div className="relative">
                                    <User className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                    <input
                                        id="customer-modal-fullName"
                                        type="text"
                                        value={formData.fullName}
                                        onChange={(e) => setFormData({ ...formData, fullName: e.target.value })}
                                        className="w-full rounded-md border border-input bg-background dark:bg-slate-950 pl-9 pr-10 py-2 text-sm outline-none ring-offset-background focus:ring-2 focus:ring-indigo-500"
                                        placeholder="Ej. Juan Pérez"
                                    />
                                </div>
                            </div>

                            {/* Casillero de documento unificado (P1): tipo + número + lupita AFIP
                                condicional. Reemplaza los dos campos sueltos que había antes. */}
                            <div className="col-span-2 space-y-1.5">
                                {/* Fix a11y (ítem 5 del re-review, 2026-07-27): el label "Documento" no
                                    apuntaba a ningún control con htmlFor, y el select de tipo + el input
                                    de número no tenían id/aria-label propio — para un lector de pantalla
                                    los dos campos quedaban mudos (no sabía qué era cada uno). El label
                                    ahora apunta al select (tipo de documento, el primero del grupo) y el
                                    input de número lleva su propio aria-label, ya que un solo <label>
                                    visual no alcanza para describir DOS controles distintos. */}
                                <label htmlFor="customer-document-type" className="text-sm font-medium text-slate-700 dark:text-slate-300">Documento</label>
                                <div className="grid grid-cols-[auto_1fr] gap-2">
                                    <select
                                        id="customer-document-type"
                                        value={formData.tipoDocumento}
                                        onChange={(e) => {
                                            const tipoDocumento = e.target.value;
                                            setFormData((prev) => ({ ...prev, tipoDocumento }));
                                            if (!tipoDocumentoTieneBusquedaAfip(tipoDocumento)) {
                                                setAfipResults([]);
                                                setSearchingField(null);
                                            }
                                        }}
                                        className="rounded-md border border-input bg-background dark:bg-slate-950 px-2 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500"
                                        data-testid="customer-modal-document-type"
                                    >
                                        {DOCUMENT_TYPE_OPTIONS.map((opcion) => (
                                            <option key={opcion.value} value={opcion.value}>{opcion.label}</option>
                                        ))}
                                    </select>

                                    <div className="relative">
                                        <input
                                            type="text"
                                            aria-label="Número de documento"
                                            placeholder={esTipoDocumentoFiscal(formData.tipoDocumento) ? "20-30111222-3" : "Número de documento"}
                                            className="w-full rounded-md border border-input bg-background dark:bg-slate-950 py-2 pr-10 px-3 text-sm outline-none focus:ring-2 focus:ring-indigo-500 font-mono"
                                            value={formData.numeroDocumento}
                                            onChange={(e) => {
                                                setFormData({ ...formData, numeroDocumento: e.target.value });
                                                if (searchingField === "document") setSearchingField(null);
                                            }}
                                            data-testid="customer-modal-document-number"
                                        />
                                        {/* Lupita AFIP: SOLO para CUIT/CUIL/DNI (mockup firmado) */}
                                        {tipoDocumentoTieneBusquedaAfip(formData.tipoDocumento) && (
                                            <button
                                                type="button"
                                                onClick={() => handleAfipSearch(formData.numeroDocumento, "document", { esBusquedaManual: true })}
                                                className="absolute right-2 top-2 p-1 text-slate-400 hover:text-indigo-600 transition-colors"
                                                title="Buscar en AFIP"
                                            >
                                                {loadingAfip && searchingField === "document" ? <Loader2 className="h-4 w-4 animate-spin text-indigo-500" /> : <Search className="h-4 w-4" />}
                                            </button>
                                        )}

                                        {afipResults.length > 0 && searchingField === "document" && (
                                            <div className="absolute left-0 right-0 z-[100] mt-1 w-full bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-lg shadow-xl overflow-hidden animate-in fade-in slide-in-from-top-2 duration-200">
                                                <div className="px-3 py-2 bg-slate-50 dark:bg-slate-800 border-b border-slate-100 dark:border-slate-700 flex justify-between items-center">
                                                    <span className="text-[10px] font-bold text-slate-500 uppercase">Resultados AFIP</span>
                                                    <button type="button" onClick={() => { setAfipResults([]); setSearchingField(null); }} className="text-slate-400 hover:text-slate-600"><XCircle className="h-3 w-3" /></button>
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
                            </div>

                            <div className="col-span-2 space-y-1.5">
                                <label htmlFor="customer-modal-taxConditionId" className="text-sm font-medium text-slate-700 dark:text-slate-300">Condición AFIP <span className="text-red-500">*</span></label>
                                <select
                                    id="customer-modal-taxConditionId"
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
                                    <label htmlFor="customer-modal-email" className="text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
                                    <div className="relative">
                                        <Mail className="absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                                        <input
                                            id="customer-modal-email"
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

                        {/* Cartel de error (P-6/P-7): en línea, se queda a la vista mientras el
                            usuario corrige — nunca un toast que desaparece solo. Acá llega, por
                            ejemplo, "Ya existe un cliente con DNI 30405060" (H3) o el CUIT inválido (H2).
                            whitespace-pre-line (P-13): respeta los saltos de línea que arma el motor. */}
                        {errorGuardado && (
                            <p role="alert" className="text-sm text-rose-600 dark:text-rose-400 whitespace-pre-line" data-testid="customer-modal-error">
                                {errorGuardado}
                            </p>
                        )}
                    </div>

                    <div className="flex gap-3 px-6 py-4 border-t bg-slate-50/50 dark:bg-slate-900/50">
                        <button
                            type="button"
                            onClick={onClose}
                            disabled={saving}
                            className="flex-1 rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={saving}
                            className="flex-1 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 shadow-sm transition-colors disabled:opacity-50"
                        >
                            {saving ? "Guardando…" : customer ? "Guardar Cambios" : "Crear Cliente"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
