using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ChatServer.Datos;

/// <summary>
/// Sesiones con token opaco. Al iniciar sesión se emite un token
/// aleatorio que se guarda aquí; el cliente lo envía al hub SignalR
/// (access_token) y a la API (Authorization: Bearer).
/// </summary>
public static class SesionesBd
{
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

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            CREATE TABLE IF NOT EXISTS Sesiones (
                Token      TEXT PRIMARY KEY,
                UsuarioId  INTEGER NOT NULL,
                CreadaEn   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Sesiones_Usuario
                ON Sesiones (UsuarioId);
        ";
        comando.ExecuteNonQuery();
    }

    /// <summary>Emite un token nuevo para el usuario y lo devuelve.</summary>
    public static string Crear(int usuarioId)
    {
        var token = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32));

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT INTO Sesiones (Token, UsuarioId, CreadaEn)
            VALUES ($token, $usuarioId, $creada);
        ";
        comando.Parameters.AddWithValue("$token", token);
        comando.Parameters.AddWithValue("$usuarioId", usuarioId);
        comando.Parameters.AddWithValue("$creada",
            DateTime.UtcNow.ToString("o"));
        comando.ExecuteNonQuery();

        return token;
    }

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
            WHERE Token = $token
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$token", token);

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
}
