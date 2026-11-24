using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using ChatServer.Datos;
using ChatServer.Llamadas;
using ChatServer.Modelos;

namespace ChatServer.Hubs;

/// <summary>
/// Hub de chat. La identidad ya no la pone el cliente: en la conexión
/// se lee el token (access_token) y se resuelve el usuario. A partir de
/// ahí los mensajes se guardan ligados al perfil autenticado.
/// </summary>
public class ChatHub : Hub
{
    private const string ClaveUsuarioId = "usuarioId";
    private const string ClaveNombre = "nombre";
    private const string ClaveToken = "token";

    private readonly LlamadasServicio _llamadas;

    public ChatHub(LlamadasServicio llamadas)
    {
        _llamadas = llamadas;
    }

    /* Conexiones abiertas por usuario (una por pestaña). Permite a la API
       sacar a un usuario de un grupo en todas sus pestañas y avisarle. */
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>>
        ConexionesPorUsuario = new();

    /* Una sesión puede estar abierta en varias pestañas. Se guarda el token
       de cada conexión para poder avisar solo a las que se revocaron. */
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>
        ConexionesPorToken = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> ConexionesDe(int usuarioId)
    {
        return ConexionesPorUsuario.TryGetValue(usuarioId, out var conexiones)
            ? conexiones.Keys.ToList()
            : new List<string>();
    }

    /// <summary>Avisa a las pestañas que usaban un token ya revocado.</summary>
    public static async Task NotificarRevocacion(
        IHubContext<ChatHub> hub,
        string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            !ConexionesPorToken.TryGetValue(token, out var conexiones) ||
            conexiones.IsEmpty)
        {
            return;
        }

