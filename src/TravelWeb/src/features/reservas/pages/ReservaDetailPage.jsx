import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Clock, CreditCard, Download, Eye, ExternalLink, FileText, History, Paperclip, Receipt, Users, Trash2, Edit2 } from "lucide-react";
import { api } from "../../../api";
import { showConfirm, showError, showSuccess } from "../../../alerts";
import ReservaTimeline from "../../../components/ReservaTimeline";
import ConfirmModal from "../../../components/ConfirmModal";
import PassengerFormModal from "../../../components/PassengerFormModal";
import PaymentModal from "../../../components/PaymentModal";
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
import { ConfirmReservaModal } from "../components/ConfirmReservaModal";
import { EditReservaDatesModal } from "../components/EditReservaDatesModal";
import { PassengerAssignmentsPanel } from "../components/PassengerAssignmentsPanel";
import { PassengerList } from "../components/PassengerList";
import { ReservaHeader } from "../components/ReservaHeader";
import { ReservaSummaryStrip } from "../components/ReservaSummaryStrip";
import { RevertStatusModal } from "../components/RevertStatusModal";
import { ServiceList } from "../components/ServiceList";
import { useReservaDetail } from "../hooks/useReservaDetail";
import { useOperationalFlags } from "../../../contexts/OperationalFlagsContext";
import CancelReservaModal from "../../cancellations/components/CancelReservaModal";
import { hasPermission } from "../../../auth";

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

// Lista de Status considerados "confirmados" — espejo de SupplierService:205 y
// ReservaCapacityRules.ConfirmedServiceStatuses. Si cambia uno, hay que sincronizar.
const CONFIRMED_SERVICE_STATUSES = new Set(["Confirmado", "Emitido", "HK", "TK", "KK", "KL"]);

