/**
 * Lógica PURA del cartel de "multa del operador" en la ficha de una reserva anulada.
 *
 * Por qué existe (spec UX "el paso de multa vive en la ficha", 2026-07-08): el backend
 * manda `reserva.operatorPenaltySituation.state` — con el estado real del paso de multa
 * (una máquina de 8 fotos posibles: None, PendingDecision, DebitNoteQueued,
 * DebitNoteFailed, DebitNoteNeedsAmountCurrency, ConfirmedNoDebitNote, Waived, Done, y
 * desde ADR-044 T1 también MultiOperatorNeedsManualReview). Antes esta decisión estaba
 * repartida en varios campos sueltos (`capabilities.operatorPenaltyOutcome`,
 * `cancelledMoneyContext === "MultaEnRevision"`, etc.) mezclados con el JSX. Estas
 * funciones son PURAS (sin JSX, sin fetch) para que el componente que arma el cartel
 * (OperatorPenaltyStepPanel) y sus tests usen la MISMA regla — nunca pueden divergir
 * entre sí.
 *
 * ADR-044 T1 (2026-07-10): una cancelación puede tener servicios de MÁS DE UN operador
 * (ADR-025), cada uno con su propia multa. El backend ahora manda ADEMÁS
 * `reserva.operatorPenaltySituations` — la MISMA foto de arriba, pero UNA POR OPERADOR.
 * En el caso de HOY (un solo operador, el 100% de los casos) esa lista trae un único
 * elemento, idéntico a `operatorPenaltySituation` — la ficha se ve EXACTAMENTE igual
 * que antes. `listaDeSituacionesMulta` es el único lugar que sabe leer esta lista con
 * su fallback al singular; el resto del módulo no debería leer `operatorPenaltySituation`
 * directo nunca más.
 *
 * Degradación segura: estas funciones asumen que SIEMPRE se les pasa una `situacion`
 * no-nula. Si `reserva.operatorPenaltySituation` no viene (DTO viejo o respuesta
 * cacheada de antes de este cambio), el componente que las usa NO debe llamarlas:
 * tiene que seguir con el comportamiento legado intacto (ver OperatorPenaltyStepPanel).
 */

// Traduce cada estado del backend a un slug corto y estable, usado tanto para armar
// el data-testid (banner-multa-<slug>) como — en algunos casos — el texto mostrado.
// Es una tabla explícita (no un cálculo con regex) para que cualquiera pueda leer de
// un vistazo qué testid le corresponde a cada estado del backend.
const SLUG_POR_ESTADO = {
  None: "none",
  PendingDecision: "pending-decision",
  DebitNoteQueued: "debit-note-queued",
  DebitNoteFailed: "debit-note-failed",
  DebitNoteNeedsAmountCurrency: "needs-amount-currency",
  ConfirmedNoDebitNote: "confirmed-no-debit-note",
  Waived: "waived",
  Done: "done",
  // ADR-044 T1 (2026-07-10): más de un operador con multa confirmada a la vez.
  MultiOperatorNeedsManualReview: "multi-operador",
};

/**
 * Slug estable para el data-testid del cartel (`banner-multa-<slug>`). Si llega un
 * estado que todavía no conocemos (versión de backend más nueva que este frontend),
 * devolvemos "desconocido" en vez de undefined — el testid nunca se rompe.
 *
 * @param {string} state
 * @returns {string}
 */
export function slugDeEstadoMulta(state) {
  return SLUG_POR_ESTADO[state] || "desconocido";
}

