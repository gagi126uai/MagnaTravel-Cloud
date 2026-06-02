# Nota de Débito en cancelación: cierre del backend de captura + diseño del flujo diferido

**Fecha:** 2026-06-01 (sesión continuación)
**Nivel:** explicación para entender qué se decidió y por qué, sin tecnicismos profundos.

## El punto de partida

Veníamos con la Nota de Débito (ND) por penalidad de cancelación "a media feature". La idea era:
cuando se cancela una reserva y la agencia se queda con un cargo propio (no plata del operador),
hay que emitirle al cliente una **Nota de Débito** además de la Nota de Crédito por la reserva.

Lo que creíamos que faltaba: "una pantalla de captura". La realidad resultó ser bastante más grande.

## Lo que hicimos hoy

### 1. Cerramos el backend de captura (lo que ya estaba a medias)

Había un trabajo committeado "sin revisar". Lo pasamos por dos revisores (backend y seguridad) y
salió **"Changes Required"**: cuatro cosas a corregir. Las arreglamos y las dejamos committeadas
(`ee22e57`), con los tests en verde (47/47). Lo más importante de esos arreglos:

- **El flag apagado tiene que dejar todo igual que antes.** Se había colado un caso donde, con la
  feature apagada, el sistema igual modificaba datos y podía romper el flujo de reembolsos. Lo
  cerramos: con el flag apagado, no toca nada.
- **No se puede emitir una ND sin saber quién la clasificó y confirmó.** Ahora es obligatorio dejar
  ese rastro antes de emitir.
- **Un vendedor sin permiso ya no rompe una cancelación.** Antes, si el operador estaba configurado
  de cierta forma, un vendedor sin permiso quedaba trabado. Ahora la cancelación sigue su curso
  (simplemente no emite la ND, queda para que alguien con permiso la resuelva).

### 2. El descubrimiento grande

Al ir a construir "la pantalla", encontramos dos cosas que cambian el panorama:

- **El frontend de cancelación no existe.** Toda la maquinaria de cancelar una reserva (con su Nota
  de Crédito) está hecha en el backend, pero **nunca se conectó a la interfaz**. Hoy no se puede
  cancelar una reserva desde el sistema; nunca se usó desde la pantalla.
- **El caso real más común no está cubierto.** Vos confirmaste que, cuando hay un cargo propio de la
  agencia, **el operador suele confirmar el monto días o semanas después** de la cancelación. El
  sistema hoy solo sabe emitir la ND si el monto ya está confirmado *en el mismo momento* de cancelar.
  No hay forma de decir "ahora sí el operador confirmó, emití la ND".

Con eso, "terminar la ND" pasó de ser una pantallita a ser **dos piezas grandes**: un paso nuevo en el
backend (la confirmación diferida) y todo el frontend de cancelación. Elegiste construir las dos.

### 3. El diseño del flujo diferido (ADR-014)

Antes de programar nada, diseñamos. Pasó por tres miradas:

- **Negocio:** cómo lo vive el agente de mostrador (qué decide, qué le sugiere el sistema, qué ve).
- **Fiscal (contador):** confirmó que emitir la ND días después es correcto, con reglas claras —la ND
  lleva la fecha del día real en que se emite, se asocia a la factura original, y sale directo por el
  monto confirmado (no hay que corregir nada del estimado porque nunca se emitió sobre el estimado).
- **Arquitectura (ADR-014):** el diseño técnico, que un revisor desafió **dos veces** hasta dejarlo
  sólido. La idea es simple y reusa lo que ya existe: un botón nuevo "confirmar la penalidad" que, el
  día que el operador confirma, dispara la misma maquinaria de emisión que ya teníamos.

Dos cosas que el revisor obligó a resolver:
- **No emitir dos ND por error** si alguien reintenta tras un choque de concurrencia. Se resolvió
  marcando "confirmado" *antes* de crear la ND, de modo que un reintento rebota solo.
- **Qué pasa con el saldo** cuando se emite la ND sobre una reserva ya cerrada. Quedó propuesto que la
  ND es **solo un comprobante y no toca el saldo** (es lo coherente con cómo funciona hoy el balance),
  **pero esto lo tenés que validar vos**: ¿querés que ese cargo le quede al cliente como deuda en
  cuenta corriente, o alcanza con emitir el comprobante y cobrarlo por fuera?

## En qué quedó (actualización 2026-06-02: la feature se construyó ENTERA en esta sesión)

Lo que arrancó como "diseño" terminó siendo la feature completa de punta a punta. La cancelación con
Nota de Débito **ya está construida, revisada y en `main`**, toda detrás del flag apagado (producción
intacta). En orden, lo que se hizo:

1. **Backend de captura** (`ee22e57`): los 4 fixes del review, revisado, tests verdes.
2. **ADR-014** (`debf95b`): el diseño del flujo diferido, desafiado dos veces.
3. **Backend del flujo diferido** (`dc2b998`): el botón "confirmar la penalidad después" que emite la
   Nota de Débito cuando el operador confirma el monto días más tarde. Revisado por dos revisores.
4. **Conexión backend↔frontend** (`2c07646`): se descubrió que el sistema no le "contaba" a la pantalla
   que la feature estaba prendida ni en qué estado estaba el cargo. Se conectó eso.
5. **Pantalla de cancelación** (`5a20c7a`): no existía nada en la interfaz. Ahora el agente puede cancelar
   una reserva, elegir si hay penalidad y de quién es, y —el caso más común— dejar el monto como estimado
   y confirmarlo más tarde desde una bandeja. Revisada dos veces.
6. **Tests** (`2013d96`): batería de pruebas del flujo (se corren en el servidor).

**El caso real más común quedó cubierto**: cancelás con monto estimado → la cancelación aparece en una
bandeja "pendiente de confirmar monto" → cuando el operador confirma, lo cargás y recién ahí se emite la
Nota de Débito.

## Lo que queda (todo opcional; elegiste cerrar acá)
- **Validarlo en el servidor**: correr los tests con Docker y hacer una prueba real con el flag prendido
  (nunca se probó encendido).
- **Deuda fiscal (M2)**: hoy la pantalla asume que el cliente es "Consumidor Final" y el proveedor
  "Responsable Inscripto". Para casos distintos, conviene que el sistema lo tome del dato real. Idealmente
  lo valida el contador.
- **Endurecimientos** menores antes de prender el flag (P1/P2) y una mejora de UX (ver el cargo pendiente
  desde la ficha de la reserva, no solo desde la bandeja).

## Lo que necesitamos de vos (destraba "prender" la feature en producción)
- Firma del contador matriculado sobre el esquema diferido.
- Homologación en ARCA de la Nota de Débito.
- Tu decisión sobre el saldo (¿la Nota de Débito le queda al cliente como deuda en cuenta corriente, o
  solo se emite el comprobante y se cobra por fuera?).
