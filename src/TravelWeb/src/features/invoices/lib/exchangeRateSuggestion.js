/**
 * Lógica pura de la sugerencia de tipo de cambio al facturar en dólares.
 *
 * Spec: docs/ux/specs/2026-08-05-tc-sugerido-en-facturar.md (ADR-011, enmienda
 * 2026-08-05 "tipo de cambio real"). Precedente en PROD que se copia como patrón:
 * ConfirmarMultaOperadorInline.jsx + lib/penaltyCrossCurrency.js (carpeta cancellations).
 *
 * QUÉ RESUELVE: hoy, al facturar en USD, el vendedor escribía el tipo de cambio de
 * memoria y SIEMPRE tenía que justificarlo. Con GET /api/exchange-rates/suggestion
 * el sistema propone el TC oficial del día y la justificación solo se pide cuando el
 * número que queda en el casillero es DISTINTO del que propuso el motor — la MISMA
 * cuenta que hace el backend para decidir si una factura quedó "a mano" o no (T-13:
 * la comparación es NÚMERO contra NÚMERO, nunca "tocó la tecla").
 *
 * Se usa desde DOS pantallas (EmitirFacturaInline.jsx en la ficha de la reserva y
 * CreateInvoiceModal.jsx en Pagos) — la regla vive acá, en un solo lugar, para que
 * "cuándo pedir justificación" no pueda divergir entre las dos.
 */

/**
 * Interpreta la respuesta cruda de GET /api/exchange-rates/suggestion.
 *
 * El backend devuelve:
 *   - 200 { tipoCambio, fecha, esDeOtraFecha, leyenda } → api.get() lo pasa tal cual.
 *   - 204 sin body → api.get() ya lo convierte en `null` (ver parseResponse en
 *     api.js): "sin sugerencia" y "todavía no respondió" se ven igual acá, nunca un
 *     error.
 *
 * REGLA DE ORO (spec §4 punto 4): el número NO se redondea ni se reformatea — se
 * devuelve tal cual lo mandó el motor. Si el front lo tocara, una factura con el
 * número "oficial" sin cambios podría terminar marcada "a mano" por una diferencia
 * de redondeo que el usuario ni generó (el motor decide comparando exacto).
 *
 * Defensivo: si el backend mandara un tipoCambio que no es un número positivo, se
 * trata igual que "sin sugerencia" (nunca prellenamos con basura).
 *
 * @param {{tipoCambio: number, fecha: string, esDeOtraFecha: boolean, leyenda: string}|null} respuesta
 * @returns {{ tipoCambioSugerido: number|null, leyenda: string|null }}
 */
export function interpretarRespuestaSugerenciaTC(respuesta) {
  if (!respuesta || !(Number(respuesta.tipoCambio) > 0)) {
    return { tipoCambioSugerido: null, leyenda: null };
  }
  return {
    tipoCambioSugerido: respuesta.tipoCambio,
    leyenda: respuesta.leyenda || null,
  };
}

/**
 * Decide si corresponde pedir la justificación del tipo de cambio (spec §4 punto 5).
 * Aparece —y es obligatoria— cuando se cumple una de estas dos:
 *   - no hubo sugerencia del motor (Momento C: no hay número oficial que aceptar); o
 *   - el número que quedó en el casillero es DISTINTO del sugerido.
 *
 * La comparación es número contra número, NO "tocó el campo": si el usuario borra
 * y vuelve a escribir el mismo número que ya estaba, la justificación desaparece.
 * Es la misma regla exacta que aplica el motor, así la pantalla nunca pide algo que
 * el motor no va a exigir (T-13). Diferencia declarada con el precedente de la
 * multa (que usa `tipoCambioTocado`, no el valor): acá el motor manda la leyenda y
 * decide con el número, no con si hubo un evento de tecleo.
 *
 * Mientras el casillero no tenga todavía un número válido (vacío, o el usuario está
 * a mitad de tipear) y SÍ hubo sugerencia, no mostramos el campo de más — evita que
 * la justificación aparezca y desaparezca en cada tecla mientras borra para volver a
 * escribir. La validación de "TC obligatorio" ya se ocupa, por su lado, de exigir un
 * número antes de dejar emitir.
 *
 * @param {{ tipoCambioEscrito: string|number, tipoCambioSugerido: number|null, huboSugerencia: boolean }} params
 * @returns {boolean}
 */
