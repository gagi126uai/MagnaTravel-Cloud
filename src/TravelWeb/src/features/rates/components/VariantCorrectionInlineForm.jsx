/**
 * Sub-ficha de "Corregir" una habitación/cabina/vehículo, dentro de la ficha del
 * producto (spec firmada 2026-08-07, §7 / M-18). Los mismos desplegables que ya usan
 * las fichas de servicio y el alta a mano — se reusan a propósito, para que "cómo se
 * escribe una habitación" se vea SIEMPRE igual en toda la app.
 *
 * Solo corrige TEXTOS: nunca toca un importe (eso lo hace el motor al guardar, si la
 * corrección deja dos habitaciones iguales, se juntan solas y queda el precio más nuevo).
 */
import { useState } from "react";
import { FreeTextWithMemoryField } from "./FreeTextWithMemoryField";
import { buildInitialVariantCorrectionFields } from "../lib/ratesLearnedProductsLogic";
import { Button } from "../../../components/ui/button";

const INPUT_CLASS = "w-full rounded-[10px] border border-slate-200 bg-white px-3 py-2 text-sm focus:border-primary focus:outline-none focus:ring-1 focus:ring-primary dark:border-slate-700 dark:bg-slate-900 dark:text-white";
const LABEL_CLASS = "block text-xs font-semibold text-slate-600 dark:text-slate-400 mb-1";

export function VariantCorrectionInlineForm({ serviceType, variant, onCancel, onSave }) {
    // Precarga con las PIEZAS reales de la variante que se está corrigiendo (fix ronda 2
    // de review, P-21): el backend ya manda roomType/mealPlan/roomCategory/cabinClass/
    // vehicleType en LearnedProductVariantDto, con los mismos valores exactos de estos
    // desplegables. Antes arrancaba siempre en "Doble/Desayuno" sin mirar la variante
    // real, así que corregir una TRIPLE la reclasificaba a DOBLE sin que nadie lo pidiera.
    const initialFields = buildInitialVariantCorrectionFields(variant);
    const [roomType, setRoomType] = useState(initialFields.roomType);
    const [mealPlan, setMealPlan] = useState(initialFields.mealPlan);
    const [roomCategory, setRoomCategory] = useState(initialFields.roomCategory);
    const [cabinClass, setCabinClass] = useState(initialFields.cabinClass);
    const [vehicleType, setVehicleType] = useState(initialFields.vehicleType);
    const [guardando, setGuardando] = useState(false);
    const [error, setError] = useState(null);

    const handleGuardar = async () => {
        setGuardando(true);
        setError(null);
        try {
            await onSave({ roomType, mealPlan, roomCategory, cabinClass, vehicleType });
        } catch (err) {
            setError(err.payload?.message || "No se pudo corregir. Revisá la conexión y probá de nuevo.");
        } finally {
            setGuardando(false);
        }
    };

    return (
        <div className="mt-2 rounded-[14px] border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900 p-3">
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
                {serviceType === "Hotel" && (
                    <>
                        <div>
                            <label className={LABEL_CLASS} htmlFor="variant-correction-meal-plan">Régimen</label>
                            <select
                                id="variant-correction-meal-plan"
                                className={INPUT_CLASS}
                                value={mealPlan}
                                onChange={(event) => setMealPlan(event.target.value)}
                                aria-label="Régimen de la habitación"
                            >
                                <option value="Solo Alojamiento">Solo alojamiento</option>
                                <option value="Desayuno">Desayuno</option>
                                <option value="Media Pension">Media pensión</option>
                                <option value="Pension Completa">Pensión completa</option>
                                <option value="All Inclusive">All inclusive</option>
                            </select>
                        </div>
                        <div>
                            <label className={LABEL_CLASS} htmlFor="variant-correction-room-type">Tipo de habitación</label>
                            <select
                                id="variant-correction-room-type"
                                className={INPUT_CLASS}
                                value={roomType}
                                onChange={(event) => setRoomType(event.target.value)}
                                aria-label="Tipo de habitación"
                            >
                                <option value="Single">Single</option>
                                <option value="Doble">Doble</option>
                                <option value="Twin">Twin</option>
                                <option value="Triple">Triple</option>
                                <option value="Cuadruple">Cuádruple</option>
                                <option value="Familiar">Familiar</option>
                                <option value="Suite">Suite</option>
                            </select>
                        </div>
                        <FreeTextWithMemoryField
                            serviceType="Hotel"
                            label="Categoría"
                            placeholder="Ej: Superior"
                            value={roomCategory}
                            onChange={setRoomCategory}
                        />
                    </>
                )}
                {serviceType === "Aereo" && (
                    <div>
                        <label className={LABEL_CLASS} htmlFor="variant-correction-cabin-class">Cabina</label>
                        <select
                            id="variant-correction-cabin-class"
                            className={INPUT_CLASS}
                            value={cabinClass}
                            onChange={(event) => setCabinClass(event.target.value)}
                            aria-label="Cabina del vuelo"
                        >
                            <option value="">Sin especificar</option>
                            <option value="Economy">Economy</option>
                            <option value="Premium">Premium Economy</option>
                            <option value="Business">Business</option>
                            <option value="First">Primera Clase</option>
                        </select>
                    </div>
                )}
                {serviceType === "Traslado" && (
                    <FreeTextWithMemoryField
                        serviceType="Traslado"
                        label="Vehículo"
                        placeholder="Van, sedán, microbús..."
                        value={vehicleType}
                        onChange={setVehicleType}
                    />
                )}
            </div>

            {error && <p className="mt-2 text-xs font-semibold text-rose-600">{error}</p>}

            <div className="mt-2 flex justify-end gap-2">
                <Button type="button" variant="outline" size="sm" onClick={onCancel}>
                    Cancelar
                </Button>
                <Button type="button" size="sm" onClick={handleGuardar} disabled={guardando}>
                    {guardando ? "Guardando..." : "Guardar"}
                </Button>
            </div>
        </div>
    );
}
