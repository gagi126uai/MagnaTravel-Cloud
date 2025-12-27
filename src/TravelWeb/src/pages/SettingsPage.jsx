import { useEffect, useMemo, useState } from "react";
import { apiRequest } from "../api";
import { showError, showInfo, showSuccess } from "../alerts";

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
    if (activeTab === "users") {
      loadUsers();
    }
  }, [activeTab]);

  const handleCreateChange = (field, value) => {
    setCreateForm((prev) => ({ ...prev, [field]: value }));
  };

  const handleEditChange = (field, value) => {
    setEditForm((prev) => ({ ...prev, [field]: value }));
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
      };
      await apiRequest(`/api/users/${editForm.id}`, {
        method: "PUT",
        body: JSON.stringify(payload),
      });
      showSuccess("Usuario actualizado.");
      setEditForm({ id: "", fullName: "", email: "", role: "Colaborador" });
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

  const startEdit = (user) => {
    setEditForm({
      id: user.id,
      fullName: user.fullName,
      email: user.email,
      role: user.roles?.[0] || "Colaborador",
    });
  };

  return (
    <div className="space-y-6">
      <header>
        <h1 className="text-2xl font-semibold text-white">Configuración</h1>
        <p className="text-sm text-slate-400">
          Gestiona usuarios, roles y la planificación operativa de la plataforma.
        </p>
      </header>

      <div className="rounded-2xl border border-slate-800 bg-slate-950/70 p-4">
        <div className="flex flex-wrap gap-2">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActiveTab(tab.id)}
              className={`rounded-full px-4 py-2 text-sm font-medium ${
                activeTab === tab.id
                  ? "bg-indigo-500/20 text-indigo-200"
                  : "text-slate-300 hover:bg-slate-900"
              }`}
            >
              {tab.label}
            </button>
          ))}
        </div>
      </div>

      {activeTab === "users" ? (
        <div className="grid gap-6 lg:grid-cols-[1.2fr_0.8fr]">
          <section className="rounded-2xl border border-slate-800 bg-slate-950/70 p-6">
            <div className="flex items-center justify-between">
              <div>
                <h2 className="text-lg font-semibold text-white">Usuarios del sistema</h2>
                <p className="text-sm text-slate-400">Administra accesos y roles.</p>
              </div>
              <span className="rounded-full bg-slate-900 px-3 py-1 text-xs text-slate-300">
                {users.length} usuarios
              </span>
            </div>

            <div className="mt-4 overflow-x-auto">
              <table className="min-w-full text-left text-sm text-slate-200">
                <thead className="text-xs uppercase text-slate-400">
                  <tr>
                    <th className="px-3 py-2">Nombre</th>
                    <th className="px-3 py-2">Email</th>
                    <th className="px-3 py-2">Rol</th>
                    <th className="px-3 py-2 text-right">Acciones</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-800">
                  {loading ? (
                    <tr>
                      <td className="px-3 py-4 text-slate-400" colSpan="4">
                        Cargando usuarios...
                      </td>
                    </tr>
                  ) : users.length === 0 ? (
                    <tr>
                      <td className="px-3 py-4 text-slate-400" colSpan="4">
                        No hay usuarios registrados.
                      </td>
                    </tr>
                  ) : (
                    users.map((user) => (
                      <tr key={user.id}>
                        <td className="px-3 py-3">{user.fullName}</td>
                        <td className="px-3 py-3">{user.email}</td>
                        <td className="px-3 py-3">
                          {roleLabels[user.roles?.[0]] || user.roles?.[0] || "Sin rol"}
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
            <form
              onSubmit={handleCreateUser}
              className="rounded-2xl border border-slate-800 bg-slate-950/70 p-6"
            >
              <h3 className="text-lg font-semibold text-white">Crear usuario</h3>
              <p className="text-sm text-slate-400">Genera un acceso nuevo con rol asignado.</p>
              <div className="mt-4 space-y-3">
                <input
                  type="text"
                  value={createForm.fullName}
                  onChange={(event) => handleCreateChange("fullName", event.target.value)}
                  className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
                  placeholder="Nombre completo"
                  required
                />
                <input
                  type="email"
                  value={createForm.email}
                  onChange={(event) => handleCreateChange("email", event.target.value)}
                  className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
                  placeholder="Email"
                  required
                />
                <input
                  type="password"
                  value={createForm.password}
                  onChange={(event) => handleCreateChange("password", event.target.value)}
                  className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
                  placeholder="Contraseña"
                  required
                />
                <select
                  value={createForm.role}
                  onChange={(event) => handleCreateChange("role", event.target.value)}
                  className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
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
                className="mt-4 w-full rounded-lg bg-indigo-500 px-4 py-2 text-sm font-semibold text-white hover:bg-indigo-400"
              >
                Crear usuario
              </button>
            </form>

            <form
              onSubmit={handleEditUser}
              className="rounded-2xl border border-slate-800 bg-slate-950/70 p-6"
            >
              <h3 className="text-lg font-semibold text-white">Editar usuario</h3>
              <p className="text-sm text-slate-400">
                Selecciona un usuario desde la tabla para modificarlo.
              </p>
              <div className="mt-4 space-y-3">
                <input
                  type="text"
                  value={editForm.fullName}
                  onChange={(event) => handleEditChange("fullName", event.target.value)}
                  className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
                  placeholder="Nombre completo"
                  disabled={!editForm.id}
                  required
                />
                <input
                  type="email"
                  value={editForm.email}
                  onChange={(event) => handleEditChange("email", event.target.value)}
                  className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
                  placeholder="Email"
                  disabled={!editForm.id}
                  required
                />
                <select
                  value={editForm.role}
                  onChange={(event) => handleEditChange("role", event.target.value)}
                  className="w-full rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-sm text-white"
                  disabled={!editForm.id}
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
                disabled={!editForm.id}
                className="mt-4 w-full rounded-lg bg-slate-700 px-4 py-2 text-sm font-semibold text-white hover:bg-slate-600 disabled:cursor-not-allowed disabled:opacity-60"
              >
                Guardar cambios
              </button>
            </form>
          </section>
        </div>
      ) : (
        <section className="space-y-6">
          <div className="rounded-2xl border border-slate-800 bg-slate-950/70 p-6">
            <h2 className="text-lg font-semibold text-white">Programación del sistema</h2>
            <p className="text-sm text-slate-400">
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
                className="rounded-2xl border border-slate-800 bg-slate-950/70 p-5"
              >
                <h3 className="text-base font-semibold text-white">{item.title}</h3>
                <p className="mt-2 text-sm text-slate-400">{item.description}</p>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
