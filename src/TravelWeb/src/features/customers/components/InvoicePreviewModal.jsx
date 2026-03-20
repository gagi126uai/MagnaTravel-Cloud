import { useEffect, useRef, useState } from "react";
import { AlertCircle, Eye, Loader2, Receipt, X } from "lucide-react";

import { api } from "../../../api";
import { Button } from "../../../components/ui/button";

const formatInvoiceNumber = (invoice) => {
    if (!invoice) return "";
    return `${String(invoice.puntoDeVenta ?? 0).padStart(5, "0")}-${String(invoice.numeroComprobante ?? 0).padStart(8, "0")}`;
};

const formatInvoiceType = (invoice) => {
    if (!invoice) return "";

    switch (invoice.tipoComprobante) {
        case 1:
            return "Factura A";
        case 6:
            return "Factura B";
        case 11:
            return "Factura C";
        default:
            return `Tipo ${invoice.tipoComprobante}`;
    }
};

export default function InvoicePreviewModal({ isOpen, invoice, onClose }) {
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState("");
    const [pdfUrl, setPdfUrl] = useState("");
    const currentUrlRef = useRef(null);

    const cleanupObjectUrl = () => {
        if (currentUrlRef.current) {
            URL.revokeObjectURL(currentUrlRef.current);
            currentUrlRef.current = null;
        }
    };

    useEffect(() => {
        if (!isOpen) return undefined;

        const handleEscape = (event) => {
            if (event.key === "Escape") {
                onClose();
            }
        };

        window.addEventListener("keydown", handleEscape);
        return () => window.removeEventListener("keydown", handleEscape);
    }, [isOpen, onClose]);

    useEffect(() => {
        if (!isOpen || !invoice?.id) {
            cleanupObjectUrl();
            setPdfUrl("");
            setLoading(false);
            setError("");
            return undefined;
        }

        let cancelled = false;

        const loadInvoicePdf = async () => {
            cleanupObjectUrl();
            setPdfUrl("");
            setError("");
            setLoading(true);

            try {
                const blob = await api.get(`/invoices/${invoice.id}/pdf`, { responseType: "blob" });
                if (!(blob instanceof Blob) || blob.size === 0) {
                    throw new Error("La factura no devolvió un PDF válido.");
                }

                const url = URL.createObjectURL(blob);
                if (cancelled) {
                    URL.revokeObjectURL(url);
                    return;
                }

                currentUrlRef.current = url;
                setPdfUrl(url);
            } catch (fetchError) {
                if (!cancelled) {
                    setError(fetchError.message || "No se pudo cargar la factura.");
                }
            } finally {
                if (!cancelled) {
                    setLoading(false);
                }
            }
        };

        loadInvoicePdf();

        return () => {
            cancelled = true;
            cleanupObjectUrl();
        };
    }, [isOpen, invoice?.id]);

    if (!isOpen || !invoice) {
        return null;
    }

    return (
        <div
            className="fixed inset-0 z-50 flex items-center justify-center bg-slate-950/70 p-3 backdrop-blur-sm md:p-6"
            onClick={onClose}
            role="dialog"
            aria-modal="true"
            aria-labelledby="invoice-preview-title"
        >
            <div
                className="flex h-[92vh] w-full max-w-6xl flex-col overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl dark:border-slate-800 dark:bg-slate-950"
                onClick={(event) => event.stopPropagation()}
            >
                <div className="flex items-start justify-between gap-4 border-b border-slate-200 px-4 py-4 dark:border-slate-800 md:px-6">
                    <div className="min-w-0">
                        <div className="flex items-center gap-2 text-xs font-black uppercase tracking-[0.2em] text-indigo-500">
                            <Receipt className="h-3.5 w-3.5" />
                            Facturación AFIP
                        </div>
                        <h2 id="invoice-preview-title" className="mt-2 text-lg font-bold text-slate-900 dark:text-white md:text-xl">
                            {formatInvoiceType(invoice)} {formatInvoiceNumber(invoice)}
                        </h2>
                        <p className="mt-1 flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400">
                            <Eye className="h-4 w-4" />
                            Vista embebida de la factura sin descarga
                        </p>
                    </div>

                    <Button type="button" variant="ghost" size="icon" onClick={onClose} aria-label="Cerrar vista previa">
                        <X className="h-5 w-5" />
                    </Button>
                </div>

                <div className="flex-1 bg-slate-100 dark:bg-slate-900">
                    {loading ? (
                        <div className="flex h-full flex-col items-center justify-center gap-3 px-6 text-center">
                            <Loader2 className="h-8 w-8 animate-spin text-indigo-500" />
                            <div>
                                <p className="font-semibold text-slate-900 dark:text-white">Cargando factura...</p>
                                <p className="text-sm text-slate-500 dark:text-slate-400">Estamos preparando el PDF para visualizarlo dentro de la cuenta corriente.</p>
                            </div>
                        </div>
                    ) : error ? (
                        <div className="flex h-full flex-col items-center justify-center gap-3 px-6 text-center">
                            <AlertCircle className="h-9 w-9 text-rose-500" />
                            <div>
                                <p className="font-semibold text-slate-900 dark:text-white">No se pudo abrir la factura</p>
                                <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{error}</p>
                            </div>
                        </div>
                    ) : (
                        <object data={pdfUrl} type="application/pdf" className="h-full w-full">
                            <div className="flex h-full flex-col items-center justify-center gap-3 px-6 text-center">
                                <AlertCircle className="h-9 w-9 text-amber-500" />
                                <div>
                                    <p className="font-semibold text-slate-900 dark:text-white">No fue posible mostrar el PDF embebido</p>
                                    <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                                        El navegador no pudo renderizar la factura dentro del modal. Cerrá esta vista e intentá nuevamente.
                                    </p>
                                </div>
                            </div>
                        </object>
                    )}
                </div>
            </div>
        </div>
    );
}
