import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    resolverMonedaPrincipalProveedor,
    calcularEquivalenteProveedor,
    construirPayloadPagoProveedor,
    ordenarBloquesPesosPrimero,
    debeMostrarseEnGrisNeutro,
    aplanarReembolsosPendientesPorMoneda,
    validarFormularioReembolsoRecibido,
} from "./supplierPageLogic.js";

// ─── resolverMonedaPrincipalProveedor ────────────────────────────────────────

describe("resolverMonedaPrincipalProveedor", () => {
    it("retorna ARS cuando el array esta vacio", () => {
        assert.equal(resolverMonedaPrincipalProveedor([]), "ARS");
    });

    it("retorna ARS cuando el argumento es null", () => {
        assert.equal(resolverMonedaPrincipalProveedor(null), "ARS");
    });

    it("retorna ARS cuando el argumento es undefined", () => {
        assert.equal(resolverMonedaPrincipalProveedor(undefined), "ARS");
    });

    it("retorna la primera moneda con saldo positivo", () => {
        const balances = [
            { currency: "USD", balance: 500 },
            { currency: "ARS", balance: 10000 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "USD");
    });

    it("retorna ARS primero si tiene deuda aunque USD tambien tenga", () => {
        const balances = [
            { currency: "ARS", balance: 5000 },
            { currency: "USD", balance: 300 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "ARS");
    });

    it("retorna la primera de la lista cuando todos estan en cero", () => {
        const balances = [
            { currency: "ARS", balance: 0 },
            { currency: "USD", balance: 0 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "ARS");
    });

    it("retorna la primera de la lista cuando todos estan a favor (balance negativo)", () => {
        const balances = [
            { currency: "ARS", balance: -100 },
            { currency: "USD", balance: -200 },
        ];
        assert.equal(resolverMonedaPrincipalProveedor(balances), "ARS");
    });

    it("funciona con una sola moneda", () => {
        assert.equal(
            resolverMonedaPrincipalProveedor([{ currency: "USD", balance: 300 }]),
            "USD"
        );
    });

    it("ignora monedas con balance undefined o null, prefiere las con deuda real", () => {
        const balances = [
            { currency: "ARS", balance: null },
            { currency: "USD", balance: 500 },
        ];
        // null no es > 0, así que USD gana (tiene 500)
        assert.equal(resolverMonedaPrincipalProveedor(balances), "USD");
    });
});

// ─── calcularEquivalenteProveedor ────────────────────────────────────────────

describe("calcularEquivalenteProveedor", () => {
    it("retorna null cuando las monedas son iguales (ARS → ARS)", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "1200", "ARS", "ARS"), null);
    });

    it("retorna null cuando las monedas son iguales (USD → USD)", () => {
        assert.equal(calcularEquivalenteProveedor("500", "1200", "USD", "USD"), null);
    });

    it("retorna null cuando el TC esta vacio", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "", "ARS", "USD"), null);
    });

    it("retorna null cuando el monto esta vacio", () => {
        assert.equal(calcularEquivalenteProveedor("", "1200", "ARS", "USD"), null);
    });

    it("retorna null cuando el TC es cero", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "0", "ARS", "USD"), null);
    });

    it("retorna null cuando el TC es negativo", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "-1200", "ARS", "USD"), null);
    });

    it("retorna null cuando monedaCobro es undefined", () => {
        assert.equal(calcularEquivalenteProveedor("1000", "1200", undefined, "USD"), null);
    });

    it("ARS → USD: divide por el TC (1 USD = $1200, pago $1200 → cancelo US$1)", () => {
        assert.equal(calcularEquivalenteProveedor("1200", "1200", "ARS", "USD"), 1);
    });

    it("ARS → USD: pago $2400 con TC 1200 → cancelo US$2", () => {
        assert.equal(calcularEquivalenteProveedor("2400", "1200", "ARS", "USD"), 2);
    });

    it("USD → ARS: multiplica por el TC (1 USD = $1200, pago US$1 → cancelo $1200)", () => {
        assert.equal(calcularEquivalenteProveedor("1", "1200", "USD", "ARS"), 1200);
    });

    it("USD → ARS: pago US$2.5 con TC 1000 → cancelo $2500", () => {
        assert.equal(calcularEquivalenteProveedor("2.5", "1000", "USD", "ARS"), 2500);
    });

    it("retorna null para combinaciones de moneda no soportadas", () => {
        // EUR/USD no tiene formula definida
        assert.equal(calcularEquivalenteProveedor("100", "1.1", "EUR", "USD"), null);
    });
});

// ─── construirPayloadPagoProveedor ───────────────────────────────────────────

