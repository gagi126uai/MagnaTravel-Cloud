import { useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Clock, CreditCard, ExternalLink, FileText, History, Paperclip, Receipt, Users, Trash2, Edit2 } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import ReservaTimeline from "../../../components/ReservaTimeline";
import ConfirmModal from "../../../components/ConfirmModal";
import PassengerFormModal from "../../../components/PassengerFormModal";
import PaymentModal from "../../../components/PaymentModal";
import { ReservaDocumentsTab } from "../../../components/ReservaDocumentsTab";
import ServiceFormModal from "../../../components/ServiceFormModal";
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
import { PassengerList } from "../components/PassengerList";
import { ReservaHeader } from "../components/ReservaHeader";
import { ReservaSummaryStrip } from "../components/ReservaSummaryStrip";
import { ServiceList } from "../components/ServiceList";
import { useReservaDetail } from "../hooks/useReservaDetail";

function InvoiceStatusBadge({ resultado }) {
  return (
    <span className={`rounded px-2 py-0.5 text-[10px] font-black uppercase ${resultado === "A" ? "bg-emerald-100 text-emerald-700" : "bg-rose-100 text-rose-700"}`}>
      {resultado === "A" ? "Aceptada" : "Error"}
    </span>
  );
}

function InvoiceTypeLabel({ tipoComprobante }) {
  return <>Factura {tipoComprobante === 1 ? "A" : tipoComprobante === 6 ? "B" : "C"}</>;
}

function getPaymentReceipt(payment) {
  return payment?.receipt || payment?.Receipt || null;
}

function canIssuePaymentReceipt(payment) {
  const entryType = payment?.entryType || payment?.EntryType || "Payment";
  const receipt = getPaymentReceipt(payment);
  return entryType === "Payment" && Number(payment?.amount || payment?.Amount || 0) > 0 && !receipt;
}

