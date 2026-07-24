/**
 * Panel EN LÍNEA para anular una reserva.
 *
 * ADR-035 (2026-06-19): la accion deja de ser un modal flotante y pasa a un panel inline
 * igual que RegistrarCobroInline y EmitirFacturaInline.
 *
 * ADR-036 (2026-06-21): el panel pasa a llamarse "Anular reserva" (en vez de "Cancelar").
 * En este producto "Cancelar" = saldar una deuda; "Anular" = deshacer el viaje.
 * El estado interno del backend sigue siendo "Cancelled", pero el usuario ve "Anular/Anulada".
 *
 * guia-ux-gaston.md 2026-06-25 — 3 casos distintos según `cancellationCase` del DTO:
 *   - DirectCancel (VERDE):      sin factura, sin cobros → baja directa.
 *   - PaymentsToCredit (CELESTE): sin factura, CON cobros → la plata pasa a saldo a favor.
 *   - CreditNote (ÁMBAR):        con factura CAE vivo → anulación formal con Nota de Crédito.
 *
 * Obra "anular sin factura" (2026-07-23, decisión del dueño): el caso PaymentsToCredit
 * (CELESTE) suma una línea fija del contador, pegada abajo del aviso de saldo a favor, tanto
 * en el cartel de confirmar como en el mensaje de éxito — ver TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA
 * en cancelarReservaCopy.js. Es un aviso suave (P-20): no frena ni cambia los botones.
 *
 * ADR-042 (2026-07-01): dentro del caso CreditNote, cuando la reserva tiene 2+ facturas VIVAS,
 * el flujo cambia: hay un freno extra "¿Seguro?" antes de emitir nada, y las notas de crédito
 * (una por factura) se emiten de a una con un avance visible (PROCESANDO → ÉXITO / EN REVISIÓN).
 * Es "todo o nada a nivel ESTADO": si una nota sale y otra falla, la reserva queda "en revisión"
 * (la que salió no se deshace) hasta reintentar la que falta. Con 1 sola factura no cambia nada
 * (regresión cero). Spec completa: docs/ux/2026-07-01-anulacion-multifactura.md.
 *
 * Tanda 3 "contrato pantalla-motor" (2026-07-20, spec docs/ux/2026-07-20-t3-t4-contrato-pantalla-motor.md):
 * los tres puntos donde el panel puede recibir un 409 (annul-with-credit, draft() y confirm())
 * muestran el motivo REAL que calculó el motor (mapa código → criollo en
 * lib/anularReservaRechazoLogic.js), en vez de un cartel genérico siempre igual.
 *
 * Obra "anular sin factura" (2026-07-23, decisión del dueño): el freno de plata R1 a nivel
 * reserva (operador ya cobrado, reserva sin factura para anclar el reembolso) se ELIMINÓ del
 * backend — anular la reserva entera ya no lo bloquea, así que el botón "Emitir factura" que
 * ese candado ofrecía se retiró de este panel (mismo criterio que "anular servicio", ver
 * serviceCancellationGuard.js). Si el código llegara igual por alguna versión vieja cacheada,
 * el mensaje del motor se sigue mostrando tal cual, sin ningún botón (P-13).
 *
 * Props:
 *   - reserva: objeto de la reserva (necesita publicId, numeroReserva, customerName,
 *              cancellationCase, cancellationCreditByCurrency, requiresInvoiceAnnulmentToCancel).
 *   - onCancelado: callback luego de confirmar exitosamente (nombre legacy mantenido).
 *   - onCerrar: callback cuando el usuario cierra el panel sin anular.
 *   - onSilentRefresh: (ADR-042, opcional) callback para refrescar la reserva/extracto en
 *     segundo plano SIN cerrar el panel — se llama en cuanto el resultado de las notas de
 *     crédito queda resuelto (éxito o revisión), mientras el vendedor sigue viendo el cartel.
 *   - bookingCancellationToRetry: (ADR-042, opcional) BookingCancellationDto de una anulación
 *     multi-factura que quedó a medias (franja "en revisión" de la ficha de la reserva). Cuando
 *     viene seteado, el panel salta el formulario y arranca DIRECTO reintentando esa cancelación
 *     puntual (Estado 2 en adelante) — es el flujo de "Reintentar anulación" del Estado 5.
 */

import { useState, useEffect, useCallback, useRef } from "react";
import { AlertTriangle, CheckCircle2, Loader2, Ban, X } from "lucide-react";
import { cancellationsApi } from "../api/cancellationsApi";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { showSuccess, showError } from "../../../alerts";
import { formatCurrency } from "../../../lib/utils";
import { buildPenaltyClassificationPayload } from "../lib/penaltyPayload";
import { resolverTextoRechazoAnularReserva } from "../lib/anularReservaRechazoLogic";
import {
    esAnulacionMultiFactura,
    todasLasNotasSalieronBien,
    hayNotaPendiente,
    contarNotasResueltas,
    construirTextoAvisoMultiFactura,
    construirTextoConfirmacionMulti,
    construirTextoExitoMulti,
    construirTextoEncabezadoRevision,
    entradasSaldoAFavor,
} from "../lib/multiCreditNoteFlow";
import { NotasCreditoProgressList } from "./NotasCreditoProgressList";
import {
    TEXTO_BANNER_DIRECT_CANCEL,
    TEXTO_BANNER_SALDO_FAVOR_INICIO,
    TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA,
    TEXTO_BANNER_SALDO_FAVOR_NEGRITA,
    TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA,
    TEXTO_BANNER_CREDIT_NOTE,
    TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA,
    MENSAJE_EXITO_DIRECT_CANCEL,
    MENSAJE_EXITO_PAYMENTS_TO_CREDIT,
    MENSAJE_EXITO_CREDIT_NOTE,
    TEXTO_PROCESANDO_MULTI,
    TEXTO_SALDO_A_FAVOR_MULTI_PREFIJO,
    TEXTO_BOTON_REINTENTAR_FALTANTE,
    TEXTO_TIMEOUT_MULTI,
    TEXTO_REQUIERE_APROBACION_MULTI,
} from "./cancelarReservaCopy";

