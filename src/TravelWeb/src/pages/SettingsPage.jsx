import { useEffect, useMemo, useState } from "react";
import { apiRequest } from "../api";
import { showError, showInfo, showSuccess } from "../alerts";
import { isAdmin } from "../auth";

const tabs = [
  { id: "users", label: "Usuarios" },
  { id: "programming", label: "Programación" },
];

const roleLabels = {
  Admin: "Admin",
  Colaborador: "Colaborador",
};

export default function SettingsPage() {
  const [activeTab, setActiveTab] = useState("users");
  const [users, setUsers] = useState([]);
  const [roles, setRoles] = useState([]);
  const [loading, setLoading] = useState(false);
  const adminUser = isAdmin();
  const [createForm, setCreateForm] = useState({
    fullName: "",
    email: "",
    password: "",
    role: "Colaborador",
  });
  const [editForm, setEditForm] = useState({
    id: "",
    fullName: "",
    email: "",
    role: "Colaborador",
    isActive: true,
  });
  const [passwordForm, setPasswordForm] = useState({
    id: "",
    newPassword: "",
  });

  const roleOptions = useMemo(() => {
    if (roles.length > 0) {
      return roles;
    }
    return ["Admin", "Colaborador"];
  }, [roles]);

  const loadUsers = async () => {
    setLoading(true);
    try {
      const [usersResponse, rolesResponse] = await Promise.all([
        apiRequest("/api/users"),
        apiRequest("/api/users/roles"),
      ]);
      setUsers(usersResponse);
      setRoles(rolesResponse);
    } catch (error) {
      showError(error.message || "No pudimos cargar los usuarios.");
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (activeTab === "users" && adminUser) {
      loadUsers();
    }
  }, [activeTab, adminUser]);

  const handleCreateChange = (field, value) => {
    setCreateForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleEditChange = (field, value) => {
    setEditForm((prev) => ({ ...prev, [field]: value }));
  };

  const handlePasswordChange = (field, value) => {
    setPasswordForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleCreateUser = async (event) => {
    event.preventDefault();
    try {
      const payload = {
        fullName: createForm.fullName.trim(),
        email: createForm.email.trim(),
        password: createForm.password,
        role: createForm.role,
      };
      await apiRequest("/api/users", {
        method: "POST",
        body: JSON.stringify(payload),
      });
      showSuccess("Usuario creado correctamente.");
      setCreateForm({ fullName: "", email: "", password: "", role: "Colaborador" });
      await loadUsers();
    } catch (error) {
      showError(error.message || "No pudimos crear el usuario.");
    }
  };

  const handleEditUser = async (event) => {
    event.preventDefault();
    try {
      const payload = {
        fullName: editForm.fullName.trim(),
        email: editForm.email.trim(),
        role: editForm.role,
        isActive: editForm.isActive,
      };
      await apiRequest(`/api/users/${editForm.id}`, {
        method: "PUT",
        body: JSON.stringify(payload),
      });
      showSuccess("Usuario actualizado.");
      setEditForm({ id: "", fullName: "", email: "", role: "Colaborador", isActive: true });
      setPasswordForm({ id: "", newPassword: "" });
      await loadUsers();
    } catch (error) {
      showError(error.message || "No pudimos actualizar el usuario.");
    }
  };

  const handleDeleteUser = async (userId) => {
    try {
      await apiRequest(`/api/users/${userId}`, { method: "DELETE" });
      showInfo("Usuario eliminado.");
      await loadUsers();
    } catch (error) {
      showError(error.message || "No pudimos eliminar el usuario.");
    }
  };

  const handlePasswordReset = async (event) => {
    event.preventDefault();
    if (!passwordForm.id) {
      return;
    }

    try {
      await apiRequest(`/api/users/${passwordForm.id}/password`, {
        method: "PUT",
        body: JSON.stringify({ newPassword: passwordForm.newPassword }),
      });
      showSuccess("Contraseña actualizada.");
      setPasswordForm((prev) => ({ ...prev, newPassword: "" }));
    } catch (error) {
      showError(error.message || "No pudimos actualizar la contraseña.");
    }
  };

  const startEdit = (user) => {
    setEditForm({
      id: user.id,
      fullName: user.fullName,
      email: user.email,
      role: user.roles?.[0] || "Colaborador",
      isActive: user.isActive,
    });
    setPasswordForm({ id: user.id, newPassword: "" });
  };

  return (
    <div className="space-y-6">
      <header>
        <h1 className="text-2xl font-semibold">Configuración</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Gestiona usuarios, roles y la planificación operativa de la plataforma.
        </p>
      </header>

      <div className="rounded-3xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
        <div className="flex flex-wrap gap-2">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveTab(tab.id)}
              className={`rounded-full px-4 py-2 text-sm font-medium ${activeTab === tab.id
                  ? "bg-indigo-600 text-white shadow-sm shadow-indigo-500/30"
                  : "text-slate-500 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
                }`}
            >
              {tab.label}
            </button>
          ))}
        </div>
      </div>

      {activeTab === "users" ? (
        <div className="grid gap-6 lg:grid-cols-[1.2fr_0.8fr]">
          <section className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
            <div className="flex items-center justify-between">
              <div>
                <h2 className="text-lg font-semibold">Usuarios del sistema</h2>
                <p className="text-sm text-slate-500 dark:text-slate-400">Administra accesos y asignación de roles.</p>
              </div>
              <span className="rounded-full bg-slate-100 px-3 py-1 text-xs text-slate-500 dark:bg-slate-800 dark:text-slate-300">
                {users.length} usuarios
              </span>
            </div>

            <div className="mt-4 overflow-x-auto">
              {!adminUser ? (
                <div className="rounded-xl border border-slate-800 bg-slate-900/50 p-4 text-sm text-slate-300">
                  Solo los administradores pueden gestionar usuarios. Si creaste tu cuenta antes de
                  activar roles, vuelve a iniciar sesión para refrescar permisos.
                </div>
              ) : null}
              <table className="min-w-full text-left text-sm text-slate-700 dark:text-slate-200">
                <thead className="text-xs uppercase text-slate-500 dark:text-slate-400">
                  <tr>
                    <th className="px-3 py-2">Nombre</th>
                    <th className="px-3 py-2">Email</th>
                    <th className="px-3 py-2">Rol (Grupo)</th>
                    <th className="px-3 py-2">Estado</th>
                    <th className="px-3 py-2 text-right">Acciones</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-800">
                  {!adminUser ? (
                    <tr>
                      <td className="px-3 py-4 text-slate-400" colSpan="5">
                        Acceso restringido.
                      </td>
                    </tr>
                  ) : loading ? (
                    <tr>
                      <td className="px-3 py-4 text-slate-400" colSpan="5">
                        Cargando usuarios...
                      </td>
                    </tr>
                  ) : users.length === 0 ? (
                    <tr>
                      <td className="px-3 py-4 text-slate-400" colSpan="5">
                        No hay usuarios registrados.
                      </td>
                    </tr>
                  ) : (
                    users.map((user) => (
                      <tr key={user.id}>
                        <td className="px-3 py-3">{user.fullName}</td>
                        <td className="px-3 py-3">{user.email}</td>
                        <td className="px-3 py-3">
                          <span className="inline-flex items-center rounded-md bg-blue-50 px-2 py-1 text-xs font-medium text-blue-700 ring-1 ring-inset ring-blue-700/10">
                            {user.roles?.[0] || "Sin rol"}
                          </span>
                        </td>
                        <td className="px-3 py-3">
                          <span
                            className={`rounded-full px-2 py-1 text-xs ${user.isActive
                                ? "bg-emerald-500/10 text-emerald-200"
                                : "bg-rose-500/10 text-rose-200"
                              }`}
                          >
                            {user.isActive ? "Activo" : "Inactivo"}
                          </span>
                        </td>
                        <td className="px-3 py-3 text-right">
                          <div className="flex justify-end gap-2">
                            <button
                              type="button"
                              onClick={() => startEdit(user)}
                              className="rounded-lg border border-slate-700 px-3 py-1 text-xs text-slate-200 hover:bg-slate-800"
                            >
                              Editar
                            </button>
                            <button
                              type="button"
                              onClick={() => handleDeleteUser(user.id)}
                              className="rounded-lg border border-rose-500/40 px-3 py-1 text-xs text-rose-200 hover:bg-rose-500/10"
                            >
                              Eliminar
                            </button>
                          </div>
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </section>

          <section className="space-y-6">
            <PermissionManager
              adminUser={adminUser}
              roles={roleOptions}
              onRoleChange={loadUsers} // Reload to update lists
            />

            <form
              onSubmit={handleCreateUser}
              className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
            >
              <h3 className="text-lg font-semibold">Crear usuario</h3>
              <p className="text-sm text-slate-500 dark:text-slate-400">
                Genera un acceso nuevo y asígnalo a un grupo.
              </p>
              <div className="mt-4 space-y-3">
                <input
                  type="text"
                  value={createForm.fullName}
                  onChange={(event) => handleCreateChange("fullName", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  placeholder="Nombre completo"
                  required
                  disabled={!adminUser}
                />
                <input
                  type="email"
                  value={createForm.email}
                  onChange={(event) => handleCreateChange("email", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  placeholder="Email"
                  required
                  disabled={!adminUser}
                />
                <input
                  type="password"
                  value={createForm.password}
                  onChange={(event) => handleCreateChange("password", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  placeholder="Contraseña"
                  required
                  disabled={!adminUser}
                />
                <select
                  value={createForm.role}
                  onChange={(event) => handleCreateChange("role", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  disabled={!adminUser}
                >
                  {roleOptions.map((role) => (
                    <option key={role} value={role}>
                      {roleLabels[role] || role}
                    </option>
                  ))}
                </select>
              </div>
              <button
                type="submit"
                disabled={!adminUser}
                className="mt-4 w-full rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-60"
              >
                Crear usuario
              </button>
            </form>

            <form
              onSubmit={handleEditUser}
              className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
            >
              <h3 className="text-lg font-semibold">Editar usuario</h3>
              <p className="text-sm text-slate-500 dark:text-slate-400">
                Selecciona un usuario desde la tabla para modificarlo.
              </p>
              <div className="mt-4 space-y-3">
                <input
                  type="text"
                  value={editForm.fullName}
                  onChange={(event) => handleEditChange("fullName", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  placeholder="Nombre completo"
                  disabled={!editForm.id || !adminUser}
                  required
                />
                <input
                  type="email"
                  value={editForm.email}
                  onChange={(event) => handleEditChange("email", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  placeholder="Email"
                  disabled={!editForm.id || !adminUser}
                  required
                />
                <select
                  value={editForm.role}
                  onChange={(event) => handleEditChange("role", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  disabled={!editForm.id || !adminUser}
                >
                  {roleOptions.map((role) => (
                    <option key={role} value={role}>
                      {roleLabels[role] || role}
                    </option>
                  ))}
                </select>
                <label className="flex items-center gap-2 text-sm text-slate-300">
                  <input
                    type="checkbox"
                    checked={editForm.isActive}
                    onChange={(event) => handleEditChange("isActive", event.target.checked)}
                    disabled={!editForm.id || !adminUser}
                    className="h-4 w-4 rounded border-slate-700 bg-slate-900 text-indigo-500"
                  />
                  Usuario activo
                </label>
              </div>
              <button
                type="submit"
                disabled={!editForm.id || !adminUser}
                className="mt-4 w-full rounded-xl bg-slate-700 px-4 py-3 text-sm font-semibold text-white hover:bg-slate-600 disabled:cursor-not-allowed disabled:opacity-60"
              >
                Guardar cambios
              </button>
            </form>

            <form
              onSubmit={handlePasswordReset}
              className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
            >
              <h3 className="text-lg font-semibold">Cambiar contraseña</h3>
              <p className="text-sm text-slate-500 dark:text-slate-400">
                Define una nueva contraseña para el usuario seleccionado.
              </p>
              <div className="mt-4 space-y-3">
                <input
                  type="password"
                  value={passwordForm.newPassword}
                  onChange={(event) => handlePasswordChange("newPassword", event.target.value)}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100"
                  placeholder="Nueva contraseña"
                  disabled={!passwordForm.id || !adminUser}
                  required
                />
              </div>
              <button
                type="submit"
                disabled={!passwordForm.id || !adminUser}
                className="mt-4 w-full rounded-xl bg-indigo-600 px-4 py-3 text-sm font-semibold text-white shadow-sm shadow-indigo-500/30 hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-60"
              >
                Actualizar contraseña
              </button>
            </form>
          </section>
        </div>
      ) : (
        <section className="space-y-6">
          <div className="rounded-3xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/60">
            <h2 className="text-lg font-semibold">Programación del sistema</h2>
            <p className="text-sm text-slate-500 dark:text-slate-400">
              Emisión de vouchers y control administrativo en una plataforma integral para mejorar la
              eficiencia y automatizar tareas.
            </p>
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            {[
              {
                title: "Gestión de Reservas",
                description: "Control total sobre el proceso de reservas.",
              },
              {
                title: "Pagos y Finanzas",
                description: "Manejo eficiente de transacciones y pagos.",
              },
              {
                title: "Emisión de Vouchers",
                description: "Automatización en la creación y gestión de comprobantes.",
              },
              {
                title: "Control de Operaciones",
                description: "Visión integral de la operativa turística.",
              },
              {
                title: "Automatización",
                description: "Reducción de tareas administrativas manuales.",
              },
            ].map((item) => (
              <div
                key={item.title}
                className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900/60"
              >
                <h3 className="text-base font-semibold">{item.title}</h3>
                <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">
                  {item.description}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
