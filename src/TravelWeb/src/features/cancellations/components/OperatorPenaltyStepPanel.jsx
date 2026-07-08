/**
 * Cartel de la ficha para los pasos "trabados" o "en curso" de la multa del operador
 * (spec "el paso de multa vive en la ficha", 2026-07-08). Traduce
 * `reserva.operatorPenaltySituation` a UN cartel con, como máximo, UNA acción puntual —
 * nunca reenvía a otra pantalla ("bandeja") a resolver algo.
 *
 * Cubre dos de las cinco familias de operatorPenaltyBanner.js:
 *   - "accionTrabada" (DebitNoteFailed / DebitNoteNeedsAmountCurrency / ConfirmedNoDebitNote):
 *     cartel naranja con un botón puntual (Reintentar / Corregir monto y moneda / Emitir
 *     la nota ahora), gateado por los permisos que ya vienen resueltos del backend
 *     (canRetryDebitNote / canCorrectAmountCurrency).
 *   - "procesando" (DebitNoteQueued): cartel informativo ámbar, sin botón — se está
 *     emitiendo, no hay nada para hacer todavía.
 *
 * Las familias "pregunta" (PendingDecision) y "waived" (Waived) NO se dibujan acá: ya
 * tienen su propio bloque en ReservaDetailPage (los botones "Sí cobró/No cobró" y el
 * rastro + "Deshacer" respectivamente) — este componente no las duplica.
 *
 * Reemplaza al viejo cartel "Ir a resolver" que mandaba a la bandeja back-office
 * (/pendientes-afip?tab=multas): ahora se resuelve DIRECTO acá, sin navegar a otra pantalla.
 *
 * Hallazgo de Gastón (2026-07-08): el cartel de "accionTrabada" solo ofrecía resolver el
 * COBRO (Reintentar/Corregir/Emitir) — no había salida para "en realidad el operador no
 * cobró nada" (dato de prueba, confirmación cargada por error). Se agregó un link
 * secundario y discreto DEBAJO del botón principal, gateado por `situacion.canWaive`
 * (el backend ya resuelve ahí la regla de negocio + el permiso del usuario).
 */

import { useState } from "react";
import { Loader2 } from "lucide-react";
import { familiaDeEstadoMulta, copyAccionTrabada, slugDeEstadoMulta, debeMostrarWaiveEnAccionTrabada } from "../operatorPenaltyBanner";
import { ConfirmarMultaOperadorInline } from "./ConfirmarMultaOperadorInline";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";

// Motivo fijo para el waive disparado desde el link "no cobró esta multa" del paso
// trabado: a diferencia del flujo normal "No cobró" (CerrarSinMultaInline), acá NO le
// pedimos un motivo al usuario — el contexto ya lo explica (venía de un intento de cobro
// que quedó trabado). Mismo contrato del backend que CerrarSinMultaInline (reason string).
const MOTIVO_WAIVE_DESDE_TRABADO = "Cerrada sin multa desde el paso trabado de la ficha.";

/**
 * Props:
 *   - reservaPublicId: GUID de la reserva (para buscar la cancelación vigente recién al
 *     apretar el botón — el GUID de la cancelación no viaja en el DTO de la reserva).
 *   - reservaNumero: número de negocio, para el header del panel de corrección.
 *   - situacion: reserva.operatorPenaltySituation (obligatorio no-nulo; el padre decide
 *     cuándo montar este componente según familiaDeEstadoMulta(situacion.state)).
 *     Incluye `canWaive` (bool): true solo si la multa está Confirmed, la ND no está en
 *     juego y el usuario tiene permiso — habilita el link "El operador no cobró esta multa".
 *   - monedaSugerida: moneda con la que arranca el selector del modo "corregir", si hace falta.
 *   - onResuelto: callback tras una acción exitosa (el padre refresca la reserva).
 */