// Intervalo y tope de intentos del polling de notas de crédito (mismo molde que H2/EmitirFacturaInline).
const POLL_INTERVAL_MS_MULTI = 3000;
const POLL_MAX_INTENTOS_MULTI = 20; // 20 × 3s = 60s de espera máxima antes de mostrar "sigue en proceso".

// ─── Funciones de lógica pura (replicadas en cancelarReservaInline.test.mjs) ──

/**
 * Determina el caso de anulación a partir del discriminador del backend.
 * Cuando el campo `cancellationCase` no viene (DTO viejo en cache), cae al
 * comportamiento legacy usando `requiresInvoiceAnnulmentToCancel`.
 *
 * El caso "PaymentsToCredit" NO puede inferirse desde el booleano legacy solo
 * (requeriría saber si hay cobros), así que el fallback ignora ese caso y
 * queda en DirectCancel o CreditNote — igual que el comportamiento anterior.
 *
 * @param {object} reserva - DTO de la reserva
 * @returns {string} - "DirectCancel" | "PaymentsToCredit" | "CreditNote" | "NotApplicable" | "PreSale"
 */
function determinarCasoAnulacion(reserva) {
    if (reserva?.cancellationCase) {
        return reserva.cancellationCase;
    }
    // FALLBACK: DTO sin cancellationCase (versión vieja en cache o sin actualizar).
    // CreditNote si tiene factura CAE vivo, DirectCancel en cualquier otro caso.
    return reserva?.requiresInvoiceAnnulmentToCancel === true ? "CreditNote" : "DirectCancel";
}

/**
 * Formatea los montos de saldo a favor para el cartel celeste (caso PaymentsToCredit).
 * Usa el formateador de moneda del proyecto para ser consistente con el resto de la app.
 * Separa con " · " cuando hay más de una moneda (nunca suma ARS + USD: regla del contador).
 *
 * @param {Array<{currency: string, amount: number}>} creditByCurrency
 * @returns {string} - Ej: "$ 150.000,00 · US$200,00"
 */
function formatearMontosSaldoAFavor(creditByCurrency) {
    if (!creditByCurrency || creditByCurrency.length === 0) return "";
    return creditByCurrency
        .map((item) => formatCurrency(item.amount, item.currency))
        .join(" · ");
}

// ─── Componente ───────────────────────────────────────────────────────────────

