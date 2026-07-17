/**
 * Lógica PURA de formato del extracto de la cuenta del cliente (Tanda D2, spec
 * `docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md`, §1/§3).
 *
 * Arma el texto de la columna "Documento" (fusión de las viejas columnas "Concepto" +
 * "Comprobante"): tipo de comprobante + número, con el motivo en criollo entre
 * paréntesis cuando el dato lo permite de forma SEGURA (ver comentario de
 * `MOTIVO_SEGURO_POR_KIND` más abajo — nunca se inventa un motivo que el backend no
 * puede confirmar). También arma el texto de cierre AL PIE de cada bloque de moneda
 * ("Saldo al día (debe): $X" — spec §1/§3, fix de revisión 2026-07-17).
 */

import { formatCurrency } from "../../../lib/utils.js";

const TIPO_LABEL_POR_KIND = {
  Invoice: "Factura",
  DebitNote: "Nota de débito",
  CreditNote: "Nota de crédito",
};

// Por qué SOLO "DebitNote" lleva un motivo fijo "(multa)": en este sistema la Nota de
// Débito se usa EXCLUSIVAMENTE para documentar la multa de una cancelación (ADR-013,
// no hay otro flujo que emita ND) — es un hecho de dominio verificado, no una regla de
// negocio que el front esté inventando. "CreditNote" en cambio puede ser tanto la NC de
// una anulación TOTAL como la NC PARCIAL de un servicio cancelado dentro de una reserva
// que sigue viva (T5, 2026-07-15) — el extracto no distingue esos dos casos a nivel de
// línea, así que acá NO se agrega "(anulación)" para no afirmar algo que no está
// confirmado (el link a la reserva de la fila igual permite ver el detalle real).
const MOTIVO_SEGURO_POR_KIND = {
  DebitNote: "(multa)",
};

/**
 * Arma el texto de la columna "Documento" de una línea del extracto del cliente.
 *
 * - Invoice/DebitNote/CreditNote: "{Tipo} {número}" (+ "(multa)" solo para DebitNote).
 * - Payment/CreditApplication: el backend YA arma un texto legible en criollo para
 *   estas líneas (ej. "Cobro recibo 0001-00045", "Cobro por transferencia", "Saldo a
 *   favor aplicado") — se muestra tal cual, sin tocarlo (regla dura: "el front no
 *   recalcula, solo pinta").
 *
 * @param {{ kind: string, description?: string, documentRef?: string }} linea
 * @returns {string}
 */
export function formatEtiquetaDocumentoExtracto(linea) {
  const kind = linea?.kind;
  const tipoLabel = TIPO_LABEL_POR_KIND[kind];

  if (!tipoLabel) {
    // Payment / CreditApplication / cualquier kind futuro no mapeado: el texto que ya
    // armó el backend en `description` es el mejor disponible, nunca se muestra vacío.
    return linea?.description || "—";
  }

  const numero = linea?.documentRef ? ` ${linea.documentRef}` : "";
  const motivo = MOTIVO_SEGURO_POR_KIND[kind] ? ` ${MOTIVO_SEGURO_POR_KIND[kind]}` : "";
  return `${tipoLabel}${numero}${motivo}`;
}

// Tolerancia de redondeo — mismo criterio que balanceCompositionLogic.js: un centavo de
// diferencia por redondeo nunca debe leerse como "el cliente debe" o "tiene a favor".
const EPS = 0.01;

/**
 * Texto de cierre AL PIE de cada bloque de moneda del extracto (fix de revisión
 * 2026-07-17, spec §1/§3: "cada bloque cierra con su propia línea 'Saldo al día
 * (debe): $ …'"). Antes esto vivía como un chip "Debe: $X" en la CABECERA del bloque,
 * que además decía siempre "Debe" aunque el cliente tuviera saldo a favor — mentira
 * cuando `closingBalance` es negativo.
 *
 * @param {number} saldoCierre - `bloque.closingBalance` (positivo = debe, negativo = a favor)
 * @param {string} currency
 * @returns {string}
 */
export function formatCierreExtracto(saldoCierre, currency) {
  const saldo = Number(saldoCierre ?? 0);
  if (saldo > EPS) {
    return `Saldo al día (debe): ${formatCurrency(saldo, currency)}`;
  }
  if (saldo < -EPS) {
    return `Saldo al día (a favor): ${formatCurrency(Math.abs(saldo), currency)}`;
  }
  return `Saldo al día: ${formatCurrency(0, currency)}`;
}
