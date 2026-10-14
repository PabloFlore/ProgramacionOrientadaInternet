# Music Hub

Red social de música con estética nostálgica de comunidad fan: únete a grupos, completa tareas, colecciona cartas exclusivas y **chatea en tiempo real** con otros dispositivos.

Proyecto integrador de aprendizaje (PIA). Primer avance.

## Tech Stack

- **Backend:** C# / .NET 8 Web API (minimal API) con **SignalR**.
- **Base de datos:** SQLite (Microsoft.Data.Sqlite) con hash + sal para contraseñas.
- **Frontend:** HTML, CSS y JavaScript vanilla servido por el propio backend.
- **Tiempo real:** WebSockets / SignalR para comunicación bidireccional entre dispositivos.

> La carpeta `client/` contiene un experimento anterior en React + Vite que **ya no se usa ni se versiona** (está en `.gitignore`). El frontend canónico es `web/`.

## Estructura del proyecto

| Ruta | Descripción |
|---|---|
| `web/LandingPage.html` + `LandingPage.css` | Portada con navbar y hero. |
| `web/IniciarSesion.html` | Inicio de sesión (por correo). |
| `web/RegistrarUsuario.html` | Alta de cuenta con requisitos de contraseña. |
| `web/Formularios.css` + `ValidacionRegistro.js` | Estilos compartidos y validaciones de los formularios. |
| `web/Home.html` + `Home.css` + `Home.js` | App principal: lista de chats, mensajes, tareas pendientes y colección de cartas. |
| `web/CambiarCuenta.html` + `.css` + `.js` | Edición de perfil y cambio de contraseña. |
| `web/libs/signalr.min.js` | Cliente de SignalR. |
| `server/ChatServer/` | Backend .NET 8: hub, endpoints y acceso a SQLite. |
| `server/ChatServer/Hubs/ChatHub.cs` | Methods de SignalR (unirse a canal, enviar). Resuelve la identidad por token. |
| `server/ChatServer/UsuariosBd.cs` | Tabla y operaciones de usuarios. |
| `server/ChatServer/MensajesBd.cs` | Tabla y operaciones de mensajes, conversaciones privadas. |
| `server/ChatServer/SesionesBd.cs` + `sesion.cs` | Tabla de sesiones con token opaco. |
| `server/ChatServer/conversacion.cs` | Modelo de conversación privada (último mensaje y el otro usuario). |

## Cómo correr

```bash
cd server/ChatServer
dotnet run
```

El backend sirve **la API y el frontend** en `http://0.0.0.0:5000`.

- Desde la misma PC: <http://localhost:5000>
- Desde el celular u otra PC: `http://<IP-del-servidor>:5000`

Endpoint del Hub de SignalR: `http://<IP>:5000/chat`.

> El `.csproj` usa `<RollForward>Major</RollForward>` porque en la máquina está instalado el SDK de .NET 10 (el proyecto apunta a `net8.0`).

## Flujo de uso

1. Abrir `http://<IP>:5000/RegistrarUsuario.html` y crear una cuenta.
2. Al registrarse se redirige al login con el correo ya escrito.
3. Iniciar sesión con **correo + contraseña**. `Home.html` exige sesión: sin ella redirige al login.
4. El nombre que aparece en el chat **no se escribe a mano**: sale de la cuenta con la que iniciaste sesión.
5. Para escribir en un canal, entra a `amigos`, `gaming`, `universidad` o `proyecto`.
6. Para un **chat privado**, pulsa «Nuevo mensaje privado», elige un contacto y se abre la conversación con esa persona.
7. Para cambiar de cuenta: botón de salida de sesión en `Home.html`, o el enlace a `CambiarCuenta.html`.

Cada pestaña guarda su propia sesión (`sessionStorage`), así que puedes usar **dos cuentas distintas en dos pestañas del mismo navegador**.

## Autenticación

- Las contraseñas **no se guardan en texto plano**: se almacena un hash con sal por usuario (`UsuariosBd`).
- El login es **por correo**, que es único. El campo `Usuario` puede repetirse.
- Al iniciar sesión el backend emite un **token opaco** (32 bytes aleatorios en hex) que se guarda en la tabla `Sesiones`. La respuesta del login incluye `Id` y `Token`.
- El cliente envía el token:
  - a la **API** como `Authorization: Bearer <token>`;
  - al **hub SignalR** como `?access_token=<token>` (el navegador no puede poner cabeceras en el WebSocket; clientes no-navegador pueden usar `Authorization`).
- `POST /api/logout` elimina el token, así que queda inválido de inmediato.
- La API de cuenta **ya no confía en el cliente**: el usuario se resuelve siempre desde el token, no desde un `CorreoActual` en el cuerpo.
- La contraseña solo es obligatoria en `CambiarCuenta` si se quiere cambiar; para editar nombre, apellido, usuario o correo no hace falta.
- La sesión vive en `sessionStorage` bajo la clave `musicHubUsuario` (incluye `Id` y `Token`).