describe("construirPayloadPagoProveedor", () => {
    // Datos base reutilizables para la mayoria de los tests
    const camposBase = {
        monto: "1000",
        monedaPago: "ARS",
        metodo: "Transfer",
        fecha: "2026-06-27",
        referencia: "",
        notas: "",
        reservaId: null,
        serviceRecordKind: null,
        servicePublicId: null,
        esCruzado: false,
        saldoImputado: "ARS",
        tipoCambio: "",
        fuenteTC: 5,
        fechaTC: "2026-06-27",
        montoEquivalente: null,
    };

    it("pago simple: NO incluye ningun campo de tipo de cambio", () => {
        const payload = construirPayloadPagoProveedor(camposBase);
        // Estos campos NO deben estar en el payload cuando esCruzado=false.
        // Si se enviaran con valores null/undefined el backend los rechaza.
        assert.equal("imputedCurrency" in payload, false);
        assert.equal("exchangeRate" in payload, false);
        assert.equal("exchangeRateSource" in payload, false);
        assert.equal("exchangeRateAt" in payload, false);
        assert.equal("imputedAmount" in payload, false);
    });

    it("pago simple: incluye amount, currency, method y paidAt correctamente tipados", () => {
        const payload = construirPayloadPagoProveedor(camposBase);
        assert.equal(payload.amount, 1000);
        assert.equal(typeof payload.amount, "number");
        assert.equal(payload.currency, "ARS");
        assert.equal(payload.method, "Transfer");
        // paidAt debe ser ISO string (incluye T y Z)
        assert.ok(payload.paidAt.includes("T"), `paidAt debe ser ISO: "${payload.paidAt}"`);
    });

    it("referencia vacia se convierte a null (no manda string vacio al backend)", () => {
        const payload = construirPayloadPagoProveedor({ ...camposBase, referencia: "  " });
        assert.equal(payload.reference, null);
    });

    it("notas vacias se convierten a null", () => {
        const payload = construirPayloadPagoProveedor({ ...camposBase, notas: "  " });
        assert.equal(payload.notes, null);
    });

    it("referencia con contenido se manda como string limpio", () => {
        const payload = construirPayloadPagoProveedor({ ...camposBase, referencia: "  TRF-123  " });
        assert.equal(payload.reference, "TRF-123");
    });

    it("pago simple con reserva y servicio imputados", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            reservaId: "uuid-reserva-123",
            serviceRecordKind: "hotel",
            servicePublicId: "uuid-service-456",
        });
        assert.equal(payload.reservaId, "uuid-reserva-123");
        assert.equal(payload.serviceRecordKind, "hotel");
        assert.equal(payload.servicePublicId, "uuid-service-456");
    });

    it("pago simple sin imputacion: reservaId/serviceRecordKind/servicePublicId son null", () => {
        const payload = construirPayloadPagoProveedor(camposBase);
        assert.equal(payload.reservaId, null);
        assert.equal(payload.serviceRecordKind, null);
        assert.equal(payload.servicePublicId, null);
    });

    it("pago cruzado ARS→USD: SÍ incluye los 5 campos de tipo de cambio", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            monedaPago: "ARS",
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1200",
            fuenteTC: 5,
            fechaTC: "2026-06-27",
            montoEquivalente: 0.833,
        });
        // Los 5 campos deben estar presentes
        assert.equal("imputedCurrency" in payload, true);
        assert.equal("exchangeRate" in payload, true);
        assert.equal("exchangeRateSource" in payload, true);
        assert.equal("exchangeRateAt" in payload, true);
        assert.equal("imputedAmount" in payload, true);
        // Valores correctos
        assert.equal(payload.imputedCurrency, "USD");
        assert.equal(payload.exchangeRate, 1200);
        assert.equal(payload.imputedAmount, 0.833);
    });

    it("pago cruzado: exchangeRateSource es siempre INT aunque el <select> lo devuelva como string", () => {
        // El <select> HTML devuelve strings; el backend espera un int para el enum ExchangeRateSource.
        // construirPayloadPagoProveedor debe convertir con Number().
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1000",
            fuenteTC: "6", // string que viene del <select>
            fechaTC: "2026-06-27",
            montoEquivalente: 0.5,
        });
        assert.strictEqual(typeof payload.exchangeRateSource, "number");
        assert.strictEqual(payload.exchangeRateSource, 6);
    });

    it("pago cruzado: exchangeRateAt es ISO string de fechaTC", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1200",
            fuenteTC: 1,
            fechaTC: "2026-06-15",
            montoEquivalente: 1,
        });
        assert.ok(
            payload.exchangeRateAt.startsWith("2026-06-15"),
            `exchangeRateAt debe arrancar con la fecha ingresada: "${payload.exchangeRateAt}"`
        );
    });

    it("pago cruzado USD→ARS: imputedCurrency es ARS", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            monedaPago: "USD",
            esCruzado: true,
            saldoImputado: "ARS",
            tipoCambio: "1200",
            fuenteTC: 5,
            fechaTC: "2026-06-27",
            montoEquivalente: 1200,
        });
        assert.equal(payload.imputedCurrency, "ARS");
        assert.equal(payload.currency, "USD");
    });

    it("pago cruzado: tambien incluye los campos base (amount, currency, method, etc.)", () => {
        const payload = construirPayloadPagoProveedor({
            ...camposBase,
            monto: "500",
            monedaPago: "ARS",
            metodo: "Cash",
            esCruzado: true,
            saldoImputado: "USD",
            tipoCambio: "1000",
            fuenteTC: 5,
            fechaTC: "2026-06-27",
            montoEquivalente: 0.5,
        });
        // Los campos base siguen presentes incluso en pago cruzado
        assert.equal(payload.amount, 500);
        assert.equal(payload.currency, "ARS");
        assert.equal(payload.method, "Cash");
    });
});

