/**
 * Detecta a qué factura de venta activa corresponde un servicio (o un conjunto de
 * servicios) de la reserva, para pre-seleccionar esa factura en el desplegable
 * "Factura de la devolución" al cancelar — en vez de que el usuario tenga que
 * adivinar entre varias facturas activas.
 *
 * Se apoya en InvoiceDto.ServicePublicIds (2026-07-16): cada factura activa trae la
 * lista de PublicId de los servicios cuyas líneas contiene (ver getActiveSaleInvoices
 * en cancellations/lib/partialCreditNoteEmissionLogic.js, que ya expone ese campo).
 * Facturas armadas antes de esta fecha, o cargadas a mano sin usar la sugerencia de
 * renglones, tienen esa lista vacía — para esos casos esta función no sugiere nada
 * y el usuario elige a mano, como siempre (nunca rompe, nunca inventa un dato que
 * el backend no mandó).
 *
 * Se usa en dos lugares:
 *   - ServiceList.jsx (modal "Cancelar servicio"): un solo servicio.
 *   - CancelarVariosServiciosInline.jsx: varios servicios tildados a la vez — solo
 *     sugiere si TODOS los tildados apuntan a la MISMA única factura.
 */

/**
 * @param {Array<string>} servicePublicIds - PublicId del/los servicio(s) que se están cancelando.
 * @param {Array<{publicId: string, servicePublicIds?: Array<string>}>} activeSaleInvoices
 *   - facturas de venta activas (salida de getActiveSaleInvoices).
 * @returns {string|null} publicId de la factura sugerida, o null si no hay una única coincidencia.
 */
export function sugerirFacturaParaServicios(servicePublicIds, activeSaleInvoices) {
  const idsBuscados = (Array.isArray(servicePublicIds) ? servicePublicIds : []).filter(Boolean);
  if (idsBuscados.length === 0) return null;

  const facturas = Array.isArray(activeSaleInvoices) ? activeSaleInvoices : [];

  // Una factura "sirve" para la sugerencia solo si contiene TODOS los servicios
  // pedidos (para cuando se tildan varios servicios a la vez, tienen que estar
  // todos adentro de la misma factura — nunca se sugiere partir la devolución
  // entre dos facturas distintas sin que el usuario lo decida a mano).
  const facturasQueContienenTodos = facturas.filter((factura) => {
    const idsDeLaFactura = new Set(
      Array.isArray(factura?.servicePublicIds) ? factura.servicePublicIds : []
    );
    return idsBuscados.every((id) => idsDeLaFactura.has(id));
  });

  // Si no hay ninguna coincidencia (servicio no facturado, o factura vieja sin
  // trazabilidad), o si hay más de una (ambigüedad rara pero posible), no
  // sugerimos nada: el usuario elige del desplegable, comportamiento de siempre.
  if (facturasQueContienenTodos.length !== 1) return null;

  return facturasQueContienenTodos[0].publicId;
}