export function CancelarReservaInline({ reserva, onCancelado, onCerrar, onSilentRefresh, bookingCancellationToRetry }) {
    const [reason, setReason] = useState("");

    // `processing` cubre todas las llamadas internas como un único loading visible al usuario.
    const [processing, setProcessing] = useState(false);

    // Mensaje de conflicto (400/409 recuperable). Se muestra en un banner inline para que
    // el usuario lo vea sin que desaparezca el formulario con lo cargado.
    const [conflictMessage, setConflictMessage] = useState(null);

    // ── ADR-042: estado del flujo multi-factura (2+ facturas vivas) ────────────────────────
    // "form"               → panel normal (motivo + banner). Cubre TODO el comportamiento de
    //                         siempre (DirectCancel/PaymentsToCredit/CreditNote mono-factura).
    // "confirmando-multi"  → Estados 0+1 de la spec fusionados: aviso con la lista de facturas
    //                         + "¿Seguro?" (ver nota de arquitectura en handleCancelar).
    // "procesando-multi"   → Estado 2: avance por nota, con polling al backend.
    // "exito-multi"        → Estado 3: cartel verde, todas las notas salieron bien.
    // "revision-multi"     → Estado 4: cartel naranja, alguna nota falló.
    // "timeout-multi"      → variante defensiva: el polling se agotó sin resolver todas las notas.
    const modoReintento = Boolean(bookingCancellationToRetry?.publicId);
    const [estadoMulti, setEstadoMulti] = useState(modoReintento ? "procesando-multi" : "form");
    // BC recién drafteado, antes de confirmar (solo lo necesita "confirmando-multi").
    const [draftMultiFactura, setDraftMultiFactura] = useState(null);
    // BC vigente durante procesando/éxito/revisión — se actualiza en cada vuelta del polling.
    const [bcActivo, setBcActivo] = useState(bookingCancellationToRetry ?? null);
    // Fix reviewer (2026-07-02, punto 4): true mientras "Sí, anular" o "Reintentar la que falta"
    // tienen su POST en vuelo — deshabilita el botón para que un doble click no dispare dos
    // llamadas. Un solo flag alcanza porque los dos botones nunca están visibles a la vez
    // (son de estados distintos: confirmando-multi vs revision-multi).
    const [accionMultiEnCurso, setAccionMultiEnCurso] = useState(false);

    // Referencias del polling de notas de crédito (no disparan re-render).
    const pollMultiRef = useRef(null);
    const pollIntentosMultiRef = useRef(0);
    // Fix reviewer (2026-07-02, punto 2): guarda si el componente sigue montado. Un tick del
    // polling puede tener su `await` en vuelo justo cuando el usuario cierra el panel (unmount);
    // sin este guard, la respuesta que llega DESPUÉS dispara setState sobre un componente ya
    // desmontado (warning de React + fuga potencial). Mismo patrón que el flag `cancelado` que
    // suele usarse en efectos async con fetch, pero accesible desde el intervalo también.
    const estaMontadoRef = useRef(true);

    // useEffect con []: corre una sola vez al montar el componente para resetear el estado.
    // En modo reintento NO limpiamos bcActivo/estadoMulti (ya vienen seteados arriba desde props).
    useEffect(() => {
        setReason("");
        setProcessing(false);
        setConflictMessage(null);
    }, []);

    // Limpieza del intervalo de polling al desmontar (evita un interval huérfano si el
    // usuario navega a otra página mientras el polling sigue activo).
    useEffect(() => {
        return () => {
            estaMontadoRef.current = false;
            if (pollMultiRef.current) clearInterval(pollMultiRef.current);
        };
    }, []);

    // ─── ADR-042: polling y handlers del flujo multi-factura ──────────────────────

    /**
     * Arranca (o reinicia) el polling de GET /cancellations/{publicId} hasta que ninguna nota de
     * crédito quede "Pending". Mismo molde que el polling de fiscal-status de H2/EmitirFacturaInline,
     * pero consultando el propio BookingCancellationDto (que ya trae el estado de cada NC hija).
     */
    const iniciarPollingMulti = useCallback((bcPublicId) => {
        pollIntentosMultiRef.current = 0;
        if (pollMultiRef.current) clearInterval(pollMultiRef.current);

        pollMultiRef.current = setInterval(async () => {
            pollIntentosMultiRef.current += 1;

            if (pollIntentosMultiRef.current > POLL_MAX_INTENTOS_MULTI) {
                clearInterval(pollMultiRef.current);
                pollMultiRef.current = null;
                // Fix reviewer (punto 5): refrescamos igual aunque no se resolvió del todo — si
                // AFIP contesta justo después de este timeout, que la reserva/extracto ya estén al
                // día cuando el usuario cierre el panel (en vez de quedar con datos viejos).
                onSilentRefresh?.();
                if (estaMontadoRef.current) setEstadoMulti("timeout-multi");
                return;
            }

            try {
                const bc = await cancellationsApi.getByPublicId(bcPublicId);
                // El componente pudo desmontarse MIENTRAS esperábamos esta respuesta (el usuario
                // cerró el panel). clearInterval ya frenó los próximos ticks, pero este `await` en
                // vuelo no se cancela solo — hay que chequear antes de tocar el estado.
                if (!estaMontadoRef.current) return;

                setBcActivo(bc);

                if (hayNotaPendiente(bc.creditNotes)) return; // Sigue emitiendo, seguimos consultando.

                clearInterval(pollMultiRef.current);
                pollMultiRef.current = null;

                setEstadoMulti(todasLasNotasSalieronBien(bc.creditNotes) ? "exito-multi" : "revision-multi");

                // Refrescamos la reserva/extracto en SEGUNDO PLANO (sin cerrar el panel): el vendedor
                // sigue viendo el cartel de resultado hasta que decida cerrar (mismo patrón que H2).
                onSilentRefresh?.();
            } catch {
                // Error de red durante el poll: no interrumpir, seguir intentando. El límite de
                // intentos de arriba frena si el problema persiste.
            }
        }, POLL_INTERVAL_MS_MULTI);
    }, [onSilentRefresh]);

    // ADR-042: en modo reintento (abierto desde la franja "en revisión"), arrancamos DIRECTO
    // reintentando la cancelación puntual que vino por props, sin pasar por el formulario.
    // useEffect con []: el publicId a reintentar no cambia durante la vida de este panel
    // (se abre una vez por click en "Reintentar anulación").
    useEffect(() => {
        if (!modoReintento) return;
        (async () => {
            try {
                const bc = await cancellationsApi.retryCreditNotes(bookingCancellationToRetry.publicId);
                setBcActivo(bc);
                iniciarPollingMulti(bc.publicId);
            } catch {
                showError("No se pudo reintentar la anulación. Probá de nuevo en unos segundos.");
                setEstadoMulti("revision-multi");
            }
        })();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    // Estado 1 (P2=A): el usuario cierra el "¿Seguro?" sin confirmar. El BC ya quedó Drafted en
    // la base (draft() es idempotente): si vuelve a tocar "Anular reserva" con el MISMO motivo,
    // el backend reutiliza esa misma fila en vez de crear una nueva.
    const handleVolverConfirmacionMulti = () => {
        setEstadoMulti("form");
        setDraftMultiFactura(null);
    };

    // Fix reviewer (2026-07-02, punto 6): a11y del diálogo "¿Seguro?" — Escape lo cierra (mismo
    // efecto que "Volver") y, al cerrarse (por Escape, "Volver" o "Sí, anular"), el foco vuelve al
    // elemento que lo abrió — un usuario de teclado no debe "perder" su posición en la pantalla.
    // No es un focus trap completo (Tab libre dentro/fuera del diálogo), pero cubre el caso de uso
    // real: entrar con Enter/click, salir con Escape o un botón, foco de vuelta en su lugar.
    useEffect(() => {
        if (estadoMulti !== "confirmando-multi") return;

        // document.activeElement es el botón "Anular reserva" que abrió este diálogo.
        const elementoConFocoPrevio = document.activeElement;

        const handleKeyDown = (event) => {
            if (event.key === "Escape") {
                handleVolverConfirmacionMulti();
            }
        };
        document.addEventListener("keydown", handleKeyDown);

        return () => {
            document.removeEventListener("keydown", handleKeyDown);
            elementoConFocoPrevio?.focus?.();
        };
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [estadoMulti]);

    // Estado 1 → 2: confirma la cancelación (emite TODAS las notas de crédito pendientes) y
    // arranca el avance por nota. Mismo payload de penalidad/snapshot que el camino mono-factura.
    const handleConfirmarMulti = async () => {
        if (accionMultiEnCurso) return; // Defensa contra doble click (fix reviewer punto 4).
        setAccionMultiEnCurso(true);
        setEstadoMulti("procesando-multi");

        const penaltyClassification = buildPenaltyClassificationPayload("operator_pass_through", null, null, null);
        // Tanda B (2026-07-16): ya no armamos ni mandamos "snapshotData". El backend resuelve
        // las condiciones fiscales y el tipo de cambio SOLO, directo de la base.
        const payload = {
            isAdminOverride: false,
            overrideReason: null,
            approvalRequestPublicId: null,
            ...penaltyClassification,
        };

        try {
            const confirmado = await cancellationsApi.confirm(draftMultiFactura.publicId, payload);
            setBcActivo(confirmado);
            iniciarPollingMulti(confirmado.publicId);
        } catch (error) {
            // Error ANTES de que se emitiera ninguna nota: NO dejamos la reserva "en revisión"
            // (regla dura de la spec — "en revisión" es solo si el backend llegó a intentar emitir).
            // Volvemos al formulario con el motivo intacto para que el usuario reintente.
            setEstadoMulti("form");
            // Fix bug prod (2026-07-02): 409 requiresApproval = el workflow de aprobaciones exige
            // autorización previa para anular esta factura. Mensaje específico ANTES del genérico
            // (el genérico "probá de nuevo" no le decía al usuario qué pasaba ni qué hacer).
            // El flujo completo de pedir la autorización (RequestApprovalModal) queda para otra
            // pasada; acá solo evitamos el mensaje mudo. NUNCA exponemos requestType/entityType/
            // entityId — son internos del backend.
            if (error?.status === 409 && error?.payload?.requiresApproval === true) {
                setConflictMessage(TEXTO_REQUIERE_APROBACION_MULTI);
            } else if (error?.status === 409) {
                // Tanda 3 (2026-07-20): este handler llama al MISMO confirm() que el camino
                // mono-factura de handleCancelar — mismo mapa código → criollo, misma función.
                const { texto } = resolverTextoRechazoAnularReserva(
                    error,
                    "No se pudo confirmar la anulación. Probá de nuevo; si el problema sigue, contactá a administración."
                );
                setConflictMessage(texto);
            } else {
                showError("No se pudo confirmar la anulación. Probá de nuevo en unos segundos.");
            }
        } finally {
            setAccionMultiEnCurso(false);
        }
    };

    // Estado 4: reintenta SOLO las notas faltantes desde dentro del mismo panel (sin cerrar).
    const handleReintentarDesdeRevision = async () => {
        if (accionMultiEnCurso) return; // Defensa contra doble click (fix reviewer punto 4).
        setAccionMultiEnCurso(true);
        setEstadoMulti("procesando-multi");
        try {
            const bc = await cancellationsApi.retryCreditNotes(bcActivo.publicId);
            setBcActivo(bc);
            iniciarPollingMulti(bc.publicId);
        } catch (error) {
            // Fix bug prod (2026-07-02): mismo caso que handleConfirmarMulti. Acá el error normal
            // vuelve a "revision-multi" (el usuario puede reintentar de nuevo desde ahí), pero
            // requiresApproval necesita el cartel de "form" porque es el único lugar del panel
            // donde conflictMessage tiene dónde mostrarse (revision-multi solo usa toasts, que
            // desaparecen solos — este mensaje necesita quedarse a la vista).
            if (error?.status === 409 && error?.payload?.requiresApproval === true) {
                setEstadoMulti("form");
                setConflictMessage(TEXTO_REQUIERE_APROBACION_MULTI);
            } else {
                setEstadoMulti("revision-multi");
                showError("No se pudo reintentar la anulación. Probá de nuevo en unos segundos.");
            }
        } finally {
            setAccionMultiEnCurso(false);
        }
    };

    // ─── Lógica principal ──────────────────────────────────────────────────────

    const handleCancelar = async () => {
        const trimmedReason = reason.trim();
        if (trimmedReason.length < 10) {
            showError("El motivo debe tener al menos 10 caracteres.");
            return;
        }

        setProcessing(true);
        setConflictMessage(null);

        const caso = determinarCasoAnulacion(reserva);

        // ── Casos SIN factura (DirectCancel y PaymentsToCredit) → endpoint annul-with-credit ─────────
        // Ambos van al MISMO endpoint dedicado (un solo POST con { reason }, sin el flujo de 2 pasos del draft):
        //   - DirectCancel:      sin cobros → baja directa, sin nota de crédito.
        //   - PaymentsToCredit:  con cobros → la plata cobrada queda como saldo a favor del cliente.
        // El backend decide internamente qué hacer según haya o no cobros; el front solo cambia el mensaje de
        // éxito. El front valida el motivo ≥10 chars arriba; el backend también valida server-side.
        if (caso === "DirectCancel" || caso === "PaymentsToCredit") {
            try {
                await cancellationsApi.annulWithCredit(reserva.publicId, trimmedReason);
                // Caso 3 (PaymentsToCredit): el aviso del contador va SIEMPRE pegado abajo del
                // mensaje de éxito (obra "anular sin factura", 2026-07-23) — este caso implica
                // por definición que hubo cobros sin factura, así que no depende de ningún dato
                // extra, es incondicional dentro de este `if`.
                const mensajeExito = caso === "DirectCancel"
                    ? MENSAJE_EXITO_DIRECT_CANCEL
                    : `${MENSAJE_EXITO_PAYMENTS_TO_CREDIT}\n${TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA}`;
                showSuccess(mensajeExito, "Anulación confirmada");
                onCancelado();
            } catch (error) {
                // NUNCA mostramos el texto crudo del backend (puede traer nombres internos).
                if (error?.status === 400) {
                    // 400: el backend rechazó el motivo (< 10 chars). El front ya lo valida,
                    // pero el backend también controla server-side (regla de auditoría).
                    setConflictMessage("Revisá el motivo de la anulación (mínimo 10 caracteres).");
                } else if (error?.status === 403) {
                    showError("No tenés permiso para anular esta reserva.");
                } else if (error?.status === 404) {
                    showError("No encontramos la reserva. Recargá la página.");
                } else if (error?.status === 409) {
                    // Tanda 3 (2026-07-20): el motor manda un código propio para cada uno de los 4
                    // rechazos posibles de este endpoint (ANNUL_CREDIT_*). El mapa vive en
                    // anularReservaRechazoLogic.js — mismo mapeo que usan los otros 2 puntos de abajo.
                    const { texto } = resolverTextoRechazoAnularReserva(
                        error,
                        "No se pudo anular la reserva. Probá de nuevo; si el problema sigue, contactá a administración."
                    );
                    setConflictMessage(texto);
                } else {
                    showError("No se pudo anular la reserva. Probá de nuevo en unos segundos.");
                }
                setProcessing(false);
            }
            return;
        }

        // ── Caso CreditNote (con factura CAE viva) → flujo draft → confirm ──────────────
        // Este camino emite la Nota de Crédito en AFIP/ARCA (2 llamadas al backend). DirectCancel y
        // PaymentsToCredit ya se resolvieron arriba por el endpoint annul-with-credit; acá solo cae CreditNote
        // (y el fallback conservador de DTOs viejos sin cancellationCase con requiresInvoiceAnnulmentToCancel).

        // PASO 1: crear el borrador (draft). Si falla acá no seguimos.
        let draft;
        try {
            draft = await cancellationsApi.draft(reserva.publicId, trimmedReason);
        } catch (error) {
            // Tanda 3 (2026-07-20): INV-152/081/100 ya traen su código real en invariantCode.
            // El mapa de anularReservaRechazoLogic.js decide el texto; si el código no está
            // catalogado (o no vino ninguno), cae al mismo texto neutro de siempre — NUNCA
            // mostramos el texto crudo del backend fuera de esa tabla.
            if (error?.status === 409) {
                const { texto } = resolverTextoRechazoAnularReserva(
                    error,
                    "No se pudo iniciar la anulación. Probá de nuevo; si el problema sigue, contactá a administración."
                );
                setConflictMessage(texto);
            } else {
                showError("No se pudo iniciar la anulación. Probá de nuevo en unos segundos.");
            }
            setProcessing(false);
            return;
        }

        // ADR-042: 2+ facturas vivas → freno extra antes de emitir nada. En vez de confirmar
        // directo (como el caso mono-factura de abajo), mostramos un cartel "¿Seguro?" con la
        // lista de facturas y el resumen de monedas, y recién ahí el usuario dispara la emisión.
        //
        // NOTA DE ARQUITECTURA: la spec (Estado 0) muestra este aviso ANTES de escribir el
        // motivo. No es posible acá: el backend recién devuelve la lista de facturas (con su
        // moneda) al craftear el BC, y craftear el BC exige un motivo válido (≥10 caracteres,
        // inmutable una vez creado). Por eso el aviso aparece INMEDIATAMENTE DESPUÉS del primer
        // click en "Anular reserva" (ya con el motivo escrito), fusionado con el "¿Seguro?" del
        // Estado 1 en una sola pantalla — mismo número de clicks que la spec, mismo copy exacto
        // de ambos estados, ningún dato se pierde.
        if (esAnulacionMultiFactura(draft.saleInvoices)) {
            setDraftMultiFactura(draft);
            setEstadoMulti("confirmando-multi");
            setProcessing(false);
            return;
        }

        // PASO 2: confirmar la cancelación. Emite la NC en AFIP/ARCA si hay factura (async).
        //
        // Clasificación de penalidad: siempre "operator_pass_through" (int 0).
        // La agencia NO emite ningún cargo propio ni nota de débito en este paso.
        // Solo se emite la nota de crédito. Es la opción más neutra para el mostrador.
        const penaltyClassification = buildPenaltyClassificationPayload(
            "operator_pass_through",
            null,
            null,
            null
        );

        // Tanda B (2026-07-16): ya no armamos ni mandamos "snapshotData". El backend resuelve
        // las condiciones fiscales y el tipo de cambio SOLO, directo de la base.
        const payload = {
            isAdminOverride: false,
            overrideReason: null,
            approvalRequestPublicId: null,
            ...penaltyClassification,
        };

        try {
            await cancellationsApi.confirm(draft.publicId, payload);

            // Solo llega acá el caso CreditNote (con NC en AFIP), más el fallback conservador de DTOs viejos.
            showSuccess(MENSAJE_EXITO_CREDIT_NOTE, "Anulación confirmada");
            onCancelado();
        } catch (error) {
            // NUNCA mostramos el texto crudo del backend.
            const code = error?.payload?.code || error?.payload?.invariantCode || "";
            if (error?.status === 409 && code === "CONCURRENT_EDIT") {
                // CONCURRENT_EDIT ya funciona bien — spec Tanda 3: "no se toca".
                setConflictMessage(
                    "Otro usuario modificó esta cancelación al mismo tiempo. Recargá la página y volvé a intentar."
                );
            } else if (error?.status === 409) {
                // Tanda 3 (2026-07-20): agrega INV-093 e INV-100 al mismo mapa código → criollo
                // que usan los otros 2 puntos de swallow (misma función, un solo lugar para
                // agregar códigos nuevos).
                const { texto } = resolverTextoRechazoAnularReserva(
                    error,
                    "No se pudo confirmar la anulación. Probá de nuevo; si el problema sigue, contactá a administración."
                );
                setConflictMessage(texto);
            } else {
                showError("No se pudo confirmar la anulación. Probá de nuevo en unos segundos.");
            }

            setProcessing(false);
        }
    };

    // ─── Render ───────────────────────────────────────────────────────────────

    const reasonTrimmed = reason.trim();
    const charsLeft = 1000 - reason.length;
    const tooShort = reason.length > 0 && reasonTrimmed.length < 10;
    const canSubmit = !processing && reasonTrimmed.length >= 10;

    // Determina el caso activo para mostrar el cartel correcto.
    const casoAnulacion = determinarCasoAnulacion(reserva);

    // Monto formateado para el cartel celeste (solo viene en el caso PaymentsToCredit).
    const montosFormateados = formatearMontosSaldoAFavor(reserva?.cancellationCreditByCurrency);

    return (
        <div
            className="rounded-xl border-2 border-rose-200 bg-rose-50/40 dark:border-rose-900/40 dark:bg-rose-950/10 p-5 space-y-4"
            data-testid="cancelar-reserva-inline"
        >
            {/* ── Cabecera del panel ──
                En modo reintento (abierto desde la franja "en revisión") el título cambia:
                no se está anulando de nuevo, se está completando lo que ya empezó. */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <Ban className="w-4 h-4 text-rose-600" aria-hidden="true" />
                    <h4 className="text-sm font-bold text-slate-900 dark:text-white">
                        {modoReintento ? "Reintentar anulación" : "Anular reserva"}
                    </h4>
                    <span className="text-xs text-slate-500 dark:text-slate-400">
                        #{reserva.numeroReserva} — {reserva.customerName}
                    </span>
                </div>
                <button
                    type="button"
                    onClick={onCerrar}
                    disabled={processing || estadoMulti === "procesando-multi"}
                    className="text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 rounded p-1 disabled:opacity-40"
                    aria-label="Cerrar sin anular la reserva"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            {/* ── Panel normal (motivo + banner por caso) — Estado "form" ──
                En modo reintento este bloque nunca se muestra (arranca directo en "procesando-multi"). */}
            {estadoMulti === "form" && (
                <>
                    {/* ── Cartel según el caso de anulación (guia-ux-gaston.md 2026-06-25) ──
                        Caso 2 DirectCancel     → VERDE:   sin factura, sin cobros.
                        Caso 3 PaymentsToCredit → CELESTE:  sin factura, con cobros (plata → saldo a favor).
                        Caso 4 CreditNote       → ÁMBAR:   con factura CAE → emite NC en AFIP/ARCA.
                        Cualquier otro caso     → ÁMBAR:   fallback conservador (no prometemos nada).
                        Los textos viven en cancelarReservaCopy.js para que los tests puedan importarlos.

                        Retoque P4-2 (2026-07-22, spec docs/ux/2026-07-22-p4-retoques-circuito-proveedor.md,
                        P2=A): este cartel del caso solo se muestra si NO hay error. Cuando el motor
                        rechaza la anulación, la explicación "qué iba a pasar" ya no aporta — lo único
                        que importa es el motivo del error y qué hacer. El cartel del caso reaparece
                        solo cuando el vendedor corrige y el error se limpia. */}
                    {!conflictMessage && casoAnulacion === "DirectCancel" && (
                        <div
                            className="flex items-start gap-2 rounded-lg border border-green-200 bg-green-50 p-3.5 text-xs text-green-800 dark:bg-green-950/30 dark:border-green-800 dark:text-green-200"
                            data-testid="cancelar-banner-sin-factura"
                        >
                            <span>{TEXTO_BANNER_DIRECT_CANCEL}</span>
                        </div>
                    )}

                    {!conflictMessage && casoAnulacion === "PaymentsToCredit" && (
                        <div
                            className="flex flex-col items-start gap-1.5 rounded-lg border border-sky-200 bg-sky-50 p-3.5 text-xs text-sky-800 dark:bg-sky-950/30 dark:border-sky-800 dark:text-sky-200"
                            data-testid="cancelar-banner-saldo-favor"
                        >
                            {/* Decisión UX (guia 2026-06-25): mostrar el monto cobrado por moneda
                                para que el agente sepa exactamente cuánto queda como saldo a favor.
                                Los montos nunca se suman entre monedas (regla del contador: ARS y USD siempre separados).
                                "SALDO A FAVOR" en negrita (presentacional; el texto vive en cancelarReservaCopy.js). */}
                            <span>
                                {TEXTO_BANNER_SALDO_FAVOR_INICIO}
                                {montosFormateados ? ` (${montosFormateados})` : ""}.{" "}
                                {TEXTO_BANNER_SALDO_FAVOR_ANTE_NEGRITA}
                                <strong>{TEXTO_BANNER_SALDO_FAVOR_NEGRITA}</strong>
                                {TEXTO_BANNER_SALDO_FAVOR_POST_NEGRITA}
                            </span>
                            {/* Obra "anular sin factura" (2026-07-23): aviso SUAVE del contador
                                (P-20, no frena) — pegado abajo del aviso de saldo a favor, DENTRO
                                del mismo cartel (mismo bloque semántico, no un cartel aparte ni un
                                CartelEmergente: es una excepción fiscal puntual, no un bloqueo). */}
                            <span className="font-semibold" data-testid="cancelar-aviso-contador-cobros-sin-factura">
                                {TEXTO_AVISO_CONTADOR_COBROS_SIN_FACTURA}
                            </span>
                        </div>
                    )}

                    {!conflictMessage && casoAnulacion !== "DirectCancel" && casoAnulacion !== "PaymentsToCredit" && (
                        <div
                            className="flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 p-3.5 text-xs text-amber-800 dark:bg-amber-950/30 dark:border-amber-800 dark:text-amber-200"
                            data-testid="cancelar-banner-con-factura"
                        >
                            <AlertTriangle className="h-4 w-4 flex-shrink-0 mt-0.5" aria-hidden="true" />
                            <span>{TEXTO_BANNER_CREDIT_NOTE}</span>
                        </div>
                    )}

                    {/* Aviso 4 del inventario (spec cartel emergente 2026-07-22): rechazo LARGO
                        del motor al anular → ventana, no un recuadro incrustado en el panel.
                        Obra "anular sin factura" (2026-07-23): el freno de plata R1
                        (ANNUL_CREDIT_UNANCHORED_OPERATOR_REFUND), único código de la tabla que
                        agregaba un botón ("Emitir factura"), se eliminó del backend — anular la
                        reserva entera ya no lo bloquea. Esta ventana ya no ofrece ningún botón:
                        el mensaje real del motor (tal cual, P-13) + "Entendido" alcanza.
                        Al estar en ventana, ya no hace falta "tapar" el cartel del caso de arriba
                        (P4-2): el cartel de arriba y esta ventana nunca compiten por el mismo
                        espacio — igual seguimos ocultando el cartel del caso mientras hay error
                        (ver el `!conflictMessage &&` de los banners de arriba), que sigue siendo
                        la señal correcta de "no expliques qué iba a pasar, ya no va a pasar". */}
                    <CartelEmergente
                        isOpen={Boolean(conflictMessage)}
                        variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                        message={conflictMessage}
                        onClose={() => setConflictMessage(null)}
                        dataTestId="cancelar-inline-conflict-msg"
                    />

                    {/* ── Motivo obligatorio ── */}
                    <div>
                        <label
                            className="block text-xs font-bold uppercase tracking-wider text-slate-500 mb-1.5"
                            htmlFor="cancelar-inline-reason"
                        >
                            {/* ADR-036: "de la anulación" en vez de "de la cancelación" */}
                            Motivo de la anulación <span className="text-rose-500" aria-hidden="true">*</span>
                        </label>
                        <textarea
                            id="cancelar-inline-reason"
                            value={reason}
                            onChange={(e) => setReason(e.target.value)}
                            rows={4}
                            maxLength={1000}
                            disabled={processing}
                            placeholder="Por ejemplo: el cliente cambió de planes por motivos personales..."
                            data-testid="cancelar-inline-reason-textarea"
                            aria-describedby="cancelar-inline-reason-hint"
                            className="w-full rounded-xl border border-slate-300 dark:border-slate-600 dark:bg-slate-800 dark:text-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-rose-400 disabled:opacity-50"
                        />
                        <div id="cancelar-inline-reason-hint" className="mt-1 flex justify-between text-xs text-slate-400">
                            {tooShort ? (
                                <span className="text-rose-600 font-semibold" role="alert">Mínimo 10 caracteres.</span>
                            ) : (
                                <span />
                            )}
                            <span>{charsLeft} restantes</span>
                        </div>
                    </div>

                    {/* ── Acciones ── */}
                    <div className="flex justify-end gap-3 pt-1">
                        <button
                            type="button"
                            onClick={onCerrar}
                            disabled={processing}
                            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 dark:hover:bg-slate-700 transition-colors disabled:opacity-50"
                        >
                            Volver
                        </button>
                        <button
                            type="button"
                            onClick={handleCancelar}
                            disabled={!canSubmit}
                            data-testid="cancelar-inline-confirm-btn"
                            className="rounded-lg bg-rose-600 px-4 py-2 text-sm font-bold text-white hover:bg-rose-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                        >
                            {processing && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
                            {processing ? "Anulando..." : "Anular reserva"}
                        </button>
                    </div>
                </>
            )}

            {/* ── ADR-042: "¿Seguro?" con la lista de facturas (Estados 0+1 fusionados) ──
                Ventana centrada (overlay local), como el "¿seguro?" de H2 (única excepción al
                patrón "todo en línea" — es un freno antes de algo irreversible). Foco inicial en
                "Volver" (más seguro, para que un Enter accidental no dispare la emisión). */}
            {estadoMulti === "confirmando-multi" && draftMultiFactura && (
                <div
                    className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
                    role="dialog"
                    aria-modal="true"
                    aria-labelledby="cancelar-multi-confirm-title"
                    aria-describedby="cancelar-multi-confirm-desc"
                    data-testid="cancelar-multi-confirm-dialog"
                >
                    <div className="relative bg-white dark:bg-slate-900 rounded-2xl shadow-xl max-w-md w-full mx-4 p-6 space-y-4">
                        <div className="flex items-center gap-3">
                            <div className="p-2 bg-amber-100 dark:bg-amber-900/30 rounded-lg">
                                <AlertTriangle className="w-6 h-6 text-amber-600 dark:text-amber-400" aria-hidden="true" />
                            </div>
                            <h2 id="cancelar-multi-confirm-title" className="text-base font-semibold text-slate-900 dark:text-white">
                                ¿Seguro?
                            </h2>
                        </div>

                        <div id="cancelar-multi-confirm-desc" className="text-sm text-slate-700 dark:text-slate-300 space-y-3">
                            {/* Estado 0: aviso con la cantidad de facturas + resumen de monedas. */}
                            <p>{construirTextoAvisoMultiFactura(draftMultiFactura.saleInvoices)}</p>

                            {/* Lista de facturas — una por fila, monto en su moneda, nunca sumados. */}
                            <ul className="space-y-1" data-testid="cancelar-multi-lista-facturas">
                                {draftMultiFactura.saleInvoices.map((factura, index) => (
                                    <li
                                        key={`${factura.comprobanteLabel}-${index}`}
                                        className="flex items-center justify-between gap-2 text-xs font-medium text-slate-600 dark:text-slate-300"
                                        data-testid={`cancelar-multi-factura-${index}`}
                                    >
                                        <span>· {factura.comprobanteLabel}</span>
                                        <span className="font-semibold text-slate-800 dark:text-slate-100">
                                            {formatCurrency(factura.amount, factura.currency)}
                                        </span>
                                    </li>
                                ))}
                            </ul>

                            {/* Estado 1: la confirmación "¿Seguro?" propiamente dicha. */}
                            <p className="font-semibold text-amber-700 dark:text-amber-400">
                                {construirTextoConfirmacionMulti(draftMultiFactura.saleInvoices)}
                            </p>
                        </div>

                        <div className="flex gap-3 pt-2">
                            <button
                                type="button"
                                onClick={handleVolverConfirmacionMulti}
                                autoFocus
                                disabled={accionMultiEnCurso}
                                data-testid="cancelar-multi-btn-volver"
                                className="flex-1 rounded-xl border border-slate-300 dark:border-slate-600 px-4 py-2 text-sm font-medium text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800 disabled:opacity-50"
                            >
                                Volver
                            </button>
                            <button
                                type="button"
                                onClick={handleConfirmarMulti}
                                disabled={accionMultiEnCurso}
                                data-testid="cancelar-multi-btn-si-anular"
                                className="flex-1 rounded-xl bg-rose-600 hover:bg-rose-700 px-4 py-2 text-sm font-bold text-white disabled:opacity-50"
                            >
                                Sí, anular
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* ── ADR-042 Estado 2: PROCESANDO, avance por nota ──
                Mismo lugar que el resto del panel (no cierra ni deja un toast suelto). Se
                auto-actualiza solo, consultando el backend (mismo patrón que H2). */}
            {estadoMulti === "procesando-multi" && (
                <div
                    className="flex flex-col items-center gap-4 py-8 text-center"
                    role="status"
                    aria-live="polite"
                    data-testid="cancelar-multi-procesando"
                >
                    <Loader2 className="h-8 w-8 text-rose-500 animate-spin" aria-hidden="true" />
                    <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{TEXTO_PROCESANDO_MULTI}</p>
                    <NotasCreditoProgressList creditNotes={bcActivo?.creditNotes} />
                    {Array.isArray(bcActivo?.creditNotes) && bcActivo.creditNotes.length > 0 && (
                        <p className="text-xs font-semibold text-slate-500 dark:text-slate-400" data-testid="cancelar-multi-contador">
                            ({contarNotasResueltas(bcActivo.creditNotes)} de {bcActivo.creditNotes.length})
                        </p>
                    )}
                </div>
            )}

            {/* ── ADR-042 Estado 3: ÉXITO TOTAL — cartel verde ──
                role="status" + aria-live="polite": un lector de pantalla tiene que enterarse de
                que la anulación terminó bien sin que el usuario tenga que ir a buscarlo (mismo
                criterio que el resto de los carteles de resultado del panel). */}
            {estadoMulti === "exito-multi" && (
                <div
                    className="flex flex-col items-center gap-3 py-8 text-center"
                    role="status"
                    aria-live="polite"
                    data-testid="cancelar-multi-exito"
                >
                    <CheckCircle2 className="h-10 w-10 text-emerald-500" aria-hidden="true" />
                    <div className="space-y-1">
                        <p className="text-base font-bold text-emerald-700 dark:text-emerald-400">Reserva anulada.</p>
                        <p className="text-sm text-slate-700 dark:text-slate-300">
                            {construirTextoExitoMulti(bcActivo?.creditNotes)}
                        </p>
                        {/* La línea de saldo a favor SOLO aparece si hubo cobros que se conviertan en
                            saldo (regla P4-A). La moneda del saldo es la de la FACTURA anulada. */}
                        {entradasSaldoAFavor(bcActivo?.clientCreditByCurrency).length > 0 && (
                            <p
                                className="text-sm font-semibold text-emerald-800 dark:text-emerald-300"
                                data-testid="cancelar-multi-saldo-favor"
                            >
                                {TEXTO_SALDO_A_FAVOR_MULTI_PREFIJO}{" "}
                                {entradasSaldoAFavor(bcActivo.clientCreditByCurrency)
                                    .map((entrada) => formatCurrency(entrada.amount, entrada.currency))
                                    .join(" · ")}
                            </p>
                        )}
                    </div>
                    <button
                        type="button"
                        onClick={onCerrar}
                        data-testid="cancelar-multi-btn-cerrar-exito"
                        className="mt-2 px-5 py-2 text-sm font-semibold text-white bg-emerald-600 rounded-xl hover:bg-emerald-700 transition-colors"
                    >
                        Cerrar
                    </button>
                </div>
            )}

            {/* ── ADR-042 Estado 4: EN REVISIÓN — cartel naranja con reintentar ──
                La nota que salió NO se deshace (se explica en el propio texto del encabezado). */}
            {estadoMulti === "revision-multi" && (
                <div
                    className="flex flex-col items-center gap-3 py-8 text-center"
                    role="alert"
                    data-testid="cancelar-multi-revision"
                >
                    <AlertTriangle className="h-10 w-10 text-orange-500" aria-hidden="true" />
                    <p className="text-base font-bold text-orange-700 dark:text-orange-400">
                        {construirTextoEncabezadoRevision(bcActivo?.creditNotes)}
                    </p>
                    <NotasCreditoProgressList creditNotes={bcActivo?.creditNotes} />
                    {/* Único botón de la spec (Estado 4): "Reintentar la que falta". Para salir sin
                        reintentar todavía, el usuario usa la [X] de la cabecera (no duplicamos un
                        segundo "Cerrar" acá — la reserva va a seguir mostrando la franja "en
                        revisión" la próxima vez que la abra, así que nada se pierde). */}
                    <button
                        type="button"
                        onClick={handleReintentarDesdeRevision}
                        disabled={accionMultiEnCurso}
                        data-testid="cancelar-multi-btn-reintentar"
                        className="mt-2 px-5 py-2 text-sm font-bold text-white bg-orange-600 rounded-xl hover:bg-orange-700 transition-colors disabled:opacity-50"
                    >
                        {TEXTO_BOTON_REINTENTAR_FALTANTE}
                    </button>
                </div>
            )}

            {/* ── ADR-042: el polling se agotó sin que AFIP resuelva todas las notas ──
                Variante defensiva (no forma parte de los 6 estados de la spec): evita dejar al
                usuario mirando un spinner infinito. No es un error — el resultado se va a ver
                en la reserva cuando AFIP conteste (o en la franja "en revisión" si hace falta). */}
            {estadoMulti === "timeout-multi" && (
                <div
                    className="flex flex-col items-center gap-3 py-8 text-center"
                    role="status"
                    aria-live="polite"
                    data-testid="cancelar-multi-timeout"
                >
                    <Loader2 className="h-8 w-8 text-slate-400" aria-hidden="true" />
                    <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{TEXTO_TIMEOUT_MULTI}</p>
                    <button
                        type="button"
                        onClick={onCerrar}
                        data-testid="cancelar-multi-btn-cerrar-timeout"
                        className="mt-2 px-5 py-2 text-sm font-semibold border border-slate-300 text-slate-700 rounded-xl hover:bg-slate-50 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors"
                    >
                        Cerrar
                    </button>
                </div>
            )}
        </div>
    );
}