/**
 * Agrupa los 8 estados en las 6 "familias" visuales de la spec — cada familia arma
 * un tipo de cartel distinto (color, si tiene botón, si tiene panel inline debajo):
 *
 *   - "pregunta": PendingDecision (S1). El agente todavía no eligió. Naranja/rosa,
 *     con la pregunta "¿el operador te cobró...?" y los botones Sí/No.
 *   - "procesando": DebitNoteQueued (S2). La ND está en camino. Ámbar, solo un botón
 *     para refrescar (no hay nada que decidir, solo esperar).
 *   - "accionTrabada": DebitNoteFailed / DebitNoteNeedsAmountCurrency /
 *     ConfirmedNoDebitNote (S3/S4/S5). Algo quedó trabado y hay UNA acción puntual
 *     para destrabarlo. Naranja.
 *   - "waived": Waived (S6). Se cerró sin multa. Cartel rosa con el rastro de cuándo
 *     (y de si se deshizo), más el link de "Deshacer" para administradores.
 *   - "multiOperador": MultiOperatorNeedsManualReview (ADR-044 T1, 2026-07-10). Multa
 *     CONFIRMADA pero hay más de un operador confirmado a la vez en la misma
 *     cancelación: el cargo automático al cliente queda frenado a propósito hasta que
 *     se resuelva a mano (la ND por operador es una tanda futura). Ámbar, cartel
 *     pasivo SIN botón de emisión — solo conserva el link de "no cobró esta multa" si
 *     el backend lo habilita.
 *   - "soloLectura": None / Done (S7) o cualquier estado futuro no contemplado acá.
 *     Cartel liso, sin ninguna acción.
 *
 * @param {string} state
 * @returns {"pregunta"|"procesando"|"accionTrabada"|"waived"|"multiOperador"|"soloLectura"}
 */
export function familiaDeEstadoMulta(state) {
  if (state === "PendingDecision") return "pregunta";
  if (state === "DebitNoteQueued") return "procesando";
  if (
    state === "DebitNoteFailed" ||
    state === "DebitNoteNeedsAmountCurrency" ||
    state === "ConfirmedNoDebitNote"
  ) {
    return "accionTrabada";
  }
  if (state === "Waived") return "waived";
  if (state === "MultiOperatorNeedsManualReview") return "multiOperador";
  return "soloLectura"; // None, Done, o un estado futuro que este frontend todavia no conoce
}

/**
 * Texto pasivo del cartel de la familia "multiOperador" (ADR-044 T1, 2026-07-10). Sigue
 * las 8 reglas de voz de `docs/ux/guia-ux-gaston.md` (2026-07-08): nunca nombra "nota de
 * débito" (se dice "multa"), nunca dice "revisión manual" tal cual (se dice "a mano"),
 * y no menciona ningún nombre técnico de estado.
 *
 * Por qué NO hay botón acá: cuando dos o más operadores tienen la multa confirmada al
 * mismo tiempo, el cargo automático al cliente se frena para TODOS ellos (mismo criterio
 * que ya usa el backend para no emitir una Nota de Débito que podría estar mal
 * atribuida). Resolver esto por operador es una tanda futura (T3); por ahora el único
 * botón disponible es el link de "no cobró esta multa" (canWaive), igual que en la
 * familia "accionTrabada".
 *
 * @returns {string}
 */
export function textoMultiOperador() {
  return "Hay multas confirmadas de más de un operador en esta anulación. No se cobran solas: hay que revisarlas a mano antes de seguir.";
}

/**
 * Antepone el nombre del operador al mensaje del cartel, SOLO cuando viene informado
 * (ADR-044 T1: la ficha muestra el nombre solo si hay más de un operador en juego — en
 * el caso mono-operador de siempre, `nombreOperador` es null y el cartel se ve EXACTO
 * igual que antes de este cambio).
 *
 * @param {string|null|undefined} nombreOperador
 * @param {string} mensaje
 * @returns {string}
 */
export function tituloConNombreOperador(nombreOperador, mensaje) {
  return nombreOperador ? `${nombreOperador} — ${mensaje}` : mensaje;
}

/**
 * ADR-044 T1 (2026-07-10): normaliza a una LISTA la "foto" de multa de la reserva, sin
 * importar si el backend mandó la lista nueva (`operatorPenaltySituations`, una por
 * operador) o solo el campo singular legado (`operatorPenaltySituation`). Es el ÚNICO
 * lugar del frontend que debería leer estos dos campos directo del DTO — el resto del
 * código usa esta función para no repetir la regla de compatibilidad en cada lugar.
 *
 * Regla de preferencia: si la lista nueva viene y tiene al menos 1 elemento, se usa ESA
 * (es la fuente más completa: cubre multi-operador). Si no viene o viene vacía, cae al
 * singular legado (DTOs viejos o respuestas cacheadas de antes de ADR-044).
 *
 * @param {object} reserva - DTO de la reserva.
 * @returns {object[]} - Lista de situaciones (posiblemente vacía).
 */
