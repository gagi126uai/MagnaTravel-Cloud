/**
 * Cartel de la ficha para los pasos "trabados" o "en curso" de la multa del operador
 * (spec "el paso de multa vive en la ficha", 2026-07-08). Traduce UNA situación de multa
 * (un elemento de `reserva.operatorPenaltySituations`, o el singular legado en el caso
 * mono-operador) a UN cartel con, como máximo, UNA acción puntual — nunca reenvía a otra
 * pantalla ("bandeja") a resolver algo.
 *
 * Cubre cuatro de las siete familias de operatorPenaltyBanner.js:
 *   - "accionTrabada" (DebitNoteFailed / DebitNoteNeedsAmountCurrency / ConfirmedNoDebitNote):
 *     cartel naranja con un botón puntual (Reintentar / Corregir monto y moneda / Emitir
 *     la nota ahora), gateado por los permisos que ya vienen resueltos del backend
 *     (canRetryDebitNote / canCorrectAmountCurrency).
 *   - "procesando" (DebitNoteQueued): cartel informativo ámbar, sin botón — se está
 *     emitiendo, no hay nada para hacer todavía. Se refresca SOLO cada ~10 s mientras
 *     dure esta familia (ver useOperatorPenaltyPolling) para no depender de un F5 manual.
 *   - "multiOperador" (MultiOperatorNeedsManualReview, ADR-044 T1, 2026-07-10): cartel
 *     informativo ámbar, SIN botón de emisión — hay más de un operador con la multa
 *     confirmada a la vez y el cargo automático al cliente se frena hasta resolverse a
 *     mano (tanda futura T3). Conserva el mismo link de waive que "accionTrabada".
 *   - "confirmada" (Done, ADR-044 T4, 2026-07-10): la multa quedó resuelta de punta a
 *     punta (ND emitida con CAE). Antes este estado no dibujaba NADA acá — la ficha
 *     mostraba solo el cartel genérico "Reserva anulada". Ahora muestra el monto
 *     confirmado (enmascarado sin permiso `cobranzas.see_cost`) y habilita el link
 *     "+ Agregar otro cargo de este operador" para el caso real confirmado por el
 *     contador (cargo administrativo + retención fiscal juntos), Y desde ADR-044
 *     "Deshacer una multa ya emitida" (2026-07-14) el link "· Deshacer: el operador
 *     cobró mal esta multa" (Admin ÚNICAMENTE — gateado por `debeMostrarReintentarDeshacer`,
 *     la MISMA función que gatea el botón "Reintentar" de la familia "accionTrabada",
 *     FIX BLOQUEANTE B1: nunca alcanza con `situacion.canUndoDebitNote` solo) que abre
 *     DeshacerMultaEmitidaInline — deja sin efecto el comprobante ENTERO (nunca un cargo
 *     suelto: regla fiscal dura, ver el archivo de ese componente).
 *
 * Las familias "pregunta" (PendingDecision) y "waived" (Waived) NO se dibujan acá: ya
 * tienen su propio bloque en ReservaDetailPage (los botones "Sí cobró/No cobró" y el
 * rastro + "Deshacer" respectivamente) — este componente no las duplica.
 *
 * ADR-044 "Deshacer una multa ya emitida" (2026-07-14): dos estados nuevos, mapeados en
 * operatorPenaltyBanner.js a familias YA existentes (nunca se creó una familia visual
 * nueva) — "DebitNoteAnnulling" entra en "procesando" (mismo cartel ámbar con
 * auto-refresco, solo cambia el título) y "DebitNoteAnnulmentFailed" entra en
 * "accionTrabada" (mismo cartel naranja, botón "Reintentar" que reabre
 * DeshacerMultaEmitidaInline desde cero).
 *
 * "+ Agregar otro cargo de este operador" (ADR-044 T4): además de la familia
 * "confirmada", el link también aparece en "accionTrabada"/"procesando"/"multiOperador"
 * — la multa PRINCIPAL ya está confirmada en todas esas familias (lo que está trabado es
 * la Nota de Débito, no la confirmación), así que agregar un segundo cargo independiente
 * (ej. una retención fiscal) es válido igual. Gateado por el permiso
 * `cancellations.classify_agency_penalty` (mismo permiso que confirmar la multa) —
 * chequeo de UI, el backend revalida server-side.
 *
 * Reemplaza al viejo cartel "Ir a resolver" que mandaba a la bandeja back-office
 * (/pendientes-afip?tab=multas): ahora se resuelve DIRECTO acá, sin navegar a otra pantalla.
 *
 * Hallazgo de Gastón (2026-07-08): el cartel de "accionTrabada" solo ofrecía resolver el
 * COBRO (Reintentar/Corregir/Emitir) — no había salida para "en realidad el operador no
 * cobró nada" (dato de prueba, confirmación cargada por error). Se agregó un link
 * secundario y discreto DEBAJO del botón principal, gateado por `situacion.canWaive`
 * (el backend ya resuelve ahí la regla de negocio + el permiso del usuario).
 *
 * ADR-044 T1 (2026-07-10): una cancelación puede tener servicios de más de un operador
 * (ADR-025), cada uno con su propia multa — ReservaDetailPage ahora monta UN
 * OperatorPenaltyStepPanel POR OPERADOR. Este componente sigue recibiendo UNA sola
 * `situacion` (no cambia su forma de trabajar); lo nuevo son dos props opcionales:
 *   - `nombreOperador`: si viene, se antepone al mensaje del cartel ("Turismo Cardozo —
 *     Anulada — ..."). En el caso mono-operador de siempre, el padre no la pasa (queda
 *     null) y el cartel se ve EXACTO igual que antes de este cambio.
 *   - `supplierPublicId`: identificador del operador de ESTA situación puntual. Solo se
 *     usa en el link de waive de este panel — el backend lo acepta opcional en
 *     waive-penalty para saber a CUÁL de los operadores de la cancelación corresponde el
 *     cierre sin multa cuando hay más de uno en juego.
 */

