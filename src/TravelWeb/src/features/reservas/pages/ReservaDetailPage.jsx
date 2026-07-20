import { useEffect, useState, useMemo } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Clock, CreditCard, Download, Eye, ExternalLink, FileText, History, Loader2, Paperclip, Pencil, Receipt, Send, Trash2, Users, Plus, RefreshCw, Check, Ban } from "lucide-react";
import { api } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import ReservaTimeline from "../../../components/ReservaTimeline";
import ConfirmModal from "../../../components/ConfirmModal";
import PassengerFormModal from "../../../components/PassengerFormModal";
import { ReservaDocumentsTab } from "../../../components/ReservaDocumentsTab";
import ServiceFormModal from "../../../components/ServiceFormModal";
import { ServiceInlineCard } from "../inline-service/ServiceInlineCard";
import { ReservaVoucherTab } from "../../../components/ReservaVoucherTab";
// Nota: DataGrid y sus partes ya no se usan en ReservaDetailPage — fueron
// migrados a EstadoCuentaExtracto.jsx. Se conserva este comentario para contexto
// en caso de que alguien agregue una tabla nueva en esta página.
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency } from "../../../lib/utils";
import { getPublicId, getRelatedPublicId } from "../../../lib/publicIds";
import { CapacityWarning } from "../components/CapacityWarning";
import { AvisosPlegadosBar } from "../components/AvisosPlegadosBar";
import { getServiciosSinConfirmar, construirAvisosInformativos } from "../avisosFicha";
import { EditReservaDatesModal } from "../components/EditReservaDatesModal";
import { PassengerList } from "../components/PassengerList";
import { ReservaHeader } from "../components/ReservaHeader";
import { ReservaLockBanner } from "../components/ReservaLockBanner";
import { ReservaSummaryStrip } from "../components/ReservaSummaryStrip";
import { RegistrarCobroInline } from "../components/RegistrarCobroInline";
import { EmitirFacturaInline } from "../components/EmitirFacturaInline";
import { RevertStatusModal } from "../components/RevertStatusModal";
import { ServiceList, calculateServiciosCanceladosResumen } from "../components/ServiceList";
import { EditAuthorizationModal } from "../components/EditAuthorizationModal";
import { MarkLostModal } from "../components/MarkLostModal";
import { CorregirEntradaViajeModal } from "../components/CorregirEntradaViajeModal";
import { ReprogramarViajeModal } from "../components/ReprogramarViajeModal";
import { isStatusLocked, isReservaEnEstadoVivo } from "../components/ReservaStatusBadge";
// ADR-048 T4/B3 (2026-07-17): fuente ÚNICA de "reserva sin efecto" (par Cancelled +
// PendingOperatorRefund). Los helpers de más abajo la reusan en vez de comparar el
// string de estado a mano.
import { isReservaAnulada } from "../moneyStatus";
// Tanda 2 (2026-07-18, spec docs/ux/2026-07-18-t1-t2-contrato-pantalla-motor.md):
// mismo modal y mismo comportamiento que Cobranzas → Movimientos cuando emitir o
// anular un comprobante requiere autorización de un supervisor.
import RequestApprovalModal from "../../approvals/components/RequestApprovalModal";
import { resolverAccionAlFallarComprobante, armarEtiquetaComprobante } from "../lib/receiptApprovalFlow";
import { useReservaDetail } from "../hooks/useReservaDetail";
import { useOperationalFlags } from "../../../contexts/OperationalFlagsContext";
import { useAlerts } from "../../../contexts/AlertsContext";
import { CancelarReservaInline } from "../../cancellations/components/CancelarReservaInline";
import { ConfirmarMultaOperadorInline } from "../../cancellations/components/ConfirmarMultaOperadorInline";
import { CerrarSinMultaInline } from "../../cancellations/components/CerrarSinMultaInline";
import { DeshacerCierreSinMultaInline } from "../../cancellations/components/DeshacerCierreSinMultaInline";
import { OperatorPenaltyStepPanel } from "../../cancellations/components/OperatorPenaltyStepPanel";
import { PartialCreditNoteEmissionPanel } from "../../cancellations/components/PartialCreditNoteEmissionPanel";
import { cancellationsApi } from "../../cancellations/api/cancellationsApi";
import { getActiveSaleInvoices } from "../../cancellations/lib/partialCreditNoteEmissionLogic";
import { contarNotasFaltantes, construirTextoFranjaEnRevision } from "../../cancellations/lib/multiCreditNoteFlow";
// Spec "el paso de multa vive en la ficha" (2026-07-08) + ADR-044 T1 (2026-07-10,
// multa por operador): helpers puros que traducen la situación de multa (singular o,
// desde ADR-044, una lista con un elemento POR OPERADOR) a la "familia" de cartel a
// mostrar (ver el propio archivo para el detalle de las 6 familias) y el rastro textual
// del cierre sin multa.
import {
  textoRastroWaived,
  textoRastroDeshacerMulta,
  tienePasoDeMultaOperador,
  situacionesConPanelDeMulta,
  situacionesConPreguntaDeMulta,
  hayMasDeUnOperadorConMulta,
  tituloConNombreOperador,
  sugerenciaCaminoMulta,
} from "../../cancellations/operatorPenaltyBanner";
// Configuracion de multas de cancelacion (2026-07-14, spec
// docs/ux/2026-07-14-config-multas-proveedor.md, Pieza 3): textos del "Deshacer" del
// cierre sin multa. Viven en un módulo aparte (no JSX) para que el enlace del cartel
// rosa (acá abajo) y el panel que se abre (DeshacerCierreSinMultaInline.jsx) usen
// SIEMPRE la misma frase — y para poder testearlos sin renderizar nada.
import { ENLACE_REABRIR_PASO_MULTA } from "../../cancellations/lib/reabrirPasoMultaTextos.js";
import { elegirMonedaSugeridaParaMulta } from "../lib/operatorPenaltyCurrency";
import { hasPermission, isAdmin } from "../../../auth";
import { calcularSugerenciaComposicion } from "../lib/pasajeroHint";
import { EstadoCuentaResumen } from "../components/EstadoCuentaResumen";
import { EstadoCuentaExtracto } from "../components/EstadoCuentaExtracto";
// Paso 3 (H2 2026-06-24): helper compartido con EmitirFacturaInline para formatear
// el número de comprobante. Mismo formato en el cartel de éxito y en el Estado de Cuenta.
import { formatearEtiquetaFactura } from "../lib/invoiceFormatUtils";

/**
 * Determina si la reserva está en un estado "congelado" para VOUCHERS Y DOCUMENTOS:
 * solo se puede ver/imprimir lo ya emitido, pero NO crear, anular ni editar.
 *
 * Criterio (decisión UX 2026-06-22):
 *  - En viaje (Traveling): el viaje ya arrancó, nada cambia.
 *  - Perdida (Lost): cerrada sin cobro, es histórico.
 *  - Anulada (Cancelled): proceso de anulación completado.
 *  - Esperando reembolso (PendingOperatorRefund): anulada, en solo lectura.
 *  - Facturada total (FullyInvoiced): ya no se emiten más documentos de venta.
 *
 * Se usa en la Zona C (vouchers) y en la Zona de documentos.
 * La Zona B (PDF de factura AFIP) siempre queda visible.
 *
 * IMPORTANTE: NO se usa para controlar Editar/Eliminar cobro.
 * Esas acciones se gobiernan por la capacidad `canEditOrDeletePayment` del DTO
 * (ver PaymentReceiptActions). Razón: cobro y facturación son ejes separados (ADR-037);
 * una reserva FullyInvoiced puede seguir teniendo cobros pendientes de ajuste.
 */
function esEstadoCongelado(reserva) {
  if (!reserva) return false;
  return (
    reserva.status === "Traveling" ||
    reserva.status === "Lost" ||
    isReservaAnulada(reserva) ||
    reserva.invoicingStatus === "FullyInvoiced"
  );
}

/**
 * Determina si la reserva está en un estado donde NO se puede emitir ni anular
 * el comprobante de un cobro (recibo). NO incluye FullyInvoiced porque facturación
 * y cobranza son ejes separados (ADR-037).
 *
 * Criterio para recibos de cobro (BUG IMP-3 fix, 2026-06-24):
 *  - En viaje (Traveling): viaje en curso, solo lectura.
 *  - Perdida (Lost): histórico cerrado.
 *  - Anulada (Cancelled): proceso terminado.
 *  - Esperando reembolso (PendingOperatorRefund): anulada, en solo lectura.
 *
 * Closed (Finalizada) NO está aquí: en Closed todavía se puede emitir recibo
 * de un cobro reciente. El control de Editar/Eliminar cobro se delega al
 * capability `canEditOrDeletePayment` del DTO del backend.
 */
function esCongeladoParaRecibos(reserva) {
  if (!reserva) return false;
  return (
    reserva.status === "Traveling" ||
    reserva.status === "Lost" ||
    isReservaAnulada(reserva)
  );
}

// Mapa de TipoComprobante AFIP a etiqueta legible.
//  Facturas: 1=A, 6=B, 11=C, 51=M.
//  Notas de Débito: 2=A, 7=B, 12=C, 52=M.
//  Notas de Crédito: 3=A, 8=B, 13=C, 53=M.
function getDocumentTypeLabel(tipoComprobante) {
  switch (tipoComprobante) {
    case 1: return { kind: "factura", letter: "A", label: "Factura A" };
    case 6: return { kind: "factura", letter: "B", label: "Factura B" };
    case 11: return { kind: "factura", letter: "C", label: "Factura C" };
    case 51: return { kind: "factura", letter: "M", label: "Factura M" };
    case 2: return { kind: "nd", letter: "A", label: "Nota de Débito A" };
    case 7: return { kind: "nd", letter: "B", label: "Nota de Débito B" };
    case 12: return { kind: "nd", letter: "C", label: "Nota de Débito C" };
    case 52: return { kind: "nd", letter: "M", label: "Nota de Débito M" };
    case 3: return { kind: "nc", letter: "A", label: "Nota de Crédito A" };
    case 8: return { kind: "nc", letter: "B", label: "Nota de Crédito B" };
    case 13: return { kind: "nc", letter: "C", label: "Nota de Crédito C" };
    case 53: return { kind: "nc", letter: "M", label: "Nota de Crédito M" };
    default: return { kind: "unknown", letter: "", label: `Comprobante #${tipoComprobante}` };
  }
}

// Badge de estado de la factura. Prioriza AnnulmentStatus para que una factura
// cancelada con NC se muestre claramente como "ANULADA" en vez del "Aprobada"
// historico (la factura sigue con Resultado="A" en BD pero esta anulada).
function InvoiceStatusBadge({ resultado, annulmentStatus }) {
  if (annulmentStatus === "Succeeded") {
    return <span className="rounded px-2 py-0.5 text-[10px] font-black uppercase bg-rose-100 text-rose-700">Anulada</span>;
  }
  if (annulmentStatus === "Pending") {
    return <span className="rounded px-2 py-0.5 text-[10px] font-black uppercase bg-amber-100 text-amber-700">Anulando…</span>;
  }
  const isApproved = resultado === "A";
  const isRejected = resultado === "R";
  const className = isApproved
    ? "bg-emerald-100 text-emerald-700"
    : isRejected
    ? "bg-rose-100 text-rose-700"
    : "bg-slate-100 text-slate-600";
  const label = isApproved ? "Aprobada" : isRejected ? "Rechazada" : "En proceso";
  return <span className={`rounded px-2 py-0.5 text-[10px] font-black uppercase ${className}`}>{label}</span>;
}

// Etiqueta del tipo de comprobante. Si es una NC/ND, muestra debajo un sub-label
// con la factura origen (numero formateado) para que el usuario sepa que esto
// no es una factura independiente sino que cancela / amplia una previa.
function InvoiceTypeLabel({ tipoComprobante, originalInvoiceNumeroComprobante, originalInvoicePuntoDeVenta, originalInvoiceTipoComprobante }) {
  const { kind, label } = getDocumentTypeLabel(tipoComprobante);
  const showsOriginalRef =
    (kind === "nc" || kind === "nd") &&
    originalInvoiceNumeroComprobante != null &&
    originalInvoicePuntoDeVenta != null;
  const colorClass =
    kind === "nc" ? "text-amber-700 dark:text-amber-300" :
    kind === "nd" ? "text-indigo-700 dark:text-indigo-300" :
    "";
  return (
    <div className="flex flex-col gap-0.5">
      <span className={colorClass}>{label}</span>
      {showsOriginalRef ? (
        <span className="text-[10px] font-normal text-slate-500 dark:text-slate-400">
          {kind === "nc" ? "Anula" : "Amplía"} {getDocumentTypeLabel(originalInvoiceTipoComprobante ?? 0).label.replace(/ ?[ABCM]$/, "")} {String(originalInvoicePuntoDeVenta).padStart(5, "0")}-{String(originalInvoiceNumeroComprobante).padStart(8, "0")}
        </span>
      ) : null}
    </div>
  );
}

/**
 * H2 Paso 5 (2026-06-24): Acciones de factura AFIP en el extracto de cuenta.
 *
 * Muestra número de comprobante + CAE visibles, y acciones:
 *   - "Ver PDF": abre el PDF en pestaña nueva.
 *   - "Enviar al cliente": envía el PDF al cliente/pagador por WhatsApp.
 *     Usa POST /messages/invoice (endpoint dedicado, 2026-06-24).
 *     Respuestas: 200 éxito; 400 {message} (sin contacto, factura no emitida);
 *     403 sin acceso; 502 fallo de WhatsApp → "No se pudo enviar, intentá de nuevo".
 *
 * Solo se muestran si la factura fue aceptada por AFIP (resultado === "A").
 * Botones con icono + texto (regla guia-ux-gaston.md 2026-06-08).
 *
 * Props:
 *   invoice  - InvoiceDto de la reserva (campos: invoiceType, puntoDeVenta,
 *              numeroComprobante, cae, vencimientoCAE, publicId, resultado).
 *   reserva  - DTO completo (para customerPublicId y publicId de la reserva).
 */
