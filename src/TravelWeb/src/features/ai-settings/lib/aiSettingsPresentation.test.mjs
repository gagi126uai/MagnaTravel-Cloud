import { test } from "node:test";
import assert from "node:assert/strict";
import {
  AI_API_KEY_FIELD_MODE,
  AI_API_KEY_SOURCES,
  AI_CONNECTION_TEST_CODES,
  AI_PROVIDER_CODE_OTHER,
  AI_SCREEN_MODE,
  AI_SETTINGS_STATUS_CODES,
  AI_VALIDATION_CODES,
  calcularModoCampoClave,
  construirAyudaClave,
  construirFotoEstado,
  construirResultadoPrueba,
  debeDeshabilitarBotonGuardar,
  detectarErrorDeClaveFaltante,
  esErrorDeClaveFaltante,
  formatearSegundos,
  puedeVerConfiguracionIa,
  refrescarFotoTrasPrueba,
  resolverModoDePantalla,
  validarAjustesAvanzados,
} from "./aiSettingsPresentation.js";

// ─── Gate de Admin (§15.1) ─────────────────────────────────────────────────

test("puedeVerConfiguracionIa: true solo cuando esAdmin es estrictamente true", () => {
  assert.equal(puedeVerConfiguracionIa(true), true);
  assert.equal(puedeVerConfiguracionIa(false), false);
  assert.equal(puedeVerConfiguracionIa(undefined), false);
  assert.equal(puedeVerConfiguracionIa(null), false);
});

// ─── Foto de estado (§15.5) ────────────────────────────────────────────────

test("construirFotoEstado: funcionando usa el nombre del proveedor, nunca el codigo", () => {
  const foto = construirFotoEstado({
    statusCode: AI_SETTINGS_STATUS_CODES.WORKING,
    providerDisplayName: "Groq",
    providerCode: "groq",
  });
  assert.equal(foto.emoji, "🟢");
  assert.equal(foto.texto, "Funcionando con Groq");
});

test("construirFotoEstado: ultima prueba fallo queda en ambar con el nombre del proveedor", () => {
  const foto = construirFotoEstado({
    statusCode: AI_SETTINGS_STATUS_CODES.LAST_TEST_FAILED,
    providerDisplayName: "Claude",
    providerCode: "claude",
  });
  assert.equal(foto.emoji, "🟠");
  assert.equal(foto.texto, "Configurada con Claude, pero la última prueba no anduvo.");
});

test("construirFotoEstado: sin configurar es el texto firmado en la spec", () => {
  const foto = construirFotoEstado({ statusCode: AI_SETTINGS_STATUS_CODES.NOT_CONFIGURED });
  assert.equal(foto.emoji, "⚪");
  assert.equal(foto.texto, "Sin configurar — el sistema funciona igual, sin las ayudas inteligentes.");
});

test("construirFotoEstado: default seguro — un codigo desconocido NUNCA se muestra crudo", () => {
  const foto = construirFotoEstado({ statusCode: "algo-que-el-front-no-conoce-todavia" });
  assert.equal(foto.emoji, "⚪");
  assert.doesNotMatch(foto.texto, /algo-que-el-front-no-conoce-todavia/);
});

test("construirFotoEstado: proveedor 'Otra' (fix menor 5) — funcionando sin nombre, se entiende solo", () => {
  const foto = construirFotoEstado({
    statusCode: AI_SETTINGS_STATUS_CODES.WORKING,
    providerDisplayName: "Otra",
    providerCode: AI_PROVIDER_CODE_OTHER,
  });
  assert.equal(foto.texto, "Funcionando.");
  assert.doesNotMatch(foto.texto, /Otra/);
});

test("construirFotoEstado: proveedor 'Otra' con la ultima prueba fallida tambien queda sin nombre", () => {
  const foto = construirFotoEstado({
    statusCode: AI_SETTINGS_STATUS_CODES.LAST_TEST_FAILED,
    providerDisplayName: "Otra",
    providerCode: AI_PROVIDER_CODE_OTHER,
  });
  assert.equal(foto.texto, "Configurada, pero la última prueba no anduvo.");
  assert.doesNotMatch(foto.texto, /Otra/);
});

// ─── Segundos con coma (es-AR) ─────────────────────────────────────────────

test("formatearSegundos: 800ms se muestra 0,8 (coma, un decimal)", () => {
  assert.equal(formatearSegundos(800), "0,8");
});

test("formatearSegundos: redondea a un solo decimal", () => {
  assert.equal(formatearSegundos(1234), "1,2");
});

test("formatearSegundos: valor invalido no rompe, cae a 0,0", () => {
  assert.equal(formatearSegundos(undefined), "0,0");
  assert.equal(formatearSegundos(null), "0,0");
  assert.equal(formatearSegundos("no-es-un-numero"), "0,0");
});

