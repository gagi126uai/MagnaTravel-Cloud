/**
 * Lógica pura del "matcher anti-duplicados" (decisión de Gastón, 2026-08-09; ampliado por
 * la spec FIRMADA del buscador versátil, 2026-08-10, D11..D13): la IA de la ficha de carga
 * sigue sin ser una pantalla aparte — nunca se nombra, nunca hay un cartel "pensando" — pero
 * ya no es 100% invisible: cuando hay una duda GRANDE que cambia qué producto es, se ve como
 * una línea gris con ✨ (nunca la palabra "IA"); y si el vendedor tira la frase completa en
 * el buscador ("llao llao del 10/02 al 15/02 con delfos"), lo que el motor entendió de esa
 * frase (operador, fechas) se aprovecha como precarga amarilla al elegir el producto.
 *
 * Cómo se usa (ver `useProductDedupMatch.js` y `ProductSearchField.jsx`): cuando conviene
 * (ver `busquedaLocalDebil`/`pareceLineaCompleta` más abajo — el gate que evita llamar al
 * motor de más), se consulta en silencio y — si contesta algo útil — sus candidatos se
 * MEZCLAN en el mismo desplegable de toda la vida. Si el motor no contesta nada útil, la
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

// Fix #10 (auditoría de coherencia 2026-08-10): cuando la búsqueda local YA llena el
// tope (8 filas), antes los candidatos del motor quedaban afuera del todo — el
// `.slice(0, tope)` se comía la lista entera con los locales, aunque el motor hubiera
// traído algo mejor. Se reservan estos lugares para el motor SOLO cuando trajo algo;
// si no trajo nada, los locales siguen ocupando el tope completo (sin cambios).
const LUGARES_RESERVADOS_PARA_EL_MOTOR = 2;

/**
 * Agrega al final de `resultadosActuales` los candidatos del motor que TODAVÍA no
 * estaban en la lista (mismo `ratePublicId`). Nunca reordena lo que el buscador normal
 * ya trajo — la lista "solo mejora", nunca empeora ni sorprende.
 *
 * Con tope y candidatos del motor de por medio, se reservan
 * `LUGARES_RESERVADOS_PARA_EL_MOTOR` lugares para ellos (recortando los locales si hace
 * falta): con tope 8, locales llenos y motor con candidatos, quedan 6 locales + 2 del
 * motor. Si el motor no trajo nada, los locales ocupan el tope completo, como siempre.
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

  if (typeof tope !== "number") {
    return [...actuales, ...nuevos];
  }

  if (nuevos.length === 0) {
    // Sin candidatos del motor: los locales ocupan todo el tope, como siempre.
    return actuales.slice(0, tope);
  }

  // Con candidatos del motor: se les reservan lugares, recortando los locales si hace
  // falta (nunca al revés — si hay pocos locales, el motor no "roba" lugares de más).
  const topeLocales = Math.max(tope - LUGARES_RESERVADOS_PARA_EL_MOTOR, 0);
  const localesRecortados = actuales.slice(0, topeLocales);
  const lugaresParaMotor = tope - localesRecortados.length;
  const motorRecortado = nuevos.slice(0, lugaresParaMotor);
  return [...localesRecortados, ...motorRecortado];
}

// ─── El texto de "crear ..." no puede nacer con basura ──────────────────────────

// Conectores cortos que no aportan nada al NOMBRE del producto — no cuentan ni en el
// crudo ni en el limpio a la hora de medir cuánto se "conservó" (H-3, 2026-08-11).
const STOPWORDS_TEXTO_DE_CREAR = new Set(["de", "del", "la", "el", "al", "con", "a", "y", "o"]);

/**
 * Palabras que SÍ aportan al nombre de un producto: al menos 2 caracteres, no son un
 * número puro (una fecha o un precio suelto) y no son un conector corto. Se usa para
 * medir cuánto del texto crudo sobrevivió en el texto limpio que devolvió el motor.
 */
function palabrasSignificativasDeCrear(texto) {
  return (texto || "")
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .filter((palabra) => palabra.length >= 2)
    .filter((palabra) => !/^\d+$/.test(palabra))
    .filter((palabra) => !STOPWORDS_TEXTO_DE_CREAR.has(palabra.toLowerCase()));
}

