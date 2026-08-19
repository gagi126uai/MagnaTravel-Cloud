import { Link } from "react-router-dom";

/**
 * Menú lateral de la Pantalla 2 (Sección de Configuración) — SOLO desktop. En mobile no
 * existe (§3.5 de la spec: "sin menú lateral", se cambia de sección volviendo a la
 * portada y tocando otra tarjeta — nada de cajones/drawers nuevos, P-5).
 *
 * Mismos grupos y mismo orden que la portada (§3.2), sin íconos: la portada ya los
 * mostró, repetirlos acá sería decir el mismo dato dos veces (P-16).
 */
export default function SettingsSectionNav({ grupos, slugActivo }) {
  return (
    <nav
      aria-label="Secciones de Configuración"
      className="hidden md:block w-[248px] shrink-0 border-r border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900"
    >
      <div className="p-4">
        <Link
          to="/settings"
          className="inline-flex items-center gap-1 rounded-[6px] text-[13px] font-semibold text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40 focus-visible:ring-offset-2"
        >
          ← Configuración
        </Link>

        <div className="mt-6 space-y-6">
          {grupos.map((grupo) => (
            <div key={grupo.grupo}>
              <p className="px-2 text-[11px] font-semibold uppercase tracking-wide text-slate-400">
                {grupo.grupo}
              </p>
              <ul className="mt-2 space-y-0.5">
                {grupo.items.map((seccion) => {
                  const activo = seccion.slug === slugActivo;
                  return (
                    <li key={seccion.slug}>
                      <Link
                        to={`/settings/${seccion.slug}`}
                        aria-current={activo ? "page" : undefined}
                        className={
                          "block rounded-[8px] px-2 py-2 text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40 " +
                          (activo
                            ? "bg-blue-50 font-semibold text-primary dark:bg-blue-950/40"
                            : "text-slate-500 hover:bg-slate-50 hover:text-slate-700 dark:text-slate-400 dark:hover:bg-slate-800")
                        }
                      >
                        {seccion.titulo}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </div>
      </div>
    </nav>
  );
}