export function listaDeSituacionesMulta(reserva) {
  const lista = reserva?.operatorPenaltySituations;
  if (Array.isArray(lista) && lista.length > 0) return lista;

  const singular = reserva?.operatorPenaltySituation;
  return singular ? [singular] : [];
}

/**
 * True si hay MÁS DE UN operador con multa en juego en esta cancelación (ADR-044 T1).
 * Se usa para decidir si el cartel de cada operador muestra su nombre: en el caso
 * mono-operador de siempre, no tiene sentido anteponer un nombre que antes no estaba.
 *
 * @param {object} reserva - DTO de la reserva.
 * @returns {boolean}
 */
export function hayMasDeUnOperadorConMulta(reserva) {
  return listaDeSituacionesMulta(reserva).length > 1;
}

/**
 * Filtra, de la lista completa de situaciones de multa (una por operador), las que
 * necesitan el panel accionable de la ficha (OperatorPenaltyStepPanel): familias
 * "accionTrabada" (algo quedó trabado, hay una acción puntual para destrabarlo),
 * "procesando" (se está emitiendo, solo esperar) y "multiOperador" (ADR-044 T1: más de
 * un operador confirmado a la vez, se resuelve a mano). Las familias "pregunta"
 * (PendingDecision) y "waived" tienen su propio bloque en ReservaDetailPage y no pasan
 * por acá — este componente no las duplica.
 *
 * Caso mono-operador (hoy el 100%): la lista de entrada tiene un único elemento, así
 * que esta función devuelve como máximo ESE elemento — mismo comportamiento que antes
 * de ADR-044, cuando la ficha solo miraba el campo singular.
 *
 * @param {object} reserva - DTO de la reserva.
 * @returns {object[]}
 */
export function situacionesConPanelDeMulta(reserva) {
  return listaDeSituacionesMulta(reserva).filter((situacion) => {
    const familia = familiaDeEstadoMulta(situacion.state);
    return familia === "accionTrabada" || familia === "procesando" || familia === "multiOperador";
  });
}

/**
 * Filtra, de la lista completa de situaciones de multa (una por operador), las que
 * todavía están en la familia "pregunta" (PendingDecision) Y el usuario puede
 * confirmarlas AHORA (`canConfirm`, que el backend ya combina con el permiso del
 * usuario — mismo criterio que `canRetryDebitNote` / `canCorrectAmountCurrency` /
 * `canWaive`). ReservaDetailPage usa esta lista para dibujar el bloque "¿El operador te
 * cobró una multa?" UNA VEZ POR OPERADOR (ADR-044 T1, 2026-07-10, fix de bloqueante:
 * antes había un solo bloque compartido por toda la cancelación, sin `supplierPublicId`,
 * que rebotaba con 409 apenas la anulación tenía servicios de 2+ operadores).
 *
 * Si `canConfirm` es false para un operador puntual (sin permiso, u otra precondición
 * que el backend ya evaluó), ese operador NO muestra el bloque — mismo criterio que el
 * viejo cálculo agregado, que tampoco mostraba nada cuando `allowed` era false.
 *
 * Caso mono-operador (hoy el 100%): el backend garantiza que el `canConfirm` del ÚNICO
 * elemento de la lista es el mismo valor que ya calculaba antes de ADR-044 (documentado
 * en `GetOperatorPenaltySituationsAsync` del backend, con test de paridad dedicado) —
 * la ficha se ve exactamente igual.
 *
 * @param {object} reserva - DTO de la reserva.
 * @returns {object[]}
 */
export function situacionesConPreguntaDeMulta(reserva) {
  return listaDeSituacionesMulta(reserva).filter(
    (situacion) => familiaDeEstadoMulta(situacion.state) === "pregunta" && situacion.canConfirm === true
  );
}

