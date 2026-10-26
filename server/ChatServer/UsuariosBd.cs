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

    /* Columnas en el mismo orden que LeerUsuario. Las imágenes (FotoPerfil,
       Banner) pesan hasta ~1,4 MB por usuario y no se cargan aquí: se leen
       solo con ObtenerImagenes, para no pagarlas en cada petición. */
    private const string ColumnasUsuario = @"
        Id, Nombre, Apellido, Usuario, Correo,
        HashContrasena, Sal, FechaRegistro";

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

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = @"
                CREATE TABLE IF NOT EXISTS Usuarios (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    Nombre          TEXT NOT NULL,
                    Apellido        TEXT NOT NULL,
                    Usuario         TEXT NOT NULL,
                    Correo          TEXT NOT NULL UNIQUE,
                    HashContrasena  TEXT NOT NULL,
                    Sal             TEXT NOT NULL,
                    FechaRegistro   TEXT NOT NULL,
                    FotoPerfil      TEXT NULL,
                    Banner          TEXT NULL
                );
            ";
            comando.ExecuteNonQuery();
        }

        /* Migración: bases creadas antes no tienen las columnas de imagen. */
        foreach (var columna in new[] { "FotoPerfil", "Banner" })
        {
            if (!TieneColumna(conexion, "Usuarios", columna))
            {
                using var alter = conexion.CreateCommand();
                alter.CommandText =
                    $"ALTER TABLE Usuarios ADD COLUMN {columna} TEXT NULL;";
                alter.ExecuteNonQuery();
            }
        }
    }

    private static bool TieneColumna(
        SqliteConnection conexion,
        string tabla,
        string columna)
    {
        using var pragma = conexion.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tabla});";

        using var lector = pragma.ExecuteReader();
        while (lector.Read())
        {
            if (string.Equals(
                    lector.GetString(1),
                    columna,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
    /// True si el correo ya lo usa otra cuenta distinta de idActual
    /// (sin distinguir mayúsculas).
    /// </summary>
    public static bool CorreoOcupadoPorOtro(string correo, int idActual)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT COUNT(*) FROM Usuarios
            WHERE LOWER(Correo) = LOWER($correo) AND Id <> $id;
        ";
        comando.Parameters.AddWithValue("$correo", correo);
        comando.Parameters.AddWithValue("$id", idActual);

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
        comando.CommandText = $@"
            SELECT {ColumnasUsuario}
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
        comando.CommandText = $@"
            SELECT {ColumnasUsuario}
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
        comando.CommandText = $@"
            SELECT {ColumnasUsuario}
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

    /// <summary>Foto y banner del usuario (data URL), o null si no tiene.</summary>
    public static (string? Foto, string? Banner) ObtenerImagenes(int id)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT FotoPerfil, Banner FROM Usuarios
            WHERE Id = $id
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$id", id);

        using var lector = comando.ExecuteReader();
        if (!lector.Read())
        {
            return (null, null);
        }

        return (
            lector.IsDBNull(0) ? null : lector.GetString(0),
            lector.IsDBNull(1) ? null : lector.GetString(1));
    }

    /// <summary>
    /// Guarda perfil, contraseña e imágenes en una sola transacción.
    /// - nuevaClave: null = no cambia la contraseña. Si se cambia, se
    ///   cierran las demás sesiones del usuario (se conserva tokenActual).
    /// - foto / banner: null = no cambian; "" = se quitan; otro valor =
    ///   nueva data URL.
    /// Devuelve false si el correo ya pertenece a otra cuenta.
    /// </summary>
    public static bool ActualizarCuenta(
        int id,
        string nombre,
        string apellido,
        string usuario,
        string correo,
        (string Hash, string Sal)? nuevaClave,
        string? foto,
        string? banner,
        string tokenActual)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        try
        {
            var asignaciones = new List<string>
            {
                "Nombre = $nombre",
                "Apellido = $apellido",
                "Usuario = $usuario",
                "Correo = $correo"
            };

            using var update = conexion.CreateCommand();
            update.Transaction = transaccion;
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$nombre", nombre);
            update.Parameters.AddWithValue("$apellido", apellido);
            update.Parameters.AddWithValue("$usuario", usuario);
            update.Parameters.AddWithValue("$correo", correo);

            if (nuevaClave is not null)
            {
                asignaciones.Add("HashContrasena = $hash");
                asignaciones.Add("Sal = $sal");
                update.Parameters.AddWithValue("$hash", nuevaClave.Value.Hash);
                update.Parameters.AddWithValue("$sal", nuevaClave.Value.Sal);
            }

            if (foto is not null)
            {
                asignaciones.Add("FotoPerfil = $foto");
                update.Parameters.AddWithValue(
                    "$foto", ValorONulo(foto));
            }

            if (banner is not null)
            {
                asignaciones.Add("Banner = $banner");
                update.Parameters.AddWithValue(
                    "$banner", ValorONulo(banner));
            }

            update.CommandText =
                $"UPDATE Usuarios SET {string.Join(", ", asignaciones)} " +
                "WHERE Id = $id;";
            update.ExecuteNonQuery();

            if (nuevaClave is not null)
            {
                using var cerrarOtras = conexion.CreateCommand();
                cerrarOtras.Transaction = transaccion;
                cerrarOtras.CommandText = @"
                    DELETE FROM Sesiones
                    WHERE UsuarioId = $id AND Token <> $actual;
                ";
                cerrarOtras.Parameters.AddWithValue("$id", id);
                cerrarOtras.Parameters.AddWithValue("$actual", tokenActual);
                cerrarOtras.ExecuteNonQuery();
            }

            transaccion.Commit();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            /* UNIQUE: el correo ya pertenece a otra cuenta. */
            transaccion.Rollback();
            return false;
        }
    }

    private static object ValorONulo(string valor) =>
        valor == "" ? DBNull.Value : valor;

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
