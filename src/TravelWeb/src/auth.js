export function setAuthToken(token) {
  localStorage.setItem("token", token);
}

export function clearAuthToken() {
  localStorage.removeItem("token");
}

export function isAuthenticated() {
  return Boolean(localStorage.getItem("token"));
}

export function getUserRoles() {
  const token = localStorage.getItem("token");
  if (!token) {
    return [];
  }

  const parts = token.split(".");
  if (parts.length !== 3) {
    return [];
  }

  try {
    const payload = JSON.parse(atob(parts[1].replace(/-/g, "+").replace(/_/g, "/")));
    const roles = payload["role"] ?? payload["roles"] ?? payload["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];
    if (!roles) {
      return [];
    }
    return Array.isArray(roles) ? roles : [roles];
  } catch {
    return [];
  }
}

export function isAdmin() {
  return getUserRoles().includes("Admin");
}