/**
 * La opción "crear {texto}" del dropdown usa, por defecto, la frase cruda que escribió
 * el vendedor — que puede traer precio, fechas y operador mezclados ("sheraton iguazu
 * doble desayuno ola 48 usd del 12 al 15/9"). Si el motor entendió cuál ES el nombre del
 * producto dentro de esa frase (`productSearchText`), se usa ESE para que un alta nueva
 * no nazca con basura en el nombre.
 *
 * Fix H-3 (2026-08-11): el motor a veces "limpia de más" — con "hotel e3" devolvía solo
 * "hotel", perdiendo la parte que en realidad SÍ distingue al producto (no era ni fecha
 * ni operador, el motor simplemente no la reconoció).
 *
 * Fix Bug 3 (QA 11/08/2026): el umbral del 60% de H-3 era demasiado permisivo — con un
 * nombre corto de 3 palabras ("Hotel Robot QA"), perder UNA sola ("QA", el motor la
 * confundió con un código) sigue conservando el 66%, así que pasaba el umbral y el alta
 * nueva se ofrecía como "Hotel Robot", comiéndose una palabra real del nombre. La regla
 * ahora es más estricta: el texto limpio del motor SOLO se usa cuando hay algo legítimo
 * para limpiar — una fecha/número mezclado en la frase que el motor separó del nombre
 * ("iberostar waves del 10/02 al 15/02" → "iberostar waves"). Si el vendedor tipeó un
 * nombre de producto SUELTO, sin fecha ni número (como "Hotel Robot QA"), no hay nada
 * legítimo que limpiar — cualquier achique del motor ahí es una apuesta sobre el nombre,
 * no una limpieza real, así que se usa SIEMPRE el texto tal cual lo tipeó el vendedor.
 *
 * Con fecha/número en el crudo, se sigue exigiendo UNA de estas dos condiciones (igual
 * que H-3, sin cambios) para confiar en el limpio:
 *   1. conserva al menos el 60% de las "palabras significativas" del texto crudo
 *      (`palabrasSignificativasDeCrear` — sin conectores, sin números sueltos); o
 *   2. es una FRASE (2 o más palabras) — ahí el motor típicamente separó bien "esto es
 *      el producto" de "esto es fecha/precio", aunque la proporción no llegue al 60%
 *      (una frase larga con pocas palabras de producto real es normal).
 * Si ninguna se cumple, se usa el texto crudo tal cual — la opción segura, nunca peor
 * que lo que el vendedor mismo escribió.
 *
 * @param {string|null|undefined} productSearchText — nombre limpio que sacó el motor
 * @param {string} textoOriginal — lo que el vendedor escribió en el buscador
 */
