/**
 * Lógica pura del agrupado por VARIANTE del Tarifario nuevo (spec firmada 2026-08-07,
 * §5.1 / V5=A / V6=A / V7=A). El backend ya manda los productos agrupados en
 * `variants[].suppliers[]` (ver LearnedProductDto) — acá solo se decide CÓMO pintar esa
 * estructura en la grilla: qué renglón muestra el nombre del producto, cuál muestra la
 * etiqueta de la habitación, y cuándo agregar la línea gris de "+N precios más".
 *
 * Vive separado del JSX para poder testearlo con `node --test`, sin levantar React.
 */

/**
 * Aplana `product.variants[].suppliers[]` en una lista de renglones LISTOS para pintar
 * en la grilla. Cada renglón sabe si tiene que mostrar el nombre del producto (solo el
 * primero de todos) y si tiene que mostrar la etiqueta de la habitación (solo el primero
 * de CADA variante) — así el componente no repite lógica de índices.
 *
 * Un producto sin ningún precio cargado devuelve UN renglón "vacío" (supplierPrice=null)
 * para que la fila igual se pueda pintar con "Sin precios cargados" (mismo criterio que
 * la version anterior de esta pantalla).
 *
 * @param {{variants: Array<{variantKey: string, variantLabel: string, suppliers: any[]}>}} product
 * @returns {Array<{key: string, variantLabel: string, showProductHeader: boolean, showVariantLabel: boolean, supplierPrice: any|null}>}
 */
export function buildLearnedProductDisplayRows(product) {
  const variants = product?.variants || [];

  // Sin ninguna variante cargada (producto recién creado, sin ventas todavía): un solo
  // renglón vacío para que la fila exista en la grilla.
  if (variants.length === 0) {
    return [{ key: "sin-precio", variantLabel: "", showProductHeader: true, showVariantLabel: false, supplierPrice: null }];
  }

  const rows = [];
  let esElPrimerRenglonDelProducto = true;

  for (const variant of variants) {
    const suppliers = variant.suppliers || [];
    // Una variante sin ningún operador cargado no debería pasar del backend, pero por las
    // dudas se pinta igual como un renglón vacío (defensivo, nunca "silencia" una variante).
    const supplierRows = suppliers.length > 0 ? suppliers : [null];

    supplierRows.forEach((supplierPrice, index) => {
      rows.push({
        key: `${variant.variantKey || "sin-variante"}-${supplierPrice?.supplierPublicId ?? index}`,
        variantLabel: variant.variantLabel || "",
        showProductHeader: esElPrimerRenglonDelProducto,
        // Solo el primer operador de CADA variante repite la etiqueta de la habitación
        // (spec V6=A: "Doble con desayuno" aparece una sola vez por grupo).
        showVariantLabel: index === 0,
        supplierPrice,
      });
      esElPrimerRenglonDelProducto = false;
    });
  }

  return rows;
}

/**
 * Elige la solapa activa por defecto cuando se entra al Tarifario (V8=A: ya no existe
 * "Todos"; siempre hay que pararse en una de las cinco). Se prioriza la primera solapa
 * CON productos, para no aterrizar en una pantalla vacía si el negocio recién está
 * cargando hoteles pero ya tiene un vuelo viejo cargado más abajo en la lista.
 *
 * @param {Array<{serviceType: string, count: number}>} tabs — SIEMPRE las 5, en el orden del backend
 * @returns {string} el serviceType a seleccionar (nunca vacío si `tabs` no está vacío)
 */
export function pickDefaultServiceTypeTab(tabs) {
  if (!Array.isArray(tabs) || tabs.length === 0) return "";
  const primeraConProductos = tabs.find((tab) => Number(tab.count) > 0);
  return (primeraConProductos || tabs[0]).serviceType;
}

/**
 * Texto de la solapa vacía, UNO por tipo de servicio (spec §9: "Todavía no vendiste
 * ningún hotel." / addendum V17: "Todavía no vendiste ninguna excursión."). Vive en el
 * FRONT (el motor no arma frases de UI, T-13 es sobre datos de negocio, no sobre textos
 * fijos de pantalla) — por eso agregar la solapa "Excursiones" en V17 necesitaba este
 * mapa nuevo, y no alcanzaba con el texto genérico que había antes.
 *
 * @param {string} serviceType
 */
export function emptyTabMessage(serviceType) {
  switch (serviceType) {
    case "Hotel":
      return "Todavía no vendiste ningún hotel.";
    case "Aereo":
      return "Todavía no vendiste ningún aéreo.";
    case "Traslado":
      return "Todavía no vendiste ningún traslado.";
    case "Paquete":
      return "Todavía no vendiste ningún paquete.";
    case "Asistencia":
      return "Todavía no vendiste ninguna asistencia.";
    case "Excursion":
      return "Todavía no vendiste ninguna excursión.";
    default:
      return "Todavía no vendiste ningún producto de este tipo.";
  }
}

// Solapas fijas de reserva, SOLO como red de contención visual (fix ronda 2 de review):
// si el primer pedido al Tarifario falla, todavía no llegó NINGUNA respuesta real con las
// solapas — sin esto, la barra entera desaparecía y "Probar de nuevo" quedaba flotando
// sin nada alrededor (spec §5.1: las solapas están SIEMPRE visibles). Los conteos quedan
// en 0 hasta que el pedido real tenga éxito; el conteo de verdad SIEMPRE lo manda el motor
// (T-13) — esto es puro andamiaje de pantalla para cuando todavía no hay ninguna respuesta.
const SOLAPAS_FIJAS_DE_RESERVA = [
  { serviceType: "Hotel", label: "Hoteles", count: 0 },
  { serviceType: "Aereo", label: "Aéreos", count: 0 },
  { serviceType: "Paquete", label: "Paquetes", count: 0 },
  { serviceType: "Traslado", label: "Traslados", count: 0 },
  { serviceType: "Asistencia", label: "Asistencias", count: 0 },
  { serviceType: "Excursion", label: "Excursiones", count: 0 },
];

/**
 * Decide qué solapas pintar: las que mandó el servidor si ya llegaron alguna vez, o la
 * red de contención fija de arriba mientras todavía no llegó ninguna.
 *
 * @param {Array<{serviceType: string, count: number}>|null|undefined} tabsFromServer
 */
export function resolveTabsForRender(tabsFromServer) {
  return Array.isArray(tabsFromServer) && tabsFromServer.length > 0 ? tabsFromServer : SOLAPAS_FIJAS_DE_RESERVA;
}

/**
 * Etiquetas de columna de la grilla, una por tipo de servicio (spec §5.1). Hotel/Aéreo/
 * Traslado tienen una columna extra para la variante; Paquete y Asistencia no (V2: sin
 * variante natural) — ahí `variantColumnLabel` es `null` y el componente no dibuja esa
 * columna.
 *
 * @param {string} serviceType — "Hotel", "Aereo", "Traslado", "Paquete", "Asistencia"
 */
export function columnLabelsForServiceType(serviceType) {
  switch (serviceType) {
    case "Hotel":
      return { productColumnLabel: "HOTEL", variantColumnLabel: "HABITACIÓN" };
    case "Aereo":
      return { productColumnLabel: "RUTA", variantColumnLabel: "CABINA" };
    case "Traslado":
      return { productColumnLabel: "TRAYECTO", variantColumnLabel: "VEHÍCULO" };
    default:
      // Paquete y Asistencia: sin columna del medio (V2: sin variante natural).
      return { productColumnLabel: "PRODUCTO", variantColumnLabel: null };
  }
}
