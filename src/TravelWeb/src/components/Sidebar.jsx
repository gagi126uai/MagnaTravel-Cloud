import { NavLink } from "react-router-dom";
import {
  LayoutDashboard,
  Users,
  FolderOpen,
  CreditCard,
  ArrowLeftRight,
  Building2,
  Settings,
  LogOut,
  BarChart3,
  X,
  DollarSign,
  UserPlus,
  Package,
  Shield,
} from "lucide-react";
import { cn } from "../lib/utils";
import { useAlerts } from "../contexts/AlertsContext";
import { hasPermission } from "../auth";

const mainLinks = [
  { to: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
  { to: "/crm", label: "Posibles clientes", icon: UserPlus, requiredPermission: "crm.view" },
  { to: "/reservas", label: "Reservas", icon: FolderOpen, requiredPermission: "reservas.view" },
  { to: "/customers", label: "Clientes", icon: Users, requiredPermission: "clientes.view" },
  { to: "/suppliers", label: "Proveedores", icon: Building2, requiredPermission: "proveedores.view" },
  { to: "/payments", label: "Cobranza y Facturacion", icon: CreditCard, requiredPermission: "cobranzas.view" },
  { to: "/cash", label: "Caja", icon: ArrowLeftRight, requiredPermission: "caja.view" },
  { to: "/rates", label: "Tarifario", icon: DollarSign, requiredPermission: "tarifario.view" },
  { to: "/packages", label: "Paises y destinos", icon: Package, requiredPermission: "paquetes.view" },
  { to: "/reports", label: "Reportes e Inteligencia", icon: BarChart3, requiredPermission: "reportes.view" },
  { to: "/audit", label: "Auditoría", icon: Shield, requiredPermission: "auditoria.view" },
  { to: "/settings", label: "Configuracion", icon: Settings, requiredPermission: "configuracion.view" },
];

export default function Sidebar({ onLogout, isAdmin, className, collapsed, onCloseMobile }) {
  const { alerts } = useAlerts();

  const linksWithBadges = mainLinks.map((link) => {
    if (link.to === "/alerts") {
      return { ...link, badge: alerts?.TotalCount };
    }

    return link;
  });

  // Filter links based on permissions (Dashboard is always visible)
  const finalLinks = linksWithBadges.filter((link) => {
    if (!link.requiredPermission) return true;
    return hasPermission(link.requiredPermission);
  });

  return (
    <aside className={cn("flex flex-col border-r bg-card text-card-foreground", className)}>
      <div
        className={cn(
          "flex items-center border-b border-slate-200 dark:border-slate-800",
          collapsed ? "justify-center px-2 py-4" : "justify-between px-4 py-4"
        )}
      >
        <div className={cn("flex items-center gap-3", collapsed && "justify-center")}>
          <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-600 text-white shadow-md shadow-indigo-500/20 flex-shrink-0">
            <span className="text-xl font-bold">MT</span>
          </div>
          {!collapsed && (
            <div className="min-w-0">
              <p className="text-sm font-bold truncate">MagnaTravel</p>
              <p className="text-xs text-muted-foreground">Retail ERP</p>
            </div>
          )}
        </div>

        {onCloseMobile && !collapsed && (
          <button
            onClick={onCloseMobile}
            className="md:hidden p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors"
            type="button"
          >
            <X className="h-5 w-5" />
          </button>
        )}
      </div>

      <nav className={cn("flex-1 overflow-y-auto py-4", collapsed ? "px-2" : "px-3")}>
        <div className="space-y-1">
          {finalLinks.map((link) => (
            <NavLink
              key={link.to}
              to={link.to}
              onClick={onCloseMobile}
              title={collapsed ? link.label : undefined}
              className={({ isActive }) =>
                cn(
                  "group flex items-center rounded-lg text-sm font-medium transition-all",
                  collapsed ? "justify-center p-3" : "gap-3 px-3 py-2.5",
                  "hover:bg-slate-100 dark:hover:bg-slate-800",
                  isActive
                    ? "bg-indigo-50 text-indigo-700 shadow-sm dark:bg-indigo-900/20 dark:text-indigo-300"
                    : "text-slate-600 dark:text-slate-400"
                )
              }
            >
              <link.icon className={cn("flex-shrink-0", collapsed ? "h-5 w-5" : "h-4 w-4")} />
              {!collapsed && <span className="truncate flex-1">{link.label}</span>}
              {!collapsed && link.badge > 0 && (
                <span className="bg-red-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded-full">
                  {link.badge > 99 ? "99+" : link.badge}
                </span>
              )}
              {collapsed && link.badge > 0 && (
                <span className="absolute top-2 right-2 h-2 w-2 rounded-full bg-red-500 border border-white dark:border-slate-900" />
              )}
            </NavLink>
          ))}
        </div>
      </nav>

      <div className={cn("border-t border-slate-200 dark:border-slate-800", collapsed ? "p-2" : "p-3")}>
        <button
          onClick={onLogout}
          title={collapsed ? "Cerrar sesion" : undefined}
          className={cn(
            "flex w-full items-center rounded-lg text-sm font-medium text-slate-500 transition-all",
            "hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/20",
            collapsed ? "justify-center p-3" : "gap-3 px-3 py-2.5"
          )}
          type="button"
        >
          <LogOut className={cn("flex-shrink-0", collapsed ? "h-5 w-5" : "h-4 w-4")} />
          {!collapsed && <span>Cerrar sesion</span>}
        </button>
      </div>
    </aside>
  );
}
