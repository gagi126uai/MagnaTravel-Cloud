import { NavLink } from "react-router-dom";
import {
    LayoutDashboard,
    Users,
    FolderOpen,
    CreditCard,
    Building2,
    Settings,
    LogOut,
    BarChart3
} from "lucide-react";
import { cn } from "../lib/utils";

// Clean Menu - Retail ERP Loop
const menuLinks = [
    { to: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
    { to: "/files", label: "Gestión de Viajes", icon: FolderOpen }, // Renamed from Expedientes
    { to: "/customers", label: "Clientes", icon: Users },
    { to: "/payments", label: "Caja Administrativa", icon: CreditCard }, // Renamed from Pagos
    { to: "/suppliers", label: "Proveedores", icon: Building2 },
    // Hidden: Cupos, Cotizaciones (Merged), Tariffs, Agencies
];

export default function Sidebar({ onLogout, isAdmin, className }) {
    const finalLinks = isAdmin
        ? [...menuLinks, { to: "/reports", label: "Reportes", icon: BarChart3 }, { to: "/settings", label: "Configuración", icon: Settings }]
        : menuLinks;

    return (
        <aside className={cn("flex flex-col border-r bg-card text-card-foreground", className)}>
            <div className="flex items-center gap-3 px-6 py-6">
                <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-600 text-white shadow-md shadow-indigo-500/20">
                    <span className="text-xl font-bold">MT</span>
                </div>
                <div>
                    <p className="text-sm font-bold">MagnaTravel</p>
                    <p className="text-xs text-muted-foreground">Retail ERP</p>
                </div>
            </div>
            <nav className="flex-1 space-y-1 px-4 py-4">
                {finalLinks.map((link) => (
                    <NavLink
                        key={link.to}
                        to={link.to}
                        className={({ isActive }) =>
                            cn(
                                "group flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition-all hover:bg-slate-100 dark:hover:bg-slate-800",
                                isActive ? "bg-indigo-50 text-indigo-700 shadow-sm dark:bg-indigo-900/20 dark:text-indigo-300" : "text-slate-600 dark:text-slate-400"
                            )
                        }
                    >
                        <link.icon className="h-4 w-4" />
                        {link.label}
                    </NavLink>
                ))}
            </nav>
            <div className="p-4 border-t border-slate-200 dark:border-slate-800">
                <button
                    onClick={onLogout}
                    className="flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium text-slate-500 transition-all hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/20"
                >
                    <LogOut className="h-4 w-4" />
                    Cerrar sesión
                </button>
            </div>
        </aside>
    );
}