function PaymentReceiptActions({ payment, onView, onIssue }) {
  const receipt = getPaymentReceipt(payment);

  if (receipt) {
    const isVoided = receipt.status === "Voided";
    return (
      <div className="flex flex-wrap items-center gap-2">
        <span className={`rounded-full px-2 py-1 text-[10px] font-black uppercase ${isVoided ? "bg-slate-100 text-slate-500 dark:bg-slate-800 dark:text-slate-400" : "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300"}`}>
          {isVoided ? "Anulado" : receipt.receiptNumber}
        </span>
        {!isVoided ? (
          <button
            type="button"
            onClick={() => onView(payment)}
            className="inline-flex items-center gap-1 rounded-lg px-2 py-1 text-xs font-bold text-indigo-600 transition-colors hover:bg-indigo-50 dark:text-indigo-300 dark:hover:bg-indigo-900/30"
            title="Ver comprobante de pago"
          >
            <ExternalLink className="h-3.5 w-3.5" />
            Ver PDF
          </button>
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

function PassengerCountsWidget({ initial, onSave }) {
  const [adultCount, setAdultCount] = useState(initial.adultCount);
  const [childCount, setChildCount] = useState(initial.childCount);
  const [infantCount, setInfantCount] = useState(initial.infantCount);
  const [saving, setSaving] = useState(false);

  const total = (adultCount || 0) + (childCount || 0) + (infantCount || 0);
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
        En estado Presupuesto solo se cargan cantidades. Al pasar a Reservado podras cargar cada pasajero con nombre y documento.
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
        <div className="text-sm font-bold text-slate-700 dark:text-slate-200">Total: {total} pasajeros</div>
        <button
          type="button"
          disabled={!dirty || saving}
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
  const [showServiceModal, setShowServiceModal] = useState(false);
  const [serviceToEdit, setServiceToEdit] = useState(null);
  const [showPassengerForm, setShowPassengerForm] = useState(false);
  const [editingPassenger, setEditingPassenger] = useState(null);
  const [showPaymentModal, setShowPaymentModal] = useState(false);
  const [paymentToEdit, setPaymentToEdit] = useState(null);
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
    allServices,
    hotelCapacity,
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

  const handleSavePassengerCounts = async (counts) => {
    try {
      await api.patch(`/reservas/${publicId}/passenger-counts`, counts);
      showSuccess("Cantidades actualizadas");
      await fetchReserva({ showLoading: false, preserveOnError: true });
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudieron actualizar las cantidades."));
    }
  };

  if (loading) {
    return <div className="animate-pulse p-8 text-center text-slate-500">Cargando reserva...</div>;
  }

  const isBudget = reserva?.status === "Presupuesto";

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
          if (newStatus === "Presupuesto" && reserva.status === "Reservado") {
            askConfirmation({
              title: "Deshacer reserva?",
              message: "Seguro que deseas volver el estado a 'Presupuesto'?",
              type: "warning",
              onConfirm: () => handleStatusChange(newStatus),
            });
          } else {
            handleStatusChange(newStatus);
          }
        }}
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
      />

      <ReservaSummaryStrip reserva={reserva} />

      {isBudget ? (
        <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-900/40 dark:bg-amber-950/30 dark:text-amber-200">
          <strong className="font-bold">Reserva en modo Presupuesto.</strong> Pasala a Reservado para cargar pasajeros nominales y registrar pagos.
        </div>
      ) : null}

      <CapacityWarning paxCount={reserva.passengers?.length || 0} hotelCapacity={hotelCapacity} />

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
              isBudget
                ? { id: "passengers", label: `Cantidades (${(reserva.adultCount || 0) + (reserva.childCount || 0) + (reserva.infantCount || 0)})`, icon: Users }
                : { id: "passengers", label: `Pasajeros (${reserva.passengers?.length || 0})`, icon: Users },
              { id: "history", label: "Historial", icon: Clock },
              isBudget ? null : { id: "account", label: "Estado de Cuenta", icon: CreditCard },
              { id: "voucher", label: "Vouchers", icon: FileText },
              { id: "attachments", label: "Documentos", icon: Paperclip },
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
            <ServiceList
              services={allServices}
              serviceCollectionErrors={serviceCollectionErrors}
              onAddService={() => {
                setServiceToEdit(null);
                setShowServiceModal(true);
              }}
              onEditService={(service) => {
                setServiceToEdit(service);
                setShowServiceModal(true);
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
          ) : null}

          {activeTab === "passengers" ? (
            isBudget ? (
              <PassengerCountsWidget
                initial={{
                  adultCount: reserva.adultCount || 0,
                  childCount: reserva.childCount || 0,
                  infantCount: reserva.infantCount || 0,
                }}
                onSave={handleSavePassengerCounts}
              />
            ) : (
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
            )
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
                              <PaymentReceiptActions payment={payment} onView={handleViewReceiptPdf} onIssue={handleIssueReceipt} />
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
                                <PaymentReceiptActions payment={payment} onView={handleViewReceiptPdf} onIssue={handleIssueReceipt} />
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
                  <DataGrid density="compact" minWidth="760px">
                    <DataGridHeader>
                      <DataGridHeaderRow>
                        <DataGridHeaderCell>Tipo</DataGridHeaderCell>
                        <DataGridHeaderCell>Numero</DataGridHeaderCell>
                        <DataGridHeaderCell>CAE</DataGridHeaderCell>
                        <DataGridHeaderCell>Estado</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Importe</DataGridHeaderCell>
                      </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                      {reserva.invoices?.length > 0 ? (
                        reserva.invoices.map((invoice) => (
                          <DataGridRow key={getPublicId(invoice)}>
                            <DataGridCell className="font-bold">
                              <InvoiceTypeLabel tipoComprobante={invoice.tipoComprobante} />
                            </DataGridCell>
                            <DataGridCell className="font-mono">
                              {String(invoice.puntoDeVenta).padStart(5, "0")}-{String(invoice.numeroComprobante).padStart(8, "0")}
                            </DataGridCell>
                            <DataGridCell className="font-mono text-xs text-slate-400">{invoice.cae || "---"}</DataGridCell>
                            <DataGridCell>
                              <InvoiceStatusBadge resultado={invoice.resultado} />
                            </DataGridCell>
                            <DataGridCell align="right" className="font-black">
                              {invoice.importeTotal?.toLocaleString("es-AR", { style: "currency", currency: "ARS" })}
                            </DataGridCell>
                          </DataGridRow>
                        ))
                      ) : (
                        <DataGridEmptyState colSpan={5} title="No hay facturas emitidas para esta reserva." />
                      )}
                    </DataGridBody>
                  </DataGrid>
                  {reserva.invoices?.length > 0 ? (
                    <MobileRecordList className="p-4 md:hidden">
                      {reserva.invoices.map((invoice) => (
                        <MobileRecordCard
                          key={getPublicId(invoice)}
                          statusSlot={<InvoiceStatusBadge resultado={invoice.resultado} />}
                          title={<InvoiceTypeLabel tipoComprobante={invoice.tipoComprobante} />}
                          subtitle={`${String(invoice.puntoDeVenta).padStart(5, "0")}-${String(invoice.numeroComprobante).padStart(8, "0")}`}
                          meta={<div className="text-xs text-slate-500 dark:text-slate-400">CAE {invoice.cae || "---"}</div>}
                          footer={<span className="text-sm font-bold text-slate-900 dark:text-white">{invoice.importeTotal?.toLocaleString("es-AR", { style: "currency", currency: "ARS" })}</span>}
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
    </div>
  );
}