function InvoicePdfActions({ invoice, reserva }) {
  const [busy, setBusy] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const invoicePublicId = getPublicId(invoice);

  // Solo mostramos acciones cuando AFIP aprobó la factura (resultado === "A").
  if (invoice?.resultado !== "A" || !invoicePublicId) {
    return <span className="text-xs text-slate-400">-</span>;
  }

  // Etiqueta legible reutilizando el helper compartido (Paso 3).
  // Usamos invoice.invoiceType (letra directa: "A"/"B"/"C"/"M") en lugar de tipoComprobante (int).
  const numeroLegible = formatearEtiquetaFactura(
    invoice.invoiceType,
    invoice.puntoDeVenta,
    invoice.numeroComprobante
  );

  // Label para el atributo download del PDF
  const pdv = String(invoice.puntoDeVenta ?? 0).padStart(4, "0");
  const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
  const fileLabel = `Factura-${invoice.invoiceType || "X"}-${pdv}-${num}.pdf`;

  const fetchBlob = async () => {
    const response = await api.get(`/invoices/${invoicePublicId}/pdf`, { responseType: "blob" });
    return new Blob([response], { type: "application/pdf" });
  };

  const view = async () => {
    setBusy(true);
    try {
      const blob = await fetchBlob();
      const url = window.URL.createObjectURL(blob);
      window.open(url, "_blank");
    } catch (error) {
      showError(getApiErrorMessage(error) || "No se pudo abrir la factura.", "Error al abrir factura");
    } finally {
      setBusy(false);
    }
  };

  /**
   * H2 Paso 5: envía la factura al cliente por WhatsApp via POST /messages/invoice.
   *
   * Destinatario: siempre el cliente/pagador de la reserva (personType="customer").
   * El backend valida que la factura esté emitida y que el cliente tenga teléfono.
   *
   * Errores posibles:
   *   400 → el backend devuelve { message } accionable (sin contacto, factura no emitida).
   *   403 → sin permiso de envío.
   *   502 → fallo del canal WhatsApp → "No se pudo enviar, intentá de nuevo".
   */
  const enviarAlCliente = async () => {
    const customerPublicId = reserva?.customerPublicId;
    const reservaPublicId = reserva?.publicId ?? getPublicId(reserva);

    // Si la reserva no tiene cliente asignado, el backend lo rechazaría igual con 400.
    // Cortamos antes para dar un mensaje más claro.
    if (!customerPublicId) {
      showError("No hay un contacto cargado para enviar. Asigná un cliente a la reserva.");
      return;
    }

    setEnviando(true);
    try {
      await api.post("/messages/invoice", {
        personType: "customer",
        personId: customerPublicId,
        reservaId: reservaPublicId,
        invoicePublicId: invoicePublicId,
        caption: `Factura ${pdv}-${num}`,
      });
      showSuccess("Factura enviada al cliente.");
    } catch (error) {
      const statusCode = error?.status ?? error?.response?.status ?? 0;

      if (statusCode === 403) {
        // 403: el usuario no tiene permiso de envío de mensajes.
        showError("No tenés permiso para enviar mensajes.");
      } else if (statusCode === 502) {
        // 502: el canal WhatsApp falló (servicio externo).
        showError("No se pudo enviar, intentá de nuevo.");
      } else {
        // 400 u otro: el backend devuelve { message } con texto accionable.
        // Ej: "La factura no está emitida", "El cliente no tiene teléfono".
        const mensaje = getApiErrorMessage(error) || "No se pudo enviar la factura.";
        showError(mensaje);
      }
    } finally {
      setEnviando(false);
    }
  };

  return (
    <div className="flex flex-col gap-1.5">
      {/* Número de comprobante + CAE bien a la vista (spec Paso 5 H2) */}
      <div className="text-xs font-mono font-semibold text-slate-700 dark:text-slate-300">
        {numeroLegible}
      </div>
      {invoice.cae && (
        <div className="text-[10px] text-slate-400 dark:text-slate-500">
          CAE: {invoice.cae}
          {invoice.vencimientoCAE && (
            <> · Vto: {new Date(invoice.vencimientoCAE).toLocaleDateString("es-AR")}</>
          )}
        </div>
      )}

      {/* Acciones con icono + texto (regla 2026-06-08 guia-ux-gaston.md) */}
      <div className="inline-flex items-center gap-2">
        <button
          type="button"
          onClick={view}
          disabled={busy || enviando}
          className="inline-flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-indigo-600 hover:bg-indigo-50 disabled:opacity-50 dark:text-indigo-300 dark:hover:bg-indigo-950/40 transition-colors"
          aria-label="Ver PDF de la factura"
        >
          <Eye className="h-3.5 w-3.5" aria-hidden="true" />
          Ver PDF
        </button>
        <button
          type="button"
          onClick={enviarAlCliente}
          disabled={busy || enviando}
          data-testid="btn-enviar-factura-cliente"
          className="inline-flex items-center gap-1 rounded px-2 py-1 text-xs font-medium text-emerald-600 hover:bg-emerald-50 disabled:opacity-50 dark:text-emerald-300 dark:hover:bg-emerald-950/40 transition-colors"
          aria-label="Enviar factura al cliente por WhatsApp"
        >
          <Send className="h-3.5 w-3.5" aria-hidden="true" />
          {enviando ? "Enviando..." : "Enviar al cliente"}
        </button>
      </div>
    </div>
  );
}

function getPaymentReceipt(payment) {
  return payment?.receipt || payment?.Receipt || null;
}

function canIssuePaymentReceipt(payment) {
  const entryType = payment?.entryType || payment?.EntryType || "Payment";
  const receipt = getPaymentReceipt(payment);
  return entryType === "Payment" && Number(payment?.amount || payment?.Amount || 0) > 0 && !receipt;
}

/**
 * Acciones del comprobante de pago (recibo) de un cobro individual.
 *
 * Incluye también Editar cobro y Eliminar cobro.
 * Hay dos criterios distintos de bloqueo (ADR-037 + BUG IMP-3 fix 2026-06-24):
 *
 *   - `congelado`: controla emitir/anular el RECIBO del cobro.
 *     Verdadero en estados operativos terminales (Traveling/Lost/Cancelled/PendingOperatorRefund).
 *     Usa esCongeladoParaRecibos(), que NO incluye FullyInvoiced (facturación y cobranza son
 *     ejes separados en ADR-037).
 *
 *   - `canEditarEliminar`: controla los botones Editar y Eliminar del cobro en sí.
 *     Viene de la capacidad `canEditOrDeletePayment.allowed` del DTO del backend.
 *     El backend ya considera el estado de la reserva (Closed/terminal → false).
 *     Si el backend no la envía, se asume false por seguridad.
 *
 * Decisión de UX 2026-06-22: "ver/imprimir un papel ya hecho" sí; "crear/anular/editar" no.
 *
 * Props:
 *  - congelado: boolean — solo afecta emitir/anular el RECIBO (no editar/eliminar el cobro).
 *  - canEditarEliminar: boolean — si el backend permite editar o eliminar este cobro.
 *  - onEditarCobro: callback(payment) — abre RegistrarCobroInline en modo edición.
 *  - onEliminarCobro: callback(payment) — pide confirmación y elimina el cobro.
 */
