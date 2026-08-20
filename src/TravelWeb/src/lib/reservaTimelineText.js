/**
 * Traducciones puntuales para el Historial de la reserva (tab "Historial" de la ficha,
 * componente ReservaTimeline.jsx) — hallazgo #5 del barrido de PROD 2026-07-24.
 *
 * El backend (TimelineService.cs) arma cada línea del historial en español ("Alta de
 * un Pago", "• **Importe**: $150,00"), pero el VALOR del campo Método de pago es el
 * token crudo que guardó el formulario de cobro en su momento — puede venir en inglés
 * legado ("Transfer") si el pago es viejo, o directamente ser un valor que el frontend
 * no reconoce (ej. "Other"). Ese es el único lugar donde puede colarse texto crudo/en
 * inglés en el historial: el resto de los textos ya vienen en criollo.
 */

import { traducirMetodoPago } from "../features/customers/lib/paymentHelpers.js";
import { traducirEstadoReserva } from "../features/reservas/lib/reservaStatusLabels.js";
import { formatCurrency, aHoraArgentina } from "./utils.js";

// Formato que arma TimelineService.cs para Modificación de un campo (Update/SoftDelete):
//   "• Método: de *Transfer* a **Transferencia**"
// Este SÍ se llega a ver en pantalla hoy: AppDbContext.OnBeforeSaveChanges guarda los
// cambios de Update/SoftDelete en formato {Old, New}, que es justo lo que
// TimelineService.GetTimelineAsync espera para deserializar.
const REGEX_METODO_MODIFICACION = /^(• Método: de \*)(.+?)(\* a \*\*)(.+?)(\*\*)$/;

// Formato que TimelineService.cs arma (en el código) para Alta/Eliminación de un campo:
//   "• **Método**: Transfer"
// OJO — rama hoy INALCANZABLE desde la pantalla real (bug preexistente, no se toca en
// esta tanda, ver TimelineService.cs líneas ~148-159): AppDbContext.OnBeforeSaveChanges
// guarda los cambios de "Create"/"Delete" en formato PLANO {"Campo": valor} (sin envolver
// en {Old, New}), pero TimelineService SIEMPRE intenta deserializar esperando {Old, New}
// — eso hace que CUALQUIER Alta o Eliminación (de cualquier entidad, no solo Pago) tire
// una excepción de parseo y caiga en el catch genérico ("Modificaciones en campos
// técnicos."), así que la línea "• **Método**: X" nunca llega a construirse hoy. Se
// mantiene esta rama (con test directo más abajo, ver reservaTimelineText.test.mjs)
// porque el día que se arregle ese bug compartido — reportado aparte, es más grande que
// este módulo — el formato va a volver a aparecer y esta traducción tiene que seguir
// funcionando sin que nadie tenga que acordarse de tocar este archivo.
const REGEX_METODO_ALTA_O_BAJA = /^(• \*\*Método\*\*: )(.+)$/;

// Texto que se muestra cuando el método de pago no se puede traducir (viene vacío,
// es "Other"/"Otro", o es un token nuevo que el frontend todavía no mapeó). NUNCA se
// muestra el token crudo del backend — eso sería jerga técnica/inglés en una pantalla
// en español (regla del gate de exposición de datos).
const METODO_DESCONOCIDO = "Otro medio";

/**
 * Traduce un método de pago crudo del backend, garantizando que el resultado NUNCA sea
 * el token técnico original: si `traducirMetodoPago` no lo reconoce (devuelve ""), se
 * usa el texto genérico "Otro medio" en vez del string crudo.
 *
 * @param {string} metodoCrudo
 * @returns {string}
 */
function traducirMetodoSinCrudo(metodoCrudo) {
  return traducirMetodoPago(metodoCrudo) || METODO_DESCONOCIDO;
}

/**
 * Traduce el valor del método de pago DENTRO de una línea de detalle del historial,
 * si esa línea es sobre el campo Método. Cualquier otra línea (Importe, Estado, etc.)
 * se devuelve tal cual, sin tocar nada.
 *
 * @param {string} linea - una línea de `event.details` (ya separado por "\n" en el componente)
 * @returns {string}
 */
