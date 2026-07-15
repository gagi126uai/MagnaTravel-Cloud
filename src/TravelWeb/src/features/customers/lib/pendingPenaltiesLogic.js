/**
 * Lógica PURA del bloque "Multa pendiente de cobro" de la cuenta corriente del cliente
 * (spec cerrada por Gastón 2026-07-15: `docs/ux/2026-07-15-multas-en-cuenta-del-cliente.md`,
 * sección "SPEC APROBADA PARA IMPLEMENTAR").
 *
 * QUÉ RESUELVE: hoy, cuando a un cliente se le cobra una multa por anular un viaje, esa
 * plata no aparecía en ningún lado de su cuenta corriente (la pantalla escondía las
 * reservas anuladas, y la multa solo vive ahí). El backend ahora junta esas multas en
 * `pendingPenalties` (GET /customers/{id}/account); este archivo tiene las reglas de
 * CÓMO se pinta ese dato: el texto exacto de cada chip de estado, cómo se arma el
 * recuadro de cada moneda (número grande + segunda línea ámbar), y cuándo el bloque
 * entero aparece o no.
 *
 * Se separa del JSX (CustomerAccountPage.jsx) para poder testear las reglas de negocio
 * con node:test, sin montar React ni DOM (mismo criterio que penaltyCrossCurrency.js).
 *
 * REGLA DE ORO (spec): el front NUNCA recalcula montos ni suma monedas. Estas funciones
 * solo TRADUCEN lo que ya viene armado del backend (`totalsByCurrency`, `items`) a texto
 * y color — no repiten ninguna cuenta.
 */

import { formatCurrency } from "../../../lib/utils.js";

// ============================================================================
// Estados del comprobante de una multa (mismos tokens que manda el backend en
// CustomerPendingPenaltyItemDto.Status — ver CustomerPendingPenaltyStatus.cs).
// ============================================================================
export const PENDING_PENALTY_STATUS = {
  PENDING_COLLECTION: "pendingCollection",
  ISSUING: "issuing",
  UNDER_REVIEW: "underReview",
};

// Las TRES voces en criollo del chip de cada fila (spec §3, tabla de estados). El chip
// nunca muestra el token crudo del backend ni términos fiscales: solo esto.
const CHIP_POR_ESTADO = {
  [PENDING_PENALTY_STATUS.PENDING_COLLECTION]: { texto: "Pendiente de cobro", tono: "rose" },
  [PENDING_PENALTY_STATUS.ISSUING]: { texto: "Comprobante en camino", tono: "amber" },
  [PENDING_PENALTY_STATUS.UNDER_REVIEW]: { texto: "En revisión", tono: "amber" },
};

/**
 * Texto y color del chip de UNA fila de multa, según su `status`.
 *
 * Defensivo: un status que el front todavía no conoce (dato futuro del backend) NUNCA
 * se muestra crudo — cae al texto más conservador ("En revisión", ámbar), que es el que
 * menos promete (no le dice al cliente que ya tiene una deuda firme sin estar seguros).
 *
 * @param {string} status - "pendingCollection" | "issuing" | "underReview"
 * @returns {{ texto: string, tono: "rose"|"amber" }}
 */
export function textoChipEstadoMulta(status) {
  return CHIP_POR_ESTADO[status] ?? { texto: "En revisión", tono: "amber" };
}

/**
 * True si el bloque "Multa pendiente de cobro" tiene que dibujarse. Regla dura (spec §4):
 * vacío (sin items) → el bloque NO existe, ni título ni "no hay multas" — igual que el
 * circuito del operador cuando no tiene anulaciones.
 *
 * @param {{ items?: Array }|null|undefined} pendingPenalties
 * @returns {boolean}
 */
export function debeMostrarBloqueMultasPendientes(pendingPenalties) {
  return Array.isArray(pendingPenalties?.items) && pendingPenalties.items.length > 0;
}

