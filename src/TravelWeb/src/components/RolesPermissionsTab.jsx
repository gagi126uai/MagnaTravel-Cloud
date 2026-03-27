import { useState, useEffect } from "react";
import { Shield, Plus, Trash2, Check, ChevronDown, ChevronRight } from "lucide-react";
import { apiRequest } from "../api";
import { showSuccess, showError, showConfirm } from "../alerts";
import { Button } from "./ui/button";

export default function RolesPermissionsTab() {
  const [roles, setRoles] = useState([]);
  const [catalog, setCatalog] = useState({});
  const [selectedRole, setSelectedRole] = useState(null);
  const [rolePermissions, setRolePermissions] = useState([]);
  const [newRoleName, setNewRoleName] = useState("");
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [expandedModules, setExpandedModules] = useState({});

  const systemRoles = ["Admin", "Colaborador"];

  useEffect(() => {
    loadData();
  }, []);

  const loadData = async () => {
    setLoading(true);
    try {
      const [rolesRes, catalogRes] = await Promise.all([
        apiRequest("/api/users/roles"),
        apiRequest("/api/users/permissions/catalog"),
      ]);
      setRoles(rolesRes || []);
      setCatalog(catalogRes || {});
      // Expand all modules by default
      const expanded = {};
      Object.keys(catalogRes || {}).forEach((m) => (expanded[m] = true));
      setExpandedModules(expanded);
    } catch (error) {
      showError("Error cargando roles y permisos");
    } finally {
      setLoading(false);
    }
  };

  const selectRole = async (roleName) => {
    setSelectedRole(roleName);
    try {
      const perms = await apiRequest(`/api/users/roles/${encodeURIComponent(roleName)}/permissions`);
      setRolePermissions(perms || []);
    } catch (error) {
      showError("Error cargando permisos del rol");
      setRolePermissions([]);
    }
  };

  const savePermissions = async () => {
    if (!selectedRole) return;
    setSaving(true);
    try {
      await apiRequest(`/api/users/roles/${encodeURIComponent(selectedRole)}/permissions`, {
        method: "PUT",
        body: JSON.stringify(rolePermissions),
      });
      showSuccess(`Permisos de "${selectedRole}" actualizados`);
    } catch (error) {
      showError("Error guardando permisos");
    } finally {
      setSaving(false);
    }
  };

  const togglePermission = (perm) => {
    setRolePermissions((prev) =>
      prev.includes(perm) ? prev.filter((p) => p !== perm) : [...prev, perm]
    );
  };

  const toggleModule = (moduleName) => {
    const modulePerms = catalog[moduleName] || [];
    const allSelected = modulePerms.every((p) => rolePermissions.includes(p));

    if (allSelected) {
      setRolePermissions((prev) => prev.filter((p) => !modulePerms.includes(p)));
    } else {
      setRolePermissions((prev) => [...new Set([...prev, ...modulePerms])]);
    }
  };

  const toggleModuleExpand = (moduleName) => {
    setExpandedModules((prev) => ({ ...prev, [moduleName]: !prev[moduleName] }));
  };

  const createRole = async () => {
    const trimmed = newRoleName.trim();
    if (!trimmed) return;
    try {
      await apiRequest("/api/users/roles", {
        method: "POST",
        body: JSON.stringify({ roleName: trimmed }),
      });
      showSuccess(`Rol "${trimmed}" creado`);
      setNewRoleName("");
      loadData();
    } catch (error) {
      showError(error?.message || "Error creando rol");
    }
  };

  const deleteRole = async (roleName) => {
    const confirmed = await showConfirm(
      "Eliminar rol",
      `Se eliminará el rol "${roleName}". Los usuarios con este rol perderán los permisos asociados.`,
      "Sí, eliminar",
      "red"
    );
    if (!confirmed) return;

    try {
      await apiRequest(`/api/users/roles/${encodeURIComponent(roleName)}`, { method: "DELETE" });
      showSuccess(`Rol "${roleName}" eliminado`);
      if (selectedRole === roleName) {
        setSelectedRole(null);
        setRolePermissions([]);
      }
      loadData();
    } catch (error) {
      showError(error?.message || "Error eliminando rol");
    }
  };

  const formatPermission = (perm) => {
    const parts = perm.split(".");
    const action = parts[1];
    const labels = { view: "Ver", edit: "Editar", delete: "Eliminar", users: "Usuarios", afip: "AFIP" };
    return labels[action] || action;
  };

  if (loading) {
    return (
      <div className="flex items-center justify-center py-12">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600"></div>
      </div>
    );
  }

  return (
    <div className="grid gap-6 lg:grid-cols-3">
      {/* Left: Role List */}
      <div className="space-y-4">
        <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
          <div className="px-5 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
            <div className="p-2 bg-purple-100 dark:bg-purple-900/30 rounded-lg text-purple-600 dark:text-purple-400">
              <Shield className="h-5 w-5" />
            </div>
            <div>
              <h3 className="font-semibold text-slate-900 dark:text-white">Roles del Sistema</h3>
              <p className="text-xs text-slate-500">Selecciona un rol para gestionar sus permisos</p>
            </div>
          </div>

          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {roles.map((role) => (
              <button
                key={role}
                onClick={() => selectRole(role)}
                className={`w-full flex items-center justify-between px-5 py-3 text-sm transition-colors text-left ${
                  selectedRole === role
                    ? "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/20 dark:text-indigo-300"
                    : "text-slate-700 dark:text-slate-300 hover:bg-slate-50 dark:hover:bg-slate-800/50"
                }`}
              >
                <div className="flex items-center gap-3">
                  <Shield className={`h-4 w-4 ${selectedRole === role ? "text-indigo-600 dark:text-indigo-400" : "text-slate-400"}`} />
                  <span className="font-medium">{role}</span>
                  {systemRoles.includes(role) && (
                    <span className="text-[10px] font-medium uppercase tracking-wider px-1.5 py-0.5 rounded bg-slate-100 dark:bg-slate-700 text-slate-500 dark:text-slate-400">
                      sistema
                    </span>
                  )}
                </div>
                {!systemRoles.includes(role) && (
                  <button
                    onClick={(e) => {
                      e.stopPropagation();
                      deleteRole(role);
                    }}
                    className="p-1 text-slate-400 hover:text-rose-600 transition-colors rounded hover:bg-rose-50 dark:hover:bg-rose-900/30"
                    title="Eliminar rol"
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </button>
                )}
              </button>
            ))}
          </div>

          {/* Create Role */}
          <div className="p-4 border-t border-slate-100 dark:border-slate-800">
            <div className="flex gap-2">
              <input
                type="text"
                placeholder="Nombre del nuevo rol..."
                className="flex-1 rounded-lg border border-slate-200 px-3 py-2 text-sm dark:bg-slate-800 dark:border-slate-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent"
                value={newRoleName}
                onChange={(e) => setNewRoleName(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && createRole()}
              />
              <Button
                onClick={createRole}
                disabled={!newRoleName.trim()}
                className="bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg px-3"
                size="sm"
              >
                <Plus className="h-4 w-4" />
              </Button>
            </div>
          </div>
        </div>
      </div>

      {/* Right: Permission Checkboxes */}
      <div className="lg:col-span-2">
        {!selectedRole ? (
          <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm p-12 text-center">
            <Shield className="mx-auto h-12 w-12 text-slate-200 dark:text-slate-700 mb-4" />
            <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-2">Selecciona un rol</h3>
            <p className="text-sm text-slate-500">Haz clic en un rol de la lista para ver y editar sus permisos</p>
          </div>
        ) : (
          <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
            <div className="px-5 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center justify-between bg-slate-50/50 dark:bg-slate-800/20">
              <div>
                <h3 className="font-semibold text-slate-900 dark:text-white">
                  Permisos de <span className="text-indigo-600 dark:text-indigo-400">{selectedRole}</span>
                </h3>
                <p className="text-xs text-slate-500 mt-0.5">
                  {rolePermissions.length} permiso{rolePermissions.length !== 1 ? "s" : ""} asignado{rolePermissions.length !== 1 ? "s" : ""}
                </p>
              </div>
              <Button
                onClick={savePermissions}
                disabled={saving}
                className="bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg"
                size="sm"
              >
                {saving ? "Guardando..." : "Guardar Cambios"}
              </Button>
            </div>

            <div className="divide-y divide-slate-100 dark:divide-slate-800">
              {Object.entries(catalog).map(([moduleName, perms]) => {
                const moduleSelected = perms.filter((p) => rolePermissions.includes(p));
                const allSelected = moduleSelected.length === perms.length;
                const someSelected = moduleSelected.length > 0 && !allSelected;
                const isExpanded = expandedModules[moduleName];

                return (
                  <div key={moduleName}>
                    <button
                      onClick={() => toggleModuleExpand(moduleName)}
                      className="w-full flex items-center justify-between px-5 py-3 hover:bg-slate-50 dark:hover:bg-slate-800/30 transition-colors"
                    >
                      <div className="flex items-center gap-3">
                        {isExpanded ? (
                          <ChevronDown className="h-4 w-4 text-slate-400" />
                        ) : (
                          <ChevronRight className="h-4 w-4 text-slate-400" />
                        )}
                        <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">{moduleName}</span>
                        <span className="text-[10px] text-slate-400 font-medium">
                          {moduleSelected.length}/{perms.length}
                        </span>
                      </div>
                      <button
                        onClick={(e) => {
                          e.stopPropagation();
                          toggleModule(moduleName);
                        }}
                        className={`w-5 h-5 rounded border flex items-center justify-center transition-colors ${
                          allSelected
                            ? "bg-indigo-600 border-indigo-600 text-white"
                            : someSelected
                            ? "bg-indigo-100 border-indigo-400 dark:bg-indigo-900/30 dark:border-indigo-600"
                            : "border-slate-300 dark:border-slate-600"
                        }`}
                      >
                        {(allSelected || someSelected) && <Check className="h-3 w-3" />}
                      </button>
                    </button>

                    {isExpanded && (
                      <div className="pb-3 px-5 pl-12 grid grid-cols-2 md:grid-cols-3 gap-2">
                        {perms.map((perm) => {
                          const isChecked = rolePermissions.includes(perm);
                          return (
                            <label
                              key={perm}
                              className={`flex items-center gap-2.5 px-3 py-2 rounded-lg cursor-pointer transition-colors text-sm ${
                                isChecked
                                  ? "bg-indigo-50 dark:bg-indigo-900/20 text-indigo-700 dark:text-indigo-300"
                                  : "hover:bg-slate-50 dark:hover:bg-slate-800/30 text-slate-600 dark:text-slate-400"
                              }`}
                            >
                              <input
                                type="checkbox"
                                checked={isChecked}
                                onChange={() => togglePermission(perm)}
                                className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500 dark:border-slate-600"
                              />
                              <span className="font-medium">{formatPermission(perm)}</span>
                            </label>
                          );
                        })}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
