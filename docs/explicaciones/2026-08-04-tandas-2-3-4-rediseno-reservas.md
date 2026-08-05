# 2026-08-04 — Tandas 2, 3 y 4: la ficha de la reserva entera habla el idioma de la maqueta

## Qué pasó hoy, en criollo

Ayer quedó deployado el listado (Tanda 1). Hoy se terminó TODO lo que faltaba del rediseño
que Gaston firmó el 03/08: el encabezado de la ficha (Tanda 2), los formularios y flujos
(Tanda 3) y el interior de las tres solapas que seguían con la cara vieja (Tanda 4).
En el medio, Gaston miró PROD dos veces y tuvo razón dos veces: el listado no era la maqueta
(se realineó) y la tabla de servicios estaba desalineada y a medias (se rehízo). Lección
repetida y anotada: ninguna tanda visual se reporta terminada sin que YO la mire en el
navegador contra PROD antes.

## Lo que se ve distinto (por tanda)

**Tanda 2 — el encabezado de la ficha** (`96fd2688`)
- El estado va pegado al título ("Reserva #F-2026-1064 [CONFIRMADA 🔒]").
- Abajo el cliente, el destino y los pasajeros — murió el nombre inventado "File F-2026-...".
- "Pago: … · Factura: …" en su propio renglón; las fechas con sus botones al lado.
- Un solo número grande (el que tiene plata); recaudado e inversión chiquitos. Murió el
  puntito rojo que latía.
- Los avisos: ámbar solo si te piden hacer algo (con su botón); informativos en gris de una
  línea; "Reserva anulada — solo lectura" en rojo.
- Las acciones raras (Volver atrás / Destrabar / Sacar de viaje) detrás del menú "⋯".
  Decisión nueva de Gaston: en una Anulada, ese menú conserva "Deshacer anulación".
- Murió el botón "Eliminar reserva" (nada se borra).

**Tanda 3 — formularios y flujo** (`86da5a14`)
- "Nuevo Presupuesto" ya no abre ventana: fila inline con buscador de cliente y "crear
  cliente nuevo" ahí mismo.
- La solapa Pasajeros existe desde Presupuesto; "falta el titular" ahora es un enlace que
  te lleva a cargarlo; las cantidades se guardan solas.
- Vouchers + Documentos = una sola solapa "Documentos" (la ficha queda con 5 solapas).

**Tanda 4 — el interior de las tres solapas** (`c75cb3cb`)
- **Historial**: línea de tiempo por día con frases de agencia ("Maite cobró $ 150.000,00",
  "La reserva pasó de En gestión a Confirmada") — murió el volcado técnico campo por campo.
  La plata que sale va con su palabra: "Se descontó un cobro de $ X.", nunca un signo menos.
- **Estado de Cuenta**: extracto por moneda (pesos y dólares JAMÁS mezclados) con
  Debe/Haber/Saldo, total al pie y frase de cierre en criollo.
- **Pasajeros**: cantidades arriba, "N de M nombres cargados", botones Editar/Borrar con la
  palabra visible.
- De paso se arregló un error viejo: una reserva viva con sobrepago decía "Saldo a Cobrar
  -$ 5.000" — ahora dice "Saldo a favor del cliente" en verde, también por moneda.

## Dos desvíos de la maqueta hechos a propósito (Gaston valida)

1. El extracto conservó su columna de acciones (Ver factura / WhatsApp / recibos): la maqueta
   lo dibuja "solo para mirar", pero es la única puerta a esas acciones desde la ficha.
2. "Costo y margen" quedó en Estado de Cuenta aunque la maqueta no lo dibuja: el margen no
   vive en ningún otro lado. Sigue oculto para quien no tiene permiso de costos.

## Cómo se verificó

- Cada tanda: suite front completa verde (cerró en 3153) + reviewers (bloqueantes corregidos
  y re-confirmados: 1 en T2, 4 en T3, 2+1 en T4) + gate de exposición de datos PASA en las tres.
- CI verde y deploy por tanda; capturas reales en PROD tomadas por mí y comparadas contra la
  maqueta ANTES de reportar (listado, ficha Confirmada, ficha Anulada, alta inline, Documentos,
  Historial, Estado de Cuenta, Pasajeros).

## Pendientes anotados (necesitan motor; próximas obras)

- Botón "Marcar emitido" en fila de servicio Confirmada (hoy el mecanismo existe solo En gestión).
- Historial: el motivo de la anulación y el nombre del servicio no viajan en los eventos.
- Extracto: las facturas anuladas no traen su marca para la fila tachada con auditoría.
- Menor: el resumen del listado materializa en memoria las reservas legacy sin desglose de
  moneda (escala mal si algún día son miles).
