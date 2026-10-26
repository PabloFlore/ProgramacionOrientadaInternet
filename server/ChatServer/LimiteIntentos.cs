namespace ChatServer.Datos;

/// <summary>
/// Límite de intentos fallidos de inicio de sesión, por correo. Vive en
/// memoria: se reinicia al reiniciar el servidor (aceptable para este
/// proyecto). Tras MaximoFallos fallos dentro de la Ventana, el correo
/// queda bloqueado hasta que los fallos antiguos salgan de la ventana.
/// </summary>
public static class LimiteIntentos
{
    private const int MaximoFallos = 5;
    private static readonly TimeSpan Ventana = TimeSpan.FromMinutes(15);

    private static readonly Dictionary<string, List<DateTime>> Fallos = new();
    private static readonly object Candado = new();

    public static bool Bloqueado(string clave)
    {
        lock (Candado)
        {
            return FallosRecientes(clave).Count >= MaximoFallos;
        }
    }

    public static void RegistrarFallo(string clave)
    {
        lock (Candado)
        {
            /* Barre las claves vencidas: un correo que no vuelve a
               intentar entrar no debe quedarse en memoria para siempre. */
            foreach (var otra in Fallos.Keys.ToList())
            {
                FallosRecientes(otra);
            }

            var lista = FallosRecientes(clave);
            lista.Add(DateTime.UtcNow);
            Fallos[clave] = lista;
        }
    }

    public static void Limpiar(string clave)
    {
        lock (Candado)
        {
            Fallos.Remove(clave);
        }
    }

    /* Llamar siempre dentro del candado. Quita los fallos vencidos y
       elimina la clave si ya no tiene ninguno (evita crecer sin límite). */
    private static List<DateTime> FallosRecientes(string clave)
    {
        if (!Fallos.TryGetValue(clave, out var lista))
        {
            return new List<DateTime>();
        }

        var limite = DateTime.UtcNow - Ventana;
        lista.RemoveAll(f => f < limite);

        if (lista.Count == 0)
        {
            Fallos.Remove(clave);
        }

        return lista;
    }
}
