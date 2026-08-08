/**
 * Un producto del Tarifario nuevo, agrupado por HABITACIÓN y, adentro, por operador
 * (spec firmada 2026-08-07, §5.1 / V5=A / V6=A / V7=A). El nombre del producto se
 * escribe UNA sola vez (primer renglón); la etiqueta de la habitación se repite solo al
 * cambiar de grupo — el resto de los renglones solo agregan operador/precio/fecha.
 *
 * Tocar cualquier parte de la fila abre la ficha en línea para editar nombre/ciudad/
 * habitaciones (§7). No hay botón de borrar: nada se borra, se une o se archiva (2026-08-03).
 */
import { formatCurrency, formatDate } from "../../../lib/utils";
import { buildLearnedProductDisplayRows, columnLabelsForServiceType } from "../lib/learnedProductVariantsLogic";

export function LearnedProductRow({ product, isExpanded, panelId, onToggle }) {
    const filas = buildLearnedProductDisplayRows(product);
    const { variantColumnLabel } = columnLabelsForServiceType(product.serviceType);
    // Sin columna del medio (Paquete/Asistencia): la grilla pasa de 5 a 4 columnas.
    const gridColumns = variantColumnLabel
        ? "grid grid-cols-[minmax(0,2fr)_minmax(0,1.4fr)_minmax(0,1.2fr)_minmax(0,1fr)_104px]"
        : "grid grid-cols-[minmax(0,2fr)_minmax(0,1.2fr)_minmax(0,1fr)_104px]";

    return (
        <button
            type="button"
            onClick={onToggle}
            className="block w-full text-left hover:bg-slate-50 dark:hover:bg-slate-800/40"
            data-testid="learned-product-row"
            aria-expanded={isExpanded}
            aria-controls={panelId}
        >
            <div className="divide-y divide-slate-50 dark:divide-slate-800/60">
                {filas.map((fila) => (
                    <div key={fila.key} className={`${gridColumns} items-start gap-3 px-6 py-3`}>
                        {fila.showProductHeader ? (
                            <div className="min-w-0">
                                <div className="truncate font-semibold text-slate-900 dark:text-white">{product.name}</div>
                                {product.subtitle && (
                                    <div className="truncate text-xs text-slate-500 dark:text-slate-400">{product.subtitle}</div>
                                )}
                            </div>
                        ) : <div />}
                        {/* Columna de variante (HABITACIÓN/CABINA/VEHÍCULO): vacía si no hay
                            dato cargado — V3=A, nunca se escribe "Sin especificar". */}
                        {variantColumnLabel && (
                            fila.showVariantLabel
                                ? <div className="text-sm text-slate-700 dark:text-slate-300">{fila.variantLabel}</div>
                                : <div />
                        )}
                        <div className="truncate text-sm text-slate-600 dark:text-slate-300">
                            {fila.supplierPrice?.supplierName || <span className="text-slate-400">Sin operador</span>}
                        </div>
                        <div className="text-sm font-semibold text-slate-900 dark:text-white">
                            {fila.supplierPrice
                                ? `${formatCurrency(fila.supplierPrice.price, fila.supplierPrice.currency)}${fila.supplierPrice.priceUnitLabel ? ` ${fila.supplierPrice.priceUnitLabel}` : ""}`
                                : <span className="text-slate-400">Sin precios cargados</span>}
                        </div>
                        <div className={`text-sm ${fila.supplierPrice?.isOldPrice ? "font-semibold text-amber-600 dark:text-amber-400" : "text-slate-500 dark:text-slate-400"}`}>
                            {fila.supplierPrice?.priceDate ? formatDate(fila.supplierPrice.priceDate) : ""}
                        </div>
                    </div>
                ))}
                {/* Tope de 3 renglones + el total, ya armado por el motor (V7=A). */}
                {product.morePricesText && (
                    <div className="px-6 py-2 text-xs text-slate-400 dark:text-slate-500">
                        {product.morePricesText}
                    </div>
                )}
            </div>
        </button>
    );
}
