/**
 * Lógica pura del "buscador versátil" (spec FIRMADA 2026-08-10, D1..D13): el buscador de
 * producto de la ficha de servicio ya no está encerrado en la solapa donde está parado el
 * vendedor. `GET /rates/catalog-search` devuelve resultados de los 5 tipos (Hotel, Aéreo,
 * Traslado, Paquete, Asistencia) — el `serviceType` que manda la ficha es solo una
 * PREFERENCIA de orden para el backend, ya no un filtro duro.
 *
 * Acá vive la parte SIN React de esa mecánica: qué fila es "de otro tipo", en qué orden se
 * pintan (D9: partición dura, primero el tipo activo) y cuándo la ficha tiene que aplicar
 * la selección que quedó "pendiente" al saltar de solapa (D3/D7).
 */

// ─── Una fila, ¿es de OTRO tipo que la solapa activa? ─────────────────────────────

/**
 * D1/D8: las filas del tipo de la solapa activa no llevan ninguna marca (la solapa ya lo
 * dice). Las de otro tipo sí — esta función es la única fuente de verdad de esa decisión,
 * la usan tanto la chapita gris del dropdown como el salto de solapa al elegir.
 *
 * Si el resultado no trae `serviceType` (dato faltante, no debería pasar con el contrato
 * actual del backend) se lo trata como "del tipo activo": más vale no marcar de más un
 * resultado que en realidad sí corresponde a la solapa donde está el vendedor.
 *
 * @param {{serviceType?: string}|null|undefined} result — una fila de catalog-search
 * @param {string} serviceTypeActivo — el tipo de la solapa donde está parado el vendedor
 * @returns {boolean}
 */
export function esResultadoDeOtroTipo(result, serviceTypeActivo) {
  const tipoDelResultado = result?.serviceType;
  if (!tipoDelResultado || !serviceTypeActivo) return false;
  return tipoDelResultado !== serviceTypeActivo;
}

// ─── D9: partición dura — primero el tipo activo, después el resto ────────────────

/**
 * Reordena los resultados en DOS bloques, sin tocar el orden RELATIVO de cada uno:
 * primero todas las filas del tipo de la solapa activa (tal cual llegaron), después
 * todas las de otros tipos (tal cual llegaron). Motivo firmado por Gastón (D9): que un
 * Enter rápido sobre el primer resultado no salte de solapa sin querer.
 *
 * Si NINGUNA fila es del tipo activo, las de otro tipo quedan arriba porque son las
 * únicas que hay — no hace falta un caso especial, la partición ya lo resuelve sola
 * (el primer bloque queda vacío).
 *
 * Fix #1 (auditoría de coherencia 2026-08-10, bug reportado por Gastón): si el vendedor
 * ya eligió un operador a mano en el form (`supplierIdElegido`), DENTRO de cada uno de
 * los dos bloques las filas cuya última venta fue con ESE operador quedan primero —
 * "el dato ya viaja con el vendedor". Esto es un REORDEN, no un filtro: ninguna fila
 * desaparece ni se saca de su bloque de tipo (la partición D9 sigue mandando primero).
 *
 * @param {object[]} resultados — resultados tal cual los devuelve catalog-search
 * @param {string} serviceTypeActivo
 * @param {string} [supplierIdElegido] — operador que el vendedor ya eligió a mano, si hay
 * @returns {object[]} mismos objetos, reordenados en los dos bloques
 */
export function particionarPorTipo(resultados, serviceTypeActivo, supplierIdElegido) {
  const lista = resultados || [];
  const delTipoActivo = lista.filter((resultado) => !esResultadoDeOtroTipo(resultado, serviceTypeActivo));
  const deOtroTipo = lista.filter((resultado) => esResultadoDeOtroTipo(resultado, serviceTypeActivo));
  return [
    ...priorizarPorOperadorElegido(delTipoActivo, supplierIdElegido),
    ...priorizarPorOperadorElegido(deOtroTipo, supplierIdElegido),
  ];
}

// ─── Fix #1 (auditoría 2026-08-10): priorizar filas del operador ya elegido ───────

/**
 * Reordena `resultados` poniendo PRIMERO las filas cuya ÚLTIMA VENTA fue con
 * `supplierIdElegido` (sin tocar el orden relativo dentro de cada uno de los dos
 * grupos) — nunca filtra ni saca nada, las demás quedan abajo tal cual estaban.
 *
 * Bug reportado por el dueño: el vendedor elegía un operador a mano en el select antes
 * de buscar el producto, y el buscador lo ignoraba por completo — mostraba los
 * resultados en el orden de siempre, sin aprovechar el dato que el vendedor ya había
 * dado.
 *
 * @param {object[]} resultados — filas de catalog-search (con `lastSale.supplierPublicId`)
 * @param {string} [supplierIdElegido] — operador ya elegido a mano; sin esto, no reordena nada
 */
export function priorizarPorOperadorElegido(resultados, supplierIdElegido) {
  const lista = resultados || [];
  if (!supplierIdElegido) return lista;

  const conEseOperador = lista.filter((resultado) => resultado?.lastSale?.supplierPublicId === supplierIdElegido);
  const conOtroOperador = lista.filter((resultado) => resultado?.lastSale?.supplierPublicId !== supplierIdElegido);
  return [...conEseOperador, ...conOtroOperador];
}

// ─── D6: al EDITAR, el buscador sigue limitado a su tipo ──────────────────────────

/**
 * Editando un servicio ya cargado no se puede cambiar de tipo (la ficha es para ESE
 * servicio y las solapas están apagadas) — ofrecer un resultado de otro tipo sería
 * ofrecer un salto imposible. Como el backend ya no filtra por `serviceType` (ahora es
 * solo preferencia de orden), el filtro duro en modo edición lo hace el frontend.
 *
 * @param {object[]} resultados
 * @param {string} serviceTypeActivo
 * @returns {object[]} solo las filas del tipo activo
 */
export function filtrarPorTipoActivo(resultados, serviceTypeActivo) {
  const lista = resultados || [];
  return lista.filter((resultado) => !esResultadoDeOtroTipo(resultado, serviceTypeActivo));
}

// ─── D3/D7: aplicar la selección "pendiente" tras el salto de solapa ──────────────

/**
 * `ServiceInlineCard` guarda en `seleccionPendiente` el resultado que el vendedor eligió
 * de OTRO tipo mientras cambia la solapa sola (D3, silencioso e inmediato). Cada uno de
 * los 5 formularios mira, con un `useEffect`, si esa pendiente es DE SU TIPO — esta
 * función es la regla pura de esa decisión (sin React, sin efectos), para que se pueda
 * probar sin montar ningún componente.
 *
 * `ultimaAplicada` es la ÚLTIMA pendiente que ESTE formulario ya aplicó (guardada en un
 * ref por el hook de arriba, `useSeleccionPendienteDelTipo`): comparar por referencia
 * evita aplicar la misma selección dos veces si React vuelve a correr el efecto (por
 * ejemplo, el doble-render de StrictMode en desarrollo).
 *
 * @param {{seleccionPendiente: {serviceType:string}|null, serviceType: string, ultimaAplicada: object|null}} params
 * @returns {boolean} true si HAY que aplicar la pendiente ahora
 */
export function debeAplicarSeleccionPendiente({ seleccionPendiente, serviceType, ultimaAplicada }) {
  if (!seleccionPendiente) return false;
  if (seleccionPendiente.serviceType !== serviceType) return false;
  if (seleccionPendiente === ultimaAplicada) return false;
  return true;
}
