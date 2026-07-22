/**
 * Lógica pura del "Cartel emergente" (spec docs/ux/2026-07-22-tratamiento-unico-avisos-bloqueo.md):
 * decide el título corto y el texto del botón de salida según la GRAVEDAD del aviso, nunca
 * según el caso puntual (el caso lo cuenta el mensaje del motor, que se muestra tal cual).
 *
 * Vive en un .js sin JSX (mismo patrón que serviceCancellationGuard.js /
 * anularReservaRechazoLogic.js) para poder testearlo con Node puro, sin bundler:
 *   node --test src/lib/cartelEmergenteLogic.test.mjs
 */

// Las dos "gravedades" (trajes) que define la spec. Un mismo componente, dos pintas.
export const CARTEL_EMERGENTE_VARIANTES = Object.freeze({
  BLOQUEO: "bloqueo",
  CONFIRMACION: "confirmacion",
});

// Títulos genéricos por tipo (spec P3=A, respuesta firmada de Gastón 2026-07-22): el título
// NUNCA cuenta el caso puntual (eso lo hace el mensaje del motor) — es solo una etiqueta de
// "qué tipo de aviso es esto", para leerlo de un vistazo.
const TITULO_POR_VARIANTE = Object.freeze({
  [CARTEL_EMERGENTE_VARIANTES.BLOQUEO]: "No se puede todavía",
  [CARTEL_EMERGENTE_VARIANTES.CONFIRMACION]: "Confirmá antes de seguir",
});

// Texto del botón secundario (el que cierra sin resolver nada) por gravedad — spec 3.1.
const TEXTO_BOTON_SECUNDARIO_POR_VARIANTE = Object.freeze({
  [CARTEL_EMERGENTE_VARIANTES.BLOQUEO]: "Entendido",
  [CARTEL_EMERGENTE_VARIANTES.CONFIRMACION]: "Volver",
});

/**
 * Título corto de arriba del cartel. Si el llamador pasa un título propio (caso raro, casi
 * ningún aviso de la app lo necesita) se respeta; si no, cae al genérico de la gravedad.
 *
 * @param {"bloqueo"|"confirmacion"} variante
 * @param {string|null|undefined} tituloPersonalizado
 * @returns {string}
 */
export function resolverTituloCartelEmergente(variante, tituloPersonalizado) {
  if (tituloPersonalizado && tituloPersonalizado.trim()) return tituloPersonalizado.trim();
  return TITULO_POR_VARIANTE[variante] ?? TITULO_POR_VARIANTE[CARTEL_EMERGENTE_VARIANTES.BLOQUEO];
}

/**
 * Texto del botón secundario (el que siempre está: "Entendido" en bloqueo, "Volver" en
 * confirmación). Igual que el título, se puede pisar puntualmente (por ejemplo "Volver a
 * corregir" en el aviso de costo, que además mueve el foco al campo — más específico que el
 * genérico "Volver").
 *
 * @param {"bloqueo"|"confirmacion"} variante
 * @param {string|null|undefined} textoPersonalizado
 * @returns {string}
 */
export function resolverTextoBotonSecundario(variante, textoPersonalizado) {
  if (textoPersonalizado && textoPersonalizado.trim()) return textoPersonalizado.trim();
  return TEXTO_BOTON_SECUNDARIO_POR_VARIANTE[variante] ?? TEXTO_BOTON_SECUNDARIO_POR_VARIANTE[CARTEL_EMERGENTE_VARIANTES.BLOQUEO];
}
