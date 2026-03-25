import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  ArrowLeft,
  Eye,
  Loader2,
  Mail,
  Phone,
  Plus,
  Receipt,
  Search,
  Trash2,
} from "lucide-react";
import Swal from "sweetalert2";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import CustomerPaymentModal from "../../../components/CustomerPaymentModal";
import { Button } from "../../../components/ui/button";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { useDebounce } from "../../../hooks/useDebounce";
import { isDatabaseUnavailableError } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";

const emptyPage = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
  hasPreviousPage: false,
  hasNextPage: false,
};

const defaultPagingState = {
  reservas: { page: 1, pageSize: 25 },
  payments: { page: 1, pageSize: 25 },
  invoices: { page: 1, pageSize: 25 },
};

const defaultPageState = {
  reservas: emptyPage,
  payments: emptyPage,
  invoices: emptyPage,
};

const tabLabels = {
  reservas: "Reservas",
  payments: "Pagos",
  invoices: "Facturacion AFIP",
};

const formatCurrency = (value) =>
  new Intl.NumberFormat("es-AR", {
    style: "currency",
    currency: "ARS",
    minimumFractionDigits: 0,
  }).format(value || 0);

const formatDate = (dateString) => {
  if (!dateString) {
    return "-";
  }

  return new Date(dateString).toLocaleDateString("es-AR");
};

const formatInvoiceNumber = (invoice) =>
  `${String(invoice.puntoDeVenta ?? 0).padStart(5, "0")}-${String(invoice.numeroComprobante ?? 0).padStart(8, "0")}`;

const formatInvoiceType = (invoice) => {
  switch (invoice.tipoComprobante) {
    case 1:
      return "Factura A";
    case 6:
      return "Factura B";
    case 11:
      return "Factura C";
    case 3:
      return "Nota de Credito A";
    case 8:
      return "Nota de Credito B";
    case 13:
      return "Nota de Credito C";
    default:
      return `Tipo ${invoice.tipoComprobante}`;
  }
};

const escapeHtml = (value) =>
  String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");

const renderInvoiceTab = (previewWindow, { title, body }) => {
  if (!previewWindow || previewWindow.closed) {
    return;
  }

  previewWindow.document.open();
  previewWindow.document.write(`<!doctype html><html lang="es"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" /><title>${escapeHtml(title)}</title><style>:root{color-scheme:light;font-family:Inter,system-ui,sans-serif;background:#e2e8f0;color:#0f172a}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:linear-gradient(180deg,#f8fafc 0%,#e2e8f0 100%)}.shell{min-height:100vh;display:flex;flex-direction:column}.header{padding:16px 20px;border-bottom:1px solid #cbd5e1;background:rgba(255,255,255,.96);backdrop-filter:blur(10px)}.eyebrow{margin:0 0 6px;font-size:11px;font-weight:800;letter-spacing:.14em;text-transform:uppercase;color:#4f46e5}.title{margin:0;font-size:20px;font-weight:700}.subtitle{margin:6px 0 0;font-size:14px;color:#475569}.content{flex:1;padding:20px}.panel{height:calc(100vh - 117px);border:1px solid #cbd5e1;border-radius:18px;overflow:hidden;background:#fff;box-shadow:0 20px 50px rgba(15,23,42,.15)}.state{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;padding:24px;text-align:center}.state-title{margin:0;font-size:18px;font-weight:700}.state-text{margin:0;max-width:480px;color:#475569;line-height:1.5}.spinner{width:42px;height:42px;border:4px solid #cbd5e1;border-top-color:#4f46e5;border-radius:999px;animation:spin .9s linear infinite}iframe{width:100%;height:100%;border:0;background:#fff}@keyframes spin{to{transform:rotate(360deg)}}</style></head><body>${body}</body></html>`);
  previewWindow.document.close();
};

