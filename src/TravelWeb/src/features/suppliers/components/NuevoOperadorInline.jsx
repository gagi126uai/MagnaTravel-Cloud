/**
 * Ficha de ALTA de un operador nuevo, desplegada en línea dentro de la página.
 *
 * Reemplaza la apertura del SupplierFormModal para crear operadores nuevos.
 * Sin ventana flotante: se muestra dentro de la página, debajo del botón "Nuevo operador"
 * (decisión del dueño 2026-06-28: "que se abra DENTRO de la página, sin ventana encima").
 *
 * Campos obligatorios para guardar:
 *   - Razón social
 *   - Moneda por defecto (ARS por defecto, opción USD)
 *   - CUIT (salvo que el toggle "Datos fiscales pendientes" esté activo)
 *   - Condición fiscal (ídem)
 *
 * Escape "Datos fiscales pendientes": al tildarlo, CUIT y condición dejan de ser
 * obligatorios. El operador queda como fiscalmente incompleto; el backend bloquea
 * el primer pago hasta que se completen esos datos.
 *
 * Campos secundarios detrás de "Más detalles" (cerrado por defecto):
 *   Contacto · Teléfono · Email · Dirección
 *
 * Si el guardado falla: el formulario queda con todos los datos cargados intactos
 * y se muestra el mensaje de error del backend (vía getApiErrorMessage).
 *
 * Props:
 *   - onCreado: () => void — callback al crear exitosamente (para refrescar la lista)
 *   - onCancelar: () => void — callback al cerrar sin guardar
 */

import { useState } from "react";
import { Building2, ChevronDown, ChevronRight, X } from "lucide-react";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import {
    FORM_INICIAL,
    TAX_CONDITION_OPTIONS,
    CURRENCY_OPTIONS,
    validarNuevoOperador,
    construirPayloadNuevoOperador,
} from "../lib/nuevoOperadorLogic";

// Clases reutilizadas para los inputs del formulario.
// Centralizado acá para que sea fácil de actualizar si el sistema de diseño cambia.
const INPUT_CLASS =
    "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-500 dark:bg-slate-950 dark:border-slate-700 dark:text-white disabled:opacity-50";
const LABEL_CLASS = "text-xs font-semibold text-slate-600 dark:text-slate-400";

