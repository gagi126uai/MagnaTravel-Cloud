/**
 * Panel EN LÍNEA para que un ADMINISTRADOR deshaga una Nota de Débito de multa del
 * operador que YA fue emitida y aprobada por ARCA (CAE vigente) — spec
 * `docs/ux/2026-07-14-deshacer-multa-emitida.md`.
 *
 * Cuándo se usa: el comprobante quedó MAL (monto o moneda equivocados, o directamente
 * no correspondía) y un comprobante aprobado no se edita — hay que emitir el
 * comprobante inverso. Esto es DISTINTO del "Deshacer" que ya existe para el cierre
 * "sin multa" (Waived, DeshacerCierreSinMultaInline): acá SÍ hay un comprobante fiscal
 * con CAE de por medio, así que la acción es más grande y el motivo es obligatorio.
 *
 * Se usa desde OperatorPenaltyStepPanel, en dos lugares:
 *   - familia "confirmada" (Done): el link "· Deshacer: el operador cobró mal esta
 *     multa" abre este panel de cero.
 *   - familia "accionTrabada" con estado "DebitNoteAnnulmentFailed" (el intento
 *     anterior de deshacer no se pudo emitir): el botón "Reintentar" reabre este MISMO
 *     panel, siempre vacío — nunca se reenvía en silencio el motivo del intento
 *     anterior. Es una decisión deliberada: esto reversa un comprobante fiscal con CAE,
 *     así que cada intento pide su propia confirmación explícita con motivo, aunque
 *     sea el mismo texto de la vez anterior.
 *
 * VISIBILIDAD: el padre (OperatorPenaltyStepPanel) solo monta este componente cuando
 * `debeMostrarReintentarDeshacer({ canUndoDebitNote, esAdmin })` es true (Admin
 * ÚNICAMENTE + condición de negocio del backend en `situacion.canUndoDebitNote`,
 * FIX BLOQUEANTE B1 2026-07-14: antes se gateaba solo con `canUndoDebitNote`, sin
 * exigir Admin) — la lógica de visibilidad NO está acá.
 *
 * Confirmación en DOS PASOS (mismo patrón que DeshacerCierreSinMultaInline): paso 1
 * explica + pide el motivo, paso 2 confirma explícitamente antes de tocar el backend.
 * Si el pedido falla, el panel queda abierto con el motivo intacto + cartel rojo de
 * error — nunca se pierde lo que el admin ya escribió.
 */

import { useEffect, useRef, useState } from "react";
import { RotateCcw, Loader2, AlertTriangle, X } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency } from "../../../lib/utils";
import {
  validarMotivoDeshacerMulta,
  puedeEnviarDeshacerMulta,
  construirPayloadUndoDebitNote,
  debeMostrarMontoAFavor,
} from "../lib/undoDebitNoteLogic";
import { calcularAvisoPlazoDeshacerMulta } from "../operatorPenaltyBanner";

const MOTIVO_MAX = 500;

/**
 * Props:
 *   - reservaPublicId: GUID de la reserva (para buscar la cancelación vigente recién al
 *     abrir el panel — el GUID de la cancelación no viaja en el DTO de la reserva).
 *   - reservaNumero: número de negocio, para el header y los textos de confirmación.
 *   - situacion: la situación de multa "confirmada" (Done) o "trabada" en el deshacer
 *     (DebitNoteAnnulmentFailed) — se usa para el monto/moneda de la explicación, la
 *     fecha de emisión del aviso de plazo RG 4540 (`debitNoteIssuedAt`) y la porción ya
 *     cobrada al cliente para la variante "ya pagó" (`collectedPenaltyAmount`).
 *   - puedeVerMontos: si el usuario tiene permiso `cobranzas.see_cost` — sin él, el
 *     monto se tapa con "—" en vez de desaparecer la frase (regla general 2026-06-05).
 *   - onDeshecho: callback tras deshacer con éxito — el padre refresca la reserva.
 *   - onCerrar: callback para cerrar el panel sin guardar.
 */