        await hub.Clients.Clients(conexiones.Keys.ToList())
            .SendAsync("SesionRevocada");
    }

    public static bool EstaConectado(int usuarioId)
    {
        return ConexionesPorUsuario.TryGetValue(usuarioId, out var conexiones) &&
               !conexiones.IsEmpty;
    }

    public override async Task OnConnectedAsync()
    {
        var token = LeerToken(Context.GetHttpContext());

        if (!EsTokenValido(token))
        {
            Context.Abort();
            return;
        }

        var usuarioId = SesionesBd.ObtenerUsuarioId(token);

        if (usuarioId is null)
        {
            Context.Abort();
            return;
        }

        var usuario = UsuariosBd.BuscarPorId(usuarioId.Value);

        if (usuario is null)
        {
            Context.Abort();
            return;
        }

        Context.Items[ClaveUsuarioId] = usuario.Id;
        Context.Items[ClaveNombre] = usuario.NombreCompleto;
        Context.Items[ClaveToken] = token;

        var conexiones = ConexionesPorUsuario
            .GetOrAdd(usuario.Id, _ => new ConcurrentDictionary<string, byte>());
        var eraVacio = conexiones.IsEmpty;
        conexiones.TryAdd(Context.ConnectionId, 0);
        ConexionesPorToken
            .GetOrAdd(token, _ => new ConcurrentDictionary<string, byte>())
            .TryAdd(Context.ConnectionId, 0);

        if (eraVacio)
        {
            await Clients.Others.SendAsync("UsuarioConectado", usuario.Id);
        }

        await base.OnConnectedAsync();
    }

    /* El navegador no puede poner cabeceras en el WebSocket, así que manda
       el token como ?access_token=…; clientes como Node lo mandan en
       Authorization: Bearer. Se admiten las dos formas, pero Bearer manda.
       Solo se usa en este hub, nunca en la API REST. */
    private static string LeerToken(HttpContext? http)
    {
        if (http is null)
        {
            return "";
        }

        var encabezado =
            http.Request.Headers.Authorization.ToString();

        if (encabezado.StartsWith(
                "Bearer ",
                StringComparison.OrdinalIgnoreCase))
        {
            return encabezado.Substring("Bearer ".Length).Trim();
        }

        return http.Request.Query["access_token"].ToString();
    }

    /* Los tokens se emiten como 32 bytes en hexadecimal (64 caracteres). */
    private static bool EsTokenValido(string? token)
    {
        if (token is null || token.Length != 64)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!((c >= '0' && c <= '9') ||
                  (c >= 'a' && c <= 'f') ||
                  (c >= 'A' && c <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    /* Se comprueba la sesión en CADA llamada, no solo al conectar: si el
       token se revocó (logout, cambio de contraseña o caducidad), el socket
       puede seguir abierto pero ya no puede enviar nada. */
    private int UsuarioAutenticado()
    {
        if (Context.Items.TryGetValue(ClaveUsuarioId, out var valor) &&
            valor is int id &&
            Context.Items.TryGetValue(ClaveToken, out var datoToken) &&
            datoToken is string token &&
            SesionesBd.ObtenerUsuarioId(token) == id)
        {
            return id;
        }

        throw new HubException("Sesión no válida.");
    }

    public async Task JoinGroup(string chatId)
    {
        var usuarioId = UsuarioAutenticado();

        ValidarAccesoChat(chatId, usuarioId);

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            chatId
        );
    }

    public async Task LeaveGroup(string chatId)
    {
        UsuarioAutenticado();

        if (!MensajesBd.EsChatValido(chatId))
        {
            throw new HubException("El chat no es válido.");
        }

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            chatId
        );
    }

    /* Envía a los demás integrantes del canal chatId. No se envía al
       emisor: su navegador ya pintó el mensaje. Sus otras pestañas (misma
       cuenta) sí lo reciben, con usuarioId igual al suyo.
       Primero guarda en SQLite ligado al perfil autenticado: si el
       insert falla, la excepción sube y el cliente NO ve el mensaje. */
    public Task SendToGroup(
        string chatId,
        string message,
        string? tempId = null)
    {
        return EnviarAsync(chatId, message, tempId, false, null, null, null);
    }

    /* Variante cifrada: el servidor recibe ciphertext, IV, la clave
       pública del emisor y la del destinatario usada al cifrar. Guarda
       todo sin poder descifrar nada. SignalR exige todos los argumentos,
       por eso es un método aparte. */
    public Task SendToGroupCifrado(
        string chatId,
        string message,
        string? tempId,
        string? iv,
        string? clavePublica,
        string? clavePublicaDestino)
    {
        return EnviarAsync(
            chatId,
            message,
            tempId,
            true,
            iv,
            clavePublica,
            clavePublicaDestino);
    }

    /* Variante para compartir ubicación. Viaja como datos estructurados
       (tipo, latitud, longitud y etiqueta), nunca como texto plano con
       coordenadas. El cliente pinta un mapa y un enlace. */
    public async Task SendToGroupUbicacion(
        string chatId,
        double latitud,
        double longitud,
        string? etiqueta,
        string? tempId = null)
    {
        var usuarioId = UsuarioAutenticado();

        ValidarAccesoChat(chatId, usuarioId);

        if (!double.IsFinite(latitud) || latitud < -90 || latitud > 90)
        {
            throw new HubException("La latitud no es válida.");
        }

        if (!double.IsFinite(longitud) || longitud < -180 || longitud > 180)
        {
            throw new HubException("La longitud no es válida.");
        }

        var etiquetaFinal = (etiqueta ?? "").Trim();
        if (etiquetaFinal == "")
        {
            etiquetaFinal = "Mi ubicación";
        }

        if (etiquetaFinal.Length > 120)
        {
            throw new HubException(
                "La etiqueta de la ubicación es demasiado larga.");
        }

        var nombre = Context.Items[ClaveNombre] as string ?? "";

        var guardado = MensajesBd.RegistrarUbicacion(
            chatId,
            usuarioId,
            nombre,
            latitud,
            longitud,
            etiquetaFinal);

        await Clients.OthersInGroup(chatId).SendCoreAsync(
            "ReceiveUbicacion",
            new object?[]
            {
                guardado.Usuario,
                chatId,
                usuarioId,
                guardado.FechaEnvio.ToString("o"),
                guardado.Id,
                string.IsNullOrWhiteSpace(tempId) ? null : tempId,
                latitud,
                longitud,
                etiquetaFinal,
                guardado.FechaEnvio.ToLocalTime().ToString("HH:mm")
            }
        );
    }

    private static void ValidarAccesoChat(string chatId, int usuarioId)
    {
        if (!MensajesBd.EsChatValido(chatId))
        {
            throw new HubException("El chat no es válido.");
        }

        if (MensajesBd.EsChatPrivado(chatId))
        {
            var par = MensajesBd.ParsearChatPrivado(chatId);
            if (par is null || (par.Value.A != usuarioId && par.Value.B != usuarioId))
            {
                throw new HubException("No perteneces a esa conversación.");
            }
        }
        else if (!MensajesBd.EsParticipante(chatId, usuarioId))
        {
            throw new HubException("No perteneces a esa conversación.");
        }
    }

    private async Task EnviarAsync(
        string chatId,
        string message,
        string? tempId,
        bool cifrado,
        string? iv,
        string? clavePublica,
        string? clavePublicaDestino)
    {
        var usuarioId = UsuarioAutenticado();

        var texto = (message ?? "").Trim();
        if (texto == "")
        {
            return;
        }

        ValidarAccesoChat(chatId, usuarioId);

        if (cifrado)
        {
            /* El cifrado extremo a extremo solo tiene sentido en un chat
               privado: en un grupo haría falta una clave compartida. */
            if (!MensajesBd.EsChatPrivado(chatId))
            {
                throw new HubException(
                    "El cifrado solo está disponible en conversaciones privadas.");
            }

            if (texto.Length > 20000)
            {
                throw new HubException(
                    "El mensaje cifrado es demasiado largo.");
            }

            iv = (iv ?? "").Trim();
            clavePublica = (clavePublica ?? "").Trim();
            clavePublicaDestino = (clavePublicaDestino ?? "").Trim();

            if (!EsIvValido(iv))
            {
                throw new HubException("El IV del mensaje no es válido.");
            }

            if (!EsClavePublicaValida(clavePublica))
            {
                throw new HubException(
                    "La clave pública del emisor no es válida.");
            }

            if (!EsClavePublicaValida(clavePublicaDestino))
            {
                throw new HubException(
                    "La clave pública del destinatario no es válida.");
            }
        }
        else
        {
            iv = null;
            clavePublica = null;
            clavePublicaDestino = null;
        }

        var nombre = Context.Items[ClaveNombre] as string ?? "";

        var guardado = MensajesBd.Registrar(
            chatId,
            usuarioId,
            nombre,
            texto,
            cifrado,
            iv,
            clavePublica,
            clavePublicaDestino);

        await Clients.OthersInGroup(chatId).SendCoreAsync(
            "ReceiveMessage",
            new object?[]
            {
                guardado.Usuario,
                guardado.Texto,
                guardado.FechaEnvio.ToLocalTime().ToString("HH:mm"),
                chatId,
                usuarioId,
                guardado.FechaEnvio.ToString("o"),
                guardado.Id,
                string.IsNullOrWhiteSpace(tempId) ? null : tempId,
                guardado.Cifrado,
                guardado.Iv,
                guardado.ClavePublica,
                guardado.ClavePublicaDestino
            }
        );
    }

    private static bool EsIvValido(string iv)
    {
        if (string.IsNullOrWhiteSpace(iv))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(iv).Length == 12;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool EsClavePublicaValida(string clave)
    {
        if (string.IsNullOrWhiteSpace(clave) || clave.Length > 512)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(clave);

            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(bytes, out _);

            return ecdh.KeySize == 256;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /* -------------------- Videollamadas 1 a 1 -------------------- */

    /* Inicia una llamada en un chat privado. Devuelve el id de la llamada. */
    public Task<string> IniciarLlamada(string chatId)
    {
        var usuarioId = UsuarioAutenticado();
        var nombre = Context.Items[ClaveNombre] as string ?? "";

        return _llamadas.Iniciar(usuarioId, nombre, (chatId ?? "").Trim());
    }

    public Task ContestarLlamada(string llamadaId)
    {
        return _llamadas.Contestar(llamadaId, UsuarioAutenticado());
    }

    public Task RechazarLlamada(string llamadaId)
    {
        return _llamadas.Rechazar(llamadaId, UsuarioAutenticado());
    }

    /* La página de la llamada se une para recibir la señalización WebRTC. */
    public Task<LlamadaInfo> UnirseLlamada(string llamadaId)
    {
        return _llamadas.UnirseAsync(llamadaId, UsuarioAutenticado(), Context.ConnectionId);
    }

    /* tipo: "listo", "oferta", "respuesta" o "candidato". datos: JSON. */
    public Task EnviarSenalLlamada(string llamadaId, string tipo, string datos)
    {
        return _llamadas.Senal(llamadaId, UsuarioAutenticado(), Context.ConnectionId, tipo, datos);
    }

    public Task ColgarLlamada(string llamadaId)
    {
        return _llamadas.Colgar(llamadaId, UsuarioAutenticado());
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR limpia los grupos automáticamente al desconectarse
        if (Context.Items.TryGetValue(ClaveToken, out var datoToken) &&
            datoToken is string token &&
            ConexionesPorToken.TryGetValue(token, out var porToken))
        {
            porToken.TryRemove(Context.ConnectionId, out _);
            if (porToken.IsEmpty)
            {
                ConexionesPorToken.TryRemove(token, out _);
            }
        }

        if (Context.Items.TryGetValue(ClaveUsuarioId, out var valor) &&
            valor is int usuarioId &&
            ConexionesPorUsuario.TryGetValue(usuarioId, out var conexiones))
        {
            conexiones.TryRemove(Context.ConnectionId, out _);
            if (conexiones.IsEmpty)
            {
                ConexionesPorUsuario.TryRemove(usuarioId, out _);
                await Clients.Others.SendAsync("UsuarioDesconectado", usuarioId);
                _llamadas.UsuarioSinConexion(usuarioId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
