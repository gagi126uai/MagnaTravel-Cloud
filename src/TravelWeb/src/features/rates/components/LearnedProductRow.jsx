/**
 * Un producto del Tarifario nuevo, con un renglón por operador debajo (spec firmada
 * 2026-08-06, §2.1/§2.6). El nombre, la ciudad/subtítulo y el tipo se escriben UNA sola
 * vez, en el primer renglón — los siguientes solo agregan operador/precio/fecha.
 *
 * Tocar cualquier parte de la fila abre la ficha en línea para editar nombre/ciudad
 * (§2.2). No hay botón de borrar: nada se borra, se une o se archiva (2026-08-03).
 */
import { formatCurrency, formatDate } from "../../../lib/utils";

const GRID_COLUMNS = "grid grid-cols-[minmax(0,2fr)_88px_minmax(0,1.2fr)_minmax(0,1fr)_104px]";

export function LearnedProductRow({ product, isExpanded, panelId, onToggle }) {
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
                {(product.suppliers.length > 0 ? product.suppliers : [null]).map((supplierPrice, index) => (
                    <div key={supplierPrice?.supplierPublicId ?? `${product.productPublicId}-sin-precio-${index}`} className={`${GRID_COLUMNS} items-start gap-3 px-6 py-3`}>
                        {index === 0 ? (
                            <div className="min-w-0">
                                <div className="truncate font-semibold text-slate-900 dark:text-white">{product.name}</div>
                                {product.subtitle && (
                                    <div className="truncate text-xs text-slate-500 dark:text-slate-400">{product.subtitle}</div>
                                )}
                            </div>
                        ) : <div />}
                        {index === 0 ? (
                            <div className="text-sm text-slate-600 dark:text-slate-300">{product.serviceTypeLabel}</div>
                        ) : <div />}
                        <div className="truncate text-sm text-slate-600 dark:text-slate-300">
                            {supplierPrice?.supplierName || <span className="text-slate-400">Sin operador</span>}
                        </div>
                        <div className="text-sm font-semibold text-slate-900 dark:text-white">
                            {supplierPrice
                                ? `${formatCurrency(supplierPrice.price, supplierPrice.currency)}${supplierPrice.priceUnitLabel ? ` ${supplierPrice.priceUnitLabel}` : ""}`
                                : <span className="text-slate-400">Sin precios cargados</span>}
                        </div>
                        <div className={`text-sm ${supplierPrice?.isOldPrice ? "font-semibold text-amber-600 dark:text-amber-400" : "text-slate-500 dark:text-slate-400"}`}>
                            {supplierPrice?.priceDate ? formatDate(supplierPrice.priceDate) : ""}
                        </div>
                    </div>
                ))}
            </div>
        </button>
    );
}