/**
 * Texto y acción del cartel de la familia "accionTrabada" (S3/S4/S5) — el ÚNICO lugar
 * donde se decide la copy y qué botón corresponde a cada uno de los tres estados, para
 * que el componente no tenga que repetir el switch.
 *
 * Los booleanos `canRetryDebitNote` / `canCorrectAmountCurrency` YA vienen resueltos
 * por el backend (incluyen el chequeo de permisos): si el que corresponde es false,
 * esta función devuelve `accion: null` y el componente muestra el cartel sin botón
 * (versión informativa, regla de visibilidad de la spec).
 *
 * @param {{ state: string, canRetryDebitNote: boolean, canCorrectAmountCurrency: boolean }} situacion
 * @returns {{ mensaje: string, accion: "reintentar"|"corregir"|"emitir"|null, textoBoton: string|null }}
 */
export function copyAccionTrabada({ state, canRetryDebitNote, canCorrectAmountCurrency }) {
  if (state === "DebitNoteFailed") {
    return {
      mensaje: "Anulada — el cargo de la multa al cliente no salió. Probá de nuevo.",
      accion: canRetryDebitNote ? "reintentar" : null,
      textoBoton: canRetryDebitNote ? "Reintentar" : null,
    };
  }

  if (state === "DebitNoteNeedsAmountCurrency") {
    return {
      mensaje: "Anulada — el cargo de la multa al cliente quedó trabado: falta confirmar el monto y la moneda.",
      accion: canCorrectAmountCurrency ? "corregir" : null,
      textoBoton: canCorrectAmountCurrency ? "Corregir monto y moneda" : null,
    };
  }

  if (state === "ConfirmedNoDebitNote") {
    return {
      mensaje: "Anulada — la multa está confirmada pero todavía no se le cobró al cliente.",
      accion: canRetryDebitNote ? "emitir" : null,
      textoBoton: canRetryDebitNote ? "Cobrarle la multa ahora" : null,
    };
  }

  // Nunca debería llamarse con un state fuera de los tres de arriba.
  return { mensaje: "", accion: null, textoBoton: null };
}

/**
 * Rastro textual del cierre "sin multa" (S6 Waived): cuándo se cerró, QUIÉN lo cerró y,
 * si alguna vez se deshizo, cuándo y quién lo hizo. Las fechas se formatean en es-AR
 * (día/mes/año) porque es el mismo formato que usa el resto de la ficha.
 *
 * `waivedByName` se agregó en la spec "el paso de multa vive en la ficha" (2026-07-08):
 * el backend ya lo manda en operatorPenaltySituation, pero es opcional acá (DTOs viejos
 * cacheados en el browser pueden no traerlo todavía) — sin ese dato, el texto cae al
 * formato anterior (solo la fecha), nunca se rompe.
 *
 * @param {{ waivedAt: string|null, waivedByName?: string|null, revertedAt: string|null, revertedByName: string|null }} situacion
 * @returns {string}
 */
export function textoRastroWaived({ waivedAt, waivedByName, revertedAt, revertedByName }) {
  const formatearFecha = (iso) =>
    new Date(iso).toLocaleDateString("es-AR", { day: "2-digit", month: "2-digit", year: "numeric" });

  let texto;
  if (waivedAt && waivedByName) {
    texto = `Cerrada sin multa el ${formatearFecha(waivedAt)} por ${waivedByName}`;
  } else if (waivedAt) {
    texto = `Cerrada sin multa el ${formatearFecha(waivedAt)}`;
  } else {
    texto = "Cerrada sin multa del operador.";
  }

  // Los dos campos vienen juntos o no vienen (si se deshizo, tiene que haber quién lo hizo).
  if (revertedAt && revertedByName) {
    texto += ` · deshecho el ${formatearFecha(revertedAt)} (${revertedByName})`;
  }

  return texto;
}

