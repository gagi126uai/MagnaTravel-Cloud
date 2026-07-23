/**
 * Lógica pura del bloque de conversión de moneda del panel "Corregir monto y moneda"
 * de la multa del operador (spec cerrada 2026-07-13, bug F-2026-1033).
 *
 * QUÉ RESUELVE: cuando la multa se cargó en una moneda (ej. US$) distinta de la
 * moneda real de la factura del cliente (ej. $), el panel de corrección hoy volvía a
 * trabar la multa una y otra vez — no había forma de decirle al sistema "esto está en
 * dólares, convertilo a pesos". Este archivo tiene las reglas de CUÁNDO aparece el
 * bloque de conversión, CÓMO se calcula el monto convertido, y CÓMO se arma el
 * pedazo del payload que viaja a PATCH /cancellations/:id/correct-penalty.
 *
 * Se separa de ConfirmarMultaOperadorInline.jsx (mismo criterio que penaltyPayload.js)
 * para poder testear las reglas de negocio con node:test, sin montar React ni DOM.
 *
 * PRE-CARGA DEL DÓLAR BNA (2026-07-14): el hook useBnaUsdRateForDate (carpeta
 * ../hooks/) consulta GET /cancellations/bna-usd-rate?date=YYYY-MM-DD cada vez que
 * cambia la fecha en que el operador cobró, y usa las funciones de este archivo
 * (interpretarRespuestaBnaRate, textoEstadoDolarBna, debeAplicarRespuestaBna) para
 * decidir qué hacer con la respuesta. El backend puede devolver una `rateDate`
 * distinta de la fecha pedida (fines de semana/feriados sin cotización propia caen
 * al último día hábil anterior) — el rótulo SIEMPRE usa la fecha real del dato
 * (rateDate), nunca la fecha que pidió el usuario.
 */

import { hoyArgentina } from "../../../lib/utils.js";

// ============================================================================
// Fuente del tipo de cambio — mismos códigos que el resto de la app
// (ver EXCHANGE_RATE_SOURCE en penaltyPayload.js y FUENTES_TC en RegistrarCobroInline.jsx).
// BNA_VendedorDivisa = 6 (dólar oficial del banco). Manual = 5 (lo escribió el usuario).
// ============================================================================
export const EXCHANGE_RATE_SOURCE_BNA = 6;
export const EXCHANGE_RATE_SOURCE_MANUAL = 5;

/**
 * Umbral de "el tipo de cambio escrito está muy lejos del oficial" para el aviso
 * suave (decisión de Gastón 2026-07-13, P1=A). El aviso NUNCA bloquea el guardado.
 *
 * OJO: el número exacto (20%) es un default razonable puesto por frontend porque
 * dominio/negocio todavía no fijó la cifra oficial (ver spec §3 punto 3: "Cuánto es
 * 'muy lejos' es una regla nueva que hoy no existe"). Se deja como constante
 * NOMBRADA justamente para que sea fácil de cambiar en un solo lugar el día que
 * el negocio confirme el número real — no está escondido en medio del código.
 */
export const UMBRAL_AVISO_TC_LEJANO = 0.20; // 20%

/**
 * Decide si corresponde mostrar el bloque de conversión: SOLO cuando la moneda en la
 * que está cargada la multa es distinta de la moneda REAL de la factura del cliente.
 *
 * Regla dura (2026-06-09 P4 / 2026-07-10): la comparación es contra `invoiceCurrency`
 * (dato nuevo del DTO), NUNCA contra `monedaSugerida` — esa es solo el valor inicial
 * del selector y es editable, no sirve para decidir si hay cruce real.
 *
 * Si la reserva todavía no tiene factura (`invoiceCurrency` null/undefined), no hay
 * contra qué comparar: se comporta como el caso normal, sin bloque.
 *
 * @param {string|undefined} monedaMulta - moneda elegida en el selector ("ARS"|"USD")
 * @param {string|null|undefined} invoiceCurrency - moneda real de la factura del cliente
 * @returns {boolean}
 */
export function hayCruceDeMoneda(monedaMulta, invoiceCurrency) {
  if (!invoiceCurrency) return false;
  return monedaMulta !== invoiceCurrency;
}

