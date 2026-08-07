/**
 * Fichita de alta rápida "+ Agregar producto" del Tarifario nuevo (spec firmada
 * 2026-08-06, §2.3 / contrato B). Reemplaza al formulario largo como puerta de
 * entrada: solo pide lo mínimo (tipo, nombre, ciudad si es hotel, operador y precio).
 *
 * El freno de repetidos (P7 "evitar repetidos a toda costa") es 100% del servidor:
 * si POST /rates/simple devuelve 409, mostramos el cartel ámbar de confirmación
 * (patrón único 2026-07-22) con los dos caminos que dejó firmados Gastón.
 */
import { useState } from "react";
import { api } from "../../../api";
import { showConfirmWithAlternative, showSuccess } from "../../../alerts";
import { getPublicId } from "../../../lib/publicIds";
import {
    buildCreateSimpleProductPayload,
    validateProductNameAndCity,
    resolveSimilarProductDialogDecision,
    SIMILAR_PRODUCT_DIALOG_DECISION,
} from "../lib/ratesLearnedProductsLogic";

const SERVICE_TYPES = [
    { value: "Hotel", label: "Hotel" },
    { value: "Aereo", label: "Aéreo" },
    { value: "Traslado", label: "Traslado" },
    { value: "Paquete", label: "Paquete" },
    { value: "Asistencia", label: "Asistencia" },
    { value: "Excursion", label: "Excursión" },
    { value: "Otro", label: "Otro" },
];

const INPUT_CLASS = "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500 dark:border-slate-700 dark:bg-slate-900 dark:text-white";
const LABEL_CLASS = "block text-xs font-semibold text-slate-600 dark:text-slate-400 mb-1";

const FORM_INICIAL = { serviceType: "Hotel", name: "", city: "", supplierId: "", price: "", currency: "ARS" };

