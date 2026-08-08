/**
 * Lógica pura del "texto libre CON MEMORIA" (spec firmada 2026-08-07, §5.2 / V4=B
 * ajustada / M-19): el nombre fino de la habitación ("Superior", "Vista al mar") y el
 * vehículo del traslado se escriben libres la primera vez; después el sistema los
 * ofrece como sugerencia (GET /api/rates/variant-names) y el vendedor puede usar uno de
 * los ya escritos o escribir uno nuevo.
 *
 * La UNIFICACIÓN de variaciones de tipeo ("dbl sup", "SUPERIOR", "superio") la hace el
 * SERVIDOR al guardar (RateService.ResolveVariantNameAsync) — acá el front NO adivina
 * similitud de texto, solo arma qué mostrar en el desplegable, igual que ya hace
 * ProductSearchField con "crear nuevo" como última opción (P7).
 */

/**
 * Arma las opciones del desplegable de sugerencias para un campo de texto libre con
 * memoria. Las ya escritas antes vienen primero (el orden ya lo decide el motor); "usar
 * tal cual" es la última opción SIEMPRE, salvo que el texto tipeado ya sea, letra por
 * letra (sin mayúsculas), una de las sugerencias — ahí no tiene sentido ofrecer "usar tal
 * cual" como si fuera una alternativa distinta.
 *
 * @param {string} typedText — lo que el vendedor lleva escrito en el campo
 * @param {string[]} knownSuggestions — nombres que ya se usaron alguna vez (del backend)
 * @returns {{ suggestions: string[], showUseAsIsOption: boolean }}
 */
export function buildFreeTextMemoryOptions(typedText, knownSuggestions) {
  const suggestions = Array.isArray(knownSuggestions) ? knownSuggestions : [];
  const textoNormalizado = (typedText || "").trim().toLowerCase();

  if (textoNormalizado.length === 0) {
    // Sin texto todavía: se muestran las sugerencias conocidas (si las hay), sin ofrecer
    // "usar tal cual" de una cadena vacía.
    return { suggestions, showUseAsIsOption: false };
  }

  const yaEsUnaSugerenciaExacta = suggestions.some(
    (suggestion) => suggestion.trim().toLowerCase() === textoNormalizado
  );

  return { suggestions, showUseAsIsOption: !yaEsUnaSugerenciaExacta };
}
