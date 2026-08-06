/**
 * Lógica pura de la sugerencia de tipo de cambio al facturar en dólares.
 *
 * Spec base: docs/ux/specs/2026-08-05-tc-sugerido-en-facturar.md (ADR-011, enmienda
 * 2026-08-05 "tipo de cambio real"). Precedente en PROD que se copia como patrón:
 * ConfirmarMultaOperadorInline.jsx + lib/penaltyCrossCurrency.js (carpeta cancellations).
 *
 * Ampliada por la spec docs/ux/specs/2026-08-06-ayuda-invisible-tc.md ("la ayuda
 * invisible del tipo de cambio", FIRMADA), que agrega dos casos nuevos:
 *   - A3 "el motor completa solo": mientras el sistema emite comprobantes de
 *     ensayo, el casillero de TC directamente no se dibuja — el vendedor ni se
 *     entera de que existió un número de juguete.
 *   - A4 "acomodo al techo": si el vendedor escribe un tipo de cambio más alto del
 *     que la factura admite ese día, el sistema lo baja solo al máximo apenas sale
 *     del casillero, con una línea gris — así el comprobante nunca rebota después
 *     de darle "Emitir".
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
 *   - 200 { tipoCambio, fecha, esDeOtraFecha, leyenda, topeDelDia, loCompletaElSistema }
 *     → api.get() lo pasa tal cual.
 *   - 204 sin body → api.get() ya lo convierte en `null` (ver parseResponse en
 *     api.js): "sin sugerencia" (A2) y "todavía no respondió" se ven igual acá,
 *     nunca un error.
 *
 * REGLA DE ORO (spec §4 punto 4): el número NO se redondea ni se reformatea — se
 * devuelve tal cual lo mandó el motor. Si el front lo tocara, una factura con el
 * número "oficial" sin cambios podría terminar marcada "a mano" por una diferencia
 * de redondeo que el usuario ni generó (el motor decide comparando exacto).
 *
 * "Ayuda invisible" (spec 2026-08-06, A3): cuando `loCompletaElSistema` viene en
 * `true`, el motor manda `tipoCambio: null` y `leyenda: ""` a propósito — no hay
 * casillero que precargar, ni línea gris que mostrar. Ese caso se distingue del 204
 * (sin dato en absoluto) porque acá SÍ hubo respuesta 200, solo que "no dibujes
 * nada" es la respuesta.
 *
 * Defensivo: si el backend mandara un tipoCambio que no es un número positivo
 * (fuera del caso `loCompletaElSistema`), se trata igual que "sin sugerencia"
 * (nunca prellenamos con basura).
 *
 * @param {{tipoCambio: number|null, fecha: string, esDeOtraFecha: boolean, leyenda: string, topeDelDia: number|null, loCompletaElSistema: boolean}|null} respuesta
 * @returns {{ tipoCambioSugerido: number|null, leyenda: string|null, topeDelDia: number|null, loCompletaElSistema: boolean }}
 */