// ─── ordenarBloquesPesosPrimero (Fase D — encabezado de los "dos números") ──

describe("ordenarBloquesPesosPrimero", () => {
    it("pone ARS antes que USD aunque lleguen en el orden contrario", () => {
        const bloques = [{ currency: "USD" }, { currency: "ARS" }];
        const ordenados = ordenarBloquesPesosPrimero(bloques);
        assert.deepEqual(ordenados.map((b) => b.currency), ["ARS", "USD"]);
    });

    it("mantiene el orden si ya viene ARS primero", () => {
        const bloques = [{ currency: "ARS" }, { currency: "USD" }];
        const ordenados = ordenarBloquesPesosPrimero(bloques);
        assert.deepEqual(ordenados.map((b) => b.currency), ["ARS", "USD"]);
    });

    it("con una sola moneda no rompe nada", () => {
        assert.deepEqual(
            ordenarBloquesPesosPrimero([{ currency: "USD" }]).map((b) => b.currency),
            ["USD"]
        );
    });

    it("array vacio retorna array vacio", () => {
        assert.deepEqual(ordenarBloquesPesosPrimero([]), []);
    });

    it("null/undefined no lanzan, retornan array vacio", () => {
        assert.deepEqual(ordenarBloquesPesosPrimero(null), []);
        assert.deepEqual(ordenarBloquesPesosPrimero(undefined), []);
    });

    it("no muta el array original", () => {
        const original = [{ currency: "USD" }, { currency: "ARS" }];
        const copia = JSON.stringify(original);
        ordenarBloquesPesosPrimero(original);
        assert.equal(JSON.stringify(original), copia);
    });

    it("una moneda desconocida (ej. EUR) siempre queda antes que USD", () => {
        const bloques = [{ currency: "USD" }, { currency: "EUR" }];
        const ordenados = ordenarBloquesPesosPrimero(bloques);
        assert.equal(ordenados[0].currency, "EUR");
        assert.equal(ordenados[1].currency, "USD");
    });
});

// ─── debeMostrarseEnGrisNeutro (Fase D — color de los recuadros del encabezado) ──

describe("debeMostrarseEnGrisNeutro", () => {
    it("sin permiso de ver costos, siempre gris aunque el monto sea grande", () => {
        assert.equal(debeMostrarseEnGrisNeutro(1250000, false), true);
    });

    it("sin permiso de ver costos, gris incluso con monto 0", () => {
        assert.equal(debeMostrarseEnGrisNeutro(0, false), true);
    });

    it("con permiso y monto positivo, NO va en gris (usa su color propio)", () => {
        assert.equal(debeMostrarseEnGrisNeutro(1250000, true), false);
    });

    it("con permiso y monto exactamente 0, va en gris", () => {
        assert.equal(debeMostrarseEnGrisNeutro(0, true), true);
    });

    it("con permiso y monto null/undefined, se trata como 0 -> gris", () => {
        assert.equal(debeMostrarseEnGrisNeutro(null, true), true);
        assert.equal(debeMostrarseEnGrisNeutro(undefined, true), true);
    });

    it("con permiso y un resto de redondeo menor a medio centavo, se trata como cero", () => {
        assert.equal(debeMostrarseEnGrisNeutro(0.001, true), true);
    });

    it("con permiso y un monto negativo distinto de cero, NO va en gris", () => {
        // No debería pasar en la práctica (los tres campos son siempre >= 0), pero la función
        // no debe asumir signo: solo mira si el valor absoluto es ~0.
        assert.equal(debeMostrarseEnGrisNeutro(-5, true), false);
    });
});

// ─── aplanarReembolsosPendientesPorMoneda (§4 — selector "Registrar reembolso recibido") ──

