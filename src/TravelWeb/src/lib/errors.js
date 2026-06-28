export function isDatabaseUnavailableError(error) {
  return error?.code === "database_unavailable" || error?.status === 503;
}

// ─── Detección de errores de transporte y statusText genérico ─────────────────

// Mensaje que mostramos al usuario cuando el error es de red/transporte,
// sin información útil provista por el servidor.
export const SPANISH_NETWORK_GENERIC =
  "No se pudo conectar. Revisá tu conexión e intentá de nuevo.";

// Strings exactos (case-insensitive) que identifican un error de transporte puro.
// Provienen del runtime del browser o del fetch API, no del servidor.
// Usamos un Set para comparación O(1).
const TRANSPORT_ERROR_EXACT = new Set([
  "failed to fetch",
  "network request failed",
  "load failed",
  "networkerror when attempting to fetch resource",
  "the internet connection appears to be offline",
]);

// Strings exactos que son bare HTTP statusText sin payload del servidor.
// Si el servidor hubiera enviado un cuerpo con payload.message, esa ruta
// tiene prioridad y este check nunca se alcanza (ver getApiErrorMessage).
//
// Esta lista cubre los statusTexts más comunes que el browser/fetch asigna
// cuando el servidor responde sin body o con body vacío. El match es EXACTO
// (case-insensitive vía toLowerCase) para no interceptar mensajes del servidor
// en español que contengan estas palabras como parte de un texto más largo.
const HTTP_STATUSTEXT_EXACT = new Set([
  // 4xx
  "bad request",          // 400
  "unauthorized",         // 401
  "forbidden",            // 403
  "not found",            // 404
  "too many requests",    // 429
  // 5xx
  "internal server error", // 500
  "bad gateway",           // 502
  "service unavailable",   // 503
  "gateway timeout",       // 504
  // Genérico de algunos clientes fetch/XHR
  "request failed",
]);

/**
 * Devuelve true si el mensaje es un error de transporte de red o un bare HTTP
 * statusText sin contexto del servidor. En esos casos el mensaje es en inglés
 * y no aporta información útil para el usuario de la agencia.
 *
 * Solo se compara por coincidencia EXACTA (case-insensitive) para no interceptar
 * mensajes del servidor que contengan esas palabras como parte de un texto más largo.
 */
function esErrorDeTransporteOStatusText(mensaje) {
  if (typeof mensaje !== "string") return false;
  const lower = mensaje.trim().toLowerCase();
  return TRANSPORT_ERROR_EXACT.has(lower) || HTTP_STATUSTEXT_EXACT.has(lower);
}

function tryParseJsonString(value) {
  const trimmed = value.trim();
  if (!trimmed || (!trimmed.startsWith("{") && !trimmed.startsWith("["))) {
    return null;
  }

  try {
    return JSON.parse(trimmed);
  } catch {
    return null;
  }
}

function normalizeValidationErrors(errors) {
  if (!errors || typeof errors !== "object") {
    return "";
  }

  return Object.values(errors)
    .flat()
    .filter(Boolean)
    .join("\n");
}

export function normalizeMessage(value, fallback = "Error desconocido") {
  if (value === null || value === undefined || value === "") {
    return fallback;
  }

  if (typeof value === "string") {
    // Si el string es un error de transporte/red o un bare HTTP statusText,
    // reemplazarlo con el genérico en español antes de mostrarlo al usuario.
    // Esto cubre "Failed to fetch" (Chrome/Edge), "Load failed" (Safari),
    // "Network request failed" (algunos entornos), e "Internal Server Error" (bare 500).
    if (esErrorDeTransporteOStatusText(value)) {
      return SPANISH_NETWORK_GENERIC;
    }

    const parsed = tryParseJsonString(value);
    if (parsed !== null) {
      return normalizeMessage(parsed, fallback);
    }

    return value;
  }

  if (value instanceof Error) {
    return getApiErrorMessage(value, fallback);
  }

  if (Array.isArray(value)) {
    const message = value.map((item) => normalizeMessage(item, "")).filter(Boolean).join(", ");
    return message || fallback;
  }

  if (typeof value === "object") {
    const validationMessage = normalizeValidationErrors(value.errors);
    if (validationMessage) {
      return validationMessage;
    }

    return (
      normalizeMessage(value.message, "") ||
      normalizeMessage(value.error, "") ||
      normalizeMessage(value.detail, "") ||
      normalizeMessage(value.title, "") ||
      fallback
    );
  }

  return String(value);
}

export function getApiErrorMessage(error, fallback = "Error desconocido") {
  if (!error) {
    return fallback;
  }

  if (error.payload !== undefined && error.payload !== null) {
    const payloadMessage = normalizeMessage(error.payload, "");
    if (payloadMessage) {
      return payloadMessage;
    }
  }

  return normalizeMessage(error.message || error, fallback);
}