export function interpretarRespuestaSugerenciaTC(respuesta) {
  if (!respuesta) {
    // 204: el motor no tiene ningún dato para esta fecha (A2).
    return { tipoCambioSugerido: null, leyenda: null, topeDelDia: null, loCompletaElSistema: false };
  }
  if (respuesta.loCompletaElSistema) {
    // A3: el motor va a completar el TC solo al emitir. La pantalla no tiene nada
    // que precargar ni mostrar — ni siquiera el número (no es plata de verdad).
    return { tipoCambioSugerido: null, leyenda: null, topeDelDia: null, loCompletaElSistema: true };
  }
  if (!(Number(respuesta.tipoCambio) > 0)) {
    return { tipoCambioSugerido: null, leyenda: null, topeDelDia: null, loCompletaElSistema: false };
  }
  return {
    tipoCambioSugerido: respuesta.tipoCambio,
    leyenda: respuesta.leyenda || null,
    topeDelDia: Number(respuesta.topeDelDia) > 0 ? Number(respuesta.topeDelDia) : null,
    loCompletaElSistema: false,
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
 * "Ayuda invisible" (spec 2026-08-06, A4/A5.4): cuando el número que quedó en el
 * casillero lo puso el SISTEMA (porque el que había escrito el vendedor superaba el
 * techo del día), nunca se pide explicación — el vendedor no eligió ese número, así
 * que no tiene nada que explicar. Por eso `fueAcomodadoAlTope` corta la función acá,
 * antes de comparar nada.
 *
 * @param {{ tipoCambioEscrito: string|number, tipoCambioSugerido: number|null, huboSugerencia: boolean, fueAcomodadoAlTope?: boolean }} params
 * @returns {boolean}
 */
export function debeMostrarJustificacionTC({
  tipoCambioEscrito,
  tipoCambioSugerido,
  huboSugerencia,
  fueAcomodadoAlTope = false,
}) {
  if (fueAcomodadoAlTope) return false;
  if (!huboSugerencia) return true;

  const escritoNumero = Number(tipoCambioEscrito);
  if (!(escritoNumero > 0)) return false;

  return escritoNumero !== Number(tipoCambioSugerido);
}

/**
 * "Ayuda invisible" (spec 2026-08-06, A4): decide si hay que acomodar el número que
 * el vendedor escribió al techo del día, y a qué valor.
 *
 * Se llama al SALIR del casillero (blur), nunca mientras escribe — la spec pide que
 * no salte nada mientras está tipeando. Devuelve `null` cuando no corresponde tocar
 * nada (no hay techo conocido, el casillero está vacío, o el número ya entra), y el
 * valor del techo cuando sí hay que bajarlo.
 *
 * El techo lo manda el motor (`topeDelDia`, T-13): esta función NUNCA lo calcula ni
 * le suma un margen por su cuenta, solo compara el número escrito contra el que ya
 * vino armado.
 *
 * @param {string|number} valorEscrito - lo que hay en el casillero al momento del blur
 * @param {number|null} topeDelDia - el máximo que la factura admite ese día (o null si no se conoce)
 * @returns {number|null} el valor acomodado, o null si no corresponde acomodar nada
 */
export function acomodarAlTope(valorEscrito, topeDelDia) {
  if (!(Number(topeDelDia) > 0)) return null;

  const numeroEscrito = Number(valorEscrito);
  if (!(numeroEscrito > 0)) return null;

  if (numeroEscrito <= Number(topeDelDia)) return null;

  return Number(topeDelDia);
}

// Textos exactos de la línea gris debajo del casillero (tabla A6 de la spec
// 2026-08-06, "ayuda invisible del tipo de cambio" — copiados carácter por
// carácter). Centralizados acá para que las dos pantallas muestren EXACTAMENTE el
// mismo texto en los estados "buscando" y "sin dato" — la leyenda con sugerencia,
// en cambio, la arma el motor (Leyenda del DTO) y el front la muestra tal cual,
// sin retocarla.
//
// "Escribí el tipo de cambio." reemplaza al texto viejo, más largo, que empezaba
// disculpándose ("No tenemos el tipo de cambio del día...") — la disculpa no le
// suma nada al vendedor; la única instrucción útil es la que queda (regla P-15).
export const TEXTO_BUSCANDO_TC_SUGERIDO = "Buscando el tipo de cambio…";
export const TEXTO_SIN_TC_SUGERIDO = "Escribí el tipo de cambio.";

/**
 * Arma el texto exacto "En la factura entra hasta $ X." (tabla A6) para cuando el
 * sistema acomodó el número al techo del día (A4). Reutiliza el mismo formato de
 * moneda que ya usan las dos pantallas para pesos (Intl es-AR/ARS con dos
 * decimales), así el número se ve igual en todos lados.
 *
 * @param {number} topeDelDia
 * @returns {string}
 */
export function textoAcomodadoAlTope(topeDelDia) {
  const montoFormateado = new Intl.NumberFormat("es-AR", {
    style: "currency",
    currency: "ARS",
    minimumFractionDigits: 2,
  }).format(Number(topeDelDia));
  return `En la factura entra hasta ${montoFormateado}.`;
}

/**
 * Arma el texto de la línea gris debajo del casillero de TC (spec §3, los cinco
 * momentos, ampliada por la spec 2026-08-06 con el momento "acomodado al techo").
 * Centralizada acá (no un ternario copiado en cada pantalla) para que las dos
 * muestren EXACTAMENTE la misma regla.
 *
 * Orden de prioridad (de la spec): buscando > acomodado al techo > con sugerencia
 * del motor > sin sugerencia. El acomodo va ANTES que la leyenda con sugerencia
 * porque, apenas el sistema baja el número al techo, esa es la única información
 * que le sirve al vendedor — la leyenda de "qué dólar es" ya no aplica al número
 * que quedó en el casillero.
 *
 * Fix N3 (review 2026-08-05): el fallback a "sin dato" se decide con `huboSugerencia`
 * (si hubo o no un número), NO con si `leyenda` vino vacía — defensa en profundidad:
 * aunque el motor mandara una leyenda vacía por error junto con un número válido, acá
 * NO queremos mostrar "escribí el tipo de cambio" mintiendo sobre un número que sí
 * está precargado en el casillero.
 *
 * @param {{ cargando: boolean, huboSugerencia: boolean, leyenda: string|null, fueAcomodadoAlTope?: boolean, topeDelDia?: number|null }} params
 * @returns {string}
 */
export function textoLeyendaTC({
  cargando,
  huboSugerencia,
  leyenda,
  fueAcomodadoAlTope = false,
  topeDelDia = null,
}) {
  if (cargando) return TEXTO_BUSCANDO_TC_SUGERIDO;
  if (fueAcomodadoAlTope && Number(topeDelDia) > 0) return textoAcomodadoAlTope(topeDelDia);
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
 * "Ayuda invisible" (spec 2026-08-06, A3): cuando `loCompletaElSistema` es `true`
 * no hubo casillero que el vendedor haya llenado — el número que viajaría en
 * `tipoCambio` ni siquiera representa algo que él haya visto. Mandamos un
 * `monCotiz` de relleno (1): el backend lo IGNORA por completo y pisa el tipo de
 * cambio solo, con el número que el comprobante de práctica exige en ese momento
 * (ver `InvoiceService.ApplySystemFilledExchangeRate` — corre ANTES de cualquier
 * validación sobre el número que llegó). Tampoco se manda justificación: no hubo
 * ningún número que el vendedor haya elegido, así que no hay nada que explicar.
 *
 * @param {{ tipoCambio: string|number, justificacion: string, mostrarJustificacion: boolean, loCompletaElSistema?: boolean }} params
 * @returns {{ monId: "USD", monCotiz: number, exchangeRateJustification?: string }}
 */
export function construirCamposUSDParaPayload({
  tipoCambio,
  justificacion,
  mostrarJustificacion,
  loCompletaElSistema = false,
}) {
  if (loCompletaElSistema) {
    return { monId: "USD", monCotiz: 1 };
  }

  const campos = {
    monId: "USD",
    monCotiz: Number(tipoCambio),
  };
  if (mostrarJustificacion) {
    campos.exchangeRateJustification = String(justificacion ?? "").trim();
  }
  return campos;
}
