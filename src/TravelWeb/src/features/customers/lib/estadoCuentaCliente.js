/**
 * Lógica pura del extracto de cuenta corriente del cliente.
 *
 * Contiene las funciones de combinación, ordenamiento, agrupamiento y cálculo
 * de saldo corriente del extracto estilo libro mayor (extracto ERP).
 *
 * Módulo puro: sin React, sin side effects. Testeable con `node --test` sin bundler.
 * Se puede importar desde EstadoCuentaClienteTab.jsx y desde pruebas unitarias.
 *
 * Regla multimoneda (invariante duro):
 *   - ARS y USD NUNCA se suman ni mezclan en el mismo bloque.
 *   - El saldo corriente de cada bloque se calcula SOLO con los movimientos de su moneda.
 *   - Para pagos cruzados (efectivo en moneda X imputa contra deuda en moneda Y),
 *     el bloque de la deuda (imputedCurrency) recibe el abono; el dato del efectivo
 *     real se guarda como metadata para mostrarlo como detalle sin afectar el saldo.
 */
import { formatTipoComprobante, resolverKindComprobante } from "./facturacionFilters";
import { traducirMetodoPago } from "./paymentHelpers";

/**
 * Construye la lista plana de líneas del extracto a partir de pagos e invoices.
 *
 * Regla de imputación de pagos:
 *   El campo `imputedCurrency` (ISO) es la moneda del SALDO que el pago cancela.
 *   El campo `currency` es la moneda del efectivo recibido.
 *   Para que el saldo por moneda reconcilie con la deuda real:
 *     - currency del bloque = `pago.imputedCurrency ?? pago.currency ?? "ARS"`
 *     - abono del bloque   = `Number(pago.imputedAmount) || Number(pago.amount) || 0`
 *       (|| en vez de ?? para que un 0 accidental del DTO no registre un abono nulo)
 *   Si el pago es cruzado (efectivo en una moneda, deuda en otra), se guardan
 *   `isCrossCurrency`, `cashCurrency` y `cashAmount` para que el componente
 *   muestre un detalle sutil sin afectar la aritmética del saldo.
 *
 * @param {Array} pagos         — CustomerAccountPaymentListItemDto[]
 * @param {Array} comprobantes  — InvoiceListDto[]
 * @returns {Array}             — líneas planas sin ordenar, cada una con campo `currency`
 */
export function construirLineas(pagos, comprobantes) {
  const lineas = [];

  for (const pago of pagos) {
    const metodoEspanol = traducirMetodoPago(pago.method);

    // La moneda de imputación determina en qué bloque del libro mayor cae esta línea.
    // Si el backend no envía imputedCurrency (pagos legacy), se usa la moneda del efectivo.
    const monedaImputada = pago.imputedCurrency ?? pago.currency ?? "ARS";
    // Fallback numérico (|| en lugar de ??): si imputedAmount es 0 (valor por defecto
    // del DTO cuando no hay conversión cruzada), caemos al amount real del efectivo.
    // Con ?? un 0 legítimo NO haría fallback (0 != null/undefined); con || sí lo hace,
    // lo que garantiza que nunca se registre un abono de $0 por error de serialización.
    const montoImputado = Number(pago.imputedAmount) || Number(pago.amount) || 0;

    // Detectar pago cruzado: el efectivo es en una moneda y el saldo es en otra
    const monedaEfectiva = pago.currency ?? "ARS";
    const esPagoCruzado  = !!(pago.imputedCurrency && pago.imputedCurrency !== monedaEfectiva);

    lineas.push({
      date:        pago.paidAt,
      kind:        "cobro",
      // currency = moneda del BLOQUE (determina en qué extracto aparece)
      currency:    monedaImputada,
      charge:      0,
      credit:      montoImputado,  // mueve el saldo en la moneda imputada
      description: `Cobro${metodoEspanol ? ` · ${metodoEspanol}` : ""}${pago.numeroReserva ? ` — ${pago.numeroReserva}` : ""}`,
      documentRef: pago.notes || pago.receiptNumber || "",
      // Metadata de pago cruzado para display (no usada en aritmética del saldo)
      isCrossCurrency: esPagoCruzado,
      cashCurrency:    monedaEfectiva,
      cashAmount:      Number(pago.amount ?? 0),
      source: pago,
    });
  }

  // Las facturas y notas de crédito/débito no tienen imputación cruzada:
  // siempre usan su propia moneda.
  for (const invoice of comprobantes) {
    const monto    = Number(invoice.importeTotal ?? 0);
    const esAbono  = resolverKindComprobante(invoice.tipoComprobante) === "abono";
    const tipoTexto = formatTipoComprobante(invoice.tipoComprobante);
    const pdv = String(invoice.puntoDeVenta  ?? 0).padStart(5, "0");
    const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");

    lineas.push({
      date:        invoice.createdAt,
      kind:        "comprobante",
      currency:    invoice.currency ?? "ARS",
      charge:      esAbono ? 0    : monto,
      credit:      esAbono ? monto : 0,
      description: tipoTexto,
      documentRef: `${pdv}-${num}`,
      isCrossCurrency: false,
      cashCurrency:    null,
      cashAmount:      null,
      source: invoice,
    });
  }

  return lineas;
}

/**
 * Ordena las líneas cronológicamente (más antiguas primero).
 * El ordenamiento ANTES del agrupamiento garantiza que el saldo corriente
 * dentro de cada bloque se calcule en el orden temporal correcto.
 *
 * @param {Array} lineas — sin ordenar
 * @returns {Array}      — copia ordenada por fecha ASC
 */
export function ordenarLineasPorFecha(lineas) {
  return [...lineas].sort((a, b) => {
    const fechaA = a.date ? new Date(a.date).getTime() : 0;
    const fechaB = b.date ? new Date(b.date).getTime() : 0;
    return fechaA - fechaB;
  });
}

/**
 * Agrupa las líneas por moneda.
 * Orden garantizado: ARS primero, USD después (el más frecuente en primer lugar).
 *
 * @param {Array} lineasOrdenadas — ya ordenadas cronológicamente
 * @returns {Array<{ currency: string, lineas: Array }>}
 */
export function agruparPorMoneda(lineasOrdenadas) {
  const mapaGrupos = {};  // { ARS: [...], USD: [...] }

  for (const linea of lineasOrdenadas) {
    const moneda = linea.currency ?? "ARS";
    if (!mapaGrupos[moneda]) mapaGrupos[moneda] = [];
    mapaGrupos[moneda].push(linea);
  }

  // ARS siempre primero, el resto en orden alfabético
  const claves = Object.keys(mapaGrupos).sort((a, b) => {
    if (a === "ARS") return -1;
    if (b === "ARS") return 1;
    return a.localeCompare(b);
  });

  return claves.map((moneda) => ({ currency: moneda, lineas: mapaGrupos[moneda] }));
}

/**
 * Agrega el saldo corriente acumulado a cada línea de UN grupo de moneda.
 * El saldo sube con cada cargo (deuda) y baja con cada abono (pago / NC).
 *
 * PRECONDICIÓN: todas las líneas deben ser de la misma moneda.
 * La mezcla de monedas en el mismo array es un error de uso.
 *
 * @param {Array} lineas — líneas de UNA sola moneda, ordenadas cronológicamente
 * @returns {Array}      — mismas líneas con campo `runningBalance` agregado
 */
export function calcularSaldoCorrienteDeGrupo(lineas) {
  let saldo = 0;
  return lineas.map((linea) => {
    saldo = saldo + linea.charge - linea.credit;
    return { ...linea, runningBalance: saldo };
  });
}