/**
 * True si, en la familia "accionTrabada" (S3/S4/S5 — un intento de cobro que quedó
 * trabado), hay que mostrar el link secundario "El operador no cobró esta multa".
 *
 * Por qué existe (hallazgo de Gastón, 2026-07-08): antes, cuando la multa quedaba
 * trabada, el cartel SOLO ofrecía resolver el cobro (Reintentar/Corregir/Emitir) —
 * no había salida para el caso real "el operador en realidad no cobró nada" (dato de
 * prueba, confirmación cargada por error, etc.). El backend ya resuelve TODAS las
 * condiciones de negocio en un solo booleano (`canWaive`: multa Confirmed + la ND no
 * está en juego + el usuario tiene permiso) — acá solo lo leemos, no repetimos la regla.
 *
 * @param {{ canWaive?: boolean }} situacion
 * @returns {boolean}
 */
export function debeMostrarWaiveEnAccionTrabada(situacion) {
  return situacion?.canWaive === true;
}

/**
 * True si, en el estado actual del cartel, hay que estar refrescando la situación de
 * la multa sola (sin que el agente toque nada). Bug reportado por Gastón (2026-07-08):
 * el cartel "se está emitiendo la multa, puede demorar unos minutos" quedaba TRABADO
 * para siempre aunque la ND ya se hubiera emitido del lado del backend — la base ya
 * decía "emitida" pero la pantalla nunca se enteraba, solo un F5 manual lo destrababa.
 *
 * Solo la familia "procesando" (DebitNoteQueued, la ND está en camino) necesita este
 * refresco automático: es la ÚNICA familia sin ninguna acción del agente — todas las
 * demás (pregunta, accionTrabada, waived, soloLectura) ya se refrescan solas cuando el
 * agente hace algo (clickea un botón, confirma un modal), así que pollearlas sería
 * gastar llamadas al backend sin necesidad.
 *
 * @param {"pregunta"|"procesando"|"accionTrabada"|"waived"|"soloLectura"} familia
 * @returns {boolean}
 */
export function debePollearSituacionMulta(familia) {
  return familia === "procesando";
}

/**
 * True si ya se agotó el tope prudente de espera del polling (evita pollear infinito
 * si el backend quedó realmente trabado — cola caída, worker caído, etc.). A partir de
 * acá el cartel deja de refrescarse solo y le suma al agente una línea chica para que
 * actualice la página a mano.
 *
 * Recibe el tiempo transcurrido como parámetro (no lee el reloj del sistema) para que
 * el hook que la usa (useOperatorPenaltyPolling) y sus tests puedan simular el paso del
 * tiempo sin esperas reales — mismo patrón que shouldStopPolling en useInvoicePolling.
 *
 * @param {number} elapsedMs - milisegundos transcurridos desde que arrancó el polling.
 * @param {number} maxDurationMs - tope prudente en milisegundos.
 * @returns {boolean}
 */
export function seAgotoElBudgetDePollingDeMulta(elapsedMs, maxDurationMs) {
  return elapsedMs >= maxDurationMs;
}

/**
 * True si, según la situación de la multa, sigue habiendo un paso ACTIVO que atender
 * en la ficha (cualquier estado que no sea None/Done, en CUALQUIERA de los operadores
 * en juego). Reemplaza al viejo cálculo `capabilities.operatorPenaltyOutcome ===
 * "Pending" || "Waived"` cuando el DTO ya trae situación de multa (singular o lista,
 * ver listaDeSituacionesMulta); si no trae ninguna, cae al cálculo legado (degradación
 * segura).
 *
 * ADR-044 T1 (2026-07-10): ahora recorre TODOS los operadores de la lista, no solo el
 * singular — si CUALQUIERA de ellos tiene un paso activo, la ficha tiene que mostrarlo.
 *
 * @param {object} reserva - DTO de la reserva.
 * @returns {boolean}
 */
export function tienePasoDeMultaOperador(reserva) {
  const lista = listaDeSituacionesMulta(reserva);
  if (lista.length > 0) {
    return lista.some((situacion) => situacion.state !== "None" && situacion.state !== "Done");
  }
  // Fallback legado — DTO viejo sin operatorPenaltySituation ni operatorPenaltySituations.
  const outcome = reserva?.capabilities?.operatorPenaltyOutcome;
  return outcome === "Pending" || outcome === "Waived";
}
