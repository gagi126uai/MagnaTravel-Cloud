import { NavLink } from "react-router-dom";
import { Activity, ClipboardList, PlaneTakeoff, Users } from "lucide-react";

// Spec firmada 2026-08-06 (§4.1, P11=A): 4 tabs nuevos. "Por reserva" se elimina
// (repetía lo que ya da la ficha de la reserva) y en su lugar entran "Viajan pronto y
// deben" y "Deuda por cliente".
//
// También se saca la tab "NC por revisar": esas entradas fueron derogadas el
// 2026-07-08 ("fin de las bandejas por tipo de comprobante") y hoy esa ruta vieja
// (/cancellations/credit-notes/inbox) solo redirige a Facturación — sacarla de acá es
// aplicar una regla ya firmada, no una decisión nueva de esta tanda.
const NAV_ITEMS = [
  { to: "/payments/departures", label: "Viajan pronto y deben", icon: PlaneTakeoff },
  { to: "/payments/by-customer", label: "Deuda por cliente", icon: Users },
  { to: "/payments/pending", label: "Pendientes de facturar", icon: ClipboardList },
  { to: "/payments/movements", label: "Movimientos", icon: Activity },
];

export function FinanceSubnav() {
  return (
    <div className="flex gap-6 border-b border-slate-100 dark:border-slate-800 overflow-x-auto">
      {NAV_ITEMS.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          end={item.end}
          className={({ isActive }) =>
            `pb-3 text-sm font-medium transition-colors relative whitespace-nowrap ${
              isActive
                ? "text-slate-900 dark:text-white"
                : "text-slate-400 hover:text-slate-600"
            }`
          }
        >
          {({ isActive }) => (
            <>
              <div className="flex items-center gap-2">
                <item.icon className="w-4 h-4" />
                {item.label}
              </div>
              {isActive && (
                <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-slate-900 dark:bg-white rounded-t-full" />
              )}
            </>
          )}
        </NavLink>
      ))}
    </div>
  );
}
