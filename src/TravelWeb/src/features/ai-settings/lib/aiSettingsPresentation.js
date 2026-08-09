// Reglas de presentacion de "Configuracion → Inteligencia artificial" (spec firmada
// 2026-08-07, docs/ux/specs/2026-08-07-tarifario-inteligente-FIRMADA.md §15).
//
// Todo lo que decide QUE TEXTO mostrar segun un codigo vive aca, separado del componente
// visual, para poder probarlo con tests simples (sin renderizar React) y para que el
// componente no se llene de "if" repetidos. Regla P-17 de la constitucion: cero jerga
// tecnica en las frases, y ningun codigo crudo llega jamas a la pantalla — todo tiene un
// "default seguro" (una frase generica) para codigos que el front todavia no conoce.

// ─── Codigos que manda el motor (tienen que ser IGUAL de texto que el backend) ────────
// Ver src/TravelApi.Application/DTOs/AiSettingsDtos.cs — son los mismos strings, el motor
// los manda tal cual y el front SOLO elige la frase (nunca compara mensajes de texto libre).

export const AI_SETTINGS_STATUS_CODES = {
  NOT_CONFIGURED: "sinConfigurar",
  WORKING: "funcionando",
  LAST_TEST_FAILED: "ultimaPruebaFallo",
};

export const AI_API_KEY_SOURCES = {
  NONE: "ninguna",
  SAVED: "guardada",
  SERVER: "servidor",
};

export const AI_CONNECTION_TEST_CODES = {
  OK: "ok",
  INVALID_KEY: "claveInvalida",
  NO_RESPONSE: "noResponde",
  INVALID_ADDRESS: "direccionInvalida",
  MODEL_NOT_FOUND: "modeloInexistente",
};

// Codigo del proveedor "Otra" (ver src/TravelApi.Application/Ai/AiProviderPresets.cs,
// Code: "otra"): es el unico donde NO tiene sentido nombrar "el proveedor" en las frases
// ("Te la da Otra en su página" no se entiende — "Otra" no es el nombre de nadie).
export const AI_PROVIDER_CODE_OTHER = "otra";

// Codigos de CodedValidationException que puede tirar el guardado (ver
// src/TravelApi.Domain/Exceptions/CodedValidationException.cs). Viajan en
// ProblemDetails.Extensions.validationCode — separado de "code" para no romper el
// contrato existente (T-13: decidir por dato estructurado, no adivinando el texto).
export const AI_VALIDATION_CODES = {
  API_KEY_MISSING: "aiClaveFaltante",
};

// Modos posibles del campo "Clave" (§15.3 + §15.8). Son 4, no 3, porque el caso "la puso
// el tecnico por el servidor" necesita su propia ayuda ("si pegas una, manda la tuya") y
// no tiene prefijo para mostrar (el motor nunca guarda un prefijo de la clave del servidor).
export const AI_API_KEY_FIELD_MODE = {
  EMPTY: "vacia",
  CONFIGURED: "configurada",
  CHANGING: "cambiando",
  SERVER_FALLBACK: "respaldoServidor",
};

/**
 * Solo Admin ve esta pantalla (§15.1): la solapa NO existe para nadie mas, ni apagada.
 * Es una funcion trivial a proposito: separarla permite un test que documenta la regla
 * sin tener que renderizar el SettingsPage completo.
 */
export function puedeVerConfiguracionIa(esAdmin) {
  return esAdmin === true;
}

/**
 * La foto de arriba, en una sola linea (§15.5). SIEMPRE usa el nombre del proveedor en
 * criollo (providerDisplayName), nunca el codigo interno ("groq"). Si el motor mandara
 * un codigo que el front todavia no conoce, cae a "sin configurar" en vez de romper o
 * mostrar un codigo crudo — ese es el "default seguro".
 *
 * Caso "Otra" (fix reviewer, hallazgo menor 5): "Funcionando con Otra" no se entiende
 * ("Otra" no es el nombre de ningun servicio) — para ese codigo la frase queda sin nombre.
 */
