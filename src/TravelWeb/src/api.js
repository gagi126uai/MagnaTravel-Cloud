const baseUrl = import.meta.env.VITE_API_URL || "http://localhost:5000";

export async function apiRequest(path, options = {}) {
  const token = localStorage.getItem("token");
  const headers = {
    "Content-Type": "application/json",
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
    ...(options.headers || {}),
  };

  // Ensure proper URL construction
  const cleanBaseUrl = baseUrl.replace(/\/$/, "");
  const cleanPath = path.startsWith("/") ? path : `/${path}`;

  // Auto-prepend /api if missing (Common issue with VITE_API_URL configuration)
  const finalPath = cleanPath.startsWith("/api") ? cleanPath : `/api${cleanPath}`;

  const response = await fetch(`${cleanBaseUrl}${finalPath}`, {
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

  // Handle empty responses (e.g., DELETE returning 200 with no body)
  const text = await response.text();
  if (!text) {
    return null;
  }

  return JSON.parse(text);
}

export const api = {
  get: (url, options) => apiRequest(url, { ...options, method: 'GET' }),
  post: (url, data, options) => apiRequest(url, { ...options, method: 'POST', body: JSON.stringify(data) }),
  put: (url, data, options) => apiRequest(url, { ...options, method: 'PUT', body: JSON.stringify(data) }),
  delete: (url, options) => apiRequest(url, { ...options, method: 'DELETE' })
};
