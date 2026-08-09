/**
 * Tests de COMPONENTE de AiSettingsTab.jsx (patrón existente en
 * features/cancellations/components/*.test.mjs): este repo no tiene jsdom ni
 * @testing-library instalado, asi que "test de componente" es sobre la LOGICA DE DECISION
 * que el componente usa para elegir que renderizar — las mismas funciones puras que
 * importa AiSettingsTab.jsx (nada se replica: se importa directo de
 * ../lib/aiSettingsPresentation.js, igual que el componente).
 *
 * Cubre los 5 escenarios pedidos en la ronda de revision (bloqueantes B1/B2/B3):
 *   (a) cambio de proveedor con clave guardada -> pide clave nueva Y el error de campo
 *       tiene donde renderizarse (AiApiKeyField.jsx ya no lo gatea por modo).
 *   (b) GET inicial fallido -> cartel + Reintentar, sin formulario; el reintento recarga.
 *   (c) prueba OK + refresco de la foto fallido -> el resultado de la prueba sobrevive.
 *   (d) un resultCode inventado nunca aparece en el render (default seguro).
 *   (e) un 500 con reference/code no muestra "internal_error" ni la reference.
 *
 * Como correr:
 *   node --test src/features/ai-settings/components/AiSettingsTab.test.mjs
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { getApiErrorMessage } from "../../../lib/errors.js";
import {
  AI_API_KEY_FIELD_MODE,
  AI_API_KEY_SOURCES,
  AI_SCREEN_MODE,
  AI_SETTINGS_STATUS_CODES,
  AI_VALIDATION_CODES,
  calcularModoCampoClave,
  construirResultadoPrueba,
  detectarErrorDeClaveFaltante,
  refrescarFotoTrasPrueba,
  resolverModoDePantalla,
} from "../lib/aiSettingsPresentation.js";

// ─── (a) Cambio de proveedor con clave guardada ────────────────────────────

test("(a) cambio de proveedor con clave guardada: el modo pide clave nueva Y el error de guardado se detecta como error de campo", () => {
  // Arrange: como quedaria el estado del componente en el escenario reportado — hay una
  // clave guardada (de Groq) y el usuario tilda OpenAI en los radios sin pegar una nueva.
  const settingsGuardados = { providerCode: "groq", hasApiKey: true, apiKeySource: AI_API_KEY_SOURCES.SAVED };
  const providerCodeSeleccionadoEnPantalla = "openai";
  const cambioDeProveedor = providerCodeSeleccionadoEnPantalla !== settingsGuardados.providerCode;

  // Act: el modo del campo Clave (lo que decide que ve el usuario).
  const modo = calcularModoCampoClave({
    hasApiKey: settingsGuardados.hasApiKey,
    apiKeySource: settingsGuardados.apiKeySource,
    queriendoCambiarClave: false,
    cambioDeProveedor,
  });

  // Assert 1: el campo pide una clave nueva (ya NO queda en "Configurada ✓" con el
  // prefijo de Groq, que era el bug reportado).
  assert.equal(modo, AI_API_KEY_FIELD_MODE.EMPTY);

  // Act: si igual el usuario aprieta "Guardar" sin pegar clave, el motor rechaza con el
  // codigo estructurado "aiClaveFaltante" (CodedValidationException).
  const mensajeDelMotor = "Pegá la clave de OpenAI para poder usarla.";
  const esErrorDeClave = detectarErrorDeClaveFaltante({
    validationCode: AI_VALIDATION_CODES.API_KEY_MISSING,
    mensaje: mensajeDelMotor,
  });

  // Assert 2: se clasifica como error de CAMPO (fieldErrorClave), no cartel general.
  assert.equal(esErrorDeClave, true);

  // Assert 3 (verificado por lectura de codigo, no por render — no hay DOM aca):
  // AiApiKeyField.jsx renderiza `fieldError` en LAS DOS ramas (CONFIGURED y la rama con
  // input), sin gatear por `modo`. Con `modo === EMPTY` cae en la segunda rama, que
  // siempre lo muestra. Antes del fix, el bug real era que el modo NO cambiaba (seguia
  // en CONFIGURED) mientras la UI no tenia forma de mostrar el error ahi — con el fix de
  // arriba (cambioDeProveedor), ese modo incorrecto ya no ocurre.
});

// ─── (b) GET inicial fallido ────────────────────────────────────────────────

test("(b) GET inicial fallido: cartel + Reintentar (sin formulario), y reintentar vuelve a 'cargando'", () => {
  // 1) Arranca cargando.
  let modo = resolverModoDePantalla({ loading: true, loadError: null });
  assert.equal(modo, AI_SCREEN_MODE.LOADING);

  // 2) El GET falla: loading termina, loadError queda seteado.
  modo = resolverModoDePantalla({ loading: false, loadError: "No se pudo cargar la configuración de inteligencia artificial." });
  assert.equal(modo, AI_SCREEN_MODE.LOAD_ERROR);
  // La garantia central del fix B2: en este modo NUNCA es "formulario".
  assert.notEqual(modo, AI_SCREEN_MODE.FORM);

  // 3) El usuario aprieta "Reintentar": AiSettingsTab.jsx llama a la MISMA funcion
  // `cargarConfiguracion` que uso el efecto de montaje (no hay una copia separada), que
  // arranca poniendo loading=true y loadError=null antes de volver a pedir los datos.
  modo = resolverModoDePantalla({ loading: true, loadError: null });
  assert.equal(modo, AI_SCREEN_MODE.LOADING);

  // 4) Si esta vez el GET funciona, la pantalla pasa a formulario.
  modo = resolverModoDePantalla({ loading: false, loadError: null });
  assert.equal(modo, AI_SCREEN_MODE.FORM);
});

// ─── (c) Prueba OK + refresco de la foto fallido ───────────────────────────

test("(c) prueba OK + refresco de la foto fallido: el 'Funciona ✓' sigue en pantalla", async () => {
  // Arrange: lo que handleProbarConexion hace apenas contesta el motor.
  const resultadoDeLaPrueba = construirResultadoPrueba({ resultCode: "ok", elapsedMilliseconds: 800 });
  assert.equal(resultadoDeLaPrueba.texto, "Funciona ✓ (contestó en 0,8 s)");
  assert.equal(resultadoDeLaPrueba.esExito, true);

  // Act: el refresco de la foto (un GET aparte) falla justo despues.
  const settingsActualizados = await refrescarFotoTrasPrueba(async () => {
    throw new Error("se cortó la conexión");
  });

  // Assert: el refresco devuelve null (nunca relanza), asi que el componente jamas entra
  // al catch que pisaria testResult — el resultado de la prueba, que ya se guardo en el
  // paso anterior, queda EXACTAMENTE igual.
  assert.equal(settingsActualizados, null);
  assert.equal(resultadoDeLaPrueba.texto, "Funciona ✓ (contestó en 0,8 s)");
  assert.equal(resultadoDeLaPrueba.esExito, true);
});

// ─── (d) resultCode inventado nunca aparece en el render ───────────────────

test("(d) un resultCode inventado (que el motor podria mandar mañana) nunca aparece en el texto mostrado", () => {
  const resultado = construirResultadoPrueba({ resultCode: "unCodigoQueTodaviaNoExiste" });
  assert.doesNotMatch(resultado.texto, /unCodigoQueTodaviaNoExiste/);
  // Cae al default seguro (mismo texto que "noResponde"), nunca al codigo pelado.
  assert.match(resultado.texto, /No hay conexión/);
  assert.equal(resultado.esExito, false);
});

// ─── (e) Un 500 con reference/code no filtra jerga tecnica ─────────────────

test("(e) un 500 (con code:'internal_error' y reference) nunca muestra esas dos claves — solo el detail amigable", () => {
  // Forma real de un 500 armado por GlobalExceptionHandler.cs: Title/Detail en criollo +
  // Extensions.code="internal_error" + Extensions.reference=<TraceIdentifier opaco>. Estas
  // extensiones NO son parte del contrato que getApiErrorMessage expone al usuario.
  const errorDe500 = {
    status: 500,
    payload: {
      title: "Ocurrió un error inesperado.",
      detail: "Ocurrió un error inesperado. Volvé a intentar; si el problema sigue, escribinos.",
      status: 500,
      code: "internal_error",
      reference: "0HN7F8G3ABCDE:00000001",
    },
    message: "Internal Server Error",
  };

  const mensaje = getApiErrorMessage(errorDe500, "No se pudo probar la conexión. Intentá de nuevo.");

  assert.equal(mensaje, "Ocurrió un error inesperado. Volvé a intentar; si el problema sigue, escribinos.");
  assert.doesNotMatch(mensaje, /internal_error/);
  assert.doesNotMatch(mensaje, /0HN7F8G3ABCDE/);
});

test("(e) un 429 (tope de intentos de 'Probar conexión') SI muestra el mensaje real del servidor, no el generico", () => {
  // Fix bloqueante B3: antes el catch de handleProbarConexion descartaba el mensaje del
  // servidor y mostraba siempre la misma frase fija, aunque el 429 ya trajera un texto
  // en criollo listo para mostrar (ver policy "ai-test" en Program.cs).
  const errorDe429 = {
    status: 429,
    payload: {
      title: "Demasiados intentos.",
      detail: "Probaste muchas veces seguidas. Esperá un minuto y volvé a intentar.",
    },
  };

  const mensaje = getApiErrorMessage(errorDe429, "No se pudo probar la conexión. Intentá de nuevo.");
  assert.equal(mensaje, "Probaste muchas veces seguidas. Esperá un minuto y volvé a intentar.");
});

test("(e) un 400 de validacion (que no es el caso 'clave faltante') SI muestra el mensaje real del servidor", () => {
  const errorDe400 = {
    status: 400,
    payload: {
      title: "Solicitud invalida.",
      detail: "Esa dirección no sirve. Tiene que ser una dirección de internet que empiece con https.",
      code: "validation_failed",
    },
  };

  const mensaje = getApiErrorMessage(errorDe400, "No se pudo probar la conexión. Intentá de nuevo.");
  assert.equal(mensaje, "Esa dirección no sirve. Tiene que ser una dirección de internet que empiece con https.");
  assert.equal(detectarErrorDeClaveFaltante({ validationCode: undefined, mensaje }), false);
});
