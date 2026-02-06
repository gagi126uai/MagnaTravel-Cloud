import { useEffect, useMemo, useState } from "react";
import { apiRequest, api } from "../api";
import { showError, showInfo, showSuccess } from "../alerts";
import { isAdmin } from "../auth";
import {
  Pencil,
  Trash2,
  Key,
  Plus,
  Search,
  X,
  Shield,
  User,
  MoreHorizontal
} from "lucide-react";
import Swal from "sweetalert2";

const serviceTypes = [
  { value: "", label: "Todos los servicios" },
  { value: "Aereo", label: "Aéreo" },
  { value: "Hotel", label: "Hotel" },
  { value: "Traslado", label: "Traslado" },
  { value: "Asistencia", label: "Asistencia" },
  { value: "Excursion", label: "Excursión" },
  { value: "Paquete", label: "Paquete" },
  { value: "Otro", label: "Otro" },
];

// --- Components ---

const Modal = ({ isOpen, onClose, title, children }) => {
  if (!isOpen) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm transition-opacity">
      <div className="w-full max-w-lg overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-800">
          <h3 className="text-lg font-semibold text-slate-900 dark:text-white">{title}</h3>
          <button
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800 dark:hover:text-slate-300"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="p-6">
          {children}
        </div>
      </div>
    </div>
  );
};

const RoleBadge = ({ role }) => {
  const colors = {
    Admin: "bg-purple-100 text-purple-700 dark:bg-purple-500/10 dark:text-purple-300",
    Colaborador: "bg-blue-100 text-blue-700 dark:bg-blue-500/10 dark:text-blue-300",
    Default: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300"
  };
  return (
    <span className={`inline-flex items-center rounded-md px-2 py-1 text-xs font-medium ring-1 ring-inset ring-current/10 ${colors[role] || colors.Default}`}>
      {role}
    </span>
  );
};

const Avatar = ({ name }) => {
  const initials = name
    .split(" ")
    .map((n) => n[0])
    .join("")
    .substring(0, 2)
    .toUpperCase();

  return (
    <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-indigo-600 text-sm font-semibold text-white shadow-sm ring-2 ring-white dark:ring-slate-900">
      {initials}
    </div>
  );
};

// --- Page ---

const tabs = [
  { id: "agency", label: "Datos de la Agencia" },
  { id: "commissions", label: "Comisiones" },
  { id: "users", label: "Usuarios" },
  { id: "programming", label: "Programación" },
];