export function resolverTextoDeCrear(productSearchText, textoOriginal) {
  const limpio = (productSearchText || "").trim();
  const crudo = (textoOriginal || "").trim();
  if (!limpio) return crudo;

  const significativasCrudo = palabrasSignificativasDeCrear(crudo);

  // Sin fecha ni importe DE VERDAD en lo que tipeó el vendedor no hay nada legítimo
  // que el motor pueda estar "limpiando" (no hay fecha/precio/operador que separar) —
  // cualquier texto más corto que el motor devuelva acá es una apuesta sobre el
  // nombre, el mismo riesgo que ya cazó H-3. Preferimos siempre lo que el vendedor
  // tipeó — pero solo cuando el crudo efectivamente TIENE palabras que proteger (si no
  // trajo ninguna palabra significativa, no hay nada que perder y seguimos de largo a
  // la lógica de siempre, más abajo).
  //
  // Fix I3 (review): /\d/.test(crudo) — CUALQUIER dígito — era demasiado ancho: un
  // nombre de producto real como "Hotel 5 Estrellas" tiene un dígito (la "5") sin ser
  // ni fecha ni precio, y caía en la rama permisiva de abajo, volviendo el mismo bug
  // del lavado que este fix arregla (perdía "Robot QA" del final). Ahora se exige que
  // el dígito tenga PINTA de fecha ("10/02", "05-03") o de importe (3+ dígitos
  // seguidos, como "48000" o "91000") — un solo dígito suelto ("5 Estrellas") no cuenta.
  const pareceFechaOImporte = /\d{1,2}[/-]\d{1,2}/.test(crudo) || /\d{3,}/.test(crudo);
  if (!pareceFechaOImporte && significativasCrudo.length > 0) return crudo;

  const significativasLimpio = palabrasSignificativasDeCrear(limpio);
  // Sin palabras significativas en el crudo (raro: solo números/conectores) no hay nada
  // que "perder" — el límite del 60% no aplica, se confía en el limpio.
  const conserva60Porciento =
    significativasCrudo.length === 0
      ? true
      : significativasLimpio.length / significativasCrudo.length >= 0.6;

  const cantidadPalabrasDelLimpio = limpio.split(/\s+/).filter(Boolean).length;
  const esCasoFrase = cantidadPalabrasDelLimpio >= 2;

  return conserva60Porciento || esCasoFrase ? limpio : crudo;
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

// ─── Cuenta de opciones navegables (la ✨ NUNCA entra acá) ───────────────────────

/**
 * Cantidad de opciones que el teclado (↑↓/Enter) puede recorrer: los resultados que se
 * ven + la opción "crear" si está visible. La línea con ✨ (spec D12, 2026-08-10) NO es
 * un parámetro de esta cuenta — a propósito: es un aviso, no una opción, así que nunca
 * puede sumar al total ni correr el índice del teclado.
 *
 * @param {{cantidadResultados:number, hayOpcionCrear:boolean}} params
 */
export function contarOpcionesNavegables({ cantidadResultados, hayOpcionCrear }) {
  return (cantidadResultados || 0) + (hayOpcionCrear ? 1 : 0);
}

// ─── Gate del matcher/motor (spec D5, 2026-08-10): llamarlo MENOS ────────────────

// Umbral de "parecido flojo" para la búsqueda LOCAL (catalog-search de siempre): un
// primer resultado con score bajo significa que el nombre tipeado no se parece bien a
// nada del tarifario — ahí SÍ vale la pena gastar una consulta al motor.
const UMBRAL_PARECIDO_FLOJO = 0.45;

/**
 * La búsqueda local (catalog-search) vino "floja": o no encontró nada, o el primer
 * resultado tiene un score bajo (no hay con qué confiar). En cualquiera de los dos
 * casos, consultar al motor puede aportar algo que el buscador de nombres solo no
 * encuentra (sinónimos, typos grandes, orden de palabras raro).
 *
 * @param {object[]} results — mismos `results` de catalog-search que ya mira `hayParecidoFuerte`
 */
export function busquedaLocalDebil(results) {
  const lista = results || [];
  if (lista.length === 0) return true;
  const primerScore = lista[0]?.score;
  return primerScore != null && primerScore < UMBRAL_PARECIDO_FLOJO;
}

// Meses en español, para detectar que el vendedor escribió una fecha en palabras
// ("15 de febrero") y no solo con números.
const MESES_EN_ESPANIOL = [
  "enero", "febrero", "marzo", "abril", "mayo", "junio",
  "julio", "agosto", "septiembre", "setiembre", "octubre", "noviembre", "diciembre",
];

// Cuántas "palabras significativas" hacen falta para que el texto cuente como frase
// completa (no una palabra suelta tipo "sheraton"). Nota de calibración: para que
// frases como "hotel de la cañada cordoba" (con conectores cortos "de"/"la") cuenten
// como completas, el largo mínimo de palabra "significativa" quedó en 2 caracteres —
// alcanza para descartar ruido de una sola letra sin descartar conectores reales.
const CANTIDAD_MINIMA_DE_PALABRAS = 4;
const LARGO_MINIMO_DE_PALABRA_SIGNIFICATIVA = 2;

/**
 * ¿El texto tipeado "parece una frase completa" (con fechas/operador mezclados), y no
 * solo el nombre suelto de un producto? Si es así, vale la pena consultar al motor
 * aunque la búsqueda local ya haya encontrado un parecido fuerte por nombre — la frase
 * puede traer, además del producto, datos (fechas, operador) que el buscador de
 * nombres nunca mira.
 *
 * Cualquiera de estas señales alcanza:
 *   - trae números (fechas tipo 10/02, precios, años);
 *   - menciona un mes en español;
 *   - tiene el patrón "del ... al ..." (rango de fechas en palabras);
 *   - tiene 4 palabras significativas o más (una frase, no un nombre suelto).
 *
 * @param {string} texto — lo que el vendedor tipeó en el buscador
 */
export function pareceLineaCompleta(texto) {
  const limpio = (texto || "").trim();
  if (!limpio) return false;

  if (/\d/.test(limpio)) return true;

  const normalizado = limpio.toLowerCase();
  if (MESES_EN_ESPANIOL.some((mes) => normalizado.includes(mes))) return true;
  if (/\bdel\b[\s\S]*\bal\b/.test(normalizado)) return true;

  const palabrasSignificativas = limpio
    .split(/\s+/)
    .filter((palabra) => palabra.length >= LARGO_MINIMO_DE_PALABRA_SIGNIFICATIVA);
  return palabrasSignificativas.length >= CANTIDAD_MINIMA_DE_PALABRAS;
}

// ─── La interpretación de la frase, para la precarga-hack (D13, 2026-08-10) ──────

// Mismo código que ServiceLineDoubtCodes.DatesYear en el backend (ver
// ServiceLineInterpretationDtos.cs) — no hay un import compartido entre API y
// frontend, así que el string literal se repite ahí y acá.
const DOUBT_CODE_ANIO_DE_FECHAS = "anioDeFechas";

/**
 * De la respuesta completa del motor (`POST /linea-inteligente`), separa SOLO lo que
 * esta obra necesita para la precarga-hack de D13: el operador y las fechas que sacó de
 * la frase. El precio SÍ puede venir armado en la respuesta (el motor lo calcula igual),
 * pero acá se ignora A PROPÓSITO: decisión de Gastón del 2026-08-10 (D13 quedó firmado
 * "sin precio en v1" — pidió textual "hotel + operador y fecha"; el precio además se
 * cruza con el permiso de ver costos, complejidad que se dejó afuera de esta vuelta).
 *
 * Devuelve `null` cuando no hay NADA utilizable, para que el llamador pueda tratarlo
 * igual que "no hay interpretación" sin mirar cada campo por separado.
 *
 * `anioAmbiguo` (fix dominio, review 11/08/2026): el backend (`ServiceLineDoubtCodes.
 * DatesYear = "anioDeFechas"`, ver ServiceLineInterpretationDtos.cs) manda esta duda
 * puntual CUANDO el motor tuvo que elegir un año porque la frase no lo traía escrito
 * ("del 05/03 al 12/03" sin decir 2026 o 2027). Si esa duda NO está presente mientras
 * SÍ hay fechas, es porque el motor está seguro del año (vino explícito en la frase, ej.
 * "del 05/03/2027 al 12/03/2027") — una carga retroactiva LEGÍTIMA (ej. cargar en agosto
 * un viaje de marzo que YA VIAJÓ) no se puede "corregir" al futuro sin pisar la intención
 * real del vendedor. `clampearFechasSugeridasAlFuturo` (inlineServiceFormHelpers.js) usa
 * esta bandera para clampear SOLO cuando el año es realmente ambiguo.
 *
 * @param {object|null} respuestaDelMotor — la respuesta cruda de `/linea-inteligente`
 * @returns {{supplier:{supplierPublicId:string,name:string}|null, dates:{from:string,to:string|null}|null, anioAmbiguo:boolean}|null}
 */
export function extraerInterpretacionParaPrecarga(respuestaDelMotor) {
  if (!respuestaDelMotor) return null;

  const supplierCrudo = respuestaDelMotor.supplier || null;
  const supplier = supplierCrudo?.supplierPublicId
    ? { supplierPublicId: supplierCrudo.supplierPublicId, name: supplierCrudo.name || null }
    : null;

  const datesCrudo = respuestaDelMotor.dates || null;
  const dates = datesCrudo?.from || datesCrudo?.to
    ? { from: datesCrudo.from || null, to: datesCrudo.to || null }
    : null;

  if (!supplier && !dates) return null;

  const anioAmbiguo = respuestaDelMotor.doubt?.code === DOUBT_CODE_ANIO_DE_FECHAS;
  return { supplier, dates, anioAmbiguo };
}

// ─── Duda de producto LOCAL, sin motor (H-1, 2026-08-11) ─────────────────────────

// Saca tildes y colapsa espacios de sobra, para que "Sheratón" y "Sheraton" (o "Hotel  X"
// con doble espacio) cuenten como el MISMO nombre al comparar.
function normalizarNombreDeProducto(nombre) {
  return (nombre || "")
    .toLowerCase()
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "") // saca los acentos (quedan solo las letras base)
    .replace(/\s+/g, " ")
    .trim();
}

