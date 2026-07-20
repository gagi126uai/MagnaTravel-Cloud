/**
 * Tests de lógica pura para CancelarVariosServiciosInline.
 *
 * Testea:
 *   - calcularTotalPorMoneda: agrupación correcta por moneda (sin mezclar).
 *   - filtrarServiciosCancelables: qué servicios aparecen como candidatos.
 *   - extraerMensajeError: lectura del error real del cliente api.js (fetch nativo).
 *   - clasificarResultadoFinal: clasificación del resultado tras el proceso secuencial,
 *     incluyendo el caso de ÉXITO PARCIAL (el más crítico según el reviewer).
 *
 * Regla dura multimoneda verificada: nunca se suman pesos con dólares.
 *
 * Nota sobre el shape del error:
 *   Este proyecto usa fetch nativo (api.js), NO axios. Los errores tienen:
 *     error.message  → string normalizado (campo principal)
 *     error.status   → número HTTP
 *     error.code     → string|null
 *     error.payload  → body JSON del backend
 *   NO tienen error.response ni error.response.data (eso es axios).
 *
 * Cómo correr: node --test src/features/reservas/components/cancelarVariosServiciosInline.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import { normalizeMessage, SPANISH_NETWORK_GENERIC } from "../../../lib/errors.js";

// ─── Lógica pura copiada de CancelarVariosServiciosInline.jsx ────────────────
// (Copiada en vez de importada porque el runner es Node puro sin bundler.
//  Si cambia la función original, actualizar acá también.)

function calcularTotalPorMoneda(servicios) {
  return servicios.reduce((acumulador, svc) => {
    const moneda = svc.currency || "ARS";
    acumulador[moneda] = (acumulador[moneda] || 0) + (svc.salePrice || 0);
    return acumulador;
  }, {});
}

function filtrarServiciosCancelables(services) {
  return (services || []).filter((svc) => {
    const tieneProveedor = Boolean(svc.supplierPublicId || svc.supplierId || svc.supplierName);
    const esTipoEspecifico = svc.recordKind && svc.recordKind !== "generic";
    const estaCancelado = (svc.workflowStatus || svc.status) === "Cancelado";
    return (tieneProveedor || esTipoEspecifico) && !estaCancelado;
  });
}

/**
 * El cliente api.js lanza errores con:
 *   error.message  → string normalizado (campo principal, SIEMPRE presente si el cliente lo armó)
 *   error.status   → número HTTP
 *   error.payload  → body JSON del backend
 *
 * Esta función extrae el mensaje más legible disponible. Es réplica exacta de
 * la del componente (que no se puede importar por ser .jsx), pero delega en el
 * MISMO normalizeMessage real de lib/errors.js — así el filtro de textos de
 * transporte en inglés se prueba de verdad, no una copia que pueda divergir.
 */
function extraerMensajeError(error, fallback) {
  if (!error) return fallback;

  if (typeof error.message === "string" && error.message) {
    return normalizeMessage(error.message, fallback);
  }

  const payload = error?.payload;
  if (payload !== undefined && payload !== null) {
    const mensajePayload = normalizeMessage(payload, "");
    if (mensajePayload) return mensajePayload;
  }

  return fallback;
}

/**
 * Clasifica el resultado final del proceso de cancelación secuencial.
 *
 * @param {Array} resultados - Array de { svc, ok, mensajeError?, esBloqueo409 }
 * @returns {{ todosOk, algunoFallo, totalExitos, totalFallos }}
 */
function clasificarResultadoFinal(resultados) {
  if (!resultados || resultados.length === 0) {
    return { todosOk: false, algunoFallo: false, totalExitos: 0, totalFallos: 0 };
  }

  const totalExitos = resultados.filter((r) => r.ok).length;
  const totalFallos = resultados.filter((r) => !r.ok).length;
  const todosOk = totalFallos === 0;
  const algunoFallo = totalFallos > 0;

  return { todosOk, algunoFallo, totalExitos, totalFallos };
}

// ─── Tests: calcularTotalPorMoneda ───────────────────────────────────────────

test("calcularTotalPorMoneda: lista vacía → objeto vacío", () => {
  const resultado = calcularTotalPorMoneda([]);
  assert.deepStrictEqual(resultado, {});
});

