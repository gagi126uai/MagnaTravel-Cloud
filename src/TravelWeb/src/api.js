import { normalizeMessage, SPANISH_NETWORK_GENERIC, getApiErrorMessage } from "./lib/errors";
import { esErrorDeMantenimiento } from "./features/admin/lib/dangerRestoreLogic";
import { activateMaintenance } from "./maintenanceState";
import { isSessionDefinitelyInvalid } from "./lib/sessionRefreshFailure";

const configuredApiUrl = (import.meta.env.VITE_API_URL || "").trim();

function normalizeBasePath(pathname) {
  return pathname.replace(/\/$/, "").replace(/\/api$/i, "");
}

function joinBaseUrl(baseUrl, path) {
  const cleanBaseUrl = baseUrl.replace(/\/$/, "");
  const cleanPath = path.startsWith("/") ? path : `/${path}`;
  return `${cleanBaseUrl}${cleanPath}`;
}

function resolveAppBaseUrl() {
  if (typeof window === "undefined") {
    try {
      const serverUrl = new URL(configuredApiUrl || "http://localhost:5000");
      return `${serverUrl.origin}${normalizeBasePath(serverUrl.pathname)}`;
    } catch {
      return "http://localhost:5000";
    }
  }

  const currentOrigin = window.location.origin;
  const isLocalDevelopment =
    window.location.hostname === "localhost" || window.location.hostname === "127.0.0.1";

  if (!configuredApiUrl) {
    return currentOrigin;
  }

  try {
    const configuredUrl = new URL(configuredApiUrl, currentOrigin);
    const normalizedConfiguredBase = `${configuredUrl.origin}${normalizeBasePath(configuredUrl.pathname)}`;

    if (!isLocalDevelopment && configuredUrl.origin !== currentOrigin) {
      return currentOrigin;
    }

    return normalizedConfiguredBase || currentOrigin;
  } catch {
    return currentOrigin;
  }
}

export const APP_BASE_URL = resolveAppBaseUrl();
export const API_BASE_URL = joinBaseUrl(APP_BASE_URL, "/api");

export function buildAppUrl(path) {
  return joinBaseUrl(APP_BASE_URL, path);
}

export function buildApiUrl(path) {
  const cleanPath = path.startsWith("/") ? path : `/${path}`;
  const finalPath = cleanPath.startsWith("/api") ? cleanPath : `/api${cleanPath}`;
  return joinBaseUrl(APP_BASE_URL, finalPath);
}

function getCookieValue(name) {
  if (typeof document === "undefined") {
    return "";
  }

  const escapedName = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const match = document.cookie.match(new RegExp(`(?:^|; )${escapedName}=([^;]*)`));
  return match ? decodeURIComponent(match[1]) : "";
}

export function hasSessionCookieHint() {
  return Boolean(getCookieValue("mt_csrf"));
}

function isMutationMethod(method) {
  return ["POST", "PUT", "PATCH", "DELETE"].includes((method || "GET").toUpperCase());
}

function shouldSetJsonContentType(body, headers) {
  if (body instanceof FormData || body === undefined) {
    return false;
  }

  return !Object.keys(headers).some((header) => header.toLowerCase() === "content-type");
}

function mergeHeaders(options = {}) {
  const headers = { ...(options.headers || {}) };

  if (shouldSetJsonContentType(options.body, headers)) {
    headers["Content-Type"] = "application/json";
  }

  if (isMutationMethod(options.method)) {
    const csrfToken = getCookieValue("mt_csrf");
    if (csrfToken) {
      headers["X-CSRF-Token"] = csrfToken;
    }
  }

  return headers;
}

async function parseErrorResponse(response) {
  const errorText = await response.text();

  let errorInfo;
  if (!errorText) {
    errorInfo = {
      message: response.statusText || "Request failed",
      code: null,
      payload: null,
    };
  } else {
    try {
      const data = JSON.parse(errorText);
      errorInfo = {
        message: normalizeMessage(data, response.statusText || "Request failed"),
        code: data?.code || null,
        payload: data,
      };
    } catch {
      // Fix bug real de PROD (plan tanda F): si el cuerpo NO parsea como JSON, no es una
      // respuesta armada por nuestro motor (que siempre manda JSON en sus errores, sea
      // "application/json" o "application/problem+json" en los 400 de validación) — es
      // típicamente la página de error HTML de un proxy/gateway intermedio (nginx del HOST,
      // timeout de 60s, ver dangerRestoreLogic.js + nginx.conf: una restauración total tarda
      // minutos y ese nginx corta antes, devolviendo su propio 502/504 con HTML en el body).
      // Antes ese HTML se guardaba tal cual en `message`/`payload` y se mostraba LITERAL en
      // pantalla a un usuario no programador. Ningún consumidor de `error.payload` en el
      // resto del front espera un string (todos leen `.code`/`.message`/`.invariantCode` de
      // un objeto — grep confirmado), así que dejarlo en `null` acá no rompe ningún flujo
      // real; caemos al mismo mensaje genérico de conexión que ya usa el resto de la app.
      errorInfo = {
        message: SPANISH_NETWORK_GENERIC,
        code: null,
        payload: null,
      };
    }
  }

  // Obra 2026-07-27 "Restaurar todo": mientras el motor está restaurando el sistema
  // completo, CUALQUIER pedido a /api/** devuelve 503 con code="MAINTENANCE" (contrato
  // nuevo, ver dangerRestoreLogic.js). Se detecta ACÁ, en el único lugar donde se arma el
  // error de cualquier pedido fallido (tanto el pedido normal como el retry de refresh de
  // sesión), para prender la pantalla de mantenimiento global sin importar qué pantalla
  // estaba pidiendo qué cosa cuando el 503 llegó.
  if (esErrorDeMantenimiento({ status: response.status, code: errorInfo.code })) {
    activateMaintenance();
  }

  return errorInfo;
}

