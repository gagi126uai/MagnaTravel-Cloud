import { useEffect, useState } from "react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { isAdmin } from "../../../auth";
import { Building2 } from "lucide-react";
import { ListaCuentasBancarias } from "../../bank-accounts/components/ListaCuentasBancarias";

/**
 * Sección "Agencia" de Configuración: datos legales/de contacto + cuentas bancarias.
 *
 * Extracción MECÁNICA (spec 2026-08-18 §5): es exactamente el formulario que antes vivía
 * adentro de pages/SettingsPage.jsx (la única de las 8 secciones que no era un componente
 * propio) — mismos campos, mismo orden, mismas etiquetas, mismas llamadas a la API. La
 * única diferencia real es que el botón "Guardar cambios" YA NO vive acá abajo: ahora
 * vive una sola vez en la cabecera de la sección (SettingsSectionPage.jsx), disparando
 * este mismo <form> a través del atributo `form="agency-settings-form"` (P-16: un botón
 * de guardar, un solo lugar). Por eso este componente avisa hacia arriba cuándo está
 * guardando, vía `onSavingChange` — así el botón de la cabecera puede mostrarse
 * deshabilitado y con el texto "Guardando..." mientras el submit está en vuelo.
 */
export default function AgencySettingsTab({ onSavingChange }) {
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

  // Le avisamos al padre (la cabecera de sección) cada vez que cambia savingAgency, para
  // que el botón "Guardar cambios" de ahí arriba sepa cuándo deshabilitarse. onSavingChange
  // es estable entre renders (setState de useState), así que este efecto no genera un
  // loop — solo corre cuando savingAgency realmente cambia.
  useEffect(() => {
    onSavingChange?.(savingAgency);
  }, [savingAgency, onSavingChange]);

  const loadAgencySettings = async () => {
    try {
      const data = await api.get("/reports/settings");
      if (data) {
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

  // Carga inicial: se dispara una sola vez al montar (esta sección ya no vive detrás de
  // un `activeTab === "agency"`, el ruteo se encarga de montar/desmontar el componente).
  useEffect(() => {
    loadAgencySettings();
  }, []);

  return (
    <div className="grid gap-6 lg:grid-cols-3">
      <div className="lg:col-span-2 space-y-6">
        <form id="agency-settings-form" onSubmit={saveAgencySettings} className="space-y-6">
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
            {/* El botón "Guardar cambios" que antes vivía acá abajo se borró (spec §5,
                regla P-16): ahora vive una sola vez, en la cabecera de la sección. */}
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
              <select className="h-10 w-full rounded-[10px] border border-slate-200 bg-white px-3 text-sm text-slate-900 focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white"
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
  );
}