describe("aplanarReembolsosPendientesPorMoneda", () => {
    it("retorna array vacío si el argumento es null, undefined o no-array", () => {
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda(null), []);
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda(undefined), []);
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda("no-es-array"), []);
    });

    it("retorna array vacío si items es un array vacío", () => {
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda([]), []);
    });

    it("una cancelación con UNA moneda estimada genera UNA fila", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-1",
                numeroReserva: "R-1050",
                clienteNombre: "Juan Pérez",
                amountsMasked: false,
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 80000 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas.length, 1);
        assert.equal(filas[0].key, "bc-1-ARS");
        assert.equal(filas[0].bookingCancellationPublicId, "bc-1");
        assert.equal(filas[0].numeroReserva, "R-1050");
        assert.equal(filas[0].clienteNombre, "Juan Pérez");
        assert.equal(filas[0].currency, "ARS");
        assert.equal(filas[0].estimatedAmount, 80000);
        assert.equal(filas[0].amountsMasked, false);
    });

    it("una cancelación con DOS monedas estimadas genera DOS filas separadas", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-2",
                numeroReserva: "R-2000",
                clienteNombre: "María López",
                amountsMasked: false,
                estimatedRefundsByCurrency: [
                    { currency: "ARS", estimatedAmount: 50000 },
                    { currency: "USD", estimatedAmount: 300 },
                ],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas.length, 2);
        assert.deepEqual(filas.map((f) => f.key), ["bc-2-ARS", "bc-2-USD"]);
        assert.equal(filas[0].currency, "ARS");
        assert.equal(filas[1].currency, "USD");
    });

    it("varias cancelaciones producen filas independientes, cada una con su propia key", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-1",
                numeroReserva: "R-1",
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 1000 }],
            },
            {
                bookingCancellationPublicId: "bc-2",
                numeroReserva: "R-2",
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 2000 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas.length, 2);
        assert.notEqual(filas[0].key, filas[1].key);
    });

    it("una cancelación sin estimatedRefundsByCurrency (undefined) no genera filas", () => {
        const items = [{ bookingCancellationPublicId: "bc-3", numeroReserva: "R-3" }];
        assert.deepEqual(aplanarReembolsosPendientesPorMoneda(items), []);
    });

    it("respeta amountsMasked=true por fila (viene del item completo, no de la moneda)", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-4",
                numeroReserva: "R-4",
                amountsMasked: true,
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 0 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas[0].amountsMasked, true);
        assert.equal(filas[0].estimatedAmount, 0);
    });

    it("usa cadena vacía como fallback cuando numeroReserva/clienteNombre son null", () => {
        const items = [
            {
                bookingCancellationPublicId: "bc-5",
                numeroReserva: null,
                clienteNombre: null,
                estimatedRefundsByCurrency: [{ currency: "ARS", estimatedAmount: 100 }],
            },
        ];
        const filas = aplanarReembolsosPendientesPorMoneda(items);
        assert.equal(filas[0].numeroReserva, "");
        assert.equal(filas[0].clienteNombre, "");
    });
});

// ─── validarFormularioReembolsoRecibido (§4 — validación local antes de llamar al backend) ──

describe("validarFormularioReembolsoRecibido", () => {
    const filaValida = {
        key: "bc-1-ARS",
        bookingCancellationPublicId: "bc-1",
        currency: "ARS",
        estimatedAmount: 80000,
        amountsMasked: false,
    };

    it("exige elegir un reembolso pendiente (filaSeleccionada null)", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: null,
            monto: "1000",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /Elegí a qué reembolso pendiente/);
    });

    it("rechaza monto vacío", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /mayor a 0/);
    });

    it("rechaza monto 0 o negativo", () => {
        assert.match(
            validarFormularioReembolsoRecibido({ filaSeleccionada: filaValida, monto: "0", fecha: "2026-07-01" }),
            /mayor a 0/
        );
        assert.match(
            validarFormularioReembolsoRecibido({ filaSeleccionada: filaValida, monto: "-50", fecha: "2026-07-01" }),
            /mayor a 0/
        );
    });

    it("rechaza monto no numérico", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "abc",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /mayor a 0/);
    });

    it("rechaza un monto mayor al estimado cuando el estimado es conocido", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "90000",
            fecha: "2026-07-01",
        });
        assert.match(mensaje, /no puede superar el estimado/);
    });

    it("acepta un monto igual al estimado (límite exacto)", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "80000",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });

    it("NO compara contra el estimado cuando amountsMasked=true (evita error falso)", () => {
        const filaEnmascarada = { ...filaValida, amountsMasked: true, estimatedAmount: 0 };
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaEnmascarada,
            monto: "999999",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });

    it("exige fecha", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "1000",
            fecha: "",
        });
        assert.match(mensaje, /fecha es obligatoria/);
    });

    it("formulario completo y válido no genera error", () => {
        const mensaje = validarFormularioReembolsoRecibido({
            filaSeleccionada: filaValida,
            monto: "50000",
            fecha: "2026-07-01",
        });
        assert.equal(mensaje, null);
    });
});
