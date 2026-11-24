# Music Hub

Aplicación web de chat en tiempo real para una comunidad de fans de la música. Permite crear cuenta, tener mensajes privados 1 a 1 (con videollamada, cifrado extremo a extremo opcional y ubicación compartida con mapa), crear grupos con sus integrantes y un enlace opcional que habilita 3 retos por persona (mensaje, archivo y ubicación), elegir 3 photocards y un enlace de canje, coleccionar 18 photocards de recompensa y editar el perfil. Sin enlace el grupo funciona solo como chat; el enlace puede agregarse después. Corre en una PC y se usa desde navegadores de varios dispositivos conectados a la misma red local.

Proyecto integrador de aprendizaje (PIA), primer avance.

**Documentación completa:** [docs/DOCUMENTACION.md](docs/DOCUMENTACION.md) (arquitectura, back, front, base de datos, API, hub, seguridad, pruebas y decisiones de diseño).

## Tecnologías

- **Backend:** C# con .NET 8, ASP.NET Core (minimal API) y SignalR. Carpeta `server/ChatServer/`.
- **Base de datos:** SQLite en un archivo (`server/ChatServer/data/chat.db`), con Microsoft.Data.Sqlite. Está fuera de `bin/`, así que un rebuild no borra el historial.
- **Frontend:** HTML, CSS y JavaScript vanilla, servido por el propio backend. Carpeta `web/`. El cifrado vive en `web/cifrado.js` (Web Crypto: ECDH P-256 + AES-256-GCM).

## Cómo correr

El servidor solo habla HTTPS, así que primero hay que generar el certificado y confiar en la CA local:

```powershell
cd server\ChatServer
powershell -NoProfile -ExecutionPolicy Bypass -File tools\generar_certificados.ps1 -Ip <IP-de-la-PC>
certutil -user -addstore Root certs\ca-music-hub.cer
dotnet run
```

El script usa el `openssl` de Git. Si la IP de la PC cambia, vuelve a ejecutarlo con la IP nueva (la CA se reutiliza).

El servidor atiende la API, el hub de tiempo real y el frontend en el puerto 5000.

- Desde la misma PC: <https://localhost:5000>
- Desde otro dispositivo de la misma red Wi-Fi: `https://<IP-de-la-PC>:5000` (la IP se obtiene con `ipconfig`).

En cada dispositivo nuevo hay que instalar `certs\ca-music-hub.cer` una vez; sin eso el navegador avisa que la conexión no es privada y bloquea cámara y micrófono (necesarios para la videollamada).

La base viva es `server\ChatServer\data\chat.db`. La copia en `server\ChatServer\bin\Debug\net8.0\chat.db` es el respaldo de la ubicación anterior: se conserva, pero el servidor no la usa. Para ver los datos, abre la de `data/` con DB Browser for SQLite.

