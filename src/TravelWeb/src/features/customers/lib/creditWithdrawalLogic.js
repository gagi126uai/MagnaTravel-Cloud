/**
 * Lógica pura del flujo "Usar saldo a favor del cliente".
 *
 * Funciones exportadas para poder testarlas con Node sin React.
 * El componente UsarSaldoAFavorInline las consume internamente.
 *
 * REGLA MULTIMONEDA: nunca mezclar ARS con USD.
 * Cada entry de crédito tiene su propia moneda; el retiro siempre
 * es en la moneda del entry seleccionado.
 *
 * DESTINOS DE RETIRO (enum ClientCreditWithdrawalKind del backend):
 *   0 = KeptAsCredit        → cierre sin movimiento (el saldo queda)
 *   1 = PhysicalCash        → devolución en efectivo (sujeto a tope Ley 25.345)
 *   2 = Transfer            → devolución por transferencia
 *   3 = AppliedToNewBooking → aplicar a otra reserva del mismo cliente (FC4 completo)
 *
 * El kind 3 usa un endpoint DIFERENTE: POST /customers/{id}/credit/apply
 * (no el endpoint de withdrawals). El backend drena los bolsillos en FIFO
 * y registra el pago en la reserva destino atómicamente.
 *
 * KIND_APLICAR_A_MULTA (4, Tanda D1 2026-07-16): NO es un valor del enum
 * ClientCreditWithdrawalKind del backend — es un pseudo-kind SOLO del front, igual
 * criterio que el kind 3, para elegir qué rama de la UI mostrar y a qué endpoint
 * mandar el pedido: POST /customers/{id}/credit/apply-to-penalty (nunca /withdrawals).
 */

// Fix de revisión (2026-07-17, gate de exposición): los mensajes de validación de monto
// mostraban el saldo disponible como número crudo ("...(1500.5)") en vez de plata
// formateada ("...($1.500,50)") — se usa en validarMontoRetiro/validarAplicacion/
// validarAplicacionAMulta más abajo.
import { formatCurrency } from "../../../lib/utils.js";

export const KIND_APLICAR_A_MULTA = 4;

/**
 * Lista de destinos de retiro que se muestran al usuario. Orden fijado por la spec
 * (2026-07-16, "Aplicar a una multa" § 2): transferencia y efectivo primero (los casos
 * más comunes), después las dos aplicaciones (multa, otra reserva), y al final "dejar
 * como crédito" (la opción más pasiva).
 */
export const DESTINOS_RETIRO = [
  { kind: 2, label: "Devolver por transferencia" },
  { kind: 1, label: "Devolver en efectivo" },
  { kind: KIND_APLICAR_A_MULTA, label: "Aplicar a una multa" },
  { kind: 3, label: "Aplicar a otra reserva" },
  { kind: 0, label: "Dejar como crédito (cerrar aviso)" },
];

/**
 * Valida el monto a retirar de un entry de crédito.
 *
 * @param {number|string} monto       - Monto ingresado por el usuario
 * @param {number}        saldoDisp   - Saldo disponible del entry (remainingBalance)
 * @param {string}        [moneda]    - "ARS"|"USD", para formatear el monto del mensaje
 *   de error ("$1.500,50" en vez del número crudo). Opcional para no romper callers
 *   viejos que todavía no la pasan (cae al número plano, mismo comportamiento de antes).
 * @returns {string|null}             - Mensaje de error en español, o null si es válido
 */
export function validarMontoRetiro(monto, saldoDisp, moneda) {
  const montoNum = parseFloat(monto);

  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }

  if (montoNum > saldoDisp) {
    const saldoTexto = moneda ? formatCurrency(saldoDisp, moneda) : saldoDisp;
    return `El monto no puede superar el saldo disponible (${saldoTexto}).`;
  }

  return null;
}

/**
 * Formatea un entry de crédito para mostrarlo en la lista.
 *
 * Ejemplo de salida: "Quedan $1.500,00 de $2.000,00 · ARS · origen: reserva 2024/001"
 *
 * @param {object} entry  - Objeto { remainingBalance, creditedAmount, currency, originReservaNumber }
 * @returns {string}
 */