function UnconfirmedServicesBanner({ reserva }) {
  // Solo relevante en Confirmed: cuando ya esta En viaje no se puede cambiar
  // servicios, y en Presupuesto no aplica todavia.
  if (reserva.status !== "Confirmed") return null;

  const all = [
    ...(reserva.hotelBookings || []).map(b => ({ label: `Hotel ${b.hotelName || ""}`, status: b.status })),
    ...(reserva.transferBookings || []).map(b => ({ label: `Transfer ${b.vehicleType || ""}`, status: b.status })),
    ...(reserva.packageBookings || []).map(b => ({ label: `Paquete ${b.packageName || ""}`, status: b.status })),
    ...(reserva.flightSegments || []).map(b => ({ label: `Vuelo ${b.airlineCode || ""}${b.flightNumber || ""}`, status: b.status })),
    ...(reserva.servicios || []).map(b => ({ label: `${b.description || "Servicio"}`, status: b.status })),
  ];
  const unconfirmed = all.filter(s => s.status && !CONFIRMED_SERVICE_STATUSES.has(s.status));
  if (unconfirmed.length === 0) return null;

  return (
    <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-200">
      <div className="font-bold mb-1">
        {unconfirmed.length} servicio(s) sin confirmar con el proveedor
      </div>
      <div className="text-xs text-amber-800 dark:text-amber-300 mb-2">
        Confirma todos los servicios antes de marcar la reserva en viaje. Los servicios sin confirmar no entran al balance del proveedor y dejarian la cuenta corriente con datos sucios.
      </div>
      <ul className="text-xs space-y-0.5">
        {unconfirmed.slice(0, 8).map((s, i) => (
          <li key={i}>
            • <strong>{s.label.trim()}</strong> — estado: <span className="font-mono">{s.status}</span>
          </li>
        ))}
        {unconfirmed.length > 8 && <li className="italic">y {unconfirmed.length - 8} mas...</li>}
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
        En estado Presupuesto solo se cargan cantidades. Al confirmar la reserva podras cargar cada pasajero con nombre y documento.
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

  // Lee si el ciclo extendido de reservas esta habilitado.
  // Con flag OFF (default en produccion): la pagina es identica a antes (Budget→Confirmed directo).
  // Con flag ON: Budget→Sold (con modal de pasajeros) → Confirmed.
  // loadingFlags: mientras es true, isSoldToSettleEnabled es false (default).
  // Lo pasamos a ReservaHeader para que no muestre la botonera hasta tener
  // el valor definitivo del flag — evita el salto del boton "Confirmar Reserva" a "Vender".
  const { flags, loadingFlags } = useOperationalFlags();
  const isSoldToSettleEnabled = flags.enableSoldToSettleStates;
  // ADR-017: cuando está ON, la carga de servicios usa la ficha en línea (ServiceInlineCard)
  // en lugar del modal (ServiceFormModal). Con OFF el comportamiento es idéntico al de hoy.
  const isCatalogFindOrCreateEnabled = flags.enableCatalogFindOrCreate;

  // Estado de la ficha inline (solo se usa cuando enableCatalogFindOrCreate está ON)
  const [showInlineCard, setShowInlineCard] = useState(false);
  const [serviceToEditInline, setServiceToEditInline] = useState(null);

  // Permiso de cancelacion: se resuelve client-side desde el store de auth.
  // NOTA: esto es UI-only. El server-side siempre re-valida el permiso.
  // Con hasPermission("reservas.cancel"), isAdmin() retorna true para admins (bypass).
  const canCancelReserva = hasPermission("reservas.cancel");

  const [showCancelModal, setShowCancelModal] = useState(false);

  const [showServiceModal, setShowServiceModal] = useState(false);
  const [serviceToEdit, setServiceToEdit] = useState(null);
  const [showPassengerForm, setShowPassengerForm] = useState(false);
  const [editingPassenger, setEditingPassenger] = useState(null);
  const [showPaymentModal, setShowPaymentModal] = useState(false);
  const [paymentToEdit, setPaymentToEdit] = useState(null);
  // targetStatus: "Confirmed" (ciclo base) o "Sold" (ciclo extendido con flag ON).
  const [confirmReservaModal, setConfirmReservaModal] = useState({ isOpen: false, readiness: null, targetStatus: "Confirmed" });
  const [showRevertModal, setShowRevertModal] = useState(false);
  const [showEditDatesModal, setShowEditDatesModal] = useState(false);
  const [confirmConfig, setConfirmConfig] = useState({
    isOpen: false,
    title: "",
    message: "",
    onConfirm: null,
    type: "warning",
  });

  const askConfirmation = (config) => {
    setConfirmConfig({
      isOpen: true,
      title: config.title || "Confirmar accion",
      message: config.message || "Estas seguro?",
      type: config.type || "warning",
      onConfirm: () => {
        config.onConfirm();
        setConfirmConfig((prev) => ({ ...prev, isOpen: false }));
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
    handleDeletePassenger,
    handleServiceUpdated,
    allServices,
    capacity,
  } = useReservaDetail(publicId, navigate);

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
   * Flujo de "vender" o "confirmar" que abre el modal de pasajeros (readiness).
   *
   * Con ciclo base (flag OFF): se llama cuando el usuario hace click en
   *   "Confirmar Reserva" (Budget → Confirmed).
   * Con ciclo extendido (flag ON): se llama cuando el usuario hace click en
   *   "Vender" (Budget → Sold). La transicion target cambia pero el modal es el mismo.
   *
   * El parametro `targetStatus` permite que la misma logica sirva para los dos casos.
   */
  const handleConfirmReservation = async (targetStatus = "Confirmed") => {
    try {
      // Consultamos readiness para saber si hay pasajeros faltantes o reglas no-pax.
      const readiness = await api.get(`/reservas/${publicId}/transition-readiness?to=${targetStatus}`);
      const expected = readiness?.expectedPassengerCount || 0;
      const blockingNonPax = (readiness?.blockingReasons || []).filter(r => !r.toLowerCase().includes("pasajero"));

      if (readiness?.allowed && expected === 0 && blockingNonPax.length === 0) {
        // Camino directo: nada que cargar, transicion inmediata.
        await handleStatusChange(targetStatus);
        return;
      }
      // Modal forzado: hay pasajeros faltantes o reglas no-pax que mostrar.
      // Pasamos targetStatus para que el modal dispare la transicion correcta.
      setConfirmReservaModal({ isOpen: true, readiness, targetStatus });
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo verificar el estado de la reserva."));
    }
  };

  const isBudget = reserva?.status === "Budget";

  useEffect(() => {
    if (isBudget && (activeTab === "voucher" || activeTab === "attachments" || activeTab === "account" || activeTab === "passengers")) {
      setActiveTab("services");
    }
  }, [isBudget, activeTab]);

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
        isSoldToSettleEnabled={isSoldToSettleEnabled}
        loadingFlags={loadingFlags}
        onBack={() => navigate("/reservas")}
        onStatusChange={(newStatus) => {
          // Con ciclo base (flag OFF): Budget → Confirmed abre el modal de pasajeros.
          // Con ciclo extendido (flag ON): Budget → Sold abre el modal de pasajeros.
          // Cualquier otra transicion va directo al PUT /status sin modal.
          if (newStatus === "Confirmed" && reserva.status === "Budget" && !isSoldToSettleEnabled) {
            handleConfirmReservation("Confirmed");
          } else if (newStatus === "Sold" && reserva.status === "Budget" && isSoldToSettleEnabled) {
            // Reapuntamos el flujo de pasajeros a la nueva transicion "Vender".
            handleConfirmReservation("Sold");
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
        onCancelReserva={() => setShowCancelModal(true)}
      />

      <ReservaSummaryStrip reserva={reserva} />

      {isBudget ? (
        <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-200">
          <strong className="font-bold">Reserva en modo Presupuesto.</strong>{" "}
          {isSoldToSettleEnabled
            ? "Vendé la reserva para cargar los pasajeros nominales y registrar pagos."
            : "Confirma la reserva para cargar pasajeros nominales y registrar pagos."
          }
        </div>
      ) : null}

      {/* Banner informativo cuando la reserva esta Vendida (esperando confirmacion del operador).
          Solo aparece con el ciclo extendido activo (flag ON). */}
      {isSoldToSettleEnabled && reserva.status === "Sold" ? (
        <div className="rounded-xl border border-orange-200 bg-orange-50 p-4 text-sm text-orange-900 dark:border-orange-900/40 dark:bg-orange-950/30 dark:text-orange-200">
          <strong className="font-bold">Reserva Vendida.</strong>{" "}
          Esperando confirmacion del operador. Una vez confirmada, la reserva pasara a estado Confirmada.
        </div>
      ) : null}

      {/* Banner informativo cuando la reserva esta A liquidar (viaje terminado, falta liquidar).
          Solo aparece con el ciclo extendido activo (flag ON). */}
      {isSoldToSettleEnabled && reserva.status === "ToSettle" ? (
        <div className="rounded-xl border border-violet-200 bg-violet-50 p-4 text-sm text-violet-900 dark:border-violet-900/40 dark:bg-violet-950/30 dark:text-violet-200">
          <strong className="font-bold">Reserva A liquidar.</strong>{" "}
          Apartada para cerrar cuentas con el operador. Cuando termines, usá "Finalizar" para cerrarla.
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
              // En Presupuesto no mostramos tab de Pasajeros/Cantidades — la cantidad se
              // deriva de los servicios (Hotel.Adults+Children, Package.Adults+Children, etc.)
              // y se confirma/ajusta en el modal de "Confirmar reserva".
              isBudget
                ? null
                : { id: "passengers", label: `Pasajeros (${reserva.passengers?.length || 0})`, icon: Users },
              { id: "history", label: "Historial", icon: Clock },
              isBudget ? null : { id: "account", label: "Estado de Cuenta", icon: CreditCard },
              isBudget ? null : { id: "voucher", label: "Vouchers", icon: FileText },
              isBudget ? null : { id: "attachments", label: "Documentos", icon: Paperclip },
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
              <ServiceList
                services={allServices}
                serviceCollectionErrors={serviceCollectionErrors}
                reservaId={publicId}
                isCatalogFindOrCreateEnabled={isCatalogFindOrCreateEnabled}
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
                onDeleteService={(service) =>
                  askConfirmation({
                    title: "Eliminar servicio?",
                    message: `Estas seguro de eliminar el servicio ${service.description || ""}?`,
                    type: "danger",
                    onConfirm: () => handleDeleteService(service),
                  })
                }
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
              <PassengerAssignmentsPanel reserva={reserva} isBudget={isBudget} />
            </div>
          ) : null}

          {activeTab === "passengers" && !isBudget ? (
            <PassengerList
              passengers={reserva.passengers}
              onAddPassenger={() => {
                setEditingPassenger(null);
                setShowPassengerForm(true);
              }}
              onEditPassenger={(passenger) => {
                setEditingPassenger(passenger);
                setShowPassengerForm(true);
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
              <div className="flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-800 dark:bg-slate-800/50">
                <button
                  onClick={() => {
                    setPaymentToEdit(null);
                    setShowPaymentModal(true);
                  }}
                  className="flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white transition-all hover:bg-emerald-700"
                >
                  <CreditCard className="w-4 h-4" /> Registrar Cobranza
                </button>
                <div className="text-xs italic font-medium text-slate-500">
                  * Los pagos recibidos afectan directamente al saldo de la reserva.
                </div>
              </div>

              <div className="grid grid-cols-1 gap-6">
                <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
                  <div className="flex items-center gap-2 border-b border-slate-100 bg-slate-50/30 px-6 py-4 dark:border-slate-800 dark:bg-slate-800/10">
                    <History className="w-4 h-4 text-emerald-500" />
                    <h4 className="text-sm font-bold uppercase tracking-wider text-slate-900 dark:text-white">Historial de Cobranzas y Comprobantes</h4>
                  </div>
                  <DataGrid density="compact" minWidth="900px">
                    <DataGridHeader>
                      <DataGridHeaderRow>
                        <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                        <DataGridHeaderCell>Metodo</DataGridHeaderCell>
                        <DataGridHeaderCell>Notas</DataGridHeaderCell>
                        <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Importe</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Acciones</DataGridHeaderCell>
                      </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                      {reserva.payments?.length > 0 ? (
                        reserva.payments.map((payment) => (
                          <DataGridRow key={getPublicId(payment)}>
                            <DataGridCell>{new Date(payment.paidAt).toLocaleDateString()}</DataGridCell>
                            <DataGridCell>
                              <span className="rounded bg-slate-100 px-2 py-1 text-[10px] font-black uppercase dark:bg-slate-800">
                                {payment.method}
                              </span>
                            </DataGridCell>
                            <DataGridCell>{payment.notes || "-"}</DataGridCell>
                            <DataGridCell>
                              <PaymentReceiptActions payment={payment} onView={handleViewReceiptPdf} onIssue={handleIssueReceipt} onVoid={handleVoidReceipt} />
                            </DataGridCell>
                            <DataGridCell align="right" className="font-black text-emerald-600">
                              {payment.amount?.toLocaleString("es-AR", { style: "currency", currency: "ARS" })}
                            </DataGridCell>
                            <DataGridCell align="right">
                              <div className="flex justify-end gap-1">
                                <button
                                  onClick={() => {
                                    setPaymentToEdit(payment);
                                    setShowPaymentModal(true);
                                  }}
                                  className="p-1 text-blue-600 hover:bg-blue-50 rounded transition-colors"
                                  title="Editar pago"
                                >
                                  <Edit2 className="w-3.5 h-3.5" />
                                </button>
                                <button
                                  onClick={() =>
                                    askConfirmation({
                                      title: "Eliminar pago?",
                                      message: `Seguro que deseas eliminar el pago de ${payment.amount?.toLocaleString("es-AR", { style: "currency", currency: "ARS" })}?`,
                                      type: "danger",
                                      onConfirm: () => handleDeletePayment(payment),
                                    })
                                  }
                                  className="p-1 text-rose-600 hover:bg-rose-50 rounded transition-colors"
                                  title="Eliminar pago"
                                >
                                  <Trash2 className="w-3.5 h-3.5" />
                                </button>
                              </div>
                            </DataGridCell>
                          </DataGridRow>
                        ))
                      ) : (
                        <DataGridEmptyState colSpan={6} title="No hay pagos registrados." />
                      )}
                    </DataGridBody>
                  </DataGrid>
                  {reserva.payments?.length > 0 ? (
                    <MobileRecordList className="p-4 md:hidden">
                      {reserva.payments.map((payment) => (
                        <MobileRecordCard
                          key={getPublicId(payment)}
                          title={payment.method}
                          subtitle={new Date(payment.paidAt).toLocaleDateString()}
                          meta={
                            <>
                              <div className="text-xs text-slate-500 dark:text-slate-400">{payment.notes || "Sin notas"}</div>
                              <div>
                                <PaymentReceiptActions payment={payment} onView={handleViewReceiptPdf} onIssue={handleIssueReceipt} onVoid={handleVoidReceipt} />
                              </div>
                            </>
                          }
                          footer={<span className="text-sm font-bold text-emerald-600">{payment.amount?.toLocaleString("es-AR", { style: "currency", currency: "ARS" })}</span>}
                        />
                      ))}
                    </MobileRecordList>
                  ) : (
                    <ListEmptyState
                      title="No hay pagos registrados."
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

      <PaymentModal
        isOpen={showPaymentModal}
        onClose={() => {
          setShowPaymentModal(false);
          setPaymentToEdit(null);
        }}
        reservaId={publicId}
        maxAmount={reserva?.balance}
        paymentToEdit={paymentToEdit}
        onSuccess={() => fetchReserva({ showLoading: false, preserveOnError: true })}
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
        onClose={() => setConfirmConfig((prev) => ({ ...prev, isOpen: false }))}
      />

      {confirmReservaModal.isOpen && (
        <ConfirmReservaModal
          reserva={reserva}
          readiness={confirmReservaModal.readiness}
          // Con ciclo extendido (flag ON) el target es "Sold"; con ciclo base es "Confirmed".
          targetStatus={confirmReservaModal.targetStatus || "Confirmed"}
          onClose={() => setConfirmReservaModal({ isOpen: false, readiness: null, targetStatus: "Confirmed" })}
          onConfirmed={() => {
            setConfirmReservaModal({ isOpen: false, readiness: null, targetStatus: "Confirmed" });
            fetchReserva({ showLoading: false, preserveOnError: true });
          }}
        />
      )}

      {showRevertModal && (
        <RevertStatusModal
          reserva={reserva}
          onClose={() => setShowRevertModal(false)}
          onReverted={() => {
            setShowRevertModal(false);
            fetchReserva({ showLoading: false, preserveOnError: true });
          }}
        />
      )}

      <EditReservaDatesModal
        isOpen={showEditDatesModal}
        reserva={reserva}
        onClose={() => setShowEditDatesModal(false)}
        onSave={handleSaveReservaDates}
      />

      {/* Modal de cancelacion de reserva.
          Se monta siempre pero permanece cerrado (isOpen=false) hasta que el agente
          hace click en el boton "Cancelar reserva" del header. Patron identico al
          modal de ConfirmReserva. */}
      {reserva && (
        <CancelReservaModal
          reserva={reserva}
          isOpen={showCancelModal}
          onClose={() => setShowCancelModal(false)}
          onCancelled={() => {
            setShowCancelModal(false);
            // Recargamos la reserva para reflejar el nuevo estado (Cancelled).
            fetchReserva({ showLoading: false, preserveOnError: true });
          }}
        />
      )}
    </div>
  );
}
