/**
 * Lógica PURA de la "foto de saldo" del OPERADOR — molde unificado de cuentas
 * corrientes (Tanda 3, spec `docs/ux/2026-08-18-spec-dashboard-y-cuentas-corrientes.md`,
 * §2.0/§2.2, firmada 18/08). Espejo de `balanceCompositionLogic.js` (la misma pieza pero
 * del lado del CLIENTE), con una diferencia importante de negocio:
 *
 *   OJO — semántica INVERSA a la del cliente: acá saldo positivo significa que LA
 *   AGENCIA le debe al operador (glosario firmado: "Le debés"), no que el operador nos
 *   debe a nosotros. Confundir esto sería un bug de plata real, no solo de texto.
 *
 * Recibe `currencies` (`SupplierAccountStatementDto.currencies[]`, ya calculado por el
 * backend con `ITheyOwe`/`TheyOweMe`/`Prepayment`/`EconomicClosingBalance`) y decide QUÉ
 * tono/palabra/texto mostrar — nunca vuelve a sumar ni restar ningún monto (misma regla
 * dura que el resto de las cuentas corrientes: el backend calcula, el front solo pinta).
 *
 * Se separa del JSX (`FotoDeSaldoOperador.jsx`) para poder testear las reglas con
 * node:test, sin montar React ni DOM — mismo criterio que `balanceCompositionLogic.js`.
 */

import { formatCurrency } from "../../../lib/utils.js";
import { ordenarBloquesPesosPrimero } from "./supplierPageLogic.js";

// Tolerancia de redondeo: un centavo de diferencia por redondeo nunca debe pintar
// "Le debés"/"A favor" cuando en los hechos la cuenta está saldada (mismo umbral que
// ya usa `debeMostrarseEnGrisNeutro` en `supplierPageLogic.js` para el molde anterior).
const EPS = 0.01;

/**
 * Arma UNA tarjeta de la foto de saldo, para UNA moneda puntual.
 *
 * @param {{currency:string, iTheyOwe?:number, theyOweMe?:number, prepayment?:number,
 *   economicClosingBalance?:number, closingBalance?:number}} bloque - un elemento de
 *   `currencies[]` del backend (`GET /suppliers/{id}/account/statement`)
 * @param {boolean} puedeVerMontos - `cobranzas.see_cost`. Sin este permiso, TODA la
 *   tarjeta (número grande + las 3 líneas del desglose) va en gris con "—" (spec §2.2)
 *   — mismo comportamiento que ya tenía `AmountsVisible=false` en el molde anterior de
 *   3 recuadros, solo que ahora aplica a la tarjeta entera en vez de recuadro por recuadro.
 * @returns {{currency:string, tono:"rose"|"emerald"|"neutral", montoTexto:string,
 *   palabra:string, filas:Array<{clave:string, etiqueta:string, montoTexto:string, tono:string}>}}
 */
function armarTarjeta(bloque, puedeVerMontos) {
  const currency = bloque.currency;

  if (!puedeVerMontos) {
    return {
      currency,
      tono: "neutral",
      montoTexto: "—",
      palabra: "—",
      filas: [
        { clave: "facturasPorPagar", etiqueta: "Facturas por pagar", montoTexto: "—", tono: "neutral" },
        { clave: "teTieneQueDevolver", etiqueta: "Te tiene que devolver", montoTexto: "—", tono: "neutral" },
        { clave: "saldoAFavorTuyo", etiqueta: "Saldo a favor tuyo", montoTexto: "—", tono: "neutral" },
      ],
    };
  }

  // El saldo neto de la franja es el mismo que ya reconcilia con el pie del extracto y
  // con los recuadros del molde anterior (spec §2.2: "EconomicClosingBalance/
  // ClosingBalance" son el MISMO número por invariante de backend, ver el docstring de
  // SupplierAccountStatementCurrencyBlockDto en SupplierReadDtos.cs) — se usa el que venga.
  const saldoNeto = Number(bloque.economicClosingBalance ?? bloque.closingBalance ?? 0);

  let tono = "neutral";
  if (saldoNeto > EPS) tono = "rose";
  else if (saldoNeto < -EPS) tono = "emerald";

  return {
    currency,
    tono,
    montoTexto: formatCurrency(Math.abs(saldoNeto), currency),
    palabra: resolverPalabraSaldoOperador(tono),
    filas: [
      {
        clave: "facturasPorPagar",
        etiqueta: "Facturas por pagar",
        montoTexto: formatCurrency(bloque.iTheyOwe ?? 0, currency),
        // Neutral (no rojo): el rojo grande ya está en el saldo de la franja; esta línea
        // es solo el desglose, mismo criterio que "Facturado sin cobrar" en la cuenta
        // del cliente (balanceCompositionLogic.js: nunca colorea la línea "base").
        tono: "neutral",
      },
      {
        clave: "teTieneQueDevolver",
        etiqueta: "Te tiene que devolver",
        montoTexto: formatCurrency(bloque.theyOweMe ?? 0, currency),
        // Ámbar normalizado #B45309 (spec §0) — reemplaza el "naranja" custom que usaba
        // PALETA_RECUADRO antes de esta tanda (no era ninguno de los 9 colores de B.1).
        tono: "amber",
      },
      {
        clave: "saldoAFavorTuyo",
        etiqueta: "Saldo a favor tuyo",
        montoTexto: formatCurrency(bloque.prepayment ?? 0, currency),
        tono: "emerald",
      },
    ],
  };
}

/**
 * Arma la lista de tarjetas (una por moneda, pesos primero) que necesita
 * `FotoDeSaldoOperador` para pintarse.
 *
 * @param {Array<object>} currencies - `GET /suppliers/{id}/account/statement` → `currencies[]`
 * @param {boolean} puedeVerMontos - `cobranzas.see_cost`
 * @returns {Array<{currency:string, tono:"rose"|"emerald"|"neutral", montoTexto:string,
 *   palabra:string, filas:Array<{clave:string, etiqueta:string, montoTexto:string, tono:string}>}>}
 */
export function construirFotoDeSaldoOperador(currencies, puedeVerMontos) {
  const bloques = ordenarBloquesPesosPrimero(currencies);
  return bloques.map((bloque) => armarTarjeta(bloque, puedeVerMontos));
}

/**
 * Palabra de estado que acompaña al número grande de la franja de saldo (glosario
 * firmado 18/08, spec §2.0/§2.2): "Le debés" en rojo, "A favor" en verde, "Al día" en
 * gris — palabra de la cuenta del OPERADOR, distinta de la del cliente ("Te debe"/"A
 * favor"/"Al día", en `balanceCompositionLogic.js`) porque acá quien puede deber es LA
 * AGENCIA, no la contraparte.
 *
 * @param {"rose"|"emerald"|"neutral"} tono - mismo token que arma `construirFotoDeSaldoOperador`
 * @returns {"Le debés"|"A favor"|"Al día"}
 */
export function resolverPalabraSaldoOperador(tono) {
  if (tono === "rose") return "Le debés";
  if (tono === "emerald") return "A favor";
  return "Al día";
}