import { useState } from "react";
import { Loader2 } from "lucide-react";
import {
  familiaDeEstadoMulta,
  copyAccionTrabada,
  slugDeEstadoMulta,
  debeMostrarWaiveEnAccionTrabada,
  textoMultiOperador,
  tituloConNombreOperador,
  primerCargoTrasladableSinFacturaDestino,
  tituloProcesandoMulta,
  textoRastroDeshacerMulta,
} from "../operatorPenaltyBanner";
import { useOperatorPenaltyPolling } from "../hooks/useOperatorPenaltyPolling";
import { ConfirmarMultaOperadorInline } from "./ConfirmarMultaOperadorInline";
import { AgregarOtroCargoOperadorInline } from "./AgregarOtroCargoOperadorInline";
import { ElegirFacturaDestinoInline } from "./ElegirFacturaDestinoInline";
import { DeshacerMultaEmitidaInline } from "./DeshacerMultaEmitidaInline";
import { debeMostrarDesgloseCargos, construirFilasDesgloseCargos } from "../lib/otroCargoOperador";
import { debeMostrarReintentarDeshacer } from "../lib/undoDebitNoteLogic";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { hasPermission, isAdmin } from "../../../auth";
import { formatCurrency } from "../../../lib/utils";
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
 *   - situacion: UN elemento de reserva.operatorPenaltySituations (o el singular legado
 *     en el caso mono-operador) — obligatorio no-nulo; el padre decide cuándo montar
 *     este componente según familiaDeEstadoMulta(situacion.state).
 *     Incluye `canWaive` (bool): true solo si la multa está Confirmed, la ND no está en
 *     juego y el usuario tiene permiso — habilita el link "El operador no cobró esta multa".
 *   - monedaSugerida: moneda con la que arranca el selector del modo "corregir", si hace falta.
 *   - nombreOperador (ADR-044 T1, opcional): nombre del operador de ESTA situación. Solo
 *     lo pasa el padre cuando hay MÁS de un operador en juego — en el caso mono-operador
 *     de siempre queda undefined y el cartel se ve EXACTO igual que antes.
 *   - supplierPublicId (ADR-044 T1, opcional): identificador del operador de ESTA
 *     situación puntual. Se manda en el link de waive de este panel para que el backend
 *     sepa a cuál de los operadores de la cancelación corresponde el cierre sin multa.
 *   - onResuelto: callback de refresco SILENCIOSO (sin toast propio) que el padre ya usa
 *     tras una acción exitosa Y que este panel reutiliza para el auto-refresco de la
 *     familia "procesando" (ver useOperatorPenaltyPolling más abajo).
 */
