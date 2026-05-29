# 🗺️ ROADMAP MAESTRO — MagnaTravel-Cloud (2026-05-29)

> Documento de orientación "del total del total". Nivel trainee, criollo.
> Para cuando estás mareado con las fases: **acá está todo, dónde estamos y hacia dónde vamos.**
> HEAD al momento de escribir: `72df64e`.

---

## 0. Qué es esto, en una línea

Un **ERP para agencias de viajes minoristas** (reservas, clientes, proveedores, pagos, facturación AFIP/ARCA, cancelaciones). Se **vende como producto**: **una instalación por cliente** (no es un sistema con muchas empresas adentro). La misma base de código tiene que funcionar sea el cliente **Monotributo o Responsable Inscripto**.

---

## 1. DE DÓNDE VENIMOS (lo que ya está hecho)

Pensalo como una casa que se fue construyendo por pisos. Estos pisos ya están:

### 🏗️ Piso 1 — La base del ERP (cimientos)
Migración del sistema viejo a un ERP retail. Reservas, clientes, proveedores, pagos, usuarios. **Hecho y en uso.**
> Estado: ✅ vivo en producción.

### 🔄 Piso 2 — Ciclo de vida de reservas
Estados de las reservas (presupuesto → confirmada → viajando → cerrada → cancelada), chips visuales, job que las hace avanzar solas. **Hecho.**
> Estado: ✅ vivo.
> Nota: hoy **solo Hotel está 100% operativo**. Vuelo / Paquete / Traslado / Asistencia están parciales ("no soportados" en algunos flujos nuevos).

### 🔐 Piso 3 — B1.15: refactor de permisos y aprobaciones (auth)
Sistema de roles + permisos finos (ej. `cobranzas.receipt_void`, `approvals.review`), y el **workflow de "cuatro ojos"** (cuando un vendedor quiere hacer algo sensible, lo aprueba un admin). Bandeja de aprobaciones. Guards que bloquean mutaciones indebidas.
> Estado: ✅ mayormente implementado y en uso (lo usamos esta misma sesión).
> Pendiente dentro de este piso: la **"Fase IMP" (sistema impositivo según condición fiscal Mono/RI)** quedó apuntada pero NO terminada → ver sección "lo que falta".

### ❌ Piso 4 — FC1.1 + FC1.2: módulo de cancelaciones y devoluciones
Cancelar una reserva, calcular cuánto se devuelve, manejar la devolución del operador, la cuenta corriente del cliente (saldo a favor), retiros (efectivo con tope Ley 25.345, transferencia, etc.), y emitir la **Nota de Crédito TOTAL** real al ARCA.
> Estado: ✅ código cerrado, ~94+ tests. **Llave apagada** (`EnableNewCancellationFlow`) → pendiente de firma de contador para prender.

### 🧾 Piso 5 — FC1.3: Nota de Crédito PARCIAL (lo más grande de las últimas semanas)
Cuando se cancela **solo una parte** de una venta, emitir una NC por esa parte (no anular todo). Tres fases:
- **Fase 1** ✅ — el "calculador fiscal" que decide cuánto acreditar + puente con el workflow de aprobación + 2 trabajos automáticos + migraciones. (66 tests)
- **Fase 2 (F2.0 → F2.6a)** ✅ — emisión REAL de la NC parcial al ARCA: configuración + llave maestra, liquidación fiscal congelada, tubería de emisión, conexión con cancelaciones, **soporte multimoneda en la NC**, trabajos de reconciliación de NC colgadas, blindaje de idempotencia. **Deployada en el VPS, llave apagada.**
- **Fase 3** ✅ (ESTA SESIÓN) — la **bandeja de reconciliación**: cuando se emite una NC parcial, los recibos de pago viejos quedan "vivos"; esta pantalla deja a un encargado verlos y acomodarlos a mano. Backend + frontend + tests de integración.
> Estado: ✅ código completo (Fase 1+2+3). **Llave apagada** → el sistema sigue emitiendo NC TOTAL como siempre. **Cero riesgo fiscal en producción hoy.**

### 🔎 Piso 6 — Auditoría fiscal (ESTA SESIÓN, importante)
Descubrimos que el "criterio del contador" sobre el que se construyó todo lo fiscal **era en realidad ChatGPT**, no un matriculado. Lo **verificamos con acceso a internet** contra fuentes oficiales de ARCA. Resultado:
- El esqueleto está mayormente bien (RG 4540 real, NC parcial legal, modelo intermediario/reseller correcto).
- **1 error grave** (caso 3: penalidades que se queda la agencia → probablemente hay que facturarlas, no solo "no acreditarlas").
- **1 cita falsa** en el código ("regla AFIP INV-118" que no existe) → **corregida**.
- El **tipo de cambio correcto** para facturar en USD es **dólar divisa vendedor del Banco Nación del día hábil anterior** (RG 5616/2024) — y hoy el sistema usaría uno equivocado.
> Estado: ✅ auditoría hecha, correcciones seguras aplicadas. Lo gordo (penalidades + TC) quedó documentado → ver "lo que falta".
> Bonus: les dimos **internet a los subagentes fiscales** (contador, impuestos, contable, dominio) + al arquitecto, para que verifiquen fuentes en vez de adivinar.

---

## 2. DÓNDE ESTAMOS HOY (👉 estás acá)

