/**
 * Lógica pura de filtros para la lista de Facturación del cliente.
 *
 * Módulo sin dependencias de React ni de la app: solo recibe datos y devuelve datos.
 * Eso permite:
 *   1. Testear con `node --test` sin bundler.
 *   2. Reusar en la pantalla global de Facturación (módulo Ventas, spec sec.4 2026-06-28).
 *
 * IMPORTANTE — filtrado client-side:
 *   El endpoint GET /customers/{id}/account/invoices solo soporta el param `search`
 *   (texto libre). Los filtros por Desde/Hasta/Tipo/Estado/Moneda se aplican sobre la
 *   lista ya cargada en el cliente. El volumen de facturas por cliente es bajo → aceptable.
 *
 *   TODO: cuando el backend agregue DateFrom/DateTo/TipoComprobante/Estado/Currency
 *         como params a la query, mover esos filtros a server-side para escalar.
 */

/** Período por defecto: últimos 90 días (decisión UX P13=A, 2026-06-28). */
const DIAS_PERIODO_DEFECTO = 90;

/**
 * Calcula las fechas de inicio y fin del período por defecto.
 *
 * @returns {{ desde: string, hasta: string }} — formato "YYYY-MM-DD"
 */
export function calcularPeriodoPorDefecto() {
  const hoy = new Date();
  const desde = new Date(hoy);
  desde.setDate(desde.getDate() - DIAS_PERIODO_DEFECTO);

  return {
    desde: desde.toISOString().slice(0, 10),
    hasta: hoy.toISOString().slice(0, 10),
  };
}

/**
 * Mapea el código numérico ARCA de tipoComprobante al texto visible para el usuario.
 * No se exponen códigos técnicos al vendedor: siempre en español de negocio.
 *
 * @param {number} tipoComprobante — código ARCA del tipo de comprobante
 * @returns {string}
 */
export function formatTipoComprobante(tipoComprobante) {
  // Códigos ARCA / AFIP verificados contra InvoiceComprobanteHelpers.cs y WSFEv1.
  // La variante "M" (51/52/53) corresponde al caso RI → Monotributista (tipos 51-53).
  const mapa = {
    1:  "Factura A",
    6:  "Factura B",
    11: "Factura C",
    51: "Factura M",
    2:  "Nota de Débito A",
    7:  "Nota de Débito B",
    12: "Nota de Débito C",
    52: "Nota de Débito M",
    3:  "Nota de Crédito A",
    8:  "Nota de Crédito B",
    13: "Nota de Crédito C",
    53: "Nota de Crédito M",
  };
  // Fallback neutral: sin el número interno, para no exponer códigos ARCA al usuario.
  // Si aparece un tipo desconocido corresponde loguearlo en el backend, no mostrarlo aquí.
  return mapa[tipoComprobante] ?? "Comprobante";
}

/**
 * Determina si un comprobante es un CARGO (suma deuda) o un ABONO (resta deuda).
 * Facturas y ND → cargo. NC → abono.
 *
 * @param {number} tipoComprobante
 * @returns {"cargo" | "abono"}
 */
export function resolverKindComprobante(tipoComprobante) {
  // NC: 3=A, 8=B, 13=C, 53=M → restan deuda (abono)
  // Todo lo demás (Facturas 1/6/11/51 y ND 2/7/12/52) → suma deuda (cargo)
  const notasDeCredito = [3, 8, 13, 53];
  return notasDeCredito.includes(tipoComprobante) ? "abono" : "cargo";
}

/**
 * Opciones del selector "Tipo" para el componente FacturacionFilters.
 * El valor "" representa "Todos" (sin filtro activo).
 */
export const OPCIONES_TIPO_FILTRO = [
  { valor: "",   etiqueta: "Todos" },
  { valor: "1",  etiqueta: "Factura A" },
  { valor: "6",  etiqueta: "Factura B" },
  { valor: "11", etiqueta: "Factura C" },
  { valor: "51", etiqueta: "Factura M" },
  { valor: "3",  etiqueta: "Nota de Crédito A" },
  { valor: "8",  etiqueta: "Nota de Crédito B" },
  { valor: "13", etiqueta: "Nota de Crédito C" },
  { valor: "53", etiqueta: "Nota de Crédito M" },
  { valor: "2",  etiqueta: "Nota de Débito A" },
  { valor: "7",  etiqueta: "Nota de Débito B" },
  { valor: "12", etiqueta: "Nota de Débito C" },
  { valor: "52", etiqueta: "Nota de Débito M" },
];

/**
 * Opciones del selector "Estado" (estado fiscal ARCA + estado de anulación).
 * El valor "" representa "Todos".
 *
 * Spec 2026-08-18 (chip "Anulada" en Facturación): se agrega "anulada", espejo
 * de la opción que ya tiene el filtro de la pantalla global de Facturación
 * (OPCIONES_ESTADO_FILTRO_GLOBAL en facturacionGlobalFilters.js) — antes acá
 * no se podía buscar "todas las anuladas", solo se veía el chip mal pintado.
 */
export const OPCIONES_ESTADO_FILTRO = [
  { valor: "", etiqueta: "Todos" },
  { valor: "aprobado", etiqueta: "Aprobado" },
  { valor: "rechazado", etiqueta: "Rechazado" },
  { valor: "en_proceso", etiqueta: "En proceso" },
  { valor: "anulando", etiqueta: "Anulando" },
  { valor: "anulada", etiqueta: "Anulada" },
];

/**
 * Infiere el estado fiscal de una factura para el filtro de estado.
 * La prioridad es: anulando > anulada > aprobado > rechazado > en_proceso.
 *
 * @param {{ annulmentStatus?: string, resultado?: string }} invoice
 * @returns {string} clave interna (ej. "aprobado")
 */
