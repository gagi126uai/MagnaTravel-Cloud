# Auditoría de diseño de la sección RESERVAS — veredicto elemento por elemento

**Fecha:** 2026-08-03 · **Autor:** `ux-ui-disenador` · **Estado:** propuesta — espera las respuestas de Gastón
**Maqueta que la acompaña:** `docs/ux/maquetas/2026-08-03-reservas-rediseno.html`
**Textos rotos:** NO se tratan acá. Van en el inventario paralelo `docs/ux/2026-08-03-inventario-textos-reservas.md`
(otro agente). Cuando en esta auditoría un elemento falla SOLO por cómo está escrito, se marca
**CAMBIA (texto)** y se deriva a ese inventario, sin duplicar el trabajo.

---

## 0. De dónde sale cada veredicto

Frase del dueño que ordena toda la obra (firmada hoy):

> *"me gusta lo actual pero a la vez no me cierra"* → **se conservan los huesos, se rediseña la piel y la voz.**

**Los huesos que NO se tocan** (ya firmados, no se reabren — PR-10):

- El modelo de estados y sus etapas (Cotización → Presupuesto → En gestión → Confirmada 🔒 → En viaje → Finalizada; Anulada / Perdida / Archivada aparte). *(guía, ciclo de vida 2026-06-07/08)*
- El candado de la Confirmada con su explicación y su "Destrabar". *(guía #1 y #2 del 2026-06-08; P-19)*
- La reversibilidad "Volver atrás" como capacidad. *(guía 2026-06-08)*
- La auditoría a la vista: "Anulado por X el fecha", servicio tachado con motivo/quién/cuándo. *(F-6; guía #9 2026-06-08 y 2026-06-13)*
- Los chips de estado, y el criterio de tres ejes (Estado operativo grande / Pago: / Factura:). *(ADR-035 A-quinque; guía 2026-06-22 punto 2; ADR-037 punto 1)*
- Las fichas de trabajo EN LÍNEA (servicio, cobro, anulación, pasajero). *(P-5)*

**Reglas que uso para juzgar:** la guía `docs/ux/guia-ux-gaston.md` y los números de la constitución
(`docs/estandares/2026-07-22-constitucion-producto-v1.md`). Donde no hay regla, **no decido**: va al
bloque de PREGUNTAS del final.

**Cómo leer los veredictos:**

| Veredicto | Qué significa |
|---|---|
| **QUEDA** | Está bien y respaldado por una regla firmada. No se toca. |
| **CAMBIA** | Rompe una regla firmada, o es un defecto objetivo (se pisa, miente, no tiene salida). Se corrige y digo cómo. |
| **CAMBIA (texto)** | El problema es la redacción. Va al inventario de textos, no acá. |
| **PREGUNTA Pn** | Ninguna regla lo cubre. NO lo decido: está en el bloque final con opciones + una recomendación. |

**Cambios ya firmados hoy por Gastón que esta auditoría incorpora como QUEDA-nuevo:**

1. "Anular varios" lista **solo servicios confirmados**.
2. El botón "Anular varios" **solo aparece con 2 o más anulables**.
3. El buscador del listado busca en **TODO**, ignorando el filtro de mes.
4. Se **unifica** "EN ESPERA" / "SOLICITADO" en una sola etiqueta (qué palabra queda → **P13**).
5. **NADA se borra: ni reservas ni presupuestos.** Regla del dueño, textual (2026-08-03):
   *"las reservas no se borran, se anulan; nada importante se borra"*. El verbo es **Anular** y el
   registro queda con su rastro. **El botón "Eliminar" que hoy está en la ficha de un Presupuesto es un
   sobrante y se saca de la pantalla.** El presupuesto que no prospera va a **Perdida** (estado que ya
   existe, ADR-048) y, si molesta a la vista, se **Archiva**. No existe ningún borrado de documentos en
   toda la sección Reservas.

---

## 1. LISTADO DE RESERVAS (`ReservasPage`) — capturas 01, 02, 16

### 1.1 Encabezado de la pantalla

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 1 | Título "Reservas" | **QUEDA** | Nombre del negocio, sin jerga (P-1). |
| 2 | Bajada "Administra tus reservas, presupuestos y ventas." | **CAMBIA (texto)** | No es rioplatense ("Administra" por "Administrá") y no aporta nada. Ver inventario de textos. |
| 3 | Botón principal arriba a la derecha | **QUEDA** como lugar y jerarquía | Un solo botón principal por pantalla, patrón ya usado en toda la app. |
| 4 | Su palabra: "Nuevo Presupuesto" | **PREGUNTA P1** | Choca con la guía: *"Nacimiento: SIEMPRE como cotización, sin excepciones"* (2026-06-07). Hoy el motor la crea **directo en Presupuesto** (`CreateReservaModal` manda `name:""` y el alta nace Budget) y por eso la solapa "Borradores anteriores" está **clavada en 0**. Contradicción real: no la resuelvo solo. |

### 1.2 Los cinco números de arriba (KPIs)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 5 | Los cuatro importes (Venta Total · Rentabilidad Est. · Por Cobrar, y todo lo que sea plata) **suman pesos con dólares** | **CAMBIA — grave** | Viola **P-3** (regla dura: las monedas nunca se suman). El propio código lo admite: *"si hay reservas en USD, este total puede estar mezclando montos de distinta moneda en una sola suma"* (`ReservaKPIs.jsx`, comentario). **Dependencia de motor:** el resumen del listado no trae desglose por moneda; sin ese dato el número es mentira y no debe mostrarse. |
| 6 | "Rentabilidad Est." visible solo para admin | **QUEDA** | F-14 (sin permiso de costos no se ven montos de costo/ganancia). |
| 7 | Rótulo "Rentabilidad Est." (abreviado) y "Operativos" | **CAMBIA (texto)** | "Operativos" no existe en el glosario del producto ni en el ciclo de vida; "Est." es abreviatura de sistema. P-1 / P-17. |
| 8 | Con el mes vacío quedan **cinco ceros gigantes** ocupando el ancho completo (captura 02) | **CAMBIA** | Misma familia que el reclamo de la ficha: lo que no tiene plata no puede dominar la pantalla. Cómo queda → **P2**. |
| 9 | Cuáles de los cinco números sirven de verdad | **PREGUNTA P2** | Ninguna regla firmada dice qué números van arriba del listado de reservas. |

### 1.3 La barra de solapas de estado

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 10 | "Todas" primera, seleccionada al entrar, con contador | **QUEDA** | Firmado 2026-07-25 y 2026-07-27. |
| 11 | "Anuladas" como solapa propia (incluye "esperando reembolso") | **QUEDA** | Firmado 2026-07-05 P1=A / P2=A. Palabra "Anuladas", nunca "Canceladas" (P-1). |
| 12 | "A liquidar" no existe | **QUEDA** | ADR-036 punto 0 / F-7. Verificado: no está. |
| 13 | Diez solapas en una sola fila, varias en 0 ("Borradores anteriores 0", "En gestion 0", "En viaje 0", "Perdidas 1") | **PREGUNTA P3** | Ninguna regla dice qué hacer con una solapa vacía. Es el reclamo textual del dueño. |
| 14 | "En gestion" sin tilde | **CAMBIA (texto)** | Inventario de textos. |
| 15 | Nombre "Borradores anteriores" | **PREGUNTA P1** | Es un nombre de transición: nombra la etapa por su pasado, no por lo que es. Se resuelve junto con P1. |

### 1.4 La barra de filtros

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 16 | El buscador **ignora la solapa y el período** | **QUEDA (firmado hoy y el 2026-07-05 P4=A)** | Encontrar una reserva vieja no puede depender de estar parado en la solapa correcta. **A verificar en la implementación**: es regla firmada hace un mes que Gastón volvió a pedir hoy. |
| 17 | Texto del casillero: hoy dice "Buscar reservas..." | **CAMBIA** | La regla firmada (2026-07-05 P5=A) fija **"Buscar por N° de reserva o cliente…"**. Está incumplida. |
| 18 | El buscador también encuentra por **nombre de servicio** | **QUEDA** | Firmado 2026-07-25. |
| 19 | Período inicial "Mes en curso" con flechas ◀ ▶ | **QUEDA** | Firmado 2026-07-27; mismo navegador de mes que la Caja (2026-06-13). |
| 20 | "POR creación" (desplegable de por qué fecha filtra) | **QUEDA** | Filtro legítimo. Su rótulo en versalita gris "POR" pegado al control es raro de leer → detalle de piel, resuelto en la maqueta. |
| 21 | Los tres controles + el buscador ocupan una fila entera aun cuando el buscador está vacío | **CAMBIA** | Se compacta: buscador a la izquierda (es lo que más se usa), filtros a la derecha. Sin cambiar ninguna función. |

### 1.5 La tabla

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 22 | Columna "Reserva" con el número **#F-2026-1004** en negrita | **QUEDA** | El número de negocio es la identidad (P-17 glosario; T-5 nunca el id interno). |
| 23 | Debajo, el renglón chico con `reserva.name`: **"Reserva F-2026-1023"**, **"File F-2026-1004"**, "Caribe", "ALBORNOZ" | **CAMBIA — grave** | Es un nombre autogenerado que repite el número, o basura vieja. "File" además es anglicismo (**P-1**). Origen verificado: el alta manda `name: ""` y el motor lo completa solo. Qué va en su lugar → **P4**. |
| 24 | "• Viaja: 06/02/2026" | **QUEDA** | Dato operativo real, formato argentino (P-2). |
| 25 | Columna "Cliente / pasajeros" con nombre + "N pax" | **QUEDA** | |
| 26 | Badge de estado en columna propia | **QUEDA** | Firmado 2026-07-05 P3=A ("con el badge alcanza": no se atenúa ni se tacha la fila). |
| 27 | Columna "Creada" con fecha + vendedor | **QUEDA** | Rastro a la vista (PR-12). |
| 28 | Columna "Finanzas": importe grande + chip (Debe / Saldado / Sin movimientos / A favor / Multa) | **QUEDA el contenido**, **CAMBIA la moneda** | El chip por estado de plata es correcto y sale de una única función (`getMoneyStatus`, F-1). Pero el importe se fuerza a pesos: el DTO de la fila *"no trae moneda"* (comentario en `ReservaTable.jsx`). Viola **P-3** y **T-4**. **Dependencia de motor.** |
| 29 | Columna "Acciones": **dos íconos sin palabra** | **CAMBIA — grave** | Viola **P-10** (la palabra siempre al lado, nunca tooltip). Hoy el texto vive en `title=`, o sea "apoyá el mouse para enterarte". |
| 30 | El ícono de "Ver detalles" es un **globo de chat** 💬 | **CAMBIA** | Dice una cosa y hace otra; además la fila entera ya es clickeable, así que es una acción duplicada. |
| 31 | "Archivar" apagado, con el motivo **solo en el tooltip** | **CAMBIA** | Viola **P-9** (botón vedado = gris + motivo **siempre a la vista**). |
| 32 | Qué acciones quedan en la fila | **PREGUNTA P5** | |

### 1.6 Estados de la pantalla

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 33 | Cargando (esqueleto de la tabla) | **QUEDA** | |
| 34 | Vacío: ícono + "No se encontraron reservas" + "Intenta ajustar los filtros de busqueda." | **CAMBIA** | Dos cosas: (a) el texto es neutro y sin tildes → inventario; (b) **no ofrece salida** (**P-11**). Si el mes está vacío, el cartel tiene que traer el botón para salir de ahí ("Ver todos los meses"). |
| 35 | "No se pudo traer la lista" con botón "Probar de nuevo" | **CAMBIA** | Hoy la pantalla tiene el estado de base caída, pero sin reintento a mano. Mismo criterio ya firmado para Copias de seguridad (2026-07-30: *"cartel rojo CON botón Probar de nuevo"*). |
| 36 | Pie de paginación ("0-0 de 0 · Mostrar 25 · Pagina 1 de 1") | **QUEDA** (con la tilde de "Página" → texto) | |

### 1.7 Vista angosta del listado (390px — captura 16)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 37 | Cinco tarjetas de KPI en dos columnas → la quinta queda **huérfana** ocupando media pantalla | **CAMBIA** | Se resuelve con P2 (menos números) + una tira compacta. |
| 38 | La barra de solapas se **corta** sin ninguna señal de que sigue | **CAMBIA** | El vendedor no puede saber que existen "Anuladas" o "Archivadas". Defecto objetivo, no decisión de gusto. |
| 39 | La tarjeta de reserva muestra **"Reserva F-2026-1065"** arriba y **"#F-2026-1065"** debajo | **CAMBIA** | El mismo número dicho dos veces (**P-16**) + el nombre autogenerado del punto 23. |
| 40 | La tarjeta muestra estado, cliente y fecha | **QUEDA** | |

---

## 2. VENTANA "NUEVO PRESUPUESTO" (`CreateReservaModal`) — captura 03

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 41 | **Dos controles para elegir UNA cosa**: un casillero "Buscar cliente…" que solo filtra + un desplegable "Seleccionar cliente…" que es el que realmente elige | **CAMBIA — grave** | Es el reclamo textual del dueño. El producto ya tiene firmado el patrón contrario: **un solo buscador con sugerencias debajo** (buscador de productos del servicio, 2026-06-05; pasajero reutilizable, 2026-06-23; casillero único de documento del cliente, 2026-07-25). Un solo casillero, se escribe, aparecen los parecidos, se elige uno. |
| 42 | "Se usara para facturacion y contacto." | **CAMBIA** | Cartelito aclarativo prohibido por **P-15**. Si el campo se llama "Cliente", no necesita explicación. |
| 43 | "Fecha de Inicio **(Opcional)**" | **CAMBIA** | El "(opcional)" está prohibido explícitamente (regla general 2026-06-05 / **P-15**). |
| 44 | No hay salida si el cliente **no existe todavía** | **CAMBIA** | **P-11**: el camino para resolverlo tiene que estar donde el usuario está parado ("No lo encontrás → crearlo acá"). |
| 45 | Que el alta sea una **ventana flotante** | **PREGUNTA P6** | **P-5** mató las ventanas para las *fichas de trabajo*; crear una reserva no está clasificado. No lo decido solo. |
| 46 | Bajada "Crea la reserva en estado Presupuesto y carga sus servicios." | **CAMBIA (texto)** + **PREGUNTA P1** | Nombra el estado interno como si fuera una explicación técnica, y consagra el salteo de la Cotización. |
| 47 | Botones "Cancelar" / "Crear Presupuesto" con estado "Creando…" | **QUEDA** | Bloqueo de doble envío correcto. |

---

## 3. FICHA DE RESERVA — armazón común (capturas 04 a 15)

### 3.1 Encabezado

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 48 | "← Volver a Lista" | **QUEDA** | (Con la palabra en criollo: "Volver al listado" → texto.) |
| 49 | "Reserva **#F-2026-1064**" como título | **QUEDA** | Identidad de negocio (P-17 glosario). |
| 50 | Badge grande del estado (CONFIRMADA / PRESUPUESTO / ANULADA…) | **QUEDA** | ADR-035 A-quinque: el estado operativo es UN solo badge grande. |
| 51 | Candadito 🔒 pegado al estado | **QUEDA** | Decisión #1 del 2026-06-08. |
| 52 | Chips "PAGO: …" y "FACTURA: …" con prefijo gris | **QUEDA el contenido** | Firmados: tres ejes separados (2026-06-22 punto 2), chip de facturación **siempre** visible incluso en Presupuesto (ADR-037 punto 1, respuesta 2B/1A). **No se reabre.** |
| 53 | Que esos chips vivan **en el mismo renglón que el título**, peleando con él (cuatro elementos en una línea) | **PREGUNTA P7** | El reclamo del dueño es de jerarquía, no de contenido. Ninguna regla fija en qué renglón van. |
| 54 | Nombre del cliente en grande ("PI0724 Consumidor Final") | **QUEDA** | Es el dato que el vendedor busca con la vista. |
| 55 | Debajo, en itálica, **"File F-2026-1004"** | **CAMBIA — grave** | Otra vez el nombre autogenerado + anglicismo (**P-1**). Mismo tratamiento que el punto 23 → **P4**. |
| 56 | Barra de fechas "Salida · Regreso" | **QUEDA** | |
| 57 | **"Regreso" auto-igualado a "Salida"** cuando el vendedor no cargó regreso (captura 05: Salida 10/12/2026 · Regreso 10/12/2026) | **CAMBIA — grave** | El sistema **inventa un dato** de negocio. Viola **P-21** ("el sistema SUGIERE, no decide") y **T-13**. Si no hay regreso, dice "sin cargar" (como ya hace en la captura 04a). **Dependencia de motor:** hay que ver si el dato se guarda igualado o solo se muestra así. |
| 58 | "Editar fechas" y "Reprogramar viaje" con candadito cuando la reserva está trabada | **QUEDA** | Patrón de candado firmado 2026-07-22. |

### 3.2 Los tres números

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 59 | Que sean **tres**: Saldo a cobrar · Recaudado · Inversión (costo, solo admin) | **QUEDA** | Firmado: decisión 5 del 2026-06-08 + corrección multimoneda del 2026-06-10 (*"3 números limpios"*). |
| 60 | La línea chiquita "de $ X presupuestado" bajo el saldo | **QUEDA** | Decisión 5 del 2026-06-08, textual. |
| 61 | En multimoneda, dos cifras apiladas con su cartelito $ / US$ (captura 14) | **QUEDA** | Firmado 2026-06-10 + P-3. |
| 62 | **Tres números gigantes en $ 0,00 dominando una ficha vacía** (capturas 04a, 07) | **PREGUNTA P10** | Reclamo textual del dueño. Ninguna regla dice qué tamaño tiene un número en cero. |
| 63 | El **puntito rojo que late** al lado del saldo con deuda | **PREGUNTA P10** | No está firmado en ningún lado; es decoración que compite con los avisos reales. |
| 64 | En Anulada: "SALDO —" (una rayita) y al lado Recaudado $0,00 e Inversión $0,00 gigantes | **CAMBIA** | La rayita no comunica nada. El bloque de contexto de anulación (saldo a favor / multa) ya existe en el código y es lo correcto; los ceros gigantes al lado lo tapan. Se resuelve con **P10**. |
| 65 | Rótulo "INVERSIÓN (COSTO)" | **CAMBIA (texto)** | Dos palabras para lo mismo, y ninguna es del glosario. Inventario de textos. |

### 3.3 Botonera de acciones (arriba a la derecha)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 66 | Los botones van en **una sola fila**, todos de la misma altura | **QUEDA** | ADR-035 A-bis (el botón de avance va en la misma fila, no flotando). |
| 67 | Botón de avance apagado + **motivo al lado, a la vista** ("Tiene que haber un pasajero titular con nombre") | **QUEDA** | Cumple **P-9** y la firma del 2026-07-25 (gate de titular). |
| 68 | Pero **ese motivo no tiene puerta**: en Presupuesto no existe la solapa Pasajeros ni ningún botón para cargar el titular | **CAMBIA — grave** | Viola **P-11** (ningún mensaje deja al usuario sin salida). Es el callejón que denunció el dueño. Dónde va la puerta → **P8**. |
| 69 | "Archivar" gris **sin motivo al lado** (capturas 04a, 06a) | **CAMBIA** | **P-9**: o se apaga con el motivo a la vista, o no aparece. |
| 70 | En **Anulada** siguen los tres botones (dos grises + "Volver atrás" activo, captura 07) | **CAMBIA** | ADR-036 punto 3: en Anulada y Perdida **se sacan los botones**, queda el cartel de solo lectura. Y **P-9**: lo que no aplica, no aparece. |
| 71 | En **Finalizada**, "Volver atrás" es un botón normal ámbar (captura 08) | **CAMBIA** | **F-16**: revertir algo firme es acción de último recurso — permiso elevado + motivo obligatorio + auditoría, y **"discreta, nunca un botón normal"**. Hoy compite con las acciones del día a día. |
| 72 | Dónde viven entonces las acciones de excepción (Volver atrás · Destrabar · Sacar de viaje · Reabrir) | **PREGUNTA P9** | F-16 dice "discreta" pero no dice **dónde**. |
| 73 | En **Archivada** no hay botonera: en su lugar hay un cartel "⚠ Solo lectura — Reserva archivada" **flotando donde iban los botones** (captura 09) | **CAMBIA** | El cartel de estado va en la franja de arriba, no en el lugar de la botonera (ADR-035 A: *"UN ÚNICO CARTEL arriba"*). Hoy hay dos convenciones distintas conviviendo. |
| 74 | "Anular reserva" en rojo pleno como acción más visible de una Confirmada | **PREGUNTA P14** | Aparece además repetido en la solapa Estado de Cuenta (ver 5.4). |

### 3.4 La tira de avisos

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 75 | Franja del candado en **una línea** + botón "Destrabar reserva" a la derecha | **QUEDA** | Respuesta 4B del 2026-07-05, cumplida. |
| 76 | Aviso "1 servicio sin confirmar" mostrado **directo** (sin plegar) por ser el único informativo | **QUEDA** | Respuesta 5A del 2026-07-05: *"si hay un solo aviso informativo, se muestra DIRECTO"*. La regla se está cumpliendo. |
| 77 | Que los dos sean **del mismo amarillo**, uno arriba del otro, sumando ~140 px de amarillo | **PREGUNTA P11** | Acá está el "banners amarillos apilados" del dueño: el problema **no es cuántos hay** (las reglas se cumplen), es que **accionable e informativo se pintan igual**. La sección "Colores y estilo" de la guía está **VACÍA** (vacío V-7 de la constitución). No invento un color. |
| 78 | El aviso informativo repite en tres renglones lo que dice su título ("Estos servicios todavía no tienen respuesta del proveedor. Resolvelos en la pestaña de Servicios…") | **CAMBIA (texto)** | Aviso largo donde alcanzaba una línea (**P-17 regla 7**: corto). Inventario. |
| 79 | En Presupuesto, la franja "Presupuesto. Cuando el cliente confirme, usá 'El cliente aceptó'…" | **QUEDA** | Guía de flujo de etapa temprana, explícitamente no plegable (2026-07-05, 5A). |
| 80 | Cartel de estado terminal ("Reserva anulada — solo lectura.", "Reserva finalizada — solo lectura.") | **QUEDA** | Textos firmados en ADR-035 A / ADR-037 punto 2. **No se tocan.** |

### 3.5 Las solapas de la ficha

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 81 | Seis solapas: Servicios · Pasajeros (N) · Historial · Estado de Cuenta · Vouchers · Documentos | **QUEDA la existencia** de Servicios, Pasajeros, Historial y Estado de Cuenta | Cada una tiene reglas firmadas propias. |
| 82 | En Presupuesto solo se muestran **Servicios e Historial** (desaparecen Pasajeros, Estado de Cuenta, Vouchers y Documentos) | **PREGUNTA P8** | Esconder Pasajeros en la etapa donde el sistema **te exige** el titular es lo que produce el callejón del punto 68. |
| 83 | "Vouchers" y "Documentos" hacen **casi lo mismo** | **PREGUNTA P12** | Verificado en las capturas 12 y 13: la solapa **Vouchers** se titula *"Documentación"*, se describe como *"vouchers generados por el sistema y archivos cargados externamente"* y su botón es *"Añadir Documento"*; la solapa **Documentos** es la zona de arrastrar archivos. Dos puertas para lo mismo. |
| 84 | El contador en la solapa ("Pasajeros (1)") | **QUEDA** | |

---

## 4. FICHA POR ESTADO

### 4.1 Presupuesto (capturas 04a, 14)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 85 | Botones [El cliente acepto] [Perdida] [Archivar] | **QUEDA el juego** | Son las tres acciones que le corresponden a un presupuesto: avanzar, darlo por perdido o sacarlo de la vista. |
| 85-bis | El botón **[🗑 Eliminar]** en rojo, al lado de "Perdida" | **CAMBIA — se saca de la pantalla** | **Regla del dueño, 2026-08-03 (textual): *"las reservas no se borran, se anulan; nada importante se borra"*.** Es un sobrante: **nada se borra, ni reservas ni presupuestos.** El presupuesto que no prospera va a **Perdida** (estado que ya existe, ADR-048); si molesta a la vista, se **Archiva**. Esto **deroga** la parte de la regla 2026-06-08 que listaba "Eliminar (icono 🗑) — solo en Cotización / Presupuesto (sin pagos)": ese botón deja de existir en toda la sección Reservas. La botonera de Presupuesto queda en **tres** acciones. |
| 86 | "El cliente acepto" sin tilde | **CAMBIA (texto)** | |
| 87 | El bloque "PASAJEROS DEL VIAJE" con los tres casilleros adultos/menores/infantes | **QUEDA** | Firmado 2026-06-15 P1: en el presupuesto se carga **solo la cantidad**, en tres casilleros. |
| 88 | El párrafo "Acá cargás cuántos viajan. Los nombres y documentos se agregan en la solapa Pasajeros — o directamente al emitir cada servicio." | **CAMBIA — grave** | Dos faltas: es un cartelito aclarativo (**P-15**) y **manda a una solapa que en esta etapa no existe** (**P-11**). |
| 89 | Botón "Guardar cantidades" separado, con su propio aviso de éxito | **PREGUNTA P8** | Un formulario adentro del formulario. Se resuelve junto con el lugar de los pasajeros. |
| 90 | El aviso verde "Listo / Cantidades actualizadas" como globito | **QUEDA** | **P-6**: el globito es SOLO para el éxito. Correcto. |

### 4.2 En gestión (captura 05)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 91 | Botones [Anular reserva] [Volver atrás] [Archivar gris] | **QUEDA** (salvo el motivo faltante de Archivar, punto 69, y el lugar de "Volver atrás", **P9**) | Vocabulario correcto: Anular, no Cancelar (**P-1**). |
| 92 | Resumen "1 de 3 servicios resueltos" + pelotita por fila | **CAMBIA — falta** | Decisión #4 del 2026-06-08 (firmada) pide ese resumen arriba en En gestión. No aparece en la captura. |
| 93 | El chip "PAGO: SIN MOVIMIENTOS" | **QUEDA** | Firmado 2026-06-24. |

### 4.3 Confirmada (capturas 06a, 06b, 10 a 13)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 94 | Candado + "Destrabar reserva" | **QUEDA** | #1 y #2 del 2026-06-08 + P-19. |
| 95 | Título con **cuatro** elementos peleando ("Reserva #F-2026-1064 [CONFIRMADA] 🔒 FACTURA: [✓ FACTURADA Y DEVUELTA]") | **PREGUNTA P7** | |
| 96 | Chip "✓ Facturada y devuelta" | **QUEDA** | **F-3** + firma 2026-07-17: "hubo comprobante" no se borra porque después se acreditó. |
| 97 | Falta el botón "Marcar emitido / Marcar confirmado" en la fila del servicio Solicitado | **CAMBIA — verificar** | Firmado 2026-07-24 (P1..P4) y decisión #3 del 2026-06-08. En la captura, el aéreo Solicitado solo ofrece Editar/Borrar. Puede estar tapado por el candado; si es así, el candado tiene que **decirlo** (P-9), no esconder el botón sin explicación. |

### 4.4 Anulada (captura 07)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 98 | Cartel "Reserva anulada — solo lectura." | **QUEDA** | Texto firmado. |
| 99 | Línea "1 de 1 servicio anulado" bajo el título | **QUEDA** | Firmado 2026-06-13 (contador chiquito gris, solo si hay alguno). |
| 100 | Servicio tachado con "Anulado por test el 03/08/2026" | **QUEDA** | **F-6** + #9 del 2026-06-08. Es exactamente el hueso que el dueño quiere conservar. |
| 101 | Importes del servicio anulado tachados | **QUEDA** | Firmado 2026-07-17 (T4). |
| 102 | La botonera sigue viva | **CAMBIA** | Ver punto 70. |
| 103 | El bloque "SALDO —" | **CAMBIA** | Ver punto 64. |

### 4.5 Finalizada (captura 08)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 104 | Cartel "Reserva finalizada — solo lectura." (sin invitar a reabrir) | **QUEDA** | ADR-037 punto 2 (superó a ADR-035/036). |
| 105 | Chips "PAGO: PAGADA · FACTURA: FACTURADA TOTAL" | **QUEDA** | 2026-06-22 punto 2. |
| 106 | Etiqueta "Pago parcial al operador (resta $ 3.000,00)" en la fila del servicio | **CAMBIA — grave** | ADR-036 P4=B, textual: la etiqueta de pago al operador **NUNCA muestra montos** (para que la vea también quien no tiene permiso de costos). Hoy los muestra. Viola además **F-14**. |
| 107 | "Volver atrás" como botón normal | **CAMBIA** | Ver punto 71 + **P9**. |

### 4.6 Archivada (captura 09)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 108 | Solo lectura de verdad (sin botones de escritura) | **QUEDA** | |
| 109 | El aviso de solo lectura **como cartel flotante en el lugar de la botonera** | **CAMBIA** | Ver punto 73. Un solo lugar para el cartel de estado, siempre el mismo. |
| 110 | Se puede seguir viendo/descargando lo emitido | **QUEDA** | **P-18**. |

---

## 5. LAS SEIS SOLAPAS POR DENTRO

### 5.1 Servicios (`ServiceList`)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 111 | Tabla TIPO · DESCRIPCIÓN · FECHA/ESTANCIA · ESTADO · COSTO NETO · PRECIO VENTA · AVISOS · ACCIONES | **QUEDA** | |
| 112 | "Anular varios servicios" lista **solo confirmados** | **QUEDA (firmado hoy)** | |
| 113 | "Anular varios servicios" aparece **solo con 2+ anulables** | **QUEDA (firmado hoy)** | Hoy aparece siempre que haya al menos uno. |
| 114 | Botones de la cabecera de la lista con candadito cuando la reserva está trabada | **QUEDA** | 2026-07-22. |
| 115 | Badge del servicio: "EN ESPERA" en Cotización/Presupuesto vs "SOLICITADO" en adelante | **CAMBIA — deroga regla** | Gastón firmó hoy la **unificación**. Esto **deroga** la regla del 2026-06-08 ("dos textos según la etapa"). Qué palabra queda → **P13**. |
| 116 | "Confirmado" / "Anulado" tal cual vienen | **QUEDA** | 2026-06-08 + ADR-036 punto 8 (una reserva deshecha muestra todos sus servicios "Anulado"). |
| 117 | Etiqueta "⚠ Operador impago (**$ 50.000,00**)" | **CAMBIA — grave** | Misma falta que el punto 106: la etiqueta no lleva montos (ADR-036 P4=B, **F-14**). |
| 118 | Columna AVISOS con "⏰ Empieza el 10/08 (en 7 días)" | **QUEDA** | Ronda 9 (2026-06-06), textual. |
| 119 | Control "Para: Todos" en la fila | **QUEDA** | Firmado 2026-06-15 (tarde). |
| 120 | Acciones "Editar" / "Borrar" **con la palabra al lado** | **QUEDA** | Cumple **P-10** y el wording dinámico Borrar/Cancelar del 2026-06-08. |
| 121 | Control "Ver también los cancelados (N)" arriba, al lado del título | **QUEDA** | Firmado 2026-07-05 P6/P7=A. |
| 122 | Enlace "Ver historial" por servicio | **QUEDA** | Firmado 2026-07-05 P8=A (depende de motor). |
| 123 | Costo neto en tipografía de máquina de escribir (monoespaciada) y el precio de venta en otra | **CAMBIA** | Dos tipografías para dos plata de la misma tabla. Es piel: se unifica. |
| 124 | Vacío: "No hay servicios cargados en este file." | **CAMBIA (texto)** | "file" a la vista → **P-1** / **T-5**. |

### 5.2 Pasajeros (`PassengerList`) — capturas 05, 06b

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 125 | "N de M nombres cargados" | **QUEDA** | Firmado 2026-06-15 P10. |
| 126 | Renglón por pasajero con tipo ("ADULTO 1"), nombre y documento | **QUEDA** | Firmado 2026-06-15 P9. |
| 127 | Chip rojo "DNI VENCIDO PARA EL VIAJE" / "PASAPORTE VENCIDO PARA EL VIAJE" | **QUEDA** | Obra firmada 2026-06-13 y 2026-08-03 (semáforo de documento). |
| 128 | Acciones de la fila: **lápiz y tacho sin palabra** | **CAMBIA** | Viola **P-10**. |
| 129 | En Confirmada, esos dos íconos se vuelven **dos candaditos sin palabra ni motivo** | **CAMBIA** | Viola **P-9** (motivo a la vista) y **P-10**. Dos candaditos pegados no dicen qué acción tapan. |
| 130 | Alta/edición en línea (muere el modal) | **QUEDA** | Firmado 2026-07-05 P9/P10=A. |
| 131 | Botón "+ Agregar Pasajero" | **QUEDA** | (Se oculta en estados de solo lectura — A-ter.) |

### 5.3 Historial (captura 10)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 132 | Que exista una línea de tiempo con quién y cuándo | **QUEDA — es un hueso** | **PR-12** / F-6. |
| 133 | "Cambio en una Factura **por Sistema**", "Alta de un Pago **por Sistema**" | **CAMBIA — grave** | Viola **P-17 regla 1**, textual: *"El sujeto es la reserva o el agente, **nunca 'el sistema'**"*. |
| 134 | "Cambio en una Reserva", "Alta de un Pago" | **CAMBIA (texto)** | Es el nombre de la operación interna (alta/baja/modificación de una tabla), no lo que pasó en el negocio. **T-5**. |
| 135 | Fechas "25 jul 02:07" | **CAMBIA** | Formato no argentino (**P-2** pide dd/MM) y hora que hay que confirmar que sea argentina (**T-14**). |
| 136 | "Cobro registrado: -$ 140.000,00 — Otro medio" con el signo menos | **CAMBIA** | Un cobro **entra** plata; mostrarlo en negativo invierte el sentido para el vendedor. |

### 5.4 Estado de Cuenta (captura 11)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 137 | Fila de acciones [Registrar cobro] [Emitir factura] [Anular reserva] | **QUEDA las dos primeras** | Son las acciones de plata de la solapa. |
| 138 | "Anular reserva" **repetido** acá y en la cabecera | **PREGUNTA P14** | |
| 139 | Bloque "VENTA Y FACTURACIÓN: Vendido firme · Facturado · Falta facturar" + chip | **QUEDA** | Firmado 2026-06-22 punto 3 (eje de facturación separado del de cobranza). |
| 140 | El extracto cronológico con saldo después de cada movimiento | **QUEDA** | Firmado 2026-06-22 punto 3 y 2026-07-16. (No entra en las capturas; no lo juzgo.) |
| 141 | Botón "Registrar cobro" visible y apagado con motivo cuando no hay saldo | **QUEDA** | Firmado 2026-07-25. |

### 5.5 Vouchers (captura 12)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 142 | La solapa se llama "Vouchers" pero adentro el título dice **"Documentación"** | **CAMBIA** | Un lugar, un nombre. |
| 143 | Bajada "Gestiona vouchers generados **por el sistema** y archivos cargados externamente." | **CAMBIA** | **P-17 regla 1** ("el sistema") + **P-15** (cartelito). |
| 144 | Botón "Añadir Documento" dentro de la solapa Vouchers | **PREGUNTA P12** | |
| 145 | "Enviar al pasajero" por voucher | **QUEDA** | Firmado 2026-06-23 (Tanda 3, punto 2). |
| 146 | Ver / descargar / reimprimir en estados congelados | **QUEDA** | **P-18**. |

### 5.6 Documentos (captura 13)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 147 | Zona de arrastrar archivos | **QUEDA** | |
| 148 | "Haz clic o arrastra documentos aqui" | **CAMBIA (texto)** | Español neutro + sin tilde. **P-17 regla 6** (rioplatense, vos). |
| 149 | "DNI, pasaportes, permisos, autorizaciones y adjuntos generales (max 25 MB)" | **QUEDA** | Acá el texto **sí** ayuda (dice qué se puede subir y el límite); no es un cartelito aclarativo de un campo. |

---

## 6. FORMULARIOS EN LÍNEA (capturas 04b, 04c, 14, 15)

| # | Elemento | Veredicto | Por qué |
|---|---|---|---|
| 150 | La ficha de carga de servicio se abre **en línea, debajo de la lista** | **QUEDA** | **P-5** + Propuesta C firmada 2026-06-05 (*"Sí, me encantó"*). |
| 151 | Pastillas de tipo (Hotel · Aéreo · Traslado · Paquete · Asistencia) | **QUEDA** | |
| 152 | Un solo campo de identidad por tipo ("Ruta / aerolínea") | **QUEDA** | Ronda 3, 2026-06-06. |
| 153 | Buscador sin resultados → "No encontramos '…' en tu tarifario" + crear | **QUEDA** | Ronda 2, 2026-06-05. |
| 154 | "Revisá los de arriba antes — si ya existe, elegilo evita duplicados." | **QUEDA** | Es la política antiduplicados firmada ("hacer lo imposible para evitar duplicados"), no un cartelito de campo. |
| 155 | El panel de sugerencias **tapa la fila de fechas** mientras está abierto (captura 04b) | **CAMBIA** | Defecto de superposición: el vendedor pierde de vista lo que ya cargó. |
| 156 | "+ Más detalles" cerrado por defecto | **QUEDA** | Ronda 7, 2026-06-06. |
| 157 | Pie "Venta $ 150,00 · Ganás $ 50,00" | **QUEDA** | (La ganancia solo para quien ve costos — F-14.) |
| 158 | Botones "Cancelar" / "Guardar servicio" al pie de la ficha | **QUEDA** | |
| 159 | El desplegable "Nacional (dentro del país)" **sin rótulo** arriba | **CAMBIA** | El resto de los campos tiene rótulo; este no. Se agrega. |
| 160 | El campo "Regreso" vacío que después queda igualado a la salida | **CAMBIA** | Ver punto 57 (**P-21**). |
| 161 | Si falla guardar: queda todo cargado + cartel rojo arriba de los botones | **QUEDA** | **P-6** y **P-7**. |

---

## 7. RESUMEN

- Elementos auditados: **162**
- **QUEDAN: 78** (los huesos: modelo de estados, candado, auditoría a la vista, chips de tres ejes, fichas en línea, reglas de servicios y pasajeros ya firmadas)
- **CAMBIAN: 70** — de los cuales:
  - **10 rompen una regla firmada de forma grave** (montos en las etiquetas de operador ×2, KPIs y columna Finanzas mezclando monedas ×2, íconos sin palabra ×3, nombre autogenerado "File F-…" ×2, botonera viva en Anulada, "por Sistema" en el historial, doble selector de cliente, callejón del titular, Regreso inventado, **botón "Eliminar" en el Presupuesto**). *(algunos cuentan en más de una fila)*
  - **21 son CAMBIA (texto)** → van al inventario paralelo, no se tratan acá.
- **PREGUNTAS abiertas: 14** (P1 a P14, abajo)

---

# PREGUNTAS PARA GASTON — ✅ RESPONDIDAS EL 03/08/2026

> **Las 14 están contestadas.** Abajo queda cada una con su respuesta marcada, para que se pueda releer
> por qué se decidió lo que se decidió. Lo elegido ya está dibujado en la maqueta
> `docs/ux/maquetas/2026-08-03-reservas-rediseno.html`.
> **Ojo con P1:** la respuesta fue **C**, distinta de la que yo recomendaba (A). Manda la respuesta.
> Al final del documento hay **3 preguntas nuevas** (P15, P16, P17) que se abrieron al dibujar las
> pantallas que faltaban.

| # | Tema | Respuesta |
|---|---|---|
| P1 | Cómo nace una reserva | **C** — nace SIEMPRE como **Borrador**; un solo botón "+ Nueva reserva"; adentro "Pasar a presupuesto"; la solapa se llama **"Borradores"** |
| P2 | Números de arriba del listado | **A** — tres, en tira fina, con pesos y dólares separados |
| P3 | Solapas en cero | **B** — quedan siempre, apagadas y no clickeables |
| P4 | Renglón bajo el N° de reserva | **A** — el destino del viaje |
| P5 | Acciones de la fila | **A** — una sola: "Archivar", con la palabra y el motivo a la vista |
| P6 | El alta, ¿ventana? | **A** — deja de ser ventana: fila en línea arriba del listado |
| P7 | Encabezado de la ficha | **A** — el estado con el título; pago y factura, renglón aparte abajo |
| P8 | Dónde se carga el titular | **A** — la solapa "Pasajeros" existe desde el borrador, y el motivo del botón apagado es el enlace "Cargar el titular" |
| P9 | Acciones de excepción | **A** — detrás del "⋯" |
| P10 | Los tres números | **A** — grande solo el que tiene plata; los demás, línea chica |
| P11 | Color de los avisos | **A** — el que pide acción, ámbar; el que solo informa, gris de una línea |
| P12 | Vouchers + Documentos | **A** — una sola solapa "Documentos" con dos bloques |
| P13 | Palabra única del servicio | **"SOLICITADO"** |
| P14 | "Anular reserva" duplicado | **A** — queda solo arriba, en la botonera de la reserva |

> Además, dos palabras firmadas el mismo día (van al inventario de textos):
> **"Forma de pago"** (nunca "Método") y **"Emitir igual"** (nunca "Emitir por excepción").

---

## Tema A — Cómo nace una reserva

**Contexto:** hoy el botón dice "Nuevo Presupuesto" y la reserva nace **directo como Presupuesto**. Por eso la
primera solapa del listado ("Borradores anteriores") está clavada en 0: nunca entra nada ahí. Pero lo que
está escrito en la guía desde el 2026-06-07 es lo contrario: *"nace SIEMPRE como cotización, sin excepciones"*.
Una de las dos cosas tiene que ceder.

> ✅ **RESPONDIDA: C** (03/08/2026). La reserva **nace siempre como Borrador**. Afuera hay **un solo botón:
> "+ Nueva reserva"**. Adentro, cuando está listo para mandárselo al cliente, se aprieta **"Pasar a
> presupuesto"**. La solapa se llama **"Borradores"**, a secas. ⚠️ Es **distinta de la que yo recomendaba (A)**:
> manda la respuesta. Consecuencia directa: la palabra **"Cotización" desaparece** de la pantalla — esa
> etapa ahora se llama **Borrador** en todos lados (una cosa, una palabra).

**P1. ¿Con qué arranca una reserva nueva?**

**A) Arranca como PRESUPUESTO (lo que pasa hoy) y la etapa "Cotización" se elimina del producto.**
Desaparece la solapa vacía. Un paso menos para el vendedor.

```
  Reservas                                    [ + Nuevo presupuesto ]

  ( Todas 60 )  Presupuestos 2   En gestión 0   Confirmadas 12  ...
     ↑ ya no está "Borradores anteriores"
```

**B) Arranca como BORRADOR y hay dos botones: "Nuevo borrador" y "Nuevo presupuesto".**
El vendedor elige si está haciendo números rápidos o un documento para mandar.

