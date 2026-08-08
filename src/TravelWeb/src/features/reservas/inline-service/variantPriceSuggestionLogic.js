/**
 * Lógica pura de "qué hacer con la sugerencia de precio de ESTA habitación" (spec firmada
 * 2026-08-07, §3.3 / M-15 / V9=A / V10=A). Consume la respuesta de
 * GET /api/rates/variant-price-suggestion (VariantPriceSuggestionDto) y decide:
 *
 *   1. Si corresponde PRECARGAR el costo/venta en amarillo (V9=A: solo cuando el precio
 *      es de la MISMA habitación) o dejar el casillero VACÍO y mostrar el renglón gris
 *      con el texto tal cual lo armó el motor.
 *   2. Si, al cambiar de habitación, corresponde tocar un campo que el vendedor YA
 *      escribió a mano (V10=A: nunca — "si lo escribiste vos, no se toca nunca").
 *
 * Mismo patrón que ya usa `debeMostrarJustificacionTC` (features/invoices/lib/
 * exchangeRateSuggestion.js): comparar contra el estado "sigue siendo sugerencia sin
 * tocar" (acá `camposSugeridos`, allá el número exacto), nunca contra un evento de
 * tecleo aislado.
 */

/**
 * Arma los campos a mostrar a partir de una sugerencia (o su ausencia). NO decide si hay
 * que aplicarlos al form — eso lo resuelve `resolverCamposAlCambiarVariante` de abajo,
 * que además mira si el campo sigue siendo territorio del sistema.
 *
 * @param {{isSameVariant:boolean, price:number, currency:string, suggestionText:string}|null} suggestion
 * @returns {{ debeprecargarPrecio: boolean, price: string, currency: string|null, hintText: string|null }}
 */
export function buildVariantSuggestionFields(suggestion) {
  if (!suggestion) {
    // Sin ninguna venta previa de este producto: ni precarga ni renglón gris (P-15, un
    // casillero vacío ya dice lo que hay que decir).
    return { debeprecargarPrecio: false, price: "", currency: null, hintText: null };
  }

  // V9=A: la ÚNICA variante que se puede precargar es la que coincide exacto. Si el precio
  // es "de otra habitación parecida", el casillero queda vacío pase lo que pase.
  const debeprecargarPrecio = Boolean(suggestion.isSameVariant);

  return {
    debeprecargarPrecio,
    price: debeprecargarPrecio && suggestion.price != null ? String(suggestion.price) : "",
    currency: debeprecargarPrecio ? suggestion.currency || null : null,
    // El renglón gris se muestra SIEMPRE que haya sugerencia (misma variante u otra
    // parecida) — el texto ya viene armado por el motor, tal cual (T-13).
    hintText: suggestion.suggestionText || null,
  };
}

/**
 * Decide qué hacer con el PRECIO y con la MONEDA cuando cambia la habitación DESPUÉS de
 * haber elegido el producto (V10=A). Fix ronda 2 de review — hallazgos #4 y #5:
 *
 *   #4. El precio y la moneda son DOS territorios independientes. Antes un solo booleano
 *       gobernaba los dos juntos: si el vendedor elegía la moneda a mano SIN tocar el
 *       precio, cambiar de habitación igual se la pisaba, porque el precio seguía siendo
 *       "territorio del sistema" y arrastraba la moneda con él.
 *   #5. `estaPrecioTocado`/`estaMonedaTocada` tienen que venir de un flag EXPLÍCITO que
 *       el formulario prende en el onChange de CADA campo (una vez, para siempre, hasta
 *       elegir otro producto) — nunca derivarlos de "sigue pintado de amarillo"
 *       (`camposSugeridos`), porque un casillero vacío que el vendedor JAMÁS tocó
 *       también da "no sugerido" ahí, y eso frenaba la precarga de V9=A por error (un
 *       precio real de la MISMA habitación se quedaba sin mostrar).
 *
 * @param {{estaPrecioTocado: boolean, estaMonedaTocada: boolean, suggestion: object|null}} params
 * @returns {{ debeActualizarPrecio: boolean, price: string, debeActualizarMoneda: boolean, currency: string|null, hintText: string|null }}
 */
export function resolverCamposAlCambiarVariante({ estaPrecioTocado, estaMonedaTocada, suggestion }) {
  const campos = buildVariantSuggestionFields(suggestion);

  return {
    // El vendedor ya escribió un precio a mano: NUNCA se toca, ni para vaciarlo.
    debeActualizarPrecio: !estaPrecioTocado,
    price: !estaPrecioTocado ? campos.price : null,
    // La moneda sigue la MISMA regla, pero de forma independiente del precio (fix #4).
    debeActualizarMoneda: !estaMonedaTocada,
    currency: !estaMonedaTocada ? campos.currency : null,
    // El renglón gris es solo informativo: se sigue actualizando pase lo que pase, le
    // sirve al vendedor para comparar aunque haya elegido no seguir la sugerencia.
    hintText: campos.hintText,
  };
}
