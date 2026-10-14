using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ChatServer.Modelos;

namespace ChatServer.Datos;

/// <summary>
/// Capa de datos (SQLite). El correo es único; el Nombre de usuario
/// puede repetirse (requisito actual del proyecto).
/// </summary>
public static class UsuariosBd
{
    private const int Iteraciones = 100_000;
    private const int TamSal = 16;
    private const int TamHash = 32;

    private static readonly string RutaBd =
        Path.Combine(AppContext.BaseDirectory, "chat.db");

    private static string CadenaConexion =>
        new SqliteConnectionStringBuilder
        {
            DataSource = RutaBd,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    static UsuariosBd()
    {
        CrearTabla();
    }

    private static void CrearTabla()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            CREATE TABLE IF NOT EXISTS Usuarios (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                Nombre          TEXT NOT NULL,
                Apellido        TEXT NOT NULL,
                Usuario         TEXT NOT NULL,
                Correo          TEXT NOT NULL UNIQUE,
                HashContrasena  TEXT NOT NULL,
                Sal             TEXT NOT NULL,
                FechaRegistro   TEXT NOT NULL
            );
        ";
        comando.ExecuteNonQuery();
    }

    public static bool CorreoExiste(string correo)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT COUNT(*) FROM Usuarios
            WHERE LOWER(Correo) = LOWER($correo);
        ";
        comando.Parameters.AddWithValue("$correo", correo);

        long cuenta = (long)comando.ExecuteScalar()!;
        return cuenta > 0;
    }

    /// <summary>
    /// Inserta un usuario. Devuelve false si el correo ya existe (UNIQUE).
    /// </summary>
    public static bool Registrar(Usuario usuario)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT INTO Usuarios
                (Nombre, Apellido, Usuario, Correo,
                 HashContrasena, Sal, FechaRegistro)
            VALUES
                ($nombre, $apellido, $usuario, $correo,
                 $hash, $sal, $fecha);
        ";
        comando.Parameters.AddWithValue("$nombre", usuario.Nombre);
        comando.Parameters.AddWithValue("$apellido", usuario.Apellido);
        comando.Parameters.AddWithValue("$usuario", usuario.NombreUsuario);
        comando.Parameters.AddWithValue("$correo", usuario.Correo);
        comando.Parameters.AddWithValue("$hash", usuario.HashContrasena);
        comando.Parameters.AddWithValue("$sal", usuario.Sal);
        comando.Parameters.AddWithValue("$fecha",
            usuario.FechaRegistro.ToString("o"));

        try
        {
            comando.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex)
        {
            if (ex.SqliteErrorCode == 19) // UNIQUE: correo duplicado
            {
                return false;
            }
            throw;
        }
    }

    public static Usuario? BuscarPorCorreo(string correo)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT Id, Nombre, Apellido, Usuario, Correo,
                   HashContrasena, Sal, FechaRegistro
            FROM Usuarios
            WHERE LOWER(Correo) = LOWER($correo)
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$correo", correo);

        using var lector = comando.ExecuteReader();
        if (!lector.Read())
        {
            return null;
        }

        return LeerUsuario(lector);
    }

    public static Usuario? BuscarPorId(int id)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT Id, Nombre, Apellido, Usuario, Correo,
                   HashContrasena, Sal, FechaRegistro
            FROM Usuarios
            WHERE Id = $id
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$id", id);

        using var lector = comando.ExecuteReader();
        if (!lector.Read())
        {
            return null;
        }

        return LeerUsuario(lector);
    }

    /// <summary>Todos los usuarios excepto uno (para elegir contacto).</summary>
    public static List<Usuario> Listar(int excluirId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT Id, Nombre, Apellido, Usuario, Correo,
                   HashContrasena, Sal, FechaRegistro
            FROM Usuarios
            WHERE Id <> $excluir
            ORDER BY Nombre, Apellido;
        ";
        comando.Parameters.AddWithValue("$excluir", excluirId);

        var lista = new List<Usuario>();

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            lista.Add(LeerUsuario(lector));
        }

        return lista;
    }

    private static Usuario LeerUsuario(SqliteDataReader lector)
    {
        return new Usuario
        {
            Id = lector.GetInt32(0),
            Nombre = lector.GetString(1),
            Apellido = lector.GetString(2),
            NombreUsuario = lector.GetString(3),
            Correo = lector.GetString(4),
            HashContrasena = lector.GetString(5),
            Sal = lector.GetString(6),
            FechaRegistro = DateTime.Parse(lector.GetString(7))
        };
    }

    /// <summary>
    /// Actualiza los datos de perfil. Devuelve false si el correo
    /// ya pertenece a otra cuenta (UNIQUE).
    /// </summary>
    public static bool ActualizarPerfil(
        int id,
        string nombre,
        string apellido,
        string usuario,
        string correo)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            UPDATE Usuarios
            SET Nombre = $nombre,
                Apellido = $apellido,
                Usuario = $usuario,
                Correo = $correo
            WHERE Id = $id;
        ";
        comando.Parameters.AddWithValue("$id", id);
        comando.Parameters.AddWithValue("$nombre", nombre);
        comando.Parameters.AddWithValue("$apellido", apellido);
        comando.Parameters.AddWithValue("$usuario", usuario);
        comando.Parameters.AddWithValue("$correo", correo);

        try
        {
            return comando.ExecuteNonQuery() > 0;
        }
        catch (SqliteException ex)
        {
            if (ex.SqliteErrorCode == 19) // UNIQUE: correo duplicado
            {
                return false;
            }
            throw;
        }
    }

    public static bool ActualizarContrasena(
        int id,
        string hash,
        string sal)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            UPDATE Usuarios
            SET HashContrasena = $hash,
                Sal = $sal
            WHERE Id = $id;
        ";
        comando.Parameters.AddWithValue("$id", id);
        comando.Parameters.AddWithValue("$hash", hash);
        comando.Parameters.AddWithValue("$sal", sal);

        return comando.ExecuteNonQuery() > 0;
    }

    /// <summary>PBKDF2-SHA256: devuelve (hash, sal) en Base64. Nada en texto plano.</summary>
    public static (string Hash, string Sal) CrearHashContrasena(
        string contrasena)
    {
        byte[] sal = RandomNumberGenerator.GetBytes(TamSal);

        using var pbkdf2 = new Rfc2898DeriveBytes(
            contrasena,
            sal,
            Iteraciones,
            HashAlgorithmName.SHA256);

        byte[] hash = pbkdf2.GetBytes(TamHash);

        return (
            Convert.ToBase64String(hash),
            Convert.ToBase64String(sal));
    }

    public static bool VerificarContrasena(
        string contrasena,
        string hashGuardado,
        string salGuardada)
    {
        byte[] sal = Convert.FromBase64String(salGuardada);
        byte[] hashEsperado = Convert.FromBase64String(hashGuardado);

        using var pbkdf2 = new Rfc2898DeriveBytes(
            contrasena,
            sal,
            Iteraciones,
            HashAlgorithmName.SHA256);

        byte[] hashCalculado = pbkdf2.GetBytes(hashEsperado.Length);

        return CryptographicOperations.FixedTimeEquals(
            hashCalculado,
            hashEsperado);
    }
}
