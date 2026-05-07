import { useEffect } from "react";
import { api } from "../api";
import { setUserPermissions, useAuthState } from "../auth";

/**
 * Hook que carga los permisos del usuario logueado.
 * Se re-ejecuta cada vez que cambia el userId (login de un usuario distinto).
 * Si no hay usuario activo, limpia los permisos inmediatamente.
 */
export function usePermissions() {
  const { user } = useAuthState();
  const userId = user?.userId ?? null;

  useEffect(() => {
    if (!userId) {
      // No hay usuario: garantizar que los permisos queden limpios.
      setUserPermissions([]);
      return;
    }

    const loadPermissions = async () => {
      try {
        const permissions = await api.get("/users/me/permissions");
        setUserPermissions(Array.isArray(permissions) ? permissions : []);
      } catch (error) {
        // Si el token falla (401/403) los permisos del usuario anterior no deben persistir.
        console.warn("Could not load user permissions:", error);
        setUserPermissions([]);
      }
    };

    loadPermissions();
  }, [userId]);
}