// ─── Resultado de "Probar conexion" (§15.4) — por codigo, con default seguro ──
// construirResultadoPrueba devuelve { texto, esExito } (fix reviewer menor 3): el color
// del componente se decide por esExito, no adivinando con un startsWith sobre el texto.

test("construirResultadoPrueba: ok arma la frase con segundos en coma y esExito true", () => {
  const resultado = construirResultadoPrueba({ resultCode: AI_CONNECTION_TEST_CODES.OK, elapsedMilliseconds: 800 });
  assert.equal(resultado.texto, "Funciona ✓ (contestó en 0,8 s)");
  assert.equal(resultado.esExito, true);
});

test("construirResultadoPrueba: clave invalida -> esExito false", () => {
  const resultado = construirResultadoPrueba({ resultCode: AI_CONNECTION_TEST_CODES.INVALID_KEY });
  assert.equal(resultado.texto, "✕ La clave no sirve o venció.");
  assert.equal(resultado.esExito, false);
});

test("construirResultadoPrueba: direccion invalida", () => {
  const resultado = construirResultadoPrueba({ resultCode: AI_CONNECTION_TEST_CODES.INVALID_ADDRESS });
  assert.equal(resultado.texto, "✕ Esa dirección no responde. Revisá que esté bien escrita.");
  assert.equal(resultado.esExito, false);
});

test("construirResultadoPrueba: modelo inexistente", () => {
  const resultado = construirResultadoPrueba({ resultCode: AI_CONNECTION_TEST_CODES.MODEL_NOT_FOUND });
  assert.equal(resultado.texto, "✕ Ese modelo no existe para este proveedor.");
  assert.equal(resultado.esExito, false);
});

test("construirResultadoPrueba: no responde", () => {
  const resultado = construirResultadoPrueba({ resultCode: AI_CONNECTION_TEST_CODES.NO_RESPONSE });
  assert.equal(resultado.texto, "✕ No hay conexión con el proveedor. Probá de nuevo en un rato.");
  assert.equal(resultado.esExito, false);
});

test("construirResultadoPrueba: default seguro (d) — un resultCode inventado NUNCA aparece en el texto", () => {
  const resultado = construirResultadoPrueba({ resultCode: "unCodigoInventadoQueElFrontNoConoce" });
  assert.doesNotMatch(resultado.texto, /unCodigoInventadoQueElFrontNoConoce/);
  assert.match(resultado.texto, /No hay conexión/);
  assert.equal(resultado.esExito, false);
});

// ─── Modo del campo Clave — write-only (§15.3 + §15.8) ────────────────────

test("calcularModoCampoClave: sin clave en ningun lado -> vacia", () => {
  const modo = calcularModoCampoClave({
    hasApiKey: false,
    apiKeySource: AI_API_KEY_SOURCES.NONE,
    queriendoCambiarClave: false,
    cambioDeProveedor: false,
  });
  assert.equal(modo, AI_API_KEY_FIELD_MODE.EMPTY);
});

test("calcularModoCampoClave: guardada por el dueño, mismo proveedor, sin tocar -> configurada", () => {
  const modo = calcularModoCampoClave({
    hasApiKey: true,
    apiKeySource: AI_API_KEY_SOURCES.SAVED,
    queriendoCambiarClave: false,
    cambioDeProveedor: false,
  });
  assert.equal(modo, AI_API_KEY_FIELD_MODE.CONFIGURED);
});

test("calcularModoCampoClave: guardada + 'Cambiar la clave' -> cambiando", () => {
  const modo = calcularModoCampoClave({
    hasApiKey: true,
    apiKeySource: AI_API_KEY_SOURCES.SAVED,
    queriendoCambiarClave: true,
    cambioDeProveedor: false,
  });
  assert.equal(modo, AI_API_KEY_FIELD_MODE.CHANGING);
});

test("calcularModoCampoClave: clave del servidor (respaldo) -> respaldoServidor, incluso si 'queriendoCambiarClave' quedo prendido de antes", () => {
  const modo = calcularModoCampoClave({
    hasApiKey: true,
    apiKeySource: AI_API_KEY_SOURCES.SERVER,
    queriendoCambiarClave: true,
    cambioDeProveedor: false,
  });
  assert.equal(modo, AI_API_KEY_FIELD_MODE.SERVER_FALLBACK);
});

test("calcularModoCampoClave: FIX BLOQUEANTE B1 — clave guardada pero cambiaste el proveedor -> vacia (pide clave nueva)", () => {
  // Este es el escenario exacto del bug reportado: hay clave guardada (de Groq), el
  // usuario tilda OpenAI en los radios. Antes esto quedaba en "configurada" mostrando el
  // prefijo de Groq al lado de "Configurada ✓" — un dato falso.
  const modo = calcularModoCampoClave({
    hasApiKey: true,
    apiKeySource: AI_API_KEY_SOURCES.SAVED,
    queriendoCambiarClave: false,
    cambioDeProveedor: true,
  });
  assert.equal(modo, AI_API_KEY_FIELD_MODE.EMPTY);
});

