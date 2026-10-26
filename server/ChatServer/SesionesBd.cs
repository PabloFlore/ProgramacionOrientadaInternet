using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ChatServer.Datos;

/// <summary>
/// Sesiones con token opaco. Al iniciar sesión se emite un token
/// aleatorio que se guarda aquí; el cliente lo envía al hub SignalR
/// (access_token) y a la API (Authorization: Bearer). Cada sesión
/// caduca a los DuracionDias días desde que se creó.
/// </summary>
public static class SesionesBd
{
    private const int DuracionDias = 7;

    private static readonly string RutaBd =
        Path.Combine(AppContext.BaseDirectory, "chat.db");

    private static string CadenaConexion =>
        new SqliteConnectionStringBuilder
        {
            DataSource = RutaBd,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    static SesionesBd()
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
                CREATE TABLE IF NOT EXISTS Sesiones (
                    Token      TEXT PRIMARY KEY,
                    UsuarioId  INTEGER NOT NULL,
                    CreadaEn   TEXT NOT NULL,
                    ExpiraEn   TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Sesiones_Usuario
                    ON Sesiones (UsuarioId);
            ";
            comando.ExecuteNonQuery();
        }

        /* Migración: las bases creadas antes no tienen ExpiraEn. */
        var tieneExpiraEn = false;

        using (var pragma = conexion.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Sesiones);";
            using var lector = pragma.ExecuteReader();
            while (lector.Read())
            {
                if (string.Equals(
                        lector.GetString(1),
                        "ExpiraEn",
                        StringComparison.OrdinalIgnoreCase))
                {
                    tieneExpiraEn = true;
                    break;
                }
            }
        }

        if (!tieneExpiraEn)
        {
            using var alter = conexion.CreateCommand();
            alter.CommandText =
                "ALTER TABLE Sesiones ADD COLUMN ExpiraEn TEXT NULL;";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Borra las sesiones vencidas. Las filas sin ExpiraEn (creadas
    /// antes de la caducidad) se consideran vencidas: el usuario debe
    /// iniciar sesión una vez más.
    /// </summary>
    public static void LimpiarVencidas()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            DELETE FROM Sesiones
            WHERE ExpiraEn IS NULL OR ExpiraEn <= $ahora;
        ";
        comando.Parameters.AddWithValue("$ahora", Ahora());
        comando.ExecuteNonQuery();
    }

    /// <summary>Emite un token nuevo para el usuario y lo devuelve.</summary>
    public static string Crear(int usuarioId)
    {
        var token = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));

        var ahora = DateTime.UtcNow;

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT INTO Sesiones (Token, UsuarioId, CreadaEn, ExpiraEn)
            VALUES ($token, $usuarioId, $creada, $expira);
        ";
        comando.Parameters.AddWithValue("$token", token);
        comando.Parameters.AddWithValue("$usuarioId", usuarioId);
        comando.Parameters.AddWithValue("$creada", ahora.ToString("o"));
        comando.Parameters.AddWithValue("$expira",
            ahora.AddDays(DuracionDias).ToString("o"));
        comando.ExecuteNonQuery();

        return token;
    }

    /// <summary>
    /// Id del usuario dueño del token, o null si el token no existe o
    /// ya venció.
    /// </summary>
    public static int? ObtenerUsuarioId(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT UsuarioId FROM Sesiones
            WHERE Token = $token AND ExpiraEn > $ahora
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$token", token);
        comando.Parameters.AddWithValue("$ahora", Ahora());

        var resultado = comando.ExecuteScalar();

        if (resultado is null || resultado is DBNull)
        {
            return null;
        }

        return Convert.ToInt32(resultado);
    }

    public static void Eliminar(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText =
            "DELETE FROM Sesiones WHERE Token = $token;";
        comando.Parameters.AddWithValue("$token", token);

        comando.ExecuteNonQuery();
    }

    /// <summary>
    /// Fechas en formato "o" (ISO 8601 UTC de ancho fijo), así la
    /// comparación de texto en SQL respeta el orden cronológico.
    /// </summary>
    private static string Ahora() => DateTime.UtcNow.ToString("o");
}