```
  Reservas                        [ Nuevo borrador ] [ + Nuevo presupuesto ]

  ( Todas 60 )  Borradores 3   Presupuestos 2   En gestión 0  ...
```

**C) Arranca SIEMPRE como borrador (lo que dice la guía) y adentro tenés el botón "Pasar a presupuesto".**
Un solo botón afuera, dos pasos adentro.

```
  Reservas                                    [ + Nueva reserva ]
                                                     ↓
  Reserva #F-2026-1066  [ BORRADOR ]     [ Pasar a presupuesto ]
```

> **Recomiendo A.** Hace un mes que el producto funciona así y la etapa "Cotización" nunca se usó
> (0 reservas). Mantener una solapa vacía y un paso que nadie hace es cargar al vendedor con una
> decisión que no le aporta. Si algún día hacen falta borradores internos, se agrega.

---

> ✅ **RESPONDIDA: A** (03/08/2026). Tres números en una tira fina de una línea: **Reservas activas · Por
> cobrar · Vendido**, con **pesos y dólares separados** (nunca sumados).

**P2. ¿Qué números querés ver arriba del listado?**

**Contexto:** hoy hay cinco (Reservas Activas · Operativos · Venta Total · Rentabilidad Est. · Por Cobrar) y en
un mes sin movimiento son cinco ceros gigantes ocupando toda la pantalla. Además, **los que son plata están
mal**: suman pesos con dólares en un solo número (eso se arregla sí o sí; lo que estoy preguntando es
**cuáles** querés).