export default function SettingsPage() {
  const [activeTab, setActiveTab] = useState("agency");
  const [users, setUsers] = useState([]);
  const [roles, setRoles] = useState([]);
  const [loading, setLoading] = useState(false);
  const adminUser = isAdmin();

  // Modal State
  const [modalType, setModalType] = useState(null); // 'create', 'edit', 'password', 'roles'
  const [selectedUser, setSelectedUser] = useState(null);

  // Forms
  const [createForm, setCreateForm] = useState({ fullName: "", email: "", password: "", role: "Colaborador" });
  const [editForm, setEditForm] = useState({ id: "", fullName: "", email: "", role: "Colaborador", isActive: true });
  const [passwordForm, setPasswordForm] = useState({ id: "", newPassword: "" });
  const [newRoleName, setNewRoleName] = useState("");

  // Agency Settings State
  const [agencySettings, setAgencySettings] = useState(null);
  const [agencyForm, setAgencyForm] = useState({
    agencyName: "",
    taxId: "",
    address: "",
    phone: "",
    email: "",
    defaultCommissionPercent: 10,
    currency: "ARS"
  });
  const [savingAgency, setSavingAgency] = useState(false);

  // Commission Rules State
  const [commissionRules, setCommissionRules] = useState([]);
  const [suppliers, setSuppliers] = useState([]);
  const [showCommissionModal, setShowCommissionModal] = useState(false);
  const [commissionForm, setCommissionForm] = useState({
    id: null,
    supplierId: "",
    serviceType: "",
    commissionPercent: 10,
    priority: 1,
    description: ""
  });

  const closeModal = () => {
    setModalType(null);
    setSelectedUser(null);
  };

  const roleOptions = useMemo(() => {
    return roles.length > 0 ? roles : ["Admin", "Colaborador"];
  }, [roles]);

  const loadUsers = async () => {
    if (!adminUser) return;
    setLoading(true);
    try {
      const [usersResponse, rolesResponse] = await Promise.all([
        apiRequest("/api/users"),
        apiRequest("/api/users/roles"),
      ]);
      setUsers(usersResponse);
      setRoles(rolesResponse);
    } catch (error) {
      showError(error.message || "Error cargando datos.");
    } finally {
      setLoading(false);
    }
  };

  const loadAgencySettings = async () => {
    try {
      const data = await api.get("/reports/settings");
      if (data) {
        setAgencySettings(data);
        setAgencyForm({
          agencyName: data.agencyName || "",
          taxId: data.taxId || "",
          address: data.address || "",
          phone: data.phone || "",
          email: data.email || "",
          defaultCommissionPercent: data.defaultCommissionPercent || 10,
          currency: data.currency || "ARS"
        });
      }
    } catch (error) {
      console.log("No agency settings found, using defaults");
    }
  };

  const saveAgencySettings = async (e) => {
    e.preventDefault();
    setSavingAgency(true);
    try {
      await api.put("/reports/settings", agencyForm);
      Swal.fire("Guardado", "Configuración de agencia actualizada", "success");
      loadAgencySettings();
    } catch (error) {
      Swal.fire("Error", "No se pudo guardar la configuración", "error");
    } finally {
      setSavingAgency(false);
    }
  };

  const loadCommissionRules = async () => {
    try {
      const data = await api.get("/commissions");
      console.log("Commission rules loaded:", data);
      setCommissionRules(Array.isArray(data) ? data : []);
    } catch (error) {
      console.error("Error loading commission rules:", error);
      setCommissionRules([]);
    }
  };

  const loadSuppliers = async () => {
    try {
      const data = await api.get("/suppliers");
      setSuppliers(data);
    } catch { }
  };

  const saveCommissionRule = async (e) => {
    e.preventDefault();
    try {
      if (commissionForm.id) {
        // PUT: solo actualiza porcentaje, descripción e isActive
        await api.put(`/commissions/${commissionForm.id}`, {
          commissionPercent: parseFloat(commissionForm.commissionPercent),
          priority: parseInt(commissionForm.priority),
          description: commissionForm.description || null,
          isActive: true
        });
        Swal.fire("Guardado", "Regla de comisión actualizada", "success");
      } else {
        // POST: crea nueva regla con todos los campos
        await api.post("/commissions", {
          supplierId: commissionForm.supplierId ? parseInt(commissionForm.supplierId) : null,
          serviceType: commissionForm.serviceType || null,
          commissionPercent: parseFloat(commissionForm.commissionPercent),
          priority: parseInt(commissionForm.priority),
          description: commissionForm.description || null
        });
        Swal.fire("Guardado", "Regla de comisión creada", "success");
      }
      setShowCommissionModal(false);
      setCommissionForm({ id: null, supplierId: "", serviceType: "", commissionPercent: 10, priority: 1, description: "" });
      loadCommissionRules();
    } catch (error) {
      console.error("Error saving commission rule:", error);
      Swal.fire("Error", error.message || "No se pudo guardar la regla", "error");
    }
  };

  const editCommissionRule = (rule) => {
    setCommissionForm({
      id: rule.id,
      supplierId: rule.supplierId || "",
      serviceType: rule.serviceType || "",
      commissionPercent: rule.commissionPercent,
      priority: rule.priority || 1,
      description: rule.description || ""
    });
    setShowCommissionModal(true);
  };

  const openNewCommissionModal = () => {
    setCommissionForm({ id: null, supplierId: "", serviceType: "", commissionPercent: 10, priority: 1, description: "" });
    setShowCommissionModal(true);
  };

  const deleteCommissionRule = async (id) => {
    const result = await Swal.fire({
      title: "¿Eliminar regla?",
      text: "Esta acción no se puede deshacer",
      icon: "warning",
      showCancelButton: true,
      confirmButtonColor: "#d33",
      confirmButtonText: "Sí, eliminar"
    });
    if (result.isConfirmed) {
      try {
        await api.delete(`/commissions/${id}`);
        Swal.fire("Eliminado", "Regla eliminada correctamente", "success");
        loadCommissionRules();
      } catch (error) {
        console.error("Error deleting commission rule:", error);
        Swal.fire("Error", error.message || "No se pudo eliminar", "error");
      }
    }
  };

  useEffect(() => {
    if (activeTab === "users") loadUsers();
    if (activeTab === "agency") loadAgencySettings();
    if (activeTab === "commissions") {
      loadCommissionRules();
      loadSuppliers();
    }
  }, [activeTab, adminUser]);

  // Handlers
  const handleCreateUser = async (e) => {
    e.preventDefault();
    try {
      await apiRequest("/api/users", { method: "POST", body: JSON.stringify(createForm) });
      showSuccess("Usuario creado exitosamente.");
      loadUsers();
      closeModal();
      setCreateForm({ fullName: "", email: "", password: "", role: "Colaborador" });
    } catch (error) {
      showError(error.message);
    }
  };

  const handleEditUser = async (e) => {
    e.preventDefault();
    try {
      await apiRequest(`/api/users/${editForm.id}`, { method: "PUT", body: JSON.stringify(editForm) });
      showSuccess("Usuario actualizado.");
      loadUsers();
      closeModal();
    } catch (error) {
      showError(error.message);
    }
  };

  const handlePasswordReset = async (e) => {
    e.preventDefault();
    try {
      await apiRequest(`/api/users/${passwordForm.id}/password`, {
        method: "PUT",
        body: JSON.stringify({ newPassword: passwordForm.newPassword })
      });
      showSuccess("Contraseña actualizada.");
      closeModal();
    } catch (error) {
      showError(error.message);
    }
  };

  const handleDeleteUser = async (user) => {
    if (!window.confirm(`¿Estás seguro de eliminar a ${user.fullName}?`)) return;
    try {
      await apiRequest(`/api/users/${user.id}`, { method: "DELETE" });
      showInfo("Usuario eliminado.");
      loadUsers();
    } catch (error) {
      showError(error.message);
    }
  };

  // Role Management
  const handleCreateRole = async (e) => {
    e.preventDefault();
    if (!newRoleName.trim()) return;
    try {
      await apiRequest("/api/users/roles", { method: "POST", body: JSON.stringify({ roleName: newRoleName.trim() }) });
      setNewRoleName("");
      loadUsers(); // Refresh roles
    } catch (error) {
      showError(error.message);
    }
  };

  const handleDeleteRole = async (roleName) => {
    if (!window.confirm(`¿Eliminar grupo '${roleName}'?`)) return;
    try {
      await apiRequest(`/api/users/roles/${roleName}`, { method: "DELETE" });
      loadUsers();
    } catch (error) {
      showError(error.message);
    }
  }

  // Open Modals
  const openCreateModal = () => {
    setCreateForm({ fullName: "", email: "", password: "", role: "Colaborador" });
    setModalType('create');
  };

  const openEditModal = (user) => {
    setEditForm({
      id: user.id,
      fullName: user.fullName,
      email: user.email,
      role: user.roles?.[0] || "Colaborador",
      isActive: user.isActive,
    });
    setModalType('edit');
  };

  const openPasswordModal = (user) => {
    setPasswordForm({ id: user.id, newPassword: "" });
    setSelectedUser(user);
    setModalType('password');
  };

  return (
    <div className="space-y-6">
      <header className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Configuración</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Administración de usuarios, robles y ajustes del sistema.
          </p>
        </div>
      </header>

      {/* Tabs */}
      <div className="flex border-b border-slate-200 dark:border-slate-800">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            onClick={() => setActiveTab(tab.id)}
            className={`
              relative -mb-px px-6 py-3 text-sm font-medium transition-colors
              ${activeTab === tab.id
                ? "border-b-2 border-indigo-500 text-indigo-600 dark:text-indigo-400"
                : "border-transparent text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
              }
            `}
          >
            {tab.label}
          </button>
        ))}
      </div>

      {activeTab === "agency" && (
        <section className="max-w-2xl">
          <form onSubmit={saveAgencySettings} className="space-y-6">
            {/* Datos Básicos */}
            <div className="rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50">
              <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Datos de la Agencia</h3>
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Nombre de la Agencia</label>
                  <input
                    type="text"
                    required
                    className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={agencyForm.agencyName}
                    onChange={e => setAgencyForm({ ...agencyForm, agencyName: e.target.value })}
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">CUIT</label>
                  <input
                    type="text"
                    placeholder="XX-XXXXXXXX-X"
                    className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={agencyForm.taxId}
                    onChange={e => setAgencyForm({ ...agencyForm, taxId: e.target.value })}
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Teléfono</label>
                  <input
                    type="text"
                    className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={agencyForm.phone}
                    onChange={e => setAgencyForm({ ...agencyForm, phone: e.target.value })}
                  />
                </div>
                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
                  <input
                    type="email"
                    className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={agencyForm.email}
                    onChange={e => setAgencyForm({ ...agencyForm, email: e.target.value })}
                  />
                </div>
                <div className="sm:col-span-2">
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Dirección</label>
                  <input
                    type="text"
                    className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={agencyForm.address}
                    onChange={e => setAgencyForm({ ...agencyForm, address: e.target.value })}
                  />
                </div>
              </div>
            </div>

            {/* Configuración Comercial */}
            <div className="rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50">
              <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Configuración Comercial</h3>
              <div className="grid gap-4 sm:grid-cols-2">
                <div>
                  <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Moneda Principal</label>
                  <select
                    className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={agencyForm.currency}
                    onChange={e => setAgencyForm({ ...agencyForm, currency: e.target.value })}
                  >
                    <option value="ARS">ARS - Peso Argentino</option>
                    <option value="USD">USD - Dólar Estadounidense</option>
                    <option value="EUR">EUR - Euro</option>
                  </select>
                </div>
                <div className="flex items-end">
                  <p className="text-sm text-slate-500">
                    💡 Las comisiones se configuran en la pestaña <strong>"Comisiones"</strong>
                  </p>
                </div>
              </div>
            </div>

            <div className="flex justify-end">
              <button
                type="submit"
                disabled={savingAgency}
                className="rounded-xl bg-indigo-600 px-6 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-500 disabled:opacity-50"
              >
                {savingAgency ? "Guardando..." : "Guardar Configuración"}
              </button>
            </div>
          </form>
        </section>
      )}

      {activeTab === "commissions" && (
        <section className="space-y-4">
          <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
            <div>
              <h3 className="text-lg font-semibold text-slate-900 dark:text-white">Reglas de Comisión</h3>
              <p className="text-sm text-slate-500">Configura comisiones por proveedor y/o tipo de servicio</p>
            </div>
            <button
              onClick={openNewCommissionModal}
              className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-500"
            >
              <Plus className="h-4 w-4" />
              Nueva Regla
            </button>
          </div>

          {/* Info Card */}
          <div className="rounded-xl border border-blue-200 bg-blue-50 p-4 dark:border-blue-800 dark:bg-blue-900/20">
            <p className="text-sm text-blue-800 dark:text-blue-300">
              <strong>💡 Tip:</strong> A mayor prioridad, la regla se aplica primero. Si hay empate, se usa la más específica.
            </p>
          </div>

          {/* Table */}
          <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
            <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
              <thead className="bg-slate-50 dark:bg-slate-800/50">
                <tr>
                  <th className="px-6 py-4 text-left text-xs font-semibold uppercase tracking-wider text-slate-500">Proveedor</th>
                  <th className="px-6 py-4 text-left text-xs font-semibold uppercase tracking-wider text-slate-500">Servicio</th>
                  <th className="px-6 py-4 text-left text-xs font-semibold uppercase tracking-wider text-slate-500">Comisión</th>
                  <th className="px-6 py-4 text-left text-xs font-semibold uppercase tracking-wider text-slate-500">Prioridad</th>
                  <th className="relative px-6 py-4"><span className="sr-only">Acciones</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900">
                {commissionRules.map((rule) => (
                  <tr key={rule.id} className="group hover:bg-slate-50 dark:hover:bg-slate-800/50">
                    <td className="whitespace-nowrap px-6 py-4 text-sm font-medium text-slate-900 dark:text-white">
                      {rule.supplierName || <span className="text-slate-400 italic">Todos</span>}
                    </td>
                    <td className="whitespace-nowrap px-6 py-4 text-sm text-slate-600 dark:text-slate-300">
                      {rule.serviceType || <span className="text-slate-400 italic">Todos</span>}
                    </td>
                    <td className="whitespace-nowrap px-6 py-4">
                      <span className="inline-flex items-center rounded-full bg-emerald-100 px-2.5 py-0.5 text-sm font-semibold text-emerald-800 dark:bg-emerald-900/30 dark:text-emerald-400">
                        {rule.commissionPercent}%
                      </span>
                    </td>
                    <td className="whitespace-nowrap px-6 py-4 text-sm text-slate-500">
                      {rule.priority === 3 ? "Alta" : rule.priority === 2 ? "Media" : "Base"}
                    </td>
                    <td className="whitespace-nowrap px-6 py-4 text-right">
                      <div className="flex items-center justify-end gap-1">
                        <button
                          onClick={() => editCommissionRule(rule)}
                          className="rounded-lg p-2 text-slate-400 hover:bg-indigo-50 hover:text-indigo-600 dark:hover:bg-indigo-900/30"
                          title="Editar"
                        >
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button
                          onClick={() => deleteCommissionRule(rule.id)}
                          className="rounded-lg p-2 text-slate-400 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/30"
                          title="Eliminar"
                        >
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {commissionRules.length === 0 && (
              <div className="p-12 text-center text-slate-500">
                No hay reglas de comisión. Se usará el valor por defecto de la agencia.
              </div>
            )}
          </div>

          {/* Modal Regla */}
          <Modal isOpen={showCommissionModal} onClose={() => setShowCommissionModal(false)} title={commissionForm.id ? "Editar Regla de Comisión" : "Nueva Regla de Comisión"}>
            <form onSubmit={saveCommissionRule} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Proveedor (opcional)</label>
                <select
                  className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                  value={commissionForm.supplierId}
                  onChange={e => setCommissionForm({ ...commissionForm, supplierId: e.target.value })}
                >
                  <option value="">Todos los proveedores</option>
                  {suppliers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Tipo de Servicio (opcional)</label>
                <select
                  className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                  value={commissionForm.serviceType}
                  onChange={e => setCommissionForm({ ...commissionForm, serviceType: e.target.value })}
                >
                  {serviceTypes.map(st => <option key={st.value} value={st.value}>{st.label}</option>)}
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Porcentaje de Comisión</label>
                <div className="relative mt-1">
                  <input
                    type="number"
                    min="0"
                    max="100"
                    step="0.5"
                    required
                    className="block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 pr-10 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                    value={commissionForm.commissionPercent}
                    onChange={e => setCommissionForm({ ...commissionForm, commissionPercent: e.target.value })}
                  />
                  <span className="absolute right-3 top-1/2 -translate-y-1/2 text-slate-400">%</span>
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Prioridad</label>
                <input
                  type="number"
                  min="1"
                  max="100"
                  required
                  className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                  value={commissionForm.priority}
                  onChange={e => setCommissionForm({ ...commissionForm, priority: e.target.value })}
                />
                <p className="mt-1 text-xs text-slate-500">Mayor número = mayor prioridad</p>
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Descripción (opcional)</label>
                <input
                  type="text"
                  placeholder="Ej: Comisión especial mayorista"
                  className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                  value={commissionForm.description}
                  onChange={e => setCommissionForm({ ...commissionForm, description: e.target.value })}
                />
              </div>
              <div className="flex justify-end gap-3 pt-4">
                <button type="button" onClick={() => setShowCommissionModal(false)} className="rounded-xl px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">Cancelar</button>
                <button type="submit" className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500">{commissionForm.id ? "Guardar Cambios" : "Crear Regla"}</button>
              </div>
            </form>
          </Modal>
        </section>
      )}

      {activeTab === "users" && (
        <section className="space-y-4">
          {/* Toolbar */}
          <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
            <div className="relative max-w-sm flex-1">
              <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3">
                <Search className="h-4 w-4 text-slate-400" />
              </div>
              <input
                type="text"
                placeholder="Buscar usuarios..."
                className="block w-full rounded-xl border border-slate-200 bg-white py-2 pl-10 pr-3 text-sm placeholder:text-slate-400 focus:border-indigo-500 focus:ring-indigo-500 dark:border-slate-800 dark:bg-slate-900/50"
              />
            </div>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setModalType('roles')}
                className="flex items-center gap-2 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-800 dark:bg-slate-900/50 dark:text-slate-300 dark:hover:bg-slate-800"
              >
                <Shield className="h-4 w-4" />
                Gestionar Grupos
              </button>
              <button
                onClick={openCreateModal}
                className="flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm shadow-indigo-500/20 hover:bg-indigo-500"
              >
                <Plus className="h-4 w-4" />
                Nuevo Usuario
              </button>
            </div>
          </div>

          {/* Table */}
          <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
            <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
              <thead className="bg-slate-50 dark:bg-slate-800/50">
                <tr>
                  <th scope="col" className="px-6 py-4 text-left text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Usuario</th>
                  <th scope="col" className="px-6 py-4 text-left text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Rol / Grupo</th>
                  <th scope="col" className="px-6 py-4 text-left text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">Estado</th>
                  <th scope="col" className="relative px-6 py-4"><span className="sr-only">Acciones</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-200 bg-white dark:divide-slate-800 dark:bg-slate-900">
                {users.map((user) => (
                  <tr key={user.id} className="group hover:bg-slate-50 dark:hover:bg-slate-800/50">
                    <td className="whitespace-nowrap px-6 py-4">
                      <div className="flex items-center">
                        <Avatar name={user.fullName} />
                        <div className="ml-4">
                          <div className="text-sm font-medium text-slate-900 dark:text-white">{user.fullName}</div>
                          <div className="text-sm text-slate-500 dark:text-slate-400">{user.email}</div>
                        </div>
                      </div>
                    </td>
                    <td className="whitespace-nowrap px-6 py-4">
                      <RoleBadge role={user.roles?.[0] || "Sin rol"} />
                    </td>
                    <td className="whitespace-nowrap px-6 py-4">
                      <span className={`inline-flex items-center gap-1.5 rounded-full px-2 py-1 text-xs font-medium ${user.isActive ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-red-100 text-red-700 dark:bg-red-500/10 dark:text-red-400'}`}>
                        <span className={`h-1.5 w-1.5 rounded-full ${user.isActive ? 'bg-emerald-500' : 'bg-red-500'}`} />
                        {user.isActive ? 'Activo' : 'Inactivo'}
                      </span>
                    </td>
                    <td className="whitespace-nowrap px-6 py-4 text-right text-sm font-medium">
                      <div className="flex items-center justify-end gap-2 opacity-0 transition-opacity group-hover:opacity-100">
                        <button onClick={() => openEditModal(user)} className="rounded-lg p-2 text-slate-400 hover:bg-indigo-50 hover:text-indigo-600 dark:hover:bg-indigo-900/30" title="Editar">
                          <Pencil className="h-4 w-4" />
                        </button>
                        <button onClick={() => openPasswordModal(user)} className="rounded-lg p-2 text-slate-400 hover:bg-amber-50 hover:text-amber-600 dark:hover:bg-amber-900/30" title="Cambiar Contraseña">
                          <Key className="h-4 w-4" />
                        </button>
                        <button onClick={() => handleDeleteUser(user)} className="rounded-lg p-2 text-slate-400 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/30" title="Eliminar">
                          <Trash2 className="h-4 w-4" />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {users.length === 0 && (
              <div className="p-12 text-center text-slate-500">
                No hay usuarios registrados.
              </div>
            )}
          </div>
        </section>
      )}

      {activeTab === "programming" && (
        <section className="grid gap-6 sm:grid-cols-2 lg:grid-cols-3">
          <div className="col-span-full rounded-2xl border border-dashed border-slate-300 bg-slate-50 p-12 text-center dark:border-slate-700 dark:bg-slate-900/30">
            <MoreHorizontal className="mx-auto h-12 w-12 text-slate-300" />
            <h3 className="mt-2 text-sm font-semibold text-slate-900 dark:text-white">Próximamente</h3>
            <p className="mt-1 text-sm text-slate-500">Las funciones de programación estarán disponibles aquí.</p>
          </div>
        </section>
      )}

      {/* --- MODALS --- */}

      <Modal isOpen={modalType === 'create'} onClose={closeModal} title="Crear Nuevo Usuario">
        <form onSubmit={handleCreateUser} className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Nombre Completo</label>
            <input
              required
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={createForm.fullName}
              onChange={e => setCreateForm({ ...createForm, fullName: e.target.value })}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
            <input
              required type="email"
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={createForm.email}
              onChange={e => setCreateForm({ ...createForm, email: e.target.value })}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Contraseña</label>
            <input
              required type="password"
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={createForm.password}
              onChange={e => setCreateForm({ ...createForm, password: e.target.value })}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Rol / Grupo</label>
            <select
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={createForm.role}
              onChange={e => setCreateForm({ ...createForm, role: e.target.value })}
            >
              {roleOptions.map(r => <option key={r} value={r}>{r}</option>)}
            </select>
          </div>
          <div className="flex justify-end gap-3 pt-4">
            <button type="button" onClick={closeModal} className="rounded-xl px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">Cancelar</button>
            <button type="submit" className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500">Crear Usuario</button>
          </div>
        </form>
      </Modal>

      <Modal isOpen={modalType === 'edit'} onClose={closeModal} title="Editar Usuario">
        <form onSubmit={handleEditUser} className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Nombre Completo</label>
            <input
              required
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={editForm.fullName}
              onChange={e => setEditForm({ ...editForm, fullName: e.target.value })}
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
            <input
              required type="email"
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={editForm.email}
              onChange={e => setEditForm({ ...editForm, email: e.target.value })}
            />
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Rol / Grupo</label>
              <select
                className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                value={editForm.role}
                onChange={e => setEditForm({ ...editForm, role: e.target.value })}
              >
                {roleOptions.map(r => <option key={r} value={r}>{r}</option>)}
              </select>
            </div>
            <div className="flex items-center pt-6">
              <label className="flex items-center gap-2 text-sm font-medium text-slate-700 dark:text-slate-300 cursor-pointer">
                <input
                  type="checkbox"
                  checked={editForm.isActive}
                  onChange={e => setEditForm({ ...editForm, isActive: e.target.checked })}
                  className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                />
                Usuario Activo
              </label>
            </div>
          </div>
          <div className="flex justify-end gap-3 pt-4">
            <button type="button" onClick={closeModal} className="rounded-xl px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">Cancelar</button>
            <button type="submit" className="rounded-xl bg-slate-900 text-white px-4 py-2 text-sm font-medium hover:bg-slate-800">Guardar Cambios</button>
          </div>
        </form>
      </Modal>

      <Modal isOpen={modalType === 'password'} onClose={closeModal} title={`Cambiar Contraseña: ${selectedUser?.fullName}`}>
        <form onSubmit={handlePasswordReset} className="space-y-4">
          <div className="rounded-lg bg-amber-50 p-4 text-sm text-amber-800 dark:bg-amber-900/20 dark:text-amber-300">
            Estás cambiando la contraseña para <strong>{selectedUser?.email}</strong>. Asegúrate de comunicar la nueva contraseña al usuario.
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Nueva Contraseña</label>
            <input
              required type="password"
              placeholder="Mínimo 8 caracteres, números y mayúsculas"
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={passwordForm.newPassword}
              onChange={e => setPasswordForm({ ...passwordForm, newPassword: e.target.value })}
            />
          </div>
          <div className="flex justify-end gap-3 pt-4">
            <button type="button" onClick={closeModal} className="rounded-xl px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">Cancelar</button>
            <button type="submit" className="rounded-xl bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-500">Actualizar Contraseña</button>
          </div>
        </form>
      </Modal>

      <Modal isOpen={modalType === 'roles'} onClose={closeModal} title="Gestión de Grupos y Roles">
        <div className="space-y-6">
          <div className="rounded-lg border border-slate-100 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-900/50">
            <h4 className="mb-2 text-sm font-medium text-slate-900 dark:text-white">Grupos Existentes</h4>
            <div className="flex flex-wrap gap-2">
              {roles.map(role => (
                <div key={role} className="flex items-center gap-1 rounded-full bg-white px-3 py-1 text-sm shadow-sm ring-1 ring-slate-200 dark:bg-slate-800 dark:ring-slate-700">
                  <span>{role}</span>
                  {role !== "Admin" && role !== "Colaborador" && (
                    <button onClick={() => handleDeleteRole(role)} className="ml-1 text-slate-400 hover:text-rose-500">
                      <X className="h-3 w-3" />
                    </button>
                  )}
                </div>
              ))}
            </div>
          </div>

          <form onSubmit={handleCreateRole} className="flex gap-2">
            <input
              placeholder="Nombre del nuevo grupo..."
              className="flex-1 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={newRoleName}
              onChange={e => setNewRoleName(e.target.value)}
            />
            <button type="submit" className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500">
              Crear
            </button>
          </form>
        </div>
      </Modal>
    </div>
  );
}
