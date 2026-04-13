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
  { id: "agency", label: "Agencia", icon: Building2 },
  { id: "operations", label: "Operativa y Caja", icon: Settings2 },
  { id: "afip", label: "Facturación", icon: FileText },
  { id: "whatsapp", label: "WhatsApp Bot", icon: Smartphone },
  { id: "logs", label: "Logs y Programación", icon: TerminalSquare }
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
      <div className="animate-in fade-in slide-in-from-bottom-2 duration-500">

        {/* --- AUDIT TAB --- */}
        {activeTab === "audit" && (
            <AuditPage />
        )}

        {/* --- AGENCY TAB --- */}
        {activeTab === "agency" && (
          <div className="grid gap-6 lg:grid-cols-3">
            <div className="lg:col-span-2 space-y-6">
              <form onSubmit={saveAgencySettings} className="space-y-6">
                {/* Identity Section */}
                {/* Agency Settings Card */}
                <div className="bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
                  <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
                    <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg text-indigo-600 dark:text-indigo-400">
                      <Building2 className="h-5 w-5" />
                    </div>
                    <div>
                      <h3 className="font-semibold text-slate-900 dark:text-white">Datos de la Agencia</h3>
                      <p className="text-xs text-slate-500">Información legal y de contacto</p>
                    </div>
                  </div>

                  <div className="p-6 space-y-8">
                    {/* Identidad */}
                    <div className="space-y-4">
                      <h4 className="text-sm font-semibold text-slate-900 dark:text-white border-b border-slate-100 dark:border-slate-800 pb-2">Identidad Comercial</h4>
                      <div className="grid gap-5 md:grid-cols-2">
                        <div className="col-span-2">
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Nombre de FantasÃ­a (Visible al cliente)</label>
                          <input type="text" required className="flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
                            placeholder="Ej: Magna Travel"
                            value={agencyForm.agencyName} onChange={e => setAgencyForm({ ...agencyForm, agencyName: e.target.value })} />
                        </div>
                        <div className="col-span-2">
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">RazÃ³n Social</label>
                          <input type="text" className="flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
                            placeholder="Ej: Magna Travel S.A."
                            value={agencyForm.legalName} onChange={e => setAgencyForm({ ...agencyForm, legalName: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">CUIT</label>
                          <input type="text" className="flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
                            placeholder="XX-XXXXXXXX-X"
                            value={agencyForm.taxId} onChange={e => setAgencyForm({ ...agencyForm, taxId: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">CondiciÃ³n IVA</label>
                          <select className="flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
                            value={agencyForm.taxCondition} onChange={e => setAgencyForm({ ...agencyForm, taxCondition: e.target.value })}>
                            <option value="Responsable Inscripto">Responsable Inscripto</option>
                            <option value="Monotributo">Monotributo</option>
                            <option value="Exento">Exento</option>
                          </select>
                        </div>
                      </div>
                    </div>

                    {/* Contacto */}
                    <div className="space-y-4">
                      <h4 className="text-sm font-semibold text-slate-900 dark:text-white border-b border-slate-100 dark:border-slate-800 pb-2">UbicaciÃ³n y Contacto</h4>
                      <div className="grid gap-5 md:grid-cols-2">
                        <div className="col-span-2">
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">DirecciÃ³n</label>
                          <input type="text" className="flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
                            value={agencyForm.address} onChange={e => setAgencyForm({ ...agencyForm, address: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">TelÃ©fono</label>
                          <input type="text" className="flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
                            value={agencyForm.phone} onChange={e => setAgencyForm({ ...agencyForm, phone: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Email</label>
                          <input type="email" className="flex h-10 w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-transparent dark:bg-slate-950 dark:border-slate-800 dark:text-slate-50"
                            value={agencyForm.email} onChange={e => setAgencyForm({ ...agencyForm, email: e.target.value })} />
                        </div>
                      </div>
                    </div>
                  </div>

                  <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
                    <Button
                      type="submit"
                      disabled={savingAgency}
                      className="bg-indigo-600 hover:bg-indigo-700 text-white rounded-lg px-6"
                    >
                      {savingAgency ? "Guardando..." : "Guardar Cambios"}
                    </Button>
                  </div>
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
                      <option value="USD">USD - DÃ³lar Estadounidense</option>
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
        {activeTab === "operations" && <OperationalFinanceSettingsTab />}

        {/* --- AFIP TAB --- */}
        {activeTab === "afip" && <AfipSettingsTab />}

        {/* --- WHATSAPP TAB --- */}
        {activeTab === "whatsapp" && <WhatsAppBotTab />}

        {/* --- LOGS TAB --- */}
        {activeTab === "logs" && <LogsDashboard />}

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
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">ContraseÃ±a</label>
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
      <Modal isOpen={modalType === 'password'} onClose={closeModal} title="Cambiar ContraseÃ±a">
        <form onSubmit={handlePasswordReset} className="space-y-4">
          <div className="p-3 bg-amber-50 text-amber-800 rounded-lg text-sm mb-4">
            Cambiando contraseÃ±a para <strong>{selectedUser?.fullName}</strong>
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Nueva ContraseÃ±a</label>
            <input type="password" required minLength={6} className="mt-1 block w-full rounded-xl border border-slate-200 px-3 py-2 text-sm dark:bg-slate-800 dark:border-slate-700"
              value={passwordForm.newPassword}
              onChange={e => setPasswordForm({ ...passwordForm, newPassword: e.target.value })} />
          </div>
          <div className="pt-2">
            <Button type="submit" className="w-full bg-indigo-600 hover:bg-indigo-700 text-white rounded-xl">
              Actualizar ContraseÃ±a
            </Button>
          </div>
        </form>
      </Modal>

      {/* Commission Modal */}
      <Modal isOpen={showCommissionModal} onClose={() => setShowCommissionModal(false)} title={commissionForm.id ? "Editar Regla" : "Nueva Regla de ComisiÃ³n"}>
        <form onSubmit={saveCommissionRule} className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Proveedor (opcional)</label>
            <select
              className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
              value={commissionForm.supplierId}
              onChange={e => setCommissionForm({ ...commissionForm, supplierId: e.target.value })}
            >
              <option value="">Todos los proveedores</option>
              {suppliers.map(s => <option key={getPublicId(s)} value={getPublicId(s)}>{s.name}</option>)}
            </select>
            <p className="text-xs text-slate-500 mt-1">Si se deja vacÃ­o, aplica a todos.</p>
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
              <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">ComisiÃ³n (%)</label>
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