test("calcularTotalPorMoneda: todos ARS → una sola clave ARS", () => {
  const servicios = [
    { salePrice: 10000, currency: "ARS" },
    { salePrice: 5000, currency: "ARS" },
  ];
  const resultado = calcularTotalPorMoneda(servicios);
  assert.deepStrictEqual(resultado, { ARS: 15000 });
});

test("calcularTotalPorMoneda: ARS + USD → dos claves separadas (regla dura multimoneda)", () => {
  const servicios = [
    { salePrice: 10000, currency: "ARS" },
    { salePrice: 300, currency: "USD" },
    { salePrice: 5000, currency: "ARS" },
    { salePrice: 200, currency: "USD" },
  ];
  const resultado = calcularTotalPorMoneda(servicios);
  // Regla dura: NUNCA se suman 15000 ARS + 500 USD en un mismo número.
  assert.deepStrictEqual(resultado, { ARS: 15000, USD: 500 });
});

test("calcularTotalPorMoneda: servicio sin currency → se asume ARS", () => {
  const servicios = [
    { salePrice: 8000 },          // sin currency
    { salePrice: 2000, currency: "ARS" },
  ];
  const resultado = calcularTotalPorMoneda(servicios);
  assert.deepStrictEqual(resultado, { ARS: 10000 });
});

test("calcularTotalPorMoneda: servicio sin salePrice → se trata como 0", () => {
  const servicios = [
    { currency: "ARS" },          // sin salePrice
    { salePrice: 5000, currency: "ARS" },
  ];
  const resultado = calcularTotalPorMoneda(servicios);
  assert.deepStrictEqual(resultado, { ARS: 5000 });
});

test("calcularTotalPorMoneda: tres monedas distintas → tres claves", () => {
  const servicios = [
    { salePrice: 100, currency: "USD" },
    { salePrice: 80, currency: "EUR" },
    { salePrice: 1000, currency: "ARS" },
  ];
  const resultado = calcularTotalPorMoneda(servicios);
  assert.equal(Object.keys(resultado).length, 3);
  assert.equal(resultado.USD, 100);
  assert.equal(resultado.EUR, 80);
  assert.equal(resultado.ARS, 1000);
});

// ─── Tests: filtrarServiciosCancelables ──────────────────────────────────────

test("filtrarServiciosCancelables: excluye cancelados", () => {
  const services = [
    { recordKind: "hotel", workflowStatus: "Cancelado", supplierName: "Hilton" },
    { recordKind: "flight", workflowStatus: "Emitido", supplierName: "AA" },
  ];
  const resultado = filtrarServiciosCancelables(services);
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].recordKind, "flight");
});

test("filtrarServiciosCancelables: excluye genérico sin proveedor", () => {
  const services = [
    { recordKind: "generic", workflowStatus: "Solicitado" }, // sin supplier
    { recordKind: "hotel", workflowStatus: "Confirmado", supplierName: "Plaza" },
  ];
  const resultado = filtrarServiciosCancelables(services);
  assert.equal(resultado.length, 1);
  assert.equal(resultado[0].recordKind, "hotel");
});

test("filtrarServiciosCancelables: incluye genérico CON proveedor", () => {
  const services = [
    { recordKind: "generic", workflowStatus: "Solicitado", supplierName: "Proveedor" },
  ];
  const resultado = filtrarServiciosCancelables(services);
  assert.equal(resultado.length, 1);
});

test("filtrarServiciosCancelables: incluye tipo específico sin supplierName", () => {
  // Un vuelo sin supplier en el DTO es igualmente cancelable (tipo específico).
  const services = [
    { recordKind: "flight", workflowStatus: "Solicitado" },
  ];
  const resultado = filtrarServiciosCancelables(services);
  assert.equal(resultado.length, 1);
});

test("filtrarServiciosCancelables: lista vacía → lista vacía", () => {
  assert.deepStrictEqual(filtrarServiciosCancelables([]), []);
});

test("filtrarServiciosCancelables: null → no lanza, devuelve lista vacía", () => {
  assert.deepStrictEqual(filtrarServiciosCancelables(null), []);
});