**A) Tres números, en una tira fina de una línea.**

```
  Reservas activas  12   ·   Por cobrar  $ 223.445 · US$ 1.200   ·   Vendido  $ 268.535
```

**B) Los cinco de hoy, pero en tarjetas chicas y en una sola línea.**

```
  [ Activas 12 ] [ En viaje 0 ] [ Vendido $268.535 ] [ Ganancia $59.675 ] [ Por cobrar $223.445 ]
```

**C) Ninguno: se saca la fila entera y arriba queda solo el buscador.**

```
  Reservas                                       [ + Nuevo presupuesto ]
  [ 🔍 Buscar por N° de reserva o cliente… ]
```

> **Recomiendo A**, con estos tres: **Reservas activas · Por cobrar · Vendido** (y "Por cobrar" y "Vendido"
> mostrando pesos y dólares separados, como manda la regla). Son los tres que se miran de reojo al entrar.
> "Operativos" no lo entiende nadie y "Rentabilidad Est." es un número de fin de mes, no de listado —
> ese vive mejor en Reportes.

---

> ✅ **RESPONDIDA: B** (03/08/2026). Quedan todas siempre; las que están en 0 se ven **apagadas y no se
> pueden tocar**. Cero también es información.

**P3. Las solapas que están en cero, ¿qué hacemos?**

