import { useEffect, useState } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { getApiErrorMessage } from "../lib/errors";
import { isAdmin, hasPermission } from "../auth";
import {
  Building2,
  FileText,
  Smartphone,
  TerminalSquare,
  Settings2,
  ShieldCheck,
  Sparkles,
  Palette
} from "lucide-react";
import { Button } from "../components/ui/button";
import AfipSettingsTab from "../components/AfipSettingsTab";
import BudgetPdfSettingsTab from "../components/BudgetPdfSettingsTab";
import ApprovalPoliciesTab from "../components/ApprovalPoliciesTab";
import LogsDashboard from "../components/LogsDashboard";
import OperationalFinanceSettingsTab from "../components/OperationalFinanceSettingsTab";
import WhatsAppBotTab from "../components/WhatsAppBotTab";
import { ListaCuentasBancarias } from "../features/bank-accounts/components/ListaCuentasBancarias";
import AiSettingsTab from "../features/ai-settings/components/AiSettingsTab";
import { puedeVerConfiguracionIa } from "../features/ai-settings/lib/aiSettingsPresentation.js";

// --- Page ---

const tabs = [
  { id: "agency", label: "Agencia", icon: Building2 },
  { id: "operations", label: "Operativa y Caja", icon: Settings2 },
  { id: "afip", label: "Facturación", icon: FileText },
  // Obra "PDF de presupuesto" (spec 2026-08-12, §4): identidad del PDF + condiciones. Sin
  // adminOnly — mismo criterio que Facturación/Operativa (el backend SÍ es Admin-only, pero
  // el único usuario real hoy es admin; ver el comentario largo en BudgetPdfSettingsTab.jsx).
  { id: "budgetPdf", label: "Presupuestos y PDF", icon: Palette },
  { id: "whatsapp", label: "WhatsApp Bot", icon: Smartphone },
  // §15.1 de la spec firmada 2026-08-07: solapa nueva, al lado de Facturación y WhatsApp
  // Bot, SOLO Admin (adminOnly abajo en isTabVisible — un vendedor no la ve, ni apagada).
  { id: "ai", label: "Inteligencia artificial", icon: Sparkles, adminOnly: true },
  { id: "approvals", label: "Workflows de aprobación", icon: ShieldCheck, requiredPermission: "approvals.policies" },
  { id: "logs", label: "Logs y Programación", icon: TerminalSquare }
];