test("filtrarServiciosCancelables: excluye cancelado vía svc.status (campo alternativo)", () => {
  const services = [
    { recordKind: "hotel", status: "Cancelado", supplierName: "Hotel" },
  ];
  const resultado = filtrarServiciosCancelables(services);
  assert.equal(resultado.length, 0, "cancelado por status.Cancelado debe quedar excluido");
});

// ─── Tests: extraerMensajeError ───────────────────────────────────────────────
// IMPORTANTE: el cliente api.js es fetch nativo, NO axios.
// El error que lanza tiene { message, status, code, payload }.
// NO tiene error.response ni error.response.data.

test("extraerMensajeError: null → devuelve fallback", () => {
  assert.equal(extraerMensajeError(null, "Error genérico"), "Error genérico");
});

test("extraerMensajeError: error con message (shape real del cliente api.js) → lo usa", () => {
  // El cliente api.js siempre pone el mensaje normalizado en error.message.
  const error = new Error("Bloqueo fiscal activo");
  error.status = 409;
  error.code = null;
  error.payload = { title: "Conflict", status: 409 };
  assert.equal(extraerMensajeError(error, "fallback"), "Bloqueo fiscal activo");
});

test("extraerMensajeError: error con message vacío pero payload.message → usa payload", () => {
  // Caso raro: message en blanco, pero el payload del backend tiene detalles.
  const error = { message: "", status: 400, payload: { message: "El motivo es muy corto" } };
  assert.equal(extraerMensajeError(error, "fallback"), "El motivo es muy corto");
});

test("extraerMensajeError: error con payload.title cuando no hay message → lo usa", () => {
  const error = { message: "", status: 422, payload: { title: "Validation Error" } };
  assert.equal(extraerMensajeError(error, "fallback"), "Validation Error");
});

test("extraerMensajeError: error con payload como string → lo usa", () => {
  const error = { message: "", status: 400, payload: "Motivo inválido" };
  assert.equal(extraerMensajeError(error, "fallback"), "Motivo inválido");
});

test("extraerMensajeError: statusText crudo de gateway (sin body del server) → genérico en español, nunca inglés", () => {
  // Un 502/504 del proxy llega sin body JSON; api.js deja el statusText crudo
  // en error.message. La fila fallida del lote NO debe mostrar "Bad Gateway".
  const error = { message: "Bad Gateway", status: 502 };
  assert.equal(extraerMensajeError(error, "fallback"), SPANISH_NETWORK_GENERIC);
});

test("extraerMensajeError: falla de red pura (Failed to fetch) → genérico en español", () => {
  const error = new Error("Failed to fetch");
  assert.equal(extraerMensajeError(error, "fallback"), SPANISH_NETWORK_GENERIC);
});

test("extraerMensajeError: error sin ningún campo útil → devuelve fallback", () => {
  // Un error que solo tiene status, sin message ni payload legible.
  const error = { status: 500 };
  assert.equal(extraerMensajeError(error, "Error desconocido"), "Error desconocido");
});

test("extraerMensajeError: error 409 con message (caso bloqueo fiscal) → mensaje del backend", () => {
  // El backend devuelve un mensaje descriptivo en el 409.
  const error = new Error("La factura tiene CAE vivo y no puede cancelarse.");
  error.status = 409;
  error.payload = { title: "Conflict", status: 409 };
  assert.equal(
    extraerMensajeError(error, "No se pudo cancelar este servicio."),
    "La factura tiene CAE vivo y no puede cancelarse."
  );
});

// ─── Tests: clasificarResultadoFinal ─────────────────────────────────────────
// Este es el caso más crítico: el reviewer detectó que el éxito parcial se ocultaba.

test("clasificarResultadoFinal: lista vacía → todo en false/0", () => {
  const resultado = clasificarResultadoFinal([]);
  assert.deepStrictEqual(resultado, { todosOk: false, algunoFallo: false, totalExitos: 0, totalFallos: 0 });
});

test("clasificarResultadoFinal: null → todo en false/0 (no lanza)", () => {
  const resultado = clasificarResultadoFinal(null);
  assert.deepStrictEqual(resultado, { todosOk: false, algunoFallo: false, totalExitos: 0, totalFallos: 0 });
});

