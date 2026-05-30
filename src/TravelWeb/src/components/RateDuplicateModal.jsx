/**
 * Modal que aparece cuando el admin guarda un servicio a mano y ya existe una tarifa parecida
 * en el tarifario. Le permite al admin elegir entre 3 acciones:
 *   1. Usar la tarifa que ya existe (no crear nada).
 *   2. Actualizar el precio de la tarifa existente con los valores del servicio recien cargado.
 *   3. Crear una tarifa nueva igual (cuando la existente es de otra temporada/condicion).
 *
 * Este componente SOLO se usa dentro del flujo "tarifa aprendida" disparado por ServiceFormModal.
 * No tiene estado propio de seleccion — la seleccion se maneja con useState en el padre
 * (useLearnedRateFlow) para no duplicar estado.
 */
import { useState } from "react";
import { X, CheckCircle2, RefreshCw, PlusCircle } from "lucide-react";

const formatMoney = (value) => {
    const numericValue = Number(value) || 0;
    return `$${numericValue.toLocaleString("es-AR", {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
    })}`;
};

/**
 * Tarjeta visual de una sola coincidencia del duplicate-check.
 * Muestra nombre, precio y score de similitud (si lo tiene).
 * isSelected: resalta con borde azul la tarifa que el admin esta a punto de actualizar.
 */
function RateMatchCard({ match, isSelected, onSelect }) {
    return (
        <button
            type="button"
            onClick={() => onSelect(match)}
            className={`w-full rounded-xl border p-3 text-left transition-all ${
                isSelected
                    ? "border-indigo-400 bg-indigo-50 dark:border-indigo-600 dark:bg-indigo-900/30"
                    : "border-slate-200 hover:border-indigo-200 hover:bg-slate-50 dark:border-slate-700 dark:hover:border-indigo-700 dark:hover:bg-slate-800"
            }`}
        >
            <div className="flex items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                    {/* Nombre del hotel o del producto en el tarifario */}
                    <p className="truncate text-sm font-semibold text-slate-900 dark:text-white">
                        {match.hotelName || match.productName}
                    </p>
                    {/* Si el productName es mas descriptivo que el hotelName (ej. "Sheraton - Doble BB"), lo mostramos */}
                    {match.productName && match.hotelName && match.productName !== match.hotelName && (
                        <p className="truncate text-xs text-slate-500 dark:text-slate-400">
                            {match.productName}
                        </p>
                    )}
                </div>
                <div className="shrink-0 text-right">
                    <p className="text-sm font-bold text-emerald-600 dark:text-emerald-400">
                        VTA: {formatMoney(match.salePrice)}
                    </p>
                    <p className="text-xs text-slate-500 dark:text-slate-400">
                        NET: {formatMoney(match.netCost)}
                        {/* Mostrar moneda solo si no es ARS (el default) */}
                        {match.currency && match.currency !== "ARS" ? ` ${match.currency}` : ""}
                    </p>
                </div>
            </div>

            {/* Score de similitud fuzzy — solo aparece en coincidencias aproximadas, no en exactMatch */}
            {match.score != null && (
                <p className="mt-1 text-[10px] font-semibold uppercase tracking-wider text-slate-400">
                    Similitud: {Math.round(match.score * 100)}%
                </p>
            )}
        </button>
    );
}

/**
 * Props del modal:
 *   isOpen        — controla visibilidad
 *   onClose       — cierra sin hacer nada (equivale a "usar esa" — no toca el tarifario)
 *   onUpdate      — fn(selectedMatch): actualiza precio de la tarifa seleccionada
 *   onCreate      — fn(): crea una tarifa nueva con los datos del servicio
 *   exactMatch    — objeto { publicId, productName, hotelName, salePrice, netCost, currency } o null
 *   fuzzyMatches  — array de objetos iguales pero con campo "score" (0 a 1)
 *   isLoading     — muestra spinner mientras se ejecuta la accion elegida
 *   newNetCost    — numero: costo neto del servicio recien cargado (para comparar)
 *   newSalePrice  — numero: precio venta del servicio recien cargado
 */
