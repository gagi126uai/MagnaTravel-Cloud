/**
 * Estado ÚNICO de "plata" de una reserva — la fuente que pinta deuda / saldo a favor
 * en toda la app (chips de la ficha, franja de números, listado de reservas y la
 * fila de la cuenta corriente del cliente).
 *
 * Por qué existe (saneamiento Tanda 6, 2026-07-05): la pregunta "¿esta reserva debe
 * plata?" estaba respondida 4 veces distintas en el frente (ReservaStatusChips,
 * ReservaSummaryStrip, ReservaTable, CustomerAccountPage), cada una leyendo
 * `reserva.balance > 0` a mano. Divergían entre sí y ninguna sabía que una reserva
 * ANULADA nunca debe mostrar "deuda" genérica: su plata se resuelve por el circuito
 * de cancelación (saldo a favor del cliente o multa por anulación), no por el cobro
 * normal — ver `ReservationDebtRules.cs` / `CancelledMoneyContext` en el backend.
 *
 * Esta función es la ÚNICA que decide la categorización. Los componentes NO deben
 * volver a comparar `reserva.balance > 0` para decidir qué mostrar: siempre pasan
 * por acá y solo se ocupan de la parte visual (colores, forma del chip).
 *
 * Decisión del dueño (2026-07-04): un "Inconsistente" (deuda en una anulada sin
 * Nota de Débito de multa que la respalde) es un problema para el vigía interno de
 * datos, NUNCA algo que vea el vendedor. Por eso ese caso devuelve `kind: "none"`.
 */

// Estados donde la reserva quedó "sin efecto" por un proceso de anulación. Fuera de
// estos dos estados, la plata se lee del circuito de cobro normal (collectionStatus).
const ESTADOS_ANULADOS = new Set(["Cancelled", "PendingOperatorRefund"]);

/** True si el estado corresponde a una reserva anulada (deshecha con proceso fiscal). */
export function isReservaAnulada(status) {
  return ESTADOS_ANULADOS.has(status);
}

/**
 * Categoriza la plata de una reserva.
 *
 * @param {object} reserva - DTO de reserva (detalle o fila de listado). Campos leídos
 *   (todos vienen calculados por el backend, ninguno se recalcula acá):
 *   - status
 *   - balance
 *   - collectionStatus: "SinMovimientos" | "Saldado" | "ConDeuda" | "SaldoAFavor" | undefined
 *   - hasOverdueDebt: boolean|undefined
 *   - isWithinUnpaidAlertWindow: boolean|undefined
 *   - cancelledMoneyContext: "SaldoAFavorPendiente" | "MultaPorCobrar" | "Inconsistente" | null | undefined
 *     (solo tiene sentido en reservas anuladas; en el resto de los estados el backend lo manda en null)
 *
 * @returns {{ kind: string, label: string|null, tone: "success"|"warning"|"danger"|"neutral" }}
 *   kind identifica el caso exacto (útil para tests y para que cada componente decida
 *   si le corresponde mostrar chip/color propio). label es el texto listo para mostrar
 *   (o null si no hay que mostrar nada). tone es la paleta de color a aplicar.
 */
export function getMoneyStatus(reserva) {
  if (!reserva) {
    return { kind: "none", label: null, tone: "neutral" };
  }

  return isReservaAnulada(reserva.status)
    ? getMoneyStatusAnulada(reserva)
    : getMoneyStatusEstadoVivo(reserva);
}

// ─── Reservas ANULADAS (Cancelled / PendingOperatorRefund) ─────────────────────────