export function formatearDescripcionEntry(entry) {
  if (!entry) return "";

  const { remainingBalance, creditedAmount, currency, originReservaNumber } = entry;

  // Símbolo visible según la moneda (no mezclar $ ARS con US$ USD)
  const simbolo = currency === "USD" ? "US$" : "$";

  const parteRemaining = `Quedan ${simbolo}${Number(remainingBalance || 0).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const parteDe        = `de ${simbolo}${Number(creditedAmount || 0).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
  const parteMoneda    = `· ${currency}`;
  const parteOrigen    = originReservaNumber ? `· origen: reserva ${originReservaNumber}` : "";

  return [parteRemaining, parteDe, parteMoneda, parteOrigen].filter(Boolean).join(" ");
}

/**
 * Arma el body del POST /api/client-credit-entries/{entryPublicId}/withdrawals.
 *
 * Para kind = 0 (KeptAsCredit): amount = 0, sin campos extra.
 * Para kind = 1 (PhysicalCash) o kind = 2 (Transfer): amount = monto numérico.
 * Para kind = 3: NO usar esta función — usar armarPayloadAplicacion().
 *
 * @param {number} kind    - Enum del destino (0, 1 o 2)
 * @param {number} amount  - Monto a retirar (ignorado cuando kind = 0)
 * @param {object} extras  - Campos opcionales: reference, paymentMethodOverride
 * @returns {object}       - Payload listo para el backend
 */
export function armarPayloadRetiro(kind, amount, extras = {}) {
  if (kind === 0) {
    // KeptAsCredit: no mueve plata, solo cierra el aviso
    return { kind: 0, amount: 0 };
  }

  const payload = {
    kind,
    amount: parseFloat(amount),
  };

  // Campos opcionales para transferencia (solo si el usuario los completó)
  if (extras.reference) {
    payload.reference = extras.reference;
  }
  if (extras.paymentMethodOverride) {
    payload.paymentMethodOverride = extras.paymentMethodOverride;
  }

  return payload;
}

/**
 * Valida los datos del flujo "Aplicar a otra reserva" (kind 3) ANTES de hacer el POST.
 *
 * Esta función es para kind 3 únicamente — para kinds 0/1/2 usar validarMontoRetiro().
 *
 * @param {number|string} monto               - Monto ingresado por el usuario
 * @param {number}        saldoDisponible      - Saldo disponible en la moneda (de ClientCreditOverviewDto)
 * @param {string|null}   targetReservaPublicId - PublicId de la reserva destino elegida
 * @param {string}        [moneda]             - "ARS"|"USD", para formatear el monto del
 *   mensaje de error. Opcional (mismo criterio que validarMontoRetiro).
 * @returns {string|null}                      - Mensaje de error, o null si todo está bien
 */
export function validarAplicacion(monto, saldoDisponible, targetReservaPublicId, moneda) {
  // Primero validar que haya reserva destino elegida
  if (!targetReservaPublicId) {
    return "Elegí una reserva destino antes de confirmar.";
  }

  const montoNum = parseFloat(monto);
  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }

  if (montoNum > saldoDisponible) {
    const saldoTexto = moneda ? formatCurrency(saldoDisponible, moneda) : saldoDisponible;
    return `El monto no puede superar el saldo disponible (${saldoTexto}).`;
  }

  return null;
}

/**
 * Arma el payload para POST /api/customers/{publicId}/credit/apply.
 *
 * Este endpoint es DIFERENTE al de withdrawals: drena bolsillos en FIFO
 * y registra el pago en la reserva destino automáticamente.
 *
 * Nota: el backend recibe Currency como string y TargetReservaPublicId como Guid.
 *
 * @param {string} currency              - "ARS" o "USD"
 * @param {number|string} amount         - Monto a aplicar
 * @param {string} targetReservaPublicId - PublicId de la reserva destino
 * @returns {object}                     - Payload listo para enviar como JSON
 */