export function debeMostrarJustificacionTC({ tipoCambioEscrito, tipoCambioSugerido, huboSugerencia }) {
  if (!huboSugerencia) return true;

  const escritoNumero = Number(tipoCambioEscrito);
  if (!(escritoNumero > 0)) return false;

  return escritoNumero !== Number(tipoCambioSugerido);
}

// Textos exactos de la línea gris debajo del casillero (spec §5, "Textos exactos").
// Centralizados acá para que las dos pantallas muestren EXACTAMENTE el mismo texto
// en los estados "buscando" y "sin dato" — la leyenda con sugerencia, en cambio, la
// arma el motor (Leyenda del DTO) y el front la muestra tal cual, sin retocarla.
export const TEXTO_BUSCANDO_TC_SUGERIDO = "Buscando el tipo de cambio del día…";
export const TEXTO_SIN_TC_SUGERIDO =
  "No tenemos el tipo de cambio del día. Escribí el tipo de cambio a mano.";

/**
 * Arma el texto de la línea gris debajo del casillero de TC (spec §3, los cinco
 * momentos). Centralizada acá (no un ternario copiado en cada pantalla) para que
 * las dos muestren EXACTAMENTE la misma regla.
 *
 * Fix N3 (review 2026-08-05): el fallback a "sin dato" se decide con `huboSugerencia`
 * (si hubo o no un número), NO con si `leyenda` vino vacía — defensa en profundidad:
 * aunque el motor mandara una leyenda vacía por error junto con un número válido, acá
 * NO queremos mostrar "no tenemos el tipo de cambio" mintiendo sobre un número que sí
 * está precargado en el casillero.
 *
 * @param {{ cargando: boolean, huboSugerencia: boolean, leyenda: string|null }} params
 * @returns {string}
 */
export function textoLeyendaTC({ cargando, huboSugerencia, leyenda }) {
  if (cargando) return TEXTO_BUSCANDO_TC_SUGERIDO;
  if (huboSugerencia) return leyenda || TEXTO_SIN_TC_SUGERIDO;
  return TEXTO_SIN_TC_SUGERIDO;
}

/**
 * Decide si falta cargar la justificación para poder habilitar "Emitir factura"
 * (spec §4 punto 8). Se separa de `debeMostrarJustificacionTC` a propósito: esta
 * función es la que se usa en el `disabled` del botón — recibe directamente si el
 * campo está mostrándose (`mostrar`, ya calculado una sola vez en el componente) en
 * vez de recalcular la condición completa, para que sea imposible que el botón use
 * una regla distinta a la que decidió si el campo aparece en pantalla.
 *
 * BUG QUE ESTO EVITA (B1, review 2026-08-05): antes el `disabled` del botón exigía
 * `!justificacionTC.trim()` SIEMPRE que la moneda era USD, sin mirar si el campo de
 * justificación estaba siquiera visible — con la sugerencia aceptada tal cual, el
 * campo no se mostraba pero el botón igual quedaba gris para siempre.
 *
 * @param {{ mostrar: boolean, texto: string }} params
 * @returns {boolean}
 */
export function faltaJustificacionTC({ mostrar, texto }) {
  return Boolean(mostrar) && !String(texto ?? "").trim();
}

/**
 * Arma la porción USD del payload de POST /invoices (spec §4 punto 11): moneda,
 * número de TC tal cual quedó en el casillero (sin redondear) y, SOLO si corresponde
 * (campo de justificación visible), la justificación. Nunca manda el origen ni la
 * fecha del TC — eso lo resuelve el servidor solo, comparando el número (bug V8:
 * antes el front mandaba SIEMPRE una fuente fija falsa).
 *
 * Se centraliza acá (no repetida en cada pantalla) para que el payload no pueda
 * divergir entre EmitirFacturaInline.jsx y CreateInvoiceModal.jsx.
 *
 * @param {{ tipoCambio: string|number, justificacion: string, mostrarJustificacion: boolean }} params
 * @returns {{ monId: "USD", monCotiz: number, exchangeRateJustification?: string }}
 */
export function construirCamposUSDParaPayload({ tipoCambio, justificacion, mostrarJustificacion }) {
  const campos = {
    monId: "USD",
    monCotiz: Number(tipoCambio),
  };
  if (mostrarJustificacion) {
    campos.exchangeRateJustification = String(justificacion ?? "").trim();
  }
  return campos;
}
