namespace ChatServer.Modelos;

/// <summary>
/// Resumen de una conversación privada (chatId con formato
/// dm:{idMenor}:{idMayor}) con su último mensaje.
/// </summary>
public class Conversacion
{
    public string ChatId { get; set; } = "";

    public int OtroUsuarioId { get; set; }

    public string OtroNombre { get; set; } = "";

    public string OtroUsuario { get; set; } = "";

    public string UltimoMensaje { get; set; } = "";

    public DateTime UltimaFecha { get; set; }

    public int? UltimoUsuarioId { get; set; }
}