```
  hoy:  ( Todas 60 )  Borradores anteriores 0   Presupuestos 2   En gestion 0   Confirmadas 12
        En viaje 0   Finalizadas 9   Anuladas 29   Perdidas 1   Archivadas 7
                ↑ tres solapas que no llevan a ningún lado
```

**A) Se esconden solas cuando están en 0** (vuelven a aparecer apenas entra una).

```
  ( Todas 60 )  Presupuestos 2   Confirmadas 12   Finalizadas 9   Anuladas 29   Perdidas 1   Archivadas 7
```

**B) Quedan todas siempre, pero las que están en 0 se ven apagadas y no se pueden tocar.**

```
  ( Todas 60 )  Presupuestos 2   ˹En gestión 0˼   Confirmadas 12   ˹En viaje 0˼  ...
                                   ↑ gris, no clickeable
```

**C) Quedan como están hoy.**

> **Recomiendo B.** Esconderlas (A) tiene un problema real: el vendedor mira "¿cuántas tengo en viaje?" y
> **la respuesta cero es información**. Si la solapa desaparece, no sabe si es que no hay o si el sistema
> se la comió. Apagada dice "no hay ninguna" sin ofrecerte un clic que no lleva a nada.

---

## Tema B — El listado

> ✅ **RESPONDIDA: A** (03/08/2026). Va el **destino del viaje**. Si la reserva todavía no tiene servicios,
> el renglón no aparece.