export function traducirMetodoEnLineaHistorial(linea) {
  if (!linea) return linea;

  const matchAltaOBaja = linea.match(REGEX_METODO_ALTA_O_BAJA);
  if (matchAltaOBaja) {
    const [, prefijo, valorCrudo] = matchAltaOBaja;
    return `${prefijo}${traducirMetodoSinCrudo(valorCrudo)}`;
  }

  const matchModificacion = linea.match(REGEX_METODO_MODIFICACION);
  if (matchModificacion) {
    const [, inicio, valorViejo, medio, valorNuevo, fin] = matchModificacion;
    return `${inicio}${traducirMetodoSinCrudo(valorViejo)}${medio}${traducirMetodoSinCrudo(valorNuevo)}${fin}`;
  }

  return linea;
}

/**
 * Lee el monto y el método de un evento de Alta de Pago, priorizando SIEMPRE los campos
 * estructurados del DTO (`event.amount` / `event.currency` / `event.paymentMethod`) por
 * sobre el parseo de `event.details`.
 *
 * Por qué NO se puede confiar en `event.details` para esto (bloqueante de reviewer,
 * 2026-07-24): por el mismo bug documentado arriba de REGEX_METODO_ALTA_O_BAJA, un Alta
 * de Pago NUNCA llega a tener la línea "• **Importe**: $X" en `details` — cae siempre en
 * el texto genérico "Modificaciones en campos técnicos.". Por eso el backend agregó los
 * tres campos sueltos al DTO (`TimelineEventDto.Amount/Currency/PaymentMethod`), leídos
 * directo de la tabla `Payment` (no del diff de auditoría) — son la fuente confiable.
 *
 * El parseo de `event.details` queda solo como último recurso legacy, por si algún día
 * llega un evento sin estos campos estructurados (ej. una versión vieja cacheada).
 *
 * @param {object} event
 * @returns {{montoTexto: string|null, metodoTexto: string|null}}
 */
export function leerMontoYMetodoDePago(event) {
  // Camino principal: campos estructurados del DTO — SIEMPRE se priorizan si vino
  // alguno de los dos (aunque el otro falte, es más confiable que el texto libre).
  if (event.amount != null || event.paymentMethod != null) {
    return {
      montoTexto: event.amount != null ? formatCurrency(event.amount, event.currency || "ARS") : null,
      metodoTexto: event.paymentMethod != null ? traducirMetodoSinCrudo(event.paymentMethod) : null,
    };
  }

  // Último recurso legacy: parsear las líneas de `details` (formato viejo, ver arriba).
  if (!event.details) return { montoTexto: null, metodoTexto: null };

  const lineas = event.details.split("\n");
  const lineaImporte = lineas.find((linea) => linea.includes("**Importe**"));
  const importeCrudo = lineaImporte ? lineaImporte.replace(/^• \*\*Importe\*\*: /, "").trim() : null;

  const lineaMetodo = lineas.find((linea) => linea.includes("**Método**"));
  const metodoCrudo = lineaMetodo ? lineaMetodo.replace(/^• \*\*Método\*\*: /, "").trim() : null;

  return {
    montoTexto: importeCrudo,
    metodoTexto: metodoCrudo ? traducirMetodoSinCrudo(metodoCrudo) : null,
  };
}

/**
 * Arma un resumen corto en criollo para un evento de Alta de Pago del historial
 * ("Cobro registrado: $150.000,00 — Transferencia"), en vez de que el vendedor tenga
 * que leer la lista de bullets técnica campo por campo (que además, para un Alta, hoy
 * ni siquiera se arma bien — ver `leerMontoYMetodoDePago`).
 *
 * Nota de robustez (si el dato no llega): si el backend no manda ni importe ni método
 * para este evento puntual, la función arma la frase con lo que SÍ tiene, o devuelve
 * null si no hay nada — nunca rompe ni inventa un monto.
 *
 * @param {{eventType?: string, relatedEntityType?: string, amount?: number|null,
 *   currency?: string|null, paymentMethod?: string|null, details?: string|null}} event
 * @returns {string|null} la frase resumen, o null si el evento no es un Alta de Pago
 *   o no hay ningún dato de importe/método para armar nada.
 */
