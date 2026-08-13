/**
 * Regla pura de precarga del texto "Formas de pago" en la ficha de la reserva (spec
 * docs/ux/2026-08-12-spec-pdf-emision-y-formas-de-pago.md, §1.2 — decisión #2 YA FIRMADA
 * por Gastón). El motor resuelve, al armar el PDF, `texto del presupuesto ?? plantilla de
 * Configuración ?? nada`. Este helper hace EXACTAMENTE esa misma cuenta del lado del front,
 * para que el textarea le muestre al vendedor la verdad de lo que el PDF va a usar ANTES de
 * que toque nada — nunca queda vacío si hay algo real para mostrar.
 *
 * Archivo `.js` PURO (sin JSX), mismo criterio que el resto de esta carpeta: se testea con
 * `node --test` sin montar React.
 */

/**
 * @param {string|null|undefined} textoDeLaReserva - reserva.budgetPaymentTermsText (texto
 *   propio de ESTE presupuesto, ya guardado alguna vez por el vendedor).
 * @param {string|null|undefined} plantillaDeConfiguracion - budgetPaymentTermsTemplate del
 *   GET /reports/settings (la plantilla general de la agencia).
 * @returns {string} el texto a precargar en el textarea. "" cuando ninguna de las dos fuentes
 *   tiene contenido — ahí el textarea arranca vacío con su placeholder.
 */
export function resolverTextoFormasDePagoPrecargado(textoDeLaReserva, plantillaDeConfiguracion) {
  if (textoDeLaReserva && textoDeLaReserva.trim().length > 0) return textoDeLaReserva;
  if (plantillaDeConfiguracion && plantillaDeConfiguracion.trim().length > 0) return plantillaDeConfiguracion;
  return "";
}

/**
 * True cuando el texto actual del textarea es distinto del texto que se precargó al abrir
 * la card — el momento exacto en que el dato "se materializa" como propio de la reserva
 * (spec §1.2: "recién se materializa cuando el vendedor escribe algo distinto de lo
 * precargado"). Dispara el autoguardado (debounce) en el componente.
 */
export function textoFormasDePagoFueEditado(textoActual, textoPrecargado) {
  return (textoActual ?? "") !== (textoPrecargado ?? "");
}

/**
 * Orquesta la precarga completa del textarea (spec §1.2): si la reserva YA tiene texto
 * propio, ese gana y NI SIQUIERA se llama a `obtenerPlantilla` (nada que pedir). Si no
 * tiene, pide la plantilla con la función inyectada y aplica la misma regla de
 * `resolverTextoFormasDePagoPrecargado`.
 *
 * Fix bloqueante (2026-08-13, hallazgo de frontend-reviewer): `obtenerPlantilla` tiene que
 * llamar al endpoint de LECTURA MÍNIMA `GET /reports/budget-payment-terms-template`
 * (permiso base de reservas — cualquier vendedor lo puede leer), NUNCA a
 * `GET /reports/settings` (Admin-only): un vendedor sin rol Admin recibía 403 ahí y el
 * textarea quedaba vacío, aunque la agencia sí tuviera una plantilla cargada.
 *
 * Si `obtenerPlantilla` tira (falla de red, permisos, lo que sea), esta función NO
 * explota: cae a `""`, igual que si la plantilla estuviera vacía — el textarea queda
 * usable con su placeholder (degradación elegante, §1.2 último caso).
 *
 * `obtenerPlantilla` se recibe como parámetro (en vez de importar `api` acá adentro)
 * justamente para poder testear esta orquestación con `node --test`, sin jsdom ni mockear
 * el cliente HTTP: el test le pasa una función fake que devuelve o tira lo que quiera
 * comprobar — mismo criterio que `refrescarFotoTrasPrueba` en aiSettingsPresentation.js.
 *
 * @param {string|null|undefined} textoDeLaReserva - reserva.budgetPaymentTermsText.
 * @param {() => Promise<string|null|undefined>} obtenerPlantilla - pide la plantilla de
 *   Configuración y devuelve el texto (o null/undefined si la agencia nunca cargó una).
 * @returns {Promise<string>} el texto a precargar en el textarea ("" si no hay nada).
 */
export async function cargarTextoPrecargadoFormasDePago(textoDeLaReserva, obtenerPlantilla) {
  if (textoDeLaReserva && textoDeLaReserva.trim().length > 0) {
    return textoDeLaReserva;
  }

  try {
    const plantilla = await obtenerPlantilla();
    return resolverTextoFormasDePagoPrecargado(null, plantilla);
  } catch {
    return "";
  }
}