## Endpoints

Todos los endpoints marcados con 🔒 requieren `Authorization: Bearer <token>`; sin token válido responden `401`.

| Método | Ruta | Entrada | Respuesta |
|---|---|---|---|
| `POST` | `/api/registrar` | `Nombre`, `Apellido`, `Usuario`, `Correo`, `Contrasena` | `200` con los datos del usuario · `400` campos vacíos, correo inválido o contraseña débil · `409` correo duplicado |
| `POST` | `/api/login` | `Correo`, `Contrasena` | `200` con `Id`, `Nombre`, `Apellido`, `Usuario`, `Correo` y `Token` · `401` correo o contraseña incorrectos |
| `POST` | `/api/logout` 🔒 | — | `200`; invalida el token de la sesión |
| `GET` | `/api/usuarios` 🔒 | — | `200` con los demás usuarios (`Id`, `Nombre`, `Usuario`) para iniciar un chat privado |
| `GET` | `/api/conversaciones` 🔒 | — | `200` con las conversaciones privadas del usuario y su último mensaje |
| `GET` | `/api/mensajes?chatId=…&limite=…` 🔒 | query `chatId` (obligatorio), `limite` (opcional, 100 por defecto, máx. 500) | `200` con `ChatId`, `Total` y `Mensajes` (incluye `UsuarioId`) · `403` si no perteneces a la conversación privada |
| `PATCH` | `/api/cuenta` 🔒 | `Nombre`, `Apellido`, `Usuario`, `Correo` | `200` con el perfil actualizado · `400` datos inválidos · `409` correo en uso |
| `PATCH` | `/api/cuenta/contrasena` 🔒 | `ContrasenaActual`, `ContrasenaNueva` | `200` · `400` contraseña débil · `401` contraseña actual incorrecta |

**Reglas de contraseña:** mínimo 8 caracteres, al menos una mayúscula, un número y un símbolo.

## Hub de SignalR (`/chat`)

La conexión **exige token válido**: en `OnConnectedAsync` se lee el token (`access_token` o `Authorization`) y se resuelve el usuario; si no es válido, el servidor cierra la conexión. La identidad del emisor la pone el **servidor** a partir de la sesión, nunca el cliente.

| Método | Parámetros | Qué hace |
|---|---|---|
| `JoinGroup` | `chatId` | Une la conexión a un canal. En conversaciones privadas verifica que seas participante. |
| `LeaveGroup` | `chatId` | Sale de un canal. |
| `SendToGroup` | `chatId`, `message` | **Guarda el mensaje en SQLite ligado al perfil autenticado y lo difunde solo a ese canal.** El nombre no se recibe: lo pone el servidor. |

El evento que escucha el cliente es `ReceiveMessage` y llega con cuatro argumentos:
`usuario`, `mensaje`, `hora` y **`chatId`**.

Los canales públicos de la barra lateral son `amigos`, `gaming`, `universidad` y `proyecto`; cualquiera que entre ve lo que ahí se escribió.

### Chats privados

- Un chat privado se identifica con el id `dm:{idMenor}:{idMayor}` (los dos ids de usuario ordenados).
- `GET /api/usuarios` lista los contactos para iniciar una conversación.
- `GET /api/conversaciones` devuelve tus conversaciones privadas con el último mensaje y los datos del otro usuario.
- Solo los **dos participantes** pueden unirse al grupo y leer el historial: `JoinGroup`, `SendToGroup` y `GET /api/mensajes` comprueban la pertenencia (`403`/`HubException` si no).

## Base de datos

SQLite en `chat.db`, junto al binario compilado
(`server/ChatServer/bin/Debug/net8.0/chat.db`). El archivo está en `.gitignore`.

**Tabla `Usuarios`**

| Columna | Nota |
|---|---|
| `Id` | primary key autoincremental |
| `Nombre`, `Apellido`, `Usuario` | texto |
| `Correo` | **único** |
| `HashContrasena`, `Sal` | hash y sal por usuario |
| `FechaRegistro` | ISO 8601 |

**Tabla `Mensajes`**

| Columna | Nota |
|---|---|
| `Id` | primary key autoincremental |
| `ChatId` | canal (`amigos`, `gaming`, …) o chat privado (`dm:{a}:{b}`) |
| `UsuarioId` | id del usuario que lo envió (columna añadida; las filas antiguas quedan en `NULL`) |
| `Usuario` | nombre completo de quien lo envió (copiado al escribir) |
| `Mensaje` | texto |
| `FechaEnvio` | ISO 8601 |

Índice `IX_Mensajes_Chat` sobre `(ChatId, Id)` para que el historial de un canal se lea rápido.

