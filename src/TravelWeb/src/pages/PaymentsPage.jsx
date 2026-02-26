import { useEffect, useState, useCallback, useMemo } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import Swal from "sweetalert2";
import {
  Search,
  Banknote,
  FileText,
  Clock,
  CheckCircle,
  Download,
  Loader2,
  XCircle,
  DollarSign,
  Calculator,
  Filter
} from "lucide-react";
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

  // Filters
  const [searchTerm, setSearchTerm] = useState("");
  const [dateFilter, setDateFilter] = useState("all"); // 'all', 'this_month'

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [filesRes, invoicesRes] = await Promise.all([
        api.get("/travelfiles"),
        api.get("/invoices")
      ]);

      const enhancedFiles = filesRes.map(f => {
        const fileInvoices = invoicesRes.filter(i => i.travelFileId === f.id);
        const validPayments = f.payments?.filter(p => p.status !== 'Cancelled') || [];

        const totalSale = f.totalSale || 0;
        const totalPaid = validPayments.reduce((acc, p) => acc + p.amount, 0);

        // Calculate invoiced amount from APPROVED invoices only
        const totalInvoiced = fileInvoices.reduce((acc, i) => {
          if (i.resultado !== 'A') return acc;
          const isCreditNote = [3, 8, 13, 53].includes(i.tipoComprobante);
          if (isCreditNote) return acc - i.importeTotal;
          return acc + i.importeTotal;
        }, 0);

        // LOGIC FIX: What pending to invoice? ONLY what has been collected but not invoiced.
        // We shouldn't invoice money we haven't received yet.
        const moneyCollectedNotInvoiced = totalPaid - totalInvoiced;

        return {
          ...f,
          invoices: fileInvoices,
          validPayments,
          computedPaid: totalPaid,
          computedInvoiced: totalInvoiced,
          pendingCollection: totalSale - totalPaid,
          pendingBilling: moneyCollectedNotInvoiced > 0 ? moneyCollectedNotInvoiced : 0, // ONLY bill what was paid!
          totalSaleAmount: totalSale
        };
      });

      setGlobalFiles(enhancedFiles);

      const allPayments = enhancedFiles.flatMap(f => f.validPayments.map(p => ({ ...p, travelFile: f })));
      allPayments.sort((a, b) => new Date(b.paidAt) - new Date(a.paidAt));
      setPayments(allPayments);

      const allInvoices = invoicesRes.map(i => ({
        ...i,
        travelFile: enhancedFiles.find(f => f.id === i.travelFileId) || i.travelFile
      }));
      allInvoices.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
      setInvoices(allInvoices);

    } catch (error) {
      console.error("Error loading data:", error);
      showError("Error al cargar datos");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);


  // Actions
  const handleOpenPayment = (file) => {
    setSelectedFile(file);
    setShowPaymentModal(true);
  };

  const handleOpenInvoice = (file) => {
    if (file.status === 'Presupuesto') {
      showError("No se puede facturar un presupuesto.");
      return;
    }
    // We pass the exact amount of money that was collected but not invoiced
    setSelectedFile({
      ...file,
      suggestedInvoiceAmount: file.pendingBilling
    });
    setShowInvoiceModal(true);
  }

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
      showSuccess("Reintento encolado.");
      loadData();
    } catch (error) {
      showError("Error al reintentar.");
    }
  };

  const handleAnnulInvoice = async (invoice) => {
    const result = await Swal.fire({
      title: '¿Anular Factura?',
      text: `Se generará una Nota de Crédito. ¿Continuar?`,
      icon: 'warning',
      showCancelButton: true,
      confirmButtonText: 'Sí, anular',
      cancelButtonText: 'Cancelar',
      confirmButtonColor: '#0f172a',
    });

    if (result.isConfirmed) {
      try {
        const res = await api.post(`/invoices/${invoice.id}/annul`);
        showSuccess(`Nota generada: ${res.data?.puntoDeVenta}-${res.data?.numeroComprobante || ''}`);
        loadData();
      } catch (error) {
        showError(error.response?.data?.message || 'Error al anular');
      }
    }
  };

  const getInvoiceLabel = (type) => {
    switch (type) {
      case 1: return "Factura A";
      case 6: return "Factura B";
      case 11: return "Factura C";
      case 3: return "NC A";
      case 8: return "NC B";
      case 13: return "NC C";
      default: return `Comp (${type})`;
    }
  };

  // Filter Logic
  const filteredFiles = useMemo(() => {
    return globalFiles.filter(f => {
      const matchesSearch = f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        f.fileNumber?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        f.payer?.fullName?.toLowerCase().includes(searchTerm.toLowerCase());

      if (!matchesSearch) return false;

      // Basic Date Filter for future expansion
      if (dateFilter === 'this_month') {
        // Just an example, would need actual date logic based on creation or travel date
        // return new Date(f.createdAt).getMonth() === new Date().getMonth();
      }

      return true;
    });
  }, [globalFiles, searchTerm, dateFilter]);


  // Clean KPIs
  const totalPendingCollection = globalFiles.reduce((acc, f) => acc + (f.pendingCollection > 0 ? f.pendingCollection : 0), 0);
  const totalPendingBilling = globalFiles.reduce((acc, f) => acc + (f.pendingBilling > 0 ? f.pendingBilling : 0), 0);

  const thisMonthInvoices = invoices.filter(i => i.resultado === 'A' && new Date(i.createdAt).getMonth() === new Date().getMonth());
  const totalInvoicedMonth = thisMonthInvoices.reduce((acc, i) => {
    const isCreditNote = [3, 8, 13, 53].includes(i.tipoComprobante);
    return isCreditNote ? acc - i.importeTotal : acc + i.importeTotal;
  }, 0);

  if (loading && globalFiles.length === 0) {
    return <div className="flex justify-center items-center h-64 text-slate-400"><Loader2 className="w-8 h-8 animate-spin" /></div>;
  }

  return (
    <div className="max-w-7xl mx-auto p-4 sm:p-8 space-y-8 pb-20 fade-in">

      {/* HEADER & TOP FILTERS */}
      <div className="flex flex-col md:flex-row md:items-end justify-between gap-6 pb-6 border-b border-slate-100 dark:border-slate-800/50">
        <div>
          <h1 className="text-3xl font-light tracking-tight text-slate-900 dark:text-white mb-1">Caja y Facturación</h1>
          <p className="text-sm text-slate-500 dark:text-slate-400">Panel administrativo: gestioná cobros y emití comprobantes fiscales.</p>
        </div>

        <div className="flex items-center gap-3">
          <div className="relative w-full md:w-64">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
            <input
              type="text"
              placeholder="Buscar viajes, clientes..."
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              className="pl-9 pr-4 py-2 w-full text-sm bg-slate-50 dark:bg-slate-900 border-none rounded-full focus:ring-2 focus:ring-slate-200 transition-shadow"
            />
          </div>

          <button className="p-2 text-slate-400 hover:text-slate-600 bg-slate-50 hover:bg-slate-100 rounded-full transition-colors" title="Filtros avanzados">
            <Filter className="w-4 h-4" />
          </button>
        </div>
      </div>

      {/* MINIMALIST KPIs */}
      <div className="flex flex-wrap gap-8 md:gap-16">
        <div>
          <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Cuentas por Cobrar</div>
          <div className="text-3xl font-light text-slate-900 dark:text-white">
            ${totalPendingCollection.toLocaleString('es-AR')}
          </div>
        </div>
        <div>
          <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Cobro Sin Facturar</div>
          <div className="text-3xl font-light text-slate-900 dark:text-white">
            ${totalPendingBilling.toLocaleString('es-AR')}
          </div>
        </div>
        <div>
          <div className="text-xs uppercase tracking-wider font-semibold text-slate-400 mb-1">Facturado (Mes)</div>
          <div className="text-3xl font-light text-slate-400 dark:text-slate-500">
            ${totalInvoicedMonth.toLocaleString('es-AR')}
          </div>
        </div>
      </div>

      {/* SUBTLE TABS */}
      <div className="flex gap-6 border-b border-slate-100 dark:border-slate-800">
        <button
          onClick={() => setActiveTab('collections')}
          className={`pb-3 text-sm font-medium transition-colors relative ${activeTab === 'collections' ? 'text-slate-900 dark:text-white' : 'text-slate-400 hover:text-slate-600'
            }`}
        >
          <div className="flex items-center gap-2"><Banknote className="w-4 h-4" /> Cobranzas</div>
          {activeTab === 'collections' && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-slate-900 dark:bg-white rounded-t-full" />}
        </button>
        <button
          onClick={() => setActiveTab('invoicing')}
          className={`pb-3 text-sm font-medium transition-colors relative ${activeTab === 'invoicing' ? 'text-slate-900 dark:text-white' : 'text-slate-400 hover:text-slate-600'
            }`}
        >
          <div className="flex items-center gap-2"><FileText className="w-4 h-4" /> Emisión AFIP</div>
          {activeTab === 'invoicing' && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-slate-900 dark:bg-white rounded-t-full" />}
        </button>
        <button
          onClick={() => setActiveTab('history')}
          className={`pb-3 text-sm font-medium transition-colors relative ${activeTab === 'history' ? 'text-slate-900 dark:text-white' : 'text-slate-400 hover:text-slate-600'
            }`}
        >
          <div className="flex items-center gap-2"><Clock className="w-4 h-4" /> Historial</div>
          {activeTab === 'history' && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-slate-900 dark:bg-white rounded-t-full" />}
        </button>
      </div>

      {/* TAB CONTENT: GESTIÓN DE COBROS */}
      {activeTab === 'collections' && (
        <div className="animate-in fade-in slide-in-from-bottom-2 duration-300">
          <table className="w-full text-left border-collapse">
            <thead>
              <tr className="border-b border-slate-100 dark:border-slate-800">
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Expediente</th>
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Cliente</th>
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right">Saldo Comercial</th>
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Acción</th>
              </tr>
            </thead>
            <tbody>
              {filteredFiles.filter(f => f.pendingCollection > 0).map(file => (
                <tr key={file.id} className="group border-b border-slate-50 dark:border-slate-800/50 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                  <td className="py-4 align-middle">
                    <a href={`/files/${file.id}`} className="font-medium text-slate-900 dark:text-white hover:text-slate-600">{file.fileNumber}</a>
                    <div className="text-xs text-slate-400">Total: ${file.totalSaleAmount?.toLocaleString()}</div>
                  </td>
                  <td className="py-4 align-middle text-sm text-slate-600 dark:text-slate-300">
                    {file.payer?.fullName || file.customerName || "-"}
                  </td>
                  <td className="py-4 align-middle text-right">
                    <div className="font-semibold text-slate-900 dark:text-white">
                      ${file.pendingCollection?.toLocaleString('es-AR')}
                    </div>
                    {file.computedPaid > 0 && <div className="text-[10px] text-slate-400">Abonó: ${file.computedPaid?.toLocaleString()}</div>}
                  </td>
                  <td className="py-4 align-middle text-right pr-4">
                    <button
                      onClick={() => handleOpenPayment(file)}
                      className="opacity-0 group-hover:opacity-100 transition-opacity inline-flex items-center justify-center w-8 h-8 rounded-full bg-slate-100 text-slate-600 hover:bg-slate-800 hover:text-white dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"
                      title="Registrar Cobro"
                    >
                      <DollarSign className="w-4 h-4" />
                    </button>
                    {/* Fallback for mobile where hover doesn't exist */}
                    <button
                      onClick={() => handleOpenPayment(file)}
                      className="md:hidden inline-flex items-center justify-center w-8 h-8 rounded-full bg-slate-100 text-slate-600"
                    >
                      <DollarSign className="w-4 h-4" />
                    </button>
                  </td>
                </tr>
              ))}
              {filteredFiles.filter(f => f.pendingCollection > 0).length === 0 && (
                <tr>
                  <td colSpan="4" className="py-12 text-center text-slate-400 text-sm">No hay expedientes con deuda comercial pendiente.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {/* TAB CONTENT: EMISIÓN AFIP */}
      {activeTab === 'invoicing' && (
        <div className="animate-in fade-in slide-in-from-bottom-2 duration-300">
          <table className="w-full text-left border-collapse">
            <thead>
              <tr className="border-b border-slate-100 dark:border-slate-800">
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Expediente</th>
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium">Dinero Ingresado</th>
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right">A Facturar (Sin Comprobante)</th>
                <th className="pb-3 text-xs uppercase text-slate-400 font-medium text-right pr-4">Acción</th>
              </tr>
            </thead>
            <tbody>
              {filteredFiles.filter(f => f.pendingBilling > 0).map(file => (
                <tr key={file.id} className="group border-b border-slate-50 dark:border-slate-800/50 hover:bg-slate-50/50 dark:hover:bg-slate-800/20 transition-colors">
                  <td className="py-4 align-middle">
                    <a href={`/files/${file.id}`} className="font-medium text-slate-900 dark:text-white hover:text-slate-600">{file.fileNumber}</a>
                    <div className="text-xs text-slate-400">{file.payer?.fullName || file.customerName}</div>
                  </td>
                  <td className="py-4 align-middle text-sm text-slate-600 dark:text-slate-300">
                    <div className="flex items-center gap-1.5">
                      <div className="w-1.5 h-1.5 rounded-full bg-green-400"></div>
                      <span>${file.computedPaid?.toLocaleString('es-AR')}</span>
                    </div>
                  </td>
                  <td className="py-4 align-middle text-right">
                    <div className="font-semibold text-slate-900 dark:text-white">
                      ${file.pendingBilling?.toLocaleString('es-AR')}
                    </div>
                    {file.computedInvoiced > 0 && <div className="text-[10px] text-slate-400">Ya facturado: ${file.computedInvoiced?.toLocaleString()}</div>}
                  </td>
                  <td className="py-4 align-middle text-right pr-4">
                    <button
                      onClick={() => handleOpenInvoice(file)}
                      className="opacity-0 group-hover:opacity-100 transition-opacity inline-flex items-center justify-center px-3 py-1.5 text-sm rounded-full bg-slate-900 text-white hover:bg-slate-800 dark:bg-white dark:text-slate-900 hover:shadow-md"
                      title="Emitir Factura"
                    >
                      Emitir
                    </button>
                    {/* Mobile fallback */}
                    <button
                      onClick={() => handleOpenInvoice(file)}
                      className="md:hidden inline-flex items-center justify-center p-2 rounded-full bg-slate-100 text-slate-600"
                    >
                      <Calculator className="w-4 h-4" />
                    </button>
                  </td>
                </tr>
              ))}
              {filteredFiles.filter(f => f.pendingBilling > 0).length === 0 && (
                <tr>
                  <td colSpan="4" className="py-12 text-center text-slate-400 text-sm">Todo el dinero ingresado tiene comprobante fiscal.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {/* TAB CONTENT: HISTORIAL */}
      {activeTab === 'history' && (
        <div className="animate-in fade-in slide-in-from-bottom-2 duration-300 grid grid-cols-1 lg:grid-cols-2 gap-12 mt-4">

          {/* HISTORIAL CAJA */}
          <div>
            <h3 className="text-sm font-semibold text-slate-900 mb-4 flex items-center gap-2">
              <div className="w-2 h-2 rounded-full bg-slate-300"></div> Movimientos Recientes
            </h3>
            <div className="space-y-4">
              {payments.slice(0, 15).map(p => (
                <div key={p.id} className="flex justify-between items-center group">
                  <div>
                    <div className="text-sm font-medium text-slate-900">
                      Cobro en File <a href={`/files/${p.travelFile?.id}`} className="hover:underline text-blue-600 text-xs">{p.travelFile?.fileNumber}</a>
                    </div>
                    <div className="text-xs text-slate-400">{new Date(p.paidAt).toLocaleDateString()} • {p.method}</div>
                  </div>
                  <div className="text-right font-medium text-slate-600">
                    +${p.amount?.toLocaleString()}
                  </div>
                </div>
              ))}
              {payments.length === 0 && <div className="text-sm text-slate-400">No hay movimientos.</div>}
            </div>
          </div>

          {/* HISTORIAL AFIP */}
          <div>
            <h3 className="text-sm font-semibold text-slate-900 mb-4 flex items-center gap-2">
              <div className="w-2 h-2 rounded-full bg-slate-300"></div> Comprobantes Emitidos
            </h3>
            <div className="space-y-4">
              {invoices.slice(0, 15).map(i => (
                <div key={i.id} className="flex justify-between items-center group relative">
                  <div className="pr-4 flex-1">
                    <div className="text-sm font-medium flex items-center gap-1.5">
                      {i.resultado === 'A' ? (
                        <span className="text-slate-900">{getInvoiceLabel(i.tipoComprobante)} {i.numeroComprobante}</span>
                      ) : i.resultado === 'PENDING' ? (
                        <span className="text-slate-500 flex items-center gap-1"><Loader2 className="w-3 h-3 animate-spin" /> Procesando</span>
                      ) : (
                        <span className="text-red-600 flex items-center gap-1"><XCircle className="w-3 h-3" /> Rechazada</span>
                      )}
                    </div>
                    <div className="text-xs text-slate-400 truncate max-w-[200px]" title={i.travelFile?.payer?.fullName}>
                      {i.travelFile?.payer?.fullName || `File ${i.travelFile?.fileNumber}`} • {new Date(i.createdAt).toLocaleDateString()}
                    </div>
                    {i.resultado === 'R' && <div className="text-[10px] text-red-500 truncate" title={i.observaciones}>{i.observaciones}</div>}
                  </div>

                  <div className="flex items-center gap-3">
                    <div className="text-right font-medium text-slate-900">
                      ${i.importeTotal?.toLocaleString()}
                    </div>

                    {/* Hover Actions */}
                    <div className="opacity-0 group-hover:opacity-100 transition-opacity flex gap-1 bg-white pl-2">
                      {i.resultado === 'A' && (
                        <>
                          <button onClick={() => handleDownloadPdf(i)} className="p-1.5 text-slate-400 hover:text-slate-900 bg-slate-50 rounded" title="Descargar"><Download className="w-3.5 h-3.5" /></button>
                          <button onClick={() => handleAnnulInvoice(i)} className="p-1.5 text-slate-400 hover:text-red-600 bg-slate-50 rounded" title="Anular"><XCircle className="w-3.5 h-3.5" /></button>
                        </>
                      )}
                      {i.resultado === 'R' && (
                        <button onClick={() => handleRetryInvoice(i)} className="text-xs bg-slate-100 hover:bg-slate-200 text-slate-700 px-2 py-1 rounded">Reintentar</button>
                      )}
                    </div>
                  </div>
                </div>
              ))}
              {invoices.length === 0 && <div className="text-sm text-slate-400">No hay comprobantes.</div>}
            </div>
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
        initialAmount={selectedFile?.suggestedInvoiceAmount}
        clientName={selectedFile?.payer?.fullName || selectedFile?.customerName}
        clientCuit={selectedFile?.payer?.taxId || selectedFile?.payer?.documentNumber}
        onSuccess={() => { loadData(); }}
      />
    </div>
  );
}