export function construirFotoEstado({ statusCode, providerDisplayName, providerCode }) {
  const esOtra = providerCode === AI_PROVIDER_CODE_OTHER;
  const nombre = providerDisplayName || "la inteligencia artificial";

  switch (statusCode) {
    case AI_SETTINGS_STATUS_CODES.WORKING:
      return esOtra ? { emoji: "🟢", texto: "Funcionando." } : { emoji: "🟢", texto: `Funcionando con ${nombre}` };
    case AI_SETTINGS_STATUS_CODES.LAST_TEST_FAILED:
      return esOtra
        ? { emoji: "🟠", texto: "Configurada, pero la última prueba no anduvo." }
        : { emoji: "🟠", texto: `Configurada con ${nombre}, pero la última prueba no anduvo.` };
    case AI_SETTINGS_STATUS_CODES.NOT_CONFIGURED:
    default:
      return {
        emoji: "⚪",
        texto: "Sin configurar — el sistema funciona igual, sin las ayudas inteligentes.",
      };
  }
}

/**
 * Pasa milisegundos a segundos con COMA decimal (es-AR) y un solo decimal, para armar
 * "contestó en 0,8 s" (§15.4). Un numero invalido no rompe: se muestra como "0,0".
 */
export function formatearSegundos(elapsedMilliseconds) {
  const milisegundos = Number(elapsedMilliseconds);
  const segundos = Number.isFinite(milisegundos) ? milisegundos / 1000 : 0;
  return segundos.toFixed(1).replace(".", ",");
}

/**
 * La frase de "Probar conexion", al lado del boton (§15.4). El motivo viaja por CODIGO
 * (T-13, patron 2026-07-22): el front solo elige la frase y jamas muestra el mensaje
 * crudo del proveedor ni un codigo. Un resultCode desconocido cae al "default seguro":
 * una frase generica de "no anduvo", nunca el codigo pelado.
 *
 * Devuelve { texto, esExito } en vez de un string pelado (fix reviewer, hallazgo menor 3):
 * asi el componente pinta el color por el booleano, no adivinando con un
 * `texto.startsWith("Funciona")` fragil ante un cambio de redaccion.
 */
export function construirResultadoPrueba({ resultCode, elapsedMilliseconds }) {
  if (resultCode === AI_CONNECTION_TEST_CODES.OK) {
    return { esExito: true, texto: `Funciona ✓ (contestó en ${formatearSegundos(elapsedMilliseconds)} s)` };
  }

  switch (resultCode) {
    case AI_CONNECTION_TEST_CODES.INVALID_KEY:
      return { esExito: false, texto: "✕ La clave no sirve o venció." };
    case AI_CONNECTION_TEST_CODES.INVALID_ADDRESS:
      return { esExito: false, texto: "✕ Esa dirección no responde. Revisá que esté bien escrita." };
    case AI_CONNECTION_TEST_CODES.MODEL_NOT_FOUND:
      return { esExito: false, texto: "✕ Ese modelo no existe para este proveedor." };
    case AI_CONNECTION_TEST_CODES.NO_RESPONSE:
    default:
      // Default seguro: cualquier codigo no contemplado (incluido uno nuevo que el motor
      // sume manana) cae aca, con una frase generica en vez de romper o mostrar el codigo.
      return { esExito: false, texto: "✕ No hay conexión con el proveedor. Probá de nuevo en un rato." };
  }
}

/**
 * Que modo mostrar en el campo "Clave" (§15.3 + §15.8).
 *
 * Fix reviewer (bloqueante B1): si el usuario cambio de proveedor en los radios respecto
 * al que tiene la clave guardada (`cambioDeProveedor`), esa clave YA NO SIRVE para el
 * proveedor elegido — el modo tiene que pedir una nueva, aunque `hasApiKey` siga en true
 * (la clave vieja sigue "utilizable", pero para OTRO proveedor). Antes de este fix, el
 * campo seguia mostrando "Configurada ✓" con el prefijo del proveedor viejo y el Guardar
 * fallaba en silencio (el error de campo no tenia donde renderizarse).
 *
 * @param {object} params
 * @param {boolean} params.hasApiKey - hay una clave utilizable (guardada o del servidor).
 * @param {string} params.apiKeySource - uno de AI_API_KEY_SOURCES.
 * @param {boolean} params.queriendoCambiarClave - el usuario apreto "Cambiar la clave".
 * @param {boolean} params.cambioDeProveedor - el proveedor elegido en pantalla es distinto
 *   al que tiene la clave guardada (settings.providerCode).
 */
