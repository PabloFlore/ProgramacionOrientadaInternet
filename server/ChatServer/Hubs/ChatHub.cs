using Microsoft.AspNetCore.SignalR;
using ChatServer.Datos;
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

    public override async Task OnConnectedAsync()
    {
        var token = LeerToken(Context.GetHttpContext());

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

        await base.OnConnectedAsync();
    }

    /* El navegador no puede poner cabeceras en el WebSocket, así que manda
       el token como ?access_token=…; clientes como Node lo mandan en
       Authorization: Bearer. Se admiten las dos formas. */
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

    private int UsuarioAutenticado()
    {
        return Context.Items.TryGetValue(ClaveUsuarioId, out var valor) &&
               valor is int id
            ? id
            : throw new HubException("Sesión no válida.");
    }

    public async Task JoinGroup(string chatId)
    {
        var usuarioId = UsuarioAutenticado();

        if (!MensajesBd.EsParticipante(chatId, usuarioId))
        {
            throw new HubException(
                "No perteneces a esa conversación.");
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            chatId
        );
    }

    public async Task LeaveGroup(string chatId)
    {
        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId,
            chatId
        );
    }

    /* Envía solo a los integrantes del canal chatId.
       Primero guarda en SQLite ligado al perfil autenticado: si el
       insert falla, la excepción sube y el cliente NO ve el mensaje. */
    public async Task SendToGroup(string chatId, string message)
    {
        var usuarioId = UsuarioAutenticado();

        var texto = (message ?? "").Trim();
        if (texto == "")
        {
            return;
        }

        if (!MensajesBd.EsParticipante(chatId, usuarioId))
        {
            throw new HubException(
                "No perteneces a esa conversación.");
        }

        var nombre = Context.Items[ClaveNombre] as string ?? "";

        var guardado = MensajesBd.Registrar(
            chatId,
            usuarioId,
            nombre,
            texto);

        await Clients.Group(chatId).SendAsync(
            "ReceiveMessage",
            guardado.Usuario,
            guardado.Texto,
            guardado.FechaEnvio.ToString("HH:mm:ss"),
            chatId
        );
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR limpia los grupos automáticamente al desconectarse
        await base.OnDisconnectedAsync(exception);
    }
}
