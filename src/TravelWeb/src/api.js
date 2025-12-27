const baseUrl = import.meta.env.VITE_API_URL || "http://localhost:5000";

export async function apiRequest(path, options = {}) {
  const token = localStorage.getItem("token");
  const headers = {
    "Content-Type": "application/json",
    ...(token ? { Authorization: `Bearer ${token}` } : {}),
    ...(options.headers || {}),
  };

  const response = await fetch(`${baseUrl}${path}`, {
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

  return response.json();
}
