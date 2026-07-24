import { formatCurrency } from "./utils.js";

/**
 * Lógica compartida para el aviso de sobrecobro/sobrepago (Bug #3, Tanda 4, 2026-07-24).
 *
 * Decisión FIRMADA del dueño (no se re-pregunta, ver P-14 de la constitución): cobrarle
 * de más a un cliente o pagarle de más a un proveedor NUNCA se bloquea con un tope duro.
 * Como esa acción mueve plata real, se avisa CUÁNTO excedente queda y se pide confirmar
 * antes de guardar — el excedente queda como saldo a favor (del cliente, o nuestro con
 * el operador, según el caso) para usar después.
 *
 * Estas funciones las usan tres pantallas que cobran/pagan plata:
 *   - RegistrarCobroInline (cobro al cliente, en la ficha de la reserva)
 *   - PagarProveedorInline (pago a un proveedor)
 *   - CustomerPaymentModal (cobro al cliente, modal viejo — reemplaza un tope duro que
 *     contradecía esta decisión)
 */

// Tolerancia de medio centavo: un resto de redondeo no debe disparar el aviso.
const TOLERANCIA_CENTAVOS = 0.005;

/**
 * Calcula cuánto excedente hay si se cobra/paga `monto` contra una deuda de `deudaAntes`.
 * Devuelve 0 (no 0.00001) cuando el monto no supera la deuda, para que el llamador pueda
 * usar `excedente > 0` como el único chequeo que necesita.
 *
 * @param {number|string} monto — lo que se está por cobrar/pagar
 * @param {number|string|null|undefined} deudaAntes — lo que se debía ANTES de este movimiento
 * @returns {number} excedente (siempre >= 0)
 */
export function calcularExcedente(monto, deudaAntes) {
    // Ojo: Number(null) da 0 (no NaN) en JS — sin este chequeo explícito, "sin dato de
    // deuda" se confundiría con "deuda $0" y dispararía un aviso de sobrecobro inventado.
    if (monto == null || deudaAntes == null) return 0;

    const montoNumero = Number(monto);
    const deudaNumero = Number(deudaAntes);
    if (!Number.isFinite(montoNumero) || !Number.isFinite(deudaNumero)) return 0;

    const excedente = montoNumero - deudaNumero;
    return excedente > TOLERANCIA_CENTAVOS ? excedente : 0;
}

/**
 * Arma las props para `showConfirm()` (alerts.js) cuando un COBRO AL CLIENTE supera lo
 * que debía. El excedente queda como saldo a favor DEL CLIENTE.
 *
 * @param {{excedente:number, moneda:string}} datos
 * @returns {{title:string, text:string, confirmText:string, confirmColor:string}}
 */
export function construirConfirmacionSobrecobroCliente({ excedente, moneda }) {
    return {
        title: "Estás cobrando de más",
        text: `Estás cobrando ${formatCurrency(excedente, moneda)} más de lo que debe el cliente. Ese excedente queda como saldo a favor del cliente para usar después. ¿Confirmás?`,
        confirmText: "Sí, cobrar igual",
        confirmColor: "amber",
    };
}

/**
 * Arma las props para `showConfirm()` cuando un PAGO A UN PROVEEDOR supera lo que se le
 * debía. El excedente queda como saldo a favor NUESTRO con ese proveedor.
 *
 * @param {{excedente:number, moneda:string}} datos
 * @returns {{title:string, text:string, confirmText:string, confirmColor:string}}
 */
export function construirConfirmacionSobrepagoProveedor({ excedente, moneda }) {
    return {
        title: "Estás pagando de más",
        text: `Estás pagando ${formatCurrency(excedente, moneda)} más de lo que le debías a este proveedor. Ese excedente queda como saldo a favor nuestro con el operador. ¿Confirmás?`,
        confirmText: "Sí, pagar igual",
        confirmColor: "amber",
    };
}