export function OperatorPenaltyStepPanel({
  reservaPublicId,
  reservaNumero,
  situacion,
  monedaSugerida,
  nombreOperador,
  supplierPublicId,
  onResuelto,
}) {
  const [buscando, setBuscando] = useState(false);
  const [showCorregir, setShowCorregir] = useState(false);
  // ADR-044 T4 (2026-07-10): "Elegir la factura" — corrección aparte de "corregir monto
  // y moneda", para cuando lo trabado es la falta de factura destino (ver
  // hayCargoTrasladableSinFacturaDestino en operatorPenaltyBanner.js).
  const [showElegirFactura, setShowElegirFactura] = useState(false);
  const [cancellationPublicId, setCancellationPublicId] = useState(null);
  // Link secundario "no cobró esta multa": pide confirmación explícita en línea antes de
  // llamar al backend (mismo patrón que DeshacerCierreSinMultaInline) porque cierra el
  // paso sin cobrarle nada al cliente — no es una acción trivial de deshacer.
  const [mostrarConfirmacionWaive, setMostrarConfirmacionWaive] = useState(false);
  const [procesandoWaive, setProcesandoWaive] = useState(false);
  // ADR-044 "Deshacer una multa ya emitida" (2026-07-14): panel de "Deshacer" abierto —
  // se usa TANTO desde el link de la familia "confirmada" (Done) COMO desde el botón
  // "Reintentar" de la familia "accionTrabada" cuando el estado es
  // "DebitNoteAnnulmentFailed" (el intento anterior de deshacer no se pudo emitir).
  const [mostrarDeshacerMulta, setMostrarDeshacerMulta] = useState(false);

  const familia = familiaDeEstadoMulta(situacion.state);
  // ADR-044 T1: cuando hay más de un operador (nombreOperador viene informado), dos
  // paneles pueden compartir el MISMO estado a la vez (es justamente lo que pasa en
  // "multiOperador": todos los confirmados quedan en ese mismo estado). Sumamos el guid
  // del operador al testid para que cada panel siga siendo único en el DOM; en el caso
  // mono-operador (nombreOperador ausente) el testid queda IGUAL que antes de este cambio.
  const testId = nombreOperador && supplierPublicId
    ? `banner-multa-${slugDeEstadoMulta(situacion.state)}-${supplierPublicId}`
    : `banner-multa-${slugDeEstadoMulta(situacion.state)}`;

  // Bug reportado por Gastón (2026-07-08): este cartel quedaba TRABADO para siempre
  // aunque la ND ya se hubiera emitido del lado del backend — solo un F5 manual lo
  // destrababa. Mientras la familia sea "procesando" (y solo ahí: el hook mismo se
  // encarga de no pollear en las otras familias), refrescamos la situación
  // sola cada ~10 s hasta que cambie o se agote el tope de espera. `onResuelto` es acá
  // el mismo refresco SILENCIOSO que ya usa el resto del panel (fetchReserva con
  // showLoading:false — no dispara ningún toast de éxito, solo trae la reserva
  // actualizada): cuando la ND ya se emitió, la familia deja de ser "procesando" y
  // este cartel se reemplaza solo en el próximo render, sin que el agente haga nada.
  const seAgotoLaEsperaDelPolling = useOperatorPenaltyPolling(familia, onResuelto);

  // ADR-044 T4 (2026-07-10): gatea el link "+ Agregar otro cargo de este operador" —
  // mismo permiso que confirmar la multa (chequeo de UI; el backend revalida). Se
  // calcula una sola vez acá porque lo usan varias familias distintas más abajo.
  const puedeAgregarOtroCargo = hasPermission("cancellations.classify_agency_penalty");
  // FIX F4 (gate de frontend, 2026-07-10): mismo permiso que enmascara montos en toda
  // la ficha y el extracto del operador — se calcula una sola vez para el desglose de
  // cargos (familias "confirmada" y "accionTrabada"/"multiOperador").
  const puedeVerMontos = hasPermission("cobranzas.see_cost");

  if (familia === "procesando") {
    return (
      <div
        className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-200"
        data-testid={testId}
        role="status"
      >
        <strong className="font-bold">
          {tituloConNombreOperador(nombreOperador, tituloProcesandoMulta(situacion.state))}
        </strong> Puede demorar unos minutos, no hace falta que hagas nada.
        {/* Tope de ~3 min agotado sin que el backend resuelva: puede ser una cola realmente
            trabada, así que dejamos de insistir solos y le damos la salida manual de siempre. */}
        {seAgotoLaEsperaDelPolling && (
          <p
            className="mt-1.5 text-xs text-amber-700/80 dark:text-amber-300/70"
            data-testid="multa-polling-timeout-hint"
          >
            ¿Tarda mucho? Actualizá la página.
          </p>
        )}
        {/* La multa PRINCIPAL ya está confirmada (lo que está "procesando" es la ND):
            agregar un segundo cargo independiente (ej. retención fiscal) es válido igual. */}
        {puedeAgregarOtroCargo && (
          <div className="mt-2 pt-2 border-t border-amber-200/60 dark:border-amber-800/40">
            <AgregarOtroCargoOperadorInline
              reservaPublicId={reservaPublicId}
              reservaNumero={reservaNumero}
              supplierPublicId={supplierPublicId}
              monedaSugerida={situacion.currency ?? monedaSugerida}
              onAgregado={onResuelto}
            />
          </div>
        )}
      </div>
    );
  }

  // ADR-044 T4 (2026-07-10): "confirmada" (Done) — la multa quedó resuelta de punta a
  // punta. Cartel neutro/prolijo (sin naranja ni ámbar: no hay nada trabado ni pendiente)
  // con el monto confirmado y el link de "otro cargo". Antes de esta tanda la ficha no
  // mostraba NADA para este estado (ver comentario del archivo).
  if (familia === "confirmada") {
    // ADR-044 "Deshacer una multa ya emitida" (2026-07-14): mismo gate ÚNICO que usa el
    // botón "Reintentar" del cartel "accionTrabada" (ver copyAccionTrabada) — Admin
    // ÚNICAMENTE, Y el backend ya combinó la condición de negocio (ND con CAE vigente,
    // sin otra anulación en curso) en `situacion.canUndoDebitNote`. FIX B1: las dos
    // puertas de entrada NUNCA deben poder divergir, por eso comparten esta función.
    const puedeDeshacerMulta = debeMostrarReintentarDeshacer({
      canUndoDebitNote: situacion.canUndoDebitNote,
      esAdmin: isAdmin(),
    });
    // Rastro del último "Deshacer" de este operador (2026-07-14: backend ya manda
    // `situacion.lastDebitNoteUndo` — objeto con undoneAt/undoneByName/reason, o null si
    // esta ND nunca se deshizo). textoRastroDeshacerMulta degrada solo a null cuando
    // viene null (nunca se deshizo) — no hace falta chequear acá.
    const rastroDeshacer = textoRastroDeshacerMulta(situacion.lastDebitNoteUndo);

    return (
      // Envoltorio "space-y-3": mismo patrón que el cartel Waived + DeshacerCierreSinMultaInline
      // en ReservaDetailPage — el panel de confirmación va DEBAJO del cartel, como
      // hermano, nunca anidado adentro (spec sección 2: "se abre debajo del cartel").
      <div className="space-y-3">
        <div
          className="rounded-xl border border-emerald-200 bg-emerald-50/60 p-4 text-sm text-emerald-900 dark:border-emerald-900/40 dark:bg-emerald-950/10 dark:text-emerald-200"
          data-testid={testId}
          role="status"
        >
          <strong className="font-bold">
            {tituloConNombreOperador(nombreOperador, "Anulada — multa del operador confirmada.")}
          </strong>{" "}
          {puedeVerMontos && situacion.amount != null && situacion.currency ? (
            <span data-testid="multa-confirmada-monto">
              {formatCurrency(situacion.amount, situacion.currency)}
            </span>
          ) : !puedeVerMontos ? (
            <span title="Sin permiso para ver montos">—</span>
          ) : null}

          {/* FIX de exposición (revisión 2026-07-14): el motivo del "Deshacer" puede
              llegar a 500 caracteres — sin recorte, esta línea chica estiraba el cartel
              entero. `line-clamp-2` lo corta visualmente a 2 renglones con "…"; el
              `title` deja el texto COMPLETO disponible al pasar el mouse (nunca se
              pierde información, solo se recorta la vista). */}
          {rastroDeshacer && (
            <div
              className="mt-1 text-xs text-emerald-700/80 dark:text-emerald-300/70 line-clamp-2"
              data-testid="multa-deshacer-rastro"
              title={rastroDeshacer}
            >
              {rastroDeshacer}
            </div>
          )}

          {/* FIX F4 (2026-07-10, spec sección 1.2): con más de un cargo, se ve el
              desglose completo — antes, agregar un segundo cargo no cambiaba nada visible. */}
          <DesgloseCargosOperador charges={situacion.charges} puedeVerMontos={puedeVerMontos} />
          {puedeAgregarOtroCargo && !mostrarDeshacerMulta && (
            <div className="mt-2 pt-2 border-t border-emerald-200/60 dark:border-emerald-800/40">
              <AgregarOtroCargoOperadorInline
                reservaPublicId={reservaPublicId}
                reservaNumero={reservaNumero}
                supplierPublicId={supplierPublicId}
                monedaSugerida={situacion.currency ?? monedaSugerida}
                onAgregado={onResuelto}
              />
            </div>
          )}

          {/* "· Deshacer: el operador cobró mal esta multa" (spec sección 1): link
              discreto, UNO SOLO por comprobante (nunca uno por renglón del desglose —
              regla fiscal dura, la NC anula el 100% del comprobante). Segunda fila
              aparte, separada por su propia línea fina, debajo de "Agregar otro cargo". */}
          {puedeDeshacerMulta && !mostrarDeshacerMulta && (
            <div className="mt-2 pt-2 border-t border-emerald-200/60 dark:border-emerald-800/40">
              <button
                type="button"
                onClick={() => setMostrarDeshacerMulta(true)}
                data-testid="btn-multa-deshacer-link"
                className="text-xs text-emerald-700/70 hover:text-emerald-800 dark:text-emerald-300/60 dark:hover:text-emerald-200 transition-colors"
              >
                · Deshacer: el operador cobró mal esta multa
              </button>
            </div>
          )}
        </div>

        {mostrarDeshacerMulta && (
          <DeshacerMultaEmitidaInline
            reservaPublicId={reservaPublicId}
            reservaNumero={reservaNumero}
            situacion={situacion}
            puedeVerMontos={puedeVerMontos}
            onDeshecho={() => {
              setMostrarDeshacerMulta(false);
              onResuelto();
            }}
            onCerrar={() => setMostrarDeshacerMulta(false)}
          />
        )}
      </div>
    );
  }

  // ADR-044 T1: "multiOperador" es la única familia nueva (además de "confirmada") que
  // este panel dibuja fuera de "accionTrabada" — es un cartel PASIVO (sin botón de
  // emisión), así que `esAccionTrabada` distingue cuándo hay una acción de cobro real.
  if (familia !== "accionTrabada" && familia !== "multiOperador") return null;

  const esAccionTrabada = familia === "accionTrabada";

  const { mensaje, accion, textoBoton } = esAccionTrabada
    ? copyAccionTrabada({
        state: situacion.state,
        canRetryDebitNote: situacion.canRetryDebitNote,
        canCorrectAmountCurrency: situacion.canCorrectAmountCurrency,
        canUndoDebitNote: situacion.canUndoDebitNote,
        // FIX B1: el botón "Reintentar" de DebitNoteAnnulmentFailed exige Admin además
        // de canUndoDebitNote (ver debeMostrarReintentarDeshacer) — nunca alcanza con
        // el permiso de clasificar multas solo.
        esAdmin: isAdmin(),
        manualReviewReason: situacion.manualReviewReason,
        charges: situacion.charges,
      })
    // "multiOperador": nunca tiene botón de emisión (ver comentario de textoMultiOperador).
    : { mensaje: textoMultiOperador(), accion: null, textoBoton: null };

  // Modo "corregir" abierto: el panel inline reemplaza al cartel, mismo patrón que el resto
  // de los paneles de multa de la ficha (ConfirmarMultaOperadorInline / CerrarSinMultaInline).
  // Solo aplica a "accionTrabada" — "multiOperador" no ofrece ninguna acción de emisión.
  if (esAccionTrabada && showCorregir && cancellationPublicId) {
    return (
      <ConfirmarMultaOperadorInline
        cancellationPublicId={cancellationPublicId}
        reservaNumero={reservaNumero}
        modo="corregir"
        monedaSugerida={situacion.currency ?? monedaSugerida}
        // El monto que ya estaba cargado (y quedó trabado) se precarga para corregir,
        // no para tipear de cero.
        montoInicial={situacion.amount ?? undefined}
        // 2026-07-13 (spec F-2026-1033): moneda REAL de la factura del cliente — el
        // panel la usa para decidir si aparece el bloque de conversión (nunca compara
        // contra monedaSugerida, que es editable). Y la fecha que el backend sugiere
        // para el tipo de cambio, si la tiene.
        invoiceCurrency={situacion.invoiceCurrency}
        suggestedExchangeRateDate={situacion.suggestedExchangeRateDate}
        onConfirmado={() => {
          setShowCorregir(false);
          onResuelto();
        }}
        onCerrar={() => setShowCorregir(false)}
      />
    );
  }

  // "Elegir la factura" abierto (ADR-044 T4): reemplaza al cartel, mismo patrón que
  // "corregir". El cargo puntual a corregir es el primer trasladable sin factura
  // destino (mismo criterio que usó copyAccionTrabada para ofrecer este botón).
  if (esAccionTrabada && showElegirFactura) {
    const cargoACorregir = primerCargoTrasladableSinFacturaDestino(situacion.charges);
    return (
      <ElegirFacturaDestinoInline
        reservaPublicId={reservaPublicId}
        reservaNumero={reservaNumero}
        chargePublicId={cargoACorregir?.publicId}
        onResuelto={() => {
          setShowElegirFactura(false);
          onResuelto();
        }}
        onCerrar={() => setShowElegirFactura(false)}
      />
    );
  }

  // "Reintentar" el deshacer (ADR-044, estado "DebitNoteAnnulmentFailed"): reemplaza
  // al cartel, mismo patrón que "corregir"/"elegirFactura" — reabre el panel de
  // "Deshacer" desde cero (nunca reenvía en silencio el motivo del intento anterior,
  // ver comentario de DeshacerMultaEmitidaInline).
  if (esAccionTrabada && accion === "reintentarDeshacer" && mostrarDeshacerMulta) {
    return (
      <DeshacerMultaEmitidaInline
        reservaPublicId={reservaPublicId}
        reservaNumero={reservaNumero}
        situacion={situacion}
        puedeVerMontos={puedeVerMontos}
        onDeshecho={() => {
          setMostrarDeshacerMulta(false);
          onResuelto();
        }}
        onCerrar={() => setMostrarDeshacerMulta(false)}
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
    // "elegirFactura" no necesita buscar el cancellationPublicId acá — el propio
    // ElegirFacturaDestinoInline lo resuelve solo al abrirse (mismo motivo por el que
    // AgregarOtroCargoOperadorInline también hace su propio fetch).
    if (accion === "elegirFactura") {
      setShowElegirFactura(true);
      return;
    }

    // "reintentarDeshacer" (DebitNoteAnnulmentFailed): mismo criterio, el propio
    // DeshacerMultaEmitidaInline resuelve el cancellationPublicId solo al abrirse.
    if (accion === "reintentarDeshacer") {
      setMostrarDeshacerMulta(true);
      return;
    }

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
  //
  // ADR-044 T1: se manda `supplierPublicId` (si el padre lo pasó) para que el backend
  // sepa a CUÁL operador corresponde este cierre sin multa cuando hay más de uno en
  // juego. Ojo: esta prop SÍ viaja también en el caso mono-operador de siempre (el
  // backend trae `supplierPublicId` incluso en el DTO singular legado) — no hace daño
  // mandarla, porque el backend resuelve al mismo (único) operador que ya iba a resolver
  // sin el parámetro. Lo que SÍ queda undefined en mono-operador es `nombreOperador`
  // (esa prop el padre solo la pasa cuando hay más de un operador en juego).
  const handleConfirmarWaive = async () => {
    setProcesandoWaive(true);
    try {
      const publicId = await buscarCancellationPublicId();
      if (!publicId) return;

      await cancellationsApi.waivePenalty(publicId, MOTIVO_WAIVE_DESDE_TRABADO, supplierPublicId);
      showSuccess("Listo. La multa quedó cerrada sin cobro al cliente.", "Multa cerrada");
      setMostrarConfirmacionWaive(false);
      onResuelto();
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo cerrar la multa sin cobro. Probá de nuevo."));
    } finally {
      setProcesandoWaive(false);
    }
  };

  // "multiOperador" es un cartel pasivo/informativo (ámbar, como "procesando" — nada para
  // hacer salvo, opcionalmente, el link de waive). "accionTrabada" sigue siendo naranja,
  // con su botón puntual — sin cambios visuales respecto de antes de ADR-044.
  const clasesContenedor = esAccionTrabada
    ? "rounded-xl border border-orange-300 bg-orange-50 p-4 text-sm text-orange-900 dark:border-orange-700/50 dark:bg-orange-950/30 dark:text-orange-200"
    : "rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-200";

  return (
    <div
      className={clasesContenedor}
      data-testid={testId}
      role="status"
    >
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <span>
          <strong className="font-bold">{tituloConNombreOperador(nombreOperador, mensaje)}</strong>
        </span>
        {/* Sin permiso, copyAccionTrabada devuelve accion=null: cartel informativo, sin botón.
            "multiOperador" siempre tiene accion=null (ver arriba): nunca dibuja este botón. */}
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

      {/* FIX F4 (2026-07-10, spec sección 1.2): con más de un cargo, se ve el desglose
          completo — antes, agregar un segundo cargo no cambiaba nada visible acá. */}
      <DesgloseCargosOperador charges={situacion.charges} puedeVerMontos={puedeVerMontos} />

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

      {/* ADR-044 T4 (2026-07-10): la multa PRINCIPAL de este operador ya está confirmada
          en "accionTrabada"/"multiOperador" (lo trabado es la Nota de Débito, no la
          confirmación) — agregar un segundo cargo independiente sigue siendo válido. Se
          oculta mientras el link de waive está en su confirmación explícita para no
          amontonar dos acciones a la vez en el mismo cartel. */}
      {puedeAgregarOtroCargo && !mostrarConfirmacionWaive && (
        <div className="mt-2 pt-2 border-t border-orange-200/60 dark:border-orange-800/40">
          <AgregarOtroCargoOperadorInline
            reservaPublicId={reservaPublicId}
            reservaNumero={reservaNumero}
            supplierPublicId={supplierPublicId}
            monedaSugerida={situacion.currency ?? monedaSugerida}
            onAgregado={onResuelto}
          />
        </div>
      )}
    </div>
  );
}

/**
 * Desglose de los cargos de este operador, uno por fila (ADR-044 T4, fix F4, spec
 * sección 1.2). Con 1 solo cargo (el caso simple, 100% de las anulaciones de hoy) NO
 * renderiza nada — la línea resumen de siempre (monto + moneda totales) ya alcanza;
 * recién con 2+ cargos (el caso real confirmado por el contador: cargo administrativo
 * + retención fiscal juntos) hace falta ver cada uno por separado.
 *
 * Enmascarado de costos: sin permiso `cobranzas.see_cost`, cada monto se muestra como
 * "—" (nunca el monto real) — mismo criterio que el resto de la ficha.
 */
function DesgloseCargosOperador({ charges, puedeVerMontos }) {
  if (!debeMostrarDesgloseCargos(charges)) return null;

  const filas = construirFilasDesgloseCargos(charges, puedeVerMontos);

  return (
    <ul
      className="mt-2 pt-2 border-t border-current/10 space-y-1 text-xs"
      data-testid="desglose-cargos-operador"
    >
      {filas.map((fila) => (
        <li key={fila.key} className="flex items-center justify-between gap-3">
          <span>
            {fila.tipo} <span className="opacity-70">· {fila.comoLoCobra}</span>
          </span>
          <span className="font-semibold flex-shrink-0">
            {fila.montoOculto ? (
              <span title="Sin permiso para ver montos">—</span>
            ) : (
              formatCurrency(fila.amount, fila.currency)
            )}
          </span>
        </li>
      ))}
    </ul>
  );
}
