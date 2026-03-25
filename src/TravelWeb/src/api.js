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
  if (!errorText) {
    return {
      message: response.statusText || "Request failed",
      code: null,
      payload: null,
    };
  }

  try {
    const data = JSON.parse(errorText);

    if (Array.isArray(data) && data.length > 0) {
      return { message: data.join(", "), code: null, payload: data };
    }

    if (data.errors && typeof data.errors === "object") {
      const errorMessages = Object.values(data.errors).flat();
      return {
        message: errorMessages.length > 0 ? errorMessages.join("\n") : "Error de validacion",
        code: data.code || null,
        payload: data,
      };
    }

    if (data?.message) {
      return { message: data.message, code: data.code || null, payload: data };
    }

    if (data?.title) {
      return { message: data.title, code: data.code || null, payload: data };
    }

    if (data?.error) {
      return { message: data.error, code: data.code || null, payload: data };
    }

    return { message: JSON.stringify(data), code: data.code || null, payload: data };
  } catch {
    return {
      message: errorText,
      code: null,
      payload: errorText,
    };
  }
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

    if (response.status === 401 && !retried && isRefreshEligiblePath(finalPath)) {
      try {
        await refreshSession();
        return executeRequest(true);
      } catch {
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
