# Visión de producto — Rediseño de la cancelación de reservas

**Fecha:** 2026-06-02
**Origen:** parate pedido por el dueño (Gaston). La experiencia de cancelación se sentía "rara y desvirtuada": demasiado pegada a lo fiscal, con decisiones de contador en la cara del vendedor.

---

## El problema

La función de cancelación se construyó **de adentro hacia afuera, desde lo fiscal (ARCA)**: notas de crédito, notas de débito, "snapshots fiscales", clasificación de penalidad (propia vs pass-through), bandejas de revisión. La **experiencia del usuario quedó como un subproducto** de esa plomería:

- El vendedor tiene que entender y decidir cosas de contador.
- Modal de 2 pasos + bandeja separada + flags por distintos lados.
- Lo fiscal, que debería pasar solo por debajo, está expuesto en el camino del día a día.

En palabras del dueño: *"que se cancele y no tener todo ese quilombo"*.

---

## La visión: tres capas

```
┌─ MOSTRADOR (todos, todo el día) ──────────────────┐
│  Cancelar reserva → confirmar → listo.             │
│  Cero contador. Cero decisiones fiscales.          │
└────────────────────────────────────────────────────┘
                       │
                       ▼
┌─ LA IA (el motor, invisible) ─────────────────────┐
│  Mira el contexto de la cancelación y clasifica el │
│  evento fiscal: nota de crédito, si hay penalidad  │
│  y de quién es. RESUELVE SOLA LOS CASOS CLAROS.    │
│  Escala a supervisión SOLO lo dudoso.              │
└────────────────────────────────────────────────────┘
                       │ (solo lo dudoso)
                       ▼
┌─ SUPERVISIÓN (configurable por cada cliente) ─────┐
│  Revisa y aprueba solo lo que la IA marcó dudoso.  │
│  Acá viven las penalidades, las notas de débito y  │
│  los comprobantes. Ordenado, no un trámite diario. │
└────────────────────────────────────────────────────┘
```

---

## Principios

1. **Simple por defecto.** Cancelar es apretar un botón, confirmar y listo. El camino feliz no tiene fricción fiscal.
2. **Lo fiscal es obligatorio y se mantiene** — pero **invisible** en el día a día. ARCA exige la nota de crédito; el sistema la hace solo.
3. **La IA automatiza la clasificación fiscal**: resuelve los casos claros, escala solo los dudosos. Es el diferencial del producto.
4. **Todo configurable por cliente.** Es un producto vendible: cada agencia decide quién supervisa, qué se auto-resuelve, qué requiere aprobación. No está hardcodeado para una agencia.
5. **Supervisión humana como red de seguridad.** La IA propone y resuelve, pero existe la vía de supervisión para cuando se necesita control. Para producción, los criterios fiscales se validan con un contador. **La IA no reemplaza la responsabilidad fiscal.**

---

## Qué se reusa (no se tira nada)

Todo el backend fiscal ya construido pasa a ser **el motor de la capa 2**:

- Nota de crédito total (flujo de cancelación nuevo).
- Nota de crédito parcial (ADR-009).
- Nota de débito por penalidad (ADR-013/014).
- Clasificación fiscal del evento, gating, snapshots.

Lo que cambia: **el usuario ya no lo toca**, la **IA decide** lo que hoy le preguntábamos al vendedor, y la **supervisión configurable** se queda solo con los casos dudosos.

---

## Qué NO es

- **No** es tirar lo fiscal (es necesario y se mantiene).
- **No** es una IA autónoma sin control (siempre hay vía de supervisión).
- **No** es para una agencia puntual (es producto multi-cliente, configurable).

---

## De dónde venimos (estado al 2026-06-02)

- **Nota de débito en cancelación**: construida, revisada, arreglada y deployada (homologación). Queda como parte del motor.
- **Bug de cancelación que ignoraba servicios tipados** (hoteles/vuelos): Fase 1 arreglada (commit `464339c`) — desbloquea cancelar reservas de un operador; bloquea multi-operador con aviso claro.

Estas piezas son **el motor**. La capa de experiencia simple + IA + supervisión configurable es el **rediseño nuevo** que arranca desde esta visión.

---

## Próximos pasos (todavía SIN construir)

1. **Diseño de experiencia** (especialista UX para agencias retail): cómo se ve el "cancelar simple" del mostrador y el panel de supervisión.
2. **Diseño técnico** (arquitecto): cómo encaja la IA (clasificador del evento fiscal), el umbral de confianza "claro vs dudoso", y la configuración por cliente.
3. **Dominio + fiscal** (dominio agencia + contador): qué casos son fiscalmente "claros" (auto-resuelve la IA) vs "dudosos" (van a supervisión).
4. **Recién después**: construir, por etapas, detrás de flags.