/**
 * Arma los datos de UN recuadro de moneda del bloque (spec §2), a partir de una fila
 * de `totalsByCurrency`. Devuelve todo lo que necesita el recuadro para dibujarse: el
 * monto y color del número grande, la etiqueta que lo acompaña (si corresponde), y el
 * texto de la segunda línea ámbar (o null si no hace falta).
 *
 * Dos casos posibles:
 *   1. Hay deuda FIRME (firmAmount > 0): el número grande es firmAmount en rojo. Si
 *      además hay plata sin comprobante todavía, se agrega la segunda línea ámbar
 *      "· {monto} sin comprobante todavía" — nunca se mezcla en el mismo número.
 *   2. NO hay deuda firme todavía (firmAmount === 0, solo queda el caso donde SÍ hay
 *      notYetIssuedAmount > 0, porque esta fila de totalsByCurrency no existiría si
 *      ambos fueran cero): mostrar "$0" en rojo sería engañoso (no hay nada exigible
 *      todavía), así que el número grande pasa a ser el total sin comprobante, en
 *      ámbar, con su propia etiqueta — sin segunda línea (ya está dicho una vez).
 *
 * @param {{ currency: string, firmAmount: number, notYetIssuedAmount: number }} total
 * @returns {{
 *   currency: string,
 *   montoGrandeTexto: string,
 *   colorMontoGrande: "rose"|"amber",
 *   etiquetaMontoGrande: string|null,
 *   segundaLineaAmbar: string|null
 * }}
 */
export function armarRecuadroMultaPorMoneda(total) {
  const currency = total?.currency;
  const firmAmount = Number(total?.firmAmount ?? 0);
  const notYetIssuedAmount = Number(total?.notYetIssuedAmount ?? 0);

  if (firmAmount > 0) {
    return {
      currency,
      montoGrandeTexto: formatCurrency(firmAmount, currency),
      colorMontoGrande: "rose",
      etiquetaMontoGrande: null,
      segundaLineaAmbar:
        notYetIssuedAmount > 0
          ? `· ${formatCurrency(notYetIssuedAmount, currency)} sin comprobante todavía`
          : null,
    };
  }

  return {
    currency,
    montoGrandeTexto: formatCurrency(notYetIssuedAmount, currency),
    colorMontoGrande: "amber",
    etiquetaMontoGrande: "sin comprobante todavía",
    segundaLineaAmbar: null,
  };
}

// ============================================================================
// Voz de la solapa "Reservas" (spec §5): alinea la etiqueta de la columna Saldo de
// una reserva anulada con multa, con los mismos tres criterios de arriba, PERO leyendo
// `cancelledMoneyContext` (token del contrato viejo, ver ReservationDebtRules.ToDtoString
// en el backend) — no `pendingPenalties.items[].status`, que es un contrato distinto.
// ============================================================================

/**
 * Texto de la columna Saldo cuando una reserva anulada tiene una multa (spec §5).
 * Devuelve null si el contexto NO es de multa (saldo a favor, inconsistente, o sin
 * contexto) — en esos casos la fila la sigue resolviendo la lógica de siempre
 * (ContextoAnuladaCuenta en CustomerAccountPage.jsx), esta función no opina.
 *
 * OJO — por qué esto NO pasa por moneyStatus.js: esta solapa es de la cuenta del
 * CLIENTE (no el listado del vendedor). Acá SÍ se muestra la multa "en revisión"
 * (decisión del dueño 2026-07-15); `moneyStatus.js` la esconde a propósito para el
 * vendedor y esa regla compartida no se toca. Por eso esta función lee el token crudo
 * del DTO en vez de reusar `getMoneyStatus`.
 *
 * @param {string|null|undefined} cancelledMoneyContext - "MultaPorCobrar" | "MultaEnRevision" | otro | null
 * @returns {string|null}
 */
export function textoContextoMultaAnuladaCuenta(cancelledMoneyContext) {
  if (cancelledMoneyContext === "MultaPorCobrar") {
    return "multa por anulación · pendiente de cobro";
  }
  if (cancelledMoneyContext === "MultaEnRevision") {
    return "multa por anulación · sin comprobante todavía";
  }
  return null;
}

