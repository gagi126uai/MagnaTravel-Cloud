import { useEffect } from "react";
import { api } from "../api";
import { setUserPermissions, isAuthenticated } from "../auth";

/**
 * Hook que carga los permisos del usuario logueado al montar.
 * Llama a GET /api/users/me/permissions y los guarda en auth state.
 */
export function usePermissions() {
  useEffect(() => {
    const loadPermissions = async () => {
      if (!isAuthenticated()) return;
      try {
        const permissions = await api.get("/users/me/permissions");
        setUserPermissions(Array.isArray(permissions) ? permissions : []);
      } catch (error) {
        console.warn("Could not load user permissions:", error);
        setUserPermissions([]);
      }
    };

    loadPermissions();
  }, []);
}
