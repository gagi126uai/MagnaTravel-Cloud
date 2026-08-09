/**
 * El renglón "Producto *" (mockup FIRMADO §3.3, Momento 3) que aparece SOLO cuando la
 * línea inteligente reconoció un producto directo sin que el vendedor haya elegido nada
 * todavía: la caja de arriba sigue mostrando la frase tal cual la escribió ("sheraton
 * iguazu doble desayuno ola 48 usd..."), y este renglón aparte muestra el nombre LIMPIO
 * que entendió el motor ("Sheraton Iguazú · Puerto Iguazú") — en amarillo.
 *
 * SOLO LECTURA a propósito (bug bloqueante del revisor funcional, segunda vuelta):
 * dejarlo editable era una fuga de "identidad fantasma" — si el vendedor corregía el
 * texto acá, `rateId` seguía apuntando al producto que reconoció el motor, pero el
 * nombre que viajaba al guardar era el que el vendedor tipeó en este renglón. La
 * memoria del tarifario (que se guarda POR rateId) terminaba con un nombre que no era
 * el suyo — contaminación silenciosa, sin ningún error visible en pantalla.
 *
 * Para cambiar de producto hay DOS caminos, ninguno es este renglón:
 *   1. Volver a escribir en la caja de arriba (ProductSearchField) — eso SÍ limpia
 *      `rateId` y dispara una búsqueda nueva, de cero.
 *   2. Corregir el nombre en la ficha del producto del tarifario (§7 de la spec): ahí
 *      la corrección de texto es intencional y depende del MISMO rateId, no de uno
 *      elegido por accidente acá.
 */
export function ResolvedProductRow({ id, label, value, dataTestId }) {
    return (
        <div>
            <label className="block text-xs font-semibold text-slate-600 mb-1" htmlFor={id}>
                {label}
            </label>
            <input
                id={id}
                type="text"
                className="w-full py-2 px-3 text-sm border rounded-lg border-yellow-400 bg-yellow-50 text-slate-700 cursor-default focus:outline-none"
                value={value || ""}
                readOnly
                // Fuera del orden de tabulación: es solo lectura, no hay nada que editar
                // acá (mismo criterio que "Noches" o "Total venta" en los demás forms).
                tabIndex={-1}
                data-testid={dataTestId}
                aria-label={label}
            />
        </div>
    );
}
