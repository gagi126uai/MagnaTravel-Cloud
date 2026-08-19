import { useNavigate } from "react-router-dom";
import { ArrowRight, BarChart3 } from "lucide-react";
import { Button } from "../../../components/ui/button";

/**
 * Tarjeta-link "Informes completos" (spec dashboard 2026-08-18, sección 1.3,
 * pie de la columna PLATA). Única puerta nueva a `/analytics` — no se agrega
 * entrada al Sidebar (decisión explícita de la spec, sección 5, punto 5).
 */
export function ReportsLinkCard() {
  const navigate = useNavigate();

  return (
    <div className="flex items-center justify-between gap-3 rounded-[14px] border border-slate-200 bg-white px-5 py-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-center gap-3">
        <BarChart3 className="h-5 w-5 text-slate-400" aria-hidden="true" />
        <p className="text-sm text-slate-600 dark:text-slate-300">
          Informes completos: vendedores, destinos y año contra año
        </p>
      </div>
      <Button type="button" variant="outline" size="sm" onClick={() => navigate("/analytics")}>
        Ver informes
        <ArrowRight className="h-4 w-4" aria-hidden="true" />
      </Button>
    </div>
  );
}
