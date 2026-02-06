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
    let message = "Request failed";
    const errorText = await response.text();
    if (errorText) {
      try {
        const data = JSON.parse(errorText);
        if (Array.isArray(data) && data.length > 0) {
          message = data.join(", ");
        } else if (data?.message) {
          message = data.message;
        } else {
          message = errorText;
        }
      } catch {
        message = errorText;
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
