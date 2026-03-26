import { useState, useEffect } from "react";
import { Printer, Download, Eye, Loader2, AlertCircle } from "lucide-react";
import { api } from "../api";
import { showError } from "../alerts";

export function ReservaVoucherTab({ reservaId }) {
    const [html, setHtml] = useState("");
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState(null);

    useEffect(() => {
        fetchVoucherPreview();
    }, [reservaId]);

    const fetchVoucherPreview = async () => {
        try {
            setLoading(true);
            setError(null);
            const response = await api.get(`/reservas/${reservaId}/voucher/preview`);
            setHtml(response.html);
        } catch (err) {
            console.error("Error fetching voucher preview:", err);
            setError("No se pudo cargar la vista previa del voucher.");
            showError("Error al cargar el voucher.");
        } finally {
            setLoading(false);
        }
    };

    const handlePrint = () => {
        const iframe = document.getElementById('voucher-iframe');
        if (iframe) {
            iframe.contentWindow.focus();
            iframe.contentWindow.print();
        }
    };

    const handleDownloadPdf = () => {
        downloadPdfAsBlob();
    };

    const downloadPdfAsBlob = async () => {
        try {
            const response = await api.get(`/reservas/${reservaId}/voucher/pdf`, { responseType: 'blob' });
            const url = window.URL.createObjectURL(new Blob([response]));
            const link = document.createElement('a');
            link.href = url;
            link.setAttribute('download', `voucher-${reservaId}.pdf`);
            document.body.appendChild(link);
            link.click();
            link.remove();
        } catch (err) {
            showError("No se pudo descargar el PDF.");
        }
    };

    if (loading) {
        return (
            <div className="flex flex-col items-center justify-center py-20 text-slate-500">
                <Loader2 className="w-10 h-10 animate-spin mb-4 text-indigo-500" />
                <p className="font-medium animate-pulse">Generando vista previa del voucher...</p>
            </div>
        );
    }

    if (error) {
        return (
            <div className="flex flex-col items-center justify-center py-20 text-rose-500 bg-rose-50 dark:bg-rose-950/20 rounded-2xl border border-rose-100 dark:border-rose-900/30">
                <AlertCircle className="w-12 h-12 mb-4" />
                <p className="font-bold text-lg">{error}</p>
                <button 
                    onClick={fetchVoucherPreview}
                    className="mt-4 px-4 py-2 bg-rose-600 text-white rounded-xl hover:bg-rose-700 transition-colors shadow-sm"
                >
                    Reintentar
                </button>
            </div>
        );
    }

    return (
        <div className="space-y-6 animate-in fade-in duration-500">
            {/* Header Actions */}
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 bg-slate-50 dark:bg-slate-800/50 p-4 rounded-2xl border border-slate-200 dark:border-slate-800">
                <div className="flex items-center gap-3">
                    <div className="p-2.5 bg-indigo-100 dark:bg-indigo-900/30 text-indigo-600 dark:text-indigo-400 rounded-xl">
                        <Eye className="w-5 h-5" />
                    </div>
                    <div>
                        <h3 className="font-bold text-slate-900 dark:text-white">Vista Previa del Voucher</h3>
                        <p className="text-[11px] text-slate-500 dark:text-slate-400 uppercase tracking-wider font-bold">Resumen de servicios confirmados</p>
                    </div>
                </div>
                <div className="flex w-full sm:w-auto gap-3">
                    <button 
                        onClick={handlePrint}
                        className="flex-1 sm:flex-none px-4 py-2.5 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-700 text-slate-700 dark:text-slate-200 rounded-xl text-sm font-bold hover:bg-slate-50 dark:hover:bg-slate-800 transition-all flex items-center justify-center gap-2 shadow-sm"
                    >
                        <Printer className="w-4 h-4" /> Imprimir
                    </button>
                    <button 
                        onClick={handleDownloadPdf}
                        className="flex-1 sm:flex-none px-4 py-2.5 bg-indigo-600 text-white rounded-xl text-sm font-bold hover:bg-indigo-700 transition-all flex items-center justify-center gap-2 shadow-lg shadow-indigo-200 dark:shadow-none"
                    >
                        <Download className="w-4 h-4" /> Descargar PDF
                    </button>
                </div>
            </div>

            {/* Voucher Container */}
            <div className="bg-white rounded-2xl border border-slate-300 dark:border-slate-700 shadow-2xl overflow-hidden flex flex-col mx-auto w-full max-w-[850px] ring-1 ring-slate-200 dark:ring-slate-800">
                <div className="bg-slate-100 dark:bg-slate-800 px-4 py-2 border-b border-slate-200 dark:border-slate-700 flex items-center justify-between text-[10px] font-bold text-slate-400 uppercase tracking-widest">
                    <span>Simulación de impresión</span>
                    <span>A4 Paper Size</span>
                </div>
                <iframe 
                    id="voucher-iframe"
                    title="Voucher Preview"
                    srcDoc={html}
                    className="w-full h-full border-none flex-grow bg-white"
                    style={{ minHeight: '1000px' }}
                />
            </div>

            <div className="bg-indigo-50 dark:bg-indigo-950/20 p-4 rounded-xl border border-indigo-100 dark:border-indigo-900/30 flex items-start gap-3 max-w-2xl mx-auto">
                <AlertCircle className="w-5 h-5 text-indigo-600 mt-0.5" />
                <p className="text-xs text-indigo-700 dark:text-indigo-400 leading-relaxed font-medium">
                    Esta es una previsualización del documento que recibirá el cliente. Los logos y datos de contacto se cargan desde la configuración de la agencia en el módulo de Ajustes.
                </p>
            </div>
        </div>
    );
}
