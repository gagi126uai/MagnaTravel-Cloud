import { useEffect, useRef, useState } from "react";
import { Loader2 } from "lucide-react";
import { api } from "../api";
import { deactivateMaintenance } from "../maintenanceState";
import { construirPasosEsperaRestoreTotal } from "../features/admin/lib/dangerRestoreLogic";

const INTERVALO_REINTENTO_MS = 5000;

/**
 * Pantalla de mantenimiento a pantalla completa (obra 2026-07-27, "Restaurar todo"; rediseño
 * 2026-07-30 de la solapa "Copias de seguridad", §4.5: checklist de pasos + sin promesa de
 * tiempo). Se monta UNA sola vez, en App.jsx, como un cartel ENCIMA de toda la app (no
 * reemplaza el árbol de componentes, lo tapa) cuando el store global de maintenanceState.js
 * dice que el sistema está restaurando. Esto es a propósito: si en vez de un cartel
 * superpuesto hiciéramos un "return" que reemplaza todo, la pantalla que disparó la
 * restauración (RestoreBackupFicha) se desmontaría y perdería el resumen final que está
 * esperando mostrar apenas el motor le conteste.
 *
 * Se muestra en dos caminos (ver maintenanceState.js): quien ejecutó "Volver a esta copia" en
 * esta misma pestaña (conoce `fechaResguardo`, la vio en la lista antes de tocar el botón), y
 * cualquier otro usuario cuyo pedido chocó con un 503 MAINTENANCE (interceptado en api.js) —
 * ese otro usuario NO conoce la fecha, así que ve un título genérico.
 *
 * Reintenta solo, cada 5 segundos, contra GET /system/status (pensado como endpoint
 * anónimo y liviano, disponible incluso mientras el resto de la API devuelve 503). Cada
 * respuesta también trae en qué paso va la restauración (`paso`/`pasoTexto`), que acá se
 * pinta como una checklist de 3 líneas — sin prometer un tiempo (P-20, hallazgo del dueño
 * "promete un minuto y da mal feedback").
 */
export function MaintenanceScreen({ awaitingLocalResult = false, fechaResguardo = null }) {
  const encuestaEnCursoRef = useRef(false);
  const headingRef = useRef(null);
  const [pasoActual, setPasoActual] = useState({ paso: null, pasoTexto: null });

  // useEffect con dependencia vacia: el foco se mueve al heading UNA sola vez, apenas
  // aparece la pantalla (mismo criterio de accesibilidad que ErrorBoundary.jsx), para que
  // un lector de pantalla anuncie el cambio aunque el usuario no esté mirando la ventana
  // en ese momento.
  useEffect(() => {
    headingRef.current?.focus();
  }, []);

  useEffect(() => {
    // El intervalo arranca cada vez que cambia awaitingLocalResult porque esa bandera
    // decide QUÉ hacer al detectar que el sistema volvió (ver más abajo) — si quedara
    // fija en un closure viejo, un cambio de bandera mientras el cartel sigue montado
    // no se tendría en cuenta.
    const intervalId = setInterval(async () => {
      if (encuestaEnCursoRef.current) return; // evita superponer pedidos si la red anda lenta
      encuestaEnCursoRef.current = true;
      try {
        const estado = await api.get("/system/status", { skipAuthRedirect: true });
        // Rediseño 2026-07-30: se guarda el paso en curso en TODAS las vueltas (incluso si
        // el sistema ya volvió), así la última línea que se alcanza a pintar antes de que
        // el efecto de abajo cierre el cartel es siempre la más reciente que mandó el motor.
        setPasoActual({ paso: estado?.paso ?? null, pasoTexto: estado?.pasoTexto ?? null });
        if (estado?.enMantenimiento === false) {
          if (awaitingLocalResult) {
            // Quien disparó la restauración ya tiene, en esta misma pestaña, un pedido en
            // vuelo que va a traer el resumen de qué se restauró — acá solo destrabamos el
            // cartel, SIN recargar (un reload de más perdería ese resumen).
            deactivateMaintenance();
          } else {
            // Cualquier otro usuario: no hay ningún resumen que mostrar, así que conviene
            // arrancar de cero con un reload completo en vez de intentar "resucitar" el
            // estado que tenía la pantalla antes de la restauración.
            window.location.reload();
          }
        }
      } catch {
        // Mientras dura la restauración, /system/status también puede devolver 503: se
        // interpreta como "todavía no volvió" y se sigue esperando en silencio.
      } finally {
        encuestaEnCursoRef.current = false;
      }
    }, INTERVALO_REINTENTO_MS);

    return () => clearInterval(intervalId);
  }, [awaitingLocalResult]);

  const pasos = construirPasosEsperaRestoreTotal(pasoActual);

  return (
    <div
      data-testid="maintenance-screen"
      role="alert"
      aria-live="assertive"
      className="fixed inset-0 z-[9999] flex min-h-screen flex-col items-center justify-center gap-4 bg-slate-950 px-6 text-center text-white"
    >
      <Loader2 className="h-12 w-12 animate-spin text-indigo-400" aria-hidden="true" />
      <h1
        // tabIndex={-1}: puede recibir foco por código (headingRef.focus() en el useEffect
        // de arriba) sin entrar al tab order normal del teclado.
        tabIndex={-1}
        ref={headingRef}
        className="text-2xl font-black outline-none"
      >
        {/* Solo quien disparó la accion en esta pestaña conoce la fecha del resguardo elegido
            (fechaResguardo, ver maintenanceState.js) — cualquier otro usuario ve un título
            genérico en vez de una fecha inventada. */}
        {fechaResguardo ? `Estamos volviendo a la copia del ${fechaResguardo}` : "Estamos volviendo a una copia anterior"}
      </h1>
      <p className="text-sm font-semibold text-amber-300">No cierres esta ventana</p>

      {/* Checklist de 3 pasos (rediseño 2026-07-30 §4.5, P-20 "sin prometer un tiempo"): el
          orden es el REAL del motor (datos → resguardo → actualización, ver dangerRestoreLogic.js).
          Si todavía no hay ningún paso informado (ej. el sondeo no llegó a tiempo), se muestra
          igual, sin ningún ítem marcado.
          Fix de review (accesibilidad, hallazgo bloqueante): el estado de cada paso NO puede
          depender solo del símbolo (✓/◐/○, marcado aria-hidden) ni del color — un lector de
          pantalla necesita el estado como texto. `aria-live="polite"` en la lista avisa cuando
          cambia el paso en curso, sin interrumpir como haría "assertive" en cada actualización. */}
      <ul className="mt-2 space-y-2 text-left text-sm" data-testid="maintenance-screen-pasos" aria-live="polite">
        {pasos.map((paso) => {
          const textoEstadoAccesible =
            paso.estado === "done" ? "Hecho: " : paso.estado === "doing" ? "En curso: " : "Todavía no: ";
          return (
            <li key={paso.codigo} className="flex items-center gap-2">
              <span aria-hidden="true">
                {paso.estado === "done" ? "✓" : paso.estado === "doing" ? "◐" : "○"}
              </span>
              <span
                className={
                  paso.estado === "doing"
                    ? "font-bold text-white"
                    : paso.estado === "done"
                      ? "text-emerald-400"
                      : "text-slate-400"
                }
              >
                <span className="sr-only">{textoEstadoAccesible}</span>
                {paso.texto}
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
