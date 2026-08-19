import { useNavigate } from "react-router-dom";
import { FileText } from "lucide-react";
import { Button } from "../../../components/ui/button";
import { DolarBnaTira } from "../../../components/DolarBnaTira";

/**
 * Cabecera de "Inicio" (spec dashboard 2026-08-18, sección 1.3): título + bajada
 * a la izquierda, chip del dólar BNA + botón principal a la derecha. En mobile
 * las dos filas se apilan (mismo patrón `flex-col ... lg:flex-row` que ya usa
 * el resto de la app).
 *
 * `onRefrescarDolar` reusa la MISMA función que recarga el dashboard completo
 * (no solo el dólar) — así el botón "actualizar" de adentro de `DolarBnaTira`
 * deja los KPIs/listas al día también, igual que hacía antes en AdminDashboard/
 * AgentDashboard.
 */
export function DashboardHeader({ dolarRate, onRefrescarDolar }) {
  const navigate = useNavigate();

  return (
    <header className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
      <div>
        <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Inicio</h1>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          El trabajo a la izquierda, la plata a la derecha.
        </p>
      </div>
      <div className="flex flex-wrap items-center gap-3">
        <DolarBnaTira rate={dolarRate} onRefrescar={onRefrescarDolar} />
        <Button type="button" onClick={() => navigate("/reservas?create=1")}>
          <FileText className="h-4 w-4" />
          Nuevo presupuesto
        </Button>
      </div>
    </header>
  );
}