test("calcularModoCampoClave: cambio de proveedor le gana incluso al respaldo del servidor", () => {
  const modo = calcularModoCampoClave({
    hasApiKey: true,
    apiKeySource: AI_API_KEY_SOURCES.SERVER,
    queriendoCambiarClave: false,
    cambioDeProveedor: true,
  });
  assert.equal(modo, AI_API_KEY_FIELD_MODE.EMPTY);
});

test("construirAyudaClave: cada modo tiene su frase firmada en la spec", () => {
  assert.equal(construirAyudaClave(AI_API_KEY_FIELD_MODE.EMPTY, "Groq", "groq"), "Te la da Groq en su página, al crear una cuenta.");
  assert.equal(construirAyudaClave(AI_API_KEY_FIELD_MODE.CHANGING, "Groq", "groq"), "Pegá la nueva. La anterior se reemplaza al guardar.");
  assert.equal(
    construirAyudaClave(AI_API_KEY_FIELD_MODE.SERVER_FALLBACK, "Groq", "groq"),
    "La puso el técnico al instalar. Si pegás una acá, manda la tuya."
  );
});

test("construirAyudaClave: proveedor 'Otra' (fix menor 5) — ayuda neutra, 'Te la da Otra' no se entiende", () => {
  const ayuda = construirAyudaClave(AI_API_KEY_FIELD_MODE.EMPTY, "Otra", AI_PROVIDER_CODE_OTHER);
  assert.equal(ayuda, "Te la da el servicio que uses, en su página.");
  assert.doesNotMatch(ayuda, /Otra/);
});

// ─── Boton Guardar (§15.8: "apagado hasta que haya clave") ────────────────

test("debeDeshabilitarBotonGuardar: sin clave configurada y sin tipear nada -> deshabilitado", () => {
  assert.equal(debeDeshabilitarBotonGuardar({ hasApiKey: false, claveTipeada: "", guardando: false }), true);
});

test("debeDeshabilitarBotonGuardar: sin clave configurada pero ya tipeo una -> habilitado", () => {
  assert.equal(debeDeshabilitarBotonGuardar({ hasApiKey: false, claveTipeada: "gsk_algo", guardando: false }), false);
});

test("debeDeshabilitarBotonGuardar: ya hay clave configurada -> habilitado aunque no tipee nada nuevo", () => {
  assert.equal(debeDeshabilitarBotonGuardar({ hasApiKey: true, claveTipeada: "", guardando: false }), false);
});

test("debeDeshabilitarBotonGuardar: mientras guarda, siempre deshabilitado (no se puede disparar dos veces)", () => {
  assert.equal(debeDeshabilitarBotonGuardar({ hasApiKey: true, claveTipeada: "", guardando: true }), true);
});

// ─── Error de campo "cambiaste de proveedor sin pegar clave nueva" (§15.8) ─

test("esErrorDeClaveFaltante (fallback de texto): detecta el mensaje CON nombre de proveedor", () => {
  assert.equal(esErrorDeClaveFaltante("Pegá la clave de OpenAI para poder usarla."), true);
});

test("esErrorDeClaveFaltante (fallback de texto): detecta el mensaje SIN nombre (caso 'Otra')", () => {
  assert.equal(esErrorDeClaveFaltante("Pegá la clave para poder usarla."), true);
});

test("esErrorDeClaveFaltante (fallback de texto): no confunde otros errores de guardado", () => {
  assert.equal(esErrorDeClaveFaltante("Completá la dirección y el modelo para poder guardar."), false);
  assert.equal(esErrorDeClaveFaltante(""), false);
  assert.equal(esErrorDeClaveFaltante(null), false);
  assert.equal(esErrorDeClaveFaltante(undefined), false);
});

test("detectarErrorDeClaveFaltante (fix reviewer menor 4): prioriza el codigo estructurado del motor", () => {
  // El motor ya manda CodedValidationException con code "aiClaveFaltante" (ver
  // src/TravelApi.Domain/Exceptions/CodedValidationException.cs), expuesto en
  // ProblemDetails.Extensions.validationCode. Un mensaje que NO calza con el texto viejo
  // igual se detecta correctamente si trae el codigo.
  const detectado = detectarErrorDeClaveFaltante({
    validationCode: AI_VALIDATION_CODES.API_KEY_MISSING,
    mensaje: "Un mensaje distinto que ya no matchea el texto viejo.",
  });
  assert.equal(detectado, true);
});

