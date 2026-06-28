/**
 * Lista de cuentas bancarias de un dueño (agencia, cliente o proveedor).
 *
 * Reutilizable para los tres tipos de dueño (ownerType).
 * Muestra CBU/alias TAPADOS por defecto; "Ver completo" llama al detalle (GET /{id})
 * y revela el dato completo con un botón "Copiar" al portapapeles.
 * El botón "Ver completo" NO tiene gate de permiso adicional: el backend gatea
 * el detalle por el mismo permiso del dueño que ya se requirió para ver la lista.
 * El alta/edición se hace en línea, nunca en modal (regla UX 2026-06-09).
 *
 * Props:
 *   - ownerType: "Agency" | "Customer" | "Supplier" (convertido a int internamente)
 *   - ownerId: string | number  (0 para Agency, publicId para los otros)
 *   - title: string — encabezado de la sección (default: "Datos bancarios")
 *   - canEdit: boolean — si el usuario tiene permiso para agregar/editar/borrar cuentas
 */

import { useState, useEffect, useCallback } from "react";
import {
    Plus,
    Pencil,
    Trash2,
    Star,
    Eye,
    Copy,
    Check,
    AlertCircle,
    Loader2,
} from "lucide-react";
import { api } from "../../../api";
import { showSuccess, showError, showConfirm } from "../../../alerts";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { OWNER_TYPE, ACCOUNT_TYPE } from "../lib/bankAccountLogic";
import { FichaCuentaBancariaInline } from "./FichaCuentaBancariaInline";

// Mapa de int (enum del backend) → etiqueta visible para el tipo de cuenta.
// Se usa para mostrar "Caja de Ahorro" / "Cuenta Corriente" en lugar del número crudo.
const LABEL_TIPO_CUENTA = {
    [ACCOUNT_TYPE.CajaAhorro]: "Caja de Ahorro",
    [ACCOUNT_TYPE.CuentaCorriente]: "Cuenta Corriente",
};

// ─── Copiar al portapapeles ───────────────────────────────────────────────────

/**
 * Intenta copiar un texto al portapapeles del usuario.
 * Usa la Clipboard API moderna con fallback al execCommand para contextos sin HTTPS.
 * Retorna true si tuvo éxito.
 */
async function copiarAlPortapapeles(texto) {
    try {
        await navigator.clipboard.writeText(texto);
        return true;
    } catch {
        // Fallback: crea un textarea temporal fuera de pantalla para el copy
        const textarea = document.createElement("textarea");
        textarea.value = texto;
        textarea.style.position = "fixed";
        textarea.style.opacity = "0";
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        try {
            return document.execCommand("copy");
        } catch {
            return false;
        } finally {
            document.body.removeChild(textarea);
        }
    }
}

// ─── Fila de una cuenta bancaria ─────────────────────────────────────────────

/**
 * Tarjeta visual de una cuenta dentro de la lista.
 * Muestra los datos tapados; "Ver completo" carga el detalle y habilita "Copiar".
 * Al tocar "Editar", primero carga el detalle si no está disponible (B1 fix):
 * de lo contrario el form arranca con CBU/alias vacíos y el PUT los pisaría con null.
 */