export function resumenAltaDePagoHistorial(event) {
  if (!event || event.eventType !== "Create" || event.relatedEntityType !== "Payment") return null;

  const { montoTexto, metodoTexto } = leerMontoYMetodoDePago(event);

  if (!montoTexto && !metodoTexto) return null;
  if (montoTexto && metodoTexto) return `Cobro registrado: ${montoTexto} — ${metodoTexto}`;
  if (montoTexto) return `Cobro registrado: ${montoTexto}`;
  return `Cobro registrado — ${metodoTexto}`;
}

/* ═══════════════════════════════════════════════════════════════════════════
 * Tanda 4 (rediseño de fichas, 2026-08-04) — solapa Historial "contada como
 * habla una agencia" (maqueta docs/ux/maquetas/2026-08-03-reservas-rediseno.html,
 * sección 8). La frase de Gastón fue: "no es muy clara, parece más de
 * programador que de usuario de agencia de viajes".
 *
 * describirEventoHistorial() arma, a partir del MISMO evento que ya manda el
 * backend (título, actor, tipo, entidad relacionada, diff en `details`), los
 * datos que necesita un renglón ("hito") de la línea de tiempo nueva:
 *   - de quién es la frase (actor humano, o impersonal si lo hizo "el sistema");
 *   - qué pasó, en una oración;
 *   - un detalle secundario chico (si hay algo más específico para mostrar);
 *   - el color del punto de la línea de tiempo.
 *
 * No inventa datos que el backend no manda: donde no hay un texto humano obvio
 * (la myoría de los Update/Delete de servicios, por ejemplo), arma la frase más
 * neutra posible con el verbo genérico de la acción — nunca el nombre técnico
 * crudo de la entidad ("HotelBooking", "ServicioReserva", etc.).
 * ═══════════════════════════════════════════════════════════════════════════ */

// Verbo humano de cada tipo de evento — reemplaza al "Alta de/Cambio en/Anulación de"
// que arma el backend (correcto para un título de auditoría, no para una frase hablada).
const ACCION_HUMANA = {
  Create: "creó",
  Update: "modificó",
  Delete: "eliminó",
  SoftDelete: "anuló",
};

// Nombre de la entidad CON su artículo, para que la frase quede gramaticalmente
// correcta ("anuló EL traslado", no "anuló traslado"). Mismas entidades que ya
// traduce el backend (TimelineService.NormalizeEntityName) — acá solo se les saca
// el "un/una" indefinido y se los pone en minúscula, que es como se habla.
const ENTIDAD_CON_ARTICULO = {
  Reserva: "la reserva",
  FlightSegment: "el vuelo",
  HotelBooking: "el hotel",
  PackageBooking: "el paquete",
  TransferBooking: "el traslado",
  AssistanceBooking: "la asistencia",
  ServicioReserva: "el servicio",
  Payment: "el pago",
  Invoice: "la factura",
  ReservaAttachment: "el archivo",
};

/**
 * True si el evento lo hizo una PERSONA (no "el sistema"). El backend manda
 * actor="Sistema" cuando el cambio no lo disparó ningún usuario logueado (un
 * proceso automático, una migración, etc.) — la maqueta pide que en ese caso la
 * frase arranque por lo que pasó, nunca por "el sistema" ("el sistema no es
 * sujeto de nada" es la misma regla que ya se aplicó en Vouchers, Tanda 3).
 */
function esActorHumano(actor) {
  return Boolean(actor) && actor !== "Sistema";
}

/**
 * Obra "la ficha del operador no borra la historia" (2026-08-20): color del punto para los
 * eventos cuya frase COMPLETA ya viene armada del backend (ver el "Caso 1b" de
 * `describirEventoHistorial`). Mismo criterio de siempre en toda la app (P-20, "un color, un
 * significado"): rojo = sin efecto/reversa, verde = entra plata a favor de la agencia, índigo
 * = documento fiscal/comprobante, ámbar = decisión notable SIN plata, neutro = movimiento de
 * rutina. Fuente: spec docs/ux/2026-08-20-operador-con-rastro-e-historial.md §3.4 y §4.2.
 */