/**
 * Decide TODO lo que pinta la columna Saldo de UNA fila de reserva anulada en la solapa
 * Reservas (texto de voz, monto ya formateado -o null si no corresponde mostrar ninguno-,
 * y el tono de color). Se extrae acá (fix de revisión 2026-07-1X, N1/N2/N3) para que
 * `ContextoAnuladaCuenta` en CustomerAccountPage.jsx SOLO pinte lo que esta función ya
 * decidió, sin volver a decidir "es multa / es saldo a favor / no hay nada" en el JSX.
 *
 * Reglas del monto (fix reviewer N1/N2 — "plata mal mostrada no sube"):
 *   - "MultaPorCobrar" CON `cancelledPenaltyAmount` explícito → se muestra ESE monto
 *     exacto, en `cancelledPenaltyCurrency` (el mismo dato bruto que ya usa el bloque
 *     "Multa pendiente de cobro" de arriba en la misma pantalla — nunca se mezcla con
 *     `monedaFallback` a propósito, salvo que la moneda del dato falte).
 *   - "MultaPorCobrar" SIN `cancelledPenaltyAmount` (dato legacy/inconsistente) → NO se
 *     inventa un monto: `montoTexto` queda `null` (antes esto caía a `formatCurrency(null,
 *     ...)`, que pinta "$0,00" para una multa real — mentira grave, N2).
 *   - "MultaEnRevision" → el `montoTexto` SIEMPRE es `null`. El backend no llena
 *     `cancelledPenaltyAmount` para este caso (ver CustomerAccountDtos.cs, comentario de
 *     `CancelledPenaltyAmount`); el único número disponible acá sería `reserva.balance`,
 *     que es un escalar LEGACY mono-moneda (miente en una reserva multimoneda — ver
 *     CustomerAccountDtos.cs líneas 195-198) y además es el NETO de la reserva, no el
 *     BRUTO de la multa (el bloque de arriba, que sí tiene `PenaltyAmountAtEvent`, ya
 *     muestra el monto correcto en la misma pantalla). Se prefiere "sin monto" a un
 *     monto potencialmente incorrecto (N1).
 *   - Saldo a favor (`moneyStatusKind === "saldoAFavorAnulada"`, sin contexto de multa):
 *     acá SÍ es seguro usar `balance` — es la única fuente de ese dato y `getMoneyStatus`
 *     (moneyStatus.js) ya lo valida con tolerancia de redondeo antes de llegar acá.
 *   - Cualquier otro caso — incluido un `moneyStatusKind` FUTURO que este archivo todavía
 *     no conoce (N3) — no afirma nada: tono "neutral", sin texto ni monto. Antes cualquier
 *     kind distinto de "none" se pintaba en verde "a favor", lo cual asumía que todo lo
 *     desconocido era saldo a favor.
 *
 * @param {object} params
 * @param {string|null|undefined} params.cancelledMoneyContext - "MultaPorCobrar" | "MultaEnRevision" | "SaldoAFavorPendiente" | "Inconsistente" | null
 * @param {number|null|undefined} params.cancelledPenaltyAmount
 * @param {string|null|undefined} params.cancelledPenaltyCurrency
 * @param {number|null|undefined} params.balance - balance ESCALAR legacy de la reserva (solo se usa para saldo a favor)
 * @param {string|undefined} params.moneyStatusKind - el `kind` que ya devolvió `getMoneyStatus(reserva)` para esta
 *   fila (se recibe YA calculado: esta función pura no importa moneyStatus.js, para no acoplar el contrato de esta
 *   solapa con la lógica compartida del vendedor).
 * @param {string} params.monedaFallback - moneda a usar cuando hace falta un fallback razonable (misma que usa
 *   el resto de esta pantalla: la primera línea de `reserva.porMoneda`, o "ARS").
 * @returns {{ texto: string|null, montoTexto: string|null, tono: "amber"|"emerald"|"neutral" }}
 */
export function resolverFilaReservaAnuladaCuenta({
  cancelledMoneyContext,
  cancelledPenaltyAmount,
  cancelledPenaltyCurrency,
  balance,
  moneyStatusKind,
  monedaFallback,
}) {
  const vozMulta = textoContextoMultaAnuladaCuenta(cancelledMoneyContext);

  if (vozMulta) {
    if (cancelledMoneyContext === "MultaPorCobrar") {
      const tieneMontoExplicito = cancelledPenaltyAmount !== null && cancelledPenaltyAmount !== undefined;
      return {
        texto: vozMulta,
        montoTexto: tieneMontoExplicito
          ? formatCurrency(cancelledPenaltyAmount, cancelledPenaltyCurrency ?? monedaFallback)
          : null,
        tono: "amber",
      };
    }

    // MultaEnRevision: nunca hay un monto confiable en este DTO (ver reglas arriba, N1).
    return { texto: vozMulta, montoTexto: null, tono: "amber" };
  }

  if (moneyStatusKind === "saldoAFavorAnulada") {
    return {
      texto: "a favor",
      montoTexto: formatCurrency(Math.abs(Number(balance ?? 0)), monedaFallback),
      tono: "emerald",
    };
  }

  return { texto: null, montoTexto: null, tono: "neutral" };
}
