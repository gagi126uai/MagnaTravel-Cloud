/**
 * Reglas puras del "Plan de pagos" del presupuesto (obra "PDF ronda 2", decisión firmada del
 * dueño, 2026-08-14, spec §6): una tabla de filas [cuándo / monto / moneda] que se dibuja bajo
 * la tarjeta de total del PDF. Es 100% informativo — no toca cobranzas ni la cuenta corriente
 * del cliente, mismo criterio que "Formas de pago" (ver paymentTermsCardLogic.js, hermano de
 * este archivo).
 *
 * Archivo `.js` PURO (sin JSX), mismo criterio que el resto de esta carpeta: se testea con
 * `node --test` sin montar React. El componente (`PaymentPlanCard.jsx`) solo llama a `api` y a
 * estas funciones — la cuenta de "¿está completo?", "¿cambió algo?", "¿arma el payload bien?"
 * vive acá para poder probarla sin jsdom.
 */

// Tope del backend (ReservaService.UpdatePaymentPlanAsync, obra "PDF ronda 2"): 24 filas
// alcanzan de sobra para cualquier plan de pagos real. Repetido acá para frenar en pantalla
// ANTES de pegarle al servidor — el backend igual revalida, esto es solo para no hacerle
// perder tiempo al vendedor con un 400 tardío.
export const MAX_FILAS_PLAN_DE_PAGOS = 24;

/**
 * Fila vacía nueva para el botón "+ Agregar fila". `key` se recibe de afuera (el componente
 * lleva un contador propio) para que esta función siga siendo pura y testeable sin depender
 * de `Date.now()`/`Math.random()`.
 *
 * @param {string|number} key - identificador estable para el `key` de React (no viaja al backend).
 * @param {string} monedaPorDefecto - moneda a precargar en la fila nueva.
 */
export function crearFilaVacia(key, monedaPorDefecto) {
  return { key, dueText: "", amount: "", currency: monedaPorDefecto || "ARS" };
}

/**
 * Moneda con la que arranca una fila nueva: la de la reserva si el back la informó (primera
 * línea de `reserva.porMoneda`, el desglose multimoneda que ya usa el resto de la ficha — ver
 * ADR-021 Capa 5), o "ARS" si la reserva todavía no tiene ninguna línea de plata cargada.
 *
 * @param {{porMoneda?: Array<{currency?: string}>}|null|undefined} reserva
 * @returns {string}
 */
export function resolverMonedaPorDefectoDelPlan(reserva) {
  const primeraLinea = reserva?.porMoneda?.[0];
  return primeraLinea?.currency || "ARS";
}

/**
 * Convierte las filas que ya vinieron del backend (`ReservaDto.paymentPlanInstallments`, ya
 * ordenadas por `position`) al shape que usa la tabla en pantalla. `position` se usa como
 * `key` de React porque en la carga inicial es estable (no se puede insertar en el medio, ver
 * el XML-doc de `BudgetPaymentPlanInstallment.Position` en el backend).
 *
 * @param {Array<{position?:number, dueText?:string, amount?:number, currency?:string}>|null|undefined} installments
 * @returns {Array<{key:string, dueText:string, amount:string, currency:string}>}
 */
export function filasDesdeInstallments(installments) {
  const lista = installments || [];
  return lista.map((fila, indice) => ({
    key: `plan-${fila.position ?? indice}`,
    dueText: fila.dueText || "",
    amount: fila.amount != null ? String(fila.amount) : "",
    currency: fila.currency || "ARS",
  }));
}

/**
 * Representación comparable de una lista de filas (sin el `key`, que es solo de React) — sirve
 * para detectar "¿el vendedor cambió algo respecto de lo último guardado?", mismo rol que
 * `textoFormasDePagoFueEditado` en la card hermana.
 */
function serializarFilas(filas) {
  return JSON.stringify(
    (filas || []).map((fila) => ({
      dueText: (fila.dueText || "").trim(),
      amount: fila.amount === "" || fila.amount == null ? null : Number(fila.amount),
      currency: fila.currency || "ARS",
    }))
  );
}

/**
 * True cuando la tabla actual es distinta de la última versión guardada (o precargada al
 * abrir la ficha) — dispara el autoguardado (debounce) en el componente.
 */
export function filasFueronEditadas(filasActuales, filasPrecargadas) {
  return serializarFilas(filasActuales) !== serializarFilas(filasPrecargadas);
}

/**
 * True cuando TODAS las filas tienen los dos datos obligatorios cargados (texto de "cuándo" +
 * monto mayor a 0 — mismas reglas que valida el backend en `UpdatePaymentPlanAsync`). Una
 * tabla vacía (sin filas) también cuenta como "completa": es el caso de "el vendedor borró
 * todo el plan", que sí hay que guardar (el PUT con lista vacía BORRA el plan, spec §6).
 *
 * El componente usa esto para NO disparar el autoguardado mientras hay una fila a medio
 * completar (el vendedor recién tipeó el monto y todavía no escribió el "cuándo") — evita
 * mandar al backend algo que sabemos que va a rebotar con un error, y evita mostrarle un
 * cartel de error por algo que todavía está escribiendo.
 */
export function filasEstanCompletas(filas) {
  return (filas || []).every((fila) => (fila.dueText || "").trim().length > 0 && Number(fila.amount) > 0);
}

/**
 * True cuando la tabla superó el tope de filas (backend + frontend, mismo número). El
 * componente usa esto para frenar el autoguardado y mostrar el aviso amable ANTES de llamar
 * al backend, en vez de dejar que rebote con un 400.
 */
export function filasExcedenElMaximo(filas) {
  return (filas || []).length > MAX_FILAS_PLAN_DE_PAGOS;
}

/**
 * Arma el body de `PUT /reservas/{id}/budget-payment-plan` (reemplazo total de la lista, ver
 * `UpdatePaymentPlanRequest` en el backend). El orden de `filas` en pantalla ES el orden en
 * que se numeran las posiciones 1..N — el backend no reordena.
 *
 * @param {Array<{dueText:string, amount:string|number, currency:string}>} filas
 * @returns {{installments: Array<{dueText:string, amount:number, currency:string}>}}
 */
export function armarPayloadPlanDePagos(filas) {
  return {
    installments: (filas || []).map((fila) => ({
      dueText: (fila.dueText || "").trim(),
      amount: Number(fila.amount) || 0,
      currency: fila.currency || "ARS",
    })),
  };
}