export function armarPayloadAplicacion(currency, amount, targetReservaPublicId) {
  return {
    currency,
    amount: parseFloat(amount),
    targetReservaPublicId,
  };
}

// =============================================================================
// Tanda D1 (2026-07-16): "Aplicar saldo a favor a una multa" + "neteo en la
// devolución". Funciones puras testeadas en creditWithdrawalLogic.test.mjs.
// =============================================================================

/**
 * Junta las multas aplicables (que da la previa del neteo, `RefundNettingPreviewDto.
 * openPenalties` — YA con el saldo pendiente NETEADO de cada una) con el nombre del
 * expediente (que da `pendingPenalties.items`, del overview de la cuenta) para poder
 * mostrar "R-1050 · Bariloche" en el picker (spec §3.1).
 *
 * OJO por qué se cruzan DOS fuentes en vez de usar solo una: `openPenalties` tiene el
 * monto CORRECTO (neteado) pero no el nombre del expediente; `pendingPenalties.items`
 * tiene el nombre pero su `amount` es el BRUTO sin netear (a propósito, ver
 * CustomerAccountDtos.cs) — usarlo para "Falta cobrar" mostraría un monto mayor al
 * real. Por eso el monto SIEMPRE sale de `openPenalties` y el nombre es solo un adorno
 * opcional que puede faltar (una reserva sin `pendingPenalties` correspondiente sigue
 * mostrándose, solo que sin el nombre al lado del número).
 *
 * @param {Array<{reservaPublicId, numeroReserva, debitNotePublicId, debitNoteDisplayNumber, outstandingAmount}>} openPenalties
 * @param {Array<{reservaPublicId, name}>} pendingPenaltyItems
 * @returns {Array<{reservaPublicId, numeroReserva, debitNotePublicId, outstandingAmount, name: string|null}>}
 */
export function enriquecerMultasAplicables(openPenalties, pendingPenaltyItems) {
  const lista = Array.isArray(openPenalties) ? openPenalties : [];
  const items = Array.isArray(pendingPenaltyItems) ? pendingPenaltyItems : [];

  return lista.map((multa) => {
    const item = items.find(
      (candidato) => String(candidato.debitNotePublicId ?? "") === String(multa.debitNotePublicId ?? "")
    );
    return {
      reservaPublicId: multa.reservaPublicId,
      numeroReserva: multa.numeroReserva,
      debitNotePublicId: multa.debitNotePublicId,
      outstandingAmount: multa.outstandingAmount,
      name: item?.name || null,
    };
  });
}

/**
 * Monto sugerido al elegir una multa en el picker (spec §3.1): el MENOR entre lo que
 * falta cobrar de esa multa puntual y el saldo a favor disponible del cliente.
 *
 * @param {{ outstandingAmount: number }|null} multa
 * @param {number} saldoDisponible
 * @returns {number}
 */
export function montoSugeridoAplicacionAMulta(multa, saldoDisponible) {
  if (!multa) return 0;
  return Math.min(Number(multa.outstandingAmount ?? 0), Number(saldoDisponible ?? 0));
}

/**
 * Valida el monto a aplicar contra UNA multa elegida (obra a). Mismo criterio que
 * validarAplicacion (kind 3), pero el tope es el MENOR entre lo que falta cobrar de
 * la multa y el saldo disponible (nunca el saldo disponible solo: aplicar más de lo
 * que la multa debe no tiene sentido de negocio, aunque sobre saldo).
 *
 * @param {number|string} monto
 * @param {{ outstandingAmount: number }|null} multaSeleccionada
 * @param {number} saldoDisponible
 * @param {string} [moneda] - "ARS"|"USD", para formatear el tope del mensaje de error.
 *   Opcional (mismo criterio que validarMontoRetiro/validarAplicacion).
 * @returns {string|null}
 */
