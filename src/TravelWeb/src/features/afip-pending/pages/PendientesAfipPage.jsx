/**
 * Página "Pendientes con AFIP": une las 3 bandejas back-office que antes vivían
 * sueltas y dispersas en el menú (multas/notas de débito pendientes, notas de
 * crédito por revisar, recibos por reconciliar). Casi todos estos casos se
 * arreglan solos con el tiempo; esta pantalla junta los que necesitan una mano.
 * Se accede desde "Pendientes con AFIP" en el módulo GESTIÓN del sidebar.
 *
 * Shell delgado (spec "fin de las bandejas", 2026-07-08): cada solapa es
 * exactamente la página de bandeja que ya existía, TAL CUAL, con su propio
 * loading/vacío/error — esta página solo arma la navegación entre ellas y
 * decide cuál se ve primero según los permisos del usuario logueado.
 */

import { useSearchParams } from "react-router-dom";
import { FileWarning } from "lucide-react";
import { hasPermission } from "../../../auth";
import CancellationDebitNoteInboxPage from "../../cancellations/pages/CancellationDebitNoteInboxPage";
import CancellationCreditNoteInboxPage from "../../cancellations/pages/CancellationCreditNoteInboxPage";
import CreditNoteReconciliationInboxPage from "../../creditNoteReconciliation/pages/CreditNoteReconciliationInboxPage";
import { getAllowedAfipPendingTabs, resolveInitialAfipPendingTab } from "../lib/resolveInitialTab";

// Qué componente renderiza cada solapa. Vive acá (y no en resolveInitialTab.js)
// porque ese archivo es lógica pura testeable con Node solo, sin JSX/React.
const AFIP_PENDING_TAB_CONTENT = {
  multas: CancellationDebitNoteInboxPage,
  notasCredito: CancellationCreditNoteInboxPage,
  recibos: CreditNoteReconciliationInboxPage,
};

export default function PendientesAfipPage() {
  const [searchParams, setSearchParams] = useSearchParams();

  const allowedTabs = getAllowedAfipPendingTabs(hasPermission);
  const activeTab = resolveInitialAfipPendingTab(searchParams.get("tab"), hasPermission);

  // Sin ningún permiso de las 3 bandejas: la ruta en App.jsx ya debería haber
  // redirigido antes de llegar acá. Este estado es un resguardo por si algo
  // cambió entremedio (ej: le sacaron el permiso en otra pestaña del navegador).
  if (!activeTab) {
    return (
      <div className="rounded-2xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-500 dark:border-slate-800 dark:bg-slate-900 dark:text-slate-400">
        No tenés permiso para ver ninguna de estas bandejas.
      </div>
    );
  }

  const ActiveTabContent = AFIP_PENDING_TAB_CONTENT[activeTab];

  return (
    <div className="space-y-6">
      {/* Encabezado */}
      <div className="flex items-center gap-3">
        <div className="rounded-lg bg-orange-100 p-2 text-orange-700 dark:bg-orange-900/30 dark:text-orange-300">
          <FileWarning className="h-5 w-5" />
        </div>
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white">Pendientes con AFIP</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Comprobantes que quedaron esperando resolverse con AFIP. Casi siempre se arreglan solos;
            acá están los que necesitan una mano.
          </p>
        </div>
      </div>

      {/* Barra de solapas — mismo patrón visual que CustomerAccountPage (subrayado indigo activo) */}
      <div className="flex flex-wrap gap-6 border-b border-slate-200 dark:border-slate-800">
        {allowedTabs.map((tab) => (
          <button
            key={tab.key}
            type="button"
            // replace: true para no ensuciar el historial del navegador con un
            // paso por cada click entre solapas (mismo criterio que el resto del ERP).
            onClick={() => setSearchParams({ tab: tab.key }, { replace: true })}
            className={`relative pb-4 text-sm font-semibold transition-colors ${
              activeTab === tab.key
                ? "text-indigo-600 dark:text-indigo-400"
                : "text-slate-500 hover:text-slate-700 dark:hover:text-slate-300"
            }`}
            data-testid={`tab-pendientes-${tab.key}`}
          >
            {tab.label}
            {activeTab === tab.key && (
              <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-full bg-indigo-600 dark:bg-indigo-400" />
            )}
          </button>
        ))}
      </div>

      {/* Contenido: la bandeja tal cual existía, con su propio loading/vacío/error */}
      <ActiveTabContent />
    </div>
  );
}