function getMoneyStatusAnulada(reserva) {
  const contexto = reserva.cancelledMoneyContext;

  if (contexto === "SaldoAFavorPendiente") {
    return { kind: "saldoAFavorAnulada", label: "Saldo a favor", tone: "success" };
  }

  if (contexto === "MultaPorCobrar") {
    return { kind: "multaPorCobrar", label: "Multa por anulación pendiente de cobro", tone: "warning" };
  }

  if (contexto === "Inconsistente") {
    // Regla del dueño: un dato roto (deuda sin respaldo) lo revisa el vigía interno,
    // nunca se lo mostramos al vendedor como si fuera plata real.
    return { kind: "none", label: null, tone: "neutral" };
  }

  // Fallback para DTOs que todavía no traen cancelledMoneyContext (hoy es el caso de
  // CustomerAccountReservaListItemDto, la fila de reservas dentro de la cuenta del
  // cliente). Ahí solo podemos afirmar con seguridad el saldo a favor: un balance
  // negativo en una reserva anulada SIEMPRE es plata del cliente sin devolver, sin
  // importar si hay o no una multa (ver ReservationDebtRules.DeriveForCancelled). Un
  // balance positivo sin el contexto explícito puede ser una multa legítima O un dato
  // roto — en la duda, no mostramos nada (mismo criterio que "Inconsistente").
  const balance = Number(reserva.balance ?? 0);
  const ROUNDING_TOLERANCE = 0.01; // mismo margen que ReservationDebtRules.cs (resto de centavo por conversión)
  if (balance < -ROUNDING_TOLERANCE) {
    return { kind: "saldoAFavorAnulada", label: "Saldo a favor", tone: "success" };
  }
  return { kind: "none", label: null, tone: "neutral" };
}

// ─── Reservas VIVAS (todo lo que no es Cancelled/PendingOperatorRefund) ────────────

function getMoneyStatusEstadoVivo(reserva) {
  const collectionStatus = reserva.collectionStatus;

  if (collectionStatus === "SinMovimientos") {
    return { kind: "sinMovimientos", label: "Sin movimientos", tone: "neutral" };
  }

  if (collectionStatus === "Saldado") {
    return { kind: "pagada", label: "Pagada", tone: "success" };
  }

  if (collectionStatus === "SaldoAFavor") {
    return { kind: "saldoAFavor", label: "A favor", tone: "success" };
  }

  if (collectionStatus === "ConDeuda") {
    return deudaCobrableKind(reserva);
  }

  // Sin collectionStatus explícito (DTO legacy que no lo trae, ej.
  // CustomerAccountReservaListItemDto): mismo fallback que usa el propio backend para
  // ese caso (ReservaCollectionStatus.Derive(IEnumerable<decimal>) en Postgres) — sin
  // señal de actividad, derivamos SOLO del signo del balance. Un balance en cero se
  // deja como "Sin movimientos" (no "Pagada"): fix histórico BUG MENOR-1/3, nunca
  // afirmar "pagó todo" sin que el backend lo confirme explícitamente.
  const balance = Number(reserva.balance ?? 0);
  const EPSILON = 0.005; // mismo margen que ReservaCollectionStatus.Epsilon
  if (balance > EPSILON) {
    return deudaCobrableKind(reserva);
  }
  if (balance < -EPSILON) {
    return { kind: "saldoAFavor", label: "A favor", tone: "success" };
  }
  return { kind: "sinMovimientos", label: "Sin movimientos", tone: "neutral" };
}

/**
 * Una reserva con deuda cobrable (balance > 0 en un estado no anulado) puede mostrarse
 * de tres formas distintas, de más a menos urgente:
 *   1) "Vencida con deuda": el viaje ya terminó y sigue sin cobrarse.
 *   2) "Debe — no viaja": está Confirmada dentro de la ventana de aviso (ADR-036/037):
 *      el cliente no puede viajar hasta pagar el total.
 *   3) "Debe": deuda cobrable genérica, sin ninguna de las dos urgencias de arriba.
 */
function deudaCobrableKind(reserva) {
  if (reserva.hasOverdueDebt) {
    return { kind: "vencidaConDeuda", label: "Vencida con deuda", tone: "danger" };
  }
  if (reserva.status === "Confirmed" && reserva.isWithinUnpaidAlertWindow === true) {
    return { kind: "debeNoViaja", label: "Debe — no viaja", tone: "danger" };
  }
  return { kind: "debe", label: "Debe", tone: "danger" };
}
