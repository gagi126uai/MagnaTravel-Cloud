/**
 * Lógica pura de mapeo para la pantalla GLOBAL de Facturación (módulo VENTAS).
 *
 * Convierte el estado interno de filtros (que comparte forma con la solapa del
 * cliente) a los query params que acepta GET /api/invoices en el servidor.
 *
 * Por qué un módulo separado (y no reusar facturacionFilters.js del cliente):
 *   - La solapa del cliente filtra CLIENT-SIDE sobre la lista cargada.
 *   - Esta pantalla filtra SERVER-SIDE: cada cambio de filtro dispara un fetch nuevo.
 *   - Los params del backend (DateFrom, Document, Letter, Result, Annulment…) son
 *     distintos de los campos internos del componente FacturacionFilters.
 *
 * Sin dependencias de React para ser testeable con node --test sin bundler.
 */

/**
 * Opciones del selector "Estado" para la pantalla global de Facturación.
 *
 * Amplía la lista de la solapa del cliente (OPCIONES_ESTADO_FILTRO) con "anulada"
 * (AnnulmentStatus.Succeeded), porque el backend lo soporta como parámetro separado
 * y en la pantalla global tiene sentido buscar comprobantes ya anulados.
 *
 * La solapa del cliente usa OPCIONES_ESTADO_FILTRO (sin "anulada") porque filtra
 * client-side sobre resolverEstadoFiscal(), que no distingue ese caso.
 */
export const OPCIONES_ESTADO_FILTRO_GLOBAL = [
  { valor: "",           etiqueta: "Todos" },
  { valor: "aprobado",   etiqueta: "Aprobado" },
  { valor: "rechazado",  etiqueta: "Rechazado" },
  { valor: "en_proceso", etiqueta: "En proceso" },
  { valor: "anulando",   etiqueta: "Anulando" },
  { valor: "anulada",    etiqueta: "Anulada" },
];

/**
 * Tabla de conversión: valor del select "Tipo" → Document + Letter del backend.
 *
 * FacturacionFilters usa los códigos ARCA (int) como valor de las opciones del select
 * (ej: "1"=Factura A, "6"=Factura B). El endpoint GET /api/invoices acepta
 * Document (factura/creditnote/debitnote) + Letter (A/B/C/M) en combinación.
 *
 * Fuente: InvoiceService.ApplyInvoiceDocumentAndLetterFilter
 * Matriz ARCA: Factura→A=1,B=6,C=11,M=51 | NC→A=3,B=8,C=13,M=53 | ND→A=2,B=7,C=12,M=52
 */
const TIPO_A_DOCUMENT_LETTER = {
  "1":  { document: "factura",    letter: "A" },
  "6":  { document: "factura",    letter: "B" },
  "11": { document: "factura",    letter: "C" },
  "51": { document: "factura",    letter: "M" },
  "3":  { document: "creditnote", letter: "A" },
  "8":  { document: "creditnote", letter: "B" },
  "13": { document: "creditnote", letter: "C" },
  "53": { document: "creditnote", letter: "M" },
  "2":  { document: "debitnote",  letter: "A" },
  "7":  { document: "debitnote",  letter: "B" },
  "12": { document: "debitnote",  letter: "C" },
  "52": { document: "debitnote",  letter: "M" },
};

/**
 * Convierte el valor interno del filtro "Tipo" al par {document, letter} del backend.
 *
 * @param {string} tipo — código ARCA como string (ej: "1", "6", "3")
 * @returns {{ document: string, letter: string } | null} — null si no hay filtro activo
 */
export function mapTipoToDocumentLetter(tipo) {
  if (!tipo) return null;
  return TIPO_A_DOCUMENT_LETTER[tipo] ?? null;
}

/**
 * Convierte el valor interno del filtro "Estado" a los params del backend.
 *
 * El backend tiene DOS filtros separados:
 *   - Result: estado fiscal ARCA (aprobado/rechazado/pendiente).
 *   - Annulment: estado de anulación (anulando/anulada/none).
 *
 * Fuente: InvoiceService.ApplyInvoiceStructuredFilters
 *
 * @param {string} estado — valor del select de estado
 * @returns {{ result: string | null, annulment: string | null }}
 */
export function mapEstadoToServerParams(estado) {
  switch (estado) {
    case "aprobado":
      return { result: "aprobado", annulment: null };
    case "rechazado":
      return { result: "rechazado", annulment: null };
    // "en_proceso" en el cliente = resultado que no es A ni R en el backend → "pendiente"
    case "en_proceso":
      return { result: "pendiente", annulment: null };
    // Estados de anulación: van al param Annulment, no a Result
    case "anulando":
      return { result: null, annulment: "anulando" };
    case "anulada":
      return { result: null, annulment: "anulada" };
    default:
      return { result: null, annulment: null };
  }
}

/**
 * Construye el URLSearchParams para GET /api/invoices con todos los filtros server-side.
 *
 * Por qué server-side aquí (y no client-side como la solapa del cliente):
 *   El volumen total de comprobantes de toda la agencia puede ser muy alto.
 *   Traer 500 facturas para filtrar en el navegador no escala. El backend
 *   soporta los filtros en SQL (ver ApplyInvoiceStructuredFilters).
 *
 * @param {{ desde, hasta, tipo, estado, moneda, buscarNumero }} filters
 * @param {number} page
 * @param {number} pageSize
 * @returns {URLSearchParams}
 */
export function buildInvoiceQueryParams(filters, page, pageSize) {
  const params = new URLSearchParams();

  // Paginación y orden (más reciente primero, coherente con vista de facturación)
  params.set("page", String(page));
  params.set("pageSize", String(pageSize));
  params.set("sortBy", "createdAt");
  params.set("sortDir", "desc");

  const { desde, hasta, tipo, estado, moneda, buscarNumero } = filters || {};

  // Rango de fechas: DateFrom y DateTo se mapean sobre Invoice.CreatedAt en el backend
  if (desde) params.set("DateFrom", desde);
  if (hasta) params.set("DateTo", hasta);

  // Tipo de comprobante: código ARCA int → Document + Letter para el backend
  const documentLetter = mapTipoToDocumentLetter(tipo);
  if (documentLetter) {
    params.set("Document", documentLetter.document);
    params.set("Letter", documentLetter.letter);
  }

  // Estado: puede mapearse a Result (fiscal) o a Annulment (anulación), nunca los dos
  const estadoParams = mapEstadoToServerParams(estado);
  if (estadoParams.result)    params.set("Result",    estadoParams.result);
  if (estadoParams.annulment) params.set("Annulment", estadoParams.annulment);

  // Moneda: "ARS" / "USD" — el backend traduce a MonId ("PES"/"DOL")
  if (moneda) params.set("Currency", moneda);

  // Búsqueda por número: VoucherNumber busca en NumeroComprobante y PuntoDeVenta
  if (buscarNumero && buscarNumero.trim()) {
    params.set("VoucherNumber", buscarNumero.trim());
  }

  return params;
}
