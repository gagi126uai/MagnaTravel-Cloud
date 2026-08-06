import { test } from "node:test";
import assert from "node:assert/strict";
import { isSessionDefinitelyInvalid } from "./sessionRefreshFailure.js";
import { getApiErrorMessage, SPANISH_NETWORK_GENERIC } from "./errors.js";

// Hallazgo 2026-08-06 (revision de seguridad): antes de este fix, api.js trataba CUALQUIER
// falla al intentar refrescar la sesion (429 por limite de pedidos, 5xx del backend, un error
// de red) como "la sesion murio" y deslogueaba al usuario. Estos tests fijan que SOLO un 401
// real del propio /auth/refresh cuenta como sesion invalida.

test("un 401 real del refresh SI significa que la sesion es invalida", () => {
  const refreshError = Object.assign(new Error("No autorizado"), { status: 401 });
  assert.equal(isSessionDefinitelyInvalid(refreshError), true);
});

test("un 429 (limite de pedidos, por ejemplo tras una rafaga de reconexion post-deploy) NO significa sesion invalida", () => {
  const refreshError = Object.assign(new Error("Demasiadas solicitudes"), { status: 429 });
  assert.equal(isSessionDefinitelyInvalid(refreshError), false);
});

test("un 500 del backend (falla transitoria) NO significa sesion invalida", () => {
  const refreshError = Object.assign(new Error("Error interno"), { status: 500 });
  assert.equal(isSessionDefinitelyInvalid(refreshError), false);
});

test("un 503 (backend caido un instante, por ejemplo durante el reinicio de un deploy) NO significa sesion invalida", () => {
  const refreshError = Object.assign(new Error("Servicio no disponible"), { status: 503 });
  assert.equal(isSessionDefinitelyInvalid(refreshError), false);
});

test("un error de red sin status (fetch nunca llego a responder) NO significa sesion invalida", () => {
  const networkError = new TypeError("Failed to fetch");
  assert.equal(isSessionDefinitelyInvalid(networkError), false);
});

test("un error nulo o indefinido NO significa sesion invalida", () => {
  assert.equal(isSessionDefinitelyInvalid(null), false);
  assert.equal(isSessionDefinitelyInvalid(undefined), false);
});

// Hallazgo B-1 del gate de exposicion de datos (2026-08-06): cuando el refresh de
// sesion falla por red o por un 5xx, api.js NO debe relanzar el error crudo del
// browser (por ejemplo el TypeError "Failed to fetch") hacia las ~30 pantallas
// que pintan error.message tal cual. Este test replica exactamente la traduccion
// que hace api.js en ese catch (usando getApiErrorMessage, el mismo mapeo que ya
// usa el resto del cliente HTTP) para dejar fijado que el mensaje final SIEMPRE
// es espanol, nunca el texto tecnico del browser.
//
// Nota: no ejecuta el codigo real de api.js (que hace fetch() de verdad y no es
// facil de aislar sin mockear el modulo entero) — cubre la funcion pura de la que
// depende el fix. Ver tambien errors.test.mjs, que prueba getApiErrorMessage
// directamente con un TypeError "Failed to fetch".
test("un error de red del refresh se traduce a espanol antes de llegar a la pantalla (no queda 'Failed to fetch')", () => {
  const refreshError = new TypeError("Failed to fetch");

  const mensajeQueVeElUsuario = getApiErrorMessage(refreshError, SPANISH_NETWORK_GENERIC);

  assert.equal(mensajeQueVeElUsuario, SPANISH_NETWORK_GENERIC);
  assert.doesNotMatch(mensajeQueVeElUsuario, /fetch/i, "el texto tecnico del browser nunca debe llegar al usuario");
});

test("un error 5xx del refresh sin payload del servidor tambien se traduce a espanol", () => {
  const refreshError = Object.assign(new Error("Service Unavailable"), { status: 503 });

  const mensajeQueVeElUsuario = getApiErrorMessage(refreshError, SPANISH_NETWORK_GENERIC);

  assert.equal(mensajeQueVeElUsuario, SPANISH_NETWORK_GENERIC);
});
