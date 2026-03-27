import { useSyncExternalStore } from "react";

const listeners = new Set();

let authState = {
  user: null,
  loading: true,
  permissions: [],
};

function emitChange() {
  for (const listener of listeners) {
    listener();
  }
}

function subscribe(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function getSnapshot() {
  return authState;
}

export function useAuthState() {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

export function setAuthLoading(loading) {
  authState = { ...authState, loading };
  emitChange();
}

export function setCurrentUser(user) {
  authState = {
    ...authState,
    user,
    loading: false,
  };
  emitChange();
}

export function setUserPermissions(permissions) {
  authState = { ...authState, permissions: permissions || [] };
  emitChange();
}

export function clearAuthState() {
  authState = {
    user: null,
    loading: false,
    permissions: [],
  };
  emitChange();
}

export function getCurrentUser() {
  return authState.user;
}

export function isAuthenticated() {
  return Boolean(authState.user);
}

export function getUserRoles() {
  return Array.isArray(authState.user?.roles) ? authState.user.roles : [];
}

export function isAdmin() {
  return Boolean(authState.user?.isAdmin || getUserRoles().includes("Admin"));
}

export function getUserPermissions() {
  return authState.permissions;
}

export function hasPermission(permission) {
  // Admin always has all permissions
  if (isAdmin()) return true;
  return authState.permissions.includes(permission);
}
