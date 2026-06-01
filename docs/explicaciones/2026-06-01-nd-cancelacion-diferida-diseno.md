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

## En qué quedó

- Backend de captura: **terminado, revisado y committeado** (`ee22e57`). Flag apagado, producción intacta.
- Diseño del flujo diferido: **ADR-014 listo para construir**.
- Lo que sigue (próxima sesión): programar el backend diferido, después el frontend completo de
  cancelación, y los tests.

## Lo que necesitamos de vos (no urgente, pero destraba "prender" la feature)
- Firma del contador matriculado sobre el esquema diferido.
- Homologación en ARCA de la Nota de Débito.
- Tu decisión sobre el saldo (el punto B2 de arriba).
