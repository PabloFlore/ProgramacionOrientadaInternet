using Microsoft.Data.Sqlite;
using ChatServer.Modelos;

namespace ChatServer.Datos;

public static class TareasBd
{
    public static readonly (int Id, string Titulo, string Descripcion, string Tipo)[]
        Catalogo =
        {
            (1, "Envía un mensaje al grupo",
                "Escribe un mensaje de texto en este grupo.",
                "texto"),
            (2, "Sube una imagen al grupo",
                "Adjunta una imagen en este grupo.",
                "archivo"),
            (3, "Comparte tu ubicación en el grupo",
                "Envía tu ubicación en este grupo.",
                "ubicacion")
        };

    private static string CadenaConexion =>
        new SqliteConnectionStringBuilder
        {
            DataSource = RutaDatos.Archivo,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    static TareasBd()
    {
        CrearTablas();
    }

    private static void CrearTablas()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = @"
                CREATE TABLE IF NOT EXISTS Tareas (
                    Id                INTEGER PRIMARY KEY AUTOINCREMENT,
                    GrupoId           INTEGER NOT NULL,
                    Titulo            TEXT NOT NULL,
                    Descripcion       TEXT NULL,
                    CreadorId         INTEGER NOT NULL,
                    AsignadoId        INTEGER NOT NULL,
                    FechaCreacion     TEXT NOT NULL,
                    FechaFinalizacion TEXT NULL,
                    FinalizadaPor     INTEGER NULL,
                    CatalogoId        INTEGER NOT NULL DEFAULT 0,
                    TipoVerificacion  TEXT NOT NULL DEFAULT 'manual',
                    EvidenciaMensajeId INTEGER NULL
                );
                CREATE INDEX IF NOT EXISTS IX_Tareas_Grupo
                    ON Tareas (GrupoId, Id);
            ";
            comando.ExecuteNonQuery();
        }

        var columnasExistentes = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        using (var pragma = conexion.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(Tareas);";
            using var lector = pragma.ExecuteReader();
            while (lector.Read())
            {
                columnasExistentes.Add(lector.GetString(1));
            }
        }

        var columnasNuevas = new (string Nombre, string Definicion)[]
        {
            ("CatalogoId", "CatalogoId INTEGER NOT NULL DEFAULT 0"),
            ("TipoVerificacion", "TipoVerificacion TEXT NOT NULL DEFAULT 'manual'"),
            ("EvidenciaMensajeId", "EvidenciaMensajeId INTEGER NULL")
        };

        foreach (var (nombre, definicion) in columnasNuevas)
        {
            if (columnasExistentes.Contains(nombre))
            {
                continue;
            }

            using var alter = conexion.CreateCommand();
            alter.CommandText = $"ALTER TABLE Tareas ADD COLUMN {definicion};";
            alter.ExecuteNonQuery();
        }

