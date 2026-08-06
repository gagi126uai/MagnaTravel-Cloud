/**
 * "Facturas en dólares" — lógica pura de la solapa nueva de Reportes.
 * Spec: docs/ux/specs/2026-08-06-ayuda-invisible-tc.md, Parte B.
 *
 * QUÉ ES: cobrás a un dólar (el que le cobraste al cliente) y facturás a otro (el
 * techo del día que exige el comprobante). Esa diferencia es REAL y NORMAL — el
 * contador la necesita ordenada, mes a mes, para cerrar los libros. El vendedor
 * común no la ve nunca: esta solapa vive detrás del permiso `reportes.view`.
 *
 * Estas funciones son las únicas que tocan la REGLA de cómo se ve un número en la
 * tabla (guion para "sin dato", signo para la diferencia). El resto — traer las
 * filas, calcular pesos cobrados, decidir si hay diferencia — lo hace el backend
 * (GET /api/reports/usd-invoices); acá NO se recalcula nada de plata.
 */

/**
 * "Pesos cobrados" puede venir en `null` cuando todavía no se imputó ningún cobro a
 * esa factura. El backend es explícito: null NO es un error ni un pendiente, es
 * "no hay nada que mostrar" — por eso la tabla pone un guion en vez de "$0" (mostrar
 * $0 insinuaría que se cobró exactamente cero, que es una afirmación distinta a
 * "todavía no cobró nada").
 *
 * @param {number|null|undefined} valor
 * @param {(valor: number) => string} formatearMonto - normalmente formatCurrency(valor, "ARS")
 * @returns {string}
 */
export function formatMontoOGuion(valor, formatearMonto) {
  if (valor === null || valor === undefined) return "—";
  return formatearMonto(valor);
}

/**
 * La columna "Diferencia" lleva un signo +/− bien visible adelante del número
 * (mockup de la spec: "+ 265.500"), para que el contador vea de un vistazo si contra
 * esa factura entró más o menos plata de la que dice el comprobante — sin pintar
 * nada de color (P11=A: esta solapa es toda gris, no hay semáforos: la diferencia
 * no es un error).
 *
 * El backend ya manda `null` para "no hay cobros" o "da exactamente cero" (mismo
 * criterio en los dos casos: nada que contar), así que acá nunca deberíamos recibir
 * un 0 explícito — el chequeo queda igual como defensa en profundidad.
 *
 * @param {number|null|undefined} diferencia
 * @param {(valor: number) => string} formatearMonto
 * @returns {string}
 */
export function formatDiferenciaConSigno(diferencia, formatearMonto) {
  if (diferencia === null || diferencia === undefined) return "—";
  if (diferencia === 0) return formatearMonto(0);

  const signo = diferencia > 0 ? "+ " : "− ";
  return `${signo}${formatearMonto(Math.abs(diferencia))}`;
}

/**
 * Arma el texto de la fila del pie de la tabla, sumando los tres números que el
 * backend ya trae calculados en `UsdInvoicesReportTotalsDto` (no se suma nada acá,
 * regla T-13 aplicada también a los reportes: el total lo calcula el motor).
 * Se separa como función pura solo para poder probar el formato sin armar todo el
 * componente — el cálculo en sí ya viene resuelto en `totales`.
 *
 * @param {{ pesosDeLaFactura: number, pesosCobrados: number|null, diferencia: number|null }} totales
 * @param {(valor: number) => string} formatearMonto
 * @returns {{ pesosDeLaFactura: string, pesosCobrados: string, diferencia: string }}
 */
export function formatTotalesTabla(totales, formatearMonto) {
  return {
    pesosDeLaFactura: formatearMonto(totales.pesosDeLaFactura),
    pesosCobrados: formatMontoOGuion(totales.pesosCobrados, formatearMonto),
    diferencia: formatDiferenciaConSigno(totales.diferencia, formatearMonto),
  };
}

/**
 * Decide qué le corresponde MOSTRAR a `UsdInvoicesReportTab` una vez que la
 * respuesta del backend ya llegó (estados "vacío" y "con datos" — el estado
 * "cargando"/"error" del componente es previo a esto, no depende de la respuesta).
 *
 * Se extrae como función pura (en vez de dejarlo como dos líneas sueltas dentro
 * del componente) para poder probar el contrato de la tabla sin necesidad de
 * montar React — este proyecto no tiene jsdom/Testing Library, así que las
 * decisiones de qué-se-ve viven en funciones puras testeables con Node, igual
 * que `resolverEstadoFiscal` en EmitirFacturaInline.jsx.
 *
 * @param {{ filas: Array, totales: {pesosDeLaFactura: number, pesosCobrados: number|null, diferencia: number|null}|null }} respuesta
 * @param {(valor: number) => string} formatearMonto
 * @returns {{ hayFilas: boolean, totalesFormateados: {pesosDeLaFactura: string, pesosCobrados: string, diferencia: string}|null }}
 */
export function derivarVistaUsdInvoicesReport({ filas, totales }, formatearMonto) {
  const hayFilas = Array.isArray(filas) && filas.length > 0;
  // Defensivo: si por algún motivo el backend mandara filas sin totales (no debería
  // pasar — UsdInvoicesReportResponse siempre trae los dos juntos), la tabla igual
  // se arma; el pie de página simplemente no se dibuja (el componente lo chequea).
  const totalesFormateados = totales ? formatTotalesTabla(totales, formatearMonto) : null;
  return { hayFilas, totalesFormateados };
}
