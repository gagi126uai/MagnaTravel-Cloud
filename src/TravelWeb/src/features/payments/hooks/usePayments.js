import { useState, useCallback, useEffect, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import Swal from "sweetalert2";

export function usePayments() {
    const [loading, setLoading] = useState(true);
    const [globalFiles, setGlobalFiles] = useState([]);
    const [payments, setPayments] = useState([]);
    const [invoices, setInvoices] = useState([]);
    const [searchTerm, setSearchTerm] = useState("");
    const [dateFilter, setDateFilter] = useState("all");

    const loadData = useCallback(async () => {
        setLoading(true);
        try {
            const [filesRes, invoicesRes, paymentsRes] = await Promise.all([
                api.get("/travelfiles"),
                api.get("/invoices"),
                api.get("/payments")
            ]);

            const enhancedFiles = filesRes.map(f => {
                const fileInvoices = invoicesRes.filter(i => i.travelFileId === f.id);

                const totalSale = f.totalSale || 0;
                const totalPaid = f.totalPaid || 0;

                const totalInvoiced = fileInvoices.reduce((acc, i) => {
                    if (i.resultado !== 'A') return acc;
                    const isCreditNote = [3, 8, 13, 53].includes(i.tipoComprobante);
                    if (isCreditNote) return acc - i.importeTotal;
                    return acc + i.importeTotal;
                }, 0);

                const moneyCollectedNotInvoiced = totalPaid - totalInvoiced;

                return {
                    ...f,
                    invoices: fileInvoices,
                    computedPaid: totalPaid,
                    computedInvoiced: totalInvoiced,
                    pendingCollection: totalSale - totalPaid,
                    pendingBilling: moneyCollectedNotInvoiced > 0 ? (Math.round(moneyCollectedNotInvoiced * 100) / 100) : 0,
                    totalSaleAmount: totalSale
                };
            });

            setGlobalFiles(enhancedFiles);

            const allPayments = paymentsRes.map(p => ({
                ...p,
                travelFile: enhancedFiles.find(f => f.id === p.travelFileId) || { id: p.travelFileId, fileNumber: p.fileNumber }
            }));
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

    const handleViewPdf = async (invoice) => {
        try {
            const response = await api.get(`/invoices/${invoice.id}/pdf`, { responseType: 'blob' });
            const url = window.URL.createObjectURL(new Blob([response], { type: 'application/pdf' }));
            window.open(url, '_blank');
        } catch (error) {
            showError("Error al abrir PDF");
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

    const filteredFiles = useMemo(() => {
        return globalFiles.filter(f => {
            const matchesSearch = f.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
                f.fileNumber?.toLowerCase().includes(searchTerm.toLowerCase()) ||
                f.payer?.fullName?.toLowerCase().includes(searchTerm.toLowerCase());
            return matchesSearch;
        });
    }, [globalFiles, searchTerm]);

    const stats = useMemo(() => {
        const totalPendingCollection = globalFiles.reduce((acc, f) => acc + (f.pendingCollection > 0 ? f.pendingCollection : 0), 0);
        const totalPendingBilling = globalFiles.reduce((acc, f) => acc + (f.pendingBilling > 0 ? f.pendingBilling : 0), 0);

        const thisMonthInvoices = invoices.filter(i => i.resultado === 'A' && new Date(i.createdAt).getMonth() === new Date().getMonth());
        const totalInvoicedMonth = thisMonthInvoices.reduce((acc, i) => {
            const isCreditNote = [3, 8, 13, 53].includes(i.tipoComprobante);
            return isCreditNote ? acc - i.importeTotal : acc + i.importeTotal;
        }, 0);

        return {
            totalPendingCollection,
            totalPendingBilling,
            totalInvoicedMonth
        };
    }, [globalFiles, invoices]);

    return {
        loading,
        payments,
        invoices,
        searchTerm,
        setSearchTerm,
        dateFilter,
        setDateFilter,
        loadData,
        handleDownloadPdf,
        handleViewPdf,
        handleRetryInvoice,
        handleAnnulInvoice,
        filteredFiles,
        stats
    };
}