function StatusBadge({ status }) {
  const colors = {
    Presupuesto: "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-400",
    Reservado: "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-400",
    Operativo: "bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400",
    Cerrado: "bg-slate-100 text-slate-800 dark:bg-slate-700 dark:text-slate-300",
    Cancelado: "bg-rose-100 text-rose-800 dark:bg-rose-900/30 dark:text-rose-400",
  };

  return (
    <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${colors[status] || "bg-slate-100 text-slate-800"}`}>
      {status}
    </span>
  );
}

export default function CustomerAccountPage() {
  const { publicId } = useParams();
  const navigate = useNavigate();
  const [overview, setOverview] = useState(null);
  const [pages, setPages] = useState(defaultPageState);
  const [paging, setPaging] = useState(defaultPagingState);
  const [tabLoading, setTabLoading] = useState({
    reservas: true,
    payments: true,
    invoices: true,
  });
  const [loadingOverview, setLoadingOverview] = useState(true);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);
  const [activeTab, setActiveTab] = useState("reservas");
  const [searchTerm, setSearchTerm] = useState("");
  const [reservaOptions, setReservaOptions] = useState([]);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [paymentToEdit, setPaymentToEdit] = useState(null);
  const debouncedSearch = useDebounce(searchTerm, 300);

  const loadOverview = useCallback(async () => {
    setLoadingOverview(true);
    try {
      const response = await api.get(`/customers/${publicId}/account`);
      setOverview(response);
      setDatabaseUnavailable(false);
    } catch (error) {
      setOverview(null);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("No se pudo cargar la cuenta corriente.");
    } finally {
      setLoadingOverview(false);
    }
  }, [publicId]);

  const loadReservaOptions = useCallback(async () => {
    try {
      const response = await api.get(
        `/customers/${publicId}/account/reservas?page=1&pageSize=100&sortBy=createdAt&sortDir=desc`
      );
      setReservaOptions(response?.items || []);
      setDatabaseUnavailable(false);
    } catch (error) {
      setReservaOptions([]);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
    }
  }, [publicId]);

  const loadTab = useCallback(
    async (tabKey) => {
      setTabLoading((current) => ({ ...current, [tabKey]: true }));

      try {
        const tabPaging = paging[tabKey];
        const params = new URLSearchParams({
          page: String(tabPaging.page),
          pageSize: String(tabPaging.pageSize),
          sortBy: tabKey === "payments" ? "paidAt" : "createdAt",
          sortDir: "desc",
        });

        if (debouncedSearch.trim()) {
          params.set("search", debouncedSearch.trim());
        }

        const response = await api.get(`/customers/${publicId}/account/${tabKey}?${params.toString()}`);
        setPages((current) => ({
          ...current,
          [tabKey]: { ...emptyPage, ...(response || {}) },
        }));
        setDatabaseUnavailable(false);
      } catch (error) {
        setPages((current) => ({
          ...current,
          [tabKey]: emptyPage,
        }));
        setDatabaseUnavailable(isDatabaseUnavailableError(error));
        showError(`No se pudo cargar ${tabLabels[tabKey].toLowerCase()}.`);
      } finally {
        setTabLoading((current) => ({ ...current, [tabKey]: false }));
      }
    },
    [debouncedSearch, paging, publicId]
  );

  const refreshAll = useCallback(async () => {
    await Promise.all([
      loadOverview(),
      loadReservaOptions(),
      loadTab("reservas"),
      loadTab("payments"),
      loadTab("invoices"),
    ]);
  }, [loadOverview, loadReservaOptions, loadTab]);

  useEffect(() => {
    setPaging(defaultPagingState);
    setPages(defaultPageState);
    setSearchTerm("");
    setActiveTab("reservas");
  }, [publicId]);

  useEffect(() => {
    loadOverview();
    loadReservaOptions();
  }, [loadOverview, loadReservaOptions]);

  useEffect(() => {
    loadTab(activeTab);
  }, [activeTab, loadTab]);

  useEffect(() => {
    setPaging((current) => ({
      ...current,
      [activeTab]: {
        ...current[activeTab],
        page: 1,
      },
    }));
  }, [activeTab, debouncedSearch]);

  const handleOpenModal = (payment = null) => {
    setPaymentToEdit(payment);
    setIsModalOpen(true);
  };

  const handleDeletePayment = async (payment) => {
    const result = await Swal.fire({
      title: "Eliminar pago?",
      text: `Se anulara el pago de ${formatCurrency(payment.amount)} y la deuda volvera a la reserva.`,
      icon: "warning",
      showCancelButton: true,
      confirmButtonText: "Si, eliminar",
      cancelButtonText: "Cancelar",
      confirmButtonColor: "#ef4444",
    });

    if (!result.isConfirmed || !payment.reservaPublicId) {
      return;
    }

    try {
      await api.delete(`/reservas/${payment.reservaPublicId}/payments/${getPublicId(payment)}`);
      await refreshAll();
    } catch (error) {
      console.error(error);
      showError("No se pudo eliminar el pago.");
    }
  };

  const handleOpenInvoicePreview = async (invoice) => {
    const previewWindow = window.open("", "_blank");
    if (!previewWindow) {
      showError("El navegador bloqueo la apertura de la factura.");
      return;
    }

    previewWindow.opener = null;
    const invoiceTitle = `${formatInvoiceType(invoice)} ${formatInvoiceNumber(invoice)}`;

    renderInvoiceTab(previewWindow, {
      title: invoiceTitle,
      body: `
        <div class="shell">
          <div class="header">
            <p class="eyebrow">Facturacion AFIP</p>
            <h1 class="title">${escapeHtml(invoiceTitle)}</h1>
            <p class="subtitle">Preparando la factura para mostrarla en esta pestaña.</p>
          </div>
          <div class="content">
            <div class="panel">
              <div class="state">
                <div class="spinner"></div>
                <p class="state-title">Cargando factura...</p>
                <p class="state-text">Estamos obteniendo el PDF autenticado para abrirlo fuera de la cuenta corriente.</p>
              </div>
            </div>
          </div>
        </div>
      `,
    });

    try {
      const blob = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      if (!(blob instanceof Blob) || blob.size === 0) {
        throw new Error("La factura no devolvio un PDF valido.");
      }

      const pdfUrl = URL.createObjectURL(blob);
      const releaseTimer = window.setInterval(() => {
        if (previewWindow.closed) {
          URL.revokeObjectURL(pdfUrl);
          window.clearInterval(releaseTimer);
        }
      }, 1000);

      renderInvoiceTab(previewWindow, {
        title: invoiceTitle,
        body: `
          <div class="shell">
            <div class="header">
              <p class="eyebrow">Facturacion AFIP</p>
              <h1 class="title">${escapeHtml(invoiceTitle)}</h1>
              <p class="subtitle">Vista de la factura emitida en AFIP.</p>
            </div>
            <div class="content">
              <div class="panel">
                <iframe src="${pdfUrl}" title="${escapeHtml(invoiceTitle)}"></iframe>
              </div>
            </div>
          </div>
        `,
      });
    } catch (error) {
      renderInvoiceTab(previewWindow, {
        title: invoiceTitle,
        body: `
          <div class="shell">
            <div class="header">
              <p class="eyebrow">Facturacion AFIP</p>
              <h1 class="title">${escapeHtml(invoiceTitle)}</h1>
              <p class="subtitle">No fue posible abrir la factura.</p>
            </div>
            <div class="content">
              <div class="panel">
                <div class="state">
                  <p class="state-title">No se pudo cargar la factura</p>
                  <p class="state-text">${escapeHtml(error?.message || "El servidor no devolvio un PDF valido.")}</p>
                </div>
              </div>
            </div>
          </div>
        `,
      });
    }
  };

  const currentPage = pages[activeTab] || emptyPage;
  const currentPaging = paging[activeTab] || defaultPagingState.reservas;
  const currentTabLoading = tabLoading[activeTab];
  const reservas = pages.reservas.items || [];
  const payments = pages.payments.items || [];
  const invoices = pages.invoices.items || [];
  const summary = overview?.summary || {};
  const customer = overview?.customer;

  const availableReservas = useMemo(
    () => reservaOptions.filter((reserva) => reserva.status !== "Cancelado"),
    [reservaOptions]
  );

  const updateCurrentPage = (page) => {
    setPaging((current) => ({
      ...current,
      [activeTab]: {
        ...current[activeTab],
        page,
      },
    }));
  };

  const updateCurrentPageSize = (pageSize) => {
    setPaging((current) => ({
      ...current,
      [activeTab]: {
        page: 1,
        pageSize,
      },
    }));
  };

  if (loadingOverview) {
    return <AccountPageSkeleton />;
  }

  if (!overview && !databaseUnavailable) {
    return <div className="py-12 text-center text-muted-foreground">No se encontraron datos del cliente.</div>;
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-col justify-between gap-4 sm:flex-row sm:items-center">
        <div className="flex items-center gap-4">
          <Button variant="ghost" size="icon" onClick={() => navigate("/customers")}>
            <ArrowLeft className="h-5 w-5" />
          </Button>
          <div>
            <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white">Cuenta Corriente</h1>
            <p className="text-muted-foreground">{customer?.fullName}</p>
          </div>
        </div>

        <Button
          onClick={() => handleOpenModal(null)}
          className="gap-2 bg-emerald-600 text-white shadow-lg shadow-emerald-500/20 hover:bg-emerald-700"
          disabled={databaseUnavailable}
        >
          <Plus className="h-4 w-4" />
          Nueva Cobranza
        </Button>
      </div>

      <div className="grid gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <div className="rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900/50">
          <div className="flex items-start gap-4">
            <div className="flex h-16 w-16 items-center justify-center rounded-full bg-gradient-to-br from-indigo-500 to-cyan-500 text-2xl font-bold text-white shadow-md">
              {customer?.fullName?.charAt(0)?.toUpperCase() || "?"}
            </div>
            <div className="space-y-2">
              <h2 className="text-xl font-bold text-slate-900 dark:text-white">{customer?.fullName}</h2>
              <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-slate-500">
                {customer?.email && (
                  <span className="flex items-center gap-1">
                    <Mail className="h-3.5 w-3.5" /> {customer.email}
                  </span>
                )}
                {customer?.phone && (
                  <span className="flex items-center gap-1">
                    <Phone className="h-3.5 w-3.5" /> {customer.phone}
                  </span>
                )}
                {customer?.taxId && <span>CUIT/DNI {customer.taxId}</span>}
              </div>
            </div>
          </div>

          <div className="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Ventas</div>
              <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{formatCurrency(summary.totalSales)}</div>
            </div>
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Cobrado</div>
              <div className="mt-1 text-lg font-bold text-emerald-600 dark:text-emerald-400">{formatCurrency(summary.totalPaid)}</div>
            </div>
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Reservas</div>
              <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{summary.reservaCount || 0}</div>
            </div>
            <div className="rounded-xl bg-slate-50 p-4 dark:bg-slate-950/60">
              <div className="text-xs font-semibold uppercase tracking-wider text-slate-400">Facturas</div>
              <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{summary.invoiceCount || 0}</div>
            </div>
          </div>
        </div>

        <div
          className={`rounded-xl border p-6 shadow-sm ${
            Number(summary.totalBalance || 0) > 0
              ? "border-rose-100 bg-rose-50 dark:border-rose-900/30 dark:bg-rose-900/10"
              : "border-emerald-100 bg-emerald-50 dark:border-emerald-900/30 dark:bg-emerald-900/10"
          }`}
        >
          <div className="text-sm font-medium text-slate-500 dark:text-slate-400">Saldo Actual</div>
          <div
            className={`mt-1 text-3xl font-bold ${
              Number(summary.totalBalance || 0) > 0
                ? "text-rose-600 dark:text-rose-400"
                : "text-emerald-600 dark:text-emerald-400"
            }`}
          >
            {formatCurrency(summary.totalBalance)}
          </div>
          <div className="mt-2 text-xs font-medium text-slate-400">
            {Number(summary.totalBalance || 0) > 0 ? "Deuda pendiente" : "Al dia / A favor"}
          </div>
          <div className="mt-6 grid gap-3 sm:grid-cols-2">
            <div className="rounded-lg bg-white/70 p-3 dark:bg-slate-900/60">
              <div className="text-[11px] uppercase tracking-wider text-slate-400">Pagos</div>
              <div className="text-lg font-semibold text-slate-900 dark:text-white">{summary.paymentCount || 0}</div>
            </div>
            <div className="rounded-lg bg-white/70 p-3 dark:bg-slate-900/60">
              <div className="text-[11px] uppercase tracking-wider text-slate-400">Limite credito</div>
              <div className="text-lg font-semibold text-slate-900 dark:text-white">{formatCurrency(customer?.creditLimit)}</div>
            </div>
          </div>
        </div>
      </div>

      <div className="flex flex-col gap-4">
        <div className="flex flex-wrap gap-6 border-b border-slate-200 dark:border-slate-800">
          {[
            { key: "reservas", count: summary.reservaCount || 0 },
            { key: "payments", count: summary.paymentCount || 0 },
            { key: "invoices", count: summary.invoiceCount || 0 },
          ].map((tab) => (
            <button
              key={tab.key}
              type="button"
              onClick={() => setActiveTab(tab.key)}
              className={`relative pb-4 text-sm font-semibold transition-colors ${
                activeTab === tab.key
                  ? "text-indigo-600 dark:text-indigo-400"
                  : "text-slate-500 hover:text-slate-700 dark:hover:text-slate-300"
              }`}
            >
              <span className="flex items-center gap-2">
                {tabLabels[tab.key]}
                <span className="rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-slate-800">
                  {tab.count}
                </span>
              </span>
              {activeTab === tab.key && (
                <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-full bg-indigo-600 dark:bg-indigo-400" />
              )}
            </button>
          ))}
        </div>

        <div className="flex justify-end">
          <div className="relative min-w-[260px]">
            <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              type="text"
              placeholder={`Buscar en ${tabLabels[activeTab].toLowerCase()}...`}
              value={searchTerm}
              onChange={(event) => setSearchTerm(event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
            />
          </div>
        </div>
      </div>

      {databaseUnavailable ? (
        <DatabaseUnavailableState />
      ) : currentTabLoading && currentPage.items.length === 0 ? (
        <div className="flex h-48 items-center justify-center text-slate-400">
          <Loader2 className="h-8 w-8 animate-spin" />
        </div>
      ) : (
        <>
          {activeTab === "reservas" && (
            <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead className="border-b border-slate-200 bg-slate-50 text-slate-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400">
                    <tr>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Fecha</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Reserva</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Estado</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Venta</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Cobrado</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Saldo</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Accion</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                    {reservas.map((reserva) => (
                      <tr key={getPublicId(reserva)} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30">
                        <td className="px-6 py-4 text-slate-500 dark:text-slate-400">
                          {formatDate(reserva.startDate || reserva.createdAt)}
                        </td>
                        <td className="px-6 py-4">
                          <div className="font-semibold text-slate-900 dark:text-white">{reserva.numeroReserva}</div>
                          <div className="text-xs text-slate-500 dark:text-slate-400">{reserva.name}</div>
                        </td>
                        <td className="px-6 py-4">
                          <StatusBadge status={reserva.status} />
                        </td>
                        <td className="px-6 py-4 text-right font-semibold text-slate-900 dark:text-white">
                          {formatCurrency(reserva.totalSale)}
                        </td>
                        <td className="px-6 py-4 text-right font-semibold text-emerald-600 dark:text-emerald-400">
                          {formatCurrency(reserva.paid)}
                        </td>
                        <td className="px-6 py-4 text-right font-semibold text-rose-600 dark:text-rose-400">
                          {formatCurrency(reserva.balance)}
                        </td>
                        <td className="px-6 py-4 text-right">
                          <Link
                            to={`/reservas/${getPublicId(reserva)}`}
                            className="inline-flex rounded-lg p-2 text-indigo-500 hover:bg-indigo-50 dark:hover:bg-slate-800"
                          >
                            <Eye className="h-4 w-4" />
                          </Link>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {reservas.length === 0 && (
                <div className="px-6 py-12 text-center text-sm text-slate-500 dark:text-slate-400">
                  No hay reservas para mostrar.
                </div>
              )}
            </div>
          )}

          {activeTab === "payments" && (
            <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead className="border-b border-slate-200 bg-slate-50 text-slate-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400">
                    <tr>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Fecha</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Reserva</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Metodo</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Monto</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Notas</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Accion</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                    {payments.map((payment) => (
                      <tr key={getPublicId(payment)} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30">
                        <td className="px-6 py-4 text-slate-500 dark:text-slate-400">{formatDate(payment.paidAt)}</td>
                        <td className="px-6 py-4">
                          {payment.reservaPublicId ? (
                            <Link to={`/reservas/${payment.reservaPublicId}`} className="font-semibold text-indigo-600 hover:text-indigo-700">
                              {payment.numeroReserva || "Reserva"}
                            </Link>
                          ) : (
                            <span className="text-slate-500 dark:text-slate-400">{payment.numeroReserva || "Sin reserva"}</span>
                          )}
                          <div className="text-xs text-slate-500 dark:text-slate-400">{payment.fileName}</div>
                        </td>
                        <td className="px-6 py-4 text-slate-700 dark:text-slate-300">{payment.method}</td>
                        <td className="px-6 py-4 text-right font-semibold text-emerald-600 dark:text-emerald-400">
                          {formatCurrency(payment.amount)}
                        </td>
                        <td className="px-6 py-4 text-slate-500 dark:text-slate-400">{payment.notes || "-"}</td>
                        <td className="px-6 py-4 text-right">
                          <button
                            type="button"
                            onClick={() => handleDeletePayment(payment)}
                            className="inline-flex rounded-lg p-2 text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20"
                            title="Eliminar pago"
                          >
                            <Trash2 className="h-4 w-4" />
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {payments.length === 0 && (
                <div className="px-6 py-12 text-center text-sm text-slate-500 dark:text-slate-400">
                  No hay pagos para mostrar.
                </div>
              )}
            </div>
          )}

          {activeTab === "invoices" && (
            <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead className="border-b border-slate-200 bg-slate-50 text-slate-500 dark:border-slate-800 dark:bg-slate-950 dark:text-slate-400">
                    <tr>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Fecha</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Comprobante</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase">Tipo</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Importe</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-center">Estado</th>
                      <th className="px-6 py-3 text-[10px] font-bold uppercase text-right">Accion</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                    {invoices.map((invoice) => (
                      <tr key={getPublicId(invoice)} className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30">
                        <td className="px-6 py-4 text-slate-500 dark:text-slate-400">{formatDate(invoice.createdAt)}</td>
                        <td className="px-6 py-4 font-semibold text-slate-900 dark:text-white">{formatInvoiceNumber(invoice)}</td>
                        <td className="px-6 py-4">
                          <div className="flex items-center gap-2">
                            <Receipt className="h-4 w-4 text-indigo-400" />
                            <span>{formatInvoiceType(invoice)}</span>
                          </div>
                        </td>
                        <td className="px-6 py-4 text-right font-semibold text-slate-900 dark:text-white">
                          {formatCurrency(invoice.importeTotal)}
                        </td>
                        <td className="px-6 py-4 text-center">
                          <span
                            className={`rounded-full px-2 py-0.5 text-[10px] font-bold uppercase ${
                              invoice.resultado === "A"
                                ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400"
                                : "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300"
                            }`}
                          >
                            {invoice.resultado === "A" ? "Autorizado" : invoice.resultado || "Pendiente"}
                          </span>
                        </td>
                        <td className="px-6 py-4 text-right">
                          <button
                            type="button"
                            onClick={() => handleOpenInvoicePreview(invoice)}
                            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-slate-600 transition-colors hover:bg-slate-100 hover:text-slate-900 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800 dark:hover:text-white"
                          >
                            <Eye className="h-4 w-4" />
                            Ver
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              {invoices.length === 0 && (
                <div className="px-6 py-12 text-center text-sm text-slate-500 dark:text-slate-400">
                  No hay facturas para mostrar.
                </div>
              )}
            </div>
          )}

          <PaginationFooter
            page={currentPage.page || currentPaging.page}
            pageSize={currentPage.pageSize || currentPaging.pageSize}
            totalCount={currentPage.totalCount || 0}
            totalPages={currentPage.totalPages || 0}
            hasPreviousPage={Boolean(currentPage.hasPreviousPage)}
            hasNextPage={Boolean(currentPage.hasNextPage)}
            onPageChange={updateCurrentPage}
            onPageSizeChange={updateCurrentPageSize}
          />
        </>
      )}

      <CustomerPaymentModal
        isOpen={isModalOpen}
        onClose={() => {
          setIsModalOpen(false);
          setPaymentToEdit(null);
        }}
        paymentToEdit={paymentToEdit}
        customerId={publicId}
        availableReservas={availableReservas}
        onSave={refreshAll}
      />
    </div>
  );
}
