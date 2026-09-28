namespace ChatServer.Modelos;

public class Mensaje
{
    public long Id { get; set; }

    public string ChatId { get; set; } = "";

    public string Usuario { get; set; } = "";

    public string Texto { get; set; } = "";

    public DateTime FechaEnvio { get; set; }
}