export function AddProductInlineForm({ suppliers, onCancel, onCreated, onExistingChosen, onOpenCargaCompleta }) {
    const [form, setForm] = useState(FORM_INICIAL);
    const [errors, setErrors] = useState({});
    const [saving, setSaving] = useState(false);
    const [saveError, setSaveError] = useState(null);

    const esHotel = form.serviceType === "Hotel";

    // Manda el alta al servidor. `createAnyway` solo va en true en el reintento,
    // después de que el usuario elige "Crear uno nuevo" en el cartel de repetidos.
    const enviarCreacion = async (createAnyway) => {
        setSaving(true);
        setSaveError(null);
        try {
            const payload = buildCreateSimpleProductPayload(form, { createAnyway });
            const creado = await api.post("/rates/simple", payload);
            showSuccess("Producto guardado.");
            onCreated(creado);
        } catch (err) {
            if (err.status === 409 && err.payload?.reason === "ProductoParecido") {
                const parecidos = err.payload.similarProducts || [];
                const primerParecido = parecidos[0];
                // Fix 2026-08-07: acá vivía el bug de "cualquier descarte crea el duplicado".
                // Ahora el cartel tiene DOS acciones nombradas (ninguna es "Cancelar") y
                // CUALQUIER otro descarte (ESC/X/click afuera) es un no-op — la decisión la
                // interpreta resolveSimilarProductDialogDecision, no un booleano ambiguo.
                const dialogResult = await showConfirmWithAlternative({
                    title: "Confirmá antes de seguir",
                    text: err.payload.message,
                    confirmText: primerParecido ? `Usar "${primerParecido.name}"` : "Usar el existente",
                    denyText: "Crear uno nuevo igual",
                    confirmColor: "amber",
                });
                const decision = resolveSimilarProductDialogDecision(dialogResult);
                if (decision === SIMILAR_PRODUCT_DIALOG_DECISION.UseExisting) {
                    onExistingChosen?.(primerParecido?.name || form.name);
                } else if (decision === SIMILAR_PRODUCT_DIALOG_DECISION.CreateNewAnyway) {
                    await enviarCreacion(true);
                }
                // Dismissed (ESC/X/click afuera): no-op a propósito — la fichita queda
                // abierta tal cual, sin crear nada y sin cerrar nada.
                return;
            }
            // Ronda 2 (2026-06-06): la ficha queda abierta con todo lo tipeado intacto.
            // Fix 2026-08-07: si el motor mandó un mensaje de negocio (ej. "No encontramos
            // ese operador."), se muestra tal cual — el genérico de conexión queda solo
            // para cuando el servidor no dijo nada (error de red, 500 sin payload).
            setSaveError(err.payload?.message || "No se pudo guardar. Revisá la conexión y probá de nuevo.");
        } finally {
            setSaving(false);
        }
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        const validacion = validateProductNameAndCity(form);
        setErrors(validacion);
        if (Object.keys(validacion).length > 0) return;
        await enviarCreacion(false);
    };

    return (
        <div
            className="mb-4 rounded-xl border border-indigo-200 bg-indigo-50/40 p-4 dark:border-indigo-900/40 dark:bg-indigo-950/10"
            data-testid="add-product-inline-form"
        >
            <form onSubmit={handleSubmit} className="space-y-3">
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                    <div>
                        <label className={LABEL_CLASS} htmlFor="add-product-type">Tipo</label>
                        <select
                            id="add-product-type"
                            className={INPUT_CLASS}
                            value={form.serviceType}
                            onChange={(event) => setForm((prev) => ({ ...prev, serviceType: event.target.value }))}
                        >
                            {SERVICE_TYPES.map((tipo) => <option key={tipo.value} value={tipo.value}>{tipo.label}</option>)}
                        </select>
                    </div>
                    <div className="col-span-2 sm:col-span-1">
                        <label className={LABEL_CLASS} htmlFor="add-product-name">Nombre *</label>
                        <input
                            id="add-product-name"
                            className={INPUT_CLASS}
                            value={form.name}
                            onChange={(event) => setForm((prev) => ({ ...prev, name: event.target.value }))}
                            aria-invalid={Boolean(errors.name)}
                        />
                        {errors.name && <p className="mt-1 text-xs text-rose-600">{errors.name}</p>}
                    </div>
                    {esHotel && (
                        <div>
                            <label className={LABEL_CLASS} htmlFor="add-product-city">Ciudad *</label>
                            <input
                                id="add-product-city"
                                className={INPUT_CLASS}
                                value={form.city}
                                onChange={(event) => setForm((prev) => ({ ...prev, city: event.target.value }))}
                                aria-invalid={Boolean(errors.city)}
                            />
                            {errors.city && <p className="mt-1 text-xs text-rose-600">{errors.city}</p>}
                        </div>
                    )}
                    <div>
                        <label className={LABEL_CLASS} htmlFor="add-product-supplier">Operador</label>
                        <select
                            id="add-product-supplier"
                            className={INPUT_CLASS}
                            value={form.supplierId}
                            onChange={(event) => setForm((prev) => ({ ...prev, supplierId: event.target.value }))}
                        >
                            <option value="">Sin operador</option>
                            {suppliers.map((supplier) => (
                                <option key={getPublicId(supplier)} value={getPublicId(supplier)}>{supplier.name}</option>
                            ))}
                        </select>
                    </div>
                </div>

                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                    <div>
                        <label className={LABEL_CLASS} htmlFor="add-product-currency">Moneda</label>
                        <select
                            id="add-product-currency"
                            className={INPUT_CLASS}
                            value={form.currency}
                            onChange={(event) => setForm((prev) => ({ ...prev, currency: event.target.value }))}
                        >
                            <option value="ARS">ARS (pesos)</option>
                            <option value="USD">USD (dólares)</option>
                        </select>
                    </div>
                    <div>
                        <label className={LABEL_CLASS} htmlFor="add-product-price">
                            Precio{esHotel ? " (por noche)" : ""}
                        </label>
                        <input
                            id="add-product-price"
                            type="number"
                            min={0}
                            step="0.01"
                            className={INPUT_CLASS}
                            value={form.price}
                            onChange={(event) => setForm((prev) => ({ ...prev, price: event.target.value }))}
                        />
                    </div>
                </div>

                {saveError && (
                    <p className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-semibold text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">
                        {saveError}
                    </p>
                )}

                <div className="flex items-center justify-between pt-1">
                    <button
                        type="button"
                        onClick={() => onOpenCargaCompleta(form)}
                        className="text-xs font-semibold text-slate-500 hover:text-indigo-600 dark:text-slate-400"
                    >
                        Carga completa
                    </button>
                    <div className="flex gap-2">
                        <button
                            type="button"
                            onClick={onCancel}
                            disabled={saving}
                            className="rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                        >
                            Cancelar
                        </button>
                        <button
                            type="submit"
                            disabled={saving}
                            className="rounded-lg bg-indigo-600 px-4 py-1.5 text-xs font-semibold text-white hover:bg-indigo-700 disabled:opacity-60"
                        >
                            {saving ? "Guardando..." : "Guardar"}
                        </button>
                    </div>
                </div>
            </form>
        </div>
    );
}