const COLOR_POR_EVENTO_BACKEND = {
  SupplierPurchaseConfirmed: "neutro", // "Se compró {servicio}: {monto}."
  ReservaAnnulled: "rojo", // "{Actor} anuló la reserva." (versión del historial del OPERADOR)
  OperatorPenaltyConfirmed: "indigo", // "La multa del operador quedó confirmada: {monto}."
  OperatorPenaltyWaived: "ambar", // única familia SIN plata: "cerró la multa sin cobrar nada"
  OperatorRefundRegistered: "verde", // entra plata del operador a favor de la agencia
  OperatorRefundUndone: "rojo", // reversa de un reembolso ya registrado
  SupplierPaymentRegistered: "neutro", // pago de rutina al operador
  SupplierInvoiceCreated: "indigo", // comprobante fiscal del operador
  SupplierInvoiceVoided: "rojo", // ese comprobante quedó sin efecto
  CreditNoteEmitted: "indigo", // comprobante fiscal (nota de crédito) emitido
  CreditNoteRejected: "rojo", // ARCA rechazó la nota: quedó sin efecto
};

/**
 * Caso especial: Update sobre la RESERVA en sí, con un cambio de Estado en el
 * diff VIEJO de AuditLogs (formato previo a la Tanda 3, 2026-08-18). Es el
 * ÚNICO caso donde el mapeo enum→label es total y confiable
 * (`traducirEstadoReserva`, mismo módulo que usan las pestañas y el badge de
 * estado) — para los demás tipos de entidad el "estado operativo" tiene su
 * propio vocabulario (Solicitado/Confirmado/Emitido/...) y no se traduce acá
 * para no arriesgar una traducción cruzada incorrecta.
 *
 * El formato del diff lo arma TimelineService.cs: "• Estado: de *X* a **Y**".
 *
 * FALLBACK LEGACY (Tanda 3, 2026-08-18): el backend ya NO manda este formato
 * para reservas nuevas — el cambio de estado ahora llega como su propio
 * evento `eventType: "StatusChange"` (ver `fraseYDetalleCambioDeEstado` más
 * abajo), con motivo y autorizante. Esta función queda como red de contención
 * por si algún historial viejo (AuditLogs de antes de que existiera la tabla
 * ReservaStatusChangeLogs) todavía trae el diff en este formato de texto.
 *
 * @returns {string|null}
 */
function fraseCambioDeEstadoReserva(event) {
  if (event.relatedEntityType !== "Reserva" || event.eventType !== "Update" || !event.details) {
    return null;
  }
  const match = /^• Estado: de \*(.+?)\* a \*\*(.+?)\*\*$/m.exec(event.details);
  if (!match) return null;
  const [, estadoViejo, estadoNuevo] = match;
  return `La reserva pasó de ${traducirEstadoReserva(estadoViejo)} a ${traducirEstadoReserva(estadoNuevo)}.`;
}

// Prefijo con el que el backend arma la línea del autorizante dentro de
// `details` (ver TimelineService.BuildStatusChangeDetails: "Autorizó: {nombre}").
// Se matchea EXACTO (no una regex laxa) porque es texto armado por el propio
// backend, no un dato libre que pueda variar de forma.
const PREFIJO_AUTORIZO = "Autorizó: ";

/**
 * Separa `details` de un evento `StatusChange` en sus dos posibles líneas:
 * el motivo tipeado por el usuario (texto libre) y la línea de quién
 * autorizó la reversión (si la hubo). El backend las manda unidas con "\n"
 * (ver TimelineService.BuildStatusChangeDetails) — acá solo se separan para
 * poder mostrar cada una con su propia etiqueta ("Motivo: …" / "Autorizó: …").
 *
 * @param {string|null|undefined} details
 * @returns {{motivo: string|null, autorizoTexto: string|null}}
 */