Si el dispositivo no puede entrar, revisa el firewall de Windows para el puerto 5000. Detalles en la [documentación, sección 11](docs/DOCUMENTACION.md#11-ejecución-y-uso-entre-varios-dispositivos).

## Pruebas

Con el servidor compilado (`dotnet build`) y la CA instalada, desde `server\ChatServer`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_cuenta.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_grupos.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_tareas_recompensas.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_persistencia.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_llamada.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_cifrado.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_borrado_chat.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_ubicacion.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_archivos.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_correo.ps1
node pruebas\prueba_cifrado_cliente.mjs
```

Cada script de PowerShell arranca una copia del servidor con su propia base de datos, así que no modifica `chat.db`. El de Node prueba WebCrypto sin servidor. Ejecútalos uno después de otro, porque todos los de PowerShell usan el puerto 5000.

`prueba_correo.ps1` levanta smtp4dev desde el manifiesto `dotnet-tools.json` que está en la raíz del repositorio. El script ya lo pasa con `--tool-manifest`; si lo restauras a mano, hazlo desde la raíz con `dotnet tool restore --tool-manifest dotnet-tools.json`.

Para SMTP y Cloudinary, edita `server\ChatServer\appsettings.Development.json`: es el que se usa con `dotnet run` porque `launchSettings.json` define `ASPNETCORE_ENVIRONMENT=Development`. `appsettings.json` conserva los mismos valores como respaldo; no hace falta tocar ambos.

## Actualizaciones recientes

- **Limpieza de código muerto**: se quitaron restos de canales fijos, tareas en `localStorage`, mensajes semilla comentados, estilos huérfanos y métodos sin uso; se aclaró la configuración y el inventario de pruebas sin mover archivos.
- **Retos y recompensas de grupo**: al crear un grupo se puede dar un enlace de recompensas; sin él el grupo es solo chat y el creador puede activar los retos después desde el panel. Con enlace, cada integrante recibe automáticamente 3 retos: enviar un mensaje, subir un archivo y compartir una ubicación. La interfaz busca la evidencia en el chat del grupo y la verifica; al completar los 3 se entregan 2 photocards del plan elegido por el creador. Cada integrante obtiene un token `/r/...` para la tercera y última carta. Las cartas pueden repetirse en otros grupos pero no dentro de uno. Desde el panel del grupo se ve la colección de otro; el panel derecho muestra el nombre y `@usuario` de esa persona y permite volver al perfil propio. Incluye 45 pruebas automatizadas nuevas.
- **Correo desde el chat privado**: botón en la cabecera del DM que abre el modal de envío con el otro usuario fijo.
- **HTTPS local** con CA propia (`tools\generar_certificados.ps1`), necesario para cámara y micrófono.
- **Cifrado extremo a extremo opcional** en chats privados: interruptor por chat, ECDH P-256 + AES-256-GCM en el navegador, clave privada no extraíble en IndexedDB (los pares antiguos se conservan) y ciphertext en SQLite. Incluye candados en la interfaz y 52 casos de prueba nuevos.
- **Videollamada 1 a 1 real** con WebRTC: aviso de llamada entrante, contestar/rechazar, micrófono, cámara y colgar. Solo en chats privados y dentro de la red local.
- **Ubicación compartida** en DMs y grupos: botón de ubicación, tarjeta con mapa interactivo de OpenStreetMap y enlace externo. Las coordenadas nunca se muestran como texto. El botón se deshabilita si el E2EE está activo en el DM.
- **Borrado local de chats**: cada usuario puede vaciar su historial de un DM o grupo sin afectar a los demás. En DMs la conversación desaparece de la lista hasta un mensaje nuevo; los grupos siguen visibles con historial vacío.
- **Canales fijos eliminados** de la barra lateral; quedan mensajes privados y grupos, que vienen del servidor.
- **Base de datos limpia**: sin mensajes ni grupos previos.
- **Conexión automática** con reintentos al iniciar si el servidor no está listo.
- **Envío de correo electrónico** desde la cabecera del chat privado (`POST /api/correo/enviar`); el modal deja fijar al otro usuario o elegir destinatario.
- **Chats privados 1 a 1** completos con historial y conversaciones.
- **Paginación** del historial con "Cargar más mensajes".
- **Agrupado por fecha** (Hoy/Ayer/Fecha) y mejoras en horas.
- **Barra lateral en vivo**: al llegar un mensaje (texto, archivo o ubicación), se actualizan el preview y el orden de DMs y grupos. Los grupos muestran quién envió el último mensaje y qué tipo fue.
- **Presencia online/offline** en bandeja lateral y header del chat privado.
- **Validación reforzada al enviar** mensajes directos en el hub.
- **Deduplicación de mensajes** con `tempId` entre pestañas/reconexiones.
- **Borradores por chat** guardados en `sessionStorage` (se restauran al cambiar de chat).

## Pendientes

- Probar el panel de tareas y la galería de recompensas en un navegador real.
- Alertas de mensajes nuevos por chat (fase 4).
- Archivos: falta audio en DMs y grupos; texto e imagen ya están hechos (fase 5).
- Probar el cifrado en dos navegadores reales y revisar el ciphertext en `chat.db`.
- Instalar la CA en iPhone (formato DER o `.mobileconfig` y confianza total).
- Probar la videollamada con dos navegadores y cámaras reales; hoy solo funciona en la misma red (sin STUN/TURN).
- Probar la interfaz en un navegador real (grupos, pintado de mensajes de otras pestañas, reducción de imágenes).
- Probar el correo contra un servidor SMTP real; las pruebas automatizadas usan smtp4dev.

La lista completa y el estado de cada pantalla están en la [documentación, secciones 1 y 14](docs/DOCUMENTACION.md).
