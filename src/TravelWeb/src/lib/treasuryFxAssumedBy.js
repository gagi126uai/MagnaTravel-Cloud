/**
 * Lógica PURA de "¿Quién asume el ajuste por el dólar?" (ADR-044 T4, spec
 * `docs/ux/2026-07-10-t4-multas-pantallas.md` sección 4.3).
 *
 * Contexto de negocio: cuando el dólar del día que el operador cobró la multa es
 * distinto del dólar del día en que se liquida, queda un pequeño ajuste. La regla
 * dura de multimoneda (2026-06-09) prohíbe la frase "diferencia de cambio" en
 * cualquier pantalla — por eso la etiqueta visible es siempre "Ajuste por el dólar".
 * Quién lo asume (cliente o agencia) es configurable, con dos niveles:
 *   1. Default de la AGENCIA (config general de Facturación): "El cliente" por
 *      defecto (decisión de Gastón, 2026-07-10).
 *   2. Excepción OPCIONAL por operador (ficha del operador): si nadie la toca, hereda
 *      el default general — solo se aparta el operador que de verdad lo necesite.
 *
 * Enum del backend (TreasuryFxAssumedBy.cs): Client=0, Agency=1. El backend NO tiene
 * JsonStringEnumConverter (mismo criterio que penaltyPayload.js/otroCargoOperador.js):
 * los enums viajan como INT, nunca como el nombre en texto.
 */

export const TREASURY_FX_ASSUMED_BY = {
  Client: 0,
  Agency: 1,
};

/**
 * Las 2 opciones del renglón de config GENERAL (agencia): "El cliente" | "La
 * agencia". Sin tercera opción "como el default" acá — ESTE renglón ES el default.
 */
export const OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA = [
  { value: TREASURY_FX_ASSUMED_BY.Client, label: "El cliente" },
  { value: TREASURY_FX_ASSUMED_BY.Agency, label: "La agencia" },
];

/**
 * Valor centinela para "como la configuración general" en el campo OPCIONAL de la
 * ficha del operador (no es un valor del enum del backend: es "no pisar nada",
 * equivalente a mandar `null`/omitir el campo en el PUT del operador).
 */
export const HEREDA_CONFIGURACION_GENERAL = "heredaConfiguracionGeneral";

/**
 * Las 3 opciones del campo OPCIONAL de la ficha del operador: heredar el default
 * general (primera opción, la que corresponde al default invisible), o apartarse
 * explícitamente hacia "La agencia" o "El cliente" para ESE operador puntual.
 */
export const OPCIONES_ASUME_AJUSTE_DOLAR_OPERADOR = [
  { value: HEREDA_CONFIGURACION_GENERAL, label: "Como la configuración general" },
  { value: TREASURY_FX_ASSUMED_BY.Agency, label: "Lo asume la agencia" },
  { value: TREASURY_FX_ASSUMED_BY.Client, label: "Lo asume el cliente" },
];

/**
 * Traduce el valor INT (o null) que vino del backend a la key del <select> del
 * campo opcional del operador ("heredaConfiguracionGeneral" | 0 | 1).
 *
 * @param {number|null|undefined} treasuryFxAssumedByOverride
 * @returns {string|number}
 */
export function valorSelectDesdeOverride(treasuryFxAssumedByOverride) {
  if (treasuryFxAssumedByOverride === TREASURY_FX_ASSUMED_BY.Client) return TREASURY_FX_ASSUMED_BY.Client;
  if (treasuryFxAssumedByOverride === TREASURY_FX_ASSUMED_BY.Agency) return TREASURY_FX_ASSUMED_BY.Agency;
  return HEREDA_CONFIGURACION_GENERAL;
}

/**
 * Traduce la selección del <select> del campo opcional del operador de vuelta al
 * valor que espera el payload del backend: `null` para "hereda el default general"
 * (el backend interpreta null como "sin excepción, usa la config de la agencia"),
 * o el INT del enum para una excepción explícita.
 *
 * @param {string|number} valorSelect
 * @returns {number|null}
 */
export function overrideDesdeValorSelect(valorSelect) {
  if (valorSelect === HEREDA_CONFIGURACION_GENERAL) return null;
  return Number(valorSelect);
}