function extraerMotivoYAutorizacion(details) {
  if (!details) return { motivo: null, autorizoTexto: null };

  const lineas = details
    .split("\n")
    .map((linea) => linea.trim())
    .filter(Boolean);

  const lineaAutorizo = lineas.find((linea) => linea.startsWith(PREFIJO_AUTORIZO)) || null;
  // Cualquier otra línea que no sea la del autorizante es el motivo (en la
  // práctica el backend nunca manda más de una línea de motivo).
  const lineaMotivo = lineas.find((linea) => linea !== lineaAutorizo) || null;

  return { motivo: lineaMotivo, autorizoTexto: lineaAutorizo };
}

// Par de estados "sin efecto" del dominio (EstadoReserva.IsVoidedStatus del backend, mismo
// criterio que ya usa moneyStatus.js ESTADOS_ANULADOS_FALLBACK). Se usa acá SOLO para saber
// si un StatusChange fue "la reserva se anuló" — no decide nada de plata, no duplica ninguna
// regla de negocio, solo lee el par de códigos crudos que ya manda el backend en fromStatus/
// toStatus (T-3: el front no inventa qué estados son "anulados").
const ESTADOS_ANULADOS = new Set(["Cancelled", "PendingOperatorRefund"]);

/**
 * True si este cambio de estado es el ACTO de anular la reserva: entra a un estado anulado
 * viniendo de uno que no lo era (así una reserva que pasa Cancelled → PendingOperatorRefund,
 * dos estados anulados seguidos, no dispara la frase "anuló" dos veces).
 */
function esTransicionDeAnulacion(fromStatus, toStatus) {
  return ESTADOS_ANULADOS.has(toStatus) && !ESTADOS_ANULADOS.has(fromStatus);
}

/**
 * Caso nuevo (Tanda 3, 2026-08-18): evento propio de cambio de estado de la
 * reserva, `eventType: "StatusChange"`, con los códigos crudos en
 * `fromStatus`/`toStatus` (p.ej. "InManagement"/"Confirmed") — el backend ya
 * NO los traduce, así que acá se usa el mismo traductor de siempre
 * (`traducirEstadoReserva`) para que NUNCA se vea un código técnico en
 * pantalla, ni aunque el backend mande un estado que el frontend todavía no
 * mapeó (en ese caso `traducirEstadoReserva` cae a "—", nunca al string crudo).
 *
 * El detalle secundario combina, si están, quién hizo el cambio, el motivo
 * tipeado y quién autorizó — mismo patrón " · " que ya usan otras pantallas
 * de la app para encadenar datos secundarios cortos en una sola línea chica.
 *
 * Caso especial (obra "la ficha del operador no borra la historia", 2026-08-20,
 * spec §4.2): cuando el destino es un estado anulado, la frase genérica "La
 * reserva pasó de X a Anulada" se reemplaza por la frase de ACCIÓN "anuló" —
 * es la misma palabra que ya usa el toast de éxito al confirmar la anulación
 * (`CancelarReservaInline.jsx`, "Anulación confirmada"), y el punto pasa a rojo
 * (mismo significado que usa toda la app: rojo = freno/sin efecto).
 *
 * @returns {{frase: string, detalle: string|null, colorPunto: "neutro"|"rojo", actor: string|null}|null}
 */
