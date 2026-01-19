import { NavLink } from "react-router-dom";
import {
    LayoutDashboard,
    Users,
    FileText,
    CalendarRange,
    Ticket,
    CreditCard,
    Landmark,
    BadgePercent,
    Building2,
    BarChart3,
    Settings,
    LogOut
} from "lucide-react";
import { cn } from "../lib/utils";

const baseLinks = [
    { to: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
    { to: "/customers", label: "Clientes", icon: Users },
    { to: "/quotes", label: "Cotizaciones", icon: FileText },
    { to: "/reservations", label: "Reservas", icon: CalendarRange },
    { to: "/cupos", label: "Cupos", icon: Ticket },
    { to: "/payments", label: "Pagos", icon: CreditCard },
    { to: "/suppliers", label: "Proveedores", icon: Building2 },
    { to: "/treasury", label: "Tesorería", icon: Landmark },
    { to: "/tariffs", label: "Tarifarios", icon: BadgePercent },
];

export default function Sidebar({ onLogout, isAdmin, className }) {
    const menuLinks = true // EMERGENCY OVERRIDE: isAdmin forced to true
        // Retail Pivot: Agencies is now settings, but keeping hidden or for SuperAdmin
        ? [...baseLinks, { to: "/reports", label: "Reportes", icon: BarChart3 }, { to: "/settings", label: "Configuración", icon: Settings }, { to: "/agencies", label: "Mi Empresa", icon: Building2 }]
        : baseLinks;

    return (
        <aside className={cn("flex flex-col border-r bg-card text-card-foreground", className)}>
            <div className="flex items-center gap-3 px-6 py-6">
                <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary/10 text-primary">
                    <span className="text-xl font-bold">MT</span>
                </div>
                <div>
                    <p className="text-sm font-bold">MagnaTravel</p>
                    <p className="text-xs text-muted-foreground">Backoffice Cloud</p>
                </div>
            </div>
            <nav className="flex-1 space-y-1 px-4 py-4">
                {menuLinks.map((link) => (
                    <NavLink
                        key={link.to}
                        to={link.to}
                        className={({ isActive }) =>
                            cn(
                                "group flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition-all hover:bg-accent hover:text-accent-foreground",
                                isActive ? "bg-primary text-primary-foreground shadow-sm hover:bg-primary hover:text-primary-foreground" : "text-muted-foreground"
                            )
                        }
                    >
                        <link.icon className="h-4 w-4" />
                        {link.label}
                    </NavLink>
                ))}
            </nav>
            <div className="p-4 border-t">
                <button
                    onClick={onLogout}
                    className="flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium text-muted-foreground transition-all hover:bg-destructive/10 hover:text-destructive"
                >
                    <LogOut className="h-4 w-4" />
                    Cerrar sesión
                </button>
            </div>
        </aside>
    );
}