/**
 * Título del recuadro de conversión: "La multa está en X y la factura en Y: la
 * pasamos a Y." Nunca usa la palabra "diferencia de cambio" (regla dura de voz,
 * 2026-06-09 / guía #2) — es una frase descriptiva de la conversión, no del ajuste.
 *
 * Solo soporta ARS/USD porque son las únicas monedas del selector de la multa
 * (ver MONEDAS_MULTA en ConfirmarMultaOperadorInline.jsx).
 *
 * @param {string} monedaMulta
 * @param {string} invoiceCurrency
 * @returns {string}
 */
export function tituloBloqueConversion(monedaMulta, invoiceCurrency) {
  const nombreDeMoneda = (codigo) => (codigo === "USD" ? "dólares" : "pesos");
  return `↕ La multa está en ${nombreDeMoneda(monedaMulta)} y la factura en ${nombreDeMoneda(invoiceCurrency)}: la pasamos a ${nombreDeMoneda(invoiceCurrency)}.`;
}

/**
 * Encabezado del recuadro, mostrando en qué moneda está la factura del cliente.
 * Ej.: "La factura del cliente está en pesos ($)".
 *
 * @param {string} invoiceCurrency
 * @returns {string}
 */
export function encabezadoBloqueConversion(invoiceCurrency) {
  return invoiceCurrency === "USD"
    ? "La factura del cliente está en dólares (US$)"
    : "La factura del cliente está en pesos ($)";
}

/**
 * Línea 1 (spec 2026-07-14 "explicación por qué la multa va en la moneda de la
 * factura", versión COMPLETA/V1): va como primer elemento del bloque de conversión,
 * arriba de `encabezadoBloqueConversion`. Explica el PORQUÉ (no solo el qué): la
 * factura del cliente, sus notas de crédito y la multa hablan TODOS la misma moneda
 * (ADR-012 §3.3) — aunque el operador haya cobrado la multa en otra moneda.
 *
 * Es una EXCEPCIÓN autorizada a sabiendas a la regla anti-cartelitos del 2026-06-05
 * (ver guía-ux-gaston.md, sección "Explicar POR QUÉ..."): no se replica en otras
 * pantallas.
 *
 * Se da vuelta sola según la moneda de la factura (pesos↔dólares) — nunca hace falta
 * pasar la moneda contraria, se deduce de `invoiceCurrency`.
 *
 * @param {string} invoiceCurrency - moneda real de la factura ("ARS"|"USD")
 * @returns {string}
 */
export function explicacionMonedaFacturaCompleta(invoiceCurrency) {
  const monedaDeLaFactura = invoiceCurrency === "USD" ? "dólares" : "pesos";
  const monedaContraria = invoiceCurrency === "USD" ? "pesos" : "dólares";
  return `La factura de esta reserva salió en ${monedaDeLaFactura}. Todo lo que se le cobra o se le devuelve al cliente va en esa moneda, incluida la multa — aunque el operador la haya cobrado en ${monedaContraria}.`;
}

/**
 * Línea 2 (spec 2026-07-14, versión MÍNIMA/V3): va bajo el selector de Moneda del
 * panel "Confirmar multa del operador", reemplazando al texto de siempre SOLO cuando
 * corresponde mostrar la advertencia (ver P3=B en ConfirmarMultaOperadorInline.jsx:
 * modo "confirmar" + hay factura + la moneda elegida difiere de la de la factura).
 * Más corta que la línea 1 a propósito: acá no hace falta el "porqué" completo, alcanza
 * con recordar cuál es la moneda que manda.
 *
 * Mismo espejo automático pesos↔dólares que `explicacionMonedaFacturaCompleta`.
 *
 * @param {string} invoiceCurrency - moneda real de la factura ("ARS"|"USD")
 * @returns {string}
 */
export function explicacionMonedaFacturaMinima(invoiceCurrency) {
  const monedaDeLaFactura = invoiceCurrency === "USD" ? "dólares" : "pesos";
  return `La factura de esta reserva salió en ${monedaDeLaFactura}: el cargo al cliente va en ${monedaDeLaFactura}.`;
}