async function parseResponse(response, responseType) {
  if (response.status === 204) {
    return null;
  }

  if (responseType === "blob") {
    return response.blob();
  }

  const text = await response.text();
  if (!text) {
    return null;
  }

  return JSON.parse(text);
}

function isRefreshEligiblePath(path) {
  return ![
    "/api/auth/login",
    "/api/auth/register",
    "/api/auth/refresh",
    "/api/auth/logout",
  ].includes(path);
}

let refreshPromise = null;

async function refreshSession() {
  if (!hasSessionCookieHint()) {
    const error = new Error("No active session");
    error.status = 401;
    throw error;
  }

  if (!refreshPromise) {
    refreshPromise = fetch(buildApiUrl("/auth/refresh"), {
      method: "POST",
      credentials: "include",
      headers: mergeHeaders({ method: "POST" }),
    })
      .then(async (response) => {
        if (!response.ok) {
          const errorInfo = await parseErrorResponse(response);
          const error = new Error(errorInfo.message);
          error.status = response.status;
          error.code = errorInfo.code;
          error.payload = errorInfo.payload;
          throw error;
        }

        return parseResponse(response);
      })
      .finally(() => {
        refreshPromise = null;
      });
  }

  return refreshPromise;
}

export async function apiRequest(path, options = {}) {
  const cleanPath = path.startsWith("/") ? path : `/${path}`;
  const finalPath = cleanPath.startsWith("/api") ? cleanPath : `/api${cleanPath}`;

  const executeRequest = async (retried = false) => {
    const response = await fetch(buildApiUrl(finalPath), {
      ...options,
      credentials: "include",
      headers: mergeHeaders(options),
    });

    if (response.status === 401 && !retried && isRefreshEligiblePath(finalPath) && hasSessionCookieHint()) {
      try {
        await refreshSession();
        return executeRequest(true);
      } catch (refreshError) {
        // Hallazgo 2026-08-06 (revision de seguridad): SOLO un 401 real del propio
        // /auth/refresh significa "la sesion ya no es valida" (sin cookie de sesion, o el
        // backend la rechazo explicitamente por vencida/revocada). Un 429 (limite de
        // pedidos — puede pasar en una rafaga de reconexion tras un deploy), un 5xx
        // (problema transitorio del backend) o un error de red (fetch nunca llego a
        // responder, sin "status" en el error) NO significan que la sesion este muerta:
        // son fallas pasajeras. Antes de este fix, CUALQUIERA de esos casos deslogueaba al
        // usuario aunque su sesion siguiera siendo perfectamente valida — el usuario podia
        // reintentar la accion mas tarde sin necesidad de volver a loguearse.
        if (!isSessionDefinitelyInvalid(refreshError)) {
          // No relanzamos el error crudo del refresh: si vino de un fallo de red
          // (TypeError "Failed to fetch" del browser) o de un statusText bare del
          // servidor, ese texto en ingles llegaria intacto a ~30 pantallas que
          // pintan error.message tal cual. Lo traducimos con el mismo mapeo que
          // ya usa el resto del cliente HTTP (getApiErrorMessage), conservando
          // status/code/payload por si alguna pantalla los necesita.
          const fallaPasajera = new Error(getApiErrorMessage(refreshError, SPANISH_NETWORK_GENERIC));
          fallaPasajera.status = refreshError?.status ?? null;
          fallaPasajera.code = refreshError?.code ?? null;
          fallaPasajera.payload = refreshError?.payload ?? null;
          throw fallaPasajera;
        }

        if (!options.skipAuthRedirect && typeof window !== "undefined") {
          window.dispatchEvent(new Event("auth:unauthorized"));
        }
      }
    }

    if (!response.ok) {
      if (response.status === 401 && !options.skipAuthRedirect && typeof window !== "undefined") {
        window.dispatchEvent(new Event("auth:unauthorized"));
      }

      const errorInfo = await parseErrorResponse(response);
      const error = new Error(errorInfo.message);
      error.status = response.status;
      error.code = errorInfo.code;
      error.payload = errorInfo.payload;
      throw error;
    }

    return parseResponse(response, options.responseType);
  };

  return executeRequest();
}

function createRequestOptions(method, data, options = {}) {
  const requestOptions = {
    ...options,
    method,
  };

  if (data !== undefined) {
    requestOptions.body = data instanceof FormData ? data : JSON.stringify(data);
  }

  return requestOptions;
}

export const api = {
  get: (url, options) => apiRequest(url, { ...options, method: "GET" }),
  post: (url, data, options = {}) => apiRequest(url, createRequestOptions("POST", data, options)),
  put: (url, data, options = {}) => apiRequest(url, createRequestOptions("PUT", data, options)),
  delete: (url, options) => apiRequest(url, { ...options, method: "DELETE" }),
  patch: (url, data, options = {}) => apiRequest(url, createRequestOptions("PATCH", data, options)),
};