export function resolverEstadoFiscal(invoice) {
  // annulmentStatus "Pending" = en proceso de anulación (prioridad máxima)
  if (invoice.annulmentStatus === "Pending") return "anulando";
  // Comprobante YA anulado (AnnulmentStatus.Succeeded): mismo criterio que la
  // pantalla global de Facturación — pesa más que el resultado ARCA, porque una
  // vez anulado ya no importa si en su momento fue aprobado o rechazado.
  if (invoice.annulmentStatus === "Succeeded") return "anulada";
  if (invoice.resultado === "A") return "aprobado";
  if (invoice.resultado === "R") return "rechazado";
  // Sin resultado definido (en proceso de emisión ARCA)
  return "en_proceso";
}

/**
 * Resuelve tono, texto y si el chip va tachado para el estado visual de un
 * comprobante (estado fiscal ARCA + estado de anulación).
 *
 * Centraliza el MISMO criterio que ya usa la pantalla global de Facturación
 * (FacturacionPage.jsx → ChipEstadoFiscal), para que la solapa de Facturación
 * de la reserva (InvoicingTab.jsx) y la solapa de Facturación del cliente
 * (FacturacionClienteTab.jsx) pinten exactamente el mismo chip ante los mismos
 * datos — evita que una pantalla diga "Aprobado" en verde mientras otra ya
 * sabe que el comprobante está "Anulada" en rojo.
 *
 * Prioridad (de más a menos urgente): anulando > anulada > error de anulación >
 * aprobado > rechazado > en proceso. Ver spec 2026-08-18.
 *
 * @param {{ annulmentStatus?: string, resultado?: string }} invoice
 * @returns {{ tone: "azul"|"rojo"|"verde", etiqueta: string, tachado: boolean }}
 */
export function resolverChipEstadoComprobante(invoice) {
  if (invoice?.annulmentStatus === "Pending") {
    return { tone: "azul", etiqueta: "Anulando…", tachado: false };
  }
  // Anulada de verdad: rojo + tachado, para que se lea "sin efecto" de un
  // vistazo aunque no se llegue a leer el texto del chip (B.1: rojo = freno).
  if (invoice?.annulmentStatus === "Succeeded") {
    return { tone: "rojo", etiqueta: "Anulada", tachado: true };
  }
  // Anulación fallida: caso excepcional, mismo tono rojo que el resto de los frenos.
  if (invoice?.annulmentStatus === "Failed") {
    return { tone: "rojo", etiqueta: "Error anulación", tachado: false };
  }
  if (invoice?.resultado === "A") {
    return { tone: "verde", etiqueta: "Aprobado", tachado: false };
  }
  if (invoice?.resultado === "R") {
    return { tone: "rojo", etiqueta: "Rechazado", tachado: false };
  }
  // Sin resultado definitivo: en proceso de emisión ARCA — azul "en curso" (B.5):
  // no le pide nada al usuario, solo falta la respuesta de ARCA.
  return { tone: "azul", etiqueta: "En proceso", tachado: false };
}

/**
 * Aplica el conjunto de filtros sobre la lista de facturas cargada del backend.
 * Todos los filtros son opcionales: string vacío = sin ese filtro activo.
 *
 * @param {Array} invoices — lista de InvoiceListDto ya cargada
 * @param {{
 *   desde: string,
 *   hasta: string,
 *   tipo: string,
 *   estado: string,
 *   moneda: string,
 *   buscarNumero: string
 * }} filters
 * @returns {Array} — subconjunto de invoices que pasan todos los filtros activos
 */
export function aplicarFiltros(invoices, filters) {
  if (!Array.isArray(invoices)) return [];

  const { desde, hasta, tipo, estado, moneda, buscarNumero } = filters || {};

  return invoices.filter((invoice) => {
    // Filtro por fecha "Desde": excluye comprobantes anteriores al rango
    if (desde) {
      const fechaFactura = invoice.createdAt ? invoice.createdAt.slice(0, 10) : "";
      if (fechaFactura < desde) return false;
    }

    // Filtro por fecha "Hasta": excluye comprobantes posteriores al rango
    if (hasta) {
      const fechaFactura = invoice.createdAt ? invoice.createdAt.slice(0, 10) : "";
      if (fechaFactura > hasta) return false;
    }

    // Filtro por tipo de comprobante (el valor del filtro es el código como string)
    if (tipo) {
      if (String(invoice.tipoComprobante) !== tipo) return false;
    }

    // Filtro por estado fiscal (Aprobado / Rechazado / En proceso / Anulando)
    if (estado) {
      if (resolverEstadoFiscal(invoice) !== estado) return false;
    }

    // Filtro por moneda (ARS / USD).
    // Regla multimoneda: nunca mezclar ARS y USD en el mismo número.
    // El campo `invoice.currency` viene del backend como ISO string ("ARS" / "USD").
    // Si el DTO no trae moneda (casos legacy), se asume ARS por defecto.
    if (moneda) {
      const monedaFactura = invoice.currency ?? "ARS";
      if (monedaFactura !== moneda) return false;
    }

    // Filtro por número: busca en el número formateado "00001-00012345"
    if (buscarNumero && buscarNumero.trim()) {
      const texto = buscarNumero.trim().toLowerCase();
      const puntoDeVenta = String(invoice.puntoDeVenta ?? 0).padStart(5, "0");
      const numero = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
      const numeroFormateado = `${puntoDeVenta}-${numero}`;
      if (!numeroFormateado.includes(texto)) return false;
    }

    return true;
  });
}