export function DeshacerMultaEmitidaInline({
  reservaPublicId,
  reservaNumero,
  situacion,
  puedeVerMontos,
  onDeshecho,
  onCerrar,
}) {
  const [cargando, setCargando] = useState(true);
  const [cancellationPublicId, setCancellationPublicId] = useState(null);
  const [errorCarga, setErrorCarga] = useState(null);

  const [motivo, setMotivo] = useState("");
  // El error de validación solo se muestra después de tocar el campo o intentar enviar.
  const [motivoTocado, setMotivoTocado] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [errorMensaje, setErrorMensaje] = useState(null);
  // Paso 2: confirmación explícita antes de llamar al backend (mismo patrón que
  // DeshacerCierreSinMultaInline — esto reversa un comprobante con CAE, no es gratis).
  const [mostrarConfirmacion, setMostrarConfirmacion] = useState(false);
  // Guard SINCRÓNICO anti doble-submit (fix menor de revisión, 2026-07-14): `submitting`
  // (arriba) es estado de React — se actualiza en el próximo render, no al instante. Si
  // el usuario logra disparar handleDeshacer dos veces en el MISMO tick (doble click muy
  // rápido, o Enter + click a la vez) antes de que React vuelva a pintar el botón
  // deshabilitado, las dos llamadas podrían pasar el chequeo de `submitting` y emitir DOS
  // comprobantes que anulan la misma ND. Un `ref` SÍ se lee/escribe al instante (no
  // espera un render), así que corta la segunda llamada en el mismo tick — capa
  // adicional sobre `submitting`, no un reemplazo.
  const enVueloRef = useRef(false);

  // Al montar, buscamos la cancelación vigente de esta reserva — mismo patrón que
  // ElegirFacturaDestinoInline / AgregarOtroCargoOperadorInline (el GUID de la
  // cancelación no viaja en el DTO de la reserva).
  useEffect(() => {
    let cancelado = false;
    (async () => {
      setCargando(true);
      setErrorCarga(null);
      try {
        const cancelacion = await cancellationsApi.getByReserva(reservaPublicId);
        if (cancelado) return;
        if (!cancelacion?.publicId) {
          setErrorCarga("No se encontró la cancelación de esta reserva. Actualizá la página y volvé a intentar.");
          return;
        }
        setCancellationPublicId(cancelacion.publicId);
      } catch (error) {
        if (!cancelado) {
          setErrorCarga(getApiErrorMessage(error, "No se pudo cargar los datos de la cancelación. Intentá de nuevo."));
        }
      } finally {
        if (!cancelado) setCargando(false);
      }
    })();
    return () => {
      cancelado = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reservaPublicId]);

  const motivoError = validarMotivoDeshacerMulta(motivo);
  const canSubmit = puedeEnviarDeshacerMulta({ motivo, submitting }) && Boolean(cancellationPublicId);

  // Aviso de plazo RG 4540 (spec sección 5): suave si todavía quedan días, más fuerte
  // si ya pasaron los 15 — NUNCA bloquea el botón. `Date.now()` se calcula una sola vez
  // acá (no en cada render) para que el número de días no "salte" mientras el admin
  // escribe el motivo.
  const [avisoPlazo] = useState(() =>
    calcularAvisoPlazoDeshacerMulta(situacion?.debitNoteIssuedAt, Date.now())
  );

  // Primer click en "Deshacer": valida el motivo y, si está OK, pide la confirmación
  // explícita en vez de llamar al backend directamente.
  const handlePedirConfirmacion = () => {
    setMotivoTocado(true);
    if (!canSubmit) return;
    setMostrarConfirmacion(true);
  };

  // Segundo click, ya en la confirmación ("Sí, deshacer"): ahí sí se llama al backend.
  const handleDeshacer = async () => {
    if (!canSubmit) return;
    // Guard sincrónico (ver comentario de enVueloRef más arriba): corta una segunda
    // llamada disparada en el mismo tick, antes de que `submitting` (estado async)
    // llegue a deshabilitar el botón.
    if (enVueloRef.current) return;
    enVueloRef.current = true;

    setSubmitting(true);
    setErrorMensaje(null);

    try {
      await cancellationsApi.undoDebitNote(cancellationPublicId, construirPayloadUndoDebitNote(motivo).reason);
      showSuccess("Listo. El comprobante de la multa quedó sin efecto.", "Multa deshecha");
      onDeshecho();
    } catch (error) {
      setErrorMensaje(getApiErrorMessage(error, "No se pudo deshacer la multa. Intentá de nuevo."));
      setMostrarConfirmacion(false);
      setSubmitting(false);
      // Libera el guard recién acá: el error es recuperable, el admin puede reintentar
      // con el mismo motivo intacto (no queda trabado el botón para siempre).
      enVueloRef.current = false;
    }
  };

  // Explicación del paso 1 — spec sección 2, tres variantes mutuamente excluyentes
  // (se evalúan en este orden: sin permiso siempre gana; "ya pagó" gana sobre el
  // estándar cuando corresponde):
  //   1) sin permiso de ver montos: la frase NUNCA desaparece, solo se tapa el número.
  //   2) "el cliente ya te había pagado esta multa": el backend manda
  //      `collectedPenaltyAmount` (2026-07-14) — la porción YA cobrada, en la moneda de
  //      la ND. `debeMostrarMontoAFavor` filtra los casos "no corresponde mostrar nada"
  //      (0 = impaga, null = no calculable) del caso real (> 0).
  //   3) estándar: el caso de siempre.
  const montoTexto = puedeVerMontos && situacion?.amount != null && situacion?.currency
    ? formatCurrency(situacion.amount, situacion.currency)
    : null;
  const mostrarMontoAFavor = puedeVerMontos && debeMostrarMontoAFavor(situacion?.collectedPenaltyAmount);
  const montoAFavorTexto = mostrarMontoAFavor
    ? formatCurrency(situacion.collectedPenaltyAmount, situacion.currency)
    : null;

  return (
    <div
      className="rounded-xl border-2 border-slate-200 bg-slate-50/60 dark:border-slate-700/60 dark:bg-slate-900/20 p-5 space-y-4"
      data-testid="deshacer-multa-emitida-inline"
    >
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <RotateCcw className="w-4 h-4 text-slate-600 dark:text-slate-400" aria-hidden="true" />
          <h4 className="text-sm font-bold text-slate-900 dark:text-white">
            Deshacer el comprobante de la multa
          </h4>
          <span className="text-xs text-slate-500 dark:text-slate-400">Reserva #{reservaNumero}</span>
        </div>
        <button
          type="button"
          onClick={onCerrar}
          disabled={submitting}
          className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-40"
          aria-label="Cerrar sin guardar"
        >
          <X className="w-4 h-4" />
        </button>
      </div>

      {cargando ? (
        <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 py-2">
          <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
          Cargando…
        </div>
      ) : errorCarga ? (
        <div
          role="alert"
          className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200"
        >
          {errorCarga}
        </div>
      ) : mostrarConfirmacion ? (
        <>
          {/* ── Paso 2: confirmación explícita antes de tocar el backend ── */}
          <div
            className="rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-sm text-amber-900 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200"
            data-testid="deshacer-multa-confirmacion-explicita"
            role="alert"
          >
            Esto deja sin efecto el comprobante de la multa de la reserva {reservaNumero}. Se va a
            emitir uno nuevo que lo anula; después vas a poder corregirla y volver a cobrarla, o
            cerrarla sin multa.
          </div>

          <div className="flex justify-end gap-3 pt-1">
            <button
              type="button"
              onClick={() => setMostrarConfirmacion(false)}
              disabled={submitting}
              data-testid="deshacer-multa-confirmacion-volver-btn"
              className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
            >
              Volver
            </button>
            <button
              type="button"
              onClick={handleDeshacer}
              disabled={submitting}
              data-testid="deshacer-multa-confirmar-btn"
              className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-bold text-white hover:bg-slate-800 transition-colors disabled:opacity-50 flex items-center gap-2 dark:bg-slate-600 dark:hover:bg-slate-500"
            >
              {submitting && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
              {submitting ? "Deshaciendo..." : "Sí, deshacer"}
            </button>
          </div>
        </>
      ) : (
        <>
          {/* ── Paso 1: explicación + aviso de plazo + motivo ── */}
          <div
            className="rounded-lg border border-slate-200 bg-white p-3.5 text-xs text-slate-700 dark:bg-slate-800 dark:border-slate-700 dark:text-slate-300"
            data-testid="deshacer-multa-explicacion"
          >
            {!puedeVerMontos ? (
              <p>
                Se emite el comprobante que deja sin efecto la multa actual. El monto queda oculto
                — no tenés permiso para ver costos.
              </p>
            ) : mostrarMontoAFavor ? (
              <p data-testid="deshacer-multa-explicacion-ya-pago">
                Se emite el comprobante que deja sin efecto la multa actual. Como ya te había
                pagado, le va a quedar <strong>{montoAFavorTexto}</strong> a favor para usar en
                otra reserva.
              </p>
            ) : (
              <>
                <p>
                  Se deja sin efecto el comprobante completo, con todos los cargos que salieron en
                  él.{" "}
                  {montoTexto
                    ? <>La deuda de <strong>{montoTexto}</strong> desaparece sola.</>
                    : "La deuda desaparece sola."}
                </p>
                <p className="mt-2">
                  Después podés corregir cada cargo (monto o moneda) y volver a cobrarla, o cerrar
                  el paso sin multa.
                </p>
              </>
            )}
          </div>

          {/* Aviso del plazo RG 4540 (spec sección 5) — nunca bloquea el botón de abajo,
              solo cambia de tono. Solo aparece si el backend mandó debitNoteIssuedAt; si
              no, no ocupa lugar (calcularAvisoPlazoDeshacerMulta devuelve null).
              Accesibilidad (fix de revisión, 2026-07-14): el tono "fuerte" (ya pasó el
              plazo) usa role="alert" — se anuncia de inmediato a lectores de pantalla,
              porque implica un riesgo fiscal mayor (conviene consultar a un contador
              antes de seguir). El tono "suave" (todavía quedan días) sigue en
              role="status" — informativo, sin urgencia. */}
          {avisoPlazo && (
            <div
              className={
                avisoPlazo.tono === "fuerte"
                  ? "rounded-lg border border-rose-300 bg-rose-50 p-3 text-xs text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200"
                  : "rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-800 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200"
              }
              data-testid="deshacer-multa-aviso-plazo"
              role={avisoPlazo.tono === "fuerte" ? "alert" : "status"}
            >
              ⚠ {avisoPlazo.texto}
            </div>
          )}

          {/* Banner de error de API — datos intactos, el admin puede reintentar. */}
          {errorMensaje && (
            <div
              role="alert"
              className="rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:bg-rose-950/30 dark:border-rose-800 dark:text-rose-200 flex items-start gap-2"
              data-testid="deshacer-multa-error"
            >
              <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
              <span>{errorMensaje}</span>
            </div>
          )}

          <div>
            <label
              className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
              htmlFor="deshacer-multa-motivo"
            >
              ¿Por qué la deshacés?{" "}
              <span className="text-rose-500" aria-hidden="true">*</span>
            </label>
            <textarea
              id="deshacer-multa-motivo"
              rows={3}
              value={motivo}
              onChange={(e) => setMotivo(e.target.value)}
              onBlur={() => setMotivoTocado(true)}
              maxLength={MOTIVO_MAX}
              disabled={submitting}
              placeholder="El operador cobró la multa en pesos, no en dólares..."
              data-testid="deshacer-multa-motivo-input"
              className={`w-full rounded-xl border px-3 py-2.5 text-sm focus:outline-none focus:ring-2 focus:ring-slate-400 dark:bg-slate-800 dark:text-white disabled:opacity-50 resize-none ${
                motivoTocado && motivoError ? "border-rose-400" : "border-slate-300 dark:border-slate-600"
              }`}
            />
            {motivoTocado && motivoError && (
              <div className="mt-1 text-xs text-rose-600" role="alert" data-testid="deshacer-multa-motivo-error">
                {motivoError}
              </div>
            )}
            <div className="mt-1 text-xs text-slate-400">
              {motivo.length}/{MOTIVO_MAX} caracteres
            </div>
          </div>

          <div className="flex justify-end gap-3 pt-1">
            <button
              type="button"
              onClick={onCerrar}
              disabled={submitting}
              className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
            >
              Volver
            </button>
            <button
              type="button"
              onClick={handlePedirConfirmacion}
              disabled={!canSubmit}
              data-testid="deshacer-multa-siguiente-btn"
              className="rounded-lg bg-slate-700 px-4 py-2 text-sm font-bold text-white hover:bg-slate-800 transition-colors disabled:opacity-50 flex items-center gap-2 dark:bg-slate-600 dark:hover:bg-slate-500"
            >
              Deshacer
            </button>
          </div>
        </>
      )}
    </div>
  );
}