**P4. Debajo del número de reserva hay un renglón chico que hoy dice basura: "Reserva F-2026-1023",
"File F-2026-1004", "ALBORNOZ". ¿Qué querés que diga ahí?**

**Contexto:** ese renglón es un "nombre" que el sistema se inventa solo al crear la reserva (repite el número
o queda un texto viejo). Nadie lo escribe y no sirve para nada.

**A) El destino del viaje.**

```
  #F-2026-1004
  Cancún · Riviera Maya
  • Viaja: 06/02/2026
```

**B) Nada: se saca el renglón y queda solo el número y la fecha de viaje.**

```
  #F-2026-1004
  • Viaja: 06/02/2026
```

**C) Un nombre que escribe el vendedor si quiere ("Luna de miel García").**

```
  #F-2026-1004
  Luna de miel García          ← lo escribe el vendedor, puede quedar vacío
  • Viaja: 06/02/2026
```

> **Recomiendo A: el destino.** Es lo que el vendedor necesita para reconocer la reserva de un vistazo, y
> el sistema ya lo sabe (sale de los servicios cargados). No le agrega laburo a nadie. Si la reserva
> todavía no tiene servicios, el renglón no aparece.

---

> ✅ **RESPONDIDA: A** (03/08/2026). Una sola acción en la fila: **"Archivar"**, con la palabra al lado, y
> cuando no se puede, **el motivo escrito debajo** (nunca en el mouse). El globito de chat se va.