export function calcularModoCampoClave({ hasApiKey, apiKeySource, queriendoCambiarClave, cambioDeProveedor }) {
  if (!hasApiKey || cambioDeProveedor) {
    return AI_API_KEY_FIELD_MODE.EMPTY;
  }

  if (apiKeySource === AI_API_KEY_SOURCES.SERVER) {
    return AI_API_KEY_FIELD_MODE.SERVER_FALLBACK;
  }

  return queriendoCambiarClave ? AI_API_KEY_FIELD_MODE.CHANGING : AI_API_KEY_FIELD_MODE.CONFIGURED;
}

/**
 * La linea de ayuda del campo Clave. Es la UNICA excepcion a P-15 (nada de leyendas
 * redundantes) que autoriza esta pantalla en particular (§15.2): el dato viene de afuera
 * del sistema, asi que una linea corta por campo esta permitida aca. No se replica en
 * el resto de la app.
 *
 * Caso "Otra" (fix reviewer, hallazgo menor 5): "Te la da Otra en su página" no se
 * entiende, asi que para ese codigo la ayuda queda neutra.
 */
export function construirAyudaClave(modo, providerDisplayName, providerCode) {
  const esOtra = providerCode === AI_PROVIDER_CODE_OTHER;
  const nombre = providerDisplayName || "el proveedor";

  switch (modo) {
    case AI_API_KEY_FIELD_MODE.SERVER_FALLBACK:
      return "La puso el técnico al instalar. Si pegás una acá, manda la tuya.";
    case AI_API_KEY_FIELD_MODE.CHANGING:
      return "Pegá la nueva. La anterior se reemplaza al guardar.";
    case AI_API_KEY_FIELD_MODE.EMPTY:
    default:
      return esOtra ? "Te la da el servicio que uses, en su página." : `Te la da ${nombre} en su página, al crear una cuenta.`;
  }
}

/**
 * "Guardar" arranca apagado cuando todavia no hay ninguna clave utilizable y el usuario
 * tampoco tipeo una nueva en este momento (§15.8: "Guardar apagado hasta que haya clave").
 * Una vez que hay clave (de cualquier origen) o el usuario tipeo una, el boton se habilita;
 * la validacion fina (por ejemplo "cambiaste de proveedor y no pegaste clave nueva") la hace
 * el motor al guardar y su respuesta se muestra como error de campo (ver
 * `detectarErrorDeClaveFaltante`).
 */
export function debeDeshabilitarBotonGuardar({ hasApiKey, claveTipeada, guardando }) {
  if (guardando) return true;
  const hayClaveTipeada = typeof claveTipeada === "string" && claveTipeada.trim().length > 0;
  if (!hasApiKey && !hayClaveTipeada) return true;
  return false;
}

/**
 * Detecta el rechazo puntual del motor "cambiaste de proveedor pero no pegaste la clave
 * nueva" (§15.8) SOLO por el texto exacto firmado en la spec. Es el fallback: usar
 * `detectarErrorDeClaveFaltante` (abajo), que mira primero el codigo estructurado.
 *
 * Se mantiene exportada porque el motor puede llegar a mandar este mismo mensaje por una
 * ruta vieja que todavia no migro a CodedValidationException (ver comentario en
 * GlobalExceptionHandler.cs) — sin el fallback, esa ruta caeria al cartel rojo general.
 */
export function esErrorDeClaveFaltante(mensaje) {
  return typeof mensaje === "string" && mensaje.trim().startsWith("Pegá la clave");
}

