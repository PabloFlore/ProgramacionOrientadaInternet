namespace ChatServer.Datos;

/// <summary>
/// Límite de correos enviados por usuario. Vive en memoria, igual que
/// LimiteIntentos: se reinicia al reiniciar el servidor. Evita que una
/// sola cuenta use el endpoint como servicio de spam.
/// </summary>
public static class LimiteCorreos
{
    private const int MaximoEnvios = 5;
    private static readonly TimeSpan Ventana = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<int, List<DateTime>> Envios = new();
    private static readonly object Candado = new();

    /// <summary>
    /// Registra un intento de envío si el usuario aún está dentro del
    /// límite. Devuelve false cuando debe responderse 429.
    /// </summary>
    public static bool IntentarRegistrar(int usuarioId)
    {
        lock (Candado)
        {
            /* Barre las claves vencidas para que no crezcan sin límite. */
            foreach (var otro in Envios.Keys.ToList())
            {
                EnviosRecientes(otro);
            }

            var lista = EnviosRecientes(usuarioId);

            if (lista.Count >= MaximoEnvios)
            {
                return false;
            }

            lista.Add(DateTime.UtcNow);
            Envios[usuarioId] = lista;
            return true;
        }
    }

    /* Llamar siempre dentro del candado. */
    private static List<DateTime> EnviosRecientes(int usuarioId)
    {
        if (!Envios.TryGetValue(usuarioId, out var lista))
        {
            return new List<DateTime>();
        }

        var limite = DateTime.UtcNow - Ventana;
        lista.RemoveAll(fecha => fecha < limite);

        if (lista.Count == 0)
        {
            Envios.Remove(usuarioId);
        }

        return lista;
    }
}
