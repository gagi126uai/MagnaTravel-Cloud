/**
 * Consulta GET /api/rates/variant-price-suggestion (spec firmada 2026-08-07, §3.3 / M-15)
 * para saber qué precio corresponde a la habitación/cabina/vehículo que se está por
 * vender de un producto YA elegido. Se dispara cada vez que cambia la combinación
 * (producto + operador + variante) — típicamente porque el vendedor cambió de
 * habitación, régimen, categoría, cabina o vehículo DESPUÉS de elegir el producto.
 *
 * Mismo patrón de debounce + doble guarda anti-race que useTipoCambioSugerido.js
 * (features/invoices/hooks): closure `cancelado` + comparación de "clave vigente" para
 * descartar respuestas que llegan fuera de orden.
 *
 * SIN toast ni cartel de error: red caída, 204 (sin precios aprendidos todavía) y "no
 * corresponde consultar todavía" (sin rateId) se ven todos igual, "sin sugerencia" — el
 * casillero queda como estaba, nunca se traba la ficha (spec §3.2).
 */

import { useEffect, useRef, useState } from "react";
import { api } from "../../../api";

const DEBOUNCE_MS = 300;

/**
 * @param {{
 *   ratePublicId: string|null,
 *   supplierId?: string,
 *   roomType?: string, mealPlan?: string, roomCategory?: string,
 *   cabinClass?: string, vehicleType?: string,
 * }} variante — identifica el producto + la combinación que se está por vender
 * @returns {{ suggestion: object|null, loading: boolean }}
 */
export function useVariantPriceSuggestion(variante) {
  const [suggestion, setSuggestion] = useState(null);
  const [loading, setLoading] = useState(false);

  const {
    ratePublicId, supplierId, roomType, mealPlan, roomCategory, cabinClass, vehicleType,
  } = variante || {};

  // Clave de la combinación vigente en TODO momento (no depende del closure del efecto),
  // para poder descartar una respuesta vieja aunque haya llegado con éxito.
  const claveVigenteRef = useRef("");
  const claveActual = JSON.stringify({ ratePublicId, supplierId, roomType, mealPlan, roomCategory, cabinClass, vehicleType });
  claveVigenteRef.current = claveActual;

  useEffect(() => {
    // Sin producto elegido todavía no hay nada que consultar (el vendedor puede estar
    // en medio de "crear nuevo", donde jamás existió una venta previa que preguntar).
    if (!ratePublicId) {
      setSuggestion(null);
      setLoading(false);
      return;
    }

    let cancelado = false;
    const claveConsultada = claveActual;
    setLoading(true);

    const timer = setTimeout(async () => {
      if (cancelado) return;
      try {
        const params = new URLSearchParams({ ratePublicId });
        if (supplierId) params.set("supplierId", supplierId);
        if (roomType) params.set("roomType", roomType);
        if (mealPlan) params.set("mealPlan", mealPlan);
        if (roomCategory) params.set("roomCategory", roomCategory);
        if (cabinClass) params.set("cabinClass", cabinClass);
        if (vehicleType) params.set("vehicleType", vehicleType);

        const respuesta = await api.get(`/rates/variant-price-suggestion?${params.toString()}`);
        if (cancelado || claveConsultada !== claveVigenteRef.current) return;
        // 204 (sin precios aprendidos) ya llega como `null` desde api.get().
        setSuggestion(respuesta || null);
      } catch {
        if (!cancelado) setSuggestion(null);
      } finally {
        if (!cancelado) setLoading(false);
      }
    }, DEBOUNCE_MS);

    return () => {
      cancelado = true;
      clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ratePublicId, supplierId, roomType, mealPlan, roomCategory, cabinClass, vehicleType]);

  return { suggestion, loading };
}
