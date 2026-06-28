/**
 * Formulario inline para dar de alta o editar una cuenta bancaria.
 *
 * Se muestra debajo de la lista de cuentas, sin modales (regla UX 2026-06-09).
 * Soporta los tres dueños posibles: Agency, Customer, Supplier.
 *
 * Props:
 *   - ownerType: "Agency" | "Customer" | "Supplier"
 *   - ownerId: string | number  (0 para Agency, publicId para los otros)
 *   - cuentaEditar: object|null  → si no es null, el form arranca pre-cargado
 *   - onGuardado: () => void  → se llama al guardar exitosamente
 *   - onCancelar: () => void  → se llama al cancelar
 */

import { useState, useEffect } from "react";
import { X } from "lucide-react";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { validarFormularioCuenta, construirPayloadCuentaBancaria } from "../lib/bankAccountLogic";

// Tipos de cuenta bancaria: enum del backend (AccountType).
// El backend espera enteros (0 = CajaAhorro, 1 = CuentaCorriente) o null si no se especifica.
// El <select> maneja los values como string; construirPayloadCuentaBancaria convierte a int.
const OPCIONES_TIPO_CUENTA = [
    { value: "", label: "Sin especificar" },
    { value: "0", label: "Caja de Ahorro" },
    { value: "1", label: "Cuenta Corriente" },
];

const MONEDAS = [
    { value: "ARS", label: "$ Pesos (ARS)" },
    { value: "USD", label: "US$ Dólares (USD)" },
];

const formVacio = {
    bank: "",
    accountType: "",
    cbu: "",
    alias: "",
    holderName: "",
    holderTaxId: "",
    currency: "ARS",
    notes: "",
    isPrimary: false,
};