/**
 * Fix reviewer (hallazgo menor 4): el motor YA manda un codigo estructurado propio para
 * este rechazo puntual (CodedValidationException, code "aiClaveFaltante", expuesto en
 * ProblemDetails.Extensions.validationCode — ver GlobalExceptionHandler.cs). Se mira ESE
 * codigo primero (T-13); el match de texto (`esErrorDeClaveFaltante`) queda como
 * fallback secundario para una respuesta vieja que todavia no traiga el codigo.
 *
 * @param {object} params
 * @param {string|null|undefined} params.validationCode - error.payload?.validationCode
 * @param {string|null|undefined} params.mensaje - el mensaje ya resuelto (getApiErrorMessage)
 */
export function detectarErrorDeClaveFaltante({ validationCode, mensaje }) {
  if (validationCode === AI_VALIDATION_CODES.API_KEY_MISSING) {
    return true;
  }
  return esErrorDeClaveFaltante(mensaje);
}

/**
 * Fix reviewer (hallazgo menor 2): con "Otra" (§15.6, "quedan obligatorios"), Dirección y
 * Modelo son obligatorios ANTES de mandar el guardado — se valida en el cliente para
 * usabilidad (mostrar el error pegado al campo en vez de esperar el viaje al servidor),
 * NUNCA como reemplazo de la validacion real del motor (que igual la vuelve a hacer).
 *
 * Con cualquier otro proveedor no hay nada que validar aca: esos campos ya vienen
 * precargados con el valor recomendado del preset.
 */
export function validarAjustesAvanzados({ requiresManualEndpoint, baseUrl, model }) {
  if (!requiresManualEndpoint) {
    return { baseUrlError: null, modelError: null };
  }

  const baseUrlError = baseUrl && baseUrl.trim() ? null : "Completá la dirección.";
  const modelError = model && model.trim() ? null : "Completá el modelo.";
  return { baseUrlError, modelError };
}

// ─── Los 3 "modos de pantalla", mutuamente excluyentes (fix reviewer B2) ──────────────

export const AI_SCREEN_MODE = {
  LOADING: "cargando",
  LOAD_ERROR: "errorDeCarga",
  FORM: "formulario",
};

/**
 * Que rama dibuja la pantalla: renglones grises, cartel de error + Reintentar, o el
 * formulario completo. Los 3 son EXCLUYENTES a proposito (fix reviewer, bloqueante B2):
 * antes, un GET inicial fallido dejaba el formulario a medias en pantalla (radios vacios,
 * "Probar conexión" habilitado disparando con providerCode: "", sin ninguna salida). El
 * patron firmado (guia-ux-gaston.md, "cargando → deshabilitado; error → cartel +
 * Reintentar") pide que, ante un error de carga, NO se dibuje el formulario en absoluto.
 */
export function resolverModoDePantalla({ loading, loadError }) {
  if (loading) return AI_SCREEN_MODE.LOADING;
  if (loadError) return AI_SCREEN_MODE.LOAD_ERROR;
  return AI_SCREEN_MODE.FORM;
}

/**
 * Refresca la foto de estado despues de "Probar conexión", sin dejar que un fallo de ESE
 * refresco pise el resultado de la prueba que ya se mostro (fix reviewer, bloqueante B3).
 *
 * Antes, el GET de refresco vivia adentro del MISMO try que la prueba: si la prueba
 * contestaba "Funciona ✓" pero el refresco fallaba (por ejemplo, se corto la conexion un
 * instante despues), el catch comun pisaba el resultado con "✕ No se pudo probar la
 * conexión" — un mensaje FALSO, porque la prueba si habia andado.
 *
 * Esta funcion es "best effort": si `obtenerSettings` explota, devuelve null en vez de
 * relanzar. Asi, quien la llama NUNCA necesita un try/catch alrededor — estructuralmente
 * no hay forma de que un fallo de acá contamine el resultado de la prueba.
 *
 * @param {() => Promise<object>} obtenerSettings - normalmente `() => api.get("/settings/ai")`
 * @returns {Promise<object|null>} el AiSettingsDto nuevo, o null si el refresco fallo.
 */
export async function refrescarFotoTrasPrueba(obtenerSettings) {
  try {
    return await obtenerSettings();
  } catch {
    return null;
  }
}
