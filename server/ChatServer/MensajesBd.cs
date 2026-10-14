using Microsoft.Data.Sqlite;
using ChatServer.Modelos;

namespace ChatServer.Datos;

/// <summary>
/// Historial de mensajes. El alcance es el canal (chatId): todas las
/// cuentas que están en el mismo canal ven los mismos mensajes.
/// Es una prueba de que el flujo guarda en la base de datos; no hay
/// control de acceso por usuario todavía.
/// </summary>
public static class MensajesBd
{
    private const int LimitePorDefecto = 100;

    private static readonly string RutaBd =
        Path.Combine(AppContext.BaseDirectory, "chat.db");

    private static string CadenaConexion =>
        new SqliteConnectionStringBuilder
        {
            DataSource = RutaBd,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    static MensajesBd()
    {
        CrearTabla();
    }

    private static void CrearTabla()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            CREATE TABLE IF NOT EXISTS Mensajes (
                Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId     TEXT NOT NULL,
                Usuario    TEXT NOT NULL,
                Mensaje    TEXT NOT NULL,
                FechaEnvio TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Mensajes_Chat
                ON Mensajes (ChatId, Id);
        ";
        comando.ExecuteNonQuery();
    }

    public static long Registrar(
        string chatId,
        string usuario,
        string mensaje)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT INTO Mensajes
                (ChatId, Usuario, Mensaje, FechaEnvio)
            VALUES
                ($chatId, $usuario, $mensaje, $fecha);
            SELECT last_insert_rowid();
        ";
        comando.Parameters.AddWithValue("$chatId", chatId);
        comando.Parameters.AddWithValue("$usuario", usuario);
        comando.Parameters.AddWithValue("$mensaje", mensaje);
        comando.Parameters.AddWithValue("$fecha",
            DateTime.Now.ToString("o"));

        return (long)comando.ExecuteScalar()!;
    }

    /// <summary>
    /// Devuelve los últimos mensajes del canal en orden cronológico
    /// (el más antiguo primero, para poder pintarlos en orden).
    /// </summary>
    public static List<Mensaje> ObtenerPorChat(
        string chatId,
        int limite = LimitePorDefecto)
    {
        if (limite <= 0 || limite > 500)
        {
            limite = LimitePorDefecto;
        }

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio
            FROM (
                SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio
                FROM Mensajes
                WHERE ChatId = $chatId
                ORDER BY Id DESC
                LIMIT $limite
            )
            ORDER BY Id ASC;
        ";
        comando.Parameters.AddWithValue("$chatId", chatId);
        comando.Parameters.AddWithValue("$limite", limite);

        var lista = new List<Mensaje>();

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            DateTime fecha;
            DateTime.TryParse(
                lector.GetString(4),
                out fecha);

            lista.Add(new Mensaje
            {
                Id = lector.GetInt64(0),
                ChatId = lector.GetString(1),
                Usuario = lector.GetString(2),
                Texto = lector.GetString(3),
                FechaEnvio = fecha
            });
        }

        return lista;
    }
}
