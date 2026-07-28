import { useEffect, useRef } from "react";
import { Loader2 } from "lucide-react";
import { api } from "../api";
import { deactivateMaintenance } from "../maintenanceState";

const INTERVALO_REINTENTO_MS = 5000;

/**
 * Pantalla de mantenimiento a pantalla completa (obra 2026-07-27, "Restaurar todo").
 * Se monta UNA sola vez, en App.jsx, como un cartel ENCIMA de toda la app (no reemplaza el
 * árbol de componentes, lo tapa) cuando el store global de maintenanceState.js dice que el
 * sistema está restaurando. Esto es a propósito: si en vez de un cartel superpuesto
 * hiciéramos un "return" que reemplaza todo, la pantalla que disparó la restauración
 * (RestaurarResguardoModal) se desmontaría y perdería el resumen final que está esperando
 * mostrar apenas el motor le conteste.
 *
 * Se muestra en dos caminos (ver maintenanceState.js): quien ejecutó "Restaurar todo" en
 * esta misma pestaña, y cualquier otro usuario cuyo pedido chocó con un 503 MAINTENANCE
 * (interceptado en api.js).
 *
 * Reintenta solo, cada 5 segundos, contra GET /system/status (pensado como endpoint
 * anónimo y liviano, disponible incluso mientras el resto de la API devuelve 503).
 */
export function MaintenanceScreen({ awaitingLocalResult = false }) {
  const encuestaEnCursoRef = useRef(false);
  const headingRef = useRef(null);

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
        Estamos restaurando el sistema
      </h1>
      <p className="text-lg text-slate-300">Volvemos en un minuto</p>
      <p className="text-sm font-semibold text-amber-300">No cierres esta ventana</p>
    </div>
  );
}
