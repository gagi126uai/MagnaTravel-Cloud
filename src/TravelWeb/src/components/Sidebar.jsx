import { NavLink } from "react-router-dom";

const links = [
  { to: "/dashboard", label: "Dashboard" },
  { to: "/customers", label: "Clientes" },
  { to: "/reservations", label: "Reservas" },
  { to: "/payments", label: "Pagos" },
  { to: "/reports", label: "Reportes" },
];

export default function Sidebar({ onLogout }) {
  return (
    <aside className="flex h-screen w-64 flex-col border-r border-slate-800 bg-slate-950/80 backdrop-blur">
      <div className="flex items-center gap-3 px-6 py-5 text-xl font-semibold text-white">
        <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-500/20 text-indigo-200">
          MT
        </div>
        <div>
          <p className="text-base font-semibold">MagnaTravel</p>
          <p className="text-xs text-slate-400">Back office</p>
        </div>
      </div>
      <nav className="flex-1 space-y-1 px-3">
        {links.map((link) => (
          <NavLink
            key={link.to}
            to={link.to}
            className={({ isActive }) =>
              `block rounded-lg px-3 py-2 text-sm font-medium ${
                isActive ? "bg-slate-800 text-white" : "text-slate-300 hover:bg-slate-800"
              }`
            }
          >
            {link.label}
          </NavLink>
        ))}
      </nav>
      <div className="p-4">
        <button
          type="button"
          onClick={onLogout}
          className="w-full rounded-lg border border-slate-700 px-3 py-2 text-sm text-slate-200 hover:bg-slate-800"
        >
          Cerrar sesión
        </button>
      </div>
    </aside>
  );
}