export function validarAplicacionAMulta(monto, multaSeleccionada, saldoDisponible, moneda) {
  if (!multaSeleccionada) {
    return "Elegí una multa antes de confirmar.";
  }

  const montoNum = parseFloat(monto);
  if (!monto || isNaN(montoNum) || montoNum <= 0) {
    return "El monto tiene que ser mayor a 0.";
  }

  const tope = montoSugeridoAplicacionAMulta(multaSeleccionada, saldoDisponible);
  if (montoNum > tope) {
    const topeTexto = moneda ? formatCurrency(tope, moneda) : tope;
    return `El monto no puede superar ${topeTexto} (lo que falta cobrar de esa multa, o el saldo disponible).`;
  }

  return null;
}

/**
 * Arma el payload de POST /api/customers/{id}/credit/apply-to-penalty
 * (ApplyCreditToPenaltyRequest).
 *
 * @param {string} currency
 * @param {number|string} amount
 * @param {string} debitNotePublicId
 * @returns {{ currency: string, amount: number, debitNotePublicId: string }}
 */
export function armarPayloadAplicacionAMulta(currency, amount, debitNotePublicId) {
  return {
    currency,
    amount: parseFloat(amount),
    debitNotePublicId,
  };
}

/**
 * Mapea el kind de retiro (1 = efectivo, 2 = transferencia) al token de contrato
 * `RefundMethod` que espera POST .../credit/refund-with-netting ("PhysicalCash" /
 * "Transfer"). Ese token es un detalle de la API — el front NUNCA lo muestra crudo en
 * pantalla (regla del contrato, 2026-07-16); esta función solo lo arma para el pedido.
 *
 * @param {number} kindDestino - 1 (efectivo) o 2 (transferencia)
 * @returns {"PhysicalCash"|"Transfer"|null}
 */
export function mapearKindARefundMethod(kindDestino) {
  if (kindDestino === 1) return "PhysicalCash";
  if (kindDestino === 2) return "Transfer";
  return null;
}

/**
 * Arma el payload de POST /api/customers/{id}/credit/refund-with-netting
 * (RefundWithNettingRequest). A propósito NO lleva `amount`: el neto SIEMPRE es el que
 * calcula el servidor netenado todo el saldo contra toda la deuda de multas abierta
 * (spec P4=A, "nunca un monto tecleado a mano").
 *
 * @param {string} currency
 * @param {number} kindDestino - 1 (efectivo) o 2 (transferencia)
 * @param {string} [reference] - referencia opcional (solo aplica a transferencia)
 * @returns {{ currency: string, refundMethod: string, reference: string|undefined }}
 */
export function armarPayloadRefundConNeteo(currency, kindDestino, reference) {
  return {
    currency,
    refundMethod: mapearKindARefundMethod(kindDestino),
    reference: reference || undefined,
  };
}

/**
 * Compara dos previas del neteo (la que el usuario está mirando vs. una recién pedida
 * justo antes de confirmar) para decidir si la cuenta cambió mientras tanto (spec §9,
 * "previa desactualizada"). El backend de este endpoint NO devuelve un 409 explícito
 * cuando la deuda de una multa cambió entre el preview y el confirm — SIEMPRE recalcula
 * fresco y aplica ese resultado (ver ClientCreditService.TryRefundCustomerCreditWithNettingOnceAsync,
 * 2026-07-16): por eso esta comparación se hace ACÁ, pidiendo la previa de nuevo justo
 * antes de mandar la plata, para nunca confirmar con números que el usuario ya no está
 * viendo en pantalla.
 *
 * @param {{ availableCredit: number, totalOpenPenalties: number, netToRefund: number }|null} previaVista
 * @param {{ availableCredit: number, totalOpenPenalties: number, netToRefund: number }|null} previaFresca
 * @returns {boolean}
 */
export function previewsDifierenSignificativamente(previaVista, previaFresca) {
  if (!previaVista || !previaFresca) return true;
  const EPS = 0.01;
  return (
    Math.abs(Number(previaVista.availableCredit ?? 0) - Number(previaFresca.availableCredit ?? 0)) > EPS ||
    Math.abs(Number(previaVista.totalOpenPenalties ?? 0) - Number(previaFresca.totalOpenPenalties ?? 0)) > EPS ||
    Math.abs(Number(previaVista.netToRefund ?? 0) - Number(previaFresca.netToRefund ?? 0)) > EPS
  );
}

