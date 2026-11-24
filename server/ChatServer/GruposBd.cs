using Microsoft.Data.Sqlite;

namespace ChatServer.Datos;

/// <summary>
/// Grupos de chat (SQLite). Cada grupo se identifica con el chatId
/// "grupo:{id}". Solo sus integrantes pueden leer y escribir en él. El
/// creador también cuenta como integrante.
/// </summary>
public static class GruposBd
{
    public sealed record GrupoListado(
        int Id,
        string Nombre,
        int Integrantes,
        bool Activo,
        string UltimoMensaje,
        string? UltimaFecha,
        string UltimoTipo,
        string UltimoUsuario);

    public const int MinimoIntegrantes = 3;
    public const int MaximoIntegrantes = 50;
    public const int MaximoNombre = 60;

    private static string CadenaConexion =>
        new SqliteConnectionStringBuilder
        {
            DataSource = RutaDatos.Archivo,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    static GruposBd()
    {
        CrearTablas();
    }

    public static void Inicializar()
    {
    }

    private static void CrearTablas()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            CREATE TABLE IF NOT EXISTS Grupos (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                Nombre         TEXT NOT NULL,
                CreadorId      INTEGER NOT NULL,
                FechaCreacion  TEXT NOT NULL,
                EnlaceRecompensa TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS GrupoMiembros (
                GrupoId    INTEGER NOT NULL,
                UsuarioId  INTEGER NOT NULL,
                PRIMARY KEY (GrupoId, UsuarioId)
            );
            CREATE INDEX IF NOT EXISTS IX_GrupoMiembros_Usuario
                ON GrupoMiembros (UsuarioId);
            /* Quien salió o fue quitado. Conserva el acceso de solo lectura
               al historial; no le da acceso en vivo ni permite escribir. */
            CREATE TABLE IF NOT EXISTS GrupoExintegrantes (
                GrupoId     INTEGER NOT NULL,
                UsuarioId   INTEGER NOT NULL,
                FechaSalida TEXT NOT NULL,
                PRIMARY KEY (GrupoId, UsuarioId)
            );
        ";
        comando.ExecuteNonQuery();

        using var pragma = conexion.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Grupos);";
        var columnas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var lector = pragma.ExecuteReader())
        {
            while (lector.Read())
            {
                columnas.Add(lector.GetString(1));
            }
        }

        if (!columnas.Contains("EnlaceRecompensa"))
        {
            using var alter = conexion.CreateCommand();
            alter.CommandText =
                "ALTER TABLE Grupos ADD COLUMN EnlaceRecompensa TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Crea el grupo y registra al creador y a los demás integrantes en
    /// una sola transacción. Devuelve el id del grupo.
    /// </summary>
    public static int Crear(
        string nombre,
        int creadorId,
        IReadOnlyList<int> otrosIntegrantes,
        string enlaceRecompensa = "")
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        long id;
        using (var insertarGrupo = conexion.CreateCommand())
        {
            insertarGrupo.Transaction = transaccion;
            insertarGrupo.CommandText = @"
                INSERT INTO Grupos (Nombre, CreadorId, FechaCreacion, EnlaceRecompensa)
                VALUES ($nombre, $creador, $fecha, $enlace);
                SELECT last_insert_rowid();
            ";
            insertarGrupo.Parameters.AddWithValue("$nombre", nombre);
            insertarGrupo.Parameters.AddWithValue("$creador", creadorId);
            insertarGrupo.Parameters.AddWithValue(
                "$fecha", DateTime.UtcNow.ToString("o"));
            insertarGrupo.Parameters.AddWithValue("$enlace", enlaceRecompensa);
            id = (long)insertarGrupo.ExecuteScalar()!;
        }

        var integrantes = new List<int> { creadorId };
        integrantes.AddRange(otrosIntegrantes.Where(
            u => u != creadorId));

        foreach (var usuarioId in integrantes.Distinct())
        {
            using var insertarMiembro = conexion.CreateCommand();
            insertarMiembro.Transaction = transaccion;
            insertarMiembro.CommandText = @"
                INSERT INTO GrupoMiembros (GrupoId, UsuarioId)
                VALUES ($grupo, $usuario);
            ";
            insertarMiembro.Parameters.AddWithValue("$grupo", id);
            insertarMiembro.Parameters.AddWithValue("$usuario", usuarioId);
            insertarMiembro.ExecuteNonQuery();
        }

        transaccion.Commit();
        return (int)id;
    }