test("detectarErrorDeClaveFaltante: sin codigo, cae al fallback de texto", () => {
  const detectado = detectarErrorDeClaveFaltante({
    validationCode: undefined,
    mensaje: "Pegá la clave de OpenAI para poder usarla.",
  });
  assert.equal(detectado, true);
});

test("detectarErrorDeClaveFaltante: ni codigo ni texto conocido -> false", () => {
  const detectado = detectarErrorDeClaveFaltante({
    validationCode: "otroCodigoCualquiera",
    mensaje: "Completá la dirección y el modelo para poder guardar.",
  });
  assert.equal(detectado, false);
});

// ─── "Otra" exige Direccion y Modelo (§15.6 "quedan obligatorios") — fix menor 2 ──

test("validarAjustesAvanzados: proveedor normal (con preset) -> nunca exige nada, no es 'Otra'", () => {
  const { baseUrlError, modelError } = validarAjustesAvanzados({ requiresManualEndpoint: false, baseUrl: "", model: "" });
  assert.equal(baseUrlError, null);
  assert.equal(modelError, null);
});

test("validarAjustesAvanzados: 'Otra' con ambos campos vacios -> error corto en los dos", () => {
  const { baseUrlError, modelError } = validarAjustesAvanzados({ requiresManualEndpoint: true, baseUrl: "", model: "" });
  assert.equal(baseUrlError, "Completá la dirección.");
  assert.equal(modelError, "Completá el modelo.");
});

test("validarAjustesAvanzados: 'Otra' con solo espacios en blanco -> sigue contando como vacio", () => {
  const { baseUrlError, modelError } = validarAjustesAvanzados({ requiresManualEndpoint: true, baseUrl: "   ", model: "  " });
  assert.equal(baseUrlError, "Completá la dirección.");
  assert.equal(modelError, "Completá el modelo.");
});

test("validarAjustesAvanzados: 'Otra' con los dos campos cargados -> sin error", () => {
  const { baseUrlError, modelError } = validarAjustesAvanzados({
    requiresManualEndpoint: true,
    baseUrl: "https://mi-servidor.com/v1",
    model: "mi-modelo",
  });
  assert.equal(baseUrlError, null);
  assert.equal(modelError, null);
});

// ─── Modo de pantalla — fix BLOQUEANTE B2 (cargando / error de carga / formulario) ──

test("resolverModoDePantalla: mientras carga, es 'cargando' sin importar si ya hay un loadError viejo", () => {
  assert.equal(resolverModoDePantalla({ loading: true, loadError: null }), AI_SCREEN_MODE.LOADING);
  assert.equal(resolverModoDePantalla({ loading: true, loadError: "algo" }), AI_SCREEN_MODE.LOADING);
});

test("resolverModoDePantalla: (b) GET inicial fallido -> 'errorDeCarga', NUNCA 'formulario'", () => {
  const modo = resolverModoDePantalla({ loading: false, loadError: "No se pudo cargar la configuración." });
  assert.equal(modo, AI_SCREEN_MODE.LOAD_ERROR);
  assert.notEqual(modo, AI_SCREEN_MODE.FORM);
});

test("resolverModoDePantalla: sin loading y sin error -> 'formulario'", () => {
  assert.equal(resolverModoDePantalla({ loading: false, loadError: null }), AI_SCREEN_MODE.FORM);
});

test("resolverModoDePantalla: los 3 modos son mutuamente excluyentes para cualquier combinacion", () => {
  const combinaciones = [
    { loading: true, loadError: null },
    { loading: true, loadError: "x" },
    { loading: false, loadError: "x" },
    { loading: false, loadError: null },
  ];
  for (const combinacion of combinaciones) {
    const modo = resolverModoDePantalla(combinacion);
    assert.ok(Object.values(AI_SCREEN_MODE).includes(modo));
  }
});

// ─── Refresco de la foto tras "Probar conexión" — fix BLOQUEANTE B3 ───────────

test("refrescarFotoTrasPrueba: (c) si el refresco falla, NO relanza — devuelve null en vez de tirar", async () => {
  const obtenerSettingsQueFalla = async () => {
    throw new Error("la red se cortó justo despues de la prueba");
  };
  // Si esto lanzara, el catch de handleProbarConexion lo agarraría y pisaría el "Funciona
  // ✓" que ya se mostró — por eso la funcion tiene que devolver null, nunca relanzar.
  const resultado = await refrescarFotoTrasPrueba(obtenerSettingsQueFalla);
  assert.equal(resultado, null);
});

test("refrescarFotoTrasPrueba: si el refresco funciona, devuelve el DTO nuevo tal cual", async () => {
  const dtoNuevo = { statusCode: AI_SETTINGS_STATUS_CODES.WORKING, providerCode: "groq" };
  const resultado = await refrescarFotoTrasPrueba(async () => dtoNuevo);
  assert.deepEqual(resultado, dtoNuevo);
});
