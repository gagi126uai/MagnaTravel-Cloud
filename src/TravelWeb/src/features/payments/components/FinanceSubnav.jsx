import { NavLink } from "react-router-dom";
import { Activity, ClipboardList, FolderOpen } from "lucide-react";

// B1.15 Fase D'.B (2026-05-11): 3 tabs nuevos (Por reserva / Movimientos /
// Pendientes). Reemplaza la triada vieja (Cobranzas / Facturacion / Historial)
// porque la nueva division es coherente con el modelo de Movement unificado.
const navItems = [
  { to: "/payments/reservas", label: "Por reserva", icon: FolderOpen },
  { to: "/payments/movements", label: "Movimientos", icon: Activity },
  { to: "/payments/pending", label: "Pendientes de facturar", icon: ClipboardList },
];

export function FinanceSubnav() {
  return (
    <div className="flex gap-6 border-b border-slate-100 dark:border-slate-800 overflow-x-auto">
      {navItems.map((item) => (
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
