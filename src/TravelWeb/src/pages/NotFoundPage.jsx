import { Link } from "react-router-dom";
import { Compass } from "lucide-react";

/**
 * Pantalla que se muestra cuando la URL no corresponde a ninguna pantalla del sistema
 * (hallazgo #18 del barrido de PROD 2026-07-24: /operators quedaba en blanco).
 *
 * Antes de esta obra, App.jsx no tenía una ruta "atrapa-todo" (catch-all): si el
 * usuario entraba a una URL que no coincidía con ninguna ruta definida (por ejemplo,
 * escribiendo "/operators" a mano en vez de "/suppliers", que es la ruta real de la
 * pantalla "Operadores"), React Router no renderizaba nada — la pantalla quedaba
 * blanca, con el menú lateral visible pero el contenido vacío, sin ningún aviso de
 * qué pasó. Esta pantalla reemplaza ese vacío con un mensaje claro y un camino de
 * vuelta al sistema.
 */
export default function NotFoundPage() {
  return (
    <div className="flex min-h-[60vh] flex-col items-center justify-center gap-4 px-6 text-center">
      <div className="flex h-16 w-16 items-center justify-center rounded-full bg-slate-100 dark:bg-slate-800">
        <Compass className="h-8 w-8 text-slate-400" />
      </div>
      <div className="space-y-1.5">
        <h1 className="text-lg font-bold text-slate-900 dark:text-white">
          No encontramos esta pantalla
        </h1>
        <p className="max-w-sm text-sm text-slate-500 dark:text-slate-400">
          La dirección a la que intentaste entrar no existe o cambió de nombre.
          Revisá el link o volvé al inicio.
        </p>
      </div>
      <Link
        to="/dashboard"
        className="mt-2 inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm shadow-indigo-500/20 hover:bg-indigo-700"
      >
        Volver al inicio
      </Link>
    </div>
  );
}
