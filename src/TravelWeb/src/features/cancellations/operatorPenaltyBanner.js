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

import { debeMostrarReintentarDeshacer } from "./lib/undoDebitNoteLogic.js";

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
  // ADR-044 "Deshacer una multa ya emitida" (2026-07-14): la ND con CAE se está anulando
  // (se emite la NC espejo que la deja sin efecto) y, si eso falla, queda trabado.
  DebitNoteAnnulling: "debit-note-annulling",
  DebitNoteAnnulmentFailed: "debit-note-annulment-failed",
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
 * Agrupa los estados en las 7 "familias" visuales de la spec — cada familia arma
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
 *   - "confirmada": Done (S7, ADR-044 T4, 2026-07-10). La multa quedó resuelta de punta
 *     a punta (ND emitida con CAE). Antes este estado cofundía en "soloLectura" y la
 *     ficha no mostraba NADA del cargo confirmado — con T4 pasa a tener su propio
 *     cartel prolijo ("Multa del operador: US$ 200 ✔ confirmada") que además habilita
 *     el link "+ Agregar otro cargo de este operador" (spec sección 1: el mismo
 *     operador a veces aplica un cargo administrativo Y una retención fiscal juntos) y,
 *     desde ADR-044 "Deshacer una multa ya emitida" (2026-07-14), el link "Deshacer:
 *     el operador cobró mal esta multa" (Admin ÚNICAMENTE — ver `debeMostrarReintentarDeshacer`
 *     en lib/undoDebitNoteLogic.js, nunca alcanza con canUndoDebitNote solo).
 *   - "soloLectura": None, o cualquier estado futuro no contemplado acá. Sin cartel de
 *     multa (la ficha no tiene nada que mostrar de este paso).
 *
 * ADR-044 "Deshacer una multa ya emitida" (2026-07-14): dos estados nuevos REUSAN
 * familias que ya existían (nunca se creó una familia visual nueva para esto):
 *   - DebitNoteAnnulling (se está emitiendo la NC que deja sin efecto la ND) entra en
 *     "procesando" — mismo cartel ámbar con auto-refresco que "se está emitiendo la
 *     multa al cliente" (DebitNoteQueued), solo cambia el texto (ver tituloProcesando).
 *   - DebitNoteAnnulmentFailed (esa NC no se pudo emitir) entra en "accionTrabada" —
 *     mismo cartel naranja con un botón puntual ("Reintentar") que los otros tres
 *     estados trabados.
 *
 * @param {string} state
 * @returns {"pregunta"|"procesando"|"accionTrabada"|"waived"|"multiOperador"|"confirmada"|"soloLectura"}
 */
export function familiaDeEstadoMulta(state) {
  if (state === "PendingDecision") return "pregunta";
  if (state === "DebitNoteQueued" || state === "DebitNoteAnnulling") return "procesando";
  if (
    state === "DebitNoteFailed" ||
    state === "DebitNoteNeedsAmountCurrency" ||
    state === "ConfirmedNoDebitNote" ||
    state === "DebitNoteAnnulmentFailed"
  ) {
    return "accionTrabada";
  }
  if (state === "Waived") return "waived";
  if (state === "MultiOperatorNeedsManualReview") return "multiOperador";
  if (state === "Done") return "confirmada";
  return "soloLectura"; // None, o un estado futuro que este frontend todavia no conoce
}

/**
 * Título en negrita del cartel de la familia "procesando" (spec "Deshacer una multa ya
 * emitida", sección 3a, 2026-07-14). Esta familia ahora cubre DOS situaciones de fondo
 * distintas que comparten el mismo cartel ámbar con auto-refresco (ver
 * useOperatorPenaltyPolling): la emisión NORMAL de la multa (DebitNoteQueued, texto de
 * siempre) y la anulación de una multa YA emitida (DebitNoteAnnulling, texto nuevo). El
 * resto del cartel (auto-refresco, línea de "¿tarda mucho?", link de otro cargo) no
 * cambia entre los dos casos — solo este título.
 *
 * @param {string} state
 * @returns {string}
 */
