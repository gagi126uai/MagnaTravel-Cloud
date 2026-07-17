/**
 * Lógica PURA de la "foto de saldo" de la cuenta corriente del cliente (Tanda D2,
 * spec `docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md`, §2).
 *
 * Recibe `summary.balanceCompositionByCurrency` (ya calculado por el backend,
 * `CustomerAccountBalanceCompositionDto`) y decide QUÉ filas mostrar, en qué orden
 * y con qué tono de color — sin volver a sumar ningún monto (regla dura de la spec:
 * "no recalcular saldos en el front, todo viene calculado del backend").
 *
 * Se separa del JSX (FotoDeSaldoCuenta.jsx) para poder testear las reglas con
 * node:test, sin montar React ni DOM — mismo criterio que pendingPenaltiesLogic.js.
 */

import { formatCurrency } from "../../../lib/utils.js";

// Tolerancia de redondeo (mismo criterio que el resto de la pantalla: un centavo de
// diferencia por redondeo nunca debe mostrar una fila "Multas abiertas" fantasma).
const EPS = 0.01;

function esCero(monto) {
  return Math.abs(Number(monto ?? 0)) <= EPS;
}

/**
 * Arma UNA celda de la tabla (monto + texto ya formateado + tono). Si el monto es
 * cero para esa moneda puntual, la celda queda en blanco ("—") aunque la FILA exista
 * (porque otra moneda sí tiene algo) — así se lee la tabla del mockup, donde "Multas
 * abiertas" en dólares aparece con un guion cuando solo hay multa en pesos.
 */
function armarCelda(monto, currency, tono, { esResta = false } = {}) {
  if (esCero(monto)) {
    return { monto: 0, montoTexto: "—", tono: "neutral" };
  }
  const prefijo = esResta ? "− " : "";
  return { monto: Number(monto), montoTexto: `${prefijo}${formatCurrency(monto, currency)}`, tono };
}

/**
 * Arma toda la estructura que necesita `FotoDeSaldoCuenta` para pintarse.
 *
 * Tres estados posibles:
 *   - "vacio": el cliente no tiene NINGÚN dato de composición todavía (nunca compró).
 *   - "alDia": hay datos, pero el saldo, las multas y el crédito (a favor Y no aplicado)
 *     están todos en 0 en TODAS las monedas — se muestra un cartel simple "Al día", sin
 *     la tabla.
 *   - "conDatos": hay algo que mostrar — se arma la tabla fila por fila.
 *
 * @param {Array<{currency:string, facturadoSinCobrar:number, multasAbiertas:number,
 *   multasEnTramite:number, creditoAFavor:number, saldo:number}>} composicion
 * @param {Array<{currency:string, amount:number}>} unappliedCreditByCurrency - spec §7.3:
 *   `summary.unappliedCreditByCurrency` (campo aparte del backend, NO viaja dentro de
 *   `composicion`). Se pinta como nota chica bajo "Crédito a favor", nunca como cartel
 *   propio — reemplaza al viejo cartel suelto "CRÉDITO NO APLICADO".
 * @returns {{
 *   estado: "vacio"|"alDia"|"conDatos",
 *   monedas: string[],
 *   filas: Array<{clave:string, etiqueta:string, porMoneda:Object, notaTramitePorMoneda?:Object, notaNoAplicadoPorMoneda?:Object}>,
 *   saldoPorMoneda: Object<string, {monto:number, montoTexto:string, tono:string, etiqueta:string|null}>
 * }}
 */