function FilaCuentaBancaria({ cuenta, canEdit, onEditar, onEliminar, onSetPrimary }) {
    // Estado para revelar el CBU/alias completo (se carga desde el endpoint de detalle)
    const [detalleCompleto, setDetalleCompleto] = useState(null);
    const [cargandoDetalle, setCargandoDetalle] = useState(false);
    const [errorDetalle, setErrorDetalle] = useState(null);

    // Estado del botón "Copiar": null | "copiando" | "copiado" | "error"
    const [estadoCopia, setEstadoCopia] = useState(null);

    // settingPrimary: true mientras se está llamando al endpoint set-primary
    const [settingPrimary, setSettingPrimary] = useState(false);

    // cargandoParaEditar: true mientras buscamos el detalle antes de abrir el formulario de edición
    const [cargandoParaEditar, setCargandoParaEditar] = useState(false);

    const cargarDetalle = async () => {
        setCargandoDetalle(true);
        setErrorDetalle(null);
        try {
            // Este endpoint dispara auditoría de lectura en el backend (por diseño)
            const data = await api.get(`/bank-accounts/${cuenta.publicId}`);
            setDetalleCompleto(data);
            return data;
        } catch (error) {
            setErrorDetalle(getApiErrorMessage(error, "No se pudo cargar el dato completo."));
            return null;
        } finally {
            setCargandoDetalle(false);
        }
    };

    const handleVerCompleto = async () => {
        if (detalleCompleto) {
            // Toggle: si ya se cargó, ocultar
            setDetalleCompleto(null);
            setEstadoCopia(null);
            return;
        }
        await cargarDetalle();
    };

    /**
     * Al tocar "Editar": si ya tenemos el detalle completo (porque el usuario
     * usó "Ver completo"), lo reutilizamos directamente. Si no, lo cargamos
     * antes de abrir el form. Esto evita que el form arranque con CBU/alias vacíos
     * y el PUT los pise con null (B1).
     */
    const handleEditarClick = async () => {
        if (detalleCompleto) {
            // Detalle ya disponible: pasar directo al padre para abrir el form
            onEditar(detalleCompleto);
            return;
        }
        // Cargar detalle antes de abrir (el form necesita cbu/alias sin enmascarar)
        setCargandoParaEditar(true);
        setErrorDetalle(null);
        try {
            const data = await api.get(`/bank-accounts/${cuenta.publicId}`);
            setDetalleCompleto(data); // guardamos por si el usuario también quiere "Ver completo"
            onEditar(data);
        } catch (error) {
            setErrorDetalle(getApiErrorMessage(error, "No se pudo cargar los datos para editar."));
        } finally {
            setCargandoParaEditar(false);
        }
    };

    const handleCopiar = async () => {
        // Copiamos el CBU si existe, sino el alias (ambos del detalle completo)
        const textoCopiar = detalleCompleto?.cbu || detalleCompleto?.alias || "";
        if (!textoCopiar) return;

        setEstadoCopia("copiando");
        const exito = await copiarAlPortapapeles(textoCopiar);
        setEstadoCopia(exito ? "copiado" : "error");

        // Vuelve al estado normal después de 2 segundos
        setTimeout(() => setEstadoCopia(null), 2000);
    };

    const handleSetPrimary = async () => {
        setSettingPrimary(true);
        try {
            await api.put(`/bank-accounts/${cuenta.publicId}/set-primary`);
            showSuccess("Cuenta marcada como principal.");
            onSetPrimary();
        } catch (error) {
            // Informamos el error al usuario con un toast (no es error silencioso)
            showError(getApiErrorMessage(error, "No se pudo marcar como cuenta principal. Intentá de nuevo."));
        } finally {
            setSettingPrimary(false);
        }
    };

    // Lo que se muestra de CBU/alias: tapado o completo según el estado
    const cbuVisible = detalleCompleto?.cbu
        ? detalleCompleto.cbu
        : cuenta.cbuMasked
        ? "CBU " + cuenta.cbuMasked
        : null;

    const aliasVisible = detalleCompleto?.alias
        ? detalleCompleto.alias
        : cuenta.aliasMasked
        ? "alias " + cuenta.aliasMasked
        : null;

    // Texto para el botón "Copiar"
    const textoCopiarBoton =
        estadoCopia === "copiado" ? "Copiado ✓" :
        estadoCopia === "error"   ? "Error al copiar" :
        detalleCompleto?.cbu      ? "Copiar CBU" :
        detalleCompleto?.alias    ? "Copiar alias" :
        "Copiar";

    // Etiqueta del tipo de cuenta: el backend retorna int (0=CA, 1=CC).
    // Usamos != null para no omitir el 0 (falsy en JS pero válido como enum).
    const labelTipoCuenta = cuenta.accountType != null
        ? (LABEL_TIPO_CUENTA[cuenta.accountType] ?? null)
        : null;

    return (
        <div
            className={`flex flex-col gap-2 rounded-xl border px-4 py-3 ${
                cuenta.isPrimary
                    ? "border-indigo-200 bg-indigo-50/30 dark:border-indigo-900/40 dark:bg-indigo-950/10"
                    : "border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-800/30"
            }`}
            data-testid={`cuenta-bancaria-${cuenta.publicId}`}
        >
            {/* Fila principal: datos resumidos + acciones */}
            <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="space-y-0.5 min-w-0">
                    {/* Moneda + banco + tipo de cuenta */}
                    <div className="flex items-center gap-2 flex-wrap">
                        <span className="text-[10px] font-black uppercase tracking-wider text-indigo-600 dark:text-indigo-400">
                            {cuenta.currency === "USD" ? "US$" : "$"}
                        </span>
                        {cuenta.isPrimary && (
                            <Star className="h-3.5 w-3.5 fill-amber-400 text-amber-400" aria-label="Cuenta principal" />
                        )}
                        {cuenta.bank && (
                            <span className="text-xs font-semibold text-slate-700 dark:text-slate-300">
                                {cuenta.bank}
                            </span>
                        )}
                        {/* accountType: el int 0 (CajaAhorro) es falsy en JS, por eso != null */}
                        {labelTipoCuenta && (
                            <span className="text-xs text-slate-400">— {labelTipoCuenta}</span>
                        )}
                    </div>

                    {/* Titular */}
                    <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                        {cuenta.holderName}
                    </p>

                    {/* CBU / alias (tapados por defecto, completos si se reveló) */}
                    <div className="flex flex-wrap gap-3 text-xs font-mono text-slate-500 dark:text-slate-400">
                        {cbuVisible && <span data-testid="cbu-display">{cbuVisible}</span>}
                        {aliasVisible && <span data-testid="alias-display">{aliasVisible}</span>}
                    </div>

                    {/* Notas */}
                    {cuenta.notes && (
                        <p className="text-xs text-slate-400 italic">{cuenta.notes}</p>
                    )}

                    {/* Error al cargar detalle o al intentar editar */}
                    {errorDetalle && (
                        <p className="text-xs text-rose-600 dark:text-rose-400 flex items-center gap-1" role="alert">
                            <AlertCircle className="h-3.5 w-3.5 flex-shrink-0" />
                            {errorDetalle}
                        </p>
                    )}
                </div>

                {/* Acciones */}
                <div className="flex items-center gap-1.5 flex-wrap flex-shrink-0">
                    {/* Ver completo: revela CBU/alias completo (dispara auditoría de lectura) */}
                    <button
                        type="button"
                        onClick={handleVerCompleto}
                        disabled={cargandoDetalle || cargandoParaEditar}
                        className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                        data-testid={`btn-ver-completo-${cuenta.publicId}`}
                        aria-label={detalleCompleto ? "Ocultar datos completos" : "Ver CBU/alias completo"}
                    >
                        {cargandoDetalle
                            ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                            : <Eye className="h-3.5 w-3.5" />
                        }
                        {detalleCompleto ? "Ocultar" : "Ver completo"}
                    </button>

                    {/* Copiar: solo cuando el detalle está revelado */}
                    {detalleCompleto && (
                        <button
                            type="button"
                            onClick={handleCopiar}
                            disabled={estadoCopia === "copiando"}
                            className={`inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1.5 text-xs font-medium transition-colors disabled:opacity-50 ${
                                estadoCopia === "copiado"
                                    ? "border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-900/40 dark:bg-emerald-950/20 dark:text-emerald-400"
                                    : estadoCopia === "error"
                                    ? "border-rose-200 bg-rose-50 text-rose-700 dark:border-rose-900/40 dark:text-rose-400"
                                    : "border-slate-200 bg-white text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"
                            }`}
                            data-testid={`btn-copiar-${cuenta.publicId}`}
                            aria-label="Copiar CBU o alias al portapapeles"
                        >
                            {estadoCopia === "copiado"
                                ? <Check className="h-3.5 w-3.5" />
                                : <Copy className="h-3.5 w-3.5" />
                            }
                            {textoCopiarBoton}
                        </button>
                    )}

                    {/* Marcar como principal: solo si tiene permiso de edición y no es ya la principal */}
                    {canEdit && !cuenta.isPrimary && (
                        <button
                            type="button"
                            onClick={handleSetPrimary}
                            disabled={settingPrimary}
                            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:bg-amber-50 hover:text-amber-700 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 transition-colors disabled:opacity-50"
                            data-testid={`btn-set-primary-${cuenta.publicId}`}
                            aria-label="Marcar como cuenta principal"
                        >
                            {settingPrimary
                                ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                : <Star className="h-3.5 w-3.5" />
                            }
                            Principal
                        </button>
                    )}

                    {/* Editar: carga el detalle antes de abrir el form (B1) */}
                    {canEdit && (
                        <button
                            type="button"
                            onClick={handleEditarClick}
                            disabled={cargandoParaEditar || cargandoDetalle}
                            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                            data-testid={`btn-editar-${cuenta.publicId}`}
                            aria-label="Editar cuenta bancaria"
                        >
                            {cargandoParaEditar
                                ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                                : <Pencil className="h-3.5 w-3.5" />
                            }
                            Editar
                        </button>
                    )}

                    {/* Eliminar */}
                    {canEdit && (
                        <button
                            type="button"
                            onClick={() => onEliminar(cuenta)}
                            className="inline-flex items-center gap-1.5 rounded-lg border border-rose-200 bg-white px-2.5 py-1.5 text-xs font-medium text-rose-600 hover:bg-rose-50 dark:border-rose-900/30 dark:bg-slate-800 dark:text-rose-400 dark:hover:bg-rose-950/20 transition-colors"
                            data-testid={`btn-eliminar-${cuenta.publicId}`}
                            aria-label="Eliminar cuenta bancaria"
                        >
                            <Trash2 className="h-3.5 w-3.5" />
                            Eliminar
                        </button>
                    )}
                </div>
            </div>
        </div>
    );
}

