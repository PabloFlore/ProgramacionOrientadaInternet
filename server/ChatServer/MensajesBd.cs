using Microsoft.Data.Sqlite;
using ChatServer.Modelos;

namespace ChatServer.Datos;

/// <summary>
/// Historial de mensajes. El alcance es el canal (chatId): todas las
/// cuentas que están en el mismo canal ven los mismos mensajes.
/// Los canales grupales son públicos; las conversaciones privadas usan
/// el formato dm:{idMenor}:{idMayor} y solo sus participantes pueden
/// leerlas o escribir en ellas.
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

        using (var comando = conexion.CreateCommand())
        {
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

        /* Migración: las bases de datos creadas antes de este avance
           no tienen la columna UsuarioId. Se agrega si falta. */
        var tieneUsuarioId = false;

        using (var pragma = conexion.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Mensajes);";
            using var lector = pragma.ExecuteReader();
            while (lector.Read())
            {
                if (string.Equals(
                        lector.GetString(1),
                        "UsuarioId",
                        StringComparison.OrdinalIgnoreCase))
                {
                    tieneUsuarioId = true;
                    break;
                }
            }
        }

        if (!tieneUsuarioId)
        {
            using var alter = conexion.CreateCommand();
            alter.CommandText =
                "ALTER TABLE Mensajes ADD COLUMN UsuarioId INTEGER NULL;";
            alter.ExecuteNonQuery();
        }
    }

    /* -------------------- Conversaciones privadas -------------------- */

    public static string CrearChatPrivado(int a, int b)
    {
        var menor = Math.Min(a, b);
        var mayor = Math.Max(a, b);
        return $"dm:{menor}:{mayor}";
    }

    public static bool EsChatPrivado(string chatId)
    {
        return !string.IsNullOrWhiteSpace(chatId) &&
            chatId.StartsWith("dm:", StringComparison.OrdinalIgnoreCase);
    }

    public static (int A, int B)? ParsearChatPrivado(string chatId)
    {
        if (!EsChatPrivado(chatId))
        {
            return null;
        }

        var partes = chatId.Substring(3).Split(':');
        if (partes.Length != 2)
        {
            return null;
        }

        if (!int.TryParse(partes[0], out var a) ||
            !int.TryParse(partes[1], out var b))
        {
            return null;
        }

        return (a, b);
    }

    /// <summary>
    /// Los canales grupales son públicos (true). En una conversación
    /// privada solo sus dos integrantes pueden participar.
    /// </summary>
    public static bool EsParticipante(string chatId, int usuarioId)
    {
        var par = ParsearChatPrivado(chatId);
        if (par is null)
        {
            return true;
        }

        return par.Value.A == usuarioId || par.Value.B == usuarioId;
    }

    public static int OtroParticipante(string chatId, int usuarioId)
    {
        var par = ParsearChatPrivado(chatId);
        if (par is null)
        {
            return 0;
        }

        return par.Value.A == usuarioId ? par.Value.B : par.Value.A;
    }

    /* -------------------- Escritura -------------------- */

    public static Mensaje Registrar(
        string chatId,
        int usuarioId,
        string usuario,
        string mensaje)
    {
        var fecha = DateTime.Now;

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT INTO Mensajes
                (ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId)
            VALUES
                ($chatId, $usuario, $mensaje, $fecha, $usuarioId);
            SELECT last_insert_rowid();
        ";
        comando.Parameters.AddWithValue("$chatId", chatId);
        comando.Parameters.AddWithValue("$usuario", usuario);
        comando.Parameters.AddWithValue("$mensaje", mensaje);
        comando.Parameters.AddWithValue("$fecha", fecha.ToString("o"));
        comando.Parameters.AddWithValue("$usuarioId", usuarioId);

        var id = (long)comando.ExecuteScalar()!;

        return new Mensaje
        {
            Id = id,
            ChatId = chatId,
            Usuario = usuario,
            Texto = mensaje,
            FechaEnvio = fecha,
            UsuarioId = usuarioId
        };
    }

    /* -------------------- Lectura -------------------- */

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
            SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId
            FROM (
                SELECT Id, ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId
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
            lista.Add(LeerMensaje(lector));
        }

        return lista;
    }

    /// <summary>
    /// Lista de conversaciones privadas del usuario con su último
    /// mensaje, ordenadas por fecha descendente.
    /// </summary>
    public static List<Conversacion> ObtenerConversaciones(int usuarioId)
    {
        var lista = new List<Conversacion>();

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT ChatId, Usuario, Mensaje, FechaEnvio, UsuarioId
            FROM Mensajes
            WHERE Id IN (
                SELECT MAX(Id)
                FROM Mensajes
                WHERE ChatId LIKE 'dm:%'
                GROUP BY ChatId
            );
        ";

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            var chatId = lector.GetString(0);

            if (!EsParticipante(chatId, usuarioId))
            {
                continue;
            }

            lista.Add(new Conversacion
            {
                ChatId = chatId,
                OtroUsuarioId = OtroParticipante(chatId, usuarioId),
                UltimoMensaje = lector.GetString(2),
                UltimaFecha = ParsearFecha(lector.GetString(3)),
                UltimoUsuarioId = lector.IsDBNull(4)
                    ? null
                    : lector.GetInt32(4)
            });
        }

        lista.Sort((a, b) =>
            b.UltimaFecha.CompareTo(a.UltimaFecha));

        return lista;
    }

    private static Mensaje LeerMensaje(SqliteDataReader lector)
    {
        return new Mensaje
        {
            Id = lector.GetInt64(0),
            ChatId = lector.GetString(1),
            Usuario = lector.GetString(2),
            Texto = lector.GetString(3),
            FechaEnvio = ParsearFecha(lector.GetString(4)),
            UsuarioId = lector.IsDBNull(5)
                ? null
                : lector.GetInt32(5)
        };
    }

    private static DateTime ParsearFecha(string texto)
    {
        return DateTime.TryParse(texto, out var fecha)
            ? fecha
            : DateTime.MinValue;
    }
}