export function NuevoOperadorInline({ onCreado, onCancelar }) {
    const [formData, setFormData] = useState(FORM_INICIAL);

    // Escape fiscal: cuando está activo, CUIT y condición fiscal no frenan el guardado.
    const [fiscalDataPending, setFiscalDataPending] = useState(false);

    // Sección "Más detalles" (Contacto · Teléfono · Email · Dirección) cerrada por defecto.
    const [mostrarMasDetalles, setMostrarMasDetalles] = useState(false);

    const [saving, setSaving] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    // Actualiza un campo del formulario sin pisar el resto del estado.
    function actualizarCampo(campo, valor) {
        setFormData((prev) => ({ ...prev, [campo]: valor }));
    }

    async function handleSubmit(event) {
        event.preventDefault();

        // Validación en el front: no molestamos al backend si hay un error obvio.
        const mensajeError = validarNuevoOperador(formData, fiscalDataPending);
        if (mensajeError) {
            setErrorGuardar(mensajeError);
            return;
        }

        setSaving(true);
        setErrorGuardar(null);

        try {
            const payload = construirPayloadNuevoOperador(formData);
            await api.post("/suppliers", payload);
            showSuccess("Operador creado.");
            // Notificamos al padre para que refresque la lista.
            onCreado();
        } catch (error) {
            // El formulario queda abierto con los datos intactos.
            // Mostramos el mensaje amigable del backend (ej: "Moneda no soportada: XYZ").
            setErrorGuardar(
                getApiErrorMessage(error, "No se pudo crear el operador. Revisá la conexión y probá de nuevo.")
            );
        } finally {
            setSaving(false);
        }
    }

    return (
        <div
            className="rounded-xl border-2 border-indigo-200 bg-indigo-50/30 dark:border-indigo-900/40 dark:bg-indigo-950/10 p-5 space-y-4"
            data-testid="nuevo-operador-inline"
        >
            {/* Cabecera de la ficha */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <Building2 className="w-4 h-4 text-indigo-600 dark:text-indigo-400" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                        Nuevo operador
                    </h4>
                </div>
                <button
                    type="button"
                    onClick={onCancelar}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1"
                    aria-label="Cancelar alta de operador"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            <form onSubmit={handleSubmit} className="space-y-4" noValidate>

                {/* ── Razón social (siempre visible, obligatoria) ────────────────── */}
                <div className="space-y-1">
                    <label htmlFor="nuevo-op-name" className={LABEL_CLASS}>
                        Razón social *
                    </label>
                    <input
                        id="nuevo-op-name"
                        type="text"
                        value={formData.name}
                        onChange={(e) => actualizarCampo("name", e.target.value)}
                        placeholder="Ej: Despegar Argentina S.A."
                        disabled={saving}
                        className={INPUT_CLASS}
                        data-testid="nuevo-op-name"
                        autoFocus
                    />
                </div>

                {/* ── Moneda + CUIT en la misma fila ──────────────────────────────── */}
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    <div className="space-y-1">
                        <label htmlFor="nuevo-op-currency" className={LABEL_CLASS}>
                            Moneda por defecto *
                        </label>
                        {/* Moneda por defecto: ARS (Pesos) por decisión del dueño (P9=A).
                            Impacta en qué extracto se crea primero para este operador. */}
                        <select
                            id="nuevo-op-currency"
                            value={formData.defaultCurrency}
                            onChange={(e) => actualizarCampo("defaultCurrency", e.target.value)}
                            disabled={saving}
                            className={INPUT_CLASS}
                            data-testid="nuevo-op-currency"
                        >
                            {CURRENCY_OPTIONS.map((opt) => (
                                <option key={opt.value} value={opt.value}>
                                    {opt.label}
                                </option>
                            ))}
                        </select>
                    </div>

                    <div className="space-y-1">
                        <label htmlFor="nuevo-op-taxid" className={LABEL_CLASS}>
                            {/* El asterisco se apaga visualmente cuando el escape está activo,
                                pero el campo sigue habilitado (el usuario puede escribir igual). */}
                            CUIT {!fiscalDataPending && "*"}
                        </label>
                        <input
                            id="nuevo-op-taxid"
                            type="text"
                            value={formData.taxId}
                            onChange={(e) => actualizarCampo("taxId", e.target.value)}
                            placeholder="30-12345678-9"
                            disabled={saving}
                            className={INPUT_CLASS}
                            data-testid="nuevo-op-taxid"
                        />
                    </div>
                </div>

                {/* ── Condición fiscal ─────────────────────────────────────────────── */}
                <div className="space-y-1">
                    <label htmlFor="nuevo-op-taxcondition" className={LABEL_CLASS}>
                        Condición fiscal {!fiscalDataPending && "*"}
                    </label>
                    <select
                        id="nuevo-op-taxcondition"
                        value={formData.taxCondition}
                        onChange={(e) => actualizarCampo("taxCondition", e.target.value)}
                        disabled={saving}
                        className={INPUT_CLASS}
                        data-testid="nuevo-op-taxcondition"
                    >
                        <option value="">Seleccionar…</option>
                        {TAX_CONDITION_OPTIONS.map((opt) => (
                            <option key={opt.value} value={opt.value}>
                                {opt.label}
                            </option>
                        ))}
                    </select>
                </div>

                {/* ── Toggle "Datos fiscales pendientes" ──────────────────────────── */}
                {/* Cuando está activo, CUIT y condición dejan de ser obligatorios.
                    El freno duro al pago lo pone el backend; esta pantalla solo informa. */}
                <label className="flex items-start gap-2 cursor-pointer select-none">
                    <input
                        type="checkbox"
                        checked={fiscalDataPending}
                        onChange={(e) => setFiscalDataPending(e.target.checked)}
                        disabled={saving}
                        className="mt-0.5 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                        data-testid="nuevo-op-fiscal-pending"
                    />
                    <div>
                        <span className="text-sm font-medium text-slate-700 dark:text-slate-300">
                            Datos fiscales pendientes — los completo más adelante
                        </span>
                        <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
                            No se le puede pagar hasta completarlos.
                        </p>
                    </div>
                </label>

                {/* ── "Más detalles" colapsado ─────────────────────────────────────── */}
                <div>
                    <button
                        type="button"
                        onClick={() => setMostrarMasDetalles((prev) => !prev)}
                        className="flex items-center gap-1 text-sm font-medium text-indigo-600 hover:text-indigo-800 dark:text-indigo-400 dark:hover:text-indigo-200"
                        aria-expanded={mostrarMasDetalles}
                        data-testid="nuevo-op-mas-detalles-toggle"
                    >
                        {mostrarMasDetalles ? (
                            <ChevronDown className="w-4 h-4" />
                        ) : (
                            <ChevronRight className="w-4 h-4" />
                        )}
                        Más detalles
                    </button>

                    {mostrarMasDetalles && (
                        <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                            <div className="space-y-1">
                                <label htmlFor="nuevo-op-contact" className={LABEL_CLASS}>
                                    Contacto
                                </label>
                                <input
                                    id="nuevo-op-contact"
                                    type="text"
                                    value={formData.contactName}
                                    onChange={(e) => actualizarCampo("contactName", e.target.value)}
                                    placeholder="Nombre de contacto"
                                    disabled={saving}
                                    className={INPUT_CLASS}
                                    data-testid="nuevo-op-contact"
                                />
                            </div>

                            <div className="space-y-1">
                                <label htmlFor="nuevo-op-phone" className={LABEL_CLASS}>
                                    Teléfono
                                </label>
                                <input
                                    id="nuevo-op-phone"
                                    type="tel"
                                    value={formData.phone}
                                    onChange={(e) => actualizarCampo("phone", e.target.value)}
                                    placeholder="+54 11 …"
                                    disabled={saving}
                                    className={INPUT_CLASS}
                                    data-testid="nuevo-op-phone"
                                />
                            </div>

                            <div className="space-y-1">
                                <label htmlFor="nuevo-op-email" className={LABEL_CLASS}>
                                    Email
                                </label>
                                <input
                                    id="nuevo-op-email"
                                    type="email"
                                    value={formData.email}
                                    onChange={(e) => actualizarCampo("email", e.target.value)}
                                    placeholder="contacto@operador.com"
                                    disabled={saving}
                                    className={INPUT_CLASS}
                                    data-testid="nuevo-op-email"
                                />
                            </div>

                            <div className="space-y-1">
                                <label htmlFor="nuevo-op-address" className={LABEL_CLASS}>
                                    Dirección
                                </label>
                                <input
                                    id="nuevo-op-address"
                                    type="text"
                                    value={formData.address}
                                    onChange={(e) => actualizarCampo("address", e.target.value)}
                                    placeholder="Lavalle 123, CABA"
                                    disabled={saving}
                                    className={INPUT_CLASS}
                                    data-testid="nuevo-op-address"
                                />
                            </div>
                        </div>
                    )}
                </div>

                {/* ── Error de guardado: mensaje del backend o de validación ────────── */}
                {errorGuardar && (
                    <div
                        className="rounded-lg bg-rose-50 border border-rose-200 dark:bg-rose-950/20 dark:border-rose-900/40 px-4 py-2 text-sm text-rose-700 dark:text-rose-300"
                        role="alert"
                        data-testid="nuevo-op-error"
                    >
                        {errorGuardar}
                    </div>
                )}

                {/* ── Botones ───────────────────────────────────────────────────────── */}
                <div className="flex justify-end gap-3 pt-2 border-t border-slate-100 dark:border-slate-800">
                    <button
                        type="button"
                        onClick={onCancelar}
                        disabled={saving}
                        className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 disabled:opacity-50 transition-colors"
                        data-testid="nuevo-op-cancelar"
                    >
                        Cancelar
                    </button>
                    <button
                        type="submit"
                        disabled={saving}
                        className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 shadow-sm shadow-indigo-500/20 disabled:opacity-50 transition-all"
                        data-testid="nuevo-op-submit"
                    >
                        {saving ? "Creando…" : "Crear operador"}
                    </button>
                </div>
            </form>
        </div>
    );
}