        using (var indice = conexion.CreateCommand())
        {
            indice.CommandText = @"
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Tareas_Predefinidas
                    ON Tareas (GrupoId, AsignadoId, CatalogoId)
                    WHERE CatalogoId > 0;
            ";
            indice.ExecuteNonQuery();
        }
    }

    public static int Crear(
        int grupoId,
        string titulo,
        string? descripcion,
        int creadorId,
        int asignadoId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT INTO Tareas
                (GrupoId, Titulo, Descripcion, CreadorId, AsignadoId, FechaCreacion)
            VALUES
                ($grupo, $titulo, $descripcion, $creador, $asignado, $fecha);
            SELECT last_insert_rowid();
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$titulo", titulo);
        comando.Parameters.AddWithValue(
            "$descripcion",
            string.IsNullOrWhiteSpace(descripcion) ? DBNull.Value : descripcion);
        comando.Parameters.AddWithValue("$creador", creadorId);
        comando.Parameters.AddWithValue("$asignado", asignadoId);
        comando.Parameters.AddWithValue("$fecha", DateTime.UtcNow.ToString("o"));

        return (int)(long)comando.ExecuteScalar()!;
    }

    public static void SincronizarTodo()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        var grupos = new List<int>();
        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = "SELECT Id FROM Grupos;";
            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                grupos.Add(lector.GetInt32(0));
            }
        }

        foreach (var grupoId in grupos)
        {
            if (string.IsNullOrWhiteSpace(
                GruposBd.ObtenerEnlaceRecompensa(grupoId)))
            {
                continue;
            }

            SincronizarGrupo(grupoId);
        }
    }

    public static void SincronizarGrupo(int grupoId)
    {
        var creador = GruposBd.Obtener(grupoId)?.CreadorId ?? 0;

        if (creador == 0)
        {
            return;
        }

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        foreach (var usuarioId in GruposBd.IdsMiembros(grupoId))
        {
            foreach (var tarea in Catalogo)
            {
                using var comando = conexion.CreateCommand();
                comando.Transaction = transaccion;
                comando.CommandText = @"
                    INSERT OR IGNORE INTO Tareas
                        (GrupoId, Titulo, Descripcion, CreadorId, AsignadoId,
                         FechaCreacion, CatalogoId, TipoVerificacion)
                    VALUES
                        ($grupo, $titulo, $descripcion, $creador, $asignado,
                         $fecha, $catalogo, $tipo);
                ";
                comando.Parameters.AddWithValue("$grupo", grupoId);
                comando.Parameters.AddWithValue("$titulo", tarea.Titulo);
                comando.Parameters.AddWithValue("$descripcion", tarea.Descripcion);
                comando.Parameters.AddWithValue("$creador", creador);
                comando.Parameters.AddWithValue("$asignado", usuarioId);
                comando.Parameters.AddWithValue("$fecha", DateTime.UtcNow.ToString("o"));
                comando.Parameters.AddWithValue("$catalogo", tarea.Id);
                comando.Parameters.AddWithValue("$tipo", tarea.Tipo);
                comando.ExecuteNonQuery();
            }
        }

        transaccion.Commit();
    }

    /// <summary>
    /// Quita los retos sin finalizar de quien sale del grupo. Las tareas ya
    /// cerradas se conservan porque pueden tener una recompensa asociada.
    /// </summary>
    public static void RemoverRetosPendientes(int grupoId, int usuarioId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            DELETE FROM Tareas
            WHERE GrupoId = $grupo
              AND AsignadoId = $usuario
              AND FechaFinalizacion IS NULL;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$usuario", usuarioId);
        comando.ExecuteNonQuery();
    }

    public static List<Tarea> Listar(int grupoId)
    {
        var tareas = new List<Tarea>();

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT t.Id, t.GrupoId, t.Titulo, t.Descripcion,
                   t.CreadorId, t.AsignadoId, t.FechaCreacion,
                   t.FechaFinalizacion, t.FinalizadaPor,
                   uc.Nombre, ua.Nombre, uf.Nombre,
                   t.CatalogoId, t.TipoVerificacion, t.EvidenciaMensajeId
            FROM Tareas t
            LEFT JOIN Usuarios uc ON uc.Id = t.CreadorId
            LEFT JOIN Usuarios ua ON ua.Id = t.AsignadoId
            LEFT JOIN Usuarios uf ON uf.Id = t.FinalizadaPor
            WHERE t.GrupoId = $grupo
            ORDER BY (t.FechaFinalizacion IS NOT NULL), t.CatalogoId, t.Id DESC;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            tareas.Add(LeerTarea(lector));
        }

        return tareas;
    }

    public static Tarea? Obtener(int tareaId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT t.Id, t.GrupoId, t.Titulo, t.Descripcion,
                   t.CreadorId, t.AsignadoId, t.FechaCreacion,
                   t.FechaFinalizacion, t.FinalizadaPor,
                   uc.Nombre, ua.Nombre, uf.Nombre,
                   t.CatalogoId, t.TipoVerificacion, t.EvidenciaMensajeId
            FROM Tareas t
            LEFT JOIN Usuarios uc ON uc.Id = t.CreadorId
            LEFT JOIN Usuarios ua ON ua.Id = t.AsignadoId
            LEFT JOIN Usuarios uf ON uf.Id = t.FinalizadaPor
            WHERE t.Id = $id
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$id", tareaId);

        using var lector = comando.ExecuteReader();

        return lector.Read() ? LeerTarea(lector) : null;
    }

    private static Tarea LeerTarea(SqliteDataReader lector)
    {
        var catalogoId = lector.GetInt32(12);
        var tipo = lector.IsDBNull(13)
            ? "manual"
            : lector.GetString(13);

        return new Tarea
        {
            Id = lector.GetInt32(0),
            GrupoId = lector.GetInt32(1),
            Titulo = lector.GetString(2),
            Descripcion = lector.IsDBNull(3) ? null : lector.GetString(3),
            CreadorId = lector.GetInt32(4),
            AsignadoId = lector.GetInt32(5),
            FechaCreacion = lector.GetString(6),
            FechaFinalizacion = lector.IsDBNull(7)
                ? null
                : lector.GetString(7),
            FinalizadaPor = lector.IsDBNull(8)
                ? null
                : lector.GetInt32(8),
            CreadorNombre = lector.IsDBNull(9) ? "" : lector.GetString(9),
            AsignadoNombre = lector.IsDBNull(10) ? "" : lector.GetString(10),
            FinalizadaPorNombre = lector.IsDBNull(11)
                ? ""
                : lector.GetString(11),
            CatalogoId = catalogoId,
            TipoVerificacion = tipo,
            EvidenciaMensajeId = lector.IsDBNull(14)
                ? null
                : lector.GetInt64(14)
        };
    }

    public static bool EsTareaDelGrupo(int tareaId, int grupoId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT COUNT(*)
            FROM Tareas
            WHERE Id = $id AND GrupoId = $grupo;
        ";
        comando.Parameters.AddWithValue("$id", tareaId);
        comando.Parameters.AddWithValue("$grupo", grupoId);

        return Convert.ToInt32(comando.ExecuteScalar()) > 0;
    }

    public static bool EvidenciaYaUsada(
        int grupoId,
        int usuarioId,
        long mensajeId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT COUNT(*)
            FROM Tareas
            WHERE GrupoId = $grupo
              AND FinalizadaPor = $usuario
              AND EvidenciaMensajeId = $mensaje;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$usuario", usuarioId);
        comando.Parameters.AddWithValue("$mensaje", mensajeId);

        return Convert.ToInt32(comando.ExecuteScalar()) > 0;
    }

    public static bool TareasAutomaticasCompletas(int grupoId, int usuarioId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        return TareasAutomaticasCompletasEnTransaccion(
            conexion, null, grupoId, usuarioId);
    }

    private static bool TareasAutomaticasCompletasEnTransaccion(
        SqliteConnection conexion,
        SqliteTransaction? transaccion,
        int grupoId,
        int usuarioId)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = @"
            SELECT COUNT(*)
            FROM Tareas
            WHERE GrupoId = $grupo
              AND AsignadoId = $usuario
              AND CatalogoId > 0
              AND FechaFinalizacion IS NOT NULL;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$usuario", usuarioId);

        return Convert.ToInt32(comando.ExecuteScalar()) >= Catalogo.Length;
    }

    public static bool Finalizar(
        int tareaId,
        int usuarioId,
        long? evidenciaMensajeId = null)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        return FinalizarEnTransaccion(
            conexion, null, tareaId, usuarioId, evidenciaMensajeId);
    }

    private static bool FinalizarEnTransaccion(
        SqliteConnection conexion,
        SqliteTransaction? transaccion,
        int tareaId,
        int usuarioId,
        long? evidenciaMensajeId)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = @"
            UPDATE Tareas
            SET FechaFinalizacion = $fecha,
                FinalizadaPor = $usuario,
                EvidenciaMensajeId = $evidencia
            WHERE Id = $id AND FechaFinalizacion IS NULL;
        ";
        comando.Parameters.AddWithValue("$fecha", DateTime.UtcNow.ToString("o"));
        comando.Parameters.AddWithValue("$usuario", usuarioId);
        comando.Parameters.AddWithValue(
            "$evidencia",
            evidenciaMensajeId.HasValue ? evidenciaMensajeId.Value : DBNull.Value);
        comando.Parameters.AddWithValue("$id", tareaId);

        return comando.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Cierra la tarea y otorga las recompensas en una sola transacción:
    /// si falla el otorgamiento no queda una tarea finalizada sin premio.
    /// </summary>
    public static (bool Finalizada, List<Recompensa?> Recompensas)
        FinalizarYRecompensar(
            int tareaId,
            int grupoId,
            int usuarioId,
            bool esDelCatalogo,
            long? evidenciaMensajeId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        if (!FinalizarEnTransaccion(
                conexion, transaccion, tareaId, usuarioId, evidenciaMensajeId))
        {
            transaccion.Rollback();
            return (false, new List<Recompensa?>());
        }

        var otorgadas = new List<Recompensa?>();

        if (esDelCatalogo)
        {
            if (TareasAutomaticasCompletasEnTransaccion(
                    conexion, transaccion, grupoId, usuarioId))
            {
                otorgadas.Add(RecompensasBd.OtorgarDelPlanEnTransaccion(
                    conexion, transaccion, grupoId, usuarioId, 1));
                otorgadas.Add(RecompensasBd.OtorgarDelPlanEnTransaccion(
                    conexion, transaccion, grupoId, usuarioId, 2));
            }
        }
        else
        {
            otorgadas.Add(RecompensasBd.OtorgarAleatoriaEnTransaccion(
                conexion, transaccion, usuarioId, tareaId));
        }

        transaccion.Commit();

        return (true, otorgadas);
    }
}
