import { useEffect, useState, useCallback } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import Swal from "sweetalert2";
import {
  Search,
  CreditCard,
  Banknote,
  ArrowUpRight,
  Filter,
  DollarSign,
  FileText,
  Clock,
  Briefcase,
  AlertTriangle,
  CheckCircle,
  Download,
  Plus,
  Loader2,
  XCircle,
  Calculator
} from "lucide-react";
import { Button } from "../components/ui/button";
import PaymentModal from "../components/PaymentModal";
import CreateInvoiceModal from "../components/CreateInvoiceModal";

export default function PaymentsPage() {
  const [activeTab, setActiveTab] = useState("collections"); // collections, invoicing, history
  const [loading, setLoading] = useState(true);

  // Data States
  const [globalFiles, setGlobalFiles] = useState([]);
  const [payments, setPayments] = useState([]);
  const [invoices, setInvoices] = useState([]);

  // Modals
  const [showPaymentModal, setShowPaymentModal] = useState(false);
  const [showInvoiceModal, setShowInvoiceModal] = useState(false);
  const [selectedFile, setSelectedFile] = useState(null);
  const [creatingInvoice, setCreatingInvoice] = useState(false);

  // Filters
  const [searchTerm, setSearchTerm] = useState("");

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      // 1. Fetch EVERYTHING needed for the Control Tower
      const [filesRes, invoicesRes] = await Promise.all([
        api.get("/travelfiles"),
        api.get("/invoices")
      ]);

      // Enhance files with calculated fields for the "Control Tower"
      const enhancedFiles = filesRes.map(f => {
        // Filter invoices for this file
        const fileInvoices = invoicesRes.filter(i => i.travelFileId === f.id);

        // Attach to file for downstream logic
        f.invoices = fileInvoices;

        const totalSale = f.totalSale || 0;
        const totalPaid = f.payments?.filter(p => p.status !== 'Cancelled').reduce((acc, p) => acc + p.amount, 0) || 0;

        // Calculate invoiced amount from ACTUAL invoices
        const totalInvoiced = fileInvoices.reduce((acc, i) => {
          if (i.resultado !== 'A') return acc; // Ignore Rejected/Pending for "Fiscal" total? Or include Pending? Let's include only Approved for strict fiscal.

          // Credit Notes (3, 8, 13) should SUBTRACT
          const isCreditNote = [3, 8, 13, 53].includes(i.tipoComprobante);
          if (isCreditNote) return acc - i.importeTotal;

          return acc + i.importeTotal;
        }, 0);

        return {
          ...f,
          computedPaid: totalPaid,
          computedInvoiced: totalInvoiced,
          pendingCollection: totalSale - totalPaid,
          pendingBilling: totalSale - totalInvoiced
        };
      });

      setGlobalFiles(enhancedFiles);

      // 2. Extract Payments and Invoices for the other tabs
      const allPayments = enhancedFiles.flatMap(f => f.payments?.map(p => ({ ...p, travelFile: f })) || []);
      allPayments.sort((a, b) => new Date(b.paidAt) - new Date(a.paidAt));
      setPayments(allPayments);

      const allInvoices = invoicesRes.map(i => ({
        ...i,
        travelFile: enhancedFiles.find(f => f.id === i.travelFileId) || i.travelFile // Enrich with full file context if needed
      }));
      allInvoices.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
      setInvoices(allInvoices);

    } catch (error) {
      console.error("Error loading data:", error);
      showError("Error al cargar datos");
    } finally {
      setLoading(false);
    }
  }, [activeTab]);

  useEffect(() => {
    loadData();
  }, [loadData]);

  const handleOpenPayment = (file) => {
    setSelectedFile(file);
    setShowPaymentModal(true);
  };

  const handleOpenInvoice = (file) => {
    // Validate
    if (file.status === 'Presupuesto') {
      showError("No se puede facturar un presupuesto.");
      return;
    }
    setSelectedFile(file);
    setShowInvoiceModal(true);
  }

  // ... (Existing helper functions like handleDownloadPdf, handleAnnulInvoice) ...
  const handleDownloadPdf = async (invoice) => {
    try {
      const response = await api.get(`/invoices/${invoice.id}/pdf`, { responseType: 'blob' });
      const url = window.URL.createObjectURL(new Blob([response]));
      const link = document.createElement('a');
      link.href = url;
      link.setAttribute('download', `Factura-${invoice.tipoComprobante}-${invoice.numeroComprobante}.pdf`);
      document.body.appendChild(link);
      link.click();
      link.remove();
    } catch (error) {
      showError("Error al descargar PDF");
    }
  };

  const handleRetryInvoice = async (invoice) => {
    try {
      await api.post(`/invoices/${invoice.id}/retry`);
      showSuccess("Reintento encolado. Esperá unos segundos.");
      loadData();
    } catch (error) {
      showError("Error al reintentar.");
    }
  };

  const handleAnnulInvoice = async (invoice) => {
    // ... (Keep existing logic) ...
    const result = await Swal.fire({
      title: '¿Anular Factura?',
      text: `Se generará una Nota de Crédito idéntica para la Factura ${invoice.puntoDeVenta}-${invoice.numeroComprobante}. ¿Continuar?`,
      icon: 'warning',
      showCancelButton: true,
      confirmButtonText: 'Sí, anular',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: '#ef4444',
      showLoaderOnConfirm: true,
      preConfirm: async () => {
        try {
          const res = await api.post(`/invoices/${invoice.id}/annul`);
          return res.data;
        } catch (error) {
          Swal.showValidationMessage(
            `Error: ${error.response?.data?.message || 'No se pudo anular'}`
          );
        }
      }
    });

    if (result.isConfirmed && result.value) {
      const newInvoice = result.value;
      showSuccess(`Nota generada: ${newInvoice.puntoDeVenta}-${newInvoice.numeroComprobante}`);
      loadData();
    }
  };

  const getInvoiceLabel = (type) => {
    // ... (Keep existing logic) ...
    switch (type) {
      case 1: return "Factura A";
      case 6: return "Factura B";
      case 11: return "Factura C";
      case 3: return "Nota de Crédito A";
      case 8: return "Nota de Crédito B";
      case 13: return "Nota de Crédito C";
      default: return `Comprobante (${type})`;
    }
  };


  // Filtered Lists
  const filteredFiles = globalFiles.filter(f =>
    f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
    f.fileNumber?.toLowerCase().includes(searchTerm.toLowerCase()) ||
    f.payer?.fullName?.toLowerCase().includes(searchTerm.toLowerCase())
  );

  // Totals for Dashboard
  const totalPendingCollection = globalFiles.reduce((acc, f) => acc + (f.balance > 0 ? f.balance : 0), 0);
  const totalPendingBilling = globalFiles.reduce((acc, f) => acc + (f.pendingBilling > 0 ? f.pendingBilling : 0), 0);

  return (
    <div className="space-y-6 max-w-7xl mx-auto p-4 md:p-6 pb-20">
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
        <div>
          <h2 className="text-2xl font-bold tracking-tight text-gray-900 dark:text-white">Bandejas de Administración</h2>
          <p className="text-sm text-gray-500 dark:text-gray-400">Panel de trabajo contable: Gestión de cobranzas, liquidaciones y emisión de AFIP.</p>
        </div>
      </div>

      {/* KPI Cards */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="bg-white dark:bg-slate-800 p-4 rounded-xl shadow-sm border border-slate-200 dark:border-slate-700">
          <div className="text-sm font-medium text-slate-500 mb-1">Cuentas por Cobrar (Caja)</div>
          <div className="text-2xl font-bold text-red-600">${totalPendingCollection.toLocaleString()}</div>
          <div className="text-xs text-slate-400 mt-1">Dinero pendiente de ingreso</div>
        </div>
        <div className="bg-white dark:bg-slate-800 p-4 rounded-xl shadow-sm border border-slate-200 dark:border-slate-700">
          <div className="text-sm font-medium text-slate-500 mb-1">Pendiente de Facturar (Fiscal)</div>
          <div className="text-2xl font-bold text-orange-600">${totalPendingBilling.toLocaleString()}</div>
          <div className="text-xs text-slate-400 mt-1">Ventas sin comprobante fiscal</div>
        </div>
        <div className="bg-white dark:bg-slate-800 p-4 rounded-xl shadow-sm border border-slate-200 dark:border-slate-700">
          <div className="text-sm font-medium text-slate-500 mb-1">Total Facturado (Mes)</div>
          <div className="text-2xl font-bold text-green-600 font-mono">$0</div>
          <div className="text-xs text-slate-400 mt-1">Próximamente: Filtro por fecha</div>
        </div>
      </div>

      {/* Tabs Navigation */}
      <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
        <nav className="flex -mb-px">
          <button
            onClick={() => setActiveTab('collections')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'collections' ? 'border-indigo-500 text-indigo-600 dark:text-indigo-400 bg-indigo-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <Banknote className="w-4 h-4" /> Gestión de Cobros
          </button>
          <button
            onClick={() => setActiveTab('invoicing')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'invoicing' ? 'border-orange-500 text-orange-600 dark:text-orange-400 bg-orange-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <FileText className="w-4 h-4" /> Emisión AFIP
          </button>
          <button
            onClick={() => setActiveTab('history')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'history' ? 'border-green-500 text-green-600 dark:text-green-400 bg-green-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <Clock className="w-4 h-4" /> Registros Históricos
          </button>
        </nav>
      </div>

      {/* CONTENIDO TAB: GESTIÓN DE COBROS */}
      {activeTab === 'collections' && (
        <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
          <div className="p-4 border-b border-gray-200 dark:border-slate-700 bg-indigo-50/50 dark:bg-indigo-900/10 flex justify-between items-center">
            <h3 className="font-semibold text-indigo-900 dark:text-indigo-300 flex items-center gap-2">
              <Banknote className="w-5 h-5" /> Expedientes con Deuda Comercial
            </h3>
            <div className="relative w-64">
              <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-gray-400" />
              <input
                type="text"
                placeholder="Buscar por File, Cliente..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className="pl-9 pr-4 py-2 w-full text-sm border rounded-lg bg-white dark:bg-slate-900 border-gray-200 dark:border-slate-700 focus:outline-none focus:ring-2 focus:ring-indigo-500"
              />
            </div>
          </div>

          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
              <thead className="bg-gray-50 dark:bg-slate-900">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Expediente</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Cliente</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Total Venta</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Saldo Pendiente</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Acción</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 dark:divide-slate-700">
                {filteredFiles.filter(f => f.pendingCollection > 0).map(file => (
                  <tr key={file.id} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                    <td className="px-6 py-4 whitespace-nowrap font-medium text-gray-900 dark:text-white">
                      <a href={`/files/${file.id}`} className="text-blue-600 hover:underline">{file.fileNumber}</a>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                      {file.payer?.fullName || file.customerName || "-"}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-gray-900 dark:text-white">
                      ${file.totalSale?.toLocaleString()}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right">
                      <div className="text-red-600 font-bold">
                        ${file.pendingCollection?.toLocaleString()}
                      </div>
                      <div className="text-[10px] text-gray-500">Abonado: ${file.computedPaid?.toLocaleString()}</div>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                      <button
                        onClick={() => handleOpenPayment(file)}
                        className="inline-flex items-center gap-1 px-3 py-1.5 bg-green-100 text-green-700 hover:bg-green-200 rounded-md font-medium transition-colors"
                      >
                        <DollarSign className="w-4 h-4" /> Registrar Cobro
                      </button>
                    </td>
                  </tr>
                ))}
                {filteredFiles.filter(f => f.pendingCollection > 0).length === 0 && (
                  <tr>
                    <td colSpan="5" className="px-6 py-8 text-center text-gray-500">No hay expedientes con deuda comercial pendiente.</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* CONTENIDO TAB: EMISIÓN AFIP */}
      {activeTab === 'invoicing' && (
        <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
          <div className="p-4 border-b border-gray-200 dark:border-slate-700 bg-orange-50/50 dark:bg-orange-900/10 flex justify-between items-center">
            <h3 className="font-semibold text-orange-900 dark:text-orange-300 flex items-center gap-2">
              <FileText className="w-5 h-5" /> Expedientes Pendientes de Facturación
            </h3>
            <div className="relative w-64">
              <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-gray-400" />
              <input
                type="text"
                placeholder="Buscar por File, Cliente..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className="pl-9 pr-4 py-2 w-full text-sm border rounded-lg bg-white dark:bg-slate-900 border-gray-200 dark:border-slate-700 focus:outline-none focus:ring-2 focus:ring-orange-500"
              />
            </div>
          </div>

          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
              <thead className="bg-gray-50 dark:bg-slate-900">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Expediente</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Cliente</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Cobrado (Caja)</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Monto a Facturar</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Acción</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 dark:divide-slate-700">
                {filteredFiles.filter(f => f.pendingBilling > 0 && f.computedPaid > 0).map(file => (
                  <tr key={file.id} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                    <td className="px-6 py-4 whitespace-nowrap font-medium text-gray-900 dark:text-white">
                      <a href={`/files/${file.id}`} className="text-blue-600 hover:underline">{file.fileNumber}</a>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                      {file.payer?.fullName || file.customerName || "-"}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-gray-900 dark:text-white">
                      ${file.computedPaid?.toLocaleString()}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right">
                      <div className="text-orange-600 font-bold">
                        ${file.pendingBilling?.toLocaleString()}
                      </div>
                      <div className="text-[10px] text-gray-500">Ya facturado: ${file.computedInvoiced?.toLocaleString()}</div>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                      <button
                        onClick={() => handleOpenInvoice(file)}
                        className="inline-flex items-center gap-1 px-3 py-1.5 bg-indigo-100 text-indigo-700 hover:bg-indigo-200 rounded-md font-medium transition-colors"
                      >
                        <Calculator className="w-4 h-4" /> Emitir Factura
                      </button>
                    </td>
                  </tr>
                ))}
                {filteredFiles.filter(f => f.pendingBilling > 0 && f.computedPaid > 0).length === 0 && (
                  <tr>
                    <td colSpan="5" className="px-6 py-8 text-center text-gray-500">No hay expedientes con pagos que requieran facturación.</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* TAB: REGISTROS HISTÓRICOS */}
      {activeTab === 'history' && (
        <div className="space-y-6">
          <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
            <div className="p-4 border-b border-gray-200 dark:border-slate-700 bg-gray-50 dark:bg-slate-900/50 flex justify-between items-center">
              <h3 className="font-semibold text-gray-800 dark:text-gray-200 flex items-center gap-2">
                <Clock className="w-5 h-5" /> Movimientos de Caja (Recientes)
              </h3>
            </div>
            <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
              <thead className="bg-gray-50 dark:bg-slate-900">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Fecha</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">File</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Monto</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Método</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 dark:divide-slate-700">
                {payments.slice(0, 15).map(p => (
                  <tr key={p.id}>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{new Date(p.paidAt).toLocaleDateString()}</td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm font-medium">
                      <a href={`/files/${p.travelFile?.id}`} className="text-blue-600 hover:underline">{p.travelFile?.fileNumber}</a>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-bold text-green-600">+${p.amount?.toLocaleString()}</td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{p.method}</td>
                  </tr>
                ))}
                {payments.length === 0 && (
                  <tr><td colSpan={4} className="px-6 py-4 text-center text-gray-500">No hay movimientos.</td></tr>
                )}
              </tbody>
            </table>
          </div>

          <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
            <div className="p-4 border-b border-gray-200 dark:border-slate-700 bg-gray-50 dark:bg-slate-900/50 flex justify-between items-center">
              <h3 className="font-semibold text-gray-800 dark:text-gray-200 flex items-center gap-2">
                <FileText className="w-5 h-5" /> Comprobantes Fiscales (Recientes)
              </h3>
            </div>
            <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
              <thead className="bg-gray-50 dark:bg-slate-900">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Fecha</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Comprobante</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Cliente</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Total</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Acciones</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 dark:divide-slate-700">
                {invoices.slice(0, 15).map(i => (
                  <tr key={i.id}>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{new Date(i.createdAt).toLocaleDateString()}</td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm font-mono">
                      {i.resultado === 'A' ? (
                        <span className="text-green-600 font-bold flex items-center gap-1">
                          <CheckCircle className="w-3 h-3" /> {getInvoiceLabel(i.tipoComprobante)} - {i.numeroComprobante}
                        </span>
                      ) : i.resultado === 'PENDING' ? (
                        <span className="text-yellow-600 font-bold flex items-center gap-1 animate-pulse">
                          <Loader2 className="w-3 h-3 animate-spin" /> Procesando...
                        </span>
                      ) : (
                        <div className="flex flex-col">
                          <span className="text-red-600 font-bold flex items-center gap-1">
                            <XCircle className="w-3 h-3" /> Error (Rechazado)
                          </span>
                          <span className="text-[10px] text-red-500 max-w-[200px] truncate" title={i.observaciones}>
                            {i.observaciones || "Error desconocido"}
                          </span>
                        </div>
                      )}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                      <a href={`/files/${i.travelFileId}`} className="text-blue-600 hover:underline">{i.travelFile?.payer?.fullName || "Ver Expediente"}</a>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-bold">${i.importeTotal?.toLocaleString()}</td>
                    <td className="px-6 py-4 text-right">
                      {i.resultado === 'A' && (
                        <>
                          <button onClick={() => handleDownloadPdf(i)} className="text-indigo-600 hover:text-indigo-800 mr-2" title="Descargar"><Download className="w-4 h-4" /></button>
                          <button onClick={() => handleAnnulInvoice(i)} className="text-red-500 hover:text-red-700" title="Anular"><XCircle className="w-4 h-4" /></button>
                        </>
                      )}
                      {i.resultado === 'R' && (
                        <button onClick={() => handleRetryInvoice(i)} className="text-orange-600 hover:text-orange-800 font-bold text-xs border border-orange-200 bg-orange-50 px-2 py-1 rounded" title="Reintentar">
                          Reintentar
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {invoices.length === 0 && (
                  <tr><td colSpan={5} className="px-6 py-4 text-center text-gray-500">No hay comprobantes emitidos.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* MODALS */}
      <PaymentModal
        isOpen={showPaymentModal}
        onClose={() => { setShowPaymentModal(false); setSelectedFile(null); }}
        fileId={selectedFile?.id}
        maxAmount={selectedFile?.pendingCollection}
        onSuccess={() => { loadData(); }}
      />

      <CreateInvoiceModal
        isOpen={showInvoiceModal}
        onClose={() => { setShowInvoiceModal(false); setSelectedFile(null); }}
        fileId={selectedFile?.id}
        initialAmount={selectedFile?.pendingBilling > 0 ? selectedFile.pendingBilling : 0}
        clientName={selectedFile?.payer?.fullName || selectedFile?.customerName}
        clientCuit={selectedFile?.payer?.taxId || selectedFile?.payer?.documentNumber}
        onSuccess={() => { loadData(); }}
      />
    </div>
  );
}