function fraseYDetalleCambioDeEstado(event) {
  if (event.eventType !== "StatusChange") return null;

  const humano = esActorHumano(event.actor);
  const { motivo, autorizoTexto } = extraerMotivoYAutorizacion(event.details);

  if (esTransicionDeAnulacion(event.fromStatus, event.toStatus)) {
    const partesDetalle = [];
    if (motivo) partesDetalle.push(`Motivo: ${motivo}`);
    if (autorizoTexto) partesDetalle.push(autorizoTexto);

    return {
      // El actor va SEPARADO (no metido en la frase) para que Hito lo dibuje en negrita,
      // igual que el resto de los eventos "de acción" de este archivo (ver el caso
      // genérico más abajo) — "María anuló la reserva." con "María" en negrita.
      frase: humano ? "anuló la reserva." : "Se anuló la reserva.",
      actor: humano ? event.actor : null,
      detalle: partesDetalle.length > 0 ? partesDetalle.join(" · ") : null,
      colorPunto: "rojo",
    };
  }

  const partesDetalle = [];
  if (humano) partesDetalle.push(`La hizo ${event.actor}.`);
  if (motivo) partesDetalle.push(`Motivo: ${motivo}`);
  if (autorizoTexto) partesDetalle.push(autorizoTexto);

  return {
    frase: `La reserva pasó de ${traducirEstadoReserva(event.fromStatus)} a ${traducirEstadoReserva(event.toStatus)}.`,
    actor: null, // acá la frase ya queda completa, sin sujeto al principio (caso histórico, sin tocar)
    detalle: partesDetalle.length > 0 ? partesDetalle.join(" · ") : null,
    colorPunto: "neutro",
  };
}

/**
 * Extrae el número de confirmación de un servicio del diff, si el evento trae
 * uno ("ConfirmationNumber" es de los pocos campos técnicos que el backend YA
 * traduce a "Confirmación" en TimelineService.NormalizeFieldName). Sirve como
 * detalle secundario ("N° de confirmación: CONF-123") — mismo ejemplo que
 * dibuja la maqueta.
 *
 * @returns {string|null}
 */
function detalleNumeroDeConfirmacion(event) {
  if (!event.details) return null;
  const matchModificacion = /^• Confirmación: de \*.*?\* a \*\*(.+?)\*\*$/m.exec(event.details);
  if (matchModificacion) return `N° de confirmación: ${matchModificacion[1]}`;
  const matchAlta = /^• \*\*Confirmación\*\*: (.+)$/m.exec(event.details);
  if (matchAlta) return `N° de confirmación: ${matchAlta[1]}`;
  return null;
}

/**
 * Arma todo lo que necesita un "hito" (renglón) de la línea de tiempo nueva a
 * partir de un evento crudo del backend.
 *
 * @param {object} event - TimelineEventDto (ver ReservaTimeline.jsx)
 * @returns {{
 *   colorPunto: "rojo"|"verde"|"indigo"|"ambar"|"neutro",
 *   actor: string|null,       // null = frase impersonal ("Se anuló...")
 *   esCobro: boolean,         // true → el componente arma "Actor cobró $monto." en verde
 *   montoTexto: string|null,  // solo si esCobro
 *   frase: string|null,       // la oración completa YA armada (cuando no es un cobro);
 *                             // ya incluye el actor si corresponde ("Maite anuló el traslado.")
 *   detalle: string|null,     // línea chica secundaria, o null si no hay nada más que agregar
 * }}
 */