export function construirFotoDeSaldo(composicion, unappliedCreditByCurrency = []) {
  const items = Array.isArray(composicion) ? composicion : [];
  const noAplicado = Array.isArray(unappliedCreditByCurrency) ? unappliedCreditByCurrency : [];

  if (items.length === 0) {
    return { estado: "vacio", monedas: [], filas: [], saldoPorMoneda: {} };
  }

  // "Crédito no aplicado" entra en la cuenta de "hay algo que mostrar" aunque el resto
  // esté en 0 (spec §7.3): es plata real del cliente, no se puede esconder detrás de un
  // "Al día" que sugeriría que no hay nada pendiente.
  const hayAlgunNoAplicado = noAplicado.some((item) => !esCero(item.amount));

  const todoEnCero = !hayAlgunNoAplicado && items.every(
    (item) => esCero(item.saldo) && esCero(item.multasAbiertas) && esCero(item.creditoAFavor)
  );
  if (todoEnCero) {
    return { estado: "alDia", monedas: items.map((item) => item.currency), filas: [], saldoPorMoneda: {} };
  }

  const monedas = items.map((item) => item.currency);

  const noAplicadoPorMoneda = (currency) => Number(noAplicado.find((n) => n.currency === currency)?.amount ?? 0);

  // "Multas abiertas" y "Crédito a favor" son filas OPCIONALES: solo se dibujan si
  // ALGUNA moneda tiene algo en esa línea (spec §2: "solo aparece una línea si tiene
  // monto en esa moneda"). "Facturado sin cobrar" y "SALDO" están siempre. "Crédito a
  // favor" también aparece si hay crédito NO aplicado (aunque el pool de crédito
  // consumible esté en 0), porque la nota chica de esta obra cuelga de esa fila.
  const hayAlgunaMultaAbierta = items.some((item) => !esCero(item.multasAbiertas));
  const hayAlgunCreditoAFavor = items.some((item) => !esCero(item.creditoAFavor)) || hayAlgunNoAplicado;

  const filas = [
    {
      clave: "facturadoSinCobrar",
      etiqueta: "Facturado sin cobrar",
      porMoneda: Object.fromEntries(
        items.map((item) => [item.currency, armarCelda(item.facturadoSinCobrar, item.currency, "neutral")])
      ),
    },
  ];

  if (hayAlgunaMultaAbierta) {
    filas.push({
      clave: "multasAbiertas",
      etiqueta: "Multas abiertas",
      porMoneda: Object.fromEntries(
        items.map((item) => [item.currency, armarCelda(item.multasAbiertas, item.currency, "amber")])
      ),
      // Segunda línea chica ámbar "(incluye $X en trámite)" — solo si esa moneda tiene
      // parte todavía sin comprobante (spec §2, sin jerga fiscal: nunca "issuing"/"underReview").
      notaTramitePorMoneda: Object.fromEntries(
        items.map((item) => [
          item.currency,
          !esCero(item.multasEnTramite)
            ? `(incluye ${formatCurrency(item.multasEnTramite, item.currency)} en trámite)`
            : null,
        ])
      ),
    });
  }

  if (hayAlgunCreditoAFavor) {
    filas.push({
      clave: "creditoAFavor",
      etiqueta: "Crédito a favor",
      porMoneda: Object.fromEntries(
        items.map((item) => [item.currency, armarCelda(item.creditoAFavor, item.currency, "emerald", { esResta: true })])
      ),
      // Nota chica bajo "Crédito a favor" (spec §7.3): reemplaza al viejo cartel suelto
      // "CRÉDITO NO APLICADO EN $X". Solo aparece en la moneda que de verdad tiene algo
      // sin aplicar.
      notaNoAplicadoPorMoneda: Object.fromEntries(
        monedas.map((currency) => {
          const monto = noAplicadoPorMoneda(currency);
          return [currency, !esCero(monto) ? `(incluye ${formatCurrency(monto, currency)} sin aplicar)` : null];
        })
      ),
    });
  }

  const saldoPorMoneda = Object.fromEntries(
    items.map((item) => {
      const saldo = Number(item.saldo ?? 0);
      // Rojo si debe, verde si a favor, gris si 0 — mismo criterio que el resto del ERP.
      let tono = "neutral";
      let etiqueta = null;
      if (saldo > EPS) {
        tono = "rose";
        etiqueta = "debe";
      } else if (saldo < -EPS) {
        tono = "emerald";
        etiqueta = "a favor";
      }
      return [item.currency, { monto: saldo, montoTexto: formatCurrency(Math.abs(saldo), item.currency), tono, etiqueta }];
    })
  );

  return { estado: "conDatos", monedas, filas, saldoPorMoneda };
}

/**
 * True si corresponde mostrar el botón "Usar saldo a favor" para UNA moneda puntual:
 * crédito a favor > 0 en esa moneda Y el usuario tiene permiso `cobranzas.edit` (el
 * permiso ya viene evaluado por el caller, esta función no conoce el sistema de auth).
 *
 * @param {{ creditoAFavor: number, canUsarSaldo: boolean }} params
 */
export function debeMostrarBotonUsarSaldo({ creditoAFavor, canUsarSaldo }) {
  return Number(creditoAFavor ?? 0) > EPS && canUsarSaldo === true;
}