**Tabla `Sesiones`**

| Columna | Nota |
|---|---|
| `Token` | primary key: token opaco (hex de 32 bytes aleatorios) |
| `UsuarioId` | usuario dueño de la sesión |
| `CreadaEn` | ISO 8601 |

Índice `IX_Sesiones_Usuario` sobre `(UsuarioId)`. Al hacer logout se borra la fila, por lo que el token deja de ser válido.

> `Mensajes.Usuario` guarda el **nombre completo** al momento de escribir. `Mensajes.UsuarioId` es la referencia estable al perfil autenticado: con él se filtran las conversaciones privadas y se comprueba la pertenencia.

## Notas de implementación

- **Caché desactivada en desarrollo.** El backend responde `Cache-Control: no-store` a todo lo que no sea `/chat`, porque si no el navegador y el celular siguen mostrando el HTML/JS viejo aunque el código ya haya cambiado.
- **JSON con los nombres de C#.** Se configuró `PropertyNamingPolicy = null` para que las respuestas salgan como `Nombre`, `Correo`, `Mensajes` y no en camelCase: JavaScript distingue mayúsculas y el frontend está escrito con esos nombres.
- **Estáticos fuera del `.db`.** El backend sirve `web/` con un `FileProvider` propio, no la raíz del proyecto, para que el `chat.db` (correos, hashes y sales) no quede accesible por HTTP.
- **CORS permisivo** para poder probar entre dispositivos en la red local.

## Probar entre dos dispositivos

1. Averigua la IP del equipo con el servidor: `ipconfig` → IPv4 (ej. `192.168.1.50`).
2. Ambos dispositivos deben estar en la **misma red Wi-Fi**. Si uno está en datos móviles, verá rangos de IP parecidos (los dos `192.168.x.x`) pero **no se van a alcanzar**: por eso puede parecer que funciona y luego no.
3. Dispositivo A: abre `http://192.168.1.50:5000/RegistrarUsuario.html`, crea su cuenta e inicia sesión.
4. Dispositivo B: misma URL, con **otra cuenta** (el correo debe ser distinto).
5. Entren al **mismo canal** y escriban: los mensajes se ven al instante.
6. Para un **chat privado**: en un dispositivo pulsa «Nuevo mensaje privado», elige al otro usuario; ambos verán la conversación en su lista de mensajes privados, aunque la otra cuenta abra la sesión en otro dispositivo.
7. Prueben recargar la página: el historial sigue ahí porque se lee de la base de datos.

En una sola PC se puede con dos pestañas del mismo navegador (cada una con su propia sesión).

### Notas sobre firewall

Windows Firewall puede bloquear el puerto `5000`. Acepta el permiso cuando lo pida, o agrega una regla de entrada TCP al puerto `5000` con perfil **Privado**. El equipo debe tener la red marcada como «red privada» para que la regla aplique.

## Pruebas realizadas

Verificado contra el backend en ejecución (pruebas de integración automatizadas con el cliente Node de SignalR):

- ✅ Alta de cuentas y validación de correo duplicado (`409`), contraseña débil (`400`) y correo inválido (`400`).
- ✅ Inicio de sesión correcto y rechazo de contraseña incorrecta (`401`); el login devuelve `Id` y `Token`.
- ✅ La API de mensajes responde `401` sin token y `200` con token.
- ✅ `GET /api/usuarios` no incluye al propio usuario.
- ✅ Conexiones SignalR con token válido permanecen activas; una conexión **sin token** se rechaza.
- ✅ Mensajería en tiempo real entre dos cuentas en el mismo canal, con el nombre puesto por el servidor.
- ✅ Cada mensaje queda persistido con el `UsuarioId` del perfil autenticado.
- ✅ Chat privado: el destinatario recibe el DM en tiempo real; un tercero recibe `403` al leer el historial y no puede unirse al grupo.
- ✅ `GET /api/conversaciones` devuelve la conversación con su último mensaje y el otro usuario.
- ✅ `POST /api/logout` invalida el token (deja de servir para la API).

Pendiente de probar:

- ⬜ Comportamiento con redes distintas (móvil con datos).
- ⬜ Flujo de `CambiarCuenta` desde el navegador.

## Limitaciones conocidas

- El token es opaco y **no caduca** por sí solo: solo deja de valer al hacer logout (no hay expiración ni renovación).
- Sin HTTPS, sin límite de intentos de inicio de sesión y sin protección contra spam.
- El historial se limita a los últimos 100 mensajes por chat.
- Un canal público es visible para cualquiera que haya iniciado sesión; solo las conversaciones privadas restringen el acceso a sus dos participantes.
- `Mensajes.Usuario` guarda el nombre **tal como estaba** al escribir: si el usuario cambia su nombre, los mensajes ya enviados conservan el anterior.