    public static bool EsMiembro(int grupoId, int usuarioId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT COUNT(*) FROM GrupoMiembros
            WHERE GrupoId = $grupo AND UsuarioId = $usuario;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$usuario", usuarioId);

        return (long)comando.ExecuteScalar()! > 0;
    }

    /// <summary>
    /// True si el usuario es integrante actual o alguna vez lo fue y salió
    /// o fue quitado. Solo sirve para decidir lectura del historial.
    /// </summary>
    public static bool FueMiembro(int grupoId, int usuarioId)
    {
        if (EsMiembro(grupoId, usuarioId))
        {
            return true;
        }

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT COUNT(*) FROM GrupoExintegrantes
            WHERE GrupoId = $grupo AND UsuarioId = $usuario;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$usuario", usuarioId);

        return (long)comando.ExecuteScalar()! > 0;
    }

    /// <summary>Datos del grupo, o null si no existe.</summary>
    public static (int Id, string Nombre, int CreadorId)? Obtener(int grupoId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT Id, Nombre, CreadorId FROM Grupos
            WHERE Id = $grupo
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);

        using var lector = comando.ExecuteReader();
        if (!lector.Read())
        {
            return null;
        }

        return (lector.GetInt32(0), lector.GetString(1), lector.GetInt32(2));
    }

    public static List<int> IdsMiembros(int grupoId)
    {
        var lista = new List<int>();

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT UsuarioId FROM GrupoMiembros
            WHERE GrupoId = $grupo
            ORDER BY UsuarioId;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            lista.Add(lector.GetInt32(0));
        }

        return lista;
    }

    /// <summary>
    /// Agrega integrantes en una transacción. Las reglas (existencia,
    /// no repetidos, máximo) las valida la API antes de llamar aquí.
    /// </summary>
    public static void AgregarMiembros(int grupoId, IReadOnlyList<int> usuarios)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        foreach (var usuarioId in usuarios.Distinct())
        {
            using var insertar = conexion.CreateCommand();
            insertar.Transaction = transaccion;
            insertar.CommandText = @"
                INSERT OR IGNORE INTO GrupoMiembros (GrupoId, UsuarioId)
                VALUES ($grupo, $usuario);
            ";
            insertar.Parameters.AddWithValue("$grupo", grupoId);
            insertar.Parameters.AddWithValue("$usuario", usuarioId);
            insertar.ExecuteNonQuery();

            /* Si vuelve al grupo, deja de ser exintegrante. */
            using var reingreso = conexion.CreateCommand();
            reingreso.Transaction = transaccion;
            reingreso.CommandText = @"
                DELETE FROM GrupoExintegrantes
                WHERE GrupoId = $grupo AND UsuarioId = $usuario;
            ";
            reingreso.Parameters.AddWithValue("$grupo", grupoId);
            reingreso.Parameters.AddWithValue("$usuario", usuarioId);
            reingreso.ExecuteNonQuery();
        }

        transaccion.Commit();
    }

    /// <summary>
    /// Quita un integrante. Si era el creador, el rol pasa al integrante
    /// de menor id que quede, para que siempre haya alguien que pueda
    /// quitar a otros. La API garantiza que quedan al menos 3 integrantes.
    /// </summary>
    public static void QuitarMiembro(int grupoId, int usuarioId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        using (var borrar = conexion.CreateCommand())
        {
            borrar.Transaction = transaccion;
            borrar.CommandText = @"
                DELETE FROM GrupoMiembros
                WHERE GrupoId = $grupo AND UsuarioId = $usuario;
            ";
            borrar.Parameters.AddWithValue("$grupo", grupoId);
            borrar.Parameters.AddWithValue("$usuario", usuarioId);
            borrar.ExecuteNonQuery();
        }

        using (var registrar = conexion.CreateCommand())
        {
            registrar.Transaction = transaccion;
            registrar.CommandText = @"
                INSERT OR REPLACE INTO GrupoExintegrantes
                    (GrupoId, UsuarioId, FechaSalida)
                VALUES ($grupo, $usuario, $fecha);
            ";
            registrar.Parameters.AddWithValue("$grupo", grupoId);
            registrar.Parameters.AddWithValue("$usuario", usuarioId);
            registrar.Parameters.AddWithValue(
                "$fecha", DateTime.UtcNow.ToString("o"));
            registrar.ExecuteNonQuery();
        }

        using (var traspaso = conexion.CreateCommand())
        {
            traspaso.Transaction = transaccion;
            traspaso.CommandText = @"
                UPDATE Grupos
                SET CreadorId = (
                    SELECT MIN(UsuarioId) FROM GrupoMiembros
                    WHERE GrupoId = $grupo)
                WHERE Id = $grupo AND CreadorId = $usuario;
            ";
            traspaso.Parameters.AddWithValue("$grupo", grupoId);
            traspaso.Parameters.AddWithValue("$usuario", usuarioId);
            traspaso.ExecuteNonQuery();
        }

        transaccion.Commit();
    }

    /// <summary>
    /// Grupos del usuario: los actuales (Activo = true) y los que salió o
    /// de los que fue quitado (Activo = false, solo lectura).
    /// </summary>
    public static List<GrupoListado> ListarDeUsuario(int usuarioId)
    {
        var lista = new List<GrupoListado>();

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT g.Id, g.Nombre,
                   (SELECT COUNT(*) FROM GrupoMiembros m2
                    WHERE m2.GrupoId = g.Id),
                   EXISTS (SELECT 1 FROM GrupoMiembros m
                           WHERE m.GrupoId = g.Id AND m.UsuarioId = $usuario),
                   COALESCE(ult.Mensaje, ''),
                   ult.FechaEnvio,
                   COALESCE(ult.Tipo, 'texto'),
                   COALESCE(ult.Usuario, '')
            FROM Grupos g
            LEFT JOIN (
                SELECT m.ChatId, m.Mensaje, m.FechaEnvio, m.Tipo, m.Usuario
                FROM Mensajes m
                INNER JOIN (
                    SELECT ChatId, MAX(Id) AS UltimoId
                    FROM Mensajes
                    WHERE ChatId LIKE 'grupo:%'
                    GROUP BY ChatId
                ) u ON u.UltimoId = m.Id
                LEFT JOIN BorradosChat b
                    ON b.ChatId = m.ChatId AND b.UsuarioId = $usuario
                WHERE m.Id > COALESCE(b.UltimoMensajeId, 0)
            ) ult ON ult.ChatId = 'grupo:' || CAST(g.Id AS TEXT)
            WHERE EXISTS (SELECT 1 FROM GrupoMiembros m
                          WHERE m.GrupoId = g.Id AND m.UsuarioId = $usuario)
               OR EXISTS (SELECT 1 FROM GrupoExintegrantes e
                          WHERE e.GrupoId = g.Id AND e.UsuarioId = $usuario)
            ORDER BY
                CASE WHEN EXISTS (SELECT 1 FROM GrupoMiembros m
                                  WHERE m.GrupoId = g.Id AND m.UsuarioId = $usuario)
                     THEN 0 ELSE 1 END,
                ult.FechaEnvio IS NULL,
                ult.FechaEnvio DESC,
                g.Nombre COLLATE NOCASE;
        ";
        comando.Parameters.AddWithValue("$usuario", usuarioId);

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            lista.Add(new GrupoListado(
                lector.GetInt32(0),
                lector.GetString(1),
                (int)lector.GetInt64(2),
                lector.GetInt64(3) == 1,
                lector.GetString(4),
                lector.IsDBNull(5) ? null : lector.GetString(5),
                lector.GetString(6),
                lector.GetString(7)));
        }

        return lista;
    }

    public static string ObtenerEnlaceRecompensa(int grupoId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT EnlaceRecompensa FROM Grupos
            WHERE Id = $grupo
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);

        return comando.ExecuteScalar() as string ?? "";
    }

    public static void GuardarEnlaceRecompensa(int grupoId, string enlace)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            UPDATE Grupos
            SET EnlaceRecompensa = $enlace
            WHERE Id = $grupo;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$enlace", enlace);
        comando.ExecuteNonQuery();
    }
}
