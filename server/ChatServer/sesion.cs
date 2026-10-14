namespace ChatServer.Modelos;

public class Sesion
{
    public string Token { get; set; } = "";

    public int UsuarioId { get; set; }

    public DateTime CreadaEn { get; set; }
}
