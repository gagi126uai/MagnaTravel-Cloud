import { useState } from "react";
import {
  Search,
  Banknote,
  FileText,
  Clock,
  Filter,
  Loader2
} from "lucide-react";

import { usePayments } from "../hooks/usePayments";
import PaymentModal from "../../../components/PaymentModal";
import CreateInvoiceModal from "../../../components/CreateInvoiceModal";
import { PaymentKPIs } from "../components/PaymentKPIs";
import { CollectionsTab } from "../components/CollectionsTab";
import { InvoicingTab } from "../components/InvoicingTab";
import { HistoryTab } from "../components/HistoryTab";

export default function PaymentsPage() {
  const [activeTab, setActiveTab] = useState("collections"); // collections, invoicing, history
  const [showPaymentModal, setShowPaymentModal] = useState(false);
  const [showInvoiceModal, setShowInvoiceModal] = useState(false);
  const [selectedFile, setSelectedFile] = useState(null);

  const {
    loading,
    payments,
    invoices,
    searchTerm,
    setSearchTerm,
    handleDownloadPdf,
    handleRetryInvoice,
    handleAnnulInvoice,
    filteredFiles,
    stats,
    loadData
  } = usePayments();

  const handleOpenPayment = (file) => {
    setSelectedFile(file);
    setShowPaymentModal(true);
  };

  const handleOpenInvoice = (file) => {
    if (file.status === 'Presupuesto') {
      return; // Hook or component should already guard but just in case
    }
    setSelectedFile({
      ...file,
      suggestedInvoiceAmount: file.pendingBilling
    });
    setShowInvoiceModal(true);
  };

  if (loading && filteredFiles.length === 0) {
    return <div className="flex justify-center items-center h-64 text-slate-400"><Loader2 className="w-8 h-8 animate-spin" /></div>;
  }

  return (
    <div className="max-w-7xl mx-auto p-4 sm:p-8 space-y-8 pb-20 animate-in fade-in duration-500">
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
              className="pl-9 pr-4 py-2 w-full text-sm bg-slate-50 dark:bg-slate-900 border-none rounded-full focus:ring-2 focus:ring-slate-200 transition-shadow dark:text-white"
            />
          </div>
          <button className="p-2 text-slate-400 hover:text-slate-600 bg-slate-50 dark:bg-slate-900 rounded-full transition-colors" title="Filtros avanzados">
            <Filter className="w-4 h-4" />
          </button>
        </div>
      </div>

      <PaymentKPIs stats={stats} />

      {/* SUBTLE TABS */}
      <div className="flex gap-6 border-b border-slate-100 dark:border-slate-800">
        {[
          { id: 'collections', label: 'Cobranzas', icon: Banknote },
          { id: 'invoicing', label: 'Emisión AFIP', icon: FileText },
          { id: 'history', label: 'Historial', icon: Clock },
        ].map(tab => (
          <button
            key={tab.id}
            onClick={() => setActiveTab(tab.id)}
            className={`pb-3 text-sm font-medium transition-colors relative ${activeTab === tab.id ? 'text-slate-900 dark:text-white' : 'text-slate-400 hover:text-slate-600'}`}
          >
            <div className="flex items-center gap-2"><tab.icon className="w-4 h-4" /> {tab.label}</div>
            {activeTab === tab.id && <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-slate-900 dark:bg-white rounded-t-full" />}
          </button>
        ))}
      </div>

      {/* TAB CONTENT */}
      <div className="min-h-[400px]">
        {activeTab === 'collections' && (
          <CollectionsTab files={filteredFiles} onPay={handleOpenPayment} />
        )}
        {activeTab === 'invoicing' && (
          <InvoicingTab files={filteredFiles} onInvoice={handleOpenInvoice} />
        )}
        {activeTab === 'history' && (
          <HistoryTab
            payments={payments}
            invoices={invoices}
            onDownloadPdf={handleDownloadPdf}
            onAnnulInvoice={handleAnnulInvoice}
            onRetryInvoice={handleRetryInvoice}
          />
        )}
      </div>

      {/* MODALS */}
      <PaymentModal
        isOpen={showPaymentModal}
        onClose={() => { setShowPaymentModal(false); setSelectedFile(null); }}
        fileId={selectedFile?.id}
        maxAmount={selectedFile?.pendingCollection}
        onSuccess={loadData}
      />

      <CreateInvoiceModal
        isOpen={showInvoiceModal}
        onClose={() => { setShowInvoiceModal(false); setSelectedFile(null); }}
        fileId={selectedFile?.id}
        initialAmount={selectedFile?.suggestedInvoiceAmount}
        clientName={selectedFile?.payer?.fullName || selectedFile?.customerName}
        clientCuit={selectedFile?.payer?.taxId || selectedFile?.payer?.documentNumber}
        onSuccess={loadData}
      />
    </div>
  );
}