// ─── Componente principal: lista completa ─────────────────────────────────────

export function ListaCuentasBancarias({ ownerType, ownerId, title = "Datos bancarios", canEdit = false }) {
    const [cuentas, setCuentas] = useState([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState(null);

    /**
     * editandoDetalle: controla qué ficha inline está abierta.
     *   - null: ninguna ficha abierta
     *   - "nueva": se está creando una cuenta nueva
     *   - object (con publicId): se está editando esa cuenta; el objeto es el detalle COMPLETO
     *     (con cbu/alias en claro) cargado desde el endpoint de detalle antes de abrir el form.
     *
     * Por qué guardamos el objeto completo y no solo el publicId: la ficha de edición necesita
     * el cbu/alias sin enmascarar para pre-llenar el formulario. Si pasáramos el item de la
     * lista (solo tiene cbuMasked/aliasMasked), el form arrancaría con CBU vacío y el PUT
     * pisaría el CBU real con null (B1).
     */
    const [editandoDetalle, setEditandoDetalle] = useState(null);

    // Carga las cuentas del dueño desde el backend
    const cargarCuentas = useCallback(async () => {
        setLoading(true);
        setError(null);
        try {
            // El backend espera ownerType como entero (Agency=0, Customer=1, Supplier=2).
            const ownerTypeInt = OWNER_TYPE[ownerType] ?? 0;
            const params = new URLSearchParams({ ownerType: String(ownerTypeInt), ownerId: String(ownerId) });
            const data = await api.get(`/bank-accounts?${params.toString()}`);
            // El backend puede devolver un array o un objeto { items: [...] }
            setCuentas(Array.isArray(data) ? data : (data?.items ?? []));
        } catch (err) {
            if (!isDatabaseUnavailableError(err)) {
                setError(getApiErrorMessage(err, "No se pudieron cargar las cuentas bancarias."));
            } else {
                setError("El servicio no está disponible en este momento. Intentá de nuevo.");
            }
        } finally {
            setLoading(false);
        }
    }, [ownerType, ownerId]);

    // Carga inicial y cada vez que cambia el dueño
    // eslint-disable-next-line react-hooks/exhaustive-deps
    useEffect(() => { cargarCuentas(); }, [ownerType, ownerId]);

    /**
     * Recibe el detalle COMPLETO (cbu/alias en claro) que FilaCuentaBancaria cargó
     * antes de llamar a onEditar. El toggle cierra el form si ya estaba abierto para esa cuenta.
     */
    const handleEditar = (detalleCompleto) => {
        if (editandoDetalle != null && editandoDetalle !== "nueva" &&
            editandoDetalle.publicId === detalleCompleto.publicId) {
            // Toggle: cerrar el formulario si ya estaba abierto para esta cuenta
            setEditandoDetalle(null);
        } else {
            setEditandoDetalle(detalleCompleto);
        }
    };

    const handleGuardado = () => {
        setEditandoDetalle(null);
        cargarCuentas();
    };

    const handleEliminar = async (cuenta) => {
        // Pedimos confirmación antes de borrar (la acción desactiva la cuenta)
        const confirmo = await showConfirm({
            title: "¿Eliminar esta cuenta?",
            text: `Se desactivará la cuenta de ${cuenta.holderName}${cuenta.bank ? ` en ${cuenta.bank}` : ""}.`,
            confirmText: "Sí, eliminar",
            confirmColor: "red",
        });
        if (!confirmo) return;

        try {
            await api.delete(`/bank-accounts/${cuenta.publicId}`);
            showSuccess("Cuenta eliminada.");
            cargarCuentas();
        } catch (err) {
            setError(getApiErrorMessage(err, "No se pudo eliminar la cuenta."));
        }
    };

    const handleSetPrimary = () => {
        // Recarga la lista para que el ícono de estrella se actualice
        cargarCuentas();
    };

    return (
        <div
            className="overflow-hidden rounded-xl border border-slate-200 bg-card shadow-sm dark:border-slate-700"
            data-testid="lista-cuentas-bancarias"
        >
            {/* Cabecera de la sección */}
            <div className="flex items-center justify-between border-b border-slate-100 dark:border-slate-800 px-5 py-4 bg-slate-50/50 dark:bg-slate-800/20">
                <h3 className="text-sm font-bold text-slate-900 dark:text-white">{title}</h3>
                {canEdit && (
                    <button
                        type="button"
                        onClick={() => setEditandoDetalle((prev) => (prev === "nueva" ? null : "nueva"))}
                        className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-indigo-700 shadow-sm transition-colors"
                        data-testid="btn-agregar-cuenta"
                    >
                        <Plus className="h-3.5 w-3.5" />
                        Agregar cuenta
                    </button>
                )}
            </div>

            <div className="p-4 space-y-3">
                {/* Estado: cargando */}
                {loading && (
                    <div className="flex items-center gap-2 py-6 justify-center text-sm text-muted-foreground">
                        <Loader2 className="h-4 w-4 animate-spin" />
                        Cargando cuentas bancarias…
                    </div>
                )}

                {/* Estado: error */}
                {!loading && error && (
                    <div
                        className="rounded-lg bg-rose-50 border border-rose-200 dark:bg-rose-950/20 dark:border-rose-900/40 px-4 py-3 text-sm text-rose-700 dark:text-rose-300 flex items-center justify-between gap-3"
                        role="alert"
                    >
                        <div className="flex items-center gap-2">
                            <AlertCircle className="h-4 w-4 flex-shrink-0" />
                            {error}
                        </div>
                        <button
                            type="button"
                            onClick={cargarCuentas}
                            className="rounded-lg border border-rose-200 bg-white px-3 py-1.5 text-xs font-medium text-rose-700 hover:bg-rose-50 dark:border-rose-900/40 dark:bg-slate-900 dark:text-rose-400 transition-colors flex-shrink-0"
                        >
                            Reintentar
                        </button>
                    </div>
                )}

                {/* Estado: vacío */}
                {!loading && !error && cuentas.length === 0 && editandoDetalle !== "nueva" && (
                    <p className="py-4 text-center text-sm text-muted-foreground">
                        No hay cuentas bancarias cargadas.
                        {canEdit && <> Us&aacute; &ldquo;Agregar cuenta&rdquo; para sumar una.</>}
                    </p>
                )}

                {/* Lista de cuentas: <ul> con <li> directos (sin <div> intermedio para no romper HTML) */}
                {!loading && cuentas.length > 0 && (
                    <ul className="space-y-2" role="list" aria-label="Cuentas bancarias">
                        {cuentas.map((cuenta) => (
                            <li key={cuenta.publicId} className="space-y-2">
                                {/* FilaCuentaBancaria renderiza un <div> internamente */}
                                <FilaCuentaBancaria
                                    cuenta={cuenta}
                                    canEdit={canEdit}
                                    onEditar={handleEditar}
                                    onEliminar={handleEliminar}
                                    onSetPrimary={handleSetPrimary}
                                />

                                {/* Ficha de edición inline: se despliega debajo de su fila */}
                                {editandoDetalle != null &&
                                    editandoDetalle !== "nueva" &&
                                    editandoDetalle.publicId === cuenta.publicId && (
                                    <FichaCuentaBancariaInline
                                        ownerType={ownerType}
                                        ownerId={ownerId}
                                        cuentaEditar={editandoDetalle}
                                        onGuardado={handleGuardado}
                                        onCancelar={() => setEditandoDetalle(null)}
                                    />
                                )}
                            </li>
                        ))}
                    </ul>
                )}

                {/* Ficha de alta en línea (aparece al final cuando el usuario toca "Agregar") */}
                {editandoDetalle === "nueva" && (
                    <FichaCuentaBancariaInline
                        ownerType={ownerType}
                        ownerId={ownerId}
                        cuentaEditar={null}
                        onGuardado={handleGuardado}
                        onCancelar={() => setEditandoDetalle(null)}
                    />
                )}
            </div>
        </div>
    );
}
