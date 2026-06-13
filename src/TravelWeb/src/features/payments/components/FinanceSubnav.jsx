import { NavLink } from "react-router-dom";
import { Activity, ClipboardList, FolderOpen, FileMinus2 } from "lucide-react";
import { hasPermission } from "../../../auth";

// B1.15 Fase D'.B (2026-05-11): 3 tabs principales (Por reserva / Movimientos / Pendientes).
// Reemplaza la triada vieja (Cobranzas / Facturacion / Historial)
// porque la nueva division es coherente con el modelo de Movement unificado.
//
// Tab adicional "NC por revisar" (ADR-025): solo para usuarios con cobranzas.view_all.
// La ruta /cancellations/credit-notes/inbox es una ruta separada (no sub-ruta de /payments),
// pero se expone como tab en esta subnav porque es la "bandeja hermana" del flujo de cancelación
// y pertenece conceptualmente al área de Cobranza y Facturación.
const BASE_NAV_ITEMS = [
  { to: "/payments/reservas", label: "Por reserva", icon: FolderOpen },
  { to: "/payments/movements", label: "Movimientos", icon: Activity },
  { to: "/payments/pending", label: "Pendientes de facturar", icon: ClipboardList },
];

// Tab gateado: solo aparece si el usuario tiene cobranzas.view_all (back-office).
// Definido separado para poder filtrarlo en el render sin hardcodear la lógica en el JSX.
const NC_REVIEW_ITEM = {
  to: "/cancellations/credit-notes/inbox",
  label: "NC por revisar",
  icon: FileMinus2,
  requiredPermission: "cobranzas.view_all",
};

export function FinanceSubnav() {
  // Construimos la lista de tabs según los permisos del usuario actual.
  // El filtro corre en cada render; es liviano y no necesita memoización.
  const navItems = [
    ...BASE_NAV_ITEMS,
    // Tab NC: solo visible para back-office con cobranzas.view_all.
    ...(hasPermission(NC_REVIEW_ITEM.requiredPermission) ? [NC_REVIEW_ITEM] : []),
  ];

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