**P5. En la última columna hay dos íconos sin palabra (un globito y un archivador). ¿Qué acciones querés
en la fila?**

**Contexto:** el globito abre la reserva — pero **la fila entera ya se abre haciendo clic en cualquier lado**,
así que es un botón que repite lo que ya hacés. El archivador a veces está apagado y el motivo solo aparece
si apoyás el mouse (eso está prohibido: la palabra y el motivo van siempre a la vista).

**A) Una sola acción, con palabra: "Archivar".**

```
  ...  $ 6.440,00   [Debe: $1.440]        [ 🗄 Archivar ]
  ...  $ 4.600,00   [Saldado]             [ 🗄 Archivar ]
  ...  $ 4.600,00   [Saldado]             🗄 Archivar — no se puede: tiene saldo sin cobrar
```

**B) Ninguna: se saca la columna. Archivar se hace desde adentro de la reserva.**

```
  ...  $ 6.440,00   [Debe: $1.440]
  ...  $ 4.600,00   [Saldado]
```

**C) Un botón "⋯" que abre una listita con las acciones.**

```
  ...  $ 6.440,00   [Debe: $1.440]        [ ⋯ ]
                                            └─ Archivar
                                               Abrir en otra pestaña
```

> **Recomiendo A.** Archivar de a una desde el listado es trabajo real de limpieza (siete archivadas ya
> hay), y sacarlo obligaría a entrar y salir de cada reserva. Con la palabra al lado y el motivo escrito
> cuando no se puede, se cumple la regla y se entiende sin adivinar.

---

## Tema C — La ficha de la reserva

> ✅ **RESPONDIDA: A** (03/08/2026). **Deja de ser ventana:** se abre como una fila en línea arriba del
> listado, con **un solo casillero de cliente** y la salida "Es un cliente nuevo: crearlo acá".
> **Ajustada por P1=C:** la fila ya no dice "Nuevo presupuesto" sino **"Nueva reserva"**, y lo que crea es
> un **Borrador**.

**P6. Crear un presupuesto, ¿sigue siendo una ventana que se abre encima?**

**Contexto:** en todo el resto del producto las ventanas murieron ("el modal me parece horrible"): cargar un
servicio, cobrar, anular, cargar un pasajero — todo se abre **dentro de la página**. La única que quedó es
esta. Además hoy pide el cliente con **dos controles** para elegir una sola cosa (eso se arregla igual:
queda un solo buscador).

**A) Deja de ser ventana: se abre una fila arriba del listado.**

```
  Reservas                                          [ + Nuevo presupuesto ]
  ┌──────────────────────────────────────────────────────────────────────┐
  │ Cliente  [ 🔍 escribí el nombre o el documento…            ]         │
  │          ▸ Gastón Albornoz Salafia — DNI 27.xxx                      │
  │          ▸ + Es un cliente nuevo: crearlo                            │
  │ Salida   [ dd/mm/aaaa ]              [ Cancelar ]  [ Crear ]         │
  └──────────────────────────────────────────────────────────────────────┘
  RESERVA        CLIENTE          ESTADO      ...
```

**B) Sigue siendo ventana, pero con un solo buscador de cliente y sin las leyendas.**

```
        ┌─────────────────────────────────┐
        │ Nuevo presupuesto            ✕  │
        │ Cliente  [ 🔍 nombre o doc…  ]  │
        │ Salida   [ dd/mm/aaaa       ]   │
        │        [ Cancelar ] [ Crear ]   │
        └─────────────────────────────────┘
```

**C) Se saca el paso: el botón crea la reserva vacía y te lleva derecho adentro, y el cliente se elige ahí.**

```
  [ + Nuevo presupuesto ]  →  Reserva #F-2026-1066  [ PRESUPUESTO ]
                              Cliente: [ 🔍 elegí el cliente… ]
```

> **Recomiendo A.** Es la misma forma que ya usa todo el producto y te deja ver el listado detrás mientras
> creás. Y sea cual sea la que elijas, en las tres el cliente pasa a ser **un solo casillero** con
> sugerencias, y si no existe, lo creás desde ahí mismo (hoy no hay salida para eso).

---

> ✅ **RESPONDIDA: A** (03/08/2026). El **estado se queda con el título**; **pago y factura bajan a un
> renglón propio**. No desaparece ninguno de los tres.

**P7. En el encabezado de la ficha hay cuatro cosas peleando en el mismo renglón. ¿Cómo las ordenamos?**

**Contexto:** hoy el título es
`Reserva #F-2026-1064 [CONFIRMADA] 🔒 FACTURA: [✓ FACTURADA Y DEVUELTA]`.
Ninguno de esos cartelitos sobra (los firmaste todos), pero juntos no se sabe qué mirar primero.

**A) El estado se queda con el título; el pago y la factura bajan a un renglón propio abajo.**

```
  Reserva #F-2026-1064   [ CONFIRMADA 🔒 ]
  PI0724 Consumidor Final
  Pago: Pendiente  ·  Factura: Facturada y devuelta
```

**B) Todo abajo: el título queda limpio y los tres cartelitos van juntos en su renglón.**

```
  Reserva #F-2026-1064
  PI0724 Consumidor Final
  [ CONFIRMADA 🔒 ]   Pago: Pendiente   ·   Factura: Facturada y devuelta
```

**C) Queda como está hoy.**

> **Recomiendo A.** El estado de la reserva **es** el título (es lo primero que mirás al entrar); la plata
> es una segunda capa. Separarlos en dos renglones no saca ninguna información, solo ordena el orden en
> que la leés.

---

