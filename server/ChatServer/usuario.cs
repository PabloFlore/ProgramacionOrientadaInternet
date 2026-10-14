using System.Text.Json.Serialization;

namespace ChatServer.Modelos;

public class Usuario
{
    public int Id { get; set; }

    public string Nombre { get; set; } = "";

    public string Apellido { get; set; } = "";

    [JsonPropertyName("Usuario")]
    public string NombreUsuario { get; set; } = "";

    public string Correo { get; set; } = "";

    public string HashContrasena { get; set; } = "";

    public string Sal { get; set; } = "";

    public DateTime FechaRegistro { get; set; }
}
