import { useState, useEffect, useCallback } from "react";
import { api } from "../api";
import { FileText, Plus, CheckCircle, XCircle, AlertTriangle, Eye, RefreshCw } from "lucide-react";
import Swal from "sweetalert2";
import { showError, showSuccess } from "../alerts";
import CreateInvoiceModal from "./CreateInvoiceModal";

export default function InvoicesTab({ fileId, balance, onInvoiceCreated, readOnly = false }) {
    const [invoices, setInvoices] = useState([]);
    const [loading, setLoading] = useState(false);
    const [showCreateModal, setShowCreateModal] = useState(false);

    const fetchInvoices = useCallback(async () => {
        try {
            setLoading(true);
            const res = await api.get(`/invoices/file/${fileId}`);
            setInvoices(res || []);
        } catch (error) {
            console.error(error);
        } finally {
            setLoading(false);
        }
    }, [fileId]);

    useEffect(() => {
        fetchInvoices();
    }, [fetchInvoices]);

    const handleCreateInvoice = () => {
        setShowCreateModal(true);
    };

    const handleInvoiceCreated = () => {
        fetchInvoices();
        if (onInvoiceCreated) onInvoiceCreated();
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
            fetchInvoices();

            // Offer automatic download
            const download = await Swal.fire({
                title: 'Descargar Comprobante',
                text: '¿Querés descargar el PDF de la anulación ahora?',
                icon: 'question',
                showCancelButton: true,
                confirmButtonText: 'Sí, descargar',
                cancelButtonText: 'No por ahora'
            });

            if (download.isConfirmed) {
                handleDownloadPdf(newInvoice);
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
            link.parentNode.removeChild(link);
        } catch (error) {
            console.error("Error downloading PDF:", error);
            showError("No se pudo descargar el PDF");
        }
    };

    const getInvoiceLabel = (type) => {
        switch (type) {
            case 1: return "Factura A";
            case 2: return "Nota de Débito A";
            case 3: return "Nota de Crédito A";
            case 6: return "Factura B";
            case 7: return "Nota de Débito B";
            case 8: return "Nota de Crédito B";
            case 11: return "Factura C";
            case 12: return "Nota de Débito C";
            case 13: return "Nota de Crédito C";
            case 51: return "Factura M";
            case 52: return "Nota de Débito M";
            case 53: return "Nota de Crédito M";
            default: return `Comprobante (${type})`;
        }
    };

    return (
        <div>
            <div className="flex justify-between items-center mb-4">
                <h3 className="text-lg font-medium text-gray-900 dark:text-white">Facturas Emitidas</h3>
                {!readOnly && (
                    <button
                        onClick={handleCreateInvoice}
                        disabled={creating}
                        className="flex items-center gap-2 bg-indigo-600 text-white px-4 py-2 rounded-lg hover:bg-indigo-700 transition-colors shadow-sm disabled:opacity-50"
                    >
                        {creating ? <RefreshCw className="w-4 h-4 animate-spin" /> : <Plus className="w-4 h-4" />}
                        Emitir Factura
                    </button>
                )}
            </div>

            {loading ? (
                <div className="text-center py-8 text-gray-500">Cargando...</div>
            ) : invoices.length === 0 ? (
                <div className="text-center py-12 bg-gray-50 dark:bg-slate-800 rounded-lg border border-dashed border-gray-300 dark:border-slate-700">
                    <FileText className="w-12 h-12 text-gray-300 dark:text-slate-600 mx-auto mb-3" />
                    <p className="text-gray-500 dark:text-slate-400">No hay facturas emitidas para este expediente.</p>
                </div>
            ) : (
                <div className="overflow-hidden rounded-lg border border-gray-200 dark:border-slate-700">
                    <table className="min-w-full divide-y divide-gray-200 dark:divide-slate-700">
                        <thead className="bg-gray-50 dark:bg-slate-900">
                            <tr>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-slate-400 uppercase">Fecha</th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-slate-400 uppercase">Tipo</th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-slate-400 uppercase">Número</th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-slate-400 uppercase">CAE</th>
                                <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 dark:text-slate-400 uppercase">Importe</th>
                                <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 dark:text-slate-400 uppercase">Estado</th>
                            </tr>
                        </thead>
                        <tbody className="bg-white dark:bg-slate-800 divide-y divide-gray-200 dark:divide-slate-700">
                            {invoices.map((inv) => (
                                <tr key={inv.id} className="hover:bg-gray-50 dark:hover:bg-slate-700/50">
                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                                        {new Date(inv.createdAt).toLocaleDateString()}
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900 dark:text-white">
                                        {getInvoiceLabel(inv.tipoComprobante)}
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                                        {inv.puntoDeVenta}-{inv.numeroComprobante}
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-slate-400">
                                        {inv.cae}
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-right font-medium text-gray-900 dark:text-white">
                                        ${inv.importeTotal.toLocaleString()}
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-right text-sm">
                                        <div className="flex items-center justify-end gap-2">
                                            {inv.resultado === 'A' ? (
                                                <>
                                                    <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-400">
                                                        Aprobado
                                                    </span>
                                                    <button
                                                        onClick={() => handleDownloadPdf(inv)}
                                                        className="text-indigo-600 hover:text-indigo-900 dark:text-indigo-400 dark:hover:text-indigo-300"
                                                        title="Descargar PDF"
                                                    >
                                                        <FileText className="w-4 h-4" />
                                                    </button>

                                                    {/* Annul Button for Invoices (1,6,11) and Credit Notes (3,8,13) */}
                                                    {(inv.tipoComprobante === 1 || inv.tipoComprobante === 6 || inv.tipoComprobante === 11 ||
                                                        inv.tipoComprobante === 3 || inv.tipoComprobante === 8 || inv.tipoComprobante === 13) && !readOnly && (
                                                            <button
                                                                onClick={() => handleAnnulInvoice(inv)}
                                                                className="text-red-500 hover:text-red-700 dark:text-red-400 dark:hover:text-red-300"
                                                                title="Anular (Nota de Crédito/Débito)"
                                                            >
                                                                <XCircle className="w-4 h-4" />
                                                            </button>
                                                        )}
                                                </>
                                            ) : (
                                                <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-400">
                                                    Rechazado
                                                </span>
                                            )}
                                        </div>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}

            <CreateInvoiceModal
                isOpen={showCreateModal}
                onClose={() => setShowCreateModal(false)}
                onSuccess={handleInvoiceCreated}
                fileId={fileId}
                initialAmount={balance}
            />
        </div>
    );
}