/**
 * Duda de producto SIN depender del motor de IA: la pregunta "¿el de A o el de B?" antes
 * solo llegaba en la respuesta de /linea-inteligente — pero el gate `busquedaLocalDebil`
 * apaga esa consulta JUSTO cuando el buscador local ya encontró dos resultados FUERTES
 * casi iguales (score alto en los dos), que es exactamente el caso donde más hace falta
 * preguntar. Esta función mira los primeros 2 resultados que YA trajo el buscador de
 * catálogo (sin llamar a nada nuevo) y arma la misma duda que armaría el motor: mismo
 * nombre de producto, pero en dos lugares u operadores distintos.
 *
 * Regla espejo de la del backend (mismo espíritu que `esDudaDeProducto`): nombres
 * DISTINTOS nunca generan duda acá — un vendedor que ve "Sheraton Iguazú" y "Hotel Colón"
 * ya los distingue solo, no hace falta preguntarle nada.
 *
 * @param {object[]} results — mismos `results` de catalog-search que ya mira `hayParecidoFuerte`
 *   (cada fila trae `name`, `subtitle` opcional, `lastSale` opcional con `supplierName`)
 * @returns {{field:"producto", question:string}|null}
 */
export function dudaDeProductoLocal(results) {
  const lista = results || [];
  const primero = lista[0];
  const segundo = lista[1];
  if (!primero || !segundo) return null;

  const nombreA = normalizarNombreDeProducto(primero.name);
  const nombreB = normalizarNombreDeProducto(segundo.name);
  // Sin nombre, o nombres distintos: no hay duda que armar (ya se distinguen solos).
  if (!nombreA || nombreA !== nombreB) return null;

  // El "lugar" que distingue a cada fila: primero la ciudad/ruta (subtitle); si no la
  // trae, el operador de la última venta; y si tampoco (producto sin ventas), el operador
  // de la ficha del producto (rateFallback, gate 2026-08-11).
  const lugarA = primero.subtitle || primero.lastSale?.supplierName || primero.rateFallback?.supplierName;
  const lugarB = segundo.subtitle || segundo.lastSale?.supplierName || segundo.rateFallback?.supplierName;
  if (!lugarA || !lugarB || lugarA === lugarB) return null;

  return {
    field: "producto",
    question: `¿${primero.name} de ${lugarA} o el de ${lugarB}?`,
  };
}