test("clasificarResultadoFinal: ÉXITO TOTAL (todos ok) → todosOk=true, algunoFallo=false", () => {
  // Este era el único caso que mostraba el resultado antes del fix.
  const resultados = [
    { svc: { name: "Vuelo BUE-MIA" }, ok: true, esBloqueo409: false },
    { svc: { name: "Hotel Miami" }, ok: true, esBloqueo409: false },
  ];
  const resultado = clasificarResultadoFinal(resultados);
  assert.equal(resultado.todosOk, true);
  assert.equal(resultado.algunoFallo, false);
  assert.equal(resultado.totalExitos, 2);
  assert.equal(resultado.totalFallos, 0);
});

test("clasificarResultadoFinal: ÉXITO PARCIAL (1 ok, 1 fallo) → todosOk=false, algunoFallo=true", () => {
  // CASO CRÍTICO: antes del fix la sección se cerraba sola y el usuario
  // creía que todo salió bien aunque el segundo servicio quedó sin cancelar.
  const resultados = [
    { svc: { name: "Vuelo BUE-MIA" }, ok: true, esBloqueo409: false },
    { svc: { name: "Hotel Miami" }, ok: false, mensajeError: "Bloqueo fiscal activo", esBloqueo409: true },
  ];
  const resultado = clasificarResultadoFinal(resultados);
  assert.equal(resultado.todosOk, false, "con un fallo, todosOk debe ser false");
  assert.equal(resultado.algunoFallo, true, "con al menos un fallo, algunoFallo debe ser true");
  assert.equal(resultado.totalExitos, 1, "debe contar el servicio que sí se canceló");
  assert.equal(resultado.totalFallos, 1, "debe contar el servicio que falló");
});

test("clasificarResultadoFinal: ÉXITO PARCIAL (2 ok, 1 fallo) → conteos correctos", () => {
  const resultados = [
    { svc: { name: "Vuelo" }, ok: true, esBloqueo409: false },
    { svc: { name: "Hotel" }, ok: true, esBloqueo409: false },
    { svc: { name: "Traslado" }, ok: false, mensajeError: "Tipo no reconocido.", esBloqueo409: false },
  ];
  const resultado = clasificarResultadoFinal(resultados);
  assert.equal(resultado.todosOk, false);
  assert.equal(resultado.algunoFallo, true);
  assert.equal(resultado.totalExitos, 2);
  assert.equal(resultado.totalFallos, 1);
});

test("clasificarResultadoFinal: FALLO TOTAL (todos fallaron) → todosOk=false, algunoFallo=true", () => {
  // Todos fallaron: igual debe mostrar el resultado, no cerrarse sola.
  const resultados = [
    { svc: { name: "Vuelo" }, ok: false, mensajeError: "Error de red.", esBloqueo409: false },
    { svc: { name: "Hotel" }, ok: false, mensajeError: "Bloqueo fiscal.", esBloqueo409: true },
  ];
  const resultado = clasificarResultadoFinal(resultados);
  assert.equal(resultado.todosOk, false);
  assert.equal(resultado.algunoFallo, true);
  assert.equal(resultado.totalExitos, 0);
  assert.equal(resultado.totalFallos, 2);
});

test("clasificarResultadoFinal: identifica correctamente los servicios fallidos por bloqueo 409", () => {
  // Verificamos que podemos filtrar los fallados con bloqueo fiscal
  // para mostrarlos diferenciados en la UI.
  const resultados = [
    { svc: { name: "Vuelo" }, ok: true, esBloqueo409: false },
    { svc: { name: "Hotel" }, ok: false, mensajeError: "Bloqueo fiscal.", esBloqueo409: true },
    { svc: { name: "Traslado" }, ok: false, mensajeError: "Error genérico.", esBloqueo409: false },
  ];
  const { totalExitos, totalFallos } = clasificarResultadoFinal(resultados);
  assert.equal(totalExitos, 1);
  assert.equal(totalFallos, 2);

  // Verificamos que desde resultados se pueden aislar los que son bloqueo 409.
  const conBloqueo409 = resultados.filter((r) => !r.ok && r.esBloqueo409);
  assert.equal(conBloqueo409.length, 1);
  assert.equal(conBloqueo409[0].svc.name, "Hotel");
});