export default function SettingsPage() {
  const [activeTab, setActiveTab] = useState("agency");
  const adminUser = isAdmin();

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

  const isTabVisible = (tabId) => {
    // "Logs y Programación" queda oculta para quien no es Admin. Antes esta lista tambien
    // incluia "users"/"roles"/"programming", pero esos ids nunca tuvieron una solapa real en
    // este archivo (codigo muerto) y se limpiaron junto con el resto del CRUD inalcanzable.
    if (tabId === "logs") {
      return adminUser;
    }
    const tab = tabs.find((t) => t.id === tabId);
    // §15.1 spec firmada 2026-08-07: "Inteligencia artificial" es solo-Admin y la solapa
    // NO existe para nadie mas (ni apagada). puedeVerConfiguracionIa es la misma regla,
    // como funcion pura y testeada en features/ai-settings/lib/aiSettingsPresentation.js.
    if (tab?.adminOnly) {
      return puedeVerConfiguracionIa(adminUser);
    }
    // B1.15 Fase B'': tabs nuevos pueden declarar requiredPermission.
    if (tab?.requiredPermission) {
      return hasPermission(tab.requiredPermission);
    }

    return true;
  };

  useEffect(() => {
    if (!isTabVisible(activeTab)) {
      setActiveTab("agency");
    }
  }, [activeTab, adminUser]);

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
      showError(getApiErrorMessage(error, "No se pudo guardar la configuración"));
    } finally {
      setSavingAgency(false);
    }
  };

  useEffect(() => {
    if (activeTab === "agency") loadAgencySettings();
  }, [activeTab, adminUser]);

  return (
    <div className="space-y-6 max-w-7xl mx-auto pb-20 md:pb-0">
      <header className="flex flex-col md:flex-row md:items-center justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Configuración</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
Ajustá cómo funciona el sistema para tu agencia.
          </p>
        </div>
      </header>

      {/* Navigation - Mobile optimized */}
      <div className="bg-white dark:bg-slate-900/50 rounded-[10px] border border-slate-200 dark:border-slate-800 p-1 shadow-sm overflow-x-auto">
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
                  flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-[10px] transition-all
                  ${isActive
                    ? "bg-primary/10 text-primary shadow-sm"
                    : "text-slate-500 hover:text-slate-700 hover:bg-slate-50 dark:text-slate-400 dark:hover:text-slate-200 dark:hover:bg-slate-800"
                  }
                `}
              >
                <Icon className={`h-4 w-4 ${isActive ? "text-primary" : ""}`} />
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
                {/* Agency Settings Card */}
                <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
                  <div className="px-6 py-4 border-b border-slate-100 dark:border-slate-800 flex items-center gap-3 bg-slate-50/50 dark:bg-slate-800/20">
                    <div className="p-2 bg-blue-100 dark:bg-blue-900/30 rounded-[10px] text-blue-600 dark:text-blue-400">
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
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Nombre de Fantasía (Visible al cliente)</label>
                          <input type="text" required className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            placeholder="Ej: Magna Travel"
                            value={agencyForm.agencyName} onChange={e => setAgencyForm({ ...agencyForm, agencyName: e.target.value })} />
                        </div>
                        <div className="col-span-2">
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Razón Social</label>
                          <input type="text" className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            placeholder="Ej: Magna Travel S.A."
                            value={agencyForm.legalName} onChange={e => setAgencyForm({ ...agencyForm, legalName: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">CUIT</label>
                          <input type="text" className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            placeholder="XX-XXXXXXXX-X"
                            value={agencyForm.taxId} onChange={e => setAgencyForm({ ...agencyForm, taxId: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Condición IVA</label>
                          <select className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
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
                      <h4 className="text-sm font-semibold text-slate-900 dark:text-white border-b border-slate-100 dark:border-slate-800 pb-2">Ubicación y Contacto</h4>
                      <div className="grid gap-5 md:grid-cols-2">
                        <div className="col-span-2">
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Dirección</label>
                          <input type="text" className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            value={agencyForm.address} onChange={e => setAgencyForm({ ...agencyForm, address: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Teléfono</label>
                          <input type="text" className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            value={agencyForm.phone} onChange={e => setAgencyForm({ ...agencyForm, phone: e.target.value })} />
                        </div>
                        <div>
                          <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-1.5">Email</label>
                          <input type="email" className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 placeholder:text-slate-400 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
                            value={agencyForm.email} onChange={e => setAgencyForm({ ...agencyForm, email: e.target.value })} />
                        </div>
                      </div>
                    </div>
                  </div>

                  <div className="px-6 py-4 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-100 dark:border-slate-800 flex justify-end">
                    <Button
                      type="submit"
                      disabled={savingAgency}
                      className="px-6"
                    >
                      {savingAgency ? "Guardando..." : "Guardar Cambios"}
                    </Button>
                  </div>
                </div>
              </form>

              {/* Tarjeta: Datos bancarios de la agencia.
                  ownerType="Agency", ownerId=0 (convención del backend para la agencia).
                  Solo los admins pueden agregar/editar/borrar cuentas bancarias propias de la agencia.
                  Suposición: el permiso de edición es isAdmin() — no hay permiso fino definido aún. */}
              <ListaCuentasBancarias
                ownerType="Agency"
                ownerId={0}
                title="Datos bancarios de la agencia"
                canEdit={isAdmin()}
              />
            </div>

            {/* Side Panel for Configs */}
            <div className="space-y-6">
              <div className="bg-white dark:bg-slate-900 rounded-[14px] border border-slate-200 dark:border-slate-800 shadow-sm p-6">
                <h3 className="font-semibold text-slate-900 dark:text-white mb-4">Configuración Regional</h3>
                <div className="space-y-4">
                  <div>
                    <label className="block text-xs font-medium uppercase tracking-wide text-slate-500 mb-1.5">Moneda Base</label>
                    <select className="w-full rounded-[10px] border-slate-200 dark:border-slate-700 dark:bg-slate-800"
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

        {/* --- OPERATIONS TAB --- */}
        {activeTab === "operations" && <OperationalFinanceSettingsTab />}

        {/* --- AFIP TAB --- */}
        {activeTab === "afip" && <AfipSettingsTab />}

        {/* --- PRESUPUESTOS Y PDF TAB --- */}
        {activeTab === "budgetPdf" && <BudgetPdfSettingsTab />}

        {/* --- APPROVALS TAB --- */}
        {activeTab === "approvals" && <ApprovalPoliciesTab />}

        {/* --- WHATSAPP TAB --- */}
        {activeTab === "whatsapp" && <WhatsAppBotTab />}

        {/* --- INTELIGENCIA ARTIFICIAL TAB --- */}
        {activeTab === "ai" && <AiSettingsTab />}

        {/* --- LOGS TAB --- */}
        {activeTab === "logs" && <LogsDashboard />}

      </div>

    </div>
  );
}
