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
  const [activeTab, setActiveTab] = useState("control"); // control, cashflow, invoices
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
        const totalInvoiced = fileInvoices.filter(i => i.status !== 'Annulled').reduce((acc, i) => acc + i.importeTotal, 0) || 0;

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
      if (activeTab === 'cashflow') {
        const allPayments = enhancedFiles.flatMap(f => f.payments?.map(p => ({ ...p, travelFile: f })) || []);
        allPayments.sort((a, b) => new Date(b.paidAt) - new Date(a.paidAt));
        setPayments(allPayments);
      } else if (activeTab === 'invoices') {
        const allInvoices = invoicesRes.map(i => ({
          ...i,
          travelFile: enhancedFiles.find(f => f.id === i.travelFileId) || i.travelFile // Enrich with full file context if needed
        }));
        allInvoices.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
        setInvoices(allInvoices);
      }

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
          <h2 className="text-2xl font-bold tracking-tight text-gray-900 dark:text-white">Control de Facturación y Caja</h2>
          <p className="text-sm text-gray-500 dark:text-gray-400">Torre de control financiera: Seguimiento de cobros y facturación fiscal.</p>
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
            onClick={() => setActiveTab('control')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'control' ? 'border-indigo-500 text-indigo-600 dark:text-indigo-400 bg-indigo-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <Banknote className="w-4 h-4" /> Control Global
          </button>
          <button
            onClick={() => setActiveTab('cashflow')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'cashflow' ? 'border-green-500 text-green-600 dark:text-green-400 bg-green-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <Clock className="w-4 h-4" /> Movimientos de Caja
          </button>
          <button
            onClick={() => setActiveTab('invoices')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'invoices' ? 'border-orange-500 text-orange-600 dark:text-orange-400 bg-orange-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <FileText className="w-4 h-4" /> Comprobantes Fiscales
          </button>
        </nav>
      </div>

      {/* CONTENIDO TAB: CONTROL GLOBAL */}
      {activeTab === 'control' && (
        <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
          <div className="p-4 border-b border-gray-200 dark:border-slate-700 flex gap-4">
            <div className="relative flex-1 max-w-sm">
              <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-gray-400" />
              <input
                type="text"
                placeholder="Buscar por File, Cliente..."
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                className="pl-9 pr-4 py-2 w-full text-sm border rounded-lg bg-gray-50 dark:bg-slate-900 border-gray-200 dark:border-slate-700 focus:outline-none focus:ring-2 focus:ring-indigo-500"
              />
            </div>
          </div>

          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
              <thead className="bg-gray-50 dark:bg-slate-900">
                <tr>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">File</th>
                  <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Cliente</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Total Venta</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Cobrado (Caja)</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Facturado (Fiscal)</th>
                  <th className="px-6 py-3 text-center text-xs font-medium text-gray-500 uppercase">Estado</th>
                  <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Acciones</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 dark:divide-slate-700">
                {filteredFiles.map(file => (
                  <tr key={file.id} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                    <td className="px-6 py-4 whitespace-nowrap font-medium text-gray-900 dark:text-white">
                      {file.fileNumber}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                      {file.payer?.fullName || file.customerName || "-"}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-bold text-gray-900 dark:text-white">
                      ${file.totalSale?.toLocaleString()}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right">
                      <div className={file.pendingCollection > 0 ? "text-red-600 font-bold" : "text-green-600"}>
                        ${file.computedPaid?.toLocaleString()}
                      </div>
                      {file.pendingCollection > 0 && <div className="text-[10px] text-red-500">Falta: ${file.pendingCollection.toLocaleString()}</div>}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right">
                      <div className={file.pendingBilling > 0 ? "text-orange-600 font-bold" : "text-green-600"}>
                        ${file.computedInvoiced?.toLocaleString()}
                      </div>
                      {file.pendingBilling > 0 && <div className="text-[10px] text-orange-500">Falta: ${file.pendingBilling.toLocaleString()}</div>}
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-center">
                      {/* Status Badges */}
                      <div className="flex flex-col gap-1 items-center">
                        {file.pendingCollection <= 0 && file.pendingBilling <= 0 ? (
                          <span className="px-2 py-1 bg-green-100 text-green-800 text-xs rounded-full">Ok Financiero</span>
                        ) : (
                          <>
                            {file.pendingCollection > 0 && <span className="px-2 py-0.5 bg-red-100 text-red-800 text-[10px] rounded-full">Deuda Caja</span>}
                            {file.pendingBilling > 0 && <span className="px-2 py-0.5 bg-orange-100 text-orange-800 text-[10px] rounded-full">Pend. Fiscal</span>}
                          </>
                        )}
                      </div>
                    </td>
                    <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                      <div className="flex justify-end gap-2">
                        <button
                          onClick={() => handleOpenPayment(file)}
                          className="p-1.5 text-green-600 hover:bg-green-50 rounded"
                          title="Cobrar"
                        >
                          <DollarSign className="w-4 h-4" />
                        </button>
                        <button
                          onClick={() => handleOpenInvoice(file)}
                          className="p-1.5 text-indigo-600 hover:bg-indigo-50 rounded"
                          title="Facturar"
                        >
                          <Calculator className="w-4 h-4" />
                        </button>
                        <a href={`/files/${file.id}`} className="p-1.5 text-gray-400 hover:text-gray-600" title="Ver File">
                          <ArrowUpRight className="w-4 h-4" />
                        </a>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* TAB: CASHFLOW (Existing Logic Refined) */}
      {activeTab === 'cashflow' && (
        <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
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
              {payments.map(p => (
                <tr key={p.id}>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{new Date(p.paidAt).toLocaleDateString()}</td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm font-medium">{p.travelFile?.fileNumber}</td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-bold text-green-600">+${p.amount?.toLocaleString()}</td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{p.method}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* TAB: INVOICES (Existing Logic Refined) */}
      {activeTab === 'invoices' && (
        <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
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
              {invoices.map(i => (
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
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">{i.travelFile?.payer?.fullName}</td>
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
            </tbody>
          </table>
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
