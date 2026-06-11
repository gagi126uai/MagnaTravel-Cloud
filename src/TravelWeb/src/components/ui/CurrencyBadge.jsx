/**
 * Cartelito de moneda que indica si un monto es en pesos ($) o dólares (US$).
 *
 * Aparece pegado al monto en cualquier pantalla multimoneda: lista de servicios,
 * historial de cobros, caja y reportes. La decisión de colores viene de los mockups
 * aprobados v2/v3 (2026-06-10):
 *   - Pesos: fondo teal (#0f766e, Tailwind teal-700)
 *   - Dólares: fondo índigo (#4338ca, Tailwind indigo-700)
 *
 * Regla de negocio: solo aparece cuando hay más de una moneda en pantalla (el padre
 * decide si renderizarlo o no). Este componente siempre muestra el badge cuando se lo monta.
 *
 * @param {"ARS"|"USD"} currency - Moneda del monto asociado
 * @param {"sm"|"xs"} size - Tamaño del badge; default "xs"
 */
export function CurrencyBadge({ currency, size = "xs" }) {
    const isUsd = currency === "USD";

    const textSize = size === "sm" ? "text-[11px]" : "text-[9px]";

    return (
        <span
            className={`
                inline-flex items-center rounded px-1 py-0.5 font-black uppercase leading-none
                ${textSize}
                ${isUsd
                    ? "bg-indigo-700 text-white"
                    : "bg-teal-700 text-white"
                }
            `}
            aria-label={isUsd ? "Dólares" : "Pesos argentinos"}
        >
            {isUsd ? "US$" : "$"}
        </span>
    );
}