export function describirEventoHistorial(event) {
  const humano = esActorHumano(event.actor);

  // Caso 1: alta de un cobro — el monto SIEMPRE en positivo y en verde (nunca con el
  // signo "-" que mostraba el diff crudo: "un cobro entra plata", regla firmada de
  // la maqueta). Reusa leerMontoYMetodoDePago (misma fuente confiable que ya usaba
  // resumenAltaDePagoHistorial: los campos estructurados del Payment, no el diff).
  if (event.eventType === "Create" && event.relatedEntityType === "Payment") {
    const { montoTexto, metodoTexto } = leerMontoYMetodoDePago(event);

    // Bloqueante de review (2026-08-04): existen Payments con monto NEGATIVO (la reversa
    // de una nota de crédito, una multa deshecha) — eso NO es un cobro: es un cobro que
    // se descuenta. Regla firmada de la maqueta §8: la plata que sale va con su palabra,
    // no con un signo. Punto rojo, monto SIEMPRE en positivo, frase propia.
    // Solo decidimos por el campo estructurado (event.amount); el camino legacy de
    // `details` no trae signo confiable y sigue tratándose como cobro.
    if (event.amount != null && event.amount <= 0) {
      return {
        colorPunto: "rojo",
        actor: null,
        esCobro: false,
        montoTexto: null,
        frase: `Se descontó un cobro de ${formatCurrency(Math.abs(event.amount), event.currency || "ARS")}.`,
        detalle: metodoTexto ? `Forma de pago: ${metodoTexto}` : null,
      };
    }

    return {
      colorPunto: "verde",
      actor: humano ? event.actor : null,
      esCobro: true,
      montoTexto,
      frase: null,
      detalle: metodoTexto ? `Forma de pago: ${metodoTexto}` : null,
    };
  }

  // Caso 1b (obra "la ficha del operador no borra la historia", 2026-08-20): eventos que
  // el backend arma con la FRASE COMPLETA ya lista (actor y monto adentro del texto, si
  // corresponden) — vienen tanto del historial de la RESERVA (multa del operador, notas de
  // crédito de la cancelación) como del historial NUEVO del OPERADOR (compra, anulación,
  // reembolso, pago, factura). Acá NO se reconstruye ninguna frase: el mismo texto tiene que
  // verse igual en las dos pantallas, así que solo se le asigna el color del punto según la
  // tabla de la spec de UX (§3.4/§4.2) — event.title y event.details se muestran tal cual.
  if (Object.prototype.hasOwnProperty.call(COLOR_POR_EVENTO_BACKEND, event.eventType)) {
    return {
      colorPunto: COLOR_POR_EVENTO_BACKEND[event.eventType],
      actor: null, // el actor (si lo hizo una persona) ya está adentro de event.title
      esCobro: false,
      montoTexto: null,
      // Red de seguridad: si un evento nuevo del backend viniera sin title (no debería,
      // pero un renglón mudo es peor que una frase genérica), se muestra algo igual.
      frase: event.title || "Hubo un movimiento con este operador.",
      detalle: event.details || null,
    };
  }

  // Caso 2: cambio de estado de la reserva (evento propio del backend, Tanda 3
  // 2026-08-18) — frase natural con los dos labels traducidos, más motivo y
  // autorizante si vinieron. Es el camino que usan las reservas nuevas. Incluye
  // el caso especial "anuló la reserva" (obra 2026-08-20, ver el XML-doc de
  // fraseYDetalleCambioDeEstado): por eso acá se respeta el actor/colorPunto que
  // arma esa función, en vez de fijarlos siempre a null/"neutro".
  const cambioDeEstado = fraseYDetalleCambioDeEstado(event);
  if (cambioDeEstado) {
    return {
      colorPunto: cambioDeEstado.colorPunto,
      actor: cambioDeEstado.actor,
      esCobro: false,
      montoTexto: null,
      frase: cambioDeEstado.frase,
      detalle: cambioDeEstado.detalle,
    };
  }

  // Caso 2b (fallback legacy): mismo cambio de estado, pero leído del diff
  // viejo de AuditLogs — ver el comentario largo en fraseCambioDeEstadoReserva.
  const fraseEstado = fraseCambioDeEstadoReserva(event);
  if (fraseEstado) {
    return {
      colorPunto: "neutro",
      actor: null, // la frase ya está armada completa, sin sujeto al principio
      esCobro: false,
      montoTexto: null,
      frase: fraseEstado,
      detalle: humano ? `La hizo ${event.actor}.` : null,
    };
  }

  // Caso genérico: cualquier otro Alta/Cambio/Eliminación/Anulación sobre cualquier
  // entidad. Sin texto humano "obvio" (no sabemos el nombre del vuelo/hotel/traslado
  // puntual, solo que "algo pasó con un traslado") — se arma la frase más neutra
  // posible con el verbo de la acción, nunca con el nombre técnico crudo de la entidad.
  const accion = ACCION_HUMANA[event.eventType] || "modificó";
  const entidad = ENTIDAD_CON_ARTICULO[event.relatedEntityType] || "un registro de la reserva";
  const colorPunto =
    event.eventType === "SoftDelete" || event.eventType === "Delete"
      ? "rojo"
      : event.relatedEntityType === "Invoice"
      ? "indigo"
      : "neutro";

  return {
    colorPunto,
    actor: humano ? event.actor : null,
    esCobro: false,
    montoTexto: null,
    frase: humano ? `${accion} ${entidad}.` : `Se ${accion} ${entidad}.`,
    detalle: detalleNumeroDeConfirmacion(event),
  };
}

