import { useEffect, useState, useMemo } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Clock, CreditCard, Download, Eye, ExternalLink, FileText, History, Paperclip, Receipt, Users, Trash2, Edit2, Plus, RefreshCw, Check, Ban } from "lucide-react";
import { api } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import ReservaTimeline from "../../../components/ReservaTimeline";
import ConfirmModal from "../../../components/ConfirmModal";
import PassengerFormModal from "../../../components/PassengerFormModal";
import { ReservaDocumentsTab } from "../../../components/ReservaDocumentsTab";
import ServiceFormModal from "../../../components/ServiceFormModal";
import { ServiceInlineCard } from "../inline-service/ServiceInlineCard";
import { ReservaVoucherTab } from "../../../components/ReservaVoucherTab";
import {
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId, getRelatedPublicId } from "../../../lib/publicIds";
import { CapacityWarning } from "../components/CapacityWarning";
import { EditReservaDatesModal } from "../components/EditReservaDatesModal";
import { PassengerList } from "../components/PassengerList";
import { ReservaHeader } from "../components/ReservaHeader";
import { ReservaLockBanner } from "../components/ReservaLockBanner";
import { ReservaSummaryStrip } from "../components/ReservaSummaryStrip";
import { RegistrarCobroInline } from "../components/RegistrarCobroInline";
import { EmitirFacturaInline } from "../components/EmitirFacturaInline";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { RevertStatusModal } from "../components/RevertStatusModal";
import { ServiceList, calculateServiciosCanceladosResumen } from "../components/ServiceList";
import { EditAuthorizationModal } from "../components/EditAuthorizationModal";
import { MarkLostModal } from "../components/MarkLostModal";
import { isStatusLocked } from "../components/ReservaStatusBadge";
import { useReservaDetail } from "../hooks/useReservaDetail";
import { useOperationalFlags } from "../../../contexts/OperationalFlagsContext";
import { useAlerts } from "../../../contexts/AlertsContext";
import { CancelarReservaInline } from "../../cancellations/components/CancelarReservaInline";
import { hasPermission, isAdmin } from "../../../auth";
import { calcularSugerenciaComposicion } from "../lib/pasajeroHint";

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

