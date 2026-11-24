namespace ChatServer.Modelos;

public class Tarea
{
    public int Id { get; set; }
    public int GrupoId { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descripcion { get; set; }
    public int CreadorId { get; set; }
    public int AsignadoId { get; set; }
    public string CreadorNombre { get; set; } = "";
    public string AsignadoNombre { get; set; } = "";
    public string FechaCreacion { get; set; } = "";
    public string? FechaFinalizacion { get; set; }
    public int? FinalizadaPor { get; set; }
    public string FinalizadaPorNombre { get; set; } = "";

    public bool Finalizada => FechaFinalizacion is not null;

    public int CatalogoId { get; set; }
    public string TipoVerificacion { get; set; } = "manual";
    public long? EvidenciaMensajeId { get; set; }
}
