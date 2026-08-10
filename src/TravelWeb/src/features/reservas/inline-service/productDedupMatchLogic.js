/**
 * Lógica pura del "matcher anti-duplicados" (decisión de Gastón, 2026-08-09): la IA de la
 * ficha de carga deja de ser una ayuda visible ("línea inteligente" con amarillos y
 * dudas — esa obra se revirtió entera) y pasa a ser INVISIBLE, con un solo objetivo:
 * evitar que se cree un producto duplicado en el tarifario (P7, "prevención de
 * repetidos como prioridad absoluta").
 *
 * Cómo se usa (ver `useProductDedupMatch.js` y `ProductSearchField.jsx`): cuando el
 * buscador normal del catálogo NO encuentra un parecido fuerte, se consulta al motor en
 * silencio y — si contesta algo útil — sus candidatos se MEZCLAN en el mismo desplegable
 * de toda la vida. El vendedor nunca se entera de que hubo una consulta extra: no hay
 * amarillo, no hay "pensando", no hay pregunta. Si el motor no contesta nada útil, la
 * pantalla es exactamente la de siempre.
 */

// ─── Cuándo disparar la consulta ────────────────────────────────────────────────

// 2 palabras alcanza acá (a diferencia del disparador de 3 palabras que tenía la línea
// inteligente vieja): el matcher no arma nada, solo evita duplicados — conviene que
// actúe apenas el vendedor escribió lo mínimo como para que un "parecido" tenga sentido.
const MIN_PALABRAS_PARA_DISPARAR = 2;

/**
 * @param {string} texto — lo que el vendedor escribió en el buscador de producto
 * @returns {boolean} true si hay palabras suficientes para justificar la consulta
 */
export function debeDispararDedupMatch(texto) {
  if (!texto) return false;
  const palabras = texto.trim().split(/\s+/).filter(Boolean);
  return palabras.length >= MIN_PALABRAS_PARA_DISPARAR;
}

/**
 * El matcher solo tiene sentido cuando el buscador normal del catálogo TODAVÍA no
 * encontró un parecido fuerte — si ya lo encontró, no hace falta gastar cuota del motor
 * (el buscador de siempre ya está resolviendo bien la búsqueda).
 *
 * @param {object[]} resultadosDelCatalogo — los `results` tal cual los devuelve
 *   GET /rates/catalog-search (mismo array que ya pinta ProductSearchField)
 * @param {number} umbralDeMatchFuerte — mismo STRONG_MATCH_THRESHOLD que ya usa
 *   ProductSearchField para resaltar el primer resultado
 */
export function hayParecidoFuerte(resultadosDelCatalogo, umbralDeMatchFuerte) {
  const primero = (resultadosDelCatalogo || [])[0];
  if (!primero) return false;
  // Mismo criterio que ya pinta el primer resultado como "el más parecido" en el
  // dropdown: score ausente (el backend no lo mandó) cuenta como fuerte, igual que hoy.
  return primero.score == null || primero.score >= umbralDeMatchFuerte;
}

// ─── Degradación total ──────────────────────────────────────────────────────────

/**
 * Decide si una respuesta del motor trae algo utilizable. Cualquier cosa que no sea
 * "entendí" (interpreted !== true) se trata igual que un error de red o el motor caído:
 * el buscador de siempre sigue andando, sin cartel ni texto distinto.
 */
export function esRespuestaUtilizable(dto) {
  return Boolean(dto && dto.interpreted === true);
}

// ─── Mezclar candidatos sin duplicar lo que ya está ─────────────────────────────

/**
 * Agrega al final de `resultadosActuales` los candidatos del motor que TODAVÍA no
 * estaban en la lista (mismo `ratePublicId`). Nunca reordena ni saca nada de lo que el
 * buscador normal ya trajo — la lista "solo mejora", nunca empeora ni sorprende.
 *
 * @param {object[]} resultadosActuales — resultados del buscador normal (catalog-search)
 * @param {object[]} candidatosDelMotor — `productCandidates` de la respuesta del matcher
 * @param {number} tope — cantidad máxima de filas a devolver (mismo cap que ya usa el dropdown)
 */
export function mergearCandidatosDedup(resultadosActuales, candidatosDelMotor, tope) {
  const actuales = resultadosActuales || [];
  const candidatos = candidatosDelMotor || [];
  const yaVistos = new Set(actuales.map((r) => r?.ratePublicId).filter(Boolean));

  const nuevos = candidatos.filter((candidato) => {
    const id = candidato?.ratePublicId;
    // Sin id no hay forma de saber si ya está en la lista — mejor no agregarlo que
    // arriesgar una fila duplicada al vendedor.
    if (!id || yaVistos.has(id)) return false;
    yaVistos.add(id);
    return true;
  });

  const mezclados = [...actuales, ...nuevos];
  return typeof tope === "number" ? mezclados.slice(0, tope) : mezclados;
}

// ─── El texto de "crear ..." no puede nacer con basura ──────────────────────────

/**
 * La opción "crear {texto}" del dropdown usa, por defecto, la frase cruda que escribió
 * el vendedor — que puede traer precio, fechas y operador mezclados ("sheraton iguazu
 * doble desayuno ola 48 usd del 12 al 15/9"). Si el motor entendió cuál ES el nombre del
 * producto dentro de esa frase (`productSearchText`), se usa ESE para que un alta nueva
 * no nazca con basura en el nombre. Sin esa ayuda, el texto de crear sigue siendo el de
 * siempre: la frase tal cual la escribió el vendedor.
 *
 * @param {string|null|undefined} productSearchText — nombre limpio que sacó el motor
 * @param {string} textoOriginal — lo que el vendedor escribió en el buscador
 */
export function resolverTextoDeCrear(productSearchText, textoOriginal) {
  const limpio = (productSearchText || "").trim();
  if (limpio) return limpio;
  return (textoOriginal || "").trim();
}

// ─── No sorprender a quien está navegando el dropdown con el teclado ───────────

/**
 * Bug bloqueante (revisor funcional): si el matcher aterriza una respuesta MIENTRAS el
 * vendedor tiene el dropdown navegado con las flechas (`keyboardIndex >= 0`), la lista
 * no puede crecer debajo de sus dedos — el índice que apuntaba a "crear" pasaría a
 * apuntar a un producto existente, y un Enter rápido lo elegiría por error.
 *
 * Esta función es la ÚNICA fuente de verdad de esa decisión — la usa
 * `ProductSearchField.jsx` de verdad (no una copia) para elegir entre la lista
 * "congelada" (guardada en un ref, tal cual estaba antes de arrancar a navegar) y la
 * lista fresca (derivada en cada render con `useMemo`, sin estado ni efecto — así
 * `hasNoResults`/`totalOptions` siempre leen la MISMA lista que se pinta, en el mismo
 * render, sin fotogramas atrasados).
 *
 * @param {{keyboardIndex:number, listaCongelada:object[], listaFresca:object[]}} params
 * @returns {object[]} la lista que corresponde mostrar/navegar en este render
 */
export function resolverListaParaMostrar({ keyboardIndex, listaCongelada, listaFresca }) {
  if (keyboardIndex >= 0) return listaCongelada || [];
  return listaFresca || [];
}
