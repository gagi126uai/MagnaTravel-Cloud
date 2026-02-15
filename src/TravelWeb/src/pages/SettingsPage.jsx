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
  MoreHorizontal,
  Building2,
  MapPin,
  Mail,
  FileText,
  Calendar,
  CreditCard,
  PhoneCall,
  Menu,
  Check,
  ChevronRight,
  ChevronDown,
  Briefcase
} from "lucide-react";
import Swal from "sweetalert2";
import { Button } from "../components/ui/button";

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
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm transition-opacity animate-in fade-in">
      <div className="w-full max-w-lg overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900 animate-in zoom-in-95 duration-200">
        <div className="flex items-center justify-between border-b border-slate-200 px-6 py-4 dark:border-slate-800">
          <h3 className="text-lg font-semibold text-slate-900 dark:text-white">{title}</h3>
          <button
            onClick={onClose}
            className="rounded-full p-2 text-slate-500 hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800 dark:hover:text-slate-300"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        <div className="p-6 max-h-[80vh] overflow-y-auto">
          {children}
        </div>
      </div>
    </div>
  );
};

const RoleBadge = ({ role }) => {
  const colors = {
    Admin: "bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-300 border-purple-200 dark:border-purple-800",
    Colaborador: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300 border-blue-200 dark:border-blue-800",
    Default: "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300 border-slate-200 dark:border-slate-700"
  };
  return (
    <span className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium border ${colors[role] || colors.Default}`}>
      {role}
    </span>
  );
};

const Avatar = ({ name, size = "md" }) => {
  const initials = name
    .split(" ")
    .map((n) => n[0])
    .join("")
    .substring(0, 2)
    .toUpperCase();

  const sizeClasses = {
    sm: "h-8 w-8 text-xs",
    md: "h-10 w-10 text-sm",
    lg: "h-12 w-12 text-base"
  };

  return (
    <div className={`flex shrink-0 items-center justify-center rounded-full bg-indigo-600 text-white shadow-sm ring-2 ring-white dark:ring-slate-900 ${sizeClasses[size]}`}>
      {initials}
    </div>
  );
};

// --- Page ---

import AfipSettingsTab from "../components/AfipSettingsTab";

const tabs = [
  { id: "agency", label: "Agencia", icon: Building2 },
  { id: "users", label: "Usuarios", icon: User },
  { id: "commissions", label: "Comisiones", icon: Briefcase },
  { id: "afip", label: "Facturación", icon: FileText },
  // { id: "programming", label: "Programación" }, // Hidden for now as it seemed unused in original
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
    legalName: "",
    taxCondition: "Responsable Inscripto",
    activityStartDate: "",
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

  // Mobile Nav State
  const [isMobileMenuOpen, setIsMobileMenuOpen] = useState(false);

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
          legalName: data.legalName || "",
          taxCondition: data.taxCondition || "Responsable Inscripto",
          activityStartDate: data.activityStartDate ? data.activityStartDate.split('T')[0] : "",
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
      Swal.fire({
        title: "Guardado",
        text: "Configuración de agencia actualizada",
        icon: "success",
        timer: 1500,
        showConfirmButton: false
      });
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
      setCommissionRules(Array.isArray(data) ? data : []);
    } catch (error) {
      console.error("Error loading commission rules:", error);
      setCommissionRules([]);
    }
  };

  const loadSuppliers = async () => {
    try {
      const data = await api.get("/suppliers");
      const sorted = Array.isArray(data) ? data.sort((a, b) => {
        if (a.isActive === b.isActive) return a.name.localeCompare(b.name);
        return a.isActive ? -1 : 1;
      }) : [];
      setSuppliers(sorted);
    } catch { }
  };

  const saveCommissionRule = async (e) => {
    e.preventDefault();
    try {
      if (commissionForm.id) {
        await api.put(`/commissions/${commissionForm.id}`, {
          commissionPercent: parseFloat(commissionForm.commissionPercent),
          priority: parseInt(commissionForm.priority),
          description: commissionForm.description || null,
          isActive: true
        });
        showSuccess("Regla actualizada");
      } else {
        await api.post("/commissions", {
          supplierId: commissionForm.supplierId ? parseInt(commissionForm.supplierId) : null,
          serviceType: commissionForm.serviceType || null,
          commissionPercent: parseFloat(commissionForm.commissionPercent),
          priority: parseInt(commissionForm.priority),
          description: commissionForm.description || null
        });
        showSuccess("Regla creada");
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
        showSuccess("Regla eliminada");
        loadCommissionRules();
      } catch (error) {
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
    <div className="space-y-6 max-w-7xl mx-auto pb-20 md:pb-0">
      <header className="flex flex-col md:flex-row md:items-center justify-between gap-4">
        <div>
          <h1 className="text-3xl font-bold tracking-tight text-slate-900 dark:text-white">Configuración</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
            Administración del sistema y preferencias.
          </p>
        </div>
      </header>

      {/* Navigation - Mobile optimized */}
      <div className="bg-white dark:bg-slate-900/50 rounded-xl border border-slate-200 dark:border-slate-800 p-1 shadow-sm overflow-x-auto">
        <nav className="flex space-x-1 min-w-max" aria-label="Tabs">
          {tabs.map((tab) => {
            const Icon = tab.icon;
            const isActive = activeTab === tab.id;
            return (
              <button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                className={`
                  flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg transition-all
                  ${isActive
                    ? "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/20 dark:text-indigo-300 shadow-sm"
                    : "text-slate-500 hover:text-slate-700 hover:bg-slate-50 dark:text-slate-400 dark:hover:text-slate-200 dark:hover:bg-slate-800"
                  }
                `}
              >
                <Icon className={`h-4 w-4 ${isActive ? "text-indigo-600 dark:text-indigo-400" : ""}`} />
                {tab.label}
              </button>
            );
          })}
        </nav>
      </div>

      {/* Content Area */}
      <div className="animate-in fade-in slide-in-from-bottom-2 duration-500">

        {/* --- AGENCY TAB --- */}
        {activeTab === "agency" && (
          <div className="grid gap-6 lg:grid-cols-3">
            <div className="lg:col-span-2 space-y-6">
              <form onSubmit={saveAgencySettings} className="space-y-6">
                {/* Identity Section */}
                <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
                  <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-900/50 flex items-center gap-3">
                    <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg text-indigo-600 dark:text-indigo-400">
                      <Building2 className="h-5 w-5" />
                    </div>
                    <h3 className="font-semibold text-slate-900 dark:text-white">Identidad Comercial</h3>
                  </div>
                  <div className="p-6 grid gap-6 sm:grid-cols-2">
                    <div className="sm:col-span-2">
                      <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Nombre de Fantasía</label>
                      <input type="text" required className="form-input w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                        placeholder="Ej: Magna Travel"
                        value={agencyForm.agencyName} onChange={e => setAgencyForm({ ...agencyForm, agencyName: e.target.value })} />
                    </div>
                    <div className="sm:col-span-2">
                      <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Razón Social</label>
                      <input type="text" className="form-input w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                        placeholder="Ej: Magna Travel S.A."
                        value={agencyForm.legalName} onChange={e => setAgencyForm({ ...agencyForm, legalName: e.target.value })} />
                    </div>
                    <div>
                      <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">CUIT</label>
                      <input type="text" className="form-input w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                        placeholder="XX-XXXXXXXX-X"
                        value={agencyForm.taxId} onChange={e => setAgencyForm({ ...agencyForm, taxId: e.target.value })} />
                    </div>
                    <div>
                      <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Condición IVA</label>
                      <select className="form-select w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                        value={agencyForm.taxCondition} onChange={e => setAgencyForm({ ...agencyForm, taxCondition: e.target.value })}>
                        <option value="Responsable Inscripto">Responsable Inscripto</option>
                        <option value="Monotributo">Monotributo</option>
                        <option value="Exento">Exento</option>
                      </select>
                    </div>
                  </div>
                </div>

                {/* Contact Section */}
                <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
                  <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-900/50 flex items-center gap-3">
                    <div className="p-2 bg-emerald-100 dark:bg-emerald-900/30 rounded-lg text-emerald-600 dark:text-emerald-400">
                      <MapPin className="h-5 w-5" />
                    </div>
                    <h3 className="font-semibold text-slate-900 dark:text-white">Contacto y Ubicación</h3>
                  </div>
                  <div className="p-6 grid gap-6 sm:grid-cols-2">
                    <div className="sm:col-span-2">
                      <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Dirección</label>
                      <input type="text" className="form-input w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                        value={agencyForm.address} onChange={e => setAgencyForm({ ...agencyForm, address: e.target.value })} />
                    </div>
                    <div>
                      <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Teléfono</label>
                      <input type="text" className="form-input w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                        value={agencyForm.phone} onChange={e => setAgencyForm({ ...agencyForm, phone: e.target.value })} />
                    </div>
                    <div>
                      <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Email</label>
                      <input type="email" className="form-input w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                        value={agencyForm.email} onChange={e => setAgencyForm({ ...agencyForm, email: e.target.value })} />
                    </div>
                  </div>
                </div>

                <div className="flex justify-end pt-4">
                  <Button
                    type="submit"
                    disabled={savingAgency}
                    className="bg-indigo-600 hover:bg-indigo-700 text-white rounded-xl px-8 shadow-lg shadow-indigo-500/20"
                  >
                    {savingAgency ? "Guardando..." : "Guardar Cambios"}
                  </Button>
                </div>
              </form>
            </div>

            {/* Side Panel for Configs */}
            <div className="space-y-6">
              <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6">
                <h3 className="font-semibold text-slate-900 dark:text-white mb-4">Configuración Regional</h3>
                <div className="space-y-4">
                  <div>
                    <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Moneda Base</label>
                    <select className="form-select w-full rounded-xl border-slate-200 dark:border-slate-700 dark:bg-slate-800"
                      value={agencyForm.currency} onChange={e => setAgencyForm({ ...agencyForm, currency: e.target.value })}>
                      <option value="ARS">ARS - Peso Argentino</option>
                      <option value="USD">USD - Dólar Estadounidense</option>
                      <option value="EUR">EUR - Euro</option>
                    </select>
                    <p className="text-xs text-slate-400 mt-2">Moneda utilizada para reportes y balances.</p>
                  </div>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* --- USERS TAB --- */}
        {activeTab === "users" && (
          <div className="space-y-6">
            <div className="flex justify-between items-center">
              <div>
                <h2 className="text-lg font-semibold text-slate-900 dark:text-white">Equipo</h2>
                <p className="text-sm text-slate-500">Gestiona el acceso a la plataforma.</p>
              </div>
              <Button onClick={openCreateModal} className="bg-indigo-600 hover:bg-indigo-700 text-white rounded-xl shadow-lg shadow-indigo-500/20">
                <Plus className="h-4 w-4 mr-2" />
                Nuevo Usuario
              </Button>
            </div>

            {/* Desktop Table - Hidden on Mobile */}
            <div className="hidden md:block bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
              <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
                <thead className="bg-slate-50 dark:bg-slate-800/50">
                  <tr>
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">Usuario</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">Rol</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">Estado</th>
                    <th className="px-6 py-3 text-right text-xs font-medium text-slate-500 uppercase tracking-wider">Acciones</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-200 dark:divide-slate-800">
                  {users.map((user) => (
                    <tr key={user.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors">
                      <td className="px-6 py-4 whitespace-nowrap">
                        <div className="flex items-center">
                          <Avatar name={user.fullName} size="sm" />
                          <div className="ml-4">
                            <div className="text-sm font-medium text-slate-900 dark:text-white">{user.fullName}</div>
                            <div className="text-sm text-slate-500">{user.email}</div>
                          </div>
                        </div>
                      </td>
                      <td className="px-6 py-4 whitespace-nowrap">
                        <RoleBadge role={user.roles[0] || "Colaborador"} />
                      </td>
                      <td className="px-6 py-4 whitespace-nowrap">
                        <span className={`px-2 inline-flex text-xs leading-5 font-semibold rounded-full ${user.isActive ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400' : 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400'}`}>
                          {user.isActive ? 'Activo' : 'Inactivo'}
                        </span>
                      </td>
                      <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                        <div className="flex justify-end gap-2">
                          <button onClick={() => openEditModal(user)} className="text-indigo-600 hover:text-indigo-900 dark:text-indigo-400 dark:hover:text-indigo-300 p-1 hover:bg-indigo-50 dark:hover:bg-indigo-900/30 rounded"><Pencil className="h-4 w-4" /></button>
                          <button onClick={() => openPasswordModal(user)} className="text-amber-600 hover:text-amber-900 dark:text-amber-400 dark:hover:text-amber-300 p-1 hover:bg-amber-50 dark:hover:bg-amber-900/30 rounded"><Key className="h-4 w-4" /></button>
                          <button onClick={() => handleDeleteUser(user)} className="text-rose-600 hover:text-rose-900 dark:text-rose-400 dark:hover:text-rose-300 p-1 hover:bg-rose-50 dark:hover:bg-rose-900/30 rounded"><Trash2 className="h-4 w-4" /></button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {/* Mobile Smart List */}
            <div className="md:hidden space-y-3">
              {users.map((user) => (
                <div key={user.id} className="bg-white dark:bg-slate-900 p-4 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm">
                  <div className="flex justify-between items-start mb-3">
                    <div className="flex items-center gap-3">
                      <Avatar name={user.fullName} size="md" />
                      <div>
                        <div className="font-semibold text-slate-900 dark:text-white">{user.fullName}</div>
                        <div className="text-xs text-slate-500">{user.email}</div>
                      </div>
                    </div>
                    <RoleBadge role={user.roles[0] || "Colaborador"} />
                  </div>
                  <div className="flex justify-between items-center border-t border-slate-100 dark:border-slate-800 pt-3">
                    <span className={`text-xs font-medium px-2 py-1 rounded-md ${user.isActive ? 'bg-green-50 text-green-700 dark:bg-green-900/20 dark:text-green-400' : 'bg-red-50 text-red-700'}`}>
                      {user.isActive ? 'Activo' : 'Inactivo'}
                    </span>
                    <div className="flex gap-1">
                      <Button variant="ghost" size="sm" onClick={() => openPasswordModal(user)} className="h-8 w-8 p-0 text-amber-600"><Key className="h-4 w-4" /></Button>
                      <Button variant="ghost" size="sm" onClick={() => openEditModal(user)} className="h-8 w-8 p-0 text-indigo-600"><Pencil className="h-4 w-4" /></Button>
                      <Button variant="ghost" size="sm" onClick={() => handleDeleteUser(user)} className="h-8 w-8 p-0 text-rose-600"><Trash2 className="h-4 w-4" /></Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* --- COMMISSIONS TAB --- */}
        {activeTab === "commissions" && (
          <div className="space-y-6">
            <div className="flex justify-between items-center">
              <div>
                <h2 className="text-lg font-semibold text-slate-900 dark:text-white">Reglas de Comisión</h2>
                <p className="text-sm text-slate-500">Automatiza tus ganancias por proveedor.</p>
              </div>
              <Button onClick={openNewCommissionModal} className="bg-emerald-600 hover:bg-emerald-700 text-white rounded-xl shadow-lg shadow-emerald-500/20">
                <Plus className="h-4 w-4 mr-2" />
                Nueva Regla
              </Button>
            </div>

            {/* Desktop Table - Hidden on Mobile */}
            <div className="hidden md:block bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
              <table className="min-w-full divide-y divide-slate-200 dark:divide-slate-800">
                <thead className="bg-slate-50 dark:bg-slate-800/50">
                  <tr>
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">Proveedor</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">Servicio</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">Comisión</th>
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">Prioridad</th>
                    <th className="px-6 py-3 text-right text-xs font-medium text-slate-500 uppercase tracking-wider">Acciones</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-200 dark:divide-slate-800">
                  {commissionRules.map((rule) => {
                    const supplierName = rule.supplierName || "Todos";
                    const service = rule.serviceType || "Todos";
                    return (
                      <tr key={rule.id} className="hover:bg-slate-50 dark:hover:bg-slate-800/50 transition-colors">
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-slate-900 dark:text-white">{supplierName}</td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-slate-500 dark:text-slate-400">{service}</td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className="px-2 py-1 rounded-md bg-emerald-50 text-emerald-700 dark:bg-emerald-900/20 dark:text-emerald-400 font-bold text-xs ring-1 ring-inset ring-emerald-600/20">
                            {rule.commissionPercent}%
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-slate-500">
                          {rule.priority === 3 ? "Alta" : rule.priority === 2 ? "Media" : "Baja"}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                          <div className="flex justify-end gap-2">
                            <button onClick={() => editCommissionRule(rule)} className="text-indigo-600 p-1 hover:bg-slate-100 rounded"><Pencil className="h-4 w-4" /></button>
                            <button onClick={() => deleteCommissionRule(rule.id)} className="text-rose-600 p-1 hover:bg-slate-100 rounded"><Trash2 className="h-4 w-4" /></button>
                          </div>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>

            {/* Mobile Smart List */}
            <div className="md:hidden grid gap-3">
              {commissionRules.map((rule) => (
                <div key={rule.id} className="bg-white dark:bg-slate-900 p-4 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm flex items-center justify-between">
                  <div>
                    <div className="font-semibold text-slate-900 dark:text-white flex items-center gap-2">
                      {rule.supplierName || "Todos los Proveedores"}
                      {rule.priority === 3 && <span className="w-2 h-2 rounded-full bg-rose-500" title="Alta Prioridad"></span>}
                    </div>
                    <div className="text-xs text-slate-500 mt-1 flex items-center gap-2">
                      <Briefcase className="h-3 w-3" />
                      {rule.serviceType || "Todos los servicios"}
                    </div>
                  </div>
                  <div className="flex items-center gap-3">
                    <span className="font-bold text-lg text-emerald-600 dark:text-emerald-400">{rule.commissionPercent}%</span>
                    <div className="flex flex-col gap-1">
                      <button onClick={() => editCommissionRule(rule)} className="bg-indigo-50 dark:bg-indigo-900/30 text-indigo-600 p-1.5 rounded-lg"><Pencil className="h-3.5 w-3.5" /></button>
                      <button onClick={() => deleteCommissionRule(rule.id)} className="bg-rose-50 dark:bg-rose-900/30 text-rose-600 p-1.5 rounded-lg"><Trash2 className="h-3.5 w-3.5" /></button>
                    </div>
                  </div>
                </div>
              ))}
              {commissionRules.length === 0 && (
                <div className="text-center py-10 px-4 text-slate-500 bg-slate-50 dark:bg-slate-900/50 rounded-xl border border-dashed border-slate-200 dark:border-slate-800">
                  <Briefcase className="h-8 w-8 mx-auto mb-2 opacity-50" />
                  <p>No tienes reglas configuradas.</p>
                  <Button variant="link" onClick={openNewCommissionModal} className="mt-1">Crear primera regla</Button>
                </div>
              )}
            </div>
          </div>
        )}

        {/* --- AFIP TAB --- */}
        {activeTab === "afip" && <AfipSettingsTab />}

      </div>

      {/* --- MODALS --- */}

      {/* Create/Edit User Modal */}
      <Modal
        isOpen={modalType === 'create' || modalType === 'edit'}
        onClose={closeModal}
        title={modalType === 'create' ? "Nuevo Usuario" : "Editar Usuario"}
      >
        <form onSubmit={modalType === 'create' ? handleCreateUser : handleEditUser} className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Nombre Completo</label>
            <input type="text" required className="mt-1 block w-full rounded-xl border border-slate-200 px-3 py-2 text-sm dark:bg-slate-800 dark:border-slate-700"
              value={modalType === 'create' ? createForm.fullName : editForm.fullName}
              onChange={e => modalType === 'create' ? setCreateForm({ ...createForm, fullName: e.target.value }) : setEditForm({ ...editForm, fullName: e.target.value })} />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Email</label>
            <input type="email" required className="mt-1 block w-full rounded-xl border border-slate-200 px-3 py-2 text-sm dark:bg-slate-800 dark:border-slate-700"
              value={modalType === 'create' ? createForm.email : editForm.email}
              onChange={e => modalType === 'create' ? setCreateForm({ ...createForm, email: e.target.value }) : setEditForm({ ...editForm, email: e.target.value })} />
          </div>
          {modalType === 'create' && (
            <div>
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Contraseña</label>
              <input type="password" required className="mt-1 block w-full rounded-xl border border-slate-200 px-3 py-2 text-sm dark:bg-slate-800 dark:border-slate-700"
                value={createForm.password}
                onChange={e => setCreateForm({ ...createForm, password: e.target.value })} />
            </div>
          )}
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Rol</label>
            <select className="mt-1 block w-full rounded-xl border border-slate-200 px-3 py-2 text-sm dark:bg-slate-800 dark:border-slate-700"
              value={modalType === 'create' ? createForm.role : editForm.role}
              onChange={e => modalType === 'create' ? setCreateForm({ ...createForm, role: e.target.value }) : setEditForm({ ...editForm, role: e.target.value })}>
              {roleOptions.map(role => <option key={role} value={role}>{role}</option>)}
            </select>
          </div>
          {modalType === 'edit' && (
            <div className="flex items-center gap-2">
              <input type="checkbox" id="isActive" className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-600"
                checked={editForm.isActive}
                onChange={e => setEditForm({ ...editForm, isActive: e.target.checked })} />
              <label htmlFor="isActive" className="text-sm font-medium text-slate-700 dark:text-slate-300">Usuario Activo</label>
            </div>
          )}
          <div className="pt-2">
            <Button type="submit" className="w-full bg-indigo-600 hover:bg-indigo-700 text-white rounded-xl">
              {modalType === 'create' ? "Crear Usuario" : "Guardar Cambios"}
            </Button>
          </div>
        </form>
      </Modal>

      {/* Password Modal */}
      <Modal isOpen={modalType === 'password'} onClose={closeModal} title="Cambiar Contraseña">
        <form onSubmit={handlePasswordReset} className="space-y-4">
          <div className="p-3 bg-amber-50 text-amber-800 rounded-lg text-sm mb-4">
            Cambiando contraseña para <strong>{selectedUser?.fullName}</strong>
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Nueva Contraseña</label>
            <input type="password" required minLength={6} className="mt-1 block w-full rounded-xl border border-slate-200 px-3 py-2 text-sm dark:bg-slate-800 dark:border-slate-700"
              value={passwordForm.newPassword}
              onChange={e => setPasswordForm({ ...passwordForm, newPassword: e.target.value })} />
          </div>
          <div className="pt-2">
            <Button type="submit" className="w-full bg-indigo-600 hover:bg-indigo-700 text-white rounded-xl">
              Actualizar Contraseña
            </Button>
          </div>
        </form>
      </Modal>

      {/* Commission Modal */}
      <Modal isOpen={showCommissionModal} onClose={() => setShowCommissionModal(false)} title={commissionForm.id ? "Editar Regla" : "Nueva Regla de Comisión"}>
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
            <p className="text-xs text-slate-500 mt-1">Si se deja vacío, aplica a todos.</p>
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Tipo de Servicio (opcional)</label>
            <select
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={commissionForm.serviceType}
              onChange={e => setCommissionForm({ ...commissionForm, serviceType: e.target.value })}
            >
              <option value="">Todos los servicios</option>
              {serviceTypes.map(st => st.value && <option key={st.value} value={st.value}>{st.label}</option>)}
            </select>
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Comisión (%)</label>
              <div className="relative mt-1">
                <input
                  type="number" step="0.01" required
                  className="block w-full rounded-xl border border-slate-200 bg-slate-50 pl-3 pr-8 py-2 text-sm focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-800"
                  value={commissionForm.commissionPercent}
                  onChange={e => setCommissionForm({ ...commissionForm, commissionPercent: e.target.value })}
                />
                <div className="pointer-events-none absolute inset-y-0 right-0 flex items-center pr-3">
                  <span className="text-slate-500">%</span>
                </div>
              </div>
            </div>
            <div>
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Prioridad</label>
              <select
                className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white dark:border-slate-700 dark:bg-slate-800"
                value={commissionForm.priority}
                onChange={e => setCommissionForm({ ...commissionForm, priority: e.target.value })}
              >
                <option value="1">Baja (1)</option>
                <option value="2">Media (2)</option>
                <option value="3">Alta (3)</option>
              </select>
            </div>
          </div>
          <div className="pt-2">
            <Button type="submit" className="w-full bg-emerald-600 hover:bg-emerald-700 text-white rounded-xl">
              Guardar Regla
            </Button>
          </div>
        </form>
      </Modal>

    </div>
  );
}
