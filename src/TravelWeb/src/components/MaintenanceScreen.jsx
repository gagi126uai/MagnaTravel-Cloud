import { useEffect, useRef, useState } from "react";
import { Loader2 } from "lucide-react";
import { Button } from "./ui/button";
import { api } from "../api";
import { deactivateMaintenance } from "../maintenanceState";
import {
  construirPasosEsperaRestoreTotal,
  calcularTituloPantallaMantenimiento,
} from "../features/admin/lib/dangerRestoreLogic";

const INTERVALO_REINTENTO_MS = 5000;

// Fix de review (2d, plan tanda F): la spec original pedía un "tope razonable" de espera que
// nunca se había implementado — sin esto, un restore que se cuelga de verdad (motor caído,
// bug en el propio proceso de restauración) deja al usuario mirando el spinner PARA SIEMPRE,
// sin ninguna salida. 40 minutos es MAYOR al peor caso documentado en nginx.conf (33 minutos
// de trabajo real del motor + margen operativo = 45 min con timeouts largos) menos un margen,
// pensado para "avisar que algo raro pasa" sin cortar una restauración que todavía puede estar
// viva. Se cuenta en cantidad de sondeos (no en un solo setTimeout) porque el intervalo ya
// existe y así no hace falta un segundo timer coordinado con el mismo ciclo de vida.
const TOPE_SONDEOS_ESPERA_MS = 40 * 60 * 1000;
const TOPE_SONDEOS_ESPERA = Math.ceil(TOPE_SONDEOS_ESPERA_MS / INTERVALO_REINTENTO_MS);

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
export function MaintenanceScreen({ awaitingLocalResult = false, fechaResguardo = null, pedidoLocalPerdido = false }) {
  const encuestaEnCursoRef = useRef(false);
  const headingRef = useRef(null);
  const contadorSondeosRef = useRef(0);
  const [pasoActual, setPasoActual] = useState({ paso: null, pasoTexto: null });
  // F7 (deuda 30/07): motivo en criollo que manda el motor (Motivo/IMaintenanceModeService.Reason).
  // Hoy SOLO lo llena "Restaurar todo", pero el checklist de pasos de acá abajo es específico
  // de esa acción — si algún día el sistema entra en mantenimiento por otro motivo (sin pasos
  // de restore publicados), no tiene sentido seguir mostrando "Estamos volviendo a una copia".
  const [motivo, setMotivo] = useState(null);
  // Fix de review (2d, plan tanda F): true cuando se superó TOPE_SONDEOS_ESPERA sin que el
  // sistema vuelva. Deja de girar y ofrece una salida explícita en vez de dejar al usuario
  // mirando el spinner para siempre.
  const [seAgotoElTiempoDeEspera, setSeAgotoElTiempoDeEspera] = useState(false);

  // useEffect con dependencia vacia: el foco se mueve al heading UNA sola vez, apenas
  // aparece la pantalla (mismo criterio de accesibilidad que ErrorBoundary.jsx), para que
  // un lector de pantalla anuncie el cambio aunque el usuario no esté mirando la ventana
  // en ese momento.
  useEffect(() => {
    headingRef.current?.focus();
  }, []);

  useEffect(() => {
    // El intervalo arranca cada vez que cambia awaitingLocalResult/pedidoLocalPerdido porque
    // esas banderas deciden QUÉ hacer al detectar que el sistema volvió (ver más abajo) — si
    // quedaran fijas en un closure viejo, un cambio de bandera mientras el cartel sigue
    // montado no se tendría en cuenta.
    contadorSondeosRef.current = 0;
    const intervalId = setInterval(async () => {
      if (encuestaEnCursoRef.current) return; // evita superponer pedidos si la red anda lenta
      encuestaEnCursoRef.current = true;
      try {
        const estado = await api.get("/system/status", { skipAuthRedirect: true });
        // Rediseño 2026-07-30: se guarda el paso en curso en TODAS las vueltas (incluso si
        // el sistema ya volvió), así la última línea que se alcanza a pintar antes de que
        // el efecto de abajo cierre el cartel es siempre la más reciente que mandó el motor.
        setPasoActual({ paso: estado?.paso ?? null, pasoTexto: estado?.pasoTexto ?? null });
        setMotivo(estado?.motivo ?? null);
        if (estado?.enMantenimiento === false) {
          // Fix bug real (2c, plan tanda F): si el propio pedido de esta pestaña se perdió por
          // un corte de proxy (marcarPedidoLocalPerdido en RestoreBackupFicha.jsx), esta ficha
          // NUNCA va a recibir el resumen de éxito — hay que tratarla como al usuario pasivo
          // (reload duro) en vez de solo apagar el cartel y dejarla con datos de la base vieja.
          if (awaitingLocalResult && !pedidoLocalPerdido) {
            // Quien disparó la restauración ya tiene, en esta misma pestaña, un pedido en
            // vuelo que va a traer el resumen de qué se restauró — acá solo destrabamos el
            // cartel, SIN recargar (un reload de más perdería ese resumen).
            deactivateMaintenance();
          } else {
            // Cualquier otro usuario, o esta pestaña sin forma de mostrar su propio resumen:
            // no hay ningún resumen que mostrar, así que conviene arrancar de cero con un
            // reload completo en vez de intentar "resucitar" el estado que tenía la pantalla
            // antes de la restauración.
            window.location.reload();
          }
          return;
        }
      } catch {
        // Mientras dura la restauración, /system/status también puede devolver 503: se
        // interpreta como "todavía no volvió" y se sigue esperando en silencio.
      } finally {
        encuestaEnCursoRef.current = false;
      }

      // Fix de review (2d): contamos un sondeo más SOLO cuando el sistema todavía no volvió
      // (los casos de arriba ya hicieron `return` antes de llegar acá). Al superar el tope,
      // NO se corta el intervalo (a propósito): seguimos sondeando cada 5 segundos en segundo
      // plano, así que si el sistema vuelve más tarde el cartel se destraba o recarga solo,
      // sin que el usuario tenga que hacer nada. Lo único que cambia acá es la UI: se apaga el
      // spinner y se ofrece el botón "Recargar la pantalla" como salida explícita para quien
      // no quiere esperar más.
      contadorSondeosRef.current += 1;
      if (contadorSondeosRef.current >= TOPE_SONDEOS_ESPERA) {
        setSeAgotoElTiempoDeEspera(true);
      }
    }, INTERVALO_REINTENTO_MS);

    return () => clearInterval(intervalId);
  }, [awaitingLocalResult, pedidoLocalPerdido]);

  const pasos = construirPasosEsperaRestoreTotal(pasoActual);

  // Fix de review (2e, plan tanda F): extraído a función pura (calcularTituloPantallaMantenimiento
  // en dangerRestoreLogic.js) para poder fijar con test node:test los DOS títulos posibles sin
  // montar React (este repo no tiene RTL/jsdom).
  const { esRestoreTotal, titulo } = calcularTituloPantallaMantenimiento({
    fechaResguardo,
    paso: pasoActual.paso,
  });

  return (
    <div
      data-testid="maintenance-screen"
      role="alert"
      aria-live="assertive"
      className="fixed inset-0 z-[9999] flex min-h-screen flex-col items-center justify-center gap-4 bg-slate-950 px-6 text-center text-white"
    >
      {!seAgotoElTiempoDeEspera && (
        <Loader2 className="h-12 w-12 animate-spin text-blue-400" aria-hidden="true" />
      )}
      <h1
        // tabIndex={-1}: puede recibir foco por código (headingRef.focus() en el useEffect
        // de arriba) sin entrar al tab order normal del teclado.
        tabIndex={-1}
        ref={headingRef}
        className="text-2xl font-black outline-none"
      >
        {titulo}
      </h1>

      {/* Fix de review (2d, plan tanda F): tope de espera agotado (~40 minutos sondeando sin
          que el sistema vuelva) — dejamos de girar y ofrecemos una salida explícita, en vez de
          dejar al usuario mirando el spinner para siempre. Texto en criollo, sin jerga técnica
          (T-2/T-5): no se nombra "polling", "timeout" ni ningún detalle de implementación. */}
      {seAgotoElTiempoDeEspera ? (
        <>
          <p className="max-w-sm text-sm text-slate-300" data-testid="maintenance-screen-tope-espera">
            Esto está tardando mucho más de lo normal. Puede que ya haya terminado — recargá la
            pantalla para comprobarlo.
          </p>
          <Button
            type="button"
            onClick={() => window.location.reload()}
            data-testid="maintenance-screen-recargar"
          >
            Recargar la pantalla
          </Button>
        </>
      ) : (
        <p className="text-sm font-semibold text-amber-300">No cierres esta ventana</p>
      )}

      {/* F7: mantenimiento por otro motivo (sin pasos de restore) — mostramos el texto que
          mandó el motor tal cual (P-13), sin el checklist específico de "Volver a esta copia"
          de más abajo (esos 3 pasos no aplican acá y confundirían más de lo que ayudan). */}
      {!esRestoreTotal && motivo && (
        <p className="max-w-sm text-sm text-slate-300" data-testid="maintenance-screen-motivo-generico">
          {motivo}
        </p>
      )}

      {/* Checklist de 3 pasos (rediseño 2026-07-30 §4.5, P-20 "sin prometer un tiempo"): el
          orden es el REAL del motor (datos → resguardo → actualización, ver dangerRestoreLogic.js).
          Si todavía no hay ningún paso informado (ej. el sondeo no llegó a tiempo), se muestra
          igual, sin ningún ítem marcado.
          Fix de review (accesibilidad, hallazgo bloqueante): el estado de cada paso NO puede
          depender solo del símbolo (✓/◐/○, marcado aria-hidden) ni del color — un lector de
          pantalla necesita el estado como texto. `aria-live="polite"` en la lista avisa cuando
          cambia el paso en curso, sin interrumpir como haría "assertive" en cada actualización.
          F7: solo tiene sentido cuando SÍ es una restauración total. */}
      {esRestoreTotal && (
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
      )}
    </div>
  );
}
