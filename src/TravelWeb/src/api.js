const configuredApiUrl = (import.meta.env.VITE_API_URL || "").trim();

function resolveApiBaseUrl() {
  if (typeof window === "undefined") {
    return configuredApiUrl || "http://localhost:5000";
  }

  const currentOrigin = window.location.origin;
  const isLocalDevelopment =
    window.location.hostname === "localhost" || window.location.hostname === "127.0.0.1";

  if (!configuredApiUrl) {
    return currentOrigin;
  }

  try {
    const configuredUrl = new URL(configuredApiUrl, currentOrigin);
    const normalizedConfiguredBase = `${configuredUrl.origin}${configuredUrl.pathname.replace(/\/$/, "")}`;

    // In production the app is served behind the web nginx proxy, so same-origin /api
    // avoids fragile cross-origin CORS/proxy setups.
    if (!isLocalDevelopment && configuredUrl.origin !== currentOrigin) {
      return currentOrigin;
    }

    return normalizedConfiguredBase;
  } catch {
    return currentOrigin;
  }
}

export const API_BASE_URL = resolveApiBaseUrl();

export function buildApiUrl(path) {
  const cleanBaseUrl = API_BASE_URL.replace(/\/$/, "");
  const cleanPath = path.startsWith("/") ? path : `/${path}`;

  if (cleanBaseUrl.endsWith("/api") && cleanPath === "/api") {
    return cleanBaseUrl;
  }

  if (cleanBaseUrl.endsWith("/api") && cleanPath.startsWith("/api/")) {
    return `${cleanBaseUrl}${cleanPath.slice(4)}`;
  }

  return `${cleanBaseUrl}${cleanPath}`;
}

export async function apiRequest(path, options = {}) {
  const token = localStorage.getItem("token");
  const headers = {
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
    ...(options.headers || {}),
  };

  // Only add default Content-Type if not FormData and not already set
  if (!(options.body instanceof FormData) && !headers["Content-Type"]) {
    headers["Content-Type"] = "application/json";
  }

  const cleanPath = path.startsWith("/") ? path : `/${path}`;

  // Auto-prepend /api if missing (Common issue with VITE_API_URL configuration)
  const finalPath = cleanPath.startsWith("/api") ? cleanPath : `/api${cleanPath}`;

  const response = await fetch(buildApiUrl(finalPath), {
    ...options,
    headers,
  });

  if (!response.ok) {
    if (response.status === 401) {
      // Dispatch global event for session expiration
      window.dispatchEvent(new Event('auth:unauthorized'));
    }

    let message = "Request failed";
    const errorText = await response.text();
    if (errorText) {
      try {
        const data = JSON.parse(errorText);

        // Case 1: Array of strings ["Error 1", "Error 2"]
        if (Array.isArray(data) && data.length > 0) {
          message = data.join(", ");
        }
        // Case 2: FluentValidation / RFC 7807 Problem Details { errors: { Field: ["Error"] } }
        else if (data.errors && typeof data.errors === 'object') {
          const errorMessages = Object.values(data.errors).flat();
          message = errorMessages.length > 0 ? errorMessages.join("\n") : "Error de validación";
        }
        // Case 3: Standard object { message: "Error" }
        else if (data?.message) {
          message = data.message;
        }
        // Case 4: Any other object
        else {
          // Try to find any property that looks like a message
          message = data.error || data.title || JSON.stringify(data);
        }
      } catch {
        message = errorText; // Fallback to raw text
      }
    }
    throw new Error(message);
  }

  if (response.status === 204) {
    return null;
  }

  if (options.responseType === 'blob') {
    return response.blob();
  }

  // Handle empty responses (e.g., DELETE returning 200 with no body)
  const text = await response.text();
  if (!text) {
    return null;
  }

  return JSON.parse(text);
}

export const api = {
  get: (url, options) => apiRequest(url, { ...options, method: 'GET' }),
  post: (url, data, options = {}) => {
    const isFormData = data instanceof FormData;
    return apiRequest(url, {
      ...options,
      method: 'POST',
      body: isFormData ? data : JSON.stringify(data)
    });
  },
  put: (url, data, options = {}) => {
    const isFormData = data instanceof FormData;
    return apiRequest(url, {
      ...options,
      method: 'PUT',
      body: isFormData ? data : JSON.stringify(data)
    });
  },
  delete: (url, options) => apiRequest(url, { ...options, method: 'DELETE' }),
  patch: (url, data, options = {}) => apiRequest(url, {
    ...options,
    method: 'PATCH',
    body: JSON.stringify(data)
  })
};