export default function RateDuplicateModal({
    isOpen,
    onClose,
    onUpdate,
    onCreate,
    exactMatch,
    fuzzyMatches,
    isLoading,
    newNetCost,
    newSalePrice,
}) {
    // La tarifa seleccionada para actualizar: por default el exactMatch o el primer fuzzy
    // Si el modal se reabre con datos distintos esto se resetea porque el componente se desmonta/remonta.
    const [selectedMatch, setSelectedMatch] = useState(
        () => exactMatch || (fuzzyMatches && fuzzyMatches.length > 0 ? fuzzyMatches[0] : null)
    );

    if (!isOpen) return null;

    // Armamos la lista completa para mostrar:
    // primero el exactMatch (si existe), despues los fuzzy (excluyendo el que ya esta en exactMatch)
    const allMatches = [
        ...(exactMatch ? [{ ...exactMatch, _isExact: true }] : []),
        ...(fuzzyMatches || []).filter(
            (fuzzy) => !exactMatch || fuzzy.publicId !== exactMatch.publicId
        ),
    ];

    return (
        // z-[110] para quedar POR ENCIMA del ServiceFormModal que ya usa z-50
        <div className="fixed inset-0 z-[110] flex items-center justify-center bg-black/60 p-4 backdrop-blur-sm">
            <div className="w-full max-w-md overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900">

                {/* Header */}
                <div className="flex items-center justify-between border-b border-slate-200 bg-amber-50 px-5 py-4 dark:border-slate-700 dark:bg-amber-950/20">
                    <div>
                        <h3 className="text-base font-bold text-slate-900 dark:text-white">
                            Ya existe una tarifa parecida
                        </h3>
                        <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
                            Elegí qué hacer con el tarifario
                        </p>
                    </div>
                    {/* Solo mostramos el boton cerrar cuando no hay una accion en curso */}
                    {!isLoading && (
                        <button
                            type="button"
                            onClick={onClose}
                            className="rounded-lg p-1.5 text-slate-400 hover:bg-white/80 hover:text-slate-600"
                            aria-label="Cerrar"
                        >
                            <X className="h-5 w-5" />
                        </button>
                    )}
                </div>

                <div className="space-y-4 p-5">
                    {/* Resumen del servicio que se acaba de guardar, para comparar contra el tarifario */}
                    <div className="rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-700 dark:bg-slate-800">
                        <p className="mb-1 text-[10px] font-bold uppercase tracking-wider text-slate-400">
                            Servicio recien cargado
                        </p>
                        <div className="flex gap-4 text-sm font-semibold">
                            <span className="text-slate-700 dark:text-slate-300">
                                VTA:{" "}
                                <span className="text-emerald-600 dark:text-emerald-400">
                                    {formatMoney(newSalePrice)}
                                </span>
                            </span>
                            <span className="text-slate-700 dark:text-slate-300">
                                NET:{" "}
                                <span className="text-slate-500">{formatMoney(newNetCost)}</span>
                            </span>
                        </div>
                    </div>

                    {/* Lista de coincidencias: el admin toca una para seleccionar cual actualizar */}
                    <div>
                        <p className="mb-2 text-xs font-semibold text-slate-600 dark:text-slate-400">
                            {exactMatch ? "Coincidencia exacta encontrada:" : "Coincidencias aproximadas:"}
                        </p>
                        <div className="max-h-48 space-y-2 overflow-y-auto">
                            {allMatches.map((match) => (
                                <RateMatchCard
                                    key={match.publicId}
                                    match={match}
                                    isSelected={selectedMatch?.publicId === match.publicId}
                                    onSelect={setSelectedMatch}
                                />
                            ))}
                        </div>
                    </div>

                    {/* Los 3 botones de accion */}
                    <div className="space-y-2 border-t border-slate-100 pt-4 dark:border-slate-800">

                        {/* Opcion 1: no hacer nada con el tarifario */}
                        <button
                            type="button"
                            data-testid="rate-dup-use"
                            onClick={onClose}
                            disabled={isLoading}
                            className="flex w-full items-center gap-3 rounded-xl border border-slate-200 bg-white px-4 py-3 text-left text-sm font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"
                        >
                            <CheckCircle2 className="h-4 w-4 shrink-0 text-slate-400" />
                            <div>
                                <div>Usar esa</div>
                                <div className="text-xs font-normal text-slate-400">
                                    No toca el tarifario
                                </div>
                            </div>
                        </button>

                        {/* Opcion 2: actualizar precio de la tarifa seleccionada con PUT /rates/{id} */}
                        <button
                            type="button"
                            data-testid="rate-dup-update"
                            onClick={() => onUpdate(selectedMatch)}
                            disabled={isLoading || !selectedMatch}
                            className="flex w-full items-center gap-3 rounded-xl border border-indigo-200 bg-indigo-50 px-4 py-3 text-left text-sm font-semibold text-indigo-700 transition hover:bg-indigo-100 disabled:opacity-50 dark:border-indigo-800 dark:bg-indigo-900/20 dark:text-indigo-300 dark:hover:bg-indigo-900/40"
                        >
                            {isLoading ? (
                                <RefreshCw className="h-4 w-4 shrink-0 animate-spin" />
                            ) : (
                                <RefreshCw className="h-4 w-4 shrink-0" />
                            )}
                            <div>
                                <div>Actualizar precio</div>
                                <div className="text-xs font-normal text-indigo-400">
                                    Pisa NET/VTA de la tarifa seleccionada
                                </div>
                            </div>
                        </button>

                        {/* Opcion 3: crear tarifa nueva con POST /rates */}
                        <button
                            type="button"
                            data-testid="rate-dup-create"
                            onClick={onCreate}
                            disabled={isLoading}
                            className="flex w-full items-center gap-3 rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-left text-sm font-semibold text-emerald-700 transition hover:bg-emerald-100 disabled:opacity-50 dark:border-emerald-800 dark:bg-emerald-900/20 dark:text-emerald-300 dark:hover:bg-emerald-900/40"
                        >
                            <PlusCircle className="h-4 w-4 shrink-0" />
                            <div>
                                <div>Crear nueva igual</div>
                                <div className="text-xs font-normal text-emerald-500">
                                    Agrega al tarifario con los valores del servicio
                                </div>
                            </div>
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
}
