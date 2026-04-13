import { useEffect, useMemo, useState, useRef } from "react";
import { apiRequest, api } from "../api";
import { showError, showInfo, showSuccess, showConfirm } from "../alerts";
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
  Briefcase,
  Clock,
  Smartphone,
  TerminalSquare,
  Settings2,
  ShieldAlert
} from "lucide-react";
import Swal from "sweetalert2";
import { Button } from "../components/ui/button";
import AfipSettingsTab from "../components/AfipSettingsTab";
import LogsDashboard from "../components/LogsDashboard";
import OperationalFinanceSettingsTab from "../components/OperationalFinanceSettingsTab";
import WhatsAppBotTab from "../components/WhatsAppBotTab";
import RolesPermissionsTab from "../components/RolesPermissionsTab";
import AuditPage from "./AuditPage";
import { getPublicId } from "../lib/publicIds";

const serviceTypes = [
  { value: "", label: "Todos los servicios" },
  { value: "Aereo", label: "AÃ©reo" },
  { value: "Hotel", label: "Hotel" },
  { value: "Traslado", label: "Traslado" },
  { value: "Asistencia", label: "Asistencia" },
  { value: "Excursion", label: "ExcursiÃ³n" },
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

const MsgInput = ({ label, sub, value, onChange }) => (
  <div className="space-y-1.5">
    <label className="block text-sm font-semibold text-slate-800 dark:text-slate-200">{label}</label>
    <p className="text-[11px] text-slate-500 font-medium">{sub}</p>
    <textarea
      className="w-full rounded-xl border-slate-200 dark:border-slate-800 dark:bg-slate-950 text-sm focus:ring-emerald-500 focus:border-emerald-500 min-h-[80px] p-3 shadow-sm"
      value={value}
      onChange={e => onChange(e.target.value)}
    />
  </div>
);

const Terminal = ({ logs }) => {
  const scrollRef = useRef(null);
  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [logs]);

  return (
    <div className="bg-slate-950 rounded-xl border border-slate-800 p-3 font-mono text-[10px] space-y-1 h-60 overflow-y-auto mt-4 custom-scrollbar" ref={scrollRef}>
      {logs.map((log, i) => (
        <div key={i} className="flex gap-2">
          <span className={log.includes("âœ…") ? "text-emerald-400" : log.includes("âŒ") || log.includes("âš ï¸") ? "text-rose-400" : log.includes("ðŸ“±") ? "text-amber-400" : "text-slate-300"}>
            {log}
          </span>
        </div>
      ))}
      {logs.length === 0 && <div className="text-slate-700 italic">Esperando actividad del bot...</div>}
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


const tabs = [
  { id: "users", label: "Usuarios", icon: User },
  { id: "roles", label: "Roles y Permisos", icon: Shield },
  { id: "commissions", label: "Comisiones", icon: Briefcase },
  { id: "audit", label: "Auditoría Central", icon: ShieldAlert }
];

export default function AdminHubPage() {
  const [activeTab, setActiveTab] = useState("users");
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

  // WhatsApp Bot State
  const [botStatus, setBotStatus] = useState("STARTING");
  const [qrCode, setQrCode] = useState(null);
  const [botConfig, setBotConfig] = useState({
    welcomeMessage: "",
    askInterestMessage: "",
    askDatesMessage: "",
    askTravelersMessage: "",
    thanksMessage: "",
    agentRequestMessage: "",
    duplicateMessage: ""
  });
  const [savingBot, setSavingBot] = useState(false);
  const [loadingStatus, setLoadingStatus] = useState(false);
  const [botLogs, setBotLogs] = useState([]);

  // Mobile Nav State
  const [isMobileMenuOpen, setIsMobileMenuOpen] = useState(false);
  const [showAdvancedBot, setShowAdvancedBot] = useState(false);

  const isTabVisible = (tabId) => {
    if (["users", "roles", "logs", "programming"].includes(tabId)) {
      return adminUser;
    }

    return true;
  };

  const loadBotStatus = async () => {
    try {
      const data = await api.get("/webhooks/status");
      setBotStatus(data.status);
      setQrCode(data.qr);
      console.log("Bot Status:", data.status, "QR exists:", !!data.qr);
      
      const logs = await api.get("/webhooks/logs");
      if (Array.isArray(logs)) setBotLogs(logs);
    } catch (err) {
      setBotStatus("OFFLINE");
    }
  };

  const handleLogoutBot = async () => {
    const confirmed = await showConfirm(
      "Â¿Cerrar sesiÃ³n de WhatsApp?",
      "El bot dejarÃ¡ de funcionar hasta que vuelvas a escanear el QR.",
      "SÃ­, cerrar sesiÃ³n",
      "red"
    );

    if (confirmed) {
      try {
        await api.post("/webhooks/logout");
        showSuccess("SesiÃ³n cerrada");
        loadBotStatus();
      } catch {
        showError("No se pudo cerrar la sesiÃ³n");
      }
    }
  };

  useEffect(() => {
    return undefined;
  }, [activeTab]);

  useEffect(() => {
    if (!isTabVisible(activeTab)) {
      setActiveTab("agency");
    }
  }, [activeTab, adminUser]);

  const loadBotConfig = async () => {
    try {
      const data = await api.get("/whatsapp/config");
      setBotConfig(data);
    } catch { }
  };

  const saveBotConfig = async (e) => {
    e.preventDefault();
    setSavingBot(true);
    try {
      await api.put("/whatsapp/config", botConfig);
      showSuccess("Configuración del bot guardada");
      // Optional: Trigger reload on bot
      try { await api.post("/whatsapp/webhook/reload"); } catch { }
    } catch {
      showError("No se pudo guardar la configuraciÃ³n");
    } finally {
      setSavingBot(false);
    }
  };

  const reloadBot = async () => {
    setLoadingStatus(true);
    try {
      await api.post("/webhooks/reload");
      showSuccess("Bot sincronizado");
      loadBotStatus();
    } catch (error) {
      showError("Error al sincronizar");
    } finally {
      setLoadingStatus(false);
    }
  };

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
      showSuccess("Configuración de agencia actualizada");
      loadAgencySettings();
    } catch (error) {
      showError("No se pudo guardar la configuraciÃ³n");
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
      const data = await api.get("/suppliers?page=1&pageSize=100&includeInactive=true");
      const sorted = Array.isArray(data?.items) ? data.items.sort((a, b) => {
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
          supplierId: commissionForm.supplierId || null,
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
      showError(error.message || "No se pudo guardar la regla");
    }
  };

  const editCommissionRule = (rule) => {
    setCommissionForm({
      id: rule.id,
      supplierId: rule.supplierPublicId || rule.supplierId || "",
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
    const confirmed = await showConfirm(
      "Â¿Eliminar regla?",
      "Esta acciÃ³n no se puede deshacer y afectarÃ¡ el cÃ¡lculo de comisiones futuro.",
      "SÃ­, eliminar",
      "red"
    );

    if (confirmed) {
      try {
        await api.delete(`/commissions/${id}`);
        showSuccess("Regla eliminada");
        loadCommissionRules();
      } catch (error) {
        showError(error.message || "No se pudo eliminar la regla");
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
      showSuccess("ContraseÃ±a actualizada.");
      closeModal();
    } catch (error) {
      showError(error.message);
    }
  };

  const handleDeleteUser = async (user) => {
    const confirmed = await showConfirm(
      "Eliminar Usuario",
      `Â¿EstÃ¡s seguro de que deseas eliminar permanentemente a ${user.fullName}? Esta acciÃ³n no se puede deshacer.`,
      "SÃ­, eliminar",
      "red"
    );

    if (!confirmed) return;

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
          <h1 className="text-3xl font-bold tracking-tight text-slate-900 dark:text-white">Administración</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
            Administración del sistema y preferencias.
          </p>
        </div>
      </header>

      {/* Navigation - Mobile optimized */}
      <div className="bg-white dark:bg-slate-900/50 rounded-xl border border-slate-200 dark:border-slate-800 p-1 shadow-sm overflow-x-auto">
        <nav className="flex space-x-1 min-w-max" aria-label="Tabs">
          {tabs.map((tab) => {
            if (!isTabVisible(tab.id)) return null;
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
      {/* Create / Edit User Modal */}
      <Modal
        isOpen={modalType === 'create' || modalType === 'edit'}
        onClose={() => setModalType(null)}
        title={modalType === 'create' ? "Nuevo Usuario" : "Editar Usuario"}
      >
        <div className="space-y-4">
          <MsgInput
            label="Nombre Completo"
            value={modalType === 'create' ? createForm.fullName : editForm.fullName}
            onChange={(val) => modalType === 'create' ? setCreateForm({ ...createForm, fullName: val }) : setEditForm({ ...editForm, fullName: val })}
          />
          <MsgInput
            label="Email"
            value={modalType === 'create' ? createForm.email : editForm.email}
            onChange={(val) => modalType === 'create' ? setCreateForm({ ...createForm, email: val }) : setEditForm({ ...editForm, email: val })}
          />
          {modalType === 'create' && (
            <MsgInput
              label="Contraseña"
              type="password"
              value={createForm.password}
              onChange={(val) => setCreateForm({ ...createForm, password: val })}
            />
          )}
          <div className="space-y-1.5">
            <label className="block text-sm font-semibold text-slate-800 dark:text-slate-200">Rol</label>
            <select
              className="w-full rounded-xl border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950 p-3 text-sm"
              value={modalType === 'create' ? createForm.role : editForm.role}
              onChange={e => modalType === 'create' ? setCreateForm({ ...createForm, role: e.target.value }) : setEditForm({ ...editForm, role: e.target.value })}
            >
              <option value="Colaborador">Colaborador</option>
              <option value="Vendedor">Vendedor</option>
              <option value="Admin">Admin</option>
            </select>
          </div>
          {modalType === 'edit' && (
            <div className="flex items-center gap-2 mt-2">
              <input
                type="checkbox"
                id="isActive"
                checked={editForm.isActive}
                onChange={(e) => setEditForm({...editForm, isActive: e.target.checked})}
                className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
              />
              <label htmlFor="isActive" className="text-sm font-medium text-slate-700 dark:text-slate-300">
                Usuario Activo
              </label>
            </div>
          )}
          <div className="flex justify-end gap-2 pt-4">
            <Button variant="outline" onClick={() => setModalType(null)}>Cancelar</Button>
            <Button onClick={modalType === 'create' ? handleCreateUser : handleEditUser}>
              {modalType === 'create' ? "Crear Usuario" : "Guardar Cambios"}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Change Password Modal */}
      <Modal
        isOpen={modalType === 'password'}
        onClose={() => setModalType(null)}
        title="Cambiar Contraseña"
      >
        <div className="space-y-4">
          <div className="rounded-lg bg-amber-50 p-4 border border-amber-200 dark:bg-amber-900/20 dark:border-amber-800/30">
            <p className="text-sm text-amber-800 dark:text-amber-300 font-medium">Estás a punto de forzar el cambio de contraseña para {selectedUser?.fullName}.</p>
          </div>
          <div className="space-y-1.5">
            <label className="block text-sm font-semibold text-slate-800 dark:text-slate-200">Nueva Contraseña</label>
            <input
              type="password"
              className="w-full rounded-xl border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950 p-3 text-sm focus:ring-amber-500 focus:border-amber-500"
              value={passwordForm.newPassword}
              onChange={e => setPasswordForm({ ...passwordForm, newPassword: e.target.value })}
              placeholder="Mínimo 8 caracteres, números y letras..."
            />
          </div>
          <div className="flex justify-end gap-2 pt-4">
            <Button variant="outline" onClick={() => setModalType(null)}>Cancelar</Button>
            <Button onClick={handlePasswordReset} className="bg-amber-600 hover:bg-amber-700 text-white border-none">
              Guardar Contraseña
            </Button>
          </div>
        </div>
      </Modal>

      {/* Commission Edit Modal */}
      <Modal
        isOpen={showCommissionModal}
        onClose={() => setShowCommissionModal(false)}
        title={commissionForm.id ? "Editar Regla de Comisión" : "Nueva Regla Especial"}
      >
        <div className="space-y-4">
          <div className="space-y-1.5">
            <label className="block text-sm font-semibold text-slate-800 dark:text-slate-200">Proveedor</label>
            <select
              className="w-full rounded-xl border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950 p-3 text-sm focus:ring-indigo-500 focus:border-indigo-500"
              value={commissionForm.supplierId}
              onChange={e => setCommissionForm({ ...commissionForm, supplierId: e.target.value })}
            >
              <option value="">Cualquier Proveedor (Regla General)</option>
              {suppliers.map(s => (
                <option key={s.id} value={s.id}>{s.name} ({s.code})</option>
              ))}
            </select>
          </div>
          
          <div className="space-y-1.5">
            <label className="block text-sm font-semibold text-slate-800 dark:text-slate-200">Tipo de Servicio</label>
            <select
              className="w-full rounded-xl border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950 p-3 text-sm focus:ring-indigo-500 focus:border-indigo-500"
              value={commissionForm.serviceType}
              onChange={e => setCommissionForm({ ...commissionForm, serviceType: e.target.value })}
            >
              {serviceTypes.map(st => (
                <option key={st.value} value={st.value}>{st.label}</option>
              ))}
            </select>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <label className="block text-sm font-semibold text-slate-800 dark:text-slate-200">Porcentaje (%)</label>
              <input
                type="number"
                step="0.01"
                className="w-full rounded-xl border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950 p-3 text-sm focus:ring-indigo-500 focus:border-indigo-500"
                value={commissionForm.commissionPercent}
                onChange={e => setCommissionForm({ ...commissionForm, commissionPercent: parseFloat(e.target.value) || 0 })}
              />
            </div>
            <div className="space-y-1.5">
              <label className="block text-sm font-semibold text-slate-800 dark:text-slate-200">Prioridad</label>
              <input
                type="number"
                className="w-full rounded-xl border-slate-200 bg-slate-50 dark:border-slate-800 dark:bg-slate-950 p-3 text-sm focus:ring-indigo-500 focus:border-indigo-500"
                value={commissionForm.priority}
                onChange={e => setCommissionForm({ ...commissionForm, priority: parseInt(e.target.value) || 1 })}
                placeholder="1 (Más alta)"
              />
            </div>
          </div>
          
          <MsgInput
            label="Descripción Interna"
            value={commissionForm.description}
            onChange={(val) => setCommissionForm({ ...commissionForm, description: val })}
            sub="Opcional. Ej: Acuerdo primavera 2025."
          />

          <div className="flex justify-end gap-2 pt-4">
            <Button variant="outline" onClick={() => setShowCommissionModal(false)}>Cancelar</Button>
            <Button onClick={handleSaveCommission}>Guardar Regla</Button>
          </div>
        </div>
      </Modal>

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

        {/* --- ROLES TAB --- */}
        {activeTab === "roles" && <RolesPermissionsTab />}

        {/* --- COMMISSIONS TAB --- */}
        {activeTab === "commissions" && (
          <div className="space-y-6">
            <div className="flex justify-between items-center">
              <div>
                <h2 className="text-lg font-semibold text-slate-900 dark:text-white">Reglas de ComisiÃ³n</h2>
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
                    <th className="px-6 py-3 text-left text-xs font-medium text-slate-500 uppercase tracking-wider">ComisiÃ³n</th>
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

        {/* --- OPERATIONS TAB --- */}
        {activeTab === "audit" && <AuditPage />}
    </div>
  );
}








