/**
 * Renglón gris de una sola línea bajo el campo de precio, con la última venta conocida
 * de ese producto ("Último precio: Ola Mayorista · US$ 48 · 22/05/2026").
 *
 * Es puramente informativo (spec firmada 2026-08-06, §3.2): nunca pisa lo que el
 * vendedor tipeó (P-21), solo se muestra para que compare. Si no hay texto (producto
 * nuevo, sin ventas previas) no renderiza nada — no se inventa un cartelito de "sin
 * precio anterior" (P-15).
 */
export function LastSaleHint({ text }) {
    if (!text) return null;
    return (
        <p className="mt-1 text-xs text-slate-400 dark:text-slate-500" data-testid="last-sale-hint">
            Último precio: {text}
        </p>
    );
}