> ✅ **RESPONDIDA: A** (03/08/2026). La solapa **"Pasajeros" existe desde el borrador y el presupuesto**, y
> el motivo del botón apagado es un **enlace "Cargar el titular"** que lleva ahí. Se van el párrafo
> explicativo y el botón "Guardar cantidades": las cantidades se guardan solas.

**P8. En un Presupuesto, el botón "El cliente aceptó" está apagado y dice "Tiene que haber un pasajero
titular con nombre" — pero no hay ningún lado para cargarlo. ¿Dónde lo cargás?**

**Contexto:** es el callejón sin salida que encontraste. En esa etapa la solapa "Pasajeros" no existe, y el
texto que dice "los nombres se agregan en la solapa Pasajeros" **manda a una solapa que no está**.

**A) La solapa "Pasajeros" aparece desde el Presupuesto, y el motivo del botón apagado es un enlace que
te lleva ahí.**

```
  [ El cliente aceptó ]  Falta el titular → [ Cargar el titular ]
  ─────────────────────────────────────────────────────────────
   Servicios | Pasajeros (0) | Historial
                   ↑ ahora existe desde el presupuesto
```

**B) El titular se carga en el mismo bloque donde ponés las cantidades, sin solapa nueva.**

```
  PASAJEROS
  Adultos [1]   Menores [0]   Infantes [0]
  Titular  [ nombre y apellido…      ]  [ Documento… ]
```

**C) Al apretar "El cliente aceptó" se abre ahí mismo un casillero para el titular y sigue de largo.**

```
  [ El cliente aceptó ]
     └─ Falta el titular:  [ nombre y apellido… ] [ Documento… ]  [ Confirmar ]
```

> **Recomiendo A.** Los pasajeros ya tienen su lugar propio y funciona bien; el problema es solo que está
> escondido justo en la etapa donde el sistema te lo exige. Mostrar la solapa desde el principio arregla
> el callejón **y** hace que el texto del cartel deje de mentir. De paso, el bloque de cantidades pierde
> su párrafo explicativo y su botón "Guardar cantidades" aparte: se guarda solo, como todo lo demás.

---

> ✅ **RESPONDIDA: A** (03/08/2026). Detrás de un botón **"⋯"** al final de la fila de acciones. Un clic y
> están; dejan de competir con lo de todos los días.

**P9. Las acciones de excepción ("Volver atrás", "Destrabar reserva", "Sacar de viaje"), ¿dónde viven?**

**Contexto:** son correcciones de último recurso: piden permiso, motivo obligatorio y quedan en el historial.
Hoy "Volver atrás" es un botón ámbar del mismo tamaño que "Anular reserva", al lado de las acciones de todos
los días. La regla dice que tienen que ser **discretas, nunca un botón normal** — pero no dice dónde.

**A) Detrás de un botón "⋯" al final de la fila de acciones.**

```
  [ Anular reserva ]  [ Archivar ]  [ ⋯ ]
                                     └─ Volver atrás
                                        Destrabar reserva
```

**B) En un renglón chiquito debajo de la botonera.**

```
  [ Anular reserva ]  [ Archivar ]
  Correcciones: Volver atrás · Destrabar reserva
```

**C) Quedan como botones normales, como hoy.**

> **Recomiendo A.** Es lo mismo que ya hiciste con las copias de seguridad (una acción principal grande y
> las raras chiquitas al costado): lo que se usa todos los días manda, y lo que se usa una vez cada tanto
> no compite. El "⋯" se abre en un clic, así que no esconde nada.

---

> ✅ **RESPONDIDA: A** (03/08/2026). **Grande solo el número que tiene plata**; los que están en cero, línea
> chiquita gris. Se va el puntito rojo que latía.

**P10. Los tres números grandes (Saldo a cobrar · Recaudado · Inversión). En una reserva sin plata son tres
"$ 0,00" gigantes. ¿Cómo los mostramos?**

**Contexto:** los tres números están firmados y no se discuten. Lo que pregunto es el **tamaño**: hoy los tres
tienen el mismo peso siempre, así que una ficha vacía grita tres ceros.

**A) El número que tiene plata va grande; el que está en cero, chiquito y gris.**

```
  Presupuesto sin plata:      Saldo a cobrar $0,00 · Recaudado $0,00 · Inversión $0,00
                              ────────────── una línea fina, gris ──────────────

  Confirmada con deuda:       SALDO A COBRAR
                              $ 212.000,00                 ← este sí, grande
                              de $ 212.000,00 presupuestado
                              Recaudado $0,00 · Inversión $173.000,00   ← chiquitos
```

**B) Los tres siempre del mismo tamaño, pero la mitad de grandes que hoy.**

```
  SALDO A COBRAR        RECAUDADO        INVERSIÓN (COSTO)
  $ 212.000,00          $ 0,00           $ 173.000,00
```

**C) Quedan como están hoy.**

> **Recomiendo A.** Es lo que decís vos: un número en cero no puede dominar la pantalla. Y el saldo a
> cobrar de una Confirmada **sí** tiene que gritar. De paso saco el puntito rojo que late al lado del
> saldo: parpadea al lado de los avisos de verdad y le come atención.

---

> ✅ **RESPONDIDA: A** (03/08/2026). El aviso que **pide hacer algo** va con color (ámbar); el que **solo
> informa** va **gris y en una sola línea**. Es la primera regla escrita de la sección "Colores y estilo",
> que estaba vacía.

**P11. Los avisos de la ficha: hoy el del candado y el de "1 servicio sin confirmar" son del mismo amarillo,
uno arriba del otro. ¿Los diferenciamos?**

**Contexto:** son dos cosas distintas. El del candado **te pide hacer algo** (destrabar); el de los servicios
sin confirmar **te avisa** de algo que ya sabés. Hoy se ven iguales, y por eso parecen una pila de carteles
amarillos. (Cuántos avisos hay y en qué orden ya está firmado y no lo toco: acá pregunto solo por el color
y el peso.)

**A) El que pide acción va con color; el que solo informa va gris, en una línea.**

```
  🔒 Reserva confirmada. Para cambiar algo, pedí autorización.   [ Destrabar ]   ← ámbar
  ─────────────────────────────────────────────────────────────────────────────
  1 servicio sin confirmar: Aéreo                                [ Ver ]        ← gris, una línea
```

**B) Los dos con color, pero distinto: el que pide acción en ámbar y el que informa en celeste.**

```
  🔒 Reserva confirmada. Para cambiar algo, pedí autorización.   [ Destrabar ]   ← ámbar
  ℹ 1 servicio sin confirmar: Aéreo                              [ Ver ]        ← celeste
```

**C) Quedan los dos amarillos, como hoy.**

> **Recomiendo A.** El gris es el que menos ruido mete y deja el color para lo que de verdad te frena.
> Además el aviso informativo pasa de tres renglones a uno: lo que dice hoy ("estos servicios todavía
> no tienen respuesta del proveedor, resolvelos en la pestaña Servicios") ya lo sabés con solo leer el título.

---

> ✅ **RESPONDIDA: A** (03/08/2026). **Una sola solapa "Documentos"** con dos bloques adentro:
> **"Vouchers del viaje"** y **"Archivos de la reserva"**. La solapa "Vouchers" desaparece.

**P12. "Vouchers" y "Documentos" son dos solapas que hacen casi lo mismo. ¿Las juntamos?**

**Contexto:** verificado en la pantalla: la solapa **Vouchers** se titula "Documentación", dice que maneja
"vouchers y archivos cargados externamente" y su botón es "Añadir Documento". La solapa **Documentos** es donde
arrastrás archivos. Dos puertas para lo mismo.

**A) Una sola solapa "Documentos", con dos bloques adentro.**

```
   Servicios | Pasajeros (1) | Historial | Estado de Cuenta | Documentos
   ────────────────────────────────────────────────────────────────────
   VOUCHERS DEL VIAJE
   Hotel RIU Cancún   emitido 10/07   [ Ver ] [ Descargar ] [ Enviar al pasajero ]

   ARCHIVOS DE LA RESERVA                              [ + Subir archivo ]
   pasaporte-garcia.pdf   subido por Maite el 12/07    [ Ver ] [ Descargar ]
```

**B) Quedan las dos, pero cada una hace solo lo suyo:**
Vouchers = los que emite el sistema. Documentos = los archivos que subís vos.

```
   ... | Vouchers | Documentos
        └ solo vouchers, sin botón de subir archivos
                     └ solo archivos subidos
```

**C) Quedan como están hoy.**

> **Recomiendo A.** Cuando buscás "el papel del hotel" no pensás si lo emitió el sistema o lo subiste vos:
> pensás "los papeles de esta reserva". Una sola puerta, dos bloques adentro, y una solapa menos en una
> ficha que ya tiene seis.

---

## Tema D — Una palabra

> ✅ **RESPONDIDA: A** (03/08/2026). Queda **"SOLICITADO"**, en todas las etapas. "En espera" desaparece.

**P13. Firmaste unificar "EN ESPERA" y "SOLICITADO" en una sola etiqueta. ¿Cuál queda?**

**Contexto:** hoy el mismo servicio dice **"En espera"** mientras la reserva es presupuesto y **"Solicitado"**
cuando pasa a En gestión. Es el mismo servicio en el mismo lugar cambiando de nombre solo, y confunde.

