using Microsoft.AspNetCore.SignalR;
using ChatServer.Datos;

namespace ChatServer.Hubs;

public class ChatHub : Hub
{
    /* Broadcast global (lo usa el cliente React Chat.jsx) */
    public async Task SendMessage(string user, string message)
    {
        await Clients.All.SendAsync(
            "ReceiveMessage",
            user,
            message,
            DateTime.Now.ToString("HH:mm:ss")
        );
    }

    /* Canales (privados o grupales) para Home.html */

    public async Task JoinGroup(string chatId)
    {
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
       Primero guarda en SQLite: si el insert falla, la excepción
       sube y el cliente NO ve el mensaje, para que no crea que
       se guardó cuando en realidad no se guardó. */
    public async Task SendToGroup(
        string chatId,
        string user,
        string message)
    {
        MensajesBd.Registrar(chatId, user, message);

        await Clients.Group(chatId).SendAsync(
            "ReceiveMessage",
            user,
            message,
            DateTime.Now.ToString("HH:mm:ss"),
            chatId
        );
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR limpia los grupos automáticamente al desconectarse
        await base.OnDisconnectedAsync(exception);
    }
}
