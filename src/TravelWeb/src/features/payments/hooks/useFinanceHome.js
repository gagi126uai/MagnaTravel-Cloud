import { useCallback, useEffect, useState } from "react";
import { api } from "../../../api";
import { showError } from "../../../alerts";

const emptyAlerts = { UrgentTrips: [], SupplierDebts: [], TotalCount: 0 };

export function useFinanceHome() {
  const [loading, setLoading] = useState(true);
  const [collectionsSummary, setCollectionsSummary] = useState(null);
  const [cashSummary, setCashSummary] = useState(null);
  const [invoicingSummary, setInvoicingSummary] = useState(null);
  const [alerts, setAlerts] = useState(emptyAlerts);

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [collectionsRes, cashRes, invoicingRes, alertsRes] = await Promise.all([
        api.get("/payments/collections-summary"),
        api.get("/treasury/cash-summary"),
        api.get("/invoices/summary"),
        api.get("/alerts"),
      ]);

      setCollectionsSummary(collectionsRes);
      setCashSummary(cashRes);
      setInvoicingSummary(invoicingRes);
      setAlerts(alertsRes || emptyAlerts);
    } catch (error) {
      console.error("Error loading finance home:", error);
      showError("Error al cargar el resumen del modulo.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  return {
    loading,
    collectionsSummary,
    cashSummary,
    invoicingSummary,
    alerts,
    loadData,
  };
}
