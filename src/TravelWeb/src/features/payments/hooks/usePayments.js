import { useState, useCallback, useEffect, useMemo } from 'react';
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import Swal from "sweetalert2";

export function usePayments() {
    const [loading, setLoading] = useState(true);
    const [globalReservas, setGlobalReservas] = useState([]);
    const [payments, setPayments] = useState([]);
    const [invoices, setInvoices] = useState([]);
    const [searchTerm, setSearchTerm] = useState("");
    const [dateFilter, setDateFilter] = useState("all");

    const loadData = useCallback(async () => {
        setLoading(true);
        try {
            const [reservasRes, invoicesRes, paymentsRes] = await Promise.all([
                api.get("/reservas"),
                api.get("/invoices"),
                api.get("/payments")
            ]);

            const enhancedReservas = reservasRes.map(r => {
                const reservaInvoices = invoicesRes.filter(i => i.reservaId === r.id);

                const totalSale = f.totalSale || 0;
                const totalPaid = f.totalPaid || 0;

                const totalInvoiced = reservaInvoices.reduce((acc, i) => {
                    if (i.resultado !== 'A') return acc;
                    const isCreditNote = [3, 8, 13, 53].includes(i.tipoComprobante);
                    if (isCreditNote) return acc - i.importeTotal;
                    return acc + i.importeTotal;
                }, 0);

                const moneyCollectedNotInvoiced = totalPaid - totalInvoiced;

                return {
                    ...r,
                    invoices: reservaInvoices,
                    computedPaid: totalPaid,
                    computedInvoiced: totalInvoiced,
                    pendingCollection: totalSale - totalPaid,
                    pendingBilling: moneyCollectedNotInvoiced > 0 ? (Math.round(moneyCollectedNotInvoiced * 100) / 100) : 0,
                    totalSaleAmount: totalSale
                };
            });

            setGlobalReservas(enhancedReservas);

            const allPayments = paymentsRes.map(p => ({
                ...p,
                reserva: enhancedReservas.find(r => r.id === p.reservaId) || { id: p.reservaId, numeroReserva: p.numeroReserva }
            }));
            allPayments.sort((a, b) => new Date(b.paidAt) - new Date(a.paidAt));
            setPayments(allPayments);

            const allInvoices = invoicesRes.map(i => ({
                ...i,
                reserva: enhancedReservas.find(r => r.id === i.reservaId) || i.reserva
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

    const filteredReservas = useMemo(() => {
        return globalReservas.filter(r => {
            const matchesSearch = r.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
                r.numeroReserva?.toLowerCase().includes(searchTerm.toLowerCase()) ||
                r.payer?.fullName?.toLowerCase().includes(searchTerm.toLowerCase());
            return matchesSearch;
        });
    }, [globalReservas, searchTerm]);

    const stats = useMemo(() => {
        const totalPendingCollection = globalReservas.reduce((acc, r) => acc + (r.pendingCollection > 0 ? r.pendingCollection : 0), 0);
        const totalPendingBilling = globalReservas.reduce((acc, r) => acc + (r.pendingBilling > 0 ? r.pendingBilling : 0), 0);

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
    }, [globalReservas, invoices]);

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
        filteredReservas,
        stats
    };
}