/**
 * Arma el texto del toast de éxito tras una devolución con neteo (spec §4.1/§4.2).
 *
 * Por qué necesita `openPenaltiesPreview`: el resultado del backend
 * (`RefundWithNettingResultDto.penaltyApplications`) solo trae el PublicId de la
 * reserva de cada multa pagada, nunca su número legible (para no viajar un campo de
 * más) — el número sale de cruzar `debitNotePublicId` contra la previa que el usuario
 * ya vio en pantalla (la misma fuente confiable que armó el picker).
 *
 * "Quedó saldada" solo se afirma cuando `netRefunded > 0`: por cómo arma el neteo el
 * backend (FIFO, corta apenas se acaba el saldo), si sobró algo para devolver es porque
 * CADA multa aplicada en esta operación se cubrió por completo — nunca queda una a
 * medias cuando hay sobrante (ver TryRefundCustomerCreditWithNettingOnceAsync).
 *
 * @param {{ currency:string, netRefunded:number, penaltyApplications:Array<{debitNotePublicId}> }} resultado
 * @param {Array<{debitNotePublicId, numeroReserva}>} openPenaltiesPreview
 * @returns {string}
 */
export function armarMensajeExitoNeteo(resultado, openPenaltiesPreview = []) {
  const currency = resultado?.currency;
  const simbolo = currency === "USD" ? "US$" : "$";
  const netRefunded = Number(resultado?.netRefunded ?? 0);
  const netoTexto = `${simbolo}${netRefunded.toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

  const numerosReserva = (resultado?.penaltyApplications ?? [])
    .map((aplicacion) => {
      const previa = openPenaltiesPreview.find(
        (p) => String(p.debitNotePublicId ?? "") === String(aplicacion.debitNotePublicId ?? "")
      );
      return previa?.numeroReserva ?? null;
    })
    .filter(Boolean);

  if (netRefunded > 0) {
    if (numerosReserva.length === 1) {
      return `Se registró la devolución de ${netoTexto}. La multa de ${numerosReserva[0]} quedó saldada.`;
    }
    if (numerosReserva.length > 1) {
      return `Se registró la devolución de ${netoTexto}. Las multas de ${numerosReserva.join(", ")} quedaron saldadas.`;
    }
    return `Se registró la devolución de ${netoTexto}.`;
  }

  if (numerosReserva.length > 0) {
    return `El saldo a favor se aplicó a la/s multa/s de ${numerosReserva.join(", ")}. No quedó nada para devolver.`;
  }
  return "La operación se registró correctamente.";
}

// Tokens de destino que manda el backend en ClientCreditApplicationLineDto.DestinationKind
// (ver ClientCreditApplicationDestinationKind.cs). El front SOLO usa el token para elegir
// el texto — nunca lo muestra crudo en pantalla.
const DESTINATION_KIND_RESERVA = "reserva";
const DESTINATION_KIND_MULTA = "multa";

/**
 * Prefijo de texto de UNA fila de la lista "Saldo a favor aplicado" (spec §6, título
 * renombrado para cubrir los dos destinos posibles desde la Tanda D1) — el JSX arma la
 * oración completa poniendo el número de reserva COMO LINK justo después de este
 * prefijo (por eso la función devuelve solo el texto de "antes del link", nunca la
 * oración entera armada). Nunca se muestra el token crudo `destinationKind`.
 *
 * @param {string} destinationKind - "reserva" | "multa" (ClientCreditApplicationDestinationKind)
 * @returns {string}
 */
export function prefijoDestinoAplicacionSaldo(destinationKind) {
  return destinationKind === DESTINATION_KIND_MULTA
    ? "Saldo a favor aplicado a la multa de"
    : "Saldo a favor aplicado a";
}
