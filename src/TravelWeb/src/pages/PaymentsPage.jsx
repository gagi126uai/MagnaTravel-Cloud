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
  XCircle
} from "lucide-react";
import { Button } from "../components/ui/button";
import PaymentModal from "../components/PaymentModal";
import InvoicesTab from "../components/InvoicesTab";

export default function PaymentsPage() {
  const [activeTab, setActiveTab] = useState("receivables"); // receivables, history, invoices
  const [loading, setLoading] = useState(true);

  // Data States
  const [receivables, setReceivables] = useState([]);
  const [payments, setPayments] = useState([]);
  const [invoices, setInvoices] = useState([]);

  // Modals
  const [showPaymentModal, setShowPaymentModal] = useState(false);
  const [selectedFile, setSelectedFile] = useState(null);
  const [creatingInvoice, setCreatingInvoice] = useState(false);

  // Filters
  const [searchTerm, setSearchTerm] = useState("");

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      if (activeTab === 'receivables') {
        const files = await api.get("/travelfiles");
        // Filter locally for now: Status != Budget AND Balance > 0
        const pending = files.filter(f => f.status !== 'Presupuesto' && f.balance > 0);
        setReceivables(pending);
      } else if (activeTab === 'history') {
        try {
          const data = await api.get("/payments"); // We need to ensure this endpoint exists in backend or TravelFilesController
          setPayments(data.reverse());
        } catch (e) {
          // Fallback if global endpoint doesn't exist: fetch files and aggregate
          const files = await api.get("/travelfiles");
          const allPayments = files.flatMap(f => f.payments.map(p => ({ ...p, travelFile: f })));
          allPayments.sort((a, b) => new Date(b.paidAt) - new Date(a.paidAt));
          setPayments(allPayments);
        }
      } else if (activeTab === 'invoices') {
        const data = await api.get("/invoices"); // New endpoint we added
        setInvoices(data);
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

  const handleCreateInvoice = async (file) => {
    // 1. Validate Status being NOT 'Presupuesto'
    if (file.status === 'Presupuesto') {
      showError("No se puede facturar un expediente en estado Presupuesto.");
      return;
    }

    // 2. Validate Reservation Statuses
    const hasConfirmedServices = file.reservations && file.reservations.some(r =>
      ['Confirmed', 'Issued', 'Confirmado', 'Emitido'].includes(r.status)
    );

    if (!hasConfirmedServices) {
      showError("El expediente no tiene servicios confirmados o emitidos.");
      return;
    }

    // 3. Confirm Action
    const result = await Swal.fire({
      title: '¿Emitir Factura?',
      text: `Se generará una factura para el File ${file.fileNumber} por el saldo: $${file.balance?.toLocaleString()}`,
      icon: 'question',
      showCancelButton: true,
      confirmButtonText: 'Sí, emitir',
      cancelButtonText: 'Cancelar'
    });

    if (result.isConfirmed) {
      try {
        setCreatingInvoice(true);
        await api.post('/invoices', {
          travelFileId: file.id,
          amount: file.balance
        });
        showSuccess(`Factura emitida correctamente para el File ${file.fileNumber}`);
        loadData(); // Refresh list
      } catch (error) {
        console.error(error);
        showError(error.response?.data?.message || "Error al emitir factura");
      } finally {
        setCreatingInvoice(false);
      }
    }
  };

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

  const handleAnnulInvoice = async (invoice) => {
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
          await api.post(`/invoices/${invoice.id}/annul`);
          return true;
        } catch (error) {
          Swal.showValidationMessage(
            `Error: ${error.response?.data?.message || 'No se pudo anular'}`
          );
        }
      }
    });

    if (result.isConfirmed) {
      showSuccess("Nota de Crédito generada exitosamente");
      loadData();
    }
  };

  return (
    <div className="space-y-6 max-w-7xl mx-auto p-4 md:p-6">
      <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
        <div>
          <h2 className="text-2xl font-bold tracking-tight text-gray-900 dark:text-white">Facturación y Caja</h2>
          <p className="text-sm text-gray-500 dark:text-gray-400">Gestioná tus cobros, pagos y comprobantes fiscales desde un solo lugar.</p>
        </div>
      </div>

      {/* Tabs Navigation */}
      <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
        <nav className="flex -mb-px">
          <button
            onClick={() => setActiveTab('receivables')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'receivables' ? 'border-orange-500 text-orange-600 dark:text-orange-400 bg-orange-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <Briefcase className="w-4 h-4" /> Cuentas por Cobrar
          </button>
          <button
            onClick={() => setActiveTab('history')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'history' ? 'border-green-500 text-green-600 dark:text-green-400 bg-green-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <Clock className="w-4 h-4" /> Historial de Pagos
          </button>
          <button
            onClick={() => setActiveTab('invoices')}
            className={`flex-1 py-4 px-1 text-center border-b-2 font-medium text-sm flex items-center justify-center gap-2
               ${activeTab === 'invoices' ? 'border-indigo-500 text-indigo-600 dark:text-indigo-400 bg-indigo-50 dark:bg-slate-700/50' : 'border-transparent text-gray-500 dark:text-slate-400 hover:text-gray-700 dark:hover:text-slate-200 hover:border-gray-300'}`}
          >
            <FileText className="w-4 h-4" /> Facturas Emitidas
          </button>
        </nav>
      </div>

      {loading ? (
        <div className="py-12 text-center text-gray-500">Cargando información...</div>
      ) : (
        <>
          {/* --- TAB: RECEIVABLES --- */}
          {activeTab === 'receivables' && (
            <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
              <div className="p-4 border-b border-gray-200 dark:border-slate-700 flex justify-between items-center">
                <h3 className="font-medium text-gray-900 dark:text-white">Expedientes con Saldo Pendiente</h3>
                <div className="text-sm text-gray-500">Total: <span className="font-bold text-gray-900 dark:text-white">${receivables.reduce((acc, curr) => acc + curr.balance, 0).toLocaleString()}</span></div>
              </div>

              {/* Desktop Table */}
              <div className="hidden md:block overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
                  <thead className="bg-gray-50 dark:bg-slate-900">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">File</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Cliente</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Estado</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Total Venta</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Saldo</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Acciones</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white dark:bg-slate-800 divide-y divide-gray-200 dark:divide-slate-700">
                    {receivables.map((file) => (
                      <tr key={file.id} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900 dark:text-white">
                          {file.fileNumber}
                          <div className="text-xs text-gray-500 font-normal">{file.name}</div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                          {file.payer?.fullName || "-"}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <span className={`px-2 py-1 rounded-full text-xs font-medium 
                                                ${file.status === 'Reservado' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200' :
                              file.status === 'Operativo' ? 'bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200' :
                                'bg-gray-100 text-gray-800'}`}>
                            {file.status}
                          </span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-right text-gray-500 dark:text-slate-400">
                          ${file.totalSale?.toLocaleString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-bold text-red-600 dark:text-rose-400">
                          ${file.balance?.toLocaleString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium flex justify-end gap-2">
                          <button
                            onClick={() => handleOpenPayment(file)}
                            className="text-green-600 hover:text-green-900 dark:text-green-400 dark:hover:text-green-300 font-medium px-2 py-1 hover:bg-green-50 dark:hover:bg-green-900/20 rounded"
                          >
                            Cobrar
                          </button>
                          <button
                            onClick={() => handleCreateInvoice(file)}
                            className="text-indigo-600 hover:text-indigo-900 dark:text-indigo-400 dark:hover:text-indigo-300 font-medium px-2 py-1 hover:bg-indigo-50 dark:hover:bg-indigo-900/20 rounded flex items-center gap-1"
                            disabled={creatingInvoice}
                          >
                            {creatingInvoice ? <Loader2 className="w-3 h-3 animate-spin" /> : null}
                            Facturar
                          </button>
                        </td>
                      </tr>
                    ))}
                    {receivables.length === 0 && (
                      <tr>
                        <td colSpan={6} className="px-6 py-12 text-center text-gray-500">
                          <CheckCircle className="w-12 h-12 text-green-500 mx-auto mb-3 opacity-50" />
                          <p>¡Todo al día! No hay cuentas por cobrar pendientes.</p>
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>

              {/* Mobile Cards - Receivables */}
              <div className="md:hidden divide-y divide-gray-200 dark:divide-slate-700">
                {receivables.length === 0 ? (
                  <div className="p-8 text-center text-gray-500">
                    <CheckCircle className="w-12 h-12 text-green-500 mx-auto mb-3 opacity-50" />
                    <p>¡Todo al día! No hay cuentas por cobrar pendientes.</p>
                  </div>
                ) : (
                  receivables.map((file) => (
                    <div key={file.id} className="p-4 space-y-3">
                      <div className="flex justify-between items-start">
                        <div>
                          <div className="font-bold text-lg text-primary">{file.fileNumber}</div>
                          <div className="text-sm font-medium">{file.name}</div>
                        </div>
                        <span className={`px-2 py-1 rounded-full text-xs font-medium 
                                    ${file.status === 'Reservado' ? 'bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-200' :
                            file.status === 'Operativo' ? 'bg-purple-100 text-purple-800 dark:bg-purple-900 dark:text-purple-200' :
                              'bg-gray-100 text-gray-800'}`}>
                          {file.status}
                        </span>
                      </div>

                      <div className="text-sm text-gray-500">
                        <div>Cliente: <span className="text-gray-900 dark:text-white font-medium">{file.payer?.fullName || "-"}</span></div>
                      </div>

                      <div className="flex justify-between items-end pt-2 border-t border-dashed border-gray-200 dark:border-slate-700">
                        <div>
                          <div className="text-xs text-gray-500">Saldo Pendiente</div>
                          <div className="text-xl font-bold text-red-600 dark:text-rose-400">${file.balance?.toLocaleString()}</div>
                        </div>
                        <div className="flex gap-2">
                          <button
                            onClick={() => handleCreateInvoice(file)}
                            className="p-2 text-indigo-600 bg-indigo-50 hover:bg-indigo-100 rounded-lg dark:bg-indigo-900/20 dark:text-indigo-400"
                            disabled={creatingInvoice}
                            title="Facturar"
                          >
                            {creatingInvoice ? <Loader2 className="w-5 h-5 animate-spin" /> : <FileText className="w-5 h-5" />}
                          </button>
                          <button
                            onClick={() => handleOpenPayment(file)}
                            className="px-4 py-2 bg-green-600 text-white font-medium rounded-lg hover:bg-green-700 shadow-sm shadow-green-200 dark:shadow-none"
                          >
                            Cobrar
                          </button>
                        </div>
                      </div>
                    </div>
                  ))
                )}
              </div>
            </div>
          )}

          {/* --- TAB: HISTORY --- */}
          {activeTab === 'history' && (
            <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
              {/* Desktop Table */}
              <div className="hidden md:block overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
                  <thead className="bg-gray-50 dark:bg-slate-900">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Fecha</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">File / Concepto</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Método</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Monto</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white dark:bg-slate-800 divide-y divide-gray-200 dark:divide-slate-700">
                    {payments.map((payment) => (
                      <tr key={payment.id} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                          {new Date(payment.paidAt).toLocaleDateString()}
                          <div className="text-xs text-gray-400">{new Date(payment.paidAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</div>
                        </td>
                        <td className="px-6 py-4">
                          <div className="text-sm font-medium text-gray-900 dark:text-white">
                            {payment.travelFile ? `File ${payment.travelFile.fileNumber}` : "-"}
                          </div>
                          <div className="text-xs text-gray-500">
                            {payment.notes || "Sin notas"}
                          </div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                          {payment.method}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-green-600 dark:text-emerald-400">
                          ${payment.amount?.toLocaleString()}
                        </td>
                      </tr>
                    ))}
                    {payments.length === 0 && (
                      <tr>
                        <td colSpan={4} className="px-6 py-12 text-center text-gray-500">No hay pagos registrados.</td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>

              {/* Mobile Cards - History */}
              <div className="md:hidden divide-y divide-gray-200 dark:divide-slate-700">
                {payments.length === 0 ? (
                  <div className="p-8 text-center text-gray-500">No hay pagos registrados.</div>
                ) : (
                  payments.map((payment) => (
                    <div key={payment.id} className="p-4 flex justify-between items-center bg-white dark:bg-slate-800">
                      <div>
                        <div className="flex items-center gap-2 mb-1">
                          <span className="text-sm font-bold text-gray-900 dark:text-white">
                            {payment.travelFile ? `File ${payment.travelFile.fileNumber}` : "-"}
                          </span>
                          <span className="text-xs px-2 py-0.5 bg-gray-100 dark:bg-slate-700 rounded-full text-gray-600 dark:text-slate-300">
                            {payment.method}
                          </span>
                        </div>
                        <div className="text-xs text-gray-500">
                          {new Date(payment.paidAt).toLocaleDateString()} • {new Date(payment.paidAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                        </div>
                        {payment.notes && <div className="text-xs text-gray-400 mt-1 italic">{payment.notes}</div>}
                      </div>
                      <div className="text-right font-bold text-green-600 dark:text-emerald-400">
                        +${payment.amount?.toLocaleString()}
                      </div>
                    </div>
                  ))
                )}
              </div>
            </div>
          )}

          {/* --- TAB: INVOICES --- */}
          {activeTab === 'invoices' && (
            <div className="bg-white dark:bg-slate-800 rounded-xl shadow-sm border border-gray-200 dark:border-slate-700 overflow-hidden">
              {/* Desktop Table */}
              <div className="hidden md:block overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
                  <thead className="bg-gray-50 dark:bg-slate-900">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Fecha</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Comprobante</th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase">Cliente</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Importe</th>
                      <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase">Acciones</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white dark:bg-slate-800 divide-y divide-gray-200 dark:divide-slate-700">
                    {invoices.map((inv) => (
                      <tr key={inv.id} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                          {new Date(inv.createdAt).toLocaleDateString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900 dark:text-white">
                          {inv.tipoComprobante === 1 ? "Factura A" : inv.tipoComprobante === 6 ? "Factura B" : "Comprobante"}
                          <span className="ml-2 text-gray-500 font-mono">#{inv.numeroComprobante}</span>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                          {inv.travelFile?.payer?.fullName || "Consumidor Final"}
                          <div className="text-xs text-gray-400">File {inv.travelFile?.fileNumber}</div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-gray-900 dark:text-white">
                          ${inv.importeTotal?.toLocaleString()}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                          <div className="flex items-center justify-end gap-2">
                            <button
                              onClick={() => handleDownloadPdf(inv)}
                              className="text-indigo-600 hover:text-indigo-900 dark:text-indigo-400 dark:hover:text-indigo-300 flex items-center justify-end gap-1"
                            >
                              <Download className="w-4 h-4" /> PDF
                            </button>

                            {(inv.tipoComprobante === 1 || inv.tipoComprobante === 6 || inv.tipoComprobante === 11) && (
                              <button
                                onClick={() => handleAnnulInvoice(inv)}
                                className="text-red-500 hover:text-red-700 dark:text-red-400 dark:hover:text-red-300 ml-2"
                                title="Anular (Nota de Crédito)"
                              >
                                <XCircle className="w-4 h-4" />
                              </button>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))}
                    {invoices.length === 0 && (
                      <tr>
                        <td colSpan={5} className="px-6 py-12 text-center text-gray-500">No hay facturas emitidas.</td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>

              {/* Mobile Cards - Invoices */}
              <div className="md:hidden divide-y divide-gray-200 dark:divide-slate-700">
                {invoices.length === 0 ? (
                  <div className="p-8 text-center text-gray-500">No hay facturas emitidas.</div>
                ) : (
                  invoices.map((inv) => (
                    <div key={inv.id} className="p-4 space-y-2">
                      <div className="flex justify-between items-start">
                        <div className="font-medium text-gray-900 dark:text-white">
                          {inv.tipoComprobante === 1 ? "Factura A" : inv.tipoComprobante === 6 ? "Factura B" : "Comprobante"}
                          <span className="ml-1 text-gray-500 font-mono">#{inv.numeroComprobante}</span>
                        </div>
                        <div className="text-xs text-gray-500">
                          {new Date(inv.createdAt).toLocaleDateString()}
                        </div>
                      </div>
                      <div className="text-sm flex justify-between items-center">
                        <div>
                          <div className="font-medium text-gray-700 dark:text-slate-300">{inv.travelFile?.payer?.fullName || "Consumidor Final"}</div>
                          <div className="text-xs text-gray-500">File {inv.travelFile?.fileNumber}</div>
                        </div>
                        <div className="text-right">
                          <div className="font-bold text-gray-900 dark:text-white">${inv.importeTotal?.toLocaleString()}</div>
                          <button
                            onClick={() => handleDownloadPdf(inv)}
                            className="text-xs text-indigo-600 font-medium flex items-center gap-1 justify-end mt-1"
                          >
                            <Download className="w-3 h-3" /> Descargar PDF
                          </button>
                        </div>
                      </div>
                    </div>
                  ))
                )}
              </div>
            </div>
          )}
        </>
      )}

      {/* Payment Modal */}
      <PaymentModal
        isOpen={showPaymentModal}
        onClose={() => { setShowPaymentModal(false); setSelectedFile(null); }}
        fileId={selectedFile?.id}
        maxAmount={selectedFile?.balance}
        onSuccess={() => { loadData(); }}
      />
    </div>
  );
}