/**
 * Calcula el monto ya convertido a la moneda de la factura ("→ Se le cobra al
 * cliente $ X"). El tipo de cambio SIEMPRE se carga como "1 US$ = $ TC" (pesos por
 * dólar), sea cual sea la dirección del cruce — mismo criterio que RegistrarCobroInline.
 *
 * Devuelve null mientras falten datos suficientes para calcular (nada que mostrar
 * todavía, no es un error).
 *
 * @param {{ monto: string|number, monedaMulta: string, invoiceCurrency: string, tipoCambio: string|number }} params
 * @returns {number|null}
 */
export function calcularMontoConvertido({ monto, monedaMulta, invoiceCurrency, tipoCambio }) {
  const montoNumero = Number(monto);
  const tipoCambioNumero = Number(tipoCambio);
  if (!(montoNumero > 0) || !(tipoCambioNumero > 0) || !invoiceCurrency) return null;
  if (monedaMulta === invoiceCurrency) return null; // no hay cruce, nada que convertir

  if (monedaMulta === "USD" && invoiceCurrency === "ARS") return montoNumero * tipoCambioNumero;
  if (monedaMulta === "ARS" && invoiceCurrency === "USD") return montoNumero / tipoCambioNumero;
  return null;
}

/**
 * Aviso suave (P1=A, 2026-07-13): true si el tipo de cambio que escribió el usuario
 * se aparta del oficial del BNA de esa fecha en más de UMBRAL_AVISO_TC_LEJANO.
 * NO bloquea nada — es un cartelito amarillo para atajar errores de tipeo (ej. poner
 * 12 en vez de 1.200).
 *
 * Si no hay una cotización de referencia del BNA para esa fecha (204 del endpoint,
 * o la consulta todavía no resolvió), el aviso no puede evaluarse: devuelve false.
 *
 * @param {{ tipoCambioEscrito: string|number, tipoCambioReferenciaBNA: number|null|undefined }} params
 * @returns {boolean}
 */
export function debeMostrarAvisoTCLejano({ tipoCambioEscrito, tipoCambioReferenciaBNA }) {
  const escrito = Number(tipoCambioEscrito);
  const referencia = Number(tipoCambioReferenciaBNA);
  if (!(escrito > 0) || !(referencia > 0)) return false;
  const diferenciaRelativa = Math.abs(escrito - referencia) / referencia;
  return diferenciaRelativa > UMBRAL_AVISO_TC_LEJANO;
}

/**
 * Resuelve sola la fuente del tipo de cambio (P2=A, 2026-07-13): "BNA" mientras el
 * usuario no toque el número que vino pre-escrito; pasa a "Manual" apenas lo cambia.
 * No hay desplegable de fuente que el usuario tenga que llenar — el sistema decide.
 *
 * `huboSugerenciaBNA` es true solo cuando el endpoint devolvió una cotización para la
 * fecha (200). Si dio 204 (sin dato) o todavía no respondió, es false y la fuente
 * termina en Manual apenas el usuario escribe algo — mismo comportamiento que antes
 * de conectar el endpoint.
 *
 * @param {{ fueTocadoPorElUsuario: boolean, huboSugerenciaBNA: boolean }} params
 * @returns {number} EXCHANGE_RATE_SOURCE_BNA (6) | EXCHANGE_RATE_SOURCE_MANUAL (5)
 */
export function resolverFuenteTC({ fueTocadoPorElUsuario, huboSugerenciaBNA }) {
  if (huboSugerenciaBNA && !fueTocadoPorElUsuario) return EXCHANGE_RATE_SOURCE_BNA;
  return EXCHANGE_RATE_SOURCE_MANUAL;
}

/**
 * Valida los 3 campos obligatorios del bloque de conversión (spec §3: "los tres
 * datos del recuadro son obligatorios"). La justificación SOLO es obligatoria cuando
 * la fuente terminó siendo "Manual" — con fuente BNA no se pide (el dato ya viene
 * de una fuente oficial, no hace falta que el usuario explique nada).
 *
 * La fecha tampoco puede ser futura (spec §4, mismo criterio que validarCamposMulta
 * de ConfirmarMultaOperadorInline.jsx: el operador tiene que haber cobrado YA).
 *
 * @param {{ fecha: string, tipoCambio: string|number, fuente: number, justificacion: string, hoyIso?: string }} params
 *   `hoyIso` es opcional (formato "YYYY-MM-DD") — solo para poder testear la regla de
 *   "fecha futura" sin depender del reloj de la máquina que corre el test.
 * @returns {{ fecha: string|null, tipoCambio: string|null, justificacion: string|null }}
 */
