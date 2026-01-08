import { NavLink } from "react-router-dom";

const baseLinks = [
  { to: "/dashboard", label: "Dashboard" },
  { to: "/customers", label: "Clientes" },
  { to: "/reservations", label: "Reservas" },
  { to: "/payments", label: "Pagos" },
  { to: "/suppliers", label: "Proveedores" },
];

export default function Sidebar({ onLogout, isAdmin }) {
  const menuLinks = isAdmin
    ? [...baseLinks, { to: "/reports", label: "Reportes" }, { to: "/settings", label: "Configuración" }]
    : baseLinks;
  return (
    <aside className="flex h-screen w-72 flex-col border-r border-slate-200 bg-white px-4 py-6 dark:border-slate-800 dark:bg-slate-950">
      <div className="flex items-center gap-3 px-2 py-4 text-xl font-semibold">
        <div className="flex h-11 w-11 items-center justify-center rounded-2xl bg-indigo-600/10 text-indigo-600 dark:text-indigo-300">
          MT
        </div>
        <div>
          <p className="text-base font-semibold">MagnaTravel</p>
          <p className="text-xs text-slate-500 dark:text-slate-400">Backoffice profesional</p>
        </div>
      </div>
      <nav className="flex-1 space-y-1 px-1">
        {menuLinks.map((link) => (
          <NavLink
            key={link.to}
            to={link.to}
            className={({ isActive }) =>
              `block rounded-xl px-4 py-3 text-sm font-medium transition ${
                isActive
                  ? "bg-indigo-600 text-white shadow-lg shadow-indigo-500/20"
                  : "text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-900"
              }`
            }
          >
            {link.label}
          </NavLink>
        ))}
      </nav>
      <div className="px-2 pt-4 text-xs text-slate-500 dark:text-slate-400">
        Plataforma para agencias · v1.0
      </div>
    </aside>
  );
}
