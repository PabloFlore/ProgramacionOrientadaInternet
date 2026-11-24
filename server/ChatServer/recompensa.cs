namespace ChatServer.Modelos;

public class Recompensa
{
    public int Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Imagen { get; set; } = "";
    public string? OtorgadaEn { get; set; }
    public int? GrupoId { get; set; }
    public int Orden { get; set; }
    public string Origen { get; set; } = "tareas";
}
