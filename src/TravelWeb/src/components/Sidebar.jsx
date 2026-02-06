import { NavLink } from "react-router-dom";
import {
    LayoutDashboard,
    Users,
    FolderOpen,
    CreditCard,
    Building2,
    Settings,
    LogOut,
    BarChart3,
    X
} from "lucide-react";
import { cn } from "../lib/utils";

// Clean Menu - Retail ERP Loop
const menuLinks = [
    { to: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
    { to: "/files", label: "Gestión de Viajes", icon: FolderOpen },
    { to: "/customers", label: "Clientes", icon: Users },
    { to: "/payments", label: "Caja Administrativa", icon: CreditCard },
    { to: "/suppliers", label: "Proveedores", icon: Building2 },
];

export default function Sidebar({ onLogout, isAdmin, className, collapsed, onToggleCollapse, onCloseMobile }) {
    const finalLinks = isAdmin
        ? [...menuLinks, { to: "/reports", label: "Reportes", icon: BarChart3 }, { to: "/settings", label: "Configuración", icon: Settings }]
        : menuLinks;

    return (
        <aside className={cn("flex flex-col border-r bg-card text-card-foreground", className)}>
            {/* Header */}
            <div className={cn(
                "flex items-center border-b border-slate-200 dark:border-slate-800",
                collapsed ? "justify-center px-2 py-4" : "justify-between px-4 py-4"
            )}>
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

                {/* Mobile close button */}
                {onCloseMobile && !collapsed && (
                    <button
                        onClick={onCloseMobile}
                        className="md:hidden p-2 rounded-lg hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors"
                    >
                        <X className="h-5 w-5" />
                    </button>
                )}
            </div>

            {/* Navigation */}
            <nav className={cn(
                "flex-1 overflow-y-auto py-4",
                collapsed ? "px-2" : "px-3"
            )}>
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
                                    collapsed
                                        ? "justify-center p-3"
                                        : "gap-3 px-3 py-2.5",
                                    "hover:bg-slate-100 dark:hover:bg-slate-800",
                                    isActive
                                        ? "bg-indigo-50 text-indigo-700 shadow-sm dark:bg-indigo-900/20 dark:text-indigo-300"
                                        : "text-slate-600 dark:text-slate-400"
                                )
                            }
                        >
                            <link.icon className={cn("flex-shrink-0", collapsed ? "h-5 w-5" : "h-4 w-4")} />
                            {!collapsed && <span className="truncate">{link.label}</span>}
                        </NavLink>
                    ))}
                </div>
            </nav>

            {/* Footer - Logout */}
            <div className={cn(
                "border-t border-slate-200 dark:border-slate-800",
                collapsed ? "p-2" : "p-3"
            )}>
                <button
                    onClick={onLogout}
                    title={collapsed ? "Cerrar sesión" : undefined}
                    className={cn(
                        "flex w-full items-center rounded-lg text-sm font-medium text-slate-500 transition-all",
                        "hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/20",
                        collapsed
                            ? "justify-center p-3"
                            : "gap-3 px-3 py-2.5"
                    )}
                >
                    <LogOut className={cn("flex-shrink-0", collapsed ? "h-5 w-5" : "h-4 w-4")} />
                    {!collapsed && <span>Cerrar sesión</span>}
                </button>
            </div>
        </aside>
    );
}
