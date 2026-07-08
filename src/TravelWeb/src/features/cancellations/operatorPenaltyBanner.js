/**
 * Lógica PURA del cartel de "multa del operador" en la ficha de una reserva anulada.
 *
 * Por qué existe (spec UX "el paso de multa vive en la ficha", 2026-07-08): el backend
 * ahora manda UN SOLO campo — `reserva.operatorPenaltySituation.state` — con el estado
 * real del paso de multa (una máquina de 7 fotos posibles: None, PendingDecision,
 * DebitNoteQueued, DebitNoteFailed, DebitNoteNeedsAmountCurrency, ConfirmedNoDebitNote,
 * Waived, Done). Antes esta decisión estaba repartida en varios campos sueltos
 * (`capabilities.operatorPenaltyOutcome`, `cancelledMoneyContext === "MultaEnRevision"`,
 * etc.) mezclados con el JSX. Estas funciones son PURAS (sin JSX, sin fetch) para que
 * el componente que arma el cartel (OperatorPenaltyStepPanel) y sus tests usen la MISMA
 * regla — nunca pueden divergir entre sí.
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
 * Agrupa los 7 estados en las 5 "familias" visuales de la spec — cada familia arma
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
 *   - "soloLectura": None / Done (S7) o cualquier estado futuro no contemplado acá.
 *     Cartel liso, sin ninguna acción.
 *
 * @param {string} state
 * @returns {"pregunta"|"procesando"|"accionTrabada"|"waived"|"soloLectura"}
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
  return "soloLectura"; // None, Done, o un estado futuro que este frontend todavia no conoce
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
 * True si, según la situación de la multa, sigue habiendo un paso ACTIVO que atender
 * en la ficha (cualquier estado que no sea None/Done). Reemplaza al viejo cálculo
 * `capabilities.operatorPenaltyOutcome === "Pending" || "Waived"` cuando el DTO ya trae
 * `operatorPenaltySituation`; si no lo trae, cae al cálculo legado (degradación segura).
 *
 * @param {object} reserva - DTO de la reserva.
 * @returns {boolean}
 */
export function tienePasoDeMultaOperador(reserva) {
  const situacion = reserva?.operatorPenaltySituation;
  if (situacion) {
    return situacion.state !== "None" && situacion.state !== "Done";
  }
  // Fallback legado — DTO viejo sin operatorPenaltySituation.
  const outcome = reserva?.capabilities?.operatorPenaltyOutcome;
  return outcome === "Pending" || outcome === "Waived";
}