- **Rama `main`, HEAD `72df64e`**, todo pusheado.
- **Producción**: factura **solo en pesos**, emite **NC TOTAL**, todas las llaves de lo nuevo **apagadas**. Estable, sin riesgo.
- **FC1.3 (NC parcial)**: código 100% completo y revisado, esperando 3 cosas humanas antes de prenderse (firma contador + homologación ARCA + decisión de prender).
- **Pendiente operativo inmediato**: reconfirmar que la batería de tests del VPS quede en 202/202 (arreglamos 1 test que estaba mal armado) + aplicar 2 migraciones aditivas en el VPS (Fase2_M2 + Fase3_M1).

---

## 3. LO QUE FALTA (separado en "lo que puedo hacer yo" vs "lo que necesita un humano")

### 🚧 A) Construible (código) — en orden de prioridad sugerido

1. **🌎 MULTIMONEDA DE PUNTA A PUNTA (el gran bloque nuevo que pediste hoy)**
   Que el sistema deje **facturar en dólares o en pesos**, que las **NC/ND hereden solas la moneda** de la factura que ajustan, **tarifario con precios en dólares**, y que tome el **tipo de cambio correcto** (ADR-011: dólar divisa vendedor BNA día hábil anterior, congelado en la factura). Tiene que funcionar igual en Mono y RI.
   - Hoy: **NO está enchufado** (la pantalla de crear factura no deja elegir moneda; sale siempre en pesos). El backend tiene los campos pero el frontend no los usa.
   - Es una función grande: tarifario → factura → NC/ND → tipo de cambio → contabilidad.
   - Sub-pieza ya diseñada: **ADR-011** (fuente confiable del tipo de cambio).

2. **🧮 Sistema impositivo Mono/RI robusto (la "Fase IMP")**
   El sistema **ya elige Factura A/B/C y discrimina IVA** según la condición fiscal — pero la condición es un **texto libre** (si se escribe mal, se rompe), **no hay pantalla** para cambiarla, **no hay tests** de los dos casos, y la **transición Mono→RI** no está bien manejada.
   - Falta: convertir a valor controlado + pantalla de admin + tests matriz Mono×RI + historización del cambio.

3. **💸 TEMA 1 — penalidades retenidas (depende de si sos RI)**
   Si la agencia se queda un fee por la gestión, probablemente haya que **facturarlo** (no solo netearlo en la NC). Necesita un dato nuevo (por qué se retiene cada cosa) para decidir bien.
   - **Urgencia depende de la condición fiscal**: si Monotributo, casi no importa; si RI, importante.

4. **🧹 Follow-ups menores**: doc trainee F2.7, afinar un test de redondeo multi-alícuota, etiqueta más específica en un modal, etc. (no bloquean nada).

### ✋ B) Necesita un humano (no lo puedo hacer yo)

1. **Confirmar la condición fiscal real HOY** (Monotributo o Responsable Inscripto). El código viene en RI por defecto; la memoria vieja decía Mono. **Solo vos lo sabés.** Destraba el TEMA 1.
2. **Firma de un contador matriculado de verdad** (NO ChatGPT) para: el caso 3 (penalidades), el tipo de cambio de la NC, la matriz completa de casos, y el umbral de efectivo (Ley 25.345).
3. **Homologación con ARCA**: emitir una NC parcial real (multi-alícuota y en USD) en el ambiente de prueba de ARCA y que vuelva aprobada con su número (CAE).
4. **Aplicar migraciones + prender llaves**: decisión operativa, cuando 1-2-3 estén OK.

---

## 4. HACIA DÓNDE VAMOS (orden propuesto)

> Esto es una propuesta de orden; vos decidís.

1. **Cerrar limpio FC1.3** (rápido): reconfirmar tests VPS 202/202 + aplicar las 2 migraciones aditivas. Deja la casa ordenada.
2. **Definir la condición fiscal** (Mono/RI). Una respuesta tuya. Destraba el TEMA 1 y aclara cómo facturar.
3. **MULTIMONEDA DE PUNTA A PUNTA** (el gran bloque): diseñarlo bien primero (un solo diseño que una tarifario + factura + NC/ND + tipo de cambio + Mono/RI), después construirlo por fases con la llave apagada hasta homologar. Acá entra el ADR-011.
4. **En paralelo, gestión humana**: conseguir un contador matriculado para las firmas + arrancar la homologación ARCA.
5. **Prender las llaves** (NC parcial + multimoneda) recién cuando estén las firmas y la homologación.

---

## 5. Llaves (feature flags) — estado actual

| Llave | Qué prende | Estado |
|---|---|---|
| `EnableNewCancellationFlow` | Módulo cancelación/refund FC1.2 | 🔴 OFF |
| `EnablePartialCreditNoteRealEmission` | Emisión real de NC parcial (FC1.3) | 🔴 OFF |
| (futura) multimoneda factura USD | Facturar en dólares | 🔵 no existe aún |
| (futura) condición fiscal configurable UI | Cambiar Mono↔RI desde pantalla | 🔵 no existe aún |

---

## 6. Deudas conocidas / cosas a no olvidar

- **Migraciones sin aplicar en VPS**: `Fase2_M2` (idempotency key) + `Fase3_M1` (bandeja reconciliación). Aditivas, seguras.
- **El "criterio contador" histórico = ChatGPT**: tratar los docs de criterio fiscal (2026-05-19, 2026-05-21) como hipótesis a validar, no como verdad firmada.
- **Tipo de cambio del dashboard** = scraping frágil de la web del BNA + es el dólar "billete" (probablemente el tipo equivocado para fiscal, que es "divisa").
- **Solo Hotel 100% operativo**; otros servicios parciales.
- **Tests de integración solo corren en el VPS** (no hay Postgres local).