export function validarBloqueConversion({ fecha, tipoCambio, fuente, justificacion, hoyIso }) {
  const errores = { fecha: null, tipoCambio: null, justificacion: null };
  // fix 2026-07-22 (bug real en PROD, mismo defecto que RegistrarCobroInline.jsx): el default
  // era new Date().toISOString().split("T")[0] (día en UTC, no en Argentina). hoyArgentina()
  // fija la zona horaria explícita. hoyIso sigue siendo el override para tests.
  const hoy = hoyIso ?? hoyArgentina();

  if (!fecha) {
    errores.fecha = "La fecha en que el operador cobró la multa es obligatoria.";
  } else if (fecha > hoy) {
    errores.fecha = "La fecha no puede ser futura.";
  }

  const tipoCambioNumero = Number(tipoCambio);
  if (!tipoCambio || Number.isNaN(tipoCambioNumero) || tipoCambioNumero <= 0) {
    errores.tipoCambio = "El tipo de cambio del día que el operador cobró es obligatorio.";
  }

  if (fuente === EXCHANGE_RATE_SOURCE_MANUAL && !(justificacion ?? "").trim()) {
    errores.justificacion = "Contá de dónde sacaste este tipo de cambio.";
  }

  return errores;
}

/**
 * true si el bloque de conversión no tiene ningún error pendiente — se usa para
 * habilitar/deshabilitar "Guardar corrección" junto con las validaciones de siempre
 * (monto > 0, motivo cargado).
 *
 * @param {{ fecha: string|null, tipoCambio: string|null, justificacion: string|null }} errores
 * @returns {boolean}
 */
export function bloqueConversionCompleto(errores) {
  return errores.fecha === null && errores.tipoCambio === null && errores.justificacion === null;
}

/**
 * Arma la porción del payload de PATCH /cancellations/:id/correct-penalty que
 * corresponde al bloque de conversión.
 *
 * REGLA DE ORO (spec §0): caso misma moneda → payload BYTE-IDÉNTICO a hoy, sin
 * ninguno de estos campos nuevos. Por eso, si `hayCruce` es false, se devuelve un
 * objeto vacío — el spread en el componente no agrega nada al payload de siempre.
 *
 * @param {{ hayCruce: boolean, tipoCambio: string|number, fuente: number, fecha: string, justificacion: string }} params
 * @returns {object} campos a mergear en el payload de correctPenalty (vacío si no hay cruce)
 */
export function construirCamposConversionParaPayload({ hayCruce, tipoCambio, fuente, fecha, justificacion }) {
  if (!hayCruce) return {};

  const campos = {
    exchangeRate: Number(tipoCambio),
    exchangeRateSource: fuente,
    // exchangeRateDate va "YYYY-MM-DD" PELADA (sin "T00:00:00Z"), a diferencia de
    // operatorConfirmationDate del payload de confirm-penalty. Asimetría intencional:
    // System.Text.Json en el backend parsea un string fecha-sola directo a DateTime?
    // sin problema, y ese contrato está cubierto por test del lado del backend.
    exchangeRateDate: fecha,
  };

  // La justificación solo viaja cuando la fuente es Manual (ver validarBloqueConversion).
  if (fuente === EXCHANGE_RATE_SOURCE_MANUAL) {
    campos.exchangeRateJustification = (justificacion ?? "").trim();
  }

  return campos;
}

// ============================================================================
// Pre-carga del dólar BNA (2026-07-14) — funciones que usa useBnaUsdRateForDate
// ============================================================================

/**
 * Convierte "YYYY-MM-DD" a "DD/MM" para los textos del bloque de conversión (spec
 * mockup: "Dólar oficial del BNA del 05/07...", "No tenemos el dólar del BNA para
 * el 05/07..."). Formato corto porque el año casi siempre es obvio por contexto
 * (la multa es de una anulación reciente).
 *
 * @param {string} fechaIso - "YYYY-MM-DD"
 * @returns {string}
 */
