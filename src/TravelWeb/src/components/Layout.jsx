import { useEffect, useState } from "react";
import Sidebar from "./Sidebar";

const THEME_KEY = "magna-theme";

export default function Layout({ children, onLogout, isAdmin }) {
  const [theme, setTheme] = useState(() => localStorage.getItem(THEME_KEY) || "dark");

  useEffect(() => {
    const root = document.documentElement;
    if (theme === "dark") {
      root.classList.add("dark");
    } else {
      root.classList.remove("dark");
    }
    localStorage.setItem(THEME_KEY, theme);
  }, [theme]);

  const toggleTheme = () => {
    setTheme((prev) => (prev === "dark" ? "light" : "dark"));
  };

  return (
    <div className="flex min-h-screen bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      <Sidebar onLogout={onLogout} isAdmin={isAdmin} />
      <div className="flex flex-1 flex-col">
        <header className="flex items-center justify-between border-b border-slate-200 bg-white/80 px-8 py-4 backdrop-blur dark:border-slate-800 dark:bg-slate-950/80">
          <div>
            <p className="text-xs uppercase tracking-[0.2em] text-slate-400">MagnaTravel</p>
            <h1 className="text-xl font-semibold">Backoffice de agencias</h1>
          </div>
          <div className="flex items-center gap-3">
            <button
              type="button"
              onClick={toggleTheme}
              className="rounded-full border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 transition hover:bg-slate-100 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
            >
              {theme === "dark" ? "Modo claro" : "Modo oscuro"}
            </button>
            <button
              type="button"
              onClick={onLogout}
              className="rounded-full bg-indigo-600 px-4 py-2 text-sm font-semibold text-white transition hover:bg-indigo-500"
            >
              Cerrar sesión
            </button>
          </div>
        </header>
        <main className="flex-1 px-8 py-8">
          <div className="mx-auto max-w-6xl">{children}</div>
        </main>
      </div>
    </div>
  );
}