export function FichaCuentaBancariaInline({ ownerType, ownerId, cuentaEditar, onGuardado, onCancelar }) {
    const esEdicion = cuentaEditar != null;

    const [form, setForm] = useState(formVacio);
    const [saving, setSaving] = useState(false);
    const [errorGuardar, setErrorGuardar] = useState(null);

    // Al cambiar la cuenta a editar, pre-cargamos el formulario o lo limpiamos.
    useEffect(() => {
        if (cuentaEditar) {
            setForm({
                bank: cuentaEditar.bank ?? "",
                // accountType viene del backend como int (0|1) o null.
                // Lo convertimos a string para que coincida con el value del <select>.
                accountType: cuentaEditar.accountType != null ? String(cuentaEditar.accountType) : "",
                cbu: cuentaEditar.cbu ?? "",        // vendrá completo si se cargó el detalle
                alias: cuentaEditar.alias ?? "",    // idem
                holderName: cuentaEditar.holderName ?? "",
                holderTaxId: cuentaEditar.holderTaxId ?? "",
                currency: cuentaEditar.currency ?? "ARS",
                notes: cuentaEditar.notes ?? "",
                isPrimary: Boolean(cuentaEditar.isPrimary),
            });
        } else {
            setForm(formVacio);
        }
        setErrorGuardar(null);
    }, [cuentaEditar]);

    const handleChange = (field, value) => {
        setForm((prev) => ({ ...prev, [field]: value }));
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        setErrorGuardar(null);

        // Validación del lado cliente: solo por usabilidad, el backend también valida
        const errorValidacion = validarFormularioCuenta(form);
        if (errorValidacion) {
            setErrorGuardar(errorValidacion);
            return;
        }

        setSaving(true);
        try {
            const payload = construirPayloadCuentaBancaria({ ownerType, ownerId, ...form });

            if (esEdicion) {
                await api.put(`/bank-accounts/${cuentaEditar.publicId}`, payload);
            } else {
                await api.post("/bank-accounts", payload);
            }

            showSuccess(esEdicion ? "Cuenta actualizada." : "Cuenta agregada.");
            onGuardado();
        } catch (error) {
            // La ficha queda abierta con los datos cargados; el usuario puede reintentar.
            setErrorGuardar(
                getApiErrorMessage(error, "No se pudo guardar la cuenta. Revisá la conexión y probá de nuevo.")
            );
        } finally {
            setSaving(false);
        }
    };

    const inputClass =
        "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50";
    const labelClass = "block text-xs font-semibold text-slate-600 dark:text-slate-400 mb-1";

    return (
        <div
            className="rounded-xl border-2 border-indigo-200 bg-indigo-50/30 dark:border-indigo-900/40 dark:bg-indigo-950/10 p-5 space-y-4"
            data-testid="ficha-cuenta-bancaria-inline"
        >
            {/* Cabecera */}
            <div className="flex items-center justify-between">
                <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                    {esEdicion ? "Editar cuenta bancaria" : "Agregar cuenta bancaria"}
                </h4>
                <button
                    type="button"
                    onClick={onCancelar}
                    className="rounded p-1 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
                    aria-label="Cerrar ficha de cuenta bancaria"
                >
                    <X className="h-4 w-4" />
                </button>
            </div>

            <form onSubmit={handleSubmit} className="space-y-4">
                {/* Fila 1: Titular* + CUIT */}
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    <div>
                        <label htmlFor="cuenta-titular" className={labelClass}>Titular *</label>
                        <input
                            id="cuenta-titular"
                            type="text"
                            value={form.holderName}
                            onChange={(e) => handleChange("holderName", e.target.value)}
                            disabled={saving}
                            placeholder="Razón social o nombre completo"
                            className={inputClass}
                            data-testid="cuenta-titular"
                        />
                    </div>
                    <div>
                        <label htmlFor="cuenta-cuit" className={labelClass}>CUIT del titular</label>
                        <input
                            id="cuenta-cuit"
                            type="text"
                            value={form.holderTaxId}
                            onChange={(e) => handleChange("holderTaxId", e.target.value)}
                            disabled={saving}
                            placeholder="XX-XXXXXXXX-X"
                            className={inputClass}
                            data-testid="cuenta-cuit"
                        />
                    </div>
                </div>

                {/* Fila 2: CBU + Alias */}
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    <div>
                        <label htmlFor="cuenta-cbu" className={labelClass}>CBU * (si no tenés alias)</label>
                        <input
                            id="cuenta-cbu"
                            type="text"
                            value={form.cbu}
                            onChange={(e) => handleChange("cbu", e.target.value)}
                            disabled={saving}
                            placeholder="22 dígitos"
                            maxLength={22}
                            className={inputClass}
                            data-testid="cuenta-cbu"
                        />
                    </div>
                    <div>
                        <label htmlFor="cuenta-alias" className={labelClass}>Alias * (si no tenés CBU)</label>
                        <input
                            id="cuenta-alias"
                            type="text"
                            value={form.alias}
                            onChange={(e) => handleChange("alias", e.target.value)}
                            disabled={saving}
                            placeholder="palabra.palabra.palabra"
                            className={inputClass}
                            data-testid="cuenta-alias"
                        />
                    </div>
                </div>

                {/* Aviso cuando ninguno de los dos está cargado */}
                {!form.cbu.trim() && !form.alias.trim() && (
                    <p className="text-xs text-amber-600 dark:text-amber-400" role="alert">
                        Completá al menos el CBU o el alias.
                    </p>
                )}

                {/* Fila 3: Banco + Tipo de cuenta + Moneda* */}
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                    <div>
                        <label htmlFor="cuenta-banco" className={labelClass}>Banco</label>
                        <input
                            id="cuenta-banco"
                            type="text"
                            value={form.bank}
                            onChange={(e) => handleChange("bank", e.target.value)}
                            disabled={saving}
                            placeholder="Ej: Banco Nación"
                            className={inputClass}
                            data-testid="cuenta-banco"
                        />
                    </div>
                    <div>
                        {/* Tipo de cuenta: enum del backend → select con valores int (como string).
                            construirPayloadCuentaBancaria convierte "" → null, "0" → 0, "1" → 1. */}
                        <label htmlFor="cuenta-tipo" className={labelClass}>Tipo de cuenta</label>
                        <select
                            id="cuenta-tipo"
                            value={form.accountType}
                            onChange={(e) => handleChange("accountType", e.target.value)}
                            disabled={saving}
                            className={inputClass}
                            data-testid="cuenta-tipo"
                        >
                            {OPCIONES_TIPO_CUENTA.map((op) => (
                                <option key={op.value} value={op.value}>{op.label}</option>
                            ))}
                        </select>
                    </div>
                    <div>
                        <label htmlFor="cuenta-moneda" className={labelClass}>Moneda *</label>
                        <select
                            id="cuenta-moneda"
                            value={form.currency}
                            onChange={(e) => handleChange("currency", e.target.value)}
                            disabled={saving}
                            className={inputClass}
                            data-testid="cuenta-moneda"
                        >
                            {MONEDAS.map((m) => (
                                <option key={m.value} value={m.value}>{m.label}</option>
                            ))}
                        </select>
                    </div>
                </div>

                {/* Fila 4: Notas + Marcar como principal */}
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    <div>
                        <label htmlFor="cuenta-notas" className={labelClass}>Notas internas</label>
                        <input
                            id="cuenta-notas"
                            type="text"
                            value={form.notes}
                            onChange={(e) => handleChange("notes", e.target.value)}
                            disabled={saving}
                            placeholder="Notas para el equipo…"
                            className={inputClass}
                            data-testid="cuenta-notas"
                        />
                    </div>
                    <div className="flex items-center gap-2 mt-5">
                        <input
                            type="checkbox"
                            id="cuenta-is-primary"
                            checked={form.isPrimary}
                            onChange={(e) => handleChange("isPrimary", e.target.checked)}
                            disabled={saving}
                            className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-600"
                        />
                        <label
                            htmlFor="cuenta-is-primary"
                            className="text-sm text-slate-700 dark:text-slate-300"
                        >
                            Marcar como principal en esta moneda
                        </label>
                    </div>
                </div>

                {/* Error */}
                {errorGuardar && (
                    <div
                        className="rounded-lg bg-rose-50 border border-rose-200 dark:bg-rose-950/20 dark:border-rose-900/40 px-4 py-3 text-xs text-rose-700 dark:text-rose-300"
                        role="alert"
                        data-testid="cuenta-error"
                    >
                        {errorGuardar}
                    </div>
                )}

                {/* Botones */}
                <div className="flex justify-end gap-3 pt-1">
                    <button
                        type="button"
                        onClick={onCancelar}
                        disabled={saving}
                        className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                    >
                        Cancelar
                    </button>
                    <button
                        type="submit"
                        disabled={saving}
                        className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white hover:bg-indigo-700 shadow-sm transition-colors disabled:opacity-50"
                        data-testid="cuenta-guardar"
                    >
                        {saving ? "Guardando…" : esEdicion ? "Guardar cambios" : "Agregar cuenta"}
                    </button>
                </div>
            </form>
        </div>
    );
}