export function formatFechaCorta(fechaIso) {
  const partes = String(fechaIso).split("-");
  if (partes.length !== 3) return fechaIso;
  const [, mes, dia] = partes;
  return `${dia}/${mes}`;
}

/**
 * Normaliza la respuesta cruda de GET /cancellations/bna-usd-rate?date=... a la
 * forma que usa el resto de este archivo. El backend devuelve:
 *   - 200 { rate: number, rateDate: "YYYY-MM-DD" } → api.get() lo pasa tal cual.
 *   - 204 sin body → api.get() ya lo convierte en `null` (ver parseResponse en
 *     api.js), así que acá "sin dato" y "todavía no hay respuesta" se ven igual:
 *     ambos casos son "no hay nada para prellenar", nunca un error.
 *
 * Defensivo: si el backend mandara un rate que no es un número positivo, o sin
 * rateDate, se trata igual que "sin dato" (nunca prellenamos con basura).
 *
 * @param {{rate: number, rateDate: string}|null} respuesta
 * @returns {{ tipoCambioSugerido: number|null, fechaSugeridaReal: string|null }}
 */
export function interpretarRespuestaBnaRate(respuesta) {
  if (!respuesta || !(Number(respuesta.rate) > 0) || !respuesta.rateDate) {
    return { tipoCambioSugerido: null, fechaSugeridaReal: null };
  }
  return { tipoCambioSugerido: Number(respuesta.rate), fechaSugeridaReal: respuesta.rateDate };
}

/**
 * Texto de ayuda debajo del casillero de tipo de cambio (spec §3 punto 3 / §4).
 * Tres estados posibles:
 *   1. Hay cotización del BNA (fechaSugeridaReal viene informada): avisa de qué
 *      fecha es el dato REAL — puede ser distinta a la que pidió el usuario si
 *      cayó fin de semana/feriado — y aclara que escribir otro número lo toma "a mano".
 *   2. No hay cotización para la fecha pedida (204, o la fecha pedida no tiene
 *      dato): pide escribir el tipo de cambio a mano.
 *   3. Todavía no se eligió ninguna fecha: invita a elegirla primero.
 *
 * @param {{ fechaPedida: string, fechaSugeridaReal: string|null }} params
 * @returns {string}
 */
export function textoEstadoDolarBna({ fechaPedida, fechaSugeridaReal }) {
  if (fechaSugeridaReal) {
    return `Dólar oficial del BNA del ${formatFechaCorta(fechaSugeridaReal)}. Si ponés otro número, lo tomamos "a mano".`;
  }
  if (fechaPedida) {
    return `No tenemos el dólar del BNA para el ${formatFechaCorta(fechaPedida)}. Escribí el tipo de cambio a mano.`;
  }
  return "Elegí la fecha para completar el tipo de cambio.";
}

/**
 * Guarda contra respuestas tardías (race condition clásica de fetch-al-cambiar-un-
 * campo): si el usuario cambia la fecha varias veces rápido, puede llegar la
 * respuesta de una consulta VIEJA después de que ya se pidió una nueva. Esta
 * función compara la fecha que se pidió en el momento del fetch contra la fecha
 * que está seleccionada AHORA (cuando la respuesta llega) — si no coinciden, la
 * respuesta es de una fecha vieja y no hay que aplicarla.
 *
 * El hook useBnaUsdRateForDate usa esto COMO REFUERZO del patrón de cleanup por
 * closure (`cancelled = true` en el useEffect, mismo criterio que
 * useServiceNominalCoverage.js) — las dos capas juntas cubren tanto "el componente
 * se desmontó" como "la fecha cambió de nuevo antes de que la vieja respondiera".
 *
 * @param {{ fechaPedida: string, fechaVigente: string }} params
 * @returns {boolean}
 */
export function debeAplicarRespuestaBna({ fechaPedida, fechaVigente }) {
  if (!fechaPedida || !fechaVigente) return false;
  return fechaPedida === fechaVigente;
}
