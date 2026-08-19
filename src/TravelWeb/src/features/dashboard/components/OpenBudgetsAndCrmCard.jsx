import { useNavigate } from "react-router-dom";
import { Briefcase } from "lucide-react";
import { Button } from "../../../components/ui/button";

/**
 * Tarjeta chica combinada "N presupuesto(s) abierto(s) · N posibles clientes"
 * del dashboard (spec dashboard 2026-08-18, sección 1.3). Reemplaza al botón
 * suelto "Posibles clientes" que antes vivía en la cabecera (cambio de
 * ubicación autorizado por la firma del 18/08).
 *
 * Cuenta global (NO se filtra por vendedor, ver la tabla de variantes por rol
 * de la spec, sección 1.5): el backend no recorta `Presupuestos`/
 * `ActivePotentialCustomers` por cartera hoy, así que el frontend no inventa
 * un recorte que el backend no hace (P-13).
 */
export function OpenBudgetsAndCrmCard({ presupuestosAbiertos, posiblesClientes }) {
  const navigate = useNavigate();

  return (
    <div className="flex items-center justify-between gap-3 rounded-[14px] border border-slate-200 bg-white px-5 py-4 shadow-sm dark:border-slate-800 dark:bg-slate-900">
      <p className="text-sm font-semibold text-slate-700 dark:text-slate-300">
        {pluralizar(presupuestosAbiertos, "presupuesto abierto", "presupuestos abiertos")}
        {" · "}
        {pluralizar(posiblesClientes, "posible cliente", "posibles clientes")}
      </p>
      <Button type="button" variant="outline" size="sm" onClick={() => navigate("/crm")}>
        <Briefcase className="h-4 w-4" />
        Ir al CRM →
      </Button>
    </div>
  );
}

function pluralizar(cantidad, singular, plural) {
  const numero = Number(cantidad) || 0;
  return numero === 1 ? `1 ${singular}` : `${numero} ${plural}`;
}