**A) Queda "Solicitado".**

```
  ✈ AEREO   AEP-MDZ    10/08/2026   [ SOLICITADO ]
```

**B) Queda "En espera".**

```
  ✈ AEREO   AEP-MDZ    10/08/2026   [ EN ESPERA ]
```

**C) Queda "Pendiente".**

```
  ✈ AEREO   AEP-MDZ    10/08/2026   [ PENDIENTE ]
```

> **Recomiendo A: "Solicitado".** Es la que más se ve hoy (todo el trabajo real pasa de En gestión en
> adelante) y es la que dice **qué pasó**: se le pidió al operador. "En espera" y "Pendiente" describen
> una sensación, no un hecho.

---

> ✅ **RESPONDIDA: A** (03/08/2026). Queda **solo arriba**, en la botonera de la reserva. Dentro de "Estado
> de cuenta" quedan **"Registrar cobro"** y **"Emitir factura"**.

**P14. "Anular reserva" aparece dos veces en la misma ficha: arriba en la botonera y otra vez adentro de la
solapa Estado de Cuenta. ¿Dejamos una sola?**

```
  hoy:   Reserva #F-2026-1064  [CONFIRMADA]        [ 🚫 Anular reserva ]  [ Archivar ]
         ...
         Estado de Cuenta →  [ + Registrar cobro ] [ Emitir factura ] [ 🚫 Anular reserva ]
                                                                        ↑ el mismo botón otra vez
```

**A) Queda solo arriba, en la botonera de la reserva.**

```
  Estado de Cuenta →  [ + Registrar cobro ]  [ Emitir factura ]
```

**B) Queda solo adentro de Estado de Cuenta (es donde está la plata).**

```
  Reserva #F-2026-1064  [CONFIRMADA]        [ Archivar ]
```

**C) Quedan las dos, como hoy.**

> **Recomiendo A.** Anular es una decisión sobre **toda la reserva**, no sobre su cuenta: va con las
> acciones de la reserva. Y para algo que deshace un viaje, dos botones rojos en la misma pantalla es
> una invitación a apretar el equivocado.

---

---

# PREGUNTAS NUEVAS (03/08/2026, tarde) — se abrieron al dibujar lo que faltaba

> Con tus 14 respuestas dibujé las cuatro pantallas que faltaban (Historial, Estado de cuenta, Documentos
> y Pasajeros) y aparecieron **tres decisiones que ninguna regla cubre**. Son cortitas.
> Podés contestar: "15A, 16B, 17 otra cosa: …".

---

## Tema E — El historial de la reserva

**Contexto:** me dijiste que el historial *"no es muy clara, parece más de programador que de usuario de
agencia de viajes"*. Lo reescribí entero en criollo del negocio: en vez de "Cambio en una Factura por
Sistema" ahora dice "Maite emitió la Factura B 0003-00001234 por $ 212.000,00", agrupado por día. Eso ya
está dibujado. Lo que falta decidir es **cuánto se cuenta**.

**P15. ¿Qué entra en el historial de una reserva?**

**A) Todo lo que pasa, en una sola lista** (cobros, facturas, servicios, cambios de estado, ediciones de
fechas, pasajeros cargados, archivos subidos…).

```
  Hoy — 03/08/2026
   • 14:32  Maite anuló el traslado "Aeropuerto → Hotel" — Motivo: el cliente se arrepintió
   • 11:05  Maite cobró $ 50.000,00 · Forma de pago: Efectivo
   • 10:58  Maite cambió la fecha de salida: 08/12/2026 → 10/12/2026
   • 10:52  Maite cargó al pasajero Juan Pérez
   • 10:50  Maite subió el archivo "pasaporte-perez.pdf"
```

**B) Todo, pero con un filtro arriba para mirar de a un tema.**

```
  Historial       [ Todo ▾ ]  ← Todo · Plata · Servicios · Pasajeros y papeles · Estado de la reserva
  Hoy — 03/08/2026
   • 11:05  Maite cobró $ 50.000,00 · Forma de pago: Efectivo
```

**C) Solo lo grueso** (plata, facturas, servicios y cambios de estado). Las ediciones chicas — una fecha
corregida, un pasajero editado, un archivo subido — no se muestran.

```
  Hoy — 03/08/2026
   • 14:32  Maite anuló el traslado "Aeropuerto → Hotel" — Motivo: el cliente se arrepintió
   • 11:05  Maite cobró $ 50.000,00 · Forma de pago: Efectivo
```

> **Recomiendo B.** El historial sirve para dos cosas distintas: "¿quién tocó esto?" (ahí querés ver
> **todo**, y esconder cosas te deja sin respuesta justo cuando la necesitás) y "¿cómo venimos con la
> plata?" (ahí querés ver **poco**). El filtro te da las dos sin sacar nada: entrás y ves todo, y si el
> historial es largo, elegís el tema. **C** es la que me preocupa: el día que un pasajero reclame "yo puse
> bien la fecha", el rastro tiene que estar.

---

## Tema F — Pasar de borrador a presupuesto

**Contexto:** con tu respuesta P1=C, la reserva nace **Borrador** y adentro tiene el botón **"Pasar a
presupuesto"**. Falta decidir si ese botón deja pasar siempre o pide algo antes (como "El cliente aceptó",
que pide un servicio cargado y el titular con nombre).

**P16. ¿Qué le tiene que faltar a un borrador para NO poder pasar a presupuesto?**

**A) Nada: se puede pasar siempre**, aunque esté vacío.

```
  Reserva #F-2026-1066  [ BORRADOR ]        [ Pasar a presupuesto ]  ← siempre encendido
```

**B) Tiene que tener al menos un servicio cargado.**

```
  Reserva #F-2026-1066  [ BORRADOR ]        [ Pasar a presupuesto ]  ← apagado
                                            Cargá al menos un servicio → [ Agregar servicio ]
```

**C) Tiene que tener al menos un servicio Y el cliente elegido** (hoy el cliente se elige al crear, así
que en la práctica es lo mismo que B más una red).

```
  Reserva #F-2026-1066  [ BORRADOR ]        [ Pasar a presupuesto ]  ← apagado
                                            Falta: elegir el cliente · cargar un servicio
```

> **Recomiendo B.** Un presupuesto es **algo que le mandás al cliente**: un presupuesto sin ni un servicio
> es una hoja en blanco con membrete. Pedir un servicio es el mínimo que hace que el documento signifique
> algo, y no agrega ningún paso al que ya está trabajando. El titular **no** se pide acá (eso recién lo
> pide "El cliente aceptó", como ya está firmado).

---

## Tema G — Un archivo subido por error

**Contexto:** firmaste que **nada importante se borra**. Pero en la solapa Documentos el vendedor sube
archivos a mano, y a veces sube el que no era (el DNI de otro pasajero, un PDF cortado). Hoy hay un tacho
que borra el archivo y no queda rastro.

**P17. ¿Qué pasa cuando alguien sube un archivo equivocado?**

**A) Se puede sacar de la lista, y queda anotado quién lo sacó y cuándo** (el archivo deja de mostrarse,
pero el rastro queda en el historial).

```
  pasaporte-perez.pdf   subido por Maite el 12/07/2026     [ Ver ] [ Descargar ] [ Quitar ]
  ─────────────────────────────────────────────────────────────────────────────────
  Historial →  12/07/2026 10:52  Maite quitó el archivo "pasaporte-perez.pdf"
```

**B) No se saca nada: queda tachado a la vista**, como los servicios anulados.

```
  ̶p̶a̶s̶a̶p̶o̶r̶t̶e̶-̶p̶e̶r̶e̶z̶.̶p̶d̶f̶   Quitado por Maite el 12/07/2026        [ Ver ] [ Descargar ]
```

**C) Se borra de verdad, como hoy** (los archivos sueltos no son documentos del negocio).

> **Recomiendo A.** Un archivo mal subido **sí** conviene poder sacarlo de la vista (si queda un pasaporte
> ajeno colgado, alguien lo va a mandar por error). Pero el rastro de que estuvo y quién lo sacó tiene que
> quedar, que es el espíritu de "nada se borra". **B** llenaría la lista de basura tachada, y **C** rompe
> la regla que firmaste hoy.

---

## Qué pasa después

1. Las 14 respuestas ya están escritas como reglas en `docs/ux/guia-ux-gaston.md` (con fecha, por tema) y
   dibujadas en `docs/ux/maquetas/2026-08-03-reservas-rediseno.html`.
2. Faltan P15, P16 y P17 para poder construir el Historial, el paso de borrador a presupuesto y la solapa
   Documentos completa.
3. Recién después se construye. El OK final es tuyo, mirando la pantalla real.

**Lo que NO espera respuesta y se puede arreglar ya** (son reglas ya firmadas que hoy están rotas): las
etiquetas de operador que muestran montos, los íconos sin palabra, el "por Sistema" del historial, el texto
del buscador, el "Regreso" que se copia de "Salida", la botonera viva en una reserva anulada, el doble
selector de cliente, los motivos de botón apagado que viven en el tooltip, y **sacar el botón "Eliminar"
de la ficha del Presupuesto** (firmado hoy: nada se borra).