// Botones para ver/descargar el PDF de una factura AFIP. Solo se muestran si la
// factura fue aceptada (resultado === "A"); si falló/rechazó, no hay PDF valido.
function InvoicePdfActions({ invoice }) {
  const [busy, setBusy] = useState(false);
  const publicId = getPublicId(invoice);
  if (invoice?.resultado !== "A" || !publicId) {
    return <span className="text-xs text-slate-400">-</span>;
  }

  const fileLabel = `Factura-${invoice.tipoComprobante === 1 ? "A" : invoice.tipoComprobante === 6 ? "B" : "C"}-${String(invoice.puntoDeVenta).padStart(5, "0")}-${String(invoice.numeroComprobante).padStart(8, "0")}.pdf`;

  const fetchBlob = async () => {
    const response = await api.get(`/invoices/${publicId}/pdf`, { responseType: "blob" });
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

  const download = async () => {
    setBusy(true);
    try {
      const blob = await fetchBlob();
      const url = window.URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.setAttribute("download", fileLabel);
      document.body.appendChild(link);
      link.click();
      link.remove();
      window.URL.revokeObjectURL(url);
    } catch (error) {
      showError(getApiErrorMessage(error) || "No se pudo descargar la factura.", "Error al descargar factura");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="inline-flex items-center gap-1">
      <button
        type="button"
        onClick={view}
        disabled={busy}
        className="rounded p-1.5 text-indigo-600 hover:bg-indigo-50 disabled:opacity-50 dark:text-indigo-300 dark:hover:bg-indigo-950/40"
        title="Ver PDF"
      >
        <Eye className="h-4 w-4" />
      </button>
      <button
        type="button"
        onClick={download}
        disabled={busy}
        className="rounded p-1.5 text-emerald-600 hover:bg-emerald-50 disabled:opacity-50 dark:text-emerald-300 dark:hover:bg-emerald-950/40"
        title="Descargar PDF"
      >
        <Download className="h-4 w-4" />
      </button>
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

function PaymentReceiptActions({ payment, onView, onIssue, onVoid }) {
  const receipt = getPaymentReceipt(payment);

  if (receipt) {
    const isVoided = receipt.status === "Voided";
    return (
      <div className="flex flex-wrap items-center gap-2">
        <span className={`rounded-full px-2 py-1 text-[10px] font-black uppercase ${isVoided ? "bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400" : "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300"}`}>
          {isVoided ? "Comprobante anulado" : receipt.receiptNumber}
        </span>
        {!isVoided ? (
          <>
            <button
              type="button"
              onClick={() => onView(payment)}
              className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs font-bold text-indigo-600 transition-colors hover:bg-indigo-50 dark:text-indigo-300 dark:hover:bg-indigo-900/30"
              title="Ver comprobante de pago"
            >
              <ExternalLink className="h-3.5 w-3.5" />
              Ver PDF
            </button>
            {typeof onVoid === "function" && (
              <button
                type="button"
                onClick={() => onVoid(payment)}
                className="inline-flex items-center gap-1 rounded-lg border border-rose-200 px-2 py-1 text-xs font-bold text-rose-600 transition-colors hover:bg-rose-50 dark:border-rose-900/30 dark:text-rose-400 dark:hover:bg-rose-900/20"
                title="Anular comprobante de pago"
              >
                Anular comprobante
              </button>
            )}
          </>
        ) : null}
      </div>
    );
  }

  if (canIssuePaymentReceipt(payment)) {
    return (
      <button
        type="button"
        onClick={() => onIssue(payment)}
        className="inline-flex items-center gap-1.5 rounded-lg border border-indigo-200 px-3 py-1.5 text-xs font-bold text-indigo-700 transition-colors hover:bg-indigo-50 dark:border-indigo-800 dark:text-indigo-300 dark:hover:bg-indigo-900/30"
      >
        <Receipt className="h-3.5 w-3.5" />
        Emitir comprobante
      </button>
    );
  }

  return <span className="text-xs text-slate-400">Sin comprobante</span>;
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
 */
function UnconfirmedServicesBanner({ reserva }) {
  // Solo mostramos en Confirmed: en InManagement el resumen de ResumenServiciosResueltos
  // ya cubre esta información de forma más específica.
  if (reserva.status !== "Confirmed") return null;

  // Reunimos todos los servicios para detectar cuáles no están resueltos todavía.
  // "resuelto" = el operador confirmó o el ticket fue emitido (equivalente a esServicioResuelto de ServiceList).
  const estadosResueltos = new Set(["Confirmado", "Emitido", "HK", "TK", "KK", "KL", "NoConfirmation"]);

  const todosLosServicios = [
    ...(reserva.hotelBookings || []).map(b => ({ nombre: b.name || b.hotelName || "Hotel", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.transferBookings || []).map(b => ({ nombre: b.name || "Traslado", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.packageBookings || []).map(b => ({ nombre: b.name || b.packageName || "Paquete", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.flightSegments || []).map(b => ({ nombre: b.name || "Aéreo", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.assistanceBookings || []).map(b => ({ nombre: b.name || "Asistencia", workflowStatus: b.workflowStatus || b.status })),
    ...(reserva.servicios || []).map(b => ({ nombre: b.description || "Servicio adicional", workflowStatus: b.workflowStatus || b.status })),
  ];

  // Excluimos los cancelados — no cuentan para la confirmación.
  const serviciosSinResolver = todosLosServicios.filter(
    s => s.workflowStatus !== "Cancelado" && !estadosResueltos.has(s.workflowStatus)
  );

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

  // ADR-031 v2.1 — Pieza C: estado del readiness. Se declara aquí porque useState
  // siempre va al inicio del componente, pero el useEffect que lo carga se mueve
  // DESPUÉS de useReservaDetail para evitar TDZ en el bundle de producción.
  // (Causa del crash: useEffect referenciaba `reserva` antes de que se declarara con const.)
  const [readiness, setReadiness] = useState(null);

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

  const handleDeletePayment = async (payment) => {
    try {
      await api.delete(`/payments/${getPublicId(payment)}`);
      showSuccess("Pago eliminado correctamente");
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
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
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
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
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
        serviciosCancelados={serviciosCancelados}
        totalPasajerosDeclarados={
          // P2 (ADR-031): ReservaHeader lo usa para deshabilitar "El cliente aceptó" cuando no hay pax.
          (reserva?.adultCount || 0) + (reserva?.childCount || 0) + (reserva?.infantCount || 0)
        }
      />

      <ReservaSummaryStrip reserva={reserva} />

      {/* Banner ADR-020:
          - Modo regresion (naranja): cuando la reserva volvio sola a En gestion por cambio del operador.
            Se activa desde lastRegressionReason del DTO (B2 del reviewer).
          - Modo destrabada (verde): cuando hasLiveEditAuthorization=true — hay autorizacion vigente (N3).
          - Modo candado (ambar): reserva bloqueada sin autorizacion activa (decision #1).
          Prioridad: regresion > destrabada > candado. */}
      <ReservaLockBanner
        isLocked={isStatusLocked(reserva.status)}
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

      {/* ADR-027: franja amarilla "Confirmada con cambios".
          Aparece cuando el vendedor edito precio o costo de un servicio en una reserva viva
          (InManagement/Confirmed/Traveling) y el dueño todavía no revisó el cambio.
          ADR-036: ToSettle fue eliminado.

          Detalle de pendingChanges[]: si el backend manda la lista, mostramos cada cambio
          con su descripción, campo, valores viejo→nuevo y moneda. Si no viene o viene vacía,
          mostramos el mensaje general (fallback seguro para versiones de API sin ese campo).

          El botón "Dar OK" es SOLO para administradores (isAdmin()); un no-admin ve la franja
          pero sin botón — ya puede ver el saldo actualizado y los servicios. */}
      {reserva.hasUnacknowledgedChanges && (
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
      {reserva.status === "Lost" ? (
        <div
          className="rounded-xl border border-slate-200 bg-slate-100 p-4 text-sm text-slate-700 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-400"
          data-testid="banner-estado-terminal"
          role="status"
        >
          <strong className="font-bold">Reserva perdida</strong> — solo lectura.
        </div>
      ) : reserva.status === "Cancelled" ? (
        // ADR-036: el estado interno sigue siendo "Cancelled" pero el usuario ve "Anulada".
        // "Cancelar" en este producto = saldar una deuda; "Anular" = deshacer el viaje.
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
      ) : reserva.status === "PendingOperatorRefund" ? (
        <div
          className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
          data-testid="banner-estado-terminal"
          role="status"
        >
          <strong className="font-bold">Anulada, esperando el reembolso del operador</strong> — solo lectura.
        </div>
      ) : null}

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

      {/* ── "Debe — no viaja": cartel arriba para reservas Confirmadas con saldo pendiente (ADR-036, punto 7) ──
          Aparece cuando la reserva está Confirmada, el cliente todavía debe Y la salida está dentro de la
          ventana de aviso configurada. El backend no permite que pase a "En viaje" hasta que esté 100% pagada.
          NO muestra montos de costo ni deuda al operador — solo que el cliente debe.

          ADR-037: el backend ya expone `isWithinUnpaidAlertWindow` (calculado contra StartDate y la config
          existente upcomingUnpaidReservationAlertDays). El cartel respeta esa ventana: fuera de ella no aparece. */}
      {reserva.status === "Confirmed" && !reserva.isFullyPaid && reserva.isWithinUnpaidAlertWindow ? (
        <div
          className="rounded-xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
          data-testid="banner-debe-no-viaja"
          role="status"
        >
          <strong className="font-bold">No puede viajar todavía:</strong> hay saldo pendiente del cliente.
        </div>
      ) : null}

      <UnconfirmedServicesBanner reserva={reserva} />

      <CapacityWarning paxCount={reserva.passengers?.length || 0} capacity={capacity} />

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
                onCancelService={(service, motivo) => handleCancelService(service, motivo)}
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
          {activeTab === "attachments" ? <ReservaDocumentsTab reservaId={publicId} /> : null}
          {activeTab === "voucher" ? <ReservaVoucherTab reservaId={publicId} reserva={reserva} /> : null}

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

                const registroPagoHabilitado = !capRegPago || capRegPago.allowed;
                const facturaHabilitada = !capFactura || capFactura.allowed;
                // Cancelar ademas requiere permiso de usuario (igual que antes)
                const cancelarHabilitado = canCancelReserva && (!capCancelar || capCancelar.allowed);

                return (
                  <div className="flex flex-wrap items-start gap-3 rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-800/50">

                    {/* Registrar cobro */}
                    <div className="flex flex-col items-start gap-0.5">
                      <button
                        onClick={() => {
                          if (!registroPagoHabilitado) return;
                          setCobroAEditar(null);
                          setShowCobroInline(true);
                        }}
                        disabled={!registroPagoHabilitado}
                        className={`flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold transition-all ${
                          registroPagoHabilitado
                            ? 'bg-emerald-600 text-white hover:bg-emerald-700'
                            : 'bg-slate-200 text-slate-400 dark:bg-slate-700 dark:text-slate-500 cursor-not-allowed'
                        }`}
                        data-testid="btn-registrar-cobro"
                      >
                        <Plus className="w-4 h-4" /> Registrar cobro
                      </button>
                      {/* Motivo de bloqueo en texto chico ambar (ADR-035, nunca tooltip) */}
                      {!registroPagoHabilitado && capRegPago?.reason && (
                        <p className="text-xs text-amber-600 dark:text-amber-400 font-medium px-1" data-testid="btn-registrar-cobro-reason">
                          {capRegPago.reason}
                        </p>
                      )}
                    </div>

                    {/* Emitir factura — ADR-037: la facturación se desacopló del estado, el botón
                        se gobierna por la capability canInvoiceSale (habilitado en Confirmada/En viaje/
                        Finalizada, sin reabrir nada). Decisión Gaston 2026-06-21: si la reserva ya está
                        facturada del todo, NO se muestra (para corregir es con nota de crédito/débito). */}
                    {reserva.invoicingStatus !== 'FullyInvoiced' && (
                      <div className="flex flex-col items-start gap-0.5">
                        <button
                          onClick={() => { if (facturaHabilitada) setShowFacturaInline(true); }}
                          disabled={!facturaHabilitada}
                          className={`flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold transition-all ${
                            facturaHabilitada
                              ? 'bg-indigo-600 text-white hover:bg-indigo-700'
                              : 'bg-slate-200 text-slate-400 dark:bg-slate-700 dark:text-slate-500 cursor-not-allowed'
                          }`}
                          data-testid="btn-emitir-factura"
                        >
                          <FileText className="w-4 h-4" /> Emitir factura
                        </button>
                        {!facturaHabilitada && capFactura?.reason && (
                          <p className="text-xs text-amber-600 dark:text-amber-400 font-medium px-1" data-testid="btn-emitir-factura-reason">
                            {capFactura.reason}
                          </p>
                        )}
                      </div>
                    )}

                    {/* Cancelar reserva — visible solo si el usuario tiene permiso reservas.cancel */}
                    {canCancelReserva && (
                      <div className="flex flex-col items-start gap-0.5">
                        <button
                          onClick={() => { if (cancelarHabilitado) setShowCancelInline(true); }}
                          disabled={!cancelarHabilitado}
                          className={`flex items-center gap-2 rounded-lg px-4 py-2 text-sm font-bold transition-all ${
                            cancelarHabilitado
                              ? 'bg-rose-600 text-white hover:bg-rose-700'
                              : 'bg-slate-200 text-slate-400 dark:bg-slate-700 dark:text-slate-500 cursor-not-allowed'
                          }`}
                          data-testid="btn-cancelar-reserva-account"
                        >
                          <Ban className="w-4 h-4" /> Cancelar reserva
                        </button>
                        {!cancelarHabilitado && capCancelar?.reason && (
                          <p className="text-xs text-amber-600 dark:text-amber-400 font-medium px-1" data-testid="btn-cancelar-reserva-reason">
                            {capCancelar.reason}
                          </p>
                        )}
                      </div>
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
                    fetchReserva({ showLoading: false, preserveOnError: true });
                  }}
                  onCerrar={() => setShowCancelInline(false)}
                />
              )}

              <div className="grid grid-cols-1 gap-6">
                <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
                  <div className="flex items-center gap-2 border-b border-slate-100 bg-slate-50/30 px-6 py-4 dark:border-slate-800 dark:bg-slate-800/10">
                    <History className="w-4 h-4 text-emerald-500" />
                    <h4 className="text-sm font-bold uppercase tracking-wider text-slate-900 dark:text-white">Historial de cobros y comprobantes</h4>
                  </div>
                  <DataGrid density="compact" minWidth="900px">
                    <DataGridHeader>
                      <DataGridHeaderRow>
                        <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                        <DataGridHeaderCell>Metodo</DataGridHeaderCell>
                        {/* Columna Moneda: siempre presente para multimoneda (indica en qué moneda entró cada cobro) */}
                        <DataGridHeaderCell>Moneda</DataGridHeaderCell>
                        <DataGridHeaderCell>Notas</DataGridHeaderCell>
                        <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Importe</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Acciones</DataGridHeaderCell>
                      </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                      {reserva.payments?.length > 0 ? (
                        reserva.payments.map((payment) => {
                          const monedaCobro = payment.currency || "ARS";
                          const esCruzado = payment.imputedCurrency && payment.imputedCurrency !== monedaCobro;
                          return (
                            <DataGridRow key={getPublicId(payment)}>
                              <DataGridCell>{new Date(payment.paidAt).toLocaleDateString()}</DataGridCell>
                              <DataGridCell>
                                <span className="rounded bg-slate-100 px-2 py-1 text-[10px] font-black uppercase dark:bg-slate-800">
                                  {payment.method}
                                </span>
                              </DataGridCell>
                              <DataGridCell>
                                <CurrencyBadge currency={monedaCobro} size="sm" />
                              </DataGridCell>
                              <DataGridCell>
                                <div>
                                  {payment.notes || "-"}
                                  {/* Para cobros cruzados: detalle inline del saldo imputado en la otra moneda */}
                                  {esCruzado && payment.imputedAmount != null && (
                                    <div className="text-[10px] text-slate-400 dark:text-slate-500 mt-0.5">
                                      imputado a {payment.imputedCurrency === "USD" ? "US$" : "$"}{Number(payment.imputedAmount).toLocaleString("es-AR", { minimumFractionDigits: 2 })}
                                    </div>
                                  )}
                                </div>
                              </DataGridCell>
                              <DataGridCell>
                                <PaymentReceiptActions payment={payment} onView={handleViewReceiptPdf} onIssue={handleIssueReceipt} onVoid={handleVoidReceipt} />
                              </DataGridCell>
                              <DataGridCell align="right" className="font-black text-emerald-600">
                                {/* Importe formateado con la moneda real del cobro */}
                                {payment.amount?.toLocaleString("es-AR", { style: "currency", currency: monedaCobro })}
                              </DataGridCell>
                              <DataGridCell align="right">
                                <div className="flex justify-end gap-1">
                                  <button
                                    onClick={() => {
                                      setCobroAEditar(payment);
                                      setShowCobroInline(true);
                                    }}
                                    className="p-1 text-blue-600 hover:bg-blue-50 rounded transition-colors"
                                    title="Editar cobro"
                                    aria-label="Editar cobro"
                                  >
                                    <Edit2 className="w-3.5 h-3.5" />
                                  </button>
                                  <button
                                    onClick={() =>
                                      askConfirmation({
                                        title: "Eliminar cobro?",
                                        message: `¿Seguro que deseas eliminar el cobro de ${payment.amount?.toLocaleString("es-AR", { style: "currency", currency: monedaCobro })}?`,
                                        type: "danger",
                                        onConfirm: () => handleDeletePayment(payment),
                                      })
                                    }
                                    className="p-1 text-rose-600 hover:bg-rose-50 rounded transition-colors"
                                    title="Eliminar cobro"
                                    aria-label="Eliminar cobro"
                                  >
                                    <Trash2 className="w-3.5 h-3.5" />
                                  </button>
                                </div>
                              </DataGridCell>
                            </DataGridRow>
                          );
                        })
                      ) : (
                        <DataGridEmptyState colSpan={7} title="No hay cobros registrados." />
                      )}
                    </DataGridBody>
                  </DataGrid>

                  {/* Subtotal por moneda al pie del historial — solo cuando hay más de una moneda */}
                  {(() => {
                    const pagos = reserva.payments || [];
                    if (pagos.length === 0) return null;
                    const totales = pagos.reduce((acc, p) => {
                      const moneda = p.currency || "ARS";
                      acc[moneda] = (acc[moneda] || 0) + (p.amount || 0);
                      return acc;
                    }, {});
                    const monedas = Object.keys(totales);
                    if (monedas.length <= 1) return null; // Una sola moneda: no mostrar subtotal extra
                    return (
                      <div className="flex items-center justify-end gap-4 px-6 py-3 border-t border-slate-100 dark:border-slate-800 text-sm font-bold text-slate-700 dark:text-slate-300">
                        <span className="text-xs uppercase tracking-wider text-slate-400">Total cobrado</span>
                        <span className="flex items-center gap-3">
                          {monedas.map((moneda, idx) => (
                            <span key={moneda} className="inline-flex items-center gap-1">
                              <CurrencyBadge currency={moneda} size="sm" />
                              {totales[moneda].toLocaleString("es-AR", { style: "currency", currency: moneda })}
                              {idx < monedas.length - 1 && <span className="text-slate-400 mx-1">·</span>}
                            </span>
                          ))}
                        </span>
                      </div>
                    );
                  })()}

                  {reserva.payments?.length > 0 ? (
                    <MobileRecordList className="p-4 md:hidden">
                      {reserva.payments.map((payment) => {
                        const monedaCobro = payment.currency || "ARS";
                        const esCruzado = payment.imputedCurrency && payment.imputedCurrency !== monedaCobro;
                        return (
                          <MobileRecordCard
                            key={getPublicId(payment)}
                            title={payment.method}
                            subtitle={new Date(payment.paidAt).toLocaleDateString()}
                            meta={
                              <>
                                <div className="flex items-center gap-1.5 text-xs text-slate-500 dark:text-slate-400">
                                  <CurrencyBadge currency={monedaCobro} />
                                  {esCruzado && payment.imputedAmount != null && (
                                    <span className="text-[10px]">→ imputado a {payment.imputedCurrency === "USD" ? "US$" : "$"}{Number(payment.imputedAmount).toLocaleString("es-AR", { minimumFractionDigits: 2 })}</span>
                                  )}
                                </div>
                                <div className="text-xs text-slate-500 dark:text-slate-400">{payment.notes || "Sin notas"}</div>
                                <div>
                                  <PaymentReceiptActions payment={payment} onView={handleViewReceiptPdf} onIssue={handleIssueReceipt} onVoid={handleVoidReceipt} />
                                </div>
                              </>
                            }
                            footer={<span className="text-sm font-bold text-emerald-600">{payment.amount?.toLocaleString("es-AR", { style: "currency", currency: monedaCobro })}</span>}
                          />
                        );
                      })}
                    </MobileRecordList>
                  ) : (
                    <ListEmptyState
                      title="No hay cobros registrados."
                      className="md:hidden rounded-none border-t border-dashed border-slate-200 dark:border-slate-800"
                    />
                  )}
                </div>

                <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
                  <div className="flex items-center gap-2 border-b border-slate-100 bg-slate-50/30 px-6 py-4 dark:border-slate-800 dark:bg-slate-800/10">
                    <FileText className="w-4 h-4 text-indigo-500" />
                    <h4 className="text-sm font-bold uppercase tracking-wider text-slate-900 dark:text-white">Documentos Fiscales AFIP</h4>
                  </div>
                  <DataGrid density="compact" minWidth="860px">
                    <DataGridHeader>
                      <DataGridHeaderRow>
                        <DataGridHeaderCell>Tipo</DataGridHeaderCell>
                        <DataGridHeaderCell>Numero</DataGridHeaderCell>
                        <DataGridHeaderCell>CAE</DataGridHeaderCell>
                        <DataGridHeaderCell>Estado</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Importe</DataGridHeaderCell>
                        <DataGridHeaderCell align="center">PDF</DataGridHeaderCell>
                      </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                      {reserva.invoices?.length > 0 ? (
                        reserva.invoices.map((invoice) => (
                          <DataGridRow key={getPublicId(invoice)}>
                            <DataGridCell className="font-bold">
                              <InvoiceTypeLabel
                                tipoComprobante={invoice.tipoComprobante}
                                originalInvoiceNumeroComprobante={invoice.originalInvoiceNumeroComprobante}
                                originalInvoicePuntoDeVenta={invoice.originalInvoicePuntoDeVenta}
                                originalInvoiceTipoComprobante={invoice.originalInvoiceTipoComprobante}
                              />
                            </DataGridCell>
                            <DataGridCell className="font-mono">
                              {String(invoice.puntoDeVenta).padStart(5, "0")}-{String(invoice.numeroComprobante).padStart(8, "0")}
                            </DataGridCell>
                            <DataGridCell className="font-mono text-xs text-slate-400">{invoice.cae || "---"}</DataGridCell>
                            <DataGridCell>
                              <InvoiceStatusBadge resultado={invoice.resultado} annulmentStatus={invoice.annulmentStatus} />
                            </DataGridCell>
                            <DataGridCell align="right" className="font-black">
                              {invoice.importeTotal?.toLocaleString("es-AR", { style: "currency", currency: "ARS" })}
                            </DataGridCell>
                            <DataGridCell align="center">
                              <InvoicePdfActions invoice={invoice} />
                            </DataGridCell>
                          </DataGridRow>
                        ))
                      ) : (
                        <DataGridEmptyState colSpan={6} title="No hay facturas emitidas para esta reserva." />
                      )}
                    </DataGridBody>
                  </DataGrid>
                  {reserva.invoices?.length > 0 ? (
                    <MobileRecordList className="p-4 md:hidden">
                      {reserva.invoices.map((invoice) => (
                        <MobileRecordCard
                          key={getPublicId(invoice)}
                          statusSlot={<InvoiceStatusBadge resultado={invoice.resultado} annulmentStatus={invoice.annulmentStatus} />}
                          title={
                            <InvoiceTypeLabel
                              tipoComprobante={invoice.tipoComprobante}
                              originalInvoiceNumeroComprobante={invoice.originalInvoiceNumeroComprobante}
                              originalInvoicePuntoDeVenta={invoice.originalInvoicePuntoDeVenta}
                              originalInvoiceTipoComprobante={invoice.originalInvoiceTipoComprobante}
                            />
                          }
                          subtitle={`${String(invoice.puntoDeVenta).padStart(5, "0")}-${String(invoice.numeroComprobante).padStart(8, "0")}`}
                          meta={<div className="text-xs text-slate-500 dark:text-slate-400">CAE {invoice.cae || "---"}</div>}
                          footer={<span className="text-sm font-bold text-slate-900 dark:text-white">{invoice.importeTotal?.toLocaleString("es-AR", { style: "currency", currency: "ARS" })}</span>}
                          footerActions={<InvoicePdfActions invoice={invoice} />}
                        />
                      ))}
                    </MobileRecordList>
                  ) : (
                    <ListEmptyState
                      title="No hay facturas emitidas para esta reserva."
                      className="md:hidden rounded-none border-t border-dashed border-slate-200 dark:border-slate-800"
                    />
                  )}
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
    </div>
  );
}
