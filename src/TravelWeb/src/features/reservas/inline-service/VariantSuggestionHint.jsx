/**
 * Renglón gris de una sola línea con la sugerencia de precio POR HABITACIÓN (spec
 * firmada 2026-08-07, §3.3). A diferencia de `LastSaleHint` (que arma "Último precio: X"
 * a partir de una venta cruda), acá el texto YA viene armado entero por el motor
 * (`suggestionText` de VariantPriceSuggestionDto) — el front lo muestra TAL CUAL, sin
 * agregarle ningún prefijo, porque el motor decide la frase completa según si el precio
 * es de la misma habitación o de una parecida (T-13).
 */
export function VariantSuggestionHint({ text }) {
  if (!text) return null;
  return (
    <p className="mt-1 text-xs text-slate-400" data-testid="variant-suggestion-hint">
      {text}
    </p>
  );
}