/* ─── Agrupado por día (maqueta sección 8: "Hoy · Ayer · Viernes 25/07/2026") ─── */

const NOMBRES_DE_DIA = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];

function esMismoDiaArgentina(fechaA, fechaB) {
  return (
    fechaA.getFullYear() === fechaB.getFullYear() &&
    fechaA.getMonth() === fechaB.getMonth() &&
    fechaA.getDate() === fechaB.getDate()
  );
}

/**
 * Arma la etiqueta del separador de día: "Hoy — dd/mm/aaaa", "Ayer — dd/mm/aaaa",
 * o "{Día de la semana} dd/mm/aaaa" para cualquier otra fecha — igual que dice la
 * maqueta ("es como uno se acuerda de las cosas"). Siempre en hora de Argentina,
 * nunca la del navegador de quien mira la pantalla.
 */
function etiquetaDelDia(fechaEventoArg, hoyArg, ayerArg) {
  const dd = String(fechaEventoArg.getDate()).padStart(2, "0");
  const mm = String(fechaEventoArg.getMonth() + 1).padStart(2, "0");
  const yyyy = fechaEventoArg.getFullYear();
  const fechaTexto = `${dd}/${mm}/${yyyy}`;

  if (esMismoDiaArgentina(fechaEventoArg, hoyArg)) return `Hoy — ${fechaTexto}`;
  if (esMismoDiaArgentina(fechaEventoArg, ayerArg)) return `Ayer — ${fechaTexto}`;
  return `${NOMBRES_DE_DIA[fechaEventoArg.getDay()]} ${fechaTexto}`;
}

/**
 * Agrupa los eventos del historial (ya ordenados del más nuevo al más viejo,
 * como los manda el backend) en bloques por día calendario de Argentina, cada
 * uno con su etiqueta ("Hoy — …", "Ayer — …", o el nombre del día).
 *
 * No reordena nada: si `events` llegara desordenado, el agrupado podría separar
 * un mismo día en dos bloques — se asume el orden que ya garantiza el backend
 * (`OrderByDescending(a => a.Timestamp)`).
 *
 * @param {object[]} events - lista de TimelineEventDto
 * @param {Date} [ahora] - inyectable para tests; por defecto el reloj real.
 * @returns {{ etiqueta: string, eventos: object[] }[]}
 */
export function agruparEventosPorDia(events, ahora = new Date()) {
  if (!Array.isArray(events) || events.length === 0) return [];

  const hoyArg = aHoraArgentina(ahora);
  const ayerArg = new Date(hoyArg.getFullYear(), hoyArg.getMonth(), hoyArg.getDate() - 1);

  const grupos = [];
  let grupoActual = null;
  let claveDiaActual = null;

  for (const event of events) {
    const fechaArg = aHoraArgentina(event.timestamp);
    const claveDia = `${fechaArg.getFullYear()}-${fechaArg.getMonth()}-${fechaArg.getDate()}`;

    if (claveDia !== claveDiaActual) {
      grupoActual = { etiqueta: etiquetaDelDia(fechaArg, hoyArg, ayerArg), eventos: [] };
      grupos.push(grupoActual);
      claveDiaActual = claveDia;
    }
    grupoActual.eventos.push(event);
  }

  return grupos;
}

/**
 * Hora del evento en formato HH:mm, en hora de Argentina (nunca la del navegador
 * de quien mira la pantalla) — el dato ya vive en el timestamp, esto solo lo
 * recorta a lo que necesita el renglón de la línea de tiempo (la fecha completa
 * la dice el separador de día, no hace falta repetirla acá).
 *
 * @param {string} timestamp - instante ISO del evento
 * @returns {string}
 */
export function horaDeEvento(timestamp) {
  const fechaArg = aHoraArgentina(timestamp);
  const hh = String(fechaArg.getHours()).padStart(2, "0");
  const mm = String(fechaArg.getMinutes()).padStart(2, "0");
  return `${hh}:${mm}`;
}