export function OperatorPenaltyStepPanel({ reservaPublicId, reservaNumero, situacion, monedaSugerida, onResuelto }) {
  const [buscando, setBuscando] = useState(false);
  const [showCorregir, setShowCorregir] = useState(false);
  const [cancellationPublicId, setCancellationPublicId] = useState(null);
  // Link secundario "no cobró esta multa": pide confirmación explícita en línea antes de
  // llamar al backend (mismo patrón que DeshacerCierreSinMultaInline) porque cierra el
  // paso sin cobrarle nada al cliente — no es una acción trivial de deshacer.
  const [mostrarConfirmacionWaive, setMostrarConfirmacionWaive] = useState(false);
  const [procesandoWaive, setProcesandoWaive] = useState(false);

  const familia = familiaDeEstadoMulta(situacion.state);
  const testId = `banner-multa-${slugDeEstadoMulta(situacion.state)}`;

  if (familia === "procesando") {
    return (
      <div
        className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-200"
        data-testid={testId}
        role="status"
      >
        <strong className="font-bold">Anulada — se está emitiendo la multa al cliente.</strong> Puede demorar unos minutos, no hace falta que hagas nada.
      </div>
    );
  }

  if (familia !== "accionTrabada") return null;

  const { mensaje, accion, textoBoton } = copyAccionTrabada({
    state: situacion.state,
    canRetryDebitNote: situacion.canRetryDebitNote,
    canCorrectAmountCurrency: situacion.canCorrectAmountCurrency,
  });

  // Modo "corregir" abierto: el panel inline reemplaza al cartel, mismo patrón que el resto
  // de los paneles de multa de la ficha (ConfirmarMultaOperadorInline / CerrarSinMultaInline).
  if (showCorregir && cancellationPublicId) {
    return (
      <ConfirmarMultaOperadorInline
        cancellationPublicId={cancellationPublicId}
        reservaNumero={reservaNumero}
        modo="corregir"
        monedaSugerida={situacion.currency ?? monedaSugerida}
        // El monto que ya estaba cargado (y quedó trabado) se precarga para corregir,
        // no para tipear de cero.
        montoInicial={situacion.amount ?? undefined}
        onConfirmado={() => {
          setShowCorregir(false);
          onResuelto();
        }}
        onCerrar={() => setShowCorregir(false)}
      />
    );
  }

  // El GUID de la cancelación no viaja en el DTO de la reserva: se busca recién al primer
  // click (mismo patrón que buscarCancelacionYAbrirPanel en ReservaDetailPage). Extraído
  // como función propia porque tanto el botón principal (Reintentar/Corregir/Emitir) como
  // el link secundario de waive necesitan este mismo GUID.
  const buscarCancellationPublicId = async () => {
    if (cancellationPublicId) return cancellationPublicId;
    const cancelacion = await cancellationsApi.getByReserva(reservaPublicId);
    if (!cancelacion?.publicId) {
      showError(
        "No se encontró la cancelación de esta reserva. Actualizá la página y volvé a intentar.",
        "Sin cancelación"
      );
      return null;
    }
    setCancellationPublicId(cancelacion.publicId);
    return cancelacion.publicId;
  };

  const handleClickAccion = async () => {
    setBuscando(true);
    try {
      const publicId = await buscarCancellationPublicId();
      if (!publicId) return;

      if (accion === "corregir") {
        setShowCorregir(true);
        return;
      }

      // "reintentar" (DebitNoteFailed) y "emitir" (ConfirmedNoDebitNote) usan el MISMO
      // endpoint: el backend decide qué corresponde según el estado real de la multa.
      await cancellationsApi.retryDebitNote(publicId);
      showSuccess("Listo. Se está reintentando el cargo al cliente.", "Reintentando");
      onResuelto();
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo reintentar el cargo al cliente. Probá de nuevo."));
    } finally {
      setBuscando(false);
    }
  };

  // Segundo click, ya en la confirmación inline ("Confirmar"): recién ahí se llama al
  // backend. Usa el mismo endpoint que el flujo normal "No cobró" (CerrarSinMultaInline),
  // pero con un motivo fijo — ver MOTIVO_WAIVE_DESDE_TRABADO más arriba.
  const handleConfirmarWaive = async () => {
    setProcesandoWaive(true);
    try {
      const publicId = await buscarCancellationPublicId();
      if (!publicId) return;

      await cancellationsApi.waivePenalty(publicId, MOTIVO_WAIVE_DESDE_TRABADO);
      showSuccess("Listo. La multa quedó cerrada sin cobro al cliente.", "Multa cerrada");
      setMostrarConfirmacionWaive(false);
      onResuelto();
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo cerrar la multa sin cobro. Probá de nuevo."));
    } finally {
      setProcesandoWaive(false);
    }
  };

  return (
    <div
      className="rounded-xl border border-orange-300 bg-orange-50 p-4 text-sm text-orange-900 dark:border-orange-700/50 dark:bg-orange-950/30 dark:text-orange-200"
      data-testid={testId}
      role="status"
    >
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <span>
          <strong className="font-bold">{mensaje}</strong>
        </span>
        {/* Sin permiso, copyAccionTrabada devuelve accion=null: cartel informativo, sin botón. */}
        {accion && (
          <button
            type="button"
            onClick={handleClickAccion}
            // También se bloquea mientras el waive está en vuelo: las dos acciones mutan la
            // misma cancelación y no deben poder dispararse a la vez (el backend igual
            // rebotaría la segunda, pero mejor no ofrecerla).
            disabled={buscando || procesandoWaive}
            data-testid={`btn-multa-${accion}`}
            className="inline-flex items-center gap-1.5 rounded-lg border border-orange-400 bg-orange-100 px-3 py-2 text-xs font-bold text-orange-800 hover:bg-orange-200 dark:border-orange-700 dark:bg-orange-900/40 dark:text-orange-200 dark:hover:bg-orange-900/60 transition-colors flex-shrink-0 disabled:opacity-50"
          >
            {buscando && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
            {textoBoton}
          </button>
        )}
      </div>

      {/* Link secundario y discreto — solo si el backend habilita canWaive (multa
          Confirmed + ND no en juego + permiso del usuario). Vive DEBAJO del botón
          principal, mismo patrón visual que el enlace "Deshacer" del cartel Waived en
          ReservaDetailPage: texto chico, sin fondo, separado por un borde superior. */}
      {debeMostrarWaiveEnAccionTrabada(situacion) && !mostrarConfirmacionWaive && !buscando && (
        <div className="mt-2 pt-2 border-t border-orange-200/60 dark:border-orange-800/40">
          <button
            type="button"
            onClick={() => setMostrarConfirmacionWaive(true)}
            data-testid="btn-multa-waive-link"
            className="text-xs text-orange-700/70 hover:text-orange-800 dark:text-orange-300/60 dark:hover:text-orange-200 transition-colors"
          >
            · El operador no cobró esta multa
          </button>
        </div>
      )}

      {/* Confirmación explícita en línea (patrón DeshacerCierreSinMultaInline, NO
          window.confirm): cerrar el paso sin cobro no es una acción trivial de deshacer
          "gratis", así que un solo click en el link de arriba no alcanza. */}
      {mostrarConfirmacionWaive && (
        <div
          className="mt-3 rounded-lg border border-orange-300 bg-orange-100/60 p-3.5 text-xs text-orange-900 dark:border-orange-700/50 dark:bg-orange-950/30 dark:text-orange-200 space-y-2.5"
          data-testid="multa-waive-confirmacion"
          role="alert"
        >
          <p>
            Se cierra el paso de la multa sin cobrarle nada al cliente. Vas a poder deshacerlo después.
          </p>
          <div className="flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setMostrarConfirmacionWaive(false)}
              disabled={procesandoWaive}
              data-testid="multa-waive-cancelar-btn"
              className="rounded-lg border border-orange-300 bg-white px-3 py-1.5 text-xs font-medium text-orange-800 hover:bg-orange-50 dark:bg-slate-800 dark:text-orange-200 dark:border-orange-700 transition-colors disabled:opacity-50"
            >
              Cancelar
            </button>
            <button
              type="button"
              onClick={handleConfirmarWaive}
              disabled={procesandoWaive}
              data-testid="multa-waive-confirmar-btn"
              className="inline-flex items-center gap-1.5 rounded-lg bg-orange-700 px-3 py-1.5 text-xs font-bold text-white hover:bg-orange-800 transition-colors disabled:opacity-50"
            >
              {procesandoWaive && <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />}
              Confirmar
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