function PaymentReceiptActions({ payment, onView, onIssue, onVoid, congelado, canEditarEliminar, onEditarCobro, onEliminarCobro }) {
  const receipt = getPaymentReceipt(payment);

  // Un cobro con recibo anulado ya fue procesado formalmente; no tiene sentido editarlo
  // aunque el capability diga que se puede. El recibo anulado es un "techo" extra.
  const reciboAnulado = receipt?.status === "Voided";

  // Gobernado por la capacidad real del backend + guard de recibo anulado.
  // canEditarEliminar=false en Closed, terminal u otros estados que el backend restrinja.
  const cobroEsEditable = Boolean(canEditarEliminar) && !reciboAnulado;

  if (receipt) {
    return (
      <div className="flex flex-wrap items-center gap-2">
        {/* El chip con el número de recibo (o "Comprobante anulado") se muestra siempre:
            es trazabilidad de un documento ya emitido, no una acción. */}
        <span className={`rounded-full px-2 py-1 text-[10px] font-black uppercase ${reciboAnulado ? "bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400" : "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300"}`}>
          {reciboAnulado ? "Comprobante anulado" : receipt.receiptNumber}
        </span>
        {!reciboAnulado ? (
          <>
            {/* "Ver PDF" es lectura → siempre visible, incluso en congelado */}
            <button
              type="button"
              onClick={() => onView(payment)}
              className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs font-bold text-indigo-600 transition-colors hover:bg-indigo-50 dark:text-indigo-300 dark:hover:bg-indigo-900/30"
              title="Ver comprobante de pago"
              aria-label="Ver comprobante de pago"
            >
              <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
              Ver PDF
            </button>
            {/* "Anular comprobante" es escritura → solo en estados no congelados */}
            {!congelado && typeof onVoid === "function" && (
              <button
                type="button"
                onClick={() => onVoid(payment)}
                className="inline-flex items-center gap-1 rounded-lg border border-rose-200 px-2 py-1 text-xs font-bold text-rose-600 transition-colors hover:bg-rose-50 dark:border-rose-900/30 dark:text-rose-400 dark:hover:bg-rose-900/20"
                title="Anular comprobante de pago"
                aria-label="Anular comprobante de pago"
              >
                Anular comprobante
              </button>
            )}
          </>
        ) : null}

        {/* B1: Editar / Eliminar cobro — solo en estados editables y si el recibo no está anulado.
            Un recibo anulado implica que el cobro ya fue procesado formalmente; no se edita. */}
        {cobroEsEditable && (
          <>
            {typeof onEditarCobro === "function" && (
              <button
                type="button"
                onClick={() => onEditarCobro(payment)}
                className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2 py-1 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                title="Editar cobro"
                aria-label="Editar cobro"
                data-testid="btn-editar-cobro"
              >
                <Pencil className="h-3.5 w-3.5" aria-hidden="true" />
                Editar
              </button>
            )}
            {typeof onEliminarCobro === "function" && (
              <button
                type="button"
                onClick={() => onEliminarCobro(payment)}
                className="inline-flex items-center gap-1 rounded-lg border border-rose-200 px-2 py-1 text-xs font-bold text-rose-600 transition-colors hover:bg-rose-50 dark:border-rose-900/30 dark:text-rose-400 dark:hover:bg-rose-900/20"
                title="Eliminar cobro"
                aria-label="Eliminar cobro"
                data-testid="btn-eliminar-cobro"
              >
                <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
                Eliminar
              </button>
            )}
          </>
        )}
      </div>
    );
  }

  // En congelado: si no hay recibo, no se ofrece emitir ni se muestra "Sin comprobante"
  if (congelado) return null;

  if (canIssuePaymentReceipt(payment)) {
    return (
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={() => onIssue(payment)}
          className="inline-flex items-center gap-1.5 rounded-lg border border-indigo-200 px-3 py-1.5 text-xs font-bold text-indigo-700 transition-colors hover:bg-indigo-50 dark:border-indigo-800 dark:text-indigo-300 dark:hover:bg-indigo-900/30"
        >
          <Receipt className="h-3.5 w-3.5" aria-hidden="true" />
          Emitir comprobante
        </button>
        {/* B1: también disponible Editar / Eliminar cuando aún no hay recibo */}
        {typeof onEditarCobro === "function" && (
          <button
            type="button"
            onClick={() => onEditarCobro(payment)}
            className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2 py-1 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            title="Editar cobro"
            aria-label="Editar cobro"
            data-testid="btn-editar-cobro"
          >
            <Pencil className="h-3.5 w-3.5" aria-hidden="true" />
            Editar
          </button>
        )}
        {typeof onEliminarCobro === "function" && (
          <button
            type="button"
            onClick={() => onEliminarCobro(payment)}
            className="inline-flex items-center gap-1 rounded-lg border border-rose-200 px-2 py-1 text-xs font-bold text-rose-600 transition-colors hover:bg-rose-50 dark:border-rose-900/30 dark:text-rose-400 dark:hover:bg-rose-900/20"
            title="Eliminar cobro"
            aria-label="Eliminar cobro"
            data-testid="btn-eliminar-cobro"
          >
            <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
            Eliminar
          </button>
        )}
      </div>
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-2">
      <span className="text-xs text-slate-400">Sin comprobante</span>
      {/* B1: Editar / Eliminar disponibles aunque no haya comprobante (entryType puente, etc.) */}
      {typeof onEditarCobro === "function" && (
        <button
          type="button"
          onClick={() => onEditarCobro(payment)}
          className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2 py-1 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
          title="Editar cobro"
          aria-label="Editar cobro"
          data-testid="btn-editar-cobro"
        >
          <Pencil className="h-3.5 w-3.5" aria-hidden="true" />
          Editar
        </button>
      )}
      {typeof onEliminarCobro === "function" && (
        <button
          type="button"
          onClick={() => onEliminarCobro(payment)}
          className="inline-flex items-center gap-1 rounded-lg border border-rose-200 px-2 py-1 text-xs font-bold text-rose-600 transition-colors hover:bg-rose-50 dark:border-rose-900/30 dark:text-rose-400 dark:hover:bg-rose-900/20"
          title="Eliminar cobro"
          aria-label="Eliminar cobro"
          data-testid="btn-eliminar-cobro"
        >
          <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
          Eliminar
        </button>
      )}
    </div>
  );
}

/**
 * Aviso de servicios aún no resueltos en una reserva Confirmada.
 *
 * ADR-020: la reserva pasa a Confirmada AUTOMÁTICAMENTE cuando todos los servicios
 * quedan resueltos. Si por algún motivo hay servicios sin resolver cuando la reserva
 * ya está Confirmada (caso posible por datos previos a ADR-020), este banner los muestra.
 *
 * Sin jerga ni códigos internos — el texto está pensado para el vendedor, no para el técnico.
 * La info de qué falta se lee del workflowStatus visible en la tabla de servicios.
 *
 * (2026-07-05) Es un aviso INFORMATIVO (spec 5A): no pide ninguna acción inmediata,
 * por eso vive plegado dentro de "N avisos más" (ver AvisosPlegadosBar). La decisión
 * de "hay servicios sin confirmar" vive en avisosFicha.js para que el contador del
 * plegado y este banner nunca diverjan entre sí.
 */
function UnconfirmedServicesBanner({ reserva }) {
  const serviciosSinResolver = getServiciosSinConfirmar(reserva);
  if (serviciosSinResolver.length === 0) return null;

  return (
    <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-200">
      <div className="font-bold mb-1">
        {serviciosSinResolver.length} {serviciosSinResolver.length === 1 ? 'servicio sin confirmar' : 'servicios sin confirmar'}
      </div>
      <div className="text-xs text-amber-800 dark:text-amber-300 mb-2">
        Estos servicios todavía no tienen respuesta del proveedor. Resolvelós en la pestaña de Servicios antes de que empiece el viaje.
      </div>
      <ul className="text-xs space-y-0.5">
        {serviciosSinResolver.slice(0, 8).map((s, i) => (
          <li key={i}>• <strong>{s.nombre.trim()}</strong></li>
        ))}
        {serviciosSinResolver.length > 8 && <li className="italic">y {serviciosSinResolver.length - 8} más...</li>}
      </ul>
    </div>
  );
}

function PassengerCountsWidget({ initial, expectedCapacity = 0, onSave }) {
  const [adultCount, setAdultCount] = useState(initial.adultCount);
  const [childCount, setChildCount] = useState(initial.childCount);
  const [infantCount, setInfantCount] = useState(initial.infantCount);
  const [saving, setSaving] = useState(false);

  const total = (adultCount || 0) + (childCount || 0) + (infantCount || 0);
  const overCapacity = expectedCapacity > 0 && total > expectedCapacity;
  const dirty =
    adultCount !== initial.adultCount ||
    childCount !== initial.childCount ||
    infantCount !== initial.infantCount;

  const handleSubmit = async () => {
    setSaving(true);
    try {
      await onSave({ adultCount, childCount, infantCount });
    } finally {
      setSaving(false);
    }
  };

  const inputClass = "w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-center text-lg font-bold text-slate-900 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800 dark:text-white";

  return (
    <div className="space-y-6">
      <div className="text-sm text-slate-500 dark:text-slate-400">
        Acá cargás cuántos viajan. Los nombres y documentos se agregan en la solapa Pasajeros — o directamente al emitir cada servicio.
      </div>
      <div className="grid grid-cols-3 gap-4">
        <div>
          <label className="mb-1 block text-xs font-bold uppercase text-slate-500">Adultos</label>
          <input type="number" min="0" value={adultCount} onChange={(e) => setAdultCount(Math.max(0, parseInt(e.target.value, 10) || 0))} className={inputClass} />
        </div>
        <div>
          <label className="mb-1 block text-xs font-bold uppercase text-slate-500">Menores</label>
          <input type="number" min="0" value={childCount} onChange={(e) => setChildCount(Math.max(0, parseInt(e.target.value, 10) || 0))} className={inputClass} />
        </div>
        <div>
          <label className="mb-1 block text-xs font-bold uppercase text-slate-500">Infantes</label>
          <input type="number" min="0" value={infantCount} onChange={(e) => setInfantCount(Math.max(0, parseInt(e.target.value, 10) || 0))} className={inputClass} />
        </div>
      </div>
      <div className="flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-800 dark:bg-slate-800/50">
        <div className="text-sm">
          <div className="font-bold text-slate-700 dark:text-slate-200">Total: {total} pasajeros</div>
          {expectedCapacity > 0 ? (
            <div className={`text-xs ${overCapacity ? "text-rose-600 font-bold" : "text-slate-500"}`}>
              Servicios cargados esperan {expectedCapacity} pasajeros{overCapacity ? " (excede!)" : ""}
            </div>
          ) : (
            <div className="text-xs text-slate-400 italic">Agrega servicios para validar capacidad</div>
          )}
        </div>
        <button
          type="button"
          disabled={!dirty || saving || overCapacity}
          onClick={handleSubmit}
          className="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white transition-colors hover:bg-indigo-700 disabled:opacity-50"
        >
          {saving ? "Guardando..." : "Guardar cantidades"}
        </button>
      </div>
    </div>
  );
}

export default function ReservaDetailPage() {
  const { publicId } = useParams();
  const navigate = useNavigate();
  const [activeTab, setActiveTab] = useState("services");

  // ADR-020: enableSoldToSettleStates eliminado del ciclo. El ciclo es unico y directo.
  // Solo leemos los flags que siguen vigentes.
  const { flags } = useOperationalFlags();
  // ADR-017: cuando está ON, la carga de servicios usa la ficha en línea (ServiceInlineCard)
  // en lugar del modal (ServiceFormModal). Con OFF el comportamiento es idéntico al de hoy.
  const isCatalogFindOrCreateEnabled = flags.enableCatalogFindOrCreate;
  // F2: flag de avisos de próximos inicios. Cuando está ON, ServiceList muestra la columna "Avisos".
  // Es independiente de isCatalogFindOrCreateEnabled (catálogo OFF + avisos ON → columna visible).
  const isServiceDeadlineAlertsEnabled = flags.enableServiceDeadlineAlerts;

  // F2: windowDays viene del contexto de alertas (upcomingStartsWindowDays del backend).
  // null cuando el flag está OFF o la respuesta aún no llegó → UpcomingStartPill muestra "—".
  const { alerts } = useAlerts();
  const windowDays = alerts?.upcomingStartsWindowDays ?? null;

  // Estado de la ficha inline (solo se usa cuando enableCatalogFindOrCreate está ON)
  const [showInlineCard, setShowInlineCard] = useState(false);
  const [serviceToEditInline, setServiceToEditInline] = useState(null);

  // Permiso de cancelacion: se resuelve client-side desde el store de auth.
  // NOTA: esto es UI-only. El server-side siempre re-valida el permiso.
  // Con hasPermission("reservas.cancel"), isAdmin() retorna true para admins (bypass).
  const canCancelReserva = hasPermission("reservas.cancel");

  // showCancelInline: panel en linea ADR-035 (nuevo, reemplaza al modal en la solapa account)
  const [showCancelInline, setShowCancelInline] = useState(false);

  // ADR-014: panel inline de "Confirmar multa del operador" (paso diferido post-anulación).
  // Se muestra en el cartel de estado PendingOperatorRefund cuando hay una multa pendiente.
  // multaCancellationPublicId: el GUID de la cancelación vigente (obtenido de GET by-reserva al abrir el panel).
  const [showMultaInline, setShowMultaInline] = useState(false);
  const [multaCancellationPublicId, setMultaCancellationPublicId] = useState(null);
  // ADR-044 T4 (2026-07-10): facturas de venta activas de la cancelación vigente
  // (BookingCancellationDto.SaleInvoices), para el desplegable "¿a qué factura
  // corresponde?" de ConfirmarMultaOperadorInline. Con 0/1 factura queda ignorado
  // (el panel no muestra nada — autocompletado, sin cambios respecto de antes).
  const [multaSaleInvoices, setMultaSaleInvoices] = useState([]);
  const [buscandoMulta, setBuscandoMulta] = useState(false);
  // ADR-044 T1 (2026-07-10): la cancelación puede tener servicios de MÁS de un operador
  // (ADR-025), cada uno con su propia pregunta "¿cobró multa?". Guarda la situación
  // COMPLETA del operador cuyo panel Sí/No está abierto ahora mismo (no solo el guid):
  // de ahí sacamos tanto `supplierPublicId` (para el payload) como `currency` (para la
  // moneda sugerida del formulario). En el caso mono-operador de siempre, sigue siendo
  // el único elemento posible — el comportamiento no cambia.
  const [multaSituacionAbierta, setMultaSituacionAbierta] = useState(null);
  // 2026-06-28: panel "Cerrar sin multa" — segunda opción del flujo post-anulación.
  // Se abre cuando el agente elige "No cobró nada / devolvió todo".
  const [showSinMultaInline, setShowSinMultaInline] = useState(false);
  // 2026-06-28: panel "Deshacer el cierre sin multa" — solo visible para administradores.
  // Se abre cuando el admin necesita reabrir el paso (el operador terminó cobrando algo).
  const [showDeshacerWaiveInline, setShowDeshacerWaiveInline] = useState(false);

  // ADR-042 (2026-07-01): anulación con VARIAS facturas que quedó A MEDIAS (una nota de
  // crédito salió y otra no). stuckCancellation guarda el BookingCancellationDto solo cuando
  // el backend dice que se puede reintentar (canRetryCreditNotes=true); en cualquier otro caso
  // queda en null y la franja "en revisión" no aparece. showRetryCancelInline abre el panel
  // de reintento (Estado 5 → Estado 2 en adelante).
  //
  // Fix reviewer (2026-07-02): bcSiendoReintentado es una FOTO fija del BC tomada al hacer
  // click en "Reintentar anulación", independiente de stuckCancellation. Es necesaria porque
  // el propio panel de reintento refresca la reserva en cuanto sabe el resultado (onSilentRefresh),
  // y ESE refresco puede volver stuckCancellation=null (si terminó de resolverse bien) — sin esta
  // foto separada, el panel se quedaría sin la prop bookingCancellationToRetry a mitad de camino
  // y el cartel de éxito/revisión nunca llegaría a verse.
  const [stuckCancellation, setStuckCancellation] = useState(null);
  const [showRetryCancelInline, setShowRetryCancelInline] = useState(false);
  const [bcSiendoReintentado, setBcSiendoReintentado] = useState(null);

  // ADR-027: estado de carga del botón "Dar OK" (acknowledge-changes).
  // Evita doble click y da feedback visual al usuario mientras espera la respuesta del backend.
  const [acknowledging, setAcknowledging] = useState(false);

  const [showServiceModal, setShowServiceModal] = useState(false);
  const [serviceToEdit, setServiceToEdit] = useState(null);
  const [showPassengerForm, setShowPassengerForm] = useState(false);
  const [editingPassenger, setEditingPassenger] = useState(null);
  // Ficha de cobro en línea (2026-06-09): reemplaza el modal de pago en la solapa Estado de Cuenta.
  const [showCobroInline, setShowCobroInline] = useState(false);
  const [cobroAEditar, setCobroAEditar] = useState(null);
  // Ficha de emisión de factura en línea (2026-06-13, guia-ux-gaston.md): reemplaza CreateInvoiceModal.
  // Solo una ficha abierta a la vez: si está abierta la factura, se oculta el botón de cobro y viceversa.
  const [showFacturaInline, setShowFacturaInline] = useState(false);
  // ADR-031: el flujo Budget→InManagement ya no pasa por un modal centralizado.
  // El widget de cantidades avanza la reserva directo (sin confirmación extra).
  // confirmReservaModal fue eliminado — ya no hay seteo en isOpen:true en ningún camino.
  const [showRevertModal, setShowRevertModal] = useState(false);
  // ADR-020 F4: modal de solicitar autorizacion para editar una reserva bloqueada.
  const [showEditAuthModal, setShowEditAuthModal] = useState(false);
  // ADR-020: modal para marcar una cotizacion/presupuesto como Perdida.
  const [showMarkLostModal, setShowMarkLostModal] = useState(false);
  const [showEditDatesModal, setShowEditDatesModal] = useState(false);
  // Tanda 2 (2026-06-22): modal de corrección "Sacar de viaje" — solo Admin + Traveling + capability.
  const [showCorrectTravelingModal, setShowCorrectTravelingModal] = useState(false);
  // Tanda 3 (2026-06-23): modal "Reprogramar viaje" — mueve todas las fechas de servicios.
  const [showRescheduleModal, setShowRescheduleModal] = useState(false);
  // Tanda 2 contrato pantalla-motor (2026-07-18): contexto del modal "Solicitar
  // aprobación" cuando emitir/anular un comprobante de cobro devuelve 409
  // requiresApproval. null = modal cerrado. Mismo patrón que useFinanceActions
  // (onApprovalRequired) usa en Cobranzas → Movimientos.
  const [approvalContext, setApprovalContext] = useState(null);

  // ADR-031 v2.1 — Pieza C: estado del readiness. Se declara aquí porque useState
  // siempre va al inicio del componente, pero el useEffect que lo carga se mueve
  // DESPUÉS de useReservaDetail para evitar TDZ en el bundle de producción.
  // (Causa del crash: useEffect referenciaba `reserva` antes de que se declarara con const.)
  const [readiness, setReadiness] = useState(null);

  // Saldo a favor del cliente (fetch best-effort desde su cuenta corriente).
  // Se carga al abrir la solapa "account" con el publicId del cliente.
  // Si falla, el link "Ver cuenta del cliente" sigue mostrándose — degradación elegante.
  const [saldoClientePorMoneda, setSaldoClientePorMoneda] = useState(null);
  const [loadingSaldoCliente, setLoadingSaldoCliente] = useState(false);

  // Clave de refresco del extracto contable y del saldo del cliente.
  // Se incrementa cada vez que ocurre un cambio de plata (cobro, factura, recibo, anulación)
  // para que EstadoCuentaExtracto y el useEffect del saldo del cliente se re-ejecuten
  // sin necesidad de refrescar la página completa.
  const [accountRefreshKey, setAccountRefreshKey] = useState(0);

  // Incrementa la clave de refresco del extracto. Se llama JUNTO a fetchReserva
  // en cada acción de plata, para que la reserva y el extracto queden sincronizados.
  const refrescarExtracto = () => setAccountRefreshKey((k) => k + 1);

  const [confirmConfig, setConfirmConfig] = useState({
    isOpen: false,
    title: "",
    message: "",
    onConfirm: null,
    type: "warning",
    // isLoading evita doble clic y muestra spinner en el boton mientras espera la respuesta.
    isLoading: false,
  });

  /**
   * Abre el ConfirmModal con el titulo, mensaje, tipo y accion indicados.
   *
   * El onConfirm puede ser async: esperamos que termine ANTES de cerrar el modal.
   * Esto evita que el modal desaparezca antes de que la operacion termine, y evita
   * que el usuario haga doble clic mientras la accion esta en curso.
   * Si el handler falla, el error lo maneja el propio handler (showError); el modal
   * se cierra igual para no quedar trabado.
   */
  const askConfirmation = (config) => {
    setConfirmConfig({
      isOpen: true,
      title: config.title || "Confirmar accion",
      message: config.message || "Estas seguro?",
      type: config.type || "warning",
      isLoading: false,
      onConfirm: async () => {
        // Mostramos spinner en el boton "Confirmar" para bloquear doble clic.
        setConfirmConfig((prev) => ({ ...prev, isLoading: true }));
        try {
          await config.onConfirm();
        } finally {
          // Cerramos el modal DESPUES de que la accion termino (ok o error).
          // El error ya lo muestra el handler con showError; no lo re-mostramos aca.
          setConfirmConfig((prev) => ({ ...prev, isOpen: false, isLoading: false }));
        }
      },
    });
  };

  const {
    reserva,
    loading,
    suppliers,
    serviceCollectionErrors,
    fetchReserva,
    handleArchiveReserva,
    handleDeleteReserva,
    handleStatusChange,
    handleDeleteService,
    handleCancelService,
    handleDeletePassenger,
    handleServiceUpdated,
    allServices,
    capacity,
  } = useReservaDetail(publicId, navigate);

  const activeSaleInvoices = useMemo(
    () => getActiveSaleInvoices(reserva?.invoices),
    [reserva?.invoices]
  );

  // ADR-042 (2026-07-01): detecta si la anulación de esta reserva quedó "en revisión"
  // (multi-factura, una nota de crédito salió y otra no). Confirmar la anulación SIEMPRE
  // pone la reserva en estado "PendingOperatorRefund" (haya salido todo bien o a medias);
  // por eso el chequeo va scoped a ese estado — no llama al backend en el resto de reservas.
  // accountRefreshKey en las deps: así se re-consulta después de un reintento exitoso (la
  // llamada de reintento también refresca el extracto, ver onSilentRefresh más abajo).
  //
  // ⚠️ Este useEffect DEBE ir DESPUÉS de useReservaDetail: sus deps evalúan `reserva?.status`
  // durante el render y `reserva` es const de ese hook — ponerlo antes es TDZ ("Cannot access
  // before initialization") que CRASHEA toda la página en el bundle de producción (incidente
  // 2026-07-02, mismo error ya documentado en el useEffect de ADR-031 de acá abajo).
  useEffect(() => {
    if (!publicId || reserva?.status !== "PendingOperatorRefund") {
      setStuckCancellation(null);
      return;
    }
    let cancelado = false;
    (async () => {
      try {
        const bc = await cancellationsApi.getByReserva(publicId);
        if (!cancelado) setStuckCancellation(bc?.canRetryCreditNotes ? bc : null);
      } catch {
        // 404 (sin cancelación) o error de red: no mostramos la franja. No es un error visible
        // para el usuario — el cartel normal de "esperando reembolso" ya cubre ese caso.
        if (!cancelado) setStuckCancellation(null);
      }
    })();
    return () => { cancelado = true; };
  }, [publicId, reserva?.status, accountRefreshKey]);

  // ADR-031 v2.1 — Pieza C: cargamos el TransitionReadinessDto cuando el usuario abre
  // la solapa Pasajeros. Este useEffect se coloca DESPUÉS de useReservaDetail para que
  // `reserva` ya esté declarado como const — evita el TDZ que crasheaba en producción.
  // (En dev el TDZ no se manifestaba porque el dev server tolera el orden; en el bundle
  //  de producción Vite/Rollup reordena y la referencia a `reserva` explotaba con
  //  "Cannot access 'ae' before initialization".)
  useEffect(() => {
    // Solo cargamos el readiness al abrir la tab de pasajeros y si hay una reserva cargada.
    if (activeTab !== "passengers" || !publicId || !reserva) return;

    // Usamos "to=InManagement" porque ese es el destino desde Budget.
    // Lo que nos interesa del DTO son los campos expectedAdults/Children/Infants.
    api.get(`/reservas/${publicId}/transition-readiness?to=InManagement`)
        .then(res => setReadiness(res.data))
        .catch(() => setReadiness(null)); // Si falla, no mostramos franja (best-effort)
  // Corremos el efecto cuando: la tab activa cambia, o la reserva recarga (publicId + reserva).
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, publicId, reserva?.status]);

  // Calculamos la sugerencia de composición a partir del readiness y la reserva actual.
  // calcularSugerenciaComposicion devuelve null si ya coincide o no hay datos (no molesta).
  // Se coloca acá (post useReservaDetail) por el mismo motivo que el useEffect de arriba.
  const sugerenciaComposicion = calcularSugerenciaComposicion(readiness, reserva);

  // Cargamos el saldo a favor del cliente (cuenta corriente) al abrir la solapa Estado de Cuenta.
  // Es best-effort: si falla, el link "Ver cuenta del cliente" sigue mostrándose.
  // Dependemos de activeTab, del publicId del cliente (puede no existir), y del publicId de la reserva.
  useEffect(() => {
    const clientePublicId = reserva?.customerPublicId;
    if (activeTab !== "account" || !clientePublicId) {
      setSaldoClientePorMoneda(null);
      return;
    }
    let cancelado = false;
    setLoadingSaldoCliente(true);
    api.get(`/customers/${clientePublicId}/account`)
      .then((data) => {
        if (cancelado) return;
        // El endpoint de cuenta del cliente devuelve { summary: { creditBalanceByCurrency: [...] } }
        // Solo nos interesan las entradas con saldo a favor (amount > 0).
        const entradas = data?.summary?.creditBalanceByCurrency ?? [];
        const conSaldo = entradas.filter((e) => (e.amount ?? 0) > 0);
        setSaldoClientePorMoneda(conSaldo.length > 0 ? conSaldo : null);
      })
      .catch(() => {
        if (!cancelado) setSaldoClientePorMoneda(null); // Degradación elegante: no rompe la solapa
      })
      .finally(() => {
        if (!cancelado) setLoadingSaldoCliente(false);
      });
    // Cleanup: si el usuario cambia de tab rápido, cancelamos el setState obsoleto.
    return () => { cancelado = true; };
    // accountRefreshKey se suma a las deps para que el saldo del cliente
    // se recargue también cuando cambia la plata (cobro, factura, anulación).
    // Así la cuenta corriente refleja el estado real sin refrescar la página.
  }, [activeTab, reserva?.customerPublicId, accountRefreshKey]);

  const handleDeletePayment = async (payment) => {
    try {
      await api.delete(`/payments/${getPublicId(payment)}`);
      showSuccess("Pago eliminado correctamente");
      refrescarExtracto();
      fetchReserva();
    } catch (error) {
      showError(getApiErrorMessage(error, "Error al eliminar pago"));
    }
  };

  const handleViewReceiptPdf = async (payment) => {
    try {
      const response = await api.get(`/payments/${getPublicId(payment)}/receipt/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([response], { type: "application/pdf" }));
      window.open(url, "_blank");
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo abrir el comprobante."));
    }
  };

  const handleIssueReceipt = async (payment) => {
    try {
      await api.post(`/payments/${getPublicId(payment)}/receipt`);
      showSuccess("Comprobante emitido correctamente");
      refrescarExtracto();
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
      // Tanda 2 (2026-07-18): si el motor pide autorización (409 requiresApproval),
      // abrimos directo el mismo modal que usa Cobranzas → Movimientos en vez de
      // dejar al vendedor con un cartel de error sin ninguna salida.
      const accion = resolverAccionAlFallarComprobante(error);
      if (accion.requiereAutorizacion) {
        setApprovalContext({
          requestType: accion.requestType,
          entityType: accion.entityType,
          entityId: accion.entityId,
          entityLabel: armarEtiquetaComprobante(payment, formatCurrency),
        });
        return;
      }
      showError(getApiErrorMessage(error, "No se pudo emitir el comprobante."));
    }
  };

  const handleVoidReceipt = async (payment) => {
    const confirmed = await showConfirm({
      title: "Anular comprobante",
      text: "Esta accion marcara el comprobante como anulado. El pago sigue vigente.",
      confirmText: "Si, anular",
      confirmColor: "red",
    });
    if (!confirmed) return;
    try {
      await api.post(`/payments/${getPublicId(payment)}/receipt/void`, { reason: null });
      showSuccess("Comprobante anulado.");
      refrescarExtracto();
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
      // Mismo enganche que handleIssueReceipt: 409 requiresApproval → modal de
      // autorización directo, sin cartel intermedio (paridad con Movimientos).
      const accion = resolverAccionAlFallarComprobante(error);
      if (accion.requiereAutorizacion) {
        setApprovalContext({
          requestType: accion.requestType,
          entityType: accion.entityType,
          entityId: accion.entityId,
          entityLabel: armarEtiquetaComprobante(payment, formatCurrency),
        });
        return;
      }
      showError(getApiErrorMessage(error, "No se pudo anular el comprobante."));
    }
  };

  const handleSavePassengerCounts = async (counts) => {
    try {
      await api.patch(`/reservas/${publicId}/passenger-counts`, counts);
      showSuccess("Cantidades actualizadas");
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudieron actualizar las cantidades."));
    }
  };

  /**
   * ADR-027: el dueño da OK a los cambios de precio/costo.
   * Llama a POST /api/reservas/{id}/acknowledge-changes, que limpia el flag
   * HasUnacknowledgedChanges y registra quien/cuando acuso el cambio.
   * Tras el OK, recargamos la reserva para que el banner y el badge desaparezcan.
   */
  const handleAcknowledgeChanges = async () => {
    if (acknowledging) return;
    setAcknowledging(true);
    try {
      await api.post(`/reservas/${publicId}/acknowledge-changes`);
      showSuccess("Cambios revisados. El saldo ya está al día.");
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo confirmar el acuse. Intentá de nuevo."), "Error");
    } finally {
      setAcknowledging(false);
    }
  };

  /**
   * Flujo compartido: busca la cancelación vigente de la reserva y abre el panel indicado.
   *
   * Los tres caminos del paso de multa (Sí cobró / No cobró / Deshacer) necesitan el
   * GUID de la cancelación para llamar al endpoint correcto. Ese GUID no viene en el DTO
   * de la reserva, así que lo buscamos con GET by-reserva al momento del clic.
   *
   * Si el GUID ya fue cargado (multaCancellationPublicId != null), lo reutilizamos
   * en lugar de hacer un fetch extra — caso raro pero posible si el usuario cierra
   * y reabre un panel sin que cambie la cancelación subyacente.
   *
   * @param {Function} abrirPanel - Callback que activa el estado del panel específico.
   */
  const buscarCancelacionYAbrirPanel = async (abrirPanel) => {
    setBuscandoMulta(true);
    try {
      const cancelacion = await cancellationsApi.getByReserva(publicId);
      // Defensa ante un 200 anómalo sin publicId: no abrimos el panel en silencio.
      if (!cancelacion?.publicId) {
        showError(
          "No se encontró la cancelación de esta reserva. Actualizá la página y volvé a intentar.",
          "Sin cancelación"
        );
        return;
      }
      setMultaCancellationPublicId(cancelacion.publicId);
      // ADR-044 T4: guardamos las facturas de venta activas junto con el GUID — las
      // necesita el desplegable "¿a qué factura corresponde?" del panel de confirmar.
      setMultaSaleInvoices(Array.isArray(cancelacion.saleInvoices) ? cancelacion.saleInvoices : []);
      abrirPanel();
    } catch (error) {
      if (error?.status === 404) {
        showError(
          "No se encontró la cancelación de esta reserva. Actualizá la página y volvé a intentar.",
          "Sin cancelación"
        );
      } else {
        showError("No se pudo cargar los datos de la cancelación. Intentá de nuevo.");
      }
    } finally {
      setBuscandoMulta(false);
    }
  };

  // "Sí, el operador cobró una multa" → abre ConfirmarMultaOperadorInline (naranja, emite ND).
  // ADR-044 T1: recibe la situación COMPLETA del operador que originó el click (no solo
  // el guid) para poder pasarle supplierPublicId + la moneda sugerida al panel — ver
  // multaSituacionAbierta más arriba.
  const handleAbrirMultaConPenalidad = (situacion) => {
    setMultaSituacionAbierta(situacion ?? null);
    buscarCancelacionYAbrirPanel(() => setShowMultaInline(true));
  };

  // "No cobró nada / devolvió todo" → abre CerrarSinMultaInline (teal, solo registra motivo).
  const handleAbrirSinMulta = (situacion) => {
    setMultaSituacionAbierta(situacion ?? null);
    buscarCancelacionYAbrirPanel(() => setShowSinMultaInline(true));
  };

  // "Reabrir el paso de la multa" (spec Pieza 3, 2026-07-14) → abre
  // DeshacerCierreSinMultaInline (Admin only). El nombre de la función queda igual
  // (sigue "deshaciendo" el cierre sin multa puertas adentro) aunque el texto visible
  // haya cambiado — ver reabrirPasoMultaTextos.js para la copy exacta.
  const handleAbrirDeshacer = () =>
    buscarCancelacionYAbrirPanel(() => setShowDeshacerWaiveInline(true));

  const handleSaveReservaDates = async (payload) => {
    // Lanza si falla para que el modal muestre el error inline.
    try {
      await api.patch(`/reservas/${publicId}/dates`, payload);
      showSuccess("Fechas actualizadas");
      setShowEditDatesModal(false);
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
      const message = getApiErrorMessage(error, "No se pudieron actualizar las fechas.");
      showError(message);
      throw new Error(message);
    }
  };

  /**
   * Flujo "El cliente acepto": pasa DIRECTO a En gestion sin abrir modal de nombres.
   *
   * ADR-031 (2026-06-15): el modal de pasajeros FUE ELIMINADO del flujo de avance.
   * El único requisito para avanzar es que haya al menos 1 pasajero declarado
   * (suma de adultCount + childCount + infantCount >= 1). Los nombres se cargan
   * después, en la solapa Pasajeros o mediante el mini-formulario inline al emitir.
   *
   * Si falta la cantidad (total = 0), el botón ya está apagado en ReservaHeader
   * y el usuario no puede hacer click (validación defensiva también acá).
   *
   * NOTA: el endpoint /transition-readiness sigue existiendo en el backend y
   * el backend valida que la cantidad sea >= 1. Si hay otros bloqueos que el
   * backend retorna (reglas no-pax), los mostramos con showError.
   */
  const handleConfirmReservation = async (targetStatus = "InManagement") => {
    // Validación defensiva en el front: la suma debe ser >= 1.
    // ReservaHeader ya bloquea el botón si es 0, pero re-verificamos.
    const totalPax = (reserva?.adultCount || 0) + (reserva?.childCount || 0) + (reserva?.infantCount || 0);
    if (totalPax === 0) {
      showError("Tiene que haber al menos 1 pasajero declarado antes de continuar.");
      return;
    }

    try {
      // Primero persistimos la composición declarada (si el backend lo exige).
      // Esto garantiza que adultCount/childCount/infantCount estén guardados antes de avanzar.
      await api.patch(`/reservas/${publicId}/passenger-counts`, {
        adultCount: reserva?.adultCount || 0,
        childCount: reserva?.childCount || 0,
        infantCount: reserva?.infantCount || 0,
      });

      // Transicion directa: Budget → InManagement.
      // El modal de nombres YA NO se abre (ADR-031: los nombres se cargan después).
      await handleStatusChange(targetStatus);
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo avanzar la reserva. Revisá los datos e intentá de nuevo."));
    }
  };

  // ADR-020: en Cotizacion y Presupuesto se ocultan las tabs avanzadas (pasajeros,
  // cuenta, vouchers, documentos) porque la reserva todavia no es operativa.
  // "isEarlyStage" reemplaza al antiguo "isBudget" que solo chequeaba Budget.
  const isEarlyStage = reserva?.status === "Quotation" || reserva?.status === "Budget";

  // "congelado": controla vouchers y documentos. Se ocultan las acciones de escritura
  // (emitir, anular voucher, subir documento) en estados terminales o FullyInvoiced.
  // Decisión UX 2026-06-22: un solo criterio aplicado en las zonas A y C.
  const congelado = esEstadoCongelado(reserva);

  // "congeladoParaRecibos": controla emitir/anular el RECIBO de un cobro.
  // NO incluye FullyInvoiced porque facturación y cobranza son ejes separados (ADR-037).
  // BUG IMP-3 fix 2026-06-24: Closed ya no hereda el bloqueo de vouchers.
  const congeladoParaRecibos = esCongeladoParaRecibos(reserva);

  // Capacidad del backend para editar o eliminar cobros.
  // El backend la pone en false en Closed/terminal. Si no viene en el DTO, asumimos false
  // por seguridad (no mostramos botones que el server va a rechazar).
  // BUG IMP-3 fix 2026-06-24: reemplaza al congelado local para gobernar Editar/Eliminar.
  const puedeEditarEliminarCobro = reserva?.capabilities?.canEditOrDeletePayment?.allowed === true;

  // Contador "N de M servicios cancelados" para el ReservaHeader (ADR-025).
  // Se recalcula solo cuando cambia allServices (memoizado para no correr en cada render).
  const serviciosCancelados = useMemo(
    () => calculateServiciosCanceladosResumen(allServices),
    [allServices]
  );

  // Si el usuario esta en una tab que no se muestra en estado early-stage (por ej:
  // la reserva regresa de InManagement a Budget), redirigir a "services" para evitar
  // una pantalla en blanco.
  useEffect(() => {
    if (isEarlyStage && (activeTab === "voucher" || activeTab === "attachments" || activeTab === "account" || activeTab === "passengers")) {
      setActiveTab("services");
    }
  }, [isEarlyStage, activeTab]);

  if (loading) {
    return <div className="animate-pulse p-8 text-center text-slate-500">Cargando reserva...</div>;
  }

  if (!reserva) {
    return (
      <div className="m-8 rounded-2xl border border-slate-200 bg-white p-8 text-center dark:border-slate-800 dark:bg-slate-900">
        <h3 className="text-xl font-bold text-slate-900 dark:text-white">Reserva no encontrada</h3>
        <p className="mt-2 text-slate-500 dark:text-slate-400">No se pudo cargar la informacion. Verifica que la URL sea correcta.</p>
        <div className="mt-6 flex justify-center">
          <button onClick={() => navigate("/reservas")} className="rounded-lg bg-indigo-600 px-4 py-2 text-white shadow-sm transition-colors hover:bg-indigo-700">
            Volver a la lista
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-7xl space-y-6 p-4 sm:p-6 lg:p-8">
      <ReservaHeader
        reserva={reserva}
        onBack={() => navigate("/reservas")}
        onStatusChange={(newStatus) => {
          // ADR-020: Budget → InManagement abre el modal de pasajeros.
          // Cualquier otra transicion va directo al PUT /status sin modal.
          if (newStatus === "InManagement" && reserva.status === "Budget") {
            handleConfirmReservation("InManagement");
          } else {
            handleStatusChange(newStatus);
          }
        }}
        onRevert={() => setShowRevertModal(true)}
        onEditDates={() => setShowEditDatesModal(true)}
        onDelete={() =>
          askConfirmation({
            title: "Eliminar reserva?",
            message: "Accion irreversible. Solo aplicable a reservas sin pagos.",
            type: "danger",
            onConfirm: handleDeleteReserva,
          })
        }
        onArchive={() =>
          askConfirmation({
            title: "Archivar reserva?",
            message: "El estado pasara a 'Archivado'.",
            type: "warning",
            onConfirm: handleArchiveReserva,
          })
        }
        canCancelReserva={canCancelReserva}
        onCancelReserva={() => {
          // ADR-035 fix #1: el panel CancelarReservaInline solo se renderiza en la solapa "account".
          // Si el usuario esta en otra solapa (servicios, historial, etc.) el panel no aparece.
          // Solucion: navegar a "account" ANTES de activar el panel; el scroll es automatico
          // porque el panel se monta debajo de la barra de acciones visible en esa solapa.
          setActiveTab("account");
          setShowCancelInline(true);
        }}
        onRequestEdit={() => setShowEditAuthModal(true)}
        onMarkLost={() => setShowMarkLostModal(true)}
        onCorrectTraveling={() => setShowCorrectTravelingModal(true)}
        onReschedule={() => setShowRescheduleModal(true)}
        serviciosCancelados={serviciosCancelados}
        totalPasajerosDeclarados={
          // P2 (ADR-031): ReservaHeader lo usa para deshabilitar "El cliente aceptó" cuando no hay pax.
          (reserva?.adultCount || 0) + (reserva?.childCount || 0) + (reserva?.infantCount || 0)
        }
      />

      <ReservaSummaryStrip reserva={reserva} />

      <PartialCreditNoteEmissionPanel
        reserva={reserva}
        canEmit={hasPermission("cobranzas.invoice_annul")}
        onChanged={() => fetchReserva({ showLoading: false, preserveOnError: true })}
      />

      {/* ═══ TIRA DE AVISOS (spec UX 2026-07-05, respuestas 1C/2B/3A/4B/5A) ═══════════
          "Arriba la foto, abajo solo lo que hay que hacer": primero la FOTO del estado
          (carteles de estado terminal / en viaje — no cambian, van primero), después lo
          ACCIONABLE siempre visible (banner "con cambios" grande, candado en una línea),
          y al final lo INFORMATIVO plegado ("N avisos más"). Orden de abajo hacia abajo:
            1) Carteles de estado terminal / en viaje / pregunta de multa (sin cambios).
            2) Banner "con cambios" (ADR-027) — accionable, grande, con botón "Dar OK".
            3) Franja del candado en una línea — accionable, con botón "Pedí autorización".
            4) Barra plegada "N avisos más" — informativos (servicios sin confirmar, capacidad). */}

      {/* ─── Carteles de estado ────────────────────────────────────────────────────
          Feedback 2026-06-19: UN SOLO cartel que explica el estado actual.
          Los estados terminales (Lost/Cancelled/Closed/PendingOperatorRefund/Traveling) tienen
          un cartel de solo-lectura. Los estados activos orientan al vendedor.
          Los botones deshabilitados NO repiten el motivo — el cartel lo dice todo.
          ADR-036: "ToSettle" eliminado (ya no existe ese estado en la UI). */}

      {/* ── Estado "En viaje": solo lectura, cartel chico (ADR-036 punto 2) ── */}
      {reserva.status === "Traveling" ? (
        <div
          className="rounded-xl border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-800 dark:border-emerald-900/40 dark:bg-emerald-950/20 dark:text-emerald-200"
          data-testid="banner-estado-terminal"
          role="status"
        >
          <strong className="font-bold">✈️ Reserva en viaje</strong> — solo lectura.
        </div>
      ) : null}

      {/* ── Estados terminales: solo lectura, sin botones ni mensajitos (ADR-036 puntos 3 y 4) ── */}
      {(() => {
        // (2026-07-04) Coherencia con el auto-cierre: una anulacion SIN plata al operador ahora se cierra de una
        // aunque la multa siga sin decidir. Eso deja la reserva ya "Anulada" (Cancelled) con la pregunta de la
        // multa todavia pendiente (Pending) o ya cerrada sin multa (Waived). El paso de la multa (los dos botones
        // "Sí cobró / No cobró" y el "Deshacer") vivia SOLO dentro del cartel de "esperando reembolso"
        // (PendingOperatorRefund); si no lo mostramos tambien en Cancelled, la tarea de la multa quedaba invisible.
        // El backend ya expone la capacidad y el outcome sin importar el estado; esto es solo la compuerta de UI.
        //
        // Spec "el paso de multa vive en la ficha" (2026-07-08): reemplaza el cálculo legado
        // (que solo distinguía Pending/Waived, ignorando los estados nuevos de la Nota de
        // Débito) por el helper degradación-segura tienePasoDeMultaOperador(reserva): lee
        // reserva.operatorPenaltySituation cuando el DTO lo trae (cubre TODOS los estados
        // activos) y cae al campo legado si no lo trae.
        const hayPasoDeMultaOperadorActivo = tienePasoDeMultaOperador(reserva);

        // ADR-044 T1 (2026-07-10): una cancelación puede tener servicios de MÁS de un
        // operador (ADR-025), cada uno con su propia multa. situacionesMultaConPanel ya
        // filtra, de TODOS los operadores en juego, cuáles necesitan el panel accionable
        // (familias "accionTrabada" / "procesando" / "multiOperador" — ver
        // operatorPenaltyBanner.js). Las familias "pregunta" (PendingDecision) y "waived"
        // ya tienen su propio bloque más abajo en este mismo IIFE — no se duplican acá.
        //
        // Caso mono-operador (hoy el 100%): esta lista trae, como máximo, el mismo único
        // elemento que antes leíamos del campo singular — la ficha se ve EXACTO igual.
        const situacionesMultaConPanel = situacionesConPanelDeMulta(reserva);

        // Reemplaza al viejo cartel "Ir a resolver" (mandaba a la bandeja back-office
        // /pendientes-afip): ahora se resuelve DIRECTO en la ficha, con OperatorPenaltyStepPanel.
        // Guard de estado (heredado del cartel viejo): solo en la anulada YA CERRADA (Cancelled).
        // En PendingOperatorRefund el cartel prioritario sigue siendo el de "esperando
        // reembolso" / reintento de anulación (más urgente) — ver la rama de más abajo.
        const mostrarPasoDeMultaTrabadoOProcesando =
          reserva.status === "Cancelled" && situacionesMultaConPanel.length > 0;

        return reserva.status === "Lost" ? (
        <div
          className="rounded-xl border border-slate-200 bg-slate-100 p-4 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-400"
          data-testid="banner-estado-terminal"
          role="status"
        >
          <strong className="font-bold">Reserva perdida</strong> — solo lectura.
        </div>
      ) : mostrarPasoDeMultaTrabadoOProcesando ? (
        // ADR-044 T1: un panel POR OPERADOR con multa activa. El nombre del operador solo
        // se pasa cuando hay MÁS de un operador en juego (hayMasDeUnOperadorConMulta) —
        // en el caso mono-operador de siempre queda undefined y cada panel se ve EXACTO
        // igual que antes de este cambio. La key usa el guid del operador (o "legacy" si
        // el DTO todavía no lo trae) para que React no confunda paneles entre sí.
        <div className="space-y-3">
          {situacionesMultaConPanel.map((situacion) => (
            <OperatorPenaltyStepPanel
              key={situacion.supplierPublicId ?? "legacy"}
              reservaPublicId={publicId}
              reservaNumero={reserva.numeroReserva}
              situacion={situacion}
              nombreOperador={hayMasDeUnOperadorConMulta(reserva) ? situacion.supplierName : undefined}
              supplierPublicId={situacion.supplierPublicId}
              // Tanda D1 (2026-07-16): para el botón "Ir a la cuenta del cliente" que
              // aparece si el Deshacer se frena por saldo a favor aplicado (spec §8).
              customerPublicId={reserva.customerPublicId}
              monedaSugerida={elegirMonedaSugeridaParaMulta({
                situacionCurrency: situacion?.currency,
                porMoneda: reserva.porMoneda,
              })}
              /* Refresco SILENCIOSO de punta a punta: sin spinner (showLoading:false), sin perder lo
                 que ya está en pantalla si falla (preserveOnError) y sin toast de error (silentErrors)
                 — lo usa tanto el refresco post-acción del panel como el auto-refresco cada ~10 s de
                 la familia "procesando" (useOperatorPenaltyPolling); un tick de fondo que falla no
                 tiene que gritarle nada al usuario, el próximo tick lo reintenta solo. */
              onResuelto={() => fetchReserva({ showLoading: false, preserveOnError: true, silentErrors: true })}
            />
          ))}
        </div>
      ) : (reserva.status === "Cancelled" && !hayPasoDeMultaOperadorActivo) ? (
        // ADR-036: el estado interno sigue siendo "Cancelled" pero el usuario ve "Anulada".
        // "Cancelar" en este producto = saldar una deuda; "Anular" = deshacer el viaje.
        // (2026-07-04) Solo este cartel simple cuando NO queda paso de multa: si la multa sigue pendiente o se
        // cerro sin multa, cae en la rama de mas abajo (junto con PendingOperatorRefund) para mostrar el paso.
        <div
          className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
          data-testid="banner-estado-terminal"
          role="status"
        >
          <strong className="font-bold">Reserva anulada</strong> — solo lectura.
        </div>
      ) : reserva.status === "Closed" ? (
        <div
          className="rounded-xl border border-slate-200 bg-slate-100 p-4 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-400"
          data-testid="banner-estado-terminal"
          role="status"
        >
          <strong className="font-bold">Reserva finalizada</strong> — solo lectura.
          {/* ADR-037: ya no hay "Reabrila para facturar". La facturación se desacopló del estado:
              se factura directo desde Finalizada (botón "Emitir factura" en la solapa Cuenta). */}
        </div>
      ) : isReservaAnulada(reserva) ? (
        // (2026-07-04) Llega Cancelled aca SOLO cuando tiene paso de multa (la rama simple de arriba ya se lo
        // llevo si no lo tenia). Reusa exactamente el mismo bloque que "esperando reembolso" para no duplicar UI.
        (() => {
          // ADR-042 (2026-07-01, Estado 5 de la spec): anulación multi-factura que quedó A
          // MEDIAS. Reemplaza el cartel normal de "esperando reembolso": acá lo único accionable
          // es completar la anulación, no el paso de la multa del operador (eso viene DESPUÉS,
          // cuando termine de resolverse). La reserva queda de solo lectura salvo este botón.
          //
          // Fix reviewer (2026-07-02, punto 1): el panel de reintento usa bcSiendoReintentado
          // (una FOTO fija tomada al abrirlo), NUNCA el stuckCancellation "vivo" — ese vive
          // refrescándose y puede volverse null apenas el reintento resuelve bien, lo que
          // desmontaría el panel antes de que el vendedor llegue a ver el cartel de éxito.
          //
          // Fix reviewer (2026-07-02, punto 3): la franja NUNCA se muestra al mismo tiempo que
          // un panel de anulación abierto (ni el de reintento, ni el principal de "Anular
          // reserva" en la solapa Cuenta) — ese panel ya está mostrando su propio cartel de
          // resultado; duplicar la franja sería mostrar dos veces la misma información.
          const mostrarFranjaEnRevision = Boolean(stuckCancellation) && !showRetryCancelInline && !showCancelInline;
          const mostrarPanelDeReintento = showRetryCancelInline && bcSiendoReintentado;

          if (mostrarFranjaEnRevision || mostrarPanelDeReintento) {
            return (
              <div className="space-y-3">
                {mostrarFranjaEnRevision && (
                  <div
                    className="rounded-xl border border-orange-300 bg-orange-50 p-4 text-sm text-orange-900 dark:border-orange-700/50 dark:bg-orange-950/30 dark:text-orange-200"
                    data-testid="banner-anulacion-en-revision"
                    role="status"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
                      <span>
                        <span aria-hidden="true">🟠</span>{" "}
                        <strong className="font-bold">
                          {construirTextoFranjaEnRevision(contarNotasFaltantes(stuckCancellation.creditNotes))}
                        </strong>
                      </span>
                      <button
                        type="button"
                        onClick={() => {
                          // Foto fija del BC en el momento del click (ver comentario de arriba).
                          setBcSiendoReintentado(stuckCancellation);
                          setShowRetryCancelInline(true);
                        }}
                        data-testid="btn-reintentar-anulacion"
                        className="inline-flex items-center gap-1.5 rounded-lg border border-orange-400 bg-orange-100 px-3 py-2 text-xs font-bold text-orange-800 hover:bg-orange-200 dark:border-orange-700 dark:bg-orange-900/40 dark:text-orange-200 dark:hover:bg-orange-900/60 transition-colors flex-shrink-0"
                      >
                        Reintentar anulación
                      </button>
                    </div>
                  </div>
                )}

                {mostrarPanelDeReintento && (
                  // bookingCancellationToRetry=set → el panel arranca DIRECTO en "procesando"
                  // reintentando esa cancelación puntual; nunca pasa por el formulario, así que
                  // no necesita onCancelado (ese callback solo lo usa el flujo normal de anular).
                  <CancelarReservaInline
                    reserva={reserva}
                    bookingCancellationToRetry={bcSiendoReintentado}
                    onSilentRefresh={() => {
                      refrescarExtracto();
                      fetchReserva({ showLoading: false, preserveOnError: true });
                    }}
                    onCerrar={() => {
                      setShowRetryCancelInline(false);
                      setBcSiendoReintentado(null);
                    }}
                  />
                )}
              </div>
            );
          }

          // 2026-06-28: detectamos el estado "cerrado sin multa" desde el campo dedicado.
          // Fix 2026-06-29 (reviewer): el campo `canConfirmOperatorPenalty.reason` nunca lleva
          // "OperatorPenaltyWaived" en el backend — era un campo muerto. El backend ahora expone
          // `capabilities.operatorPenaltyOutcome` con valores "None"|"Pending"|"Confirmed"|"Waived".
          // Si el campo no llega (DTO viejo o capabilities ausente) → false → banner genérico (degradación segura).
          const yaWaived = reserva.capabilities?.operatorPenaltyOutcome === "Waived";

          if (yaWaived) {
            // ── Estado: cerrado sin multa del operador ─────────────────────────
            // Los dos botones de elección desaparecen (el paso ya se resolvió).
            // Solo el admin ve el enlace discreto "Deshacer".
            return (
              <div className="space-y-3">
                <div
                  className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
                  data-testid="banner-estado-terminal"
                  role="status"
                >
                  <strong className="font-bold">Anulada — cerrada sin multa del operador.</strong> Solo lectura.

                  {/* Rastro de CUÁNDO y QUIÉN cerró sin multa (spec "el paso de multa vive
                      en la ficha", 2026-07-08). textoRastroWaived degrada solo si el DTO
                      no trae operatorPenaltySituation (nunca rompe, muestra texto genérico). */}
                  <div className="mt-1 text-xs text-rose-700/80 dark:text-rose-300/70" data-testid="multa-waived-rastro">
                    {textoRastroWaived({
                      waivedAt: reserva.operatorPenaltySituation?.waivedAt,
                      waivedByName: reserva.operatorPenaltySituation?.waivedByName,
                      revertedAt: reserva.operatorPenaltySituation?.revertedAt,
                      revertedByName: reserva.operatorPenaltySituation?.revertedByName,
                    })}
                  </div>

                  {/* Enlace discreto "Deshacer" — SOLO para administradores.
                      Separado del texto principal para diferenciarlo visualmente.
                      Copia el patrón "Sacar de viaje" (2026-06-22): discreto, sobrio,
                      no se muestra en gris/deshabilitado para no-Admin — directamente no existe. */}
                  {isAdmin() && !showDeshacerWaiveInline && (
                    <div className="mt-2 pt-2 border-t border-rose-200/60 dark:border-rose-900/30">
                      <button
                        type="button"
                        onClick={handleAbrirDeshacer}
                        disabled={buscandoMulta}
                        data-testid="btn-deshacer-cierre-sin-multa"
                        className="inline-flex items-center gap-1.5 text-xs text-rose-600/70 hover:text-rose-700 dark:text-rose-400/60 dark:hover:text-rose-300 transition-colors disabled:opacity-50"
                        aria-label={ENLACE_REABRIR_PASO_MULTA}
                      >
                        {buscandoMulta && <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />}
                        · {ENLACE_REABRIR_PASO_MULTA}
                      </button>
                    </div>
                  )}
                </div>

                {/* Panel de deshacer (solo Admin, se monta cuando el admin hace clic) */}
                {showDeshacerWaiveInline && multaCancellationPublicId && (
                  <DeshacerCierreSinMultaInline
                    cancellationPublicId={multaCancellationPublicId}
                    reservaNumero={reserva.numeroReserva}
                    onDeshecho={() => {
                      setShowDeshacerWaiveInline(false);
                      setMultaCancellationPublicId(null);
                      showSuccess("Listo. Se reabrió el paso de la multa.");
                      // Refrescamos para que el banner cambie al estado "pendiente"
                      // y los dos botones de elección vuelvan a aparecer.
                      fetchReserva({ showLoading: false, preserveOnError: true });
                    }}
                    onCerrar={() => {
                      setShowDeshacerWaiveInline(false);
                      setMultaCancellationPublicId(null);
                    }}
                  />
                )}
              </div>
            );
          }

          // ── Estado: paso de multa pendiente (el agente todavía no eligió) ──────
          // ADR-044 T1 (2026-07-10, fix de bloqueante): antes había una sola pregunta
          // compartida por toda la cancelación, gateada por `capabilities.
          // canConfirmOperatorPenalty.allowed` (BC-level). Eso rompía apenas la
          // anulación tenía servicios de 2+ operadores (ADR-025 crea una línea POR
          // proveedor): el agente elegía "Sí cobró", completaba el formulario y el
          // backend rebotaba 409 pidiendo especificar CUÁL operador — sin salida.
          //
          // Ahora situacionesConPreguntaDeMulta ya filtra, de TODOS los operadores en
          // juego, cuáles están en "pregunta" (PendingDecision) Y son confirmables
          // (canConfirm, que combina estado + permiso — mismo criterio que el resto de
          // los booleanos can* que ya usa OperatorPenaltyStepPanel). Se dibuja un bloque
          // Sí/No POR operador, cada uno con su propio supplierPublicId.
          //
          // Caso mono-operador (hoy el 100%): esta lista trae, como máximo, el mismo
          // único elemento que antes — la ficha se ve EXACTO igual (mismo testid, sin
          // prefijo de nombre).
          const situacionesPregunta = situacionesConPreguntaDeMulta(reserva);
          const hayMasDeUnOperador = hayMasDeUnOperadorConMulta(reserva);

          return (
            <div className="space-y-3">
              {/* Cartel de estado: anulada, esperando reembolso del operador.
                  ADR-014: el paso de multa del operador puede estar pendiente de resolver.
                  Fix 2026-06-24 (H3): la capability es la fuente de verdad (no el estado).
                  2026-06-28: ahora hay DOS opciones (Sí cobró / No cobró), no una sola. */}
              <div
                className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
                data-testid="banner-estado-terminal"
                role="status"
              >
                {/* (2026-07-04) El titulo depende del estado real: si la reserva ya se cerro (Anulada/Cancelled)
                    porque no habia nada que reembolsar, decir "esperando el reembolso" seria falso — ahi el unico
                    pendiente es decidir la multa. Si sigue esperando plata del operador (PendingOperatorRefund),
                    se mantiene el texto de siempre. */}
                <strong className="font-bold">
                  {reserva.status === "Cancelled"
                    ? "Anulada — falta decidir la multa del operador"
                    : "Anulada, esperando el reembolso del operador"}
                </strong> — solo lectura.

                {/* ── Elección del agente: ¿cobró multa o no cobró? ──────────────
                    Un bloque POR OPERADOR con situación "pregunta". Solo visible cuando
                    hay al menos uno confirmable y ningún panel inline está abierto (para
                    no mostrar la pregunta encima del form) — mismo gate que antes,
                    ahora aplicado a la LISTA en vez de a un solo flag agregado. */}
                {situacionesPregunta.length > 0 && !showMultaInline && !showSinMultaInline && (
                  <div className="mt-3 pt-3 border-t border-rose-200/60 dark:border-rose-900/30 space-y-4">
                    {situacionesPregunta.map((situacion) => {
                      // El sufijo del testid solo aparece con 2+ operadores — en mono-operador
                      // el testid queda IDÉNTICO al de siempre (btn-si-cobro-multa a secas).
                      const sufijoTestId = hayMasDeUnOperador && situacion.supplierPublicId
                        ? `-${situacion.supplierPublicId}`
                        : "";
                      const nombreParaTitulo = hayMasDeUnOperador ? situacion.supplierName : undefined;
                      // ADR-044 "Deshacer una multa ya emitida" (spec sección 4): si este paso
                      // se reabrió porque alguien deshizo un comprobante ya emitido, la misma
                      // frase de rastro se repite acá — para que quien vuelve a decidir sepa
                      // POR QUÉ el paso está de nuevo abierto. `lastDebitNoteUndo` degrada solo
                      // a null cuando la multa nunca se deshizo (no se muestra nada).
                      const rastroDeshacer = textoRastroDeshacerMulta(situacion.lastDebitNoteUndo);

                      // Configuracion de multas de cancelacion (2026-07-14, spec Pieza 2):
                      // el backend YA calculó, para ESTE operador puntual, qué camino es más
                      // probable (mirando `Supplier.PenaltyBehavior` de su ficha). El front
                      // NUNCA re-deriva esto — solo traduce el string a un orden de botones +
                      // una notita. Con `suggestedPenaltyPath` null (operador en "no se sabe",
                      // el default de todo operador nuevo) el resultado deja la pantalla
                      // EXACTAMENTE como se veía antes de esta tanda.
                      const sugerencia = sugerenciaCaminoMulta(situacion.suggestedPenaltyPath);

                      // Clases de cada botón: el camino sugerido conserva su color fuerte de
                      // siempre; el otro pasa a un tono gris apagado (sigue siendo un botón
                      // normal, clickeable — regla dura de la spec: nunca se esconde ni se
                      // deshabilita el camino no sugerido, solo se le baja el volumen visual).
                      const claseBotonSiCobro = sugerencia.siResaltado
                        ? "inline-flex items-center gap-2 rounded-lg border border-orange-400 bg-orange-50 px-3 py-2 text-xs font-bold text-orange-700 hover:bg-orange-100 dark:border-orange-700 dark:bg-orange-950/30 dark:text-orange-300 dark:hover:bg-orange-900/40 transition-colors disabled:opacity-50"
                        : "inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-400 dark:hover:bg-slate-800 transition-colors disabled:opacity-50";
                      const claseBotonNoCobro = sugerencia.noResaltado
                        ? "inline-flex items-center gap-2 rounded-lg border border-teal-400 bg-teal-50 px-3 py-2 text-xs font-bold text-teal-700 hover:bg-teal-100 dark:border-teal-700 dark:bg-teal-950/30 dark:text-teal-300 dark:hover:bg-teal-900/40 transition-colors disabled:opacity-50"
                        : "inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-500 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-400 dark:hover:bg-slate-800 transition-colors disabled:opacity-50";

                      const botonSiCobro = (
                        // Sí cobró: abre el panel naranja (emite Nota de Débito)
                        <button
                          key="si-cobro"
                          type="button"
                          onClick={() => handleAbrirMultaConPenalidad(situacion)}
                          disabled={buscandoMulta}
                          data-testid={`btn-si-cobro-multa${sufijoTestId}`}
                          className={claseBotonSiCobro}
                        >
                          {buscandoMulta && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                          Sí, el operador cobró una multa
                        </button>
                      );

                      const botonNoCobro = (
                        // No cobró: abre el panel teal (cierra sin ND)
                        <button
                          key="no-cobro"
                          type="button"
                          onClick={() => handleAbrirSinMulta(situacion)}
                          disabled={buscandoMulta}
                          data-testid={`btn-no-cobro-multa${sufijoTestId}`}
                          className={claseBotonNoCobro}
                        >
                          {buscandoMulta && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                          No cobró nada / devolvió todo
                        </button>
                      );

                      return (
                        <div key={situacion.supplierPublicId ?? "legacy"}>
                          <p className="text-sm font-semibold text-rose-900 dark:text-rose-200 mb-3">
                            {tituloConNombreOperador(nombreParaTitulo, "¿El operador te cobró una multa por anular?")}
                          </p>
                          {rastroDeshacer && (
                            // FIX de exposición (revisión 2026-07-14): el motivo puede
                            // llegar a 500 caracteres — se recorta a 2 renglones visibles
                            // (line-clamp-2) para no estirar el cartel; el texto COMPLETO
                            // sigue disponible en el title (tooltip al pasar el mouse).
                            <p
                              className="text-xs text-rose-700/80 dark:text-rose-300/70 mb-3 line-clamp-2"
                              data-testid="multa-deshacer-rastro-pregunta"
                              title={rastroDeshacer}
                            >
                              {rastroDeshacer}
                            </p>
                          )}
                          {sugerencia.notita && (
                            <p
                              className="text-xs font-medium text-rose-700 dark:text-rose-300 mb-2"
                              data-testid={`multa-sugerencia-notita${sufijoTestId}`}
                            >
                              {sugerencia.notita}
                            </p>
                          )}
                          <div className="flex flex-wrap gap-3">
                            {sugerencia.ordenBotones === "noPrimero"
                              ? [botonNoCobro, botonSiCobro]
                              : [botonSiCobro, botonNoCobro]}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>

              {/* Panel inline "Sí cobró" — naranja (ADR-014 diferido: emite Nota de Débito).
                  monedaSugerida (2026-07-08): acá la multa TODAVÍA no tiene monto/moneda
                  propios (recién se está por cargar), así que la moneda de la situación
                  puntual viene null — elegirMonedaSugeridaParaMulta cae a la moneda de la
                  factura ya emitida de la reserva. Nunca se deja el default USD en
                  silencio si hay factura de la cual precargar.

                  ADR-044 T1 (2026-07-10, fix de bloqueante): supplierPublicId y
                  monedaSugerida salen de `multaSituacionAbierta` — la situación PUNTUAL
                  del operador que el agente clickeó (ver handleAbrirMultaConPenalidad).
                  En mono-operador es simplemente el único elemento posible.

                  invoiceCurrency (spec 2026-07-14 "explicación por qué la multa va en la
                  moneda de la factura"): también sale de `multaSituacionAbierta` — el DTO
                  ya trae este campo para CUALQUIER estado con cancelación en juego (no
                  solo el de "corregir"), así que acá alcanza con pasarlo tal cual. Dispara
                  la línea 2 bajo el selector de Moneda cuando el usuario elige una moneda
                  distinta de la de la factura. */}
              {showMultaInline && multaCancellationPublicId && (
                <ConfirmarMultaOperadorInline
                  cancellationPublicId={multaCancellationPublicId}
                  reservaNumero={reserva.numeroReserva}
                  supplierPublicId={multaSituacionAbierta?.supplierPublicId}
                  saleInvoices={multaSaleInvoices}
                  invoiceCurrency={multaSituacionAbierta?.invoiceCurrency}
                  monedaSugerida={elegirMonedaSugeridaParaMulta({
                    situacionCurrency: multaSituacionAbierta?.currency,
                    porMoneda: reserva.porMoneda,
                  })}
                  onConfirmado={() => {
                    setShowMultaInline(false);
                    setMultaCancellationPublicId(null);
                    setMultaSituacionAbierta(null);
                    setMultaSaleInvoices([]);
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }}
                  onCerrar={() => {
                    setShowMultaInline(false);
                    setMultaCancellationPublicId(null);
                    setMultaSituacionAbierta(null);
                    setMultaSaleInvoices([]);
                  }}
                />
              )}

              {/* Panel inline "No cobró" — teal (2026-06-28: cierra sin nota de débito).
                  ADR-044 T1: supplierPublicId también sale de multaSituacionAbierta. */}
              {showSinMultaInline && multaCancellationPublicId && (
                <CerrarSinMultaInline
                  cancellationPublicId={multaCancellationPublicId}
                  reservaNumero={reserva.numeroReserva}
                  supplierPublicId={multaSituacionAbierta?.supplierPublicId}
                  onCerrado={() => {
                    setShowSinMultaInline(false);
                    setMultaCancellationPublicId(null);
                    setMultaSituacionAbierta(null);
                    showSuccess("Listo. Se cerró sin multa del operador.");
                    // Refrescamos para que el banner cambie a "cerrada sin multa"
                    // y la capability se actualice con OperatorPenaltyWaived.
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }}
                  onCerrar={() => {
                    setShowSinMultaInline(false);
                    setMultaCancellationPublicId(null);
                    setMultaSituacionAbierta(null);
                  }}
                />
              )}
            </div>
          );
        })()
        ) : null;
      })()}

      {/* ADR-027: franja amarilla "Confirmada con cambios".
          Aparece cuando el vendedor edito precio o costo de un servicio en una reserva viva
          (InManagement/Confirmed/Traveling) y el dueño todavía no revisó el cambio.
          ADR-036: ToSettle fue eliminado.

          Detalle de pendingChanges[]: si el backend manda la lista, mostramos cada cambio
          con su descripción, campo, valores viejo→nuevo y moneda. Si no viene o viene vacía,
          mostramos el mensaje general (fallback seguro para versiones de API sin ese campo).

          El botón "Dar OK" es SOLO para administradores (isAdmin()); un no-admin ve la franja
          pero sin botón — ya puede ver el saldo actualizado y los servicios.

          Bug fix 2026-07-03: el flag hasUnacknowledgedChanges puede llegar en true
          incluso en reservas Anuladas / Esperando reembolso (el backend todavia no lo
          limpia al anular). Por eso ademas del flag exigimos que el estado sea "vivo"
          (InManagement/Confirmed/Traveling) — asi el cartel no confunde diciendo
          "confirmá este cambio" sobre un viaje que ya quedo sin efecto. */}
      {reserva.hasUnacknowledgedChanges && isReservaEnEstadoVivo(reserva.status) && (
        <div
          className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-700/50 dark:bg-amber-950/30 dark:text-amber-200"
          data-testid="banner-con-cambios"
          role="status"
          aria-live="polite"
        >
          {/* Encabezado de la franja */}
          <div className="flex flex-col sm:flex-row sm:items-start gap-3">
            <RefreshCw className="h-4 w-4 flex-shrink-0 text-amber-600 dark:text-amber-400 mt-0.5" aria-hidden="true" />
            <div className="flex-1 min-w-0">
              <span className="font-bold">Se editaron precios o costos de esta reserva.</span>
              {' '}El saldo a cobrar se actualizó automáticamente.
              {reserva.changesPendingSince && (
                <span className="ml-1 text-amber-700 dark:text-amber-300 text-xs">
                  (desde el {new Date(reserva.changesPendingSince).toLocaleDateString("es-AR", { day: "2-digit", month: "2-digit", year: "numeric" })})
                </span>
              )}

              {/* Detalle de cada cambio — solo si el backend los manda y hay al menos uno.
                  Si pendingChanges viene vacío o undefined, el fallback (mensaje general) ya está arriba. */}
              {Array.isArray(reserva.pendingChanges) && reserva.pendingChanges.length > 0 && (
                <ul
                  className="mt-2 space-y-1"
                  aria-label="Detalle de cambios pendientes de revisión"
                  data-testid="pending-changes-list"
                >
                  {reserva.pendingChanges.map((change, index) => {
                    // "SalePrice" → "precio de venta"; "NetCost" → "costo".
                    const campoLabel = change.field === "SalePrice" ? "precio de venta" : "costo";

                    // Formato de valor con moneda, o "—" si el cambio está enmascarado
                    // (el vendedor editó un costo que este usuario no puede ver).
                    const formatearValor = (value) => {
                      if (change.valuesMasked) return "—";
                      if (value == null) return "—";
                      // Usamos Intl para formatear con símbolo de moneda.
                      // No mezclamos monedas: cada cambio tiene su propia currency.
                      const currency = change.currency ?? "ARS";
                      return new Intl.NumberFormat("es-AR", {
                        style: "currency",
                        currency,
                        minimumFractionDigits: 2,
                        maximumFractionDigits: 2,
                      }).format(value);
                    };

                    return (
                      <li
                        key={index}
                        className="text-xs text-amber-800 dark:text-amber-300"
                        data-testid={`pending-change-${index}`}
                      >
                        {/* Nombre del servicio + campo + valores viejo y nuevo */}
                        <span className="font-semibold">
                          {change.serviceDescription ?? "Servicio"}
                        </span>
                        {" — "}
                        {campoLabel}:{" "}
                        <span className="line-through opacity-70">
                          {formatearValor(change.oldValue)}
                        </span>
                        {" → "}
                        <span className="font-semibold">
                          {formatearValor(change.newValue)}
                        </span>
                        {/* Quién y cuándo hizo el cambio */}
                        {change.changedByUserName && (
                          <span className="ml-1 opacity-60">
                            ({change.changedByUserName})
                          </span>
                        )}
                      </li>
                    );
                  })}
                </ul>
              )}
            </div>

            {/* Botón "Dar OK": solo visible para administradores.
                Un no-admin puede VER el saldo actualizado en los servicios pero no puede
                "limpiar" la marca — esa decisión la toma el dueño. */}
            {isAdmin() && (
              <button
                type="button"
                onClick={handleAcknowledgeChanges}
                disabled={acknowledging}
                className="flex-shrink-0 inline-flex items-center gap-1.5 rounded-lg bg-amber-600 px-4 py-2 text-sm font-bold text-white transition-colors hover:bg-amber-700 disabled:opacity-60 dark:bg-amber-700 dark:hover:bg-amber-600"
                data-testid="btn-dar-ok-cambios"
                aria-label="Marcar cambios como revisados"
              >
                {acknowledging ? (
                  <>
                    <RefreshCw className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
                    Revisando...
                  </>
                ) : (
                  <>
                    <Check className="h-3.5 w-3.5" aria-hidden="true" />
                    Dar OK
                  </>
                )}
              </button>
            )}
          </div>
        </div>
      )}

      {/* Banner ADR-020:
          - Modo regresion (naranja): cuando la reserva volvio sola a En gestion por cambio del operador.
            Se activa desde lastRegressionReason del DTO (B2 del reviewer).
          - Modo destrabada (verde): cuando hasLiveEditAuthorization=true — hay autorizacion vigente (N3).
          - Modo candado (ambar): reserva bloqueada sin autorizacion activa (decision #1).
          Prioridad: regresion > destrabada > candado.

          Cambio UX 2026-06-22: "Pedí autorización" solo aparece en Confirmada.
          En Traveling y Closed el vendedor solo ve el cartel de solo-lectura (arriba).
          NO se toca isStatusLocked: sigue bloqueando edicion en Traveling/Closed,
          pero esos estados no llegan al banner (isLocked=false les llega).

          (2026-07-05) Franja de UNA LÍNEA (spec 4B): va DESPUÉS del banner "con cambios" y
          ANTES de los avisos plegados — es lo último accionable de la tira, antes de lo
          puramente informativo. */}
      <ReservaLockBanner
        isLocked={reserva.status === "Confirmed"}
        onRequestEdit={() => setShowEditAuthModal(true)}
        hasRegressionWarning={
          // B2: franja naranja cuando la reserva esta en InManagement Y tiene motivo de regresion del backend.
          // Solo se muestra en InManagement porque es el estado al que regresa automaticamente.
          reserva.status === 'InManagement' && Boolean(reserva.lastRegressionReason)
        }
        regressionReason={reserva.lastRegressionReason ?? null}
        hasLiveEditAuthorization={reserva.hasLiveEditAuthorization ?? false}
        editAuthorizationExpiresAt={reserva.editAuthorizationExpiresAt ?? null}
      />

      {/* (2026-07-05) Banner "En corrección" ELIMINADO (spec UX, respuesta 2B): quedaba
          duplicado con el chip "En corrección" del header (ReservaStatusChips), que ya
          se enciende con la MISMA condición exacta (reserva.isUnderCorrection === true —
          verificado antes de borrar, sin achicar la condición). El aviso completo ("Se
          sacó de viaje por una corrección...") vive ahora solo como title/tooltip del
          chip; no hace falta repetirlo en un banner aparte. */}

      {/* ── Estados activos: orientan al vendedor sobre el siguiente paso ── */}
      {reserva.status === "Quotation" ? (
        <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 text-sm text-slate-700 dark:border-slate-800 dark:bg-slate-800/30 dark:text-slate-300">
          <strong className="font-bold">Cotizacion.</strong>{" "}
          Carga los servicios y pasa a Presupuesto cuando tengas el armado listo para mostrarle al cliente.
        </div>
      ) : null}

      {reserva.status === "Budget" ? (
        <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-200">
          <strong className="font-bold">Presupuesto.</strong>{" "}
          Cuando el cliente confirme, usá "El cliente aceptó" para pasar a En gestión. Los nombres de los pasajeros se cargan después.
        </div>
      ) : null}

      {/* Franja B (ADR-031, 2026-06-15): recordatorio de pasajeros en estado En gestión.
          Aparece solo cuando la reserva está en InManagement Y hay pasajeros declarados
          pero no todos tienen nombre cargado.
          Desaparece automáticamente cuando todos los slots tienen nombre (cargados === total). */}
      {(() => {
        if (reserva.status !== "InManagement") return null;
        const total = (reserva.adultCount || 0) + (reserva.childCount || 0) + (reserva.infantCount || 0);
        if (total === 0) return null;
        const cargados = (reserva.passengers || []).filter(p => p?.fullName?.trim()).length;
        if (cargados >= total) return null; // todos tienen nombre → se oculta

        return (
          <div
            className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-800/40 dark:bg-amber-950/20 dark:text-amber-200"
            data-testid="banner-pasajeros-recordatorio"
            role="status"
          >
            <div className="flex flex-col sm:flex-row sm:items-center gap-2">
              <span>
                <strong className="font-bold">Cargá los nombres de los pasajeros antes de emitir cada servicio.</strong>
              </span>
              {/* Contador "X de N" sincronizado con PassengerList (P10) */}
              <span
                className="inline-block rounded-full bg-amber-200 px-2 py-0.5 text-xs font-bold text-amber-800 dark:bg-amber-900/50 dark:text-amber-300"
                data-testid="contador-nombres-banner"
              >
                {cargados} de {total} nombres cargados
              </span>
            </div>
          </div>
        );
      })()}

      {/* (2026-07-05) Banner "Debe — no viaja" ELIMINADO (spec UX, respuesta 2B): quedaba
          duplicado con el chip rojo "Debe — no viaja" del header (ReservaStatusChips),
          que lee la MISMA fuente única de plata (getMoneyStatus, ver moneyStatus.js) con
          la MISMA condición (Confirmed + deuda + dentro de la ventana de aviso) —
          verificado antes de borrar. Caso límite revisado: si además el viaje ya venció
          (hasOverdueDebt), el chip del eje Viaje muestra "Vencida con deuda" en su lugar
          — sigue avisando, con más precisión que el banner viejo (que no distinguía). */}

      {/* Tira de avisos INFORMATIVOS (spec UX 2026-07-05, respuesta 5A): "servicios sin
          confirmar" y "capacidad excedida" no piden ninguna acción inmediata, así que
          van plegados por defecto en "N avisos más" — no compiten con el candado ni con
          el banner "con cambios" de arriba. Si hay uno solo, se muestra directo (menos
          fricción que abrir un plegado de un solo ítem). */}
      {(() => {
        const paxCount = reserva.passengers?.length || 0;
        // Una sola llamada al helper puro: decide qué avisos corresponden sin repetir
        // la lógica de "hay que mostrar esto" (esa lógica vive en avisosFicha.js).
        const clavesAvisos = construirAvisosInformativos({ reserva, paxCount, capacity });

        const avisos = [];
        if (clavesAvisos.includes("serviciosSinConfirmar")) {
          avisos.push({ key: "servicios-sin-confirmar", node: <UnconfirmedServicesBanner reserva={reserva} /> });
        }
        if (clavesAvisos.includes("capacidad")) {
          avisos.push({ key: "capacidad", node: <CapacityWarning paxCount={paxCount} capacity={capacity} /> });
        }

        return <AvisosPlegadosBar avisos={avisos} />;
      })()}

      {(getRelatedPublicId(reserva, "sourceLeadPublicId", "sourceLeadId") || getRelatedPublicId(reserva, "sourceQuotePublicId", "sourceQuoteId")) ? (
        <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <div className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
            <div>
              <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Origen comercial</div>
              <div className="mt-1 text-sm text-slate-600 dark:text-slate-300">
                Esta reserva conserva la trazabilidad de la gestion comercial que la genero.
              </div>
            </div>
            <div className="flex flex-wrap gap-3">
              {getRelatedPublicId(reserva, "sourceLeadPublicId", "sourceLeadId") ? (
                <button
                  onClick={() => navigate("/crm", { state: { openLeadId: getRelatedPublicId(reserva, "sourceLeadPublicId", "sourceLeadId") } })}
                  className="rounded-xl bg-slate-100 px-4 py-2.5 text-sm font-bold text-slate-700 transition-colors hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700"
                >
                  Abrir posible cliente asociado
                </button>
              ) : null}
              {getRelatedPublicId(reserva, "sourceQuotePublicId", "sourceQuoteId") ? (
                <button
                  onClick={() => navigate("/quotes", { state: { openQuoteId: getRelatedPublicId(reserva, "sourceQuotePublicId", "sourceQuoteId") } })}
                  className="rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-bold text-white transition-colors hover:bg-indigo-700"
                >
                  Abrir cotizacion origen
                </button>
              ) : null}
            </div>
          </div>
        </div>
      ) : null}

      <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
        <div className="border-b border-slate-100 bg-slate-50/30 px-4 dark:border-slate-800 dark:bg-slate-800/20 sm:px-6">
          <nav className="scrollbar-hide flex gap-8 overflow-x-auto">
            {[
              { id: "services", label: "Servicios", icon: FileText },
              // En Cotizacion y Presupuesto (isEarlyStage) no mostramos tabs operativas:
              // los pasajeros nominales, pagos, vouchers y documentos solo tienen sentido
              // cuando la reserva paso a En gestion (el cliente confirmo).
              isEarlyStage
                ? null
                : (() => {
                    // ADR-031: el tab muestra "X de N" cuando hay nombres faltantes (P10).
                    // Si todos tienen nombre, muestra la cantidad total normal.
                    const totalDeclaradoPax = (reserva.adultCount || 0) + (reserva.childCount || 0) + (reserva.infantCount || 0);
                    const cargadosPax = (reserva.passengers || []).filter(p => p?.fullName?.trim()).length;
                    const labelPax = totalDeclaradoPax > 0 && cargadosPax < totalDeclaradoPax
                        ? `Pasajeros (${cargadosPax}/${totalDeclaradoPax})`
                        : `Pasajeros (${reserva.passengers?.length || 0})`;
                    return { id: "passengers", label: labelPax, icon: Users };
                  })(),
              { id: "history", label: "Historial", icon: Clock },
              isEarlyStage ? null : { id: "account", label: "Estado de Cuenta", icon: CreditCard },
              isEarlyStage ? null : { id: "voucher", label: "Vouchers", icon: FileText },
              isEarlyStage ? null : { id: "attachments", label: "Documentos", icon: Paperclip },
            ].filter(Boolean).map((tab) => (
              <button
                key={tab.id}
                onClick={() => setActiveTab(tab.id)}
                className={`relative flex items-center gap-2 whitespace-nowrap py-4 text-sm font-semibold transition-all ${
                  activeTab === tab.id
                    ? "text-indigo-600 dark:text-indigo-400"
                    : "text-slate-500 hover:text-slate-700 dark:text-slate-400 dark:hover:text-slate-200"
                }`}
              >
                <tab.icon className={`h-4 w-4 ${activeTab === tab.id ? "animate-bounce" : ""}`} />
                {tab.label}
                {activeTab === tab.id ? <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-t-full bg-indigo-600 dark:bg-indigo-400" /> : null}
              </button>
            ))}
          </nav>
        </div>

        <div className="p-4 sm:p-6 lg:p-8">
          {activeTab === "services" ? (
            <div className="space-y-6">
              {/* ADR-031: en Cotizacion/Presupuesto se carga la CANTIDAD de pasajeros aca
                  (los nombres van despues, por servicio). Sin esto el total queda en 0 y el
                  boton "El cliente acepto" no se habilita. La solapa Pasajeros se redirige a
                  Servicios en etapa temprana, asi que este es el lugar para cargar la cantidad. */}
              {isEarlyStage && (
                <div className="rounded-2xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
                  <h3 className="mb-4 text-sm font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                    Pasajeros del viaje
                  </h3>
                  <PassengerCountsWidget
                    key={`pax-counts-${reserva?.publicId}`}
                    initial={{
                      adultCount: reserva?.adultCount || 0,
                      childCount: reserva?.childCount || 0,
                      infantCount: reserva?.infantCount || 0,
                    }}
                    onSave={handleSavePassengerCounts}
                  />
                </div>
              )}
              <ServiceList
                services={allServices}
                serviceCollectionErrors={serviceCollectionErrors}
                reservaId={publicId}
                reservaStatus={reserva?.status}
                // ADR-031: pasamos el objeto reserva completo para el hint de pasajeros
                reserva={reserva}
                // Guía UX 2026-06-22: las capabilities gobiernan qué botones de escritura
                // se muestran en la lista de servicios. El backend ya apaga canEditServices
                // y canCancel en estados de solo lectura (Traveling/Closed/Lost/Cancelled/PendingOperatorRefund).
                // Si reserva aún no cargó (null), pasamos null → degradación elegante (muestra botones).
                capabilities={reserva?.capabilities ?? null}
                isCatalogFindOrCreateEnabled={isCatalogFindOrCreateEnabled}
                isServiceDeadlineAlertsEnabled={isServiceDeadlineAlertsEnabled}
                windowDays={windowDays}
                esMultimoneda={reserva?.esMultimoneda || false}
                onServiceConfirmed={(servicioActualizado, recordKind) => {
                  // El DTO devuelto por confirm-cost no trae recordKind (lo agrega el front al normalizar).
                  // ServiceList lo pasa como segundo argumento para saber en qué colección hacer el upsert.
                  if (recordKind) {
                    handleServiceUpdated(servicioActualizado, recordKind);
                  } else {
                    // Fallback defensivo: si no viene recordKind, recargamos silencioso
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }
                }}
                onServiceResolved={() => {
                  // Cuando un servicio se resuelve (marca emitido / no requiere confirmacion),
                  // recargamos la reserva para actualizar el estado y el resumen de resolucion.
                  fetchReserva({ showLoading: false, preserveOnError: true });
                }}
                onAddService={() => {
                  if (isCatalogFindOrCreateEnabled) {
                    // Ficha en línea (ADR-017): se abre debajo de la lista, sin modal
                    setServiceToEditInline(null);
                    setShowInlineCard(true);
                  } else {
                    // Modal viejo: comportamiento intacto con flag OFF
                    setServiceToEdit(null);
                    setShowServiceModal(true);
                  }
                }}
                onEditService={(service) => {
                  // F2 parte 2: la ficha inline maneja los 5 tipos específicos.
                  // El único tipo que sigue en el modal viejo es "generic" (ServicioReserva),
                  // porque no tiene un endpoint propio por tipo ni buscador de catálogo.
                  const esGenerico = service?.recordKind === "generic";
                  if (isCatalogFindOrCreateEnabled && !esGenerico) {
                    setServiceToEditInline(service);
                    setShowInlineCard(true);
                  } else {
                    setServiceToEdit(service);
                    setShowServiceModal(true);
                  }
                }}
                onDeleteService={(service) => handleDeleteService(service)}
                onCancelService={(service, motivo, creditSelection) => handleCancelService(service, motivo, creditSelection)}
                saleInvoices={activeSaleInvoices}
                onIrAFacturas={() => setActiveTab("account")}
                // ADR-025: "Cancelar varios" en línea.
                // canCancelServices: gate UI-only; el server siempre re-valida.
                canCancelServices={canCancelReserva}
                // serviceCancellationBlockReason: viene del DTO de la reserva.
                // Si no es null, toda la reserva tiene un bloqueo fiscal activo.
                serviceCancellationBlockReason={reserva?.serviceCancellationBlockReason ?? null}
                onCancelacionVariosTerminada={() => {
                    // Al terminar la tanda, recargamos la reserva para reflejar
                    // el nuevo estado de los servicios y el contador "N de M".
                    fetchReserva({ showLoading: false, preserveOnError: true });
                }}
                // ADR-031: cuando el vendedor guarda un pasajero desde el mini-formulario
                // inline (pantalla D o E), recargamos la reserva para actualizar el hint
                // y el contador de nombres en la franja recordatoria.
                onPasajeroGuardado={() => {
                    fetchReserva({ showLoading: false, preserveOnError: true });
                }}
                // ADR-031 v2.1 — Pieza A: pasajeros con nombre para el control "Para: Todos".
                // Filtramos la lista de pasajeros de la reserva por los que ya tienen fullName.
                pasajerosConNombre={(reserva?.passengers || []).filter(p => p?.fullName?.trim())}
              />

              {/* Ficha de carga en línea (ADR-017): solo aparece con EnableCatalogFindOrCreate ON.
                  Se monta debajo de la lista de servicios cuando el usuario hace clic en
                  "Agregar Servicio" o en el lápiz de editar. Con flag OFF nunca se renderiza. */}
              {isCatalogFindOrCreateEnabled && showInlineCard && (
                <ServiceInlineCard
                  reservaId={publicId}
                  serviceToEdit={serviceToEditInline}
                  suppliers={suppliers}
                  onGuardado={(options) => {
                    setShowInlineCard(false);
                    setServiceToEditInline(null);
                    fetchReserva(options);
                  }}
                  onCancelar={() => {
                    setShowInlineCard(false);
                    setServiceToEditInline(null);
                  }}
                />
              )}
              {/* ADR-031 (2026-06-15): PassengerAssignmentsPanel eliminado.
                  La asignación es AUTOMÁTICA — todos los pasajeros van a todos los servicios.
                  No hay paso manual de "elegir a mano quién va en cada servicio" (P7). */}
            </div>
          ) : null}

          {activeTab === "passengers" && !isEarlyStage ? (
            <PassengerList
              reserva={reserva}
              reservaId={publicId}
              // ADR-035 feedback 2026-06-19: en estados terminales (Lost/Cancelled/Closed)
              // los botones de pasajeros se ocultan. La capability viene del backend.
              // Degradación elegante: si no hay capabilities, se permite editar (comportamiento previo).
              canEditPassengers={reserva?.capabilities?.canEditPassengers?.allowed ?? true}
              onPasajeroGuardado={() => {
                // Recargar la reserva para actualizar el snapshot de pasajeros
                // y que el contador y los hints queden al día.
                fetchReserva({ showLoading: false, preserveOnError: true });
              }}
              onAddPassenger={() => {
                setEditingPassenger(null);
                setShowPassengerForm(true);
              }}
              onEditPassenger={(passenger) => {
                setEditingPassenger(passenger);
                setShowPassengerForm(true);
              }}
              // ADR-031 v2.1 — Pieza C: sugerencia de composición desde los servicios.
              // sugerenciaComposicion es null cuando ya coincide con lo actual (franja no aparece).
              sugerenciaComposicion={sugerenciaComposicion}
              onUsarSugerencia={(counts) => {
                // El vendedor apretó [Usar]: actualizamos los casilleros con la sugerencia.
                // Usamos el mismo handler que el widget de cantidades de ReservaHeader.
                handleSavePassengerCounts(counts);
              }}
              onDeletePassenger={(passengerId) =>
                askConfirmation({
                  title: "Eliminar pasajero?",
                  message: "Estas seguro de eliminar este pasajero de la reserva?",
                  type: "danger",
                  onConfirm: () => handleDeletePassenger(passengerId),
                })
              }
            />
          ) : null}

          {activeTab === "history" ? <ReservaTimeline reservaId={publicId} /> : null}
          {/* canUploadDocument: capability del backend (B3, 2026-06-24).
              En estados terminales (Finalizada/Anulada/Perdida/Esperando reembolso) = false.
              La zona de carga y los botones Renombrar/Eliminar se ocultan.
              Ver y descargar documentos ya cargados sigue disponible. */}
          {activeTab === "attachments" ? (
            <ReservaDocumentsTab
              reservaId={publicId}
              canUploadDocument={reserva?.capabilities?.canUploadDocument ?? null}
            />
          ) : null}
          {/* soloLectura: en estados congelados no se puede emitir ni anular vouchers.
              canEmitVoucher: capability del backend (G6, 2026-06-24).
              En Finalizada (Closed) = false: el viaje terminó, no se emiten vouchers nuevos. */}
          {activeTab === "voucher" ? (
            <ReservaVoucherTab
              reservaId={publicId}
              reserva={reserva}
              soloLectura={congelado}
              canEmitVoucher={reserva?.capabilities?.canEmitVoucher ?? null}
            />
          ) : null}

          {activeTab === "account" ? (
            <div className="animate-in fade-in space-y-6 duration-500">

              {/* Barra de acciones: "Registrar cobro", "Emitir factura" y "Cancelar reserva".
                  ADR-035: los botones se muestran SIEMPRE (apagados si la accion no aplica).
                  Solo una ficha inline abierta a la vez (cobro, factura o cancelacion). */}
              {!showCobroInline && !showFacturaInline && !showCancelInline && (() => {
                // Leemos capabilities del DTO para apagar botones con motivo (ADR-035).
                // Degradacion elegante: si no hay capabilities, todos los botones van habilitados.
                const capRegPago = reserva.capabilities?.canRegisterPayment;
                const capFactura = reserva.capabilities?.canInvoiceSale;
                const capCancelar = reserva.capabilities?.canCancel;
                const capAnular = reserva.capabilities?.canAnnul;

                const registroPagoHabilitado = !capRegPago || capRegPago.allowed;
                const facturaHabilitada = !capFactura || capFactura.allowed;
                // F4-2 (2026-06-26): el botón "Anular reserva" usa canAnnul como capacidad primaria.
                //   canAnnul.allowed=true  → reserva con plata viva → emite NC formal.
                //   canCancel.allowed=true → baja simple (sin documentos fiscales vivos).
                // Se muestra cuando CUALQUIERA de las dos permite la acción.
                // Degradación elegante: si no vienen capabilities, caemos al permiso de usuario.
                const puedeAnularFormal = capAnular?.allowed ?? false;
                const puedeEliminarSimple = capCancelar?.allowed ?? false;
                const cancelarHabilitado =
                  canCancelReserva && (!capAnular && !capCancelar
                    ? true               // DTO viejo sin capabilities → permitir si tiene el permiso
                    : puedeAnularFormal || puedeEliminarSimple);

                // Mostramos cada acción SOLO si está disponible. En estados de solo lectura
                // (En viaje, Finalizada, etc.) el backend apaga la capability → el botón NO
                // aparece. Nada de botón gris con un mensajito rojo debajo: el cartel de
                // solo-lectura de arriba ya explica el porqué (decisión Gaston 2026-06-22,
                // coherente con "un solo cartel arriba, nunca motivos pegados a cada botón").
                // (2026-06-24): si ya hay una factura EN PROCESO (encolada, esperando CAE), NO ofrecemos
                // "Emitir factura": el estado de facturación todavía no la cuenta (solo cuenta las que tienen
                // CAE), así que sin esto el botón seguiría visible y el usuario reemitiría otra (rebota 409).
                // En su lugar mostramos un cartel "Factura en proceso" para que sepa que ya está en camino.
                const facturaEnProceso = reserva.hasInvoiceInProgress === true;
                const mostrarFactura = reserva.invoicingStatus !== 'FullyInvoiced' && facturaHabilitada && !facturaEnProceso;
                const mostrarCancelar = canCancelReserva && cancelarHabilitado;
                if (!registroPagoHabilitado && !mostrarFactura && !mostrarCancelar && !facturaEnProceso) return null;

                return (
                  <div className="flex flex-wrap items-center gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-800/50">

                    {registroPagoHabilitado && (
                      <button
                        onClick={() => { setCobroAEditar(null); setShowCobroInline(true); }}
                        className="flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white transition-all hover:bg-emerald-700"
                        data-testid="btn-registrar-cobro"
                      >
                        <Plus className="w-4 h-4" /> Registrar cobro
                      </button>
                    )}

                    {/* Emitir factura — ADR-037: facturación desacoplada del estado (habilitada en
                        Confirmada/En viaje/Finalizada). Si ya está facturada del todo, no se muestra. */}
                    {mostrarFactura && (
                      <button
                        onClick={() => setShowFacturaInline(true)}
                        className="flex items-center gap-2 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-bold text-white transition-all hover:bg-indigo-700"
                        data-testid="btn-emitir-factura"
                      >
                        <FileText className="w-4 h-4" /> Emitir factura
                      </button>
                    )}

                    {/* Factura en proceso (2026-06-24): cartel NO clickeable. Reemplaza al botón
                        "Emitir factura" mientras la factura está encolada esperando el CAE de AFIP/ARCA.
                        Evita que el usuario reemita una segunda factura sin saber que ya hay una en camino. */}
                    {facturaEnProceso && (
                      <div
                        className="flex items-center gap-2 rounded-lg border border-amber-300 bg-amber-50 px-4 py-2 text-sm font-bold text-amber-800 dark:border-amber-700 dark:bg-amber-950/30 dark:text-amber-300"
                        data-testid="factura-en-proceso-pill"
                        role="status"
                        aria-live="polite"
                        title="La factura ya se envió a AFIP/ARCA y está esperando el CAE. No hace falta volver a emitirla."
                      >
                        <Loader2 className="w-4 h-4 animate-spin" aria-hidden="true" />
                        Factura en proceso (esperando AFIP/ARCA)
                      </div>
                    )}

                    {/* F4-2: texto "Anular reserva" (ADR-036: "anular" = deshacer el viaje,
                        no confundir con "cancelar" = saldar deuda). testid unificado
                        con el botón del encabezado para poder referenciarlo en tests. */}
                    {mostrarCancelar && (
                      <button
                        onClick={() => setShowCancelInline(true)}
                        className="flex items-center gap-2 rounded-lg bg-rose-600 px-4 py-2 text-sm font-bold text-white transition-all hover:bg-rose-700"
                        data-testid="btn-anular-reserva"
                      >
                        <Ban className="w-4 h-4" /> Anular reserva
                      </button>
                    )}
                  </div>
                );
              })()}

              {/* Ficha inline de cobro: se despliega aquí, debajo de la barra de acciones */}
              {showCobroInline && (
                <RegistrarCobroInline
                  reservaId={publicId}
                  reserva={reserva}
                  paymentToEdit={cobroAEditar}
                  onGuardado={() => {
                    setShowCobroInline(false);
                    setCobroAEditar(null);
                    // Refresco del extracto: el cobro ya está persistido en el backend;
                    // refrescarExtracto() hace que el libro mayor se recargue junto con la reserva.
                    refrescarExtracto();
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }}
                  onCancelar={() => {
                    setShowCobroInline(false);
                    setCobroAEditar(null);
                  }}
                />
              )}

              {/* Ficha inline de factura AFIP (2026-06-13): los renglones se precargan
                  desde los servicios confirmados vía GET /invoices/reserva/{id}/suggested-items.
                  El usuario puede editar antes de emitir. No bloquea la emisión si descuadra. */}
              {showFacturaInline && (
                <EmitirFacturaInline
                  reservaId={publicId}
                  reserva={reserva}
                  clientName={reserva?.customerName ?? reserva?.client?.fullName ?? null}
                  clientCuit={reserva?.customerCuit ?? reserva?.client?.cuit ?? null}
                  onFacturaEmitida={() => {
                    setShowFacturaInline(false);
                    // La factura recién emitida aparece en el extracto sin refrescar la página.
                    refrescarExtracto();
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }}
                  onCancelar={() => setShowFacturaInline(false)}
                />
              )}

              {/* Panel inline de cancelacion (ADR-035, 2026-06-19).
                  Reemplaza al modal flotante para el flujo de cancelacion en la solapa Estado de Cuenta.
                  Solo una ficha inline abierta a la vez: si este esta abierto, la barra de acciones
                  (cobro/factura) se oculta (condicion !showCancelInline ya esta en la barra arriba). */}
              {showCancelInline && reserva && (
                <CancelarReservaInline
                  reserva={reserva}
                  onCancelado={() => {
                    setShowCancelInline(false);
                    // La anulación genera NC y ND que deben aparecer en el extracto de inmediato.
                    refrescarExtracto();
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }}
                  onCerrar={() => setShowCancelInline(false)}
                  // ADR-042: en el flujo multi-factura, el panel queda abierto mostrando el
                  // cartel de éxito/revisión (no se cierra solo) — este callback refresca la
                  // reserva/extracto en segundo plano apenas se sabe el resultado de las notas.
                  onSilentRefresh={() => {
                    refrescarExtracto();
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }}
                  // Tanda 3 "contrato pantalla-motor" (2026-07-20): botón "Emitir factura" del
                  // freno de plata R1. Cierra este panel y abre el de emisión de factura que ya
                  // existe en la ficha — mismo patrón que el resto de los paneles inline (solo
                  // uno abierto a la vez).
                  onIrAEmitirFactura={() => {
                    setShowCancelInline(false);
                    setShowFacturaInline(true);
                  }}
                />
              )}

              {/* ── Franja de 3 ejes: Venta/Facturación · Cobranza · Costo/Margen ── */}
              <EstadoCuentaResumen
                reserva={reserva}
                saldoClientePorMoneda={saldoClientePorMoneda}
                loadingSaldoCliente={loadingSaldoCliente}
              />

              {/* ── Extracto único (libro mayor) ──────────────────────────────── */}
              <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
                <div className="flex items-center gap-2 border-b border-slate-100 bg-slate-50/30 px-6 py-4 dark:border-slate-800 dark:bg-slate-800/10">
                  <History className="w-4 h-4 text-emerald-500" />
                  <h4 className="text-sm font-bold uppercase tracking-wider text-slate-900 dark:text-white">
                    Extracto de cuenta
                  </h4>
                </div>
                <div className="p-4 md:p-6">
                  <EstadoCuentaExtracto
                    reservaPublicId={publicId}
                    reserva={reserva}
                    congelado={congeladoParaRecibos}
                    refreshKey={accountRefreshKey}
                    // H2 Paso 5: pasamos reserva para que InvoicePdfActions pueda enviar al cliente
                    renderAccionesFactura={(invoice) => <InvoicePdfActions invoice={invoice} reserva={reserva} />}
                    renderAccionesCobro={(payment, estaCongeladoParaRecibo) => (
                      <PaymentReceiptActions
                        payment={payment}
                        onView={handleViewReceiptPdf}
                        onIssue={handleIssueReceipt}
                        onVoid={handleVoidReceipt}
                        // congelado controla SOLO emitir/anular el recibo de comprobante.
                        // BUG IMP-3 fix 2026-06-24: ya no incluye FullyInvoiced (ADR-037).
                        congelado={estaCongeladoParaRecibo}
                        // canEditarEliminar gobierna los botones Editar y Eliminar del cobro.
                        // Viene de la capacidad real del backend: false en Closed y otros estados terminales.
                        canEditarEliminar={puedeEditarEliminarCobro}
                        onEditarCobro={(pago) => {
                          // Abre RegistrarCobroInline en modo edición con este pago cargado.
                          // setCobroAEditar + setShowCobroInline siguen el patrón de "Registrar cobro".
                          setCobroAEditar(pago);
                          setShowCobroInline(true);
                        }}
                        onEliminarCobro={(pago) => {
                          // Pedimos confirmación antes de eliminar (la acción es irreversible).
                          // Tras eliminar: refrescamos el extracto Y la reserva (igual que handleDeletePayment).
                          askConfirmation({
                            title: "Eliminar cobro",
                            message: "Esta acción es irreversible. El pago quedará eliminado de la reserva.",
                            type: "danger",
                            onConfirm: () => handleDeletePayment(pago),
                          });
                        }}
                      />
                    )}
                  />
                </div>
              </div>
            </div>
          ) : null}
        </div>
      </div>

      <ServiceFormModal
        isOpen={showServiceModal}
        onClose={() => setShowServiceModal(false)}
        reservaId={publicId}
        reservaStatus={reserva?.status}
        reservaPax={reserva?.passengers || []}
        serviceToEdit={serviceToEdit}
        onSuccess={(options) => fetchReserva(options)}
        suppliers={suppliers}
      />

      <PassengerFormModal
        isOpen={showPassengerForm}
        onClose={() => {
          setShowPassengerForm(false);
          setEditingPassenger(null);
        }}
        reservaId={publicId}
        passengerToEdit={editingPassenger}
        onSuccess={(options) => fetchReserva({ ...options, showLoading: false, preserveOnError: true })}
      />

      <ConfirmModal
        isOpen={confirmConfig.isOpen}
        title={confirmConfig.title}
        message={confirmConfig.message}
        type={confirmConfig.type}
        onConfirm={confirmConfig.onConfirm}
        isLoading={confirmConfig.isLoading}
        onClose={() => {
          // Solo permitimos cerrar el modal si no hay una operacion en curso.
          // Si isLoading=true, el usuario ya confirmo y estamos esperando al servidor.
          if (!confirmConfig.isLoading) {
            setConfirmConfig((prev) => ({ ...prev, isOpen: false }));
          }
        }}
      />

      {showRevertModal && (
        // Modal genérico de "Volver atrás" / revertir estado (ADR-037: ya no existe "Reabrir
        // para facturar"). forceReason=true: el motivo es obligatorio para todos (acción sensible,
        // queda auditada). El backend expone los destinos válidos en allowedRevert; el modal
        // auto-selecciona si solo hay una opción.
        <RevertStatusModal
          reserva={reserva}
          onClose={() => setShowRevertModal(false)}
          onReverted={() => {
            setShowRevertModal(false);
            fetchReserva({ showLoading: false, preserveOnError: true });
          }}
          forceReason
        />
      )}

      <EditReservaDatesModal
        isOpen={showEditDatesModal}
        reserva={reserva}
        onClose={() => setShowEditDatesModal(false)}
        onSave={handleSaveReservaDates}
      />

      {/* ADR-020 F4: modal para solicitar autorizacion de edicion en reservas bloqueadas. */}
      {showEditAuthModal && (
        <EditAuthorizationModal
          reservaPublicId={publicId}
          onClose={() => setShowEditAuthModal(false)}
          onAuthorized={() => {
            setShowEditAuthModal(false);
            // Recargamos para que el backend actualice el estado de bloqueo si corresponde.
            fetchReserva({ showLoading: false, preserveOnError: true });
          }}
        />
      )}

      {/* ADR-020: modal para marcar la reserva como Perdida (solo desde Cotizacion o Presupuesto). */}
      {showMarkLostModal && (
        <MarkLostModal
          reservaPublicId={publicId}
          onClose={() => setShowMarkLostModal(false)}
          onMarked={() => {
            setShowMarkLostModal(false);
            fetchReserva({ showLoading: false, preserveOnError: true });
          }}
        />
      )}

      {/* Tanda 2 (2026-06-22): modal "Sacar de viaje" — corrección de entrada errónea a En viaje.
          Solo Admin + Traveling + canCorrectTravelingEntry.allowed=true (sin factura/voucher vivo).
          Al éxito recargamos la reserva para mostrar el banner "En corrección" y el chip. */}
      {showCorrectTravelingModal && (
        <CorregirEntradaViajeModal
          reservaPublicId={publicId}
          onClose={() => setShowCorrectTravelingModal(false)}
          onCorregida={() => {
            setShowCorrectTravelingModal(false);
            // Feedback breve antes de recargar. El banner "En corrección" aparece en la reserva recargada.
            showSuccess('Reserva sacada de viaje. Volvió a Confirmada. Acordate de revisar la fecha del servicio.');
            fetchReserva({ showLoading: false, preserveOnError: true });
          }}
        />
      )}

      {/* Tanda 3 (2026-06-23): modal "Reprogramar viaje" — mueve todas las fechas de servicios.
          El botón está en ReservaHeader, visible cuando canEditServices.allowed=true.
          Al éxito recargamos la reserva para reflejar las nuevas fechas. */}
      <ReprogramarViajeModal
        isOpen={showRescheduleModal}
        reserva={reserva}
        onClose={() => setShowRescheduleModal(false)}
        onReprogramada={(nuevaSalida) => {
          setShowRescheduleModal(false);
          showSuccess(`Viaje reprogramado. Nueva salida: ${nuevaSalida}.`);
          fetchReserva({ showLoading: false, preserveOnError: true });
        }}
      />

      {/* Tanda 2 contrato pantalla-motor (2026-07-18): modal "Solicitar aprobación"
          cuando emitir o anular el comprobante de un cobro requiere autorización.
          Mismo componente y mismo comportamiento que Cobranzas → Movimientos: sin
          cartel intermedio, se abre directo apenas el motor lo pide. */}
      <RequestApprovalModal
        isOpen={Boolean(approvalContext)}
        onClose={() => setApprovalContext(null)}
        onCreated={() => {
          // El vendedor ya ve la confirmación adentro del propio modal (showSuccess
          // interno). Cerramos el modal; el reintento de emitir/anular lo hace él
          // manualmente cuando el Administrador o Colaborador apruebe la solicitud.
          setApprovalContext(null);
        }}
        requestType={approvalContext?.requestType}
        entityType={approvalContext?.entityType}
        entityId={approvalContext?.entityId}
        entityLabel={approvalContext?.entityLabel}
      />
    </div>
  );
}
