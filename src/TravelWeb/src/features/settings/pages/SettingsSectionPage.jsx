import { useState } from "react";
import { Link, Navigate, useParams } from "react-router-dom";
import { isAdmin, hasPermission } from "../../../auth";
import { Button } from "../../../components/ui/button";
import AfipSettingsTab from "../../../components/AfipSettingsTab";
import BudgetPdfSettingsTab from "../../../components/BudgetPdfSettingsTab";
import ApprovalPoliciesTab from "../../../components/ApprovalPoliciesTab";
import LogsDashboard from "../../../components/LogsDashboard";
import OperationalFinanceSettingsTab from "../../../components/OperationalFinanceSettingsTab";
import WhatsAppBotTab from "../../../components/WhatsAppBotTab";
import AiSettingsTab from "../../ai-settings/components/AiSettingsTab";
import AgencySettingsTab from "../components/AgencySettingsTab";
import SettingsSectionNav from "../components/SettingsSectionNav";
import { agruparSeccionesVisibles, encontrarSeccionVisiblePorSlug } from "../lib/settingsSections";

// Slug -> componente reusado TAL CUAL (spec §3.1 / "Qué NO hacer" #1): esta obra no toca
// ni un campo, ni una validación, ni una llamada a API de ninguno de estos 8 componentes.
// La única que es nueva es AgencySettingsTab, y es una extracción mecánica (§5).
const SECTION_COMPONENTS = {
  agencia: AgencySettingsTab,
  "operativa-caja": OperationalFinanceSettingsTab,
  facturacion: AfipSettingsTab,
  "presupuestos-pdf": BudgetPdfSettingsTab,
  whatsapp: WhatsAppBotTab,
  ia: AiSettingsTab,
  aprobaciones: ApprovalPoliciesTab,
  logs: LogsDashboard,
};

/**
 * Pantalla 2 del rediseño de Configuración (`/settings/{slug}`) — spec firmada
 * 2026-08-18. Muestra el menú lateral (solo desktop) + la cabecera de la sección
 * (título/bajada, iguales a los de la tarjeta de la portada, nunca copy nuevo) + el
 * componente existente de esa sección, reusado sin tocar su interior.
 */
export default function SettingsSectionPage() {
  const { slug } = useParams();
  const contexto = { esAdmin: isAdmin(), tienePermiso: hasPermission };
  const seccion = encontrarSeccionVisiblePorSlug(slug, contexto);

  // Guardamos acá si el formulario de Agencia está guardando, para poder deshabilitar y
  // cambiar el texto del botón "Guardar cambios" de la cabecera — ese botón vive afuera
  // del <form> (dispara el submit por el atributo form="agency-settings-form"), así que
  // esta página necesita saber el estado de guardado que expone AgencySettingsTab.
  const [agencySaving, setAgencySaving] = useState(false);

  // Slug inventado, o sección que existe pero el usuario logueado no puede ver (ej. un
  // vendedor pidiendo /settings/logs a mano): la Portada es el punto de entrada por
  // defecto de todo Configuración, NO "Agencia" (§6 de la spec) — por eso el fallback es
  // /settings, no una sección en particular.
  if (!seccion) {
    return <Navigate to="/settings" replace />;
  }

  const grupos = agruparSeccionesVisibles(contexto);
  const Contenido = SECTION_COMPONENTS[seccion.slug];
  const esAgencia = seccion.slug === "agencia";

  return (
    <div className="max-w-7xl mx-auto pb-20 md:pb-0">
      <div className="flex flex-col gap-6 md:flex-row md:items-start">
        <SettingsSectionNav grupos={grupos} slugActivo={seccion.slug} />

        <div className="min-w-0 flex-1 space-y-6">
          {/* En mobile no hay menú lateral (§3.5): este link de volver es la única forma
              de cambiar de sección, y va siempre arriba de todo. */}
          <Link
            to="/settings"
            className="inline-flex items-center gap-1 rounded-[6px] text-[13px] font-semibold text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40 focus-visible:ring-offset-2 md:hidden"
          >
            ← Configuración
          </Link>

          <header className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
            <div>
              <h1 className="text-[18px] font-bold text-slate-900 dark:text-white">{seccion.titulo}</h1>
              <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{seccion.descripcion}</p>
            </div>
            {/* Único botón de cabecera de toda esta obra (§5 de la spec): Agencia es la
                única de las 8 secciones cuyo "Guardar cambios" no vive ya resuelto adentro
                del componente reusado. Las otras 7 secciones no llevan botón acá. */}
            {esAgencia && (
              <Button type="submit" form="agency-settings-form" disabled={agencySaving} className="shrink-0">
                {agencySaving ? "Guardando..." : "Guardar cambios"}
              </Button>
            )}
          </header>

          <div>
            {esAgencia ? <Contenido onSavingChange={setAgencySaving} /> : <Contenido />}
          </div>
        </div>
      </div>
    </div>
  );
}