export function tituloProcesandoMulta(state) {
  if (state === "DebitNoteAnnulling") {
    return "Anulada — se está dejando sin efecto la multa.";
  }
  return "Anulada — se está emitiendo la multa al cliente.";
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
 * "procesando" (se está emitiendo, solo esperar), "multiOperador" (ADR-044 T1: más de
 * un operador confirmado a la vez, se resuelve a mano) y "confirmada" (ADR-044 T4,
 * 2026-07-10: la multa ya quedó resuelta del todo — antes la ficha no mostraba NADA acá;
 * ahora aparece el cartel prolijo con el monto confirmado + el link "Agregar otro
 * cargo"). Las familias "pregunta" (PendingDecision) y "waived" tienen su propio bloque
 * en ReservaDetailPage y no pasan por acá — este componente no las duplica.
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
    return (
      familia === "accionTrabada" ||
      familia === "procesando" ||
      familia === "multiOperador" ||
      familia === "confirmada"
    );
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
 * ADR-044 T4 (2026-07-10): true si, de la lista de cargos de un operador, hay al menos
 * uno TRASLADABLE (Kind != "Withholding" — las retenciones nunca emiten renglón de ND,
 * así que nunca necesitan factura destino) que todavía no tiene factura destino
 * resuelta. Es la señal que distingue, dentro del estado compartido
 * "DebitNoteNeedsAmountCurrency", el caso NUEVO "falta elegir la factura" (ADR-044 T3b)
 * del caso VIEJO "falta confirmar monto y moneda" (ADR-013/014) — el backend no agregó
 * un token de estado nuevo a propósito (ver XML-doc de `OperatorPenaltySituationDto.
 * ManualReviewReason`): esta función deriva cuál es cuál a partir del desglose de cargos
 * que YA viaja en el DTO.
 *
 * @param {Array<{kind: string, targetInvoicePublicId: string|null}>} charges
 * @returns {boolean}
 */
export function hayCargoTrasladableSinFacturaDestino(charges) {
  return (Array.isArray(charges) ? charges : []).some(
    (cargo) => cargo.kind !== "Withholding" && cargo.targetInvoicePublicId == null
  );
}

/**
 * El primer cargo trasladable sin factura destino resuelta (ver
 * `hayCargoTrasladableSinFacturaDestino`) — es el que hay que corregir con el
 * desplegable de "Elegir la factura" (PATCH .../operator-charges/{chargePublicId}/target-invoice).
 *
 * @param {Array<{kind: string, targetInvoicePublicId: string|null, publicId: string}>} charges
 * @returns {object|undefined}
 */
export function primerCargoTrasladableSinFacturaDestino(charges) {
  return (Array.isArray(charges) ? charges : []).find(
    (cargo) => cargo.kind !== "Withholding" && cargo.targetInvoicePublicId == null
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
 * ADR-044 T4 (2026-07-10): "DebitNoteNeedsAmountCurrency" cubre DOS casos reales
 * distintos que comparten el mismo token de estado: "falta elegir la factura" (nuevo,
 * T3b) y "falta confirmar monto y moneda" (viejo, ADR-013/014, comportamiento PRE-T4
 * SIN CAMBIOS). La única señal para distinguirlos es ESTRUCTURAL —
 * `hayCargoTrasladableSinFacturaDestino(charges)` — nunca un match de texto sobre
 * `manualReviewReason`: ese campo es texto libre saneado por el backend y NUNCA se
 * usa para decidir ramas ni se interpola en el cartel.
 *
 * FIX F3 (gate de exposición, 2026-07-10): la rama "elegirFactura" usa SIEMPRE el copy
 * FIJO de la spec, nunca `manualReviewReason` — aunque el backend documenta que ese
 * campo solo trae este mensaje puntual en este caso, el frontend no debe DEPENDER de
 * esa promesa (defensa en profundidad: un texto técnico que se colara ahí quedaría
 * expuesto al usuario). El parámetro se sigue recibiendo (por si en el futuro hace
 * falta para otro propósito, ej. un detalle técnico solo-admin) pero NO se renderiza.
 *
 * ADR-044 "Deshacer una multa ya emitida" (2026-07-14): se suma el cuarto estado
 * trabado, "DebitNoteAnnulmentFailed" — la NC que iba a dejar sin efecto la ND no se
 * pudo emitir. El botón "Reintentar" es la MISMA acción que el link "Deshacer" de la
 * familia "confirmada" (solo que reintentada), así que se gatea con la MISMA función
 * `debeMostrarReintentarDeshacer` — nunca con `canRetryDebitNote`, que es un permiso
 * DISTINTO (el de reintentar la EMISIÓN de la multa, no el de deshacerla).
 *
 * FIX BLOQUEANTE B1 (gate de seguridad, revisión 2026-07-14): antes de este fix, acá se
 * gateaba SOLO con `canUndoDebitNote` — un usuario sin rol Admin (pero con el permiso
 * `cancellations.classify_agency_penalty`) podía ver el botón y completar el deshacer.
 * Deshacer un comprobante con CAE es **Admin únicamente** (misma regla que "Deshacer"
 * del cierre sin multa): por eso ahora también se exige `esAdmin` — ver
 * `debeMostrarReintentarDeshacer` (undoDebitNoteLogic.js) para el detalle completo.
 *
 * @param {{ state: string, canRetryDebitNote: boolean, canCorrectAmountCurrency: boolean, canUndoDebitNote?: boolean, esAdmin?: boolean, manualReviewReason?: string|null, charges?: Array<object> }} situacion
 * @returns {{ mensaje: string, accion: "reintentar"|"corregir"|"emitir"|"elegirFactura"|"reintentarDeshacer"|null, textoBoton: string|null }}
 */
// eslint-disable-next-line no-unused-vars -- manualReviewReason se acepta a propósito pero NUNCA se interpola (ver comentario de arriba, FIX F3).
export function copyAccionTrabada({ state, canRetryDebitNote, canCorrectAmountCurrency, canUndoDebitNote, esAdmin, manualReviewReason, charges }) {
  if (state === "DebitNoteFailed") {
    return {
      mensaje: "Anulada — el cargo de la multa al cliente no salió. Probá de nuevo.",
      accion: canRetryDebitNote ? "reintentar" : null,
      textoBoton: canRetryDebitNote ? "Reintentar" : null,
    };
  }

  if (state === "DebitNoteAnnulmentFailed") {
    // FIX B1: unifica la puerta con la del link "Deshacer" de la familia "confirmada" —
    // NUNCA alcanza con canUndoDebitNote solo, hace falta también esAdmin.
    const puedeReintentarDeshacer = debeMostrarReintentarDeshacer({ canUndoDebitNote, esAdmin });
    return {
      mensaje: "Anulada — no se pudo dejar sin efecto la multa. Probá de nuevo.",
      accion: puedeReintentarDeshacer ? "reintentarDeshacer" : null,
      textoBoton: puedeReintentarDeshacer ? "Reintentar" : null,
    };
  }

  if (state === "DebitNoteNeedsAmountCurrency") {
    if (hayCargoTrasladableSinFacturaDestino(charges)) {
      return {
        // FIX F3: copy FIJO, nunca manualReviewReason (defensa en profundidad).
        mensaje: "Anulada — el cargo de la multa al cliente quedó trabado: falta elegir a qué factura corresponde.",
        accion: canCorrectAmountCurrency ? "elegirFactura" : null,
        textoBoton: canCorrectAmountCurrency ? "Elegir la factura" : null,
      };
    }
    // Comportamiento PRE-T4 EXACTO para cualquier otro motivo bajo este mismo estado
    // (incluido "no podemos distinguir" — charges vacío/ausente, registros legacy sin
    // el desglose de ADR-044): "Corregir monto y moneda" sigue siendo la ÚNICA causa
    // conocida de este estado antes de T3b, así que seguimos ofreciéndola sin cambios.
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

// ============================================================================
// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): aviso de plazo RG 4540 + rastro.
// ============================================================================

const MS_POR_DIA = 24 * 60 * 60 * 1000;
// RG 4540 (AFIP/ARCA): 15 días corridos desde que se aprobó el CAE de la ND para poder
// deshacerla "sin trámites extra". Pasado ese plazo, el sistema SIGUE dejando deshacer
// (nunca bloquea, ver sección 5 de la spec) pero avisa con un tono más fuerte.
const PLAZO_RG4540_DIAS = 15;

/**
 * Formatea una fecha en "DD/MM" (sin año) — el mismo formato corto que usan los
 * mockups de la spec ("vence el 20/07", "el 14/07"). Es DISTINTO del formato largo
 * "DD/MM/AAAA" que usa `textoRastroWaived`: acá no hace falta el año porque el plazo
 * de 15 días nunca cruza a un año distinto del de la emisión.
 *
 * OJO (verificado a mano): `toLocaleDateString("es-AR", {day:"2-digit", month:"2-digit"})`
 * SIN pedir también el año NO rellena el mes con cero en este entorno (da "14/7" en vez
 * de "14/07") — es una rareza puntual de la selección de patrón de Intl/CLDR para ese
 * combo de opciones, no un bug de tipeo. Para no depender de esa rareza, se arma el
 * string a mano con padStart (mismo criterio que `fechaLocalInput` en otroCargoOperador.js).
 *
 * @param {string|number} fechaOISO
 * @returns {string}
 */
function formatearFechaCorta(fechaOISO) {
  const fecha = new Date(fechaOISO);
  const dia = String(fecha.getDate()).padStart(2, "0");
  const mes = String(fecha.getMonth() + 1).padStart(2, "0");
  return `${dia}/${mes}`;
}

/**
 * Aviso SUAVE (nunca bloquea, spec sección 5) del plazo de 15 días corridos de la RG
 * 4540 para deshacer una Nota de Débito ya emitida sin trámites extra ante ARCA. Vive
 * en el panel de confirmación de "Deshacer", entre la explicación y el campo de motivo.
 *
 * Regla "el front no deduce, lo dice el backend" (2026-07-03): el backend manda la
 * fecha CRUDA en que se aprobó el CAE (`debitNoteIssuedAt`); esta función es la ÚNICA
 * que calcula los días a partir de esa fecha — nadie más en el frontend debería repetir
 * esta cuenta. Recibe "ahora" como parámetro (no lee el reloj del sistema) para que el
 * componente y sus tests puedan fijar una fecha exacta sin esperas reales — mismo
 * patrón que `seAgotoElBudgetDePollingDeMulta`.
 *
 * Interpretación del borde EXACTO (al cumplirse el día 15 completo): se considera
 * plazo VENCIDO (tono fuerte). Es la lectura más conservadora — en un tema fiscal es
 * mejor avisar de más (un día antes de lo estrictamente necesario) que de menos.
 *
 * @param {string|null|undefined} debitNoteIssuedAtIso - Fecha ISO en que ARCA aprobó
 *   el CAE de la ND (`OperatorPenaltySituationDto.DebitNoteIssuedAt`). Null/undefined
 *   si el backend no la manda (BC legacy, u otro estado que no sea "Done") — en ese
 *   caso no hay nada que avisar.
 * @param {number} ahoraMs - `Date.now()` del momento en que se calcula el aviso.
 * @returns {{ tono: "suave"|"fuerte", texto: string }|null} - null cuando no corresponde
 *   mostrar nada (sin fecha, o fecha inválida) — el llamador no debe inventar el aviso.
 */
export function calcularAvisoPlazoDeshacerMulta(debitNoteIssuedAtIso, ahoraMs) {
  if (!debitNoteIssuedAtIso) return null;

  const emitidoMs = new Date(debitNoteIssuedAtIso).getTime();
  if (Number.isNaN(emitidoMs)) return null; // fecha invalida (defensivo) -> no se inventa nada.

  const diasTranscurridos = Math.floor((ahoraMs - emitidoMs) / MS_POR_DIA);
  const diasRestantes = PLAZO_RG4540_DIAS - diasTranscurridos;

  if (diasRestantes > 0) {
    const vencimientoMs = emitidoMs + PLAZO_RG4540_DIAS * MS_POR_DIA;
    return {
      tono: "suave",
      texto: `Quedan ${diasRestantes} día${diasRestantes === 1 ? "" : "s"} para hacer esta ` +
        `corrección sin trámites extra ante ARCA (vence el ${formatearFechaCorta(vencimientoMs)}).`,
    };
  }

  // Texto EXACTO elegido por Gastón (2026-07-14, P2) — no se parafrasea ni se interpola nada acá.
  return {
    tono: "fuerte",
    texto: "Pasaron más de 15 días desde que se emitió este comprobante. Se puede deshacer " +
      "igual, pero convendría consultarlo con un contador antes de seguir.",
  };
}

/**
 * Rastro textual del ÚLTIMO "Deshacer" de una Nota de Débito de multa (spec sección 4,
 * mismo patrón que `textoRastroWaived`). Se muestra debajo del cartel cuando el paso de
 * la multa vuelve a quedar resuelto (confirmado de nuevo, o reabierto en "pregunta")
 * después de haberse deshecho una vez.
 *
 * Backend (2026-07-14): `OperatorPenaltySituationDto.lastDebitNoteUndo` — objeto
 * `{ undoneAt, undoneByName, reason }` cuando la ND de este operador se deshizo alguna
 * vez, o `null` si nunca se deshizo. Se recibe el objeto TAL CUAL viene del DTO (no se
 * desarma antes de llamar a esta función) para no repetir el nombre de los campos en
 * cada lugar que la usa.
 *
 * @param {{ undoneAt: string, undoneByName: string|null, reason: string }|null|undefined} lastDebitNoteUndo
 * @returns {string|null}
 */
export function textoRastroDeshacerMulta(lastDebitNoteUndo) {
  if (!lastDebitNoteUndo?.undoneAt) return null;
  const { undoneAt, undoneByName, reason } = lastDebitNoteUndo;

  // Base: siempre la fecha. Después se le suma "por Fulano" y/o el motivo entre
  // comillas, solo si vinieron — nunca se inventa ninguno de los dos (spec sección 4).
  let texto = `El comprobante anterior se dejó sin efecto el ${formatearFechaCorta(undoneAt)}`;
  if (undoneByName) {
    texto += ` por ${undoneByName}`;
  }
  if (reason) {
    texto += ` — motivo: "${reason}"`;
  } else {
    // Sin motivo, se corta acá (después de la fecha, o de "por Fulano") con un punto final.
    texto += ".";
  }
  return texto;
}
