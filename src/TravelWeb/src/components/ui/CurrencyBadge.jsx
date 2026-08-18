/**
 * Cartelito de moneda que indica si un monto es en pesos ($) o dólares (US$).
 *
 * Aparece pegado al monto en cualquier pantalla multimoneda: lista de servicios,
 * historial de cobros, caja y reportes. Colores originales (mockups aprobados v2/v3,
 * 2026-06-10): pesos en teal, dólares en índigo.
 *
 * Fix Lavado de cara (2026-08-11, docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md
 * + fix bloqueante de review I4): el azul boleto es el ÚNICO color de acción de toda la
 * app, y el verde queda reservado exclusivamente para "entró plata" (chips de Saldado,
 * etc. — ver P-20 de la constitución, "un color, un significado"). Un cartelito de
 * MONEDA no informa nada de eso, así que los dos pasan a gris neutro, tal cual la
 * maqueta firmada (docs/ux/2026-08-11-maqueta-reservas-firmada.html, `.usd`/`.ars`):
 *   - Dólares: gris tinta (#4b5563, Tailwind gray-600)
 *   - Pesos: gris más claro (#8a9199, valor exacto de la maqueta — no hay un Tailwind
 *     stock que coincida, por eso va como color arbitrario)
 *
 * Regla de negocio: solo aparece cuando hay más de una moneda en pantalla (el padre
 * decide si renderizarlo o no). Este componente siempre muestra el badge cuando se lo monta.
 *
 * @param {"ARS"|"USD"} currency - Moneda del monto asociado
 * @param {"sm"|"xs"} size - Tamaño del badge; default "xs"
 */
export function CurrencyBadge({ currency, size = "xs" }) {
    const isUsd = currency === "USD";

    // Antes "sm" y "xs" tenian tamanos distintos (11px/9px). El estandar visual
    // (docs/ux/2026-08-16-guia-rollout-estandar-visual.md, B.2) prohibe el texto
    // de 9px — ahora las dos variantes de tamano quedan iguales en 11px, el piso
    // tipografico minimo permitido en toda la app.
    const textSize = "text-[11px]";

    return (
        <span
            className={`
                inline-flex items-center rounded px-1 py-0.5 font-black uppercase leading-none
                ${textSize}
                ${isUsd
                    ? "bg-gray-600 text-white"
                    : "bg-[#8a9199] text-white"
                }
            `}
            aria-label={isUsd ? "Dólares" : "Pesos argentinos"}
        >
            {isUsd ? "US$" : "$"}
        </span>
    );
}