// ─── La ✨ es SOLO para la duda de PRODUCTO (fix C-4, review 2026-08-10) ──────────

/**
 * El motor emite 4 tipos de duda (`ServiceLineDoubtCodes` en el backend):
 * "productoAmbiguo" (¿cuál de los dos Panamericanos es?), "precioPorNoche",
 * "operadorAmbiguo" y "anioDeFechas". La línea con ✨ del desplegable (D11/D12) es
 * SOLO para la primera — las otras tres son dudas sobre un DATO ya cargado y tienen su
 * propio mecanismo firmado (D12-bis: debajo del campo, con Sí/No), que NO es esta obra.
 * Mostrarlas acá sería inventar una segunda pantalla para algo que ya tiene la suya.
 *
 * @param {{field?:string}|null|undefined} duda — el `Doubt` tal cual lo manda el motor
 */
export function esDudaDeProducto(duda) {
  return Boolean(duda && duda.field === "producto");
}

// ─── Cuándo se pinta la ✨ en pantalla (fix C-6, review 2026-08-10) ───────────────

/**
 * Decide si corresponde pintar la línea con ✨ en ESTE render. Además de ser una duda
 * de producto (`esDudaDeProducto`) y de no estar buscando, tiene que NO haber sido
 * descartada por el vendedor: D12 dice "no queda pegada después de elegir, ni reaparece
 * al volver al campo" — si el vendedor la cerró con Esc o perdió el foco (blur), un
 * refoco NO puede resucitarla aunque `dedupResult` siga teniendo la misma duda adentro
 * (nada la invalida hasta que el texto cambie). `dudaDescartada` es ese flag: lo maneja
 * `ProductSearchField.jsx` con un ref (se prende en Esc/blur, se apaga al seguir
 * tipeando) — acá solo se USA la decisión, sin acoplarse a cómo se guarda.
 *
 * Fix #9 (auditoría de coherencia 2026-08-10): tampoco corresponde mostrarla si YA hay
 * un producto vinculado (`hayProductoVinculado`, `rateId` seteado) — la identidad ya
 * está resuelta, así que cualquier duda que hubiera quedado dando vueltas (ej. un
 * refoco después de elegir) está obsoleta.
 *
 * @param {{duda:object|null, isSearching:boolean, dudaDescartada:boolean, hayProductoVinculado?:boolean}} params
 */
export function debeMostrarDuda({ duda, isSearching, dudaDescartada, hayProductoVinculado }) {
  if (isSearching) return false;
  if (dudaDescartada) return false;
  if (hayProductoVinculado) return false;
  return esDudaDeProducto(duda);
}
