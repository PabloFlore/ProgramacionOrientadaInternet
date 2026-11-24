using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ChatServer.Modelos;

namespace ChatServer.Datos;

public static class RecompensasBd
{
    private static string CadenaConexion =>
        new SqliteConnectionStringBuilder
        {
            DataSource = RutaDatos.Archivo,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

    static RecompensasBd()
    {
        CrearTablas();
        SembrarCatalogo();
    }

    private static void CrearTablas()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using (var comando = conexion.CreateCommand())
        {
            comando.CommandText = @"
                CREATE TABLE IF NOT EXISTS Recompensas (
                    Id     INTEGER PRIMARY KEY,
                    Codigo TEXT NOT NULL UNIQUE,
                    Nombre TEXT NOT NULL,
                    Imagen TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS GrupoRecompensasPlan (
                    GrupoId     INTEGER NOT NULL,
                    Orden       INTEGER NOT NULL,
                    RecompensaId INTEGER NOT NULL,
                    PRIMARY KEY (GrupoId, Orden)
                );
                CREATE TABLE IF NOT EXISTS EnlacesRecompensa (
                    GrupoId  INTEGER NOT NULL,
                    UsuarioId INTEGER NOT NULL,
                    Token    TEXT NOT NULL UNIQUE,
                    PRIMARY KEY (GrupoId, UsuarioId)
                );
            ";
            comando.ExecuteNonQuery();
        }

        MigrarUsuarioRecompensas(conexion);
        NormalizarOrigen(conexion);

        using (var indice = conexion.CreateCommand())
        {
            indice.CommandText = @"
                CREATE INDEX IF NOT EXISTS IX_UsuarioRecompensas_Usuario
                    ON UsuarioRecompensas (UsuarioId);
            ";
            indice.ExecuteNonQuery();
        }
    }

    private const string EsquemaUsuarioRecompensas = @"
        Id           INTEGER PRIMARY KEY AUTOINCREMENT,
        UsuarioId    INTEGER NOT NULL,
        RecompensaId INTEGER NOT NULL,
        TareaId      INTEGER NULL,
        GrupoId      INTEGER NULL,
        Orden        INTEGER NOT NULL DEFAULT 0,
        Origen       TEXT NOT NULL DEFAULT 'tareas',
        OtorgadaEn   TEXT NOT NULL,
        UNIQUE (UsuarioId, TareaId),
        UNIQUE (UsuarioId, GrupoId, Orden),
        UNIQUE (UsuarioId, GrupoId, RecompensaId)";

    private static void MigrarUsuarioRecompensas(SqliteConnection conexion)
    {
        var columnas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var pragma = conexion.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(UsuarioRecompensas);";
            using var lector = pragma.ExecuteReader();
            while (lector.Read())
            {
                columnas.Add(lector.GetString(1));
            }
        }

        if (columnas.Contains("GrupoId"))
        {
            return;
        }

        using var migrar = conexion.CreateCommand();

        if (columnas.Count == 0)
        {
            migrar.CommandText =
                $"CREATE TABLE IF NOT EXISTS UsuarioRecompensas ({EsquemaUsuarioRecompensas});";
            migrar.ExecuteNonQuery();
            return;
        }

        var tareaId = columnas.Contains("TareaId") ? "TareaId" : "NULL";
        var grupoId = columnas.Contains("GrupoId") ? "GrupoId" : "NULL";
        var orden = columnas.Contains("Orden") ? "Orden" : "0";
        var origen = columnas.Contains("Origen") ? "Origen" : "'tareas'";

        using var transaccion = conexion.BeginTransaction();
        migrar.Transaction = transaccion;

        migrar.CommandText = $@"
            CREATE TABLE UsuarioRecompensasNuevo ({EsquemaUsuarioRecompensas});
            INSERT INTO UsuarioRecompensasNuevo
                (UsuarioId, RecompensaId, TareaId, GrupoId, Orden, Origen, OtorgadaEn)
            SELECT
                UsuarioId,
                RecompensaId,
                {tareaId},
                {grupoId},
                {orden},
                {origen},
                OtorgadaEn
            FROM UsuarioRecompensas;
            DROP TABLE UsuarioRecompensas;
            ALTER TABLE UsuarioRecompensasNuevo
                RENAME TO UsuarioRecompensas;
        ";
        migrar.ExecuteNonQuery();
        transaccion.Commit();
    }

    /* Las bases antiguas guardaban "tarea" (singular); el frontend solo
       entiende "tareas" y "enlace". Se unifica al arrancar. */
    private static void NormalizarOrigen(SqliteConnection conexion)
    {
        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            UPDATE UsuarioRecompensas
            SET Origen = 'tareas'
            WHERE Origen = 'tarea';
        ";
        comando.ExecuteNonQuery();
    }

    private static void SembrarCatalogo()
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        for (var i = 1; i <= 18; i++)
        {
            using var comando = conexion.CreateCommand();
            comando.CommandText = @"
                INSERT OR IGNORE INTO Recompensas (Id, Codigo, Nombre, Imagen)
                VALUES ($id, $codigo, $nombre, $imagen);
            ";
            comando.Parameters.AddWithValue("$id", i);
            comando.Parameters.AddWithValue("$codigo", $"photocard-{i:00}");
            comando.Parameters.AddWithValue("$nombre", $"Photocard {i:00}");
            comando.Parameters.AddWithValue("$imagen", $"fotocards/{i}.png");
            comando.ExecuteNonQuery();
        }
    }

    public static bool Existe(int recompensaId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT COUNT(*) FROM Recompensas WHERE Id = $id;";
        comando.Parameters.AddWithValue("$id", recompensaId);

        return Convert.ToInt32(comando.ExecuteScalar()) > 0;
    }

    public static void AsegurarPlanes()
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
            AsegurarPlan(grupoId);
        }
    }

    public static void AsegurarPlan(int grupoId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            INSERT OR IGNORE INTO GrupoRecompensasPlan
                (GrupoId, Orden, RecompensaId)
            VALUES ($grupo, 1, 1), ($grupo, 2, 2), ($grupo, 3, 3);
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.ExecuteNonQuery();
    }

    public static List<Recompensa> ObtenerPlan(int grupoId)
    {
        var plan = new List<Recompensa>();

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT r.Id, r.Codigo, r.Nombre, r.Imagen,
                   '2000-01-01T00:00:00.0000000Z', p.GrupoId, p.Orden, 'plan'
            FROM GrupoRecompensasPlan p
            JOIN Recompensas r ON r.Id = p.RecompensaId
            WHERE p.GrupoId = $grupo
            ORDER BY p.Orden;
        ";
        comando.Parameters.AddWithValue("$grupo", grupoId);

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            plan.Add(LeerRecompensa(lector));
        }

        return plan;
    }

    public static void GuardarPlan(int grupoId, IReadOnlyList<int> recompensaIds)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        using (var borrar = conexion.CreateCommand())
        {
            borrar.Transaction = transaccion;
            borrar.CommandText =
                "DELETE FROM GrupoRecompensasPlan WHERE GrupoId = $grupo;";
            borrar.Parameters.AddWithValue("$grupo", grupoId);
            borrar.ExecuteNonQuery();
        }

        for (var i = 0; i < recompensaIds.Count; i++)
        {
            using var insertar = conexion.CreateCommand();
            insertar.Transaction = transaccion;
            insertar.CommandText = @"
                INSERT INTO GrupoRecompensasPlan
                    (GrupoId, Orden, RecompensaId)
                VALUES ($grupo, $orden, $recompensa);
            ";
            insertar.Parameters.AddWithValue("$grupo", grupoId);
            insertar.Parameters.AddWithValue("$orden", i + 1);
            insertar.Parameters.AddWithValue("$recompensa", recompensaIds[i]);
            insertar.ExecuteNonQuery();
        }

        transaccion.Commit();
    }

    public static string ObtenerOCrearToken(int grupoId, int usuarioId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using (var buscar = conexion.CreateCommand())
        {
            buscar.CommandText = @"
                SELECT Token FROM EnlacesRecompensa
                WHERE GrupoId = $grupo AND UsuarioId = $usuario
                LIMIT 1;
            ";
            buscar.Parameters.AddWithValue("$grupo", grupoId);
            buscar.Parameters.AddWithValue("$usuario", usuarioId);

            var token = buscar.ExecuteScalar() as string;
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        var nuevo = RandomNumberGenerator.GetHexString(24);

        using (var insertar = conexion.CreateCommand())
        {
            insertar.CommandText = @"
                INSERT OR IGNORE INTO EnlacesRecompensa
                    (GrupoId, UsuarioId, Token)
                VALUES ($grupo, $usuario, $token);
            ";
            insertar.Parameters.AddWithValue("$grupo", grupoId);
            insertar.Parameters.AddWithValue("$usuario", usuarioId);
            insertar.Parameters.AddWithValue("$token", nuevo);
            insertar.ExecuteNonQuery();
        }

        using (var buscar = conexion.CreateCommand())
        {
            buscar.CommandText = @"
                SELECT Token FROM EnlacesRecompensa
                WHERE GrupoId = $grupo AND UsuarioId = $usuario
                LIMIT 1;
            ";
            buscar.Parameters.AddWithValue("$grupo", grupoId);
            buscar.Parameters.AddWithValue("$usuario", usuarioId);

            return (string)buscar.ExecuteScalar()!;
        }
    }

    public static (int GrupoId, int UsuarioId)? BuscarPorToken(string token)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT GrupoId, UsuarioId FROM EnlacesRecompensa
            WHERE Token = $token
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$token", token);

        using var lector = comando.ExecuteReader();
        if (!lector.Read())
        {
            return null;
        }

        return (lector.GetInt32(0), lector.GetInt32(1));
    }

    public static List<Recompensa> DeUsuario(int usuarioId)
    {
        var recompensas = new List<Recompensa>();

        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var comando = conexion.CreateCommand();
        comando.CommandText = @"
            SELECT r.Id, r.Codigo, r.Nombre, r.Imagen, ur.OtorgadaEn,
                   ur.GrupoId, ur.Orden, ur.Origen
            FROM UsuarioRecompensas ur
            JOIN Recompensas r ON r.Id = ur.RecompensaId
            WHERE ur.UsuarioId = $usuario
            ORDER BY ur.OtorgadaEn DESC, r.Id;
        ";
        comando.Parameters.AddWithValue("$usuario", usuarioId);

        using var lector = comando.ExecuteReader();
        while (lector.Read())
        {
            recompensas.Add(LeerRecompensa(lector));
        }

        return recompensas;
    }

    private static Recompensa LeerRecompensa(SqliteDataReader lector)
    {
        return new Recompensa
        {
            Id = lector.GetInt32(0),
            Codigo = lector.GetString(1),
            Nombre = lector.GetString(2),
            Imagen = lector.GetString(3),
            OtorgadaEn = lector.GetString(4),
            GrupoId = lector.IsDBNull(5) ? null : lector.GetInt32(5),
            Orden = lector.GetInt32(6),
            Origen = lector.GetString(7)
        };
    }

    public static Recompensa? OtorgarAleatoria(int usuarioId, int tareaId)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        var otorgada = OtorgarAleatoriaEnTransaccion(
            conexion, transaccion, usuarioId, tareaId);
        transaccion.Commit();
        return otorgada;
    }

    internal static Recompensa? OtorgarAleatoriaEnTransaccion(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        int usuarioId,
        int tareaId)
    {
        using (var anterior = conexion.CreateCommand())
        {
            anterior.Transaction = transaccion;
            anterior.CommandText = @"
                SELECT r.Id, r.Codigo, r.Nombre, r.Imagen, ur.OtorgadaEn,
                       ur.GrupoId, ur.Orden, ur.Origen
                FROM UsuarioRecompensas ur
                JOIN Recompensas r ON r.Id = ur.RecompensaId
                WHERE ur.UsuarioId = $usuario AND ur.TareaId = $tarea
                LIMIT 1;
            ";
            anterior.Parameters.AddWithValue("$usuario", usuarioId);
            anterior.Parameters.AddWithValue("$tarea", tareaId);

            using var lector = anterior.ExecuteReader();
            if (lector.Read())
            {
                var existente = LeerRecompensa(lector);
                lector.Close();
                return existente;
            }
        }

        var candidatos = new List<int>();
        using (var disponibles = conexion.CreateCommand())
        {
            disponibles.Transaction = transaccion;
            disponibles.CommandText = @"
                SELECT r.Id
                FROM Recompensas r
                WHERE NOT EXISTS (
                    SELECT 1 FROM UsuarioRecompensas ur
                    WHERE ur.UsuarioId = $usuario AND ur.RecompensaId = r.Id
                )
                ORDER BY r.Id;
            ";
            disponibles.Parameters.AddWithValue("$usuario", usuarioId);

            using var lector = disponibles.ExecuteReader();
            while (lector.Read())
            {
                candidatos.Add(lector.GetInt32(0));
            }
        }

        if (candidatos.Count == 0)
        {
            return null;
        }

        var elegido = candidatos[Random.Shared.Next(candidatos.Count)];
        using (var insertar = conexion.CreateCommand())
        {
            insertar.Transaction = transaccion;
            insertar.CommandText = @"
                INSERT OR IGNORE INTO UsuarioRecompensas
                    (UsuarioId, RecompensaId, TareaId, OtorgadaEn)
                VALUES ($usuario, $recompensa, $tarea, $fecha);
            ";
            insertar.Parameters.AddWithValue("$usuario", usuarioId);
            insertar.Parameters.AddWithValue("$recompensa", elegido);
            insertar.Parameters.AddWithValue("$tarea", tareaId);
            insertar.Parameters.AddWithValue("$fecha", DateTime.UtcNow.ToString("o"));
            insertar.ExecuteNonQuery();
        }

        using (var seleccionar = conexion.CreateCommand())
        {
            seleccionar.Transaction = transaccion;
            seleccionar.CommandText = @"
                SELECT r.Id, r.Codigo, r.Nombre, r.Imagen, ur.OtorgadaEn,
                       ur.GrupoId, ur.Orden, ur.Origen
                FROM UsuarioRecompensas ur
                JOIN Recompensas r ON r.Id = ur.RecompensaId
                WHERE ur.UsuarioId = $usuario AND ur.TareaId = $tarea
                LIMIT 1;
            ";
            seleccionar.Parameters.AddWithValue("$usuario", usuarioId);
            seleccionar.Parameters.AddWithValue("$tarea", tareaId);

            using var lector = seleccionar.ExecuteReader();
            if (!lector.Read())
            {
                return null;
            }

            var otorgada = LeerRecompensa(lector);
            lector.Close();
            return otorgada;
        }
    }

    public static Recompensa? OtorgarDelPlan(int grupoId, int usuarioId, int orden)
    {
        using var conexion = new SqliteConnection(CadenaConexion);
        conexion.Open();

        using var transaccion = conexion.BeginTransaction();

        var otorgada = OtorgarDelPlanEnTransaccion(
            conexion, transaccion, grupoId, usuarioId, orden);
        transaccion.Commit();
        return otorgada;
    }

    internal static Recompensa? OtorgarDelPlanEnTransaccion(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        int grupoId,
        int usuarioId,
        int orden)
    {
        var existente = LeerDeSlot(conexion, transaccion, grupoId, usuarioId, orden);
        if (existente is not null)
        {
            return existente;
        }

        var plan = new List<int>();
        using (var comando = conexion.CreateCommand())
        {
            comando.Transaction = transaccion;
            comando.CommandText = @"
                SELECT RecompensaId FROM GrupoRecompensasPlan
                WHERE GrupoId = $grupo
                ORDER BY CASE WHEN Orden = $orden THEN 0 ELSE 1 END, Orden;
            ";
            comando.Parameters.AddWithValue("$grupo", grupoId);
            comando.Parameters.AddWithValue("$orden", orden);

            using var lector = comando.ExecuteReader();
            while (lector.Read())
            {
                plan.Add(lector.GetInt32(0));
            }
        }

        var elegido = 0;
        foreach (var candidato in plan)
        {
            using var usada = conexion.CreateCommand();
            usada.Transaction = transaccion;
            usada.CommandText = @"
                SELECT COUNT(*) FROM UsuarioRecompensas
                WHERE UsuarioId = $usuario AND GrupoId = $grupo
                  AND RecompensaId = $recompensa;
            ";
            usada.Parameters.AddWithValue("$usuario", usuarioId);
            usada.Parameters.AddWithValue("$grupo", grupoId);
            usada.Parameters.AddWithValue("$recompensa", candidato);

            if (Convert.ToInt32(usada.ExecuteScalar()) == 0)
            {
                elegido = candidato;
                break;
            }
        }

        if (elegido == 0)
        {
            return null;
        }

        using (var insertar = conexion.CreateCommand())
        {
            insertar.Transaction = transaccion;
            insertar.CommandText = @"
                INSERT OR IGNORE INTO UsuarioRecompensas
                    (UsuarioId, RecompensaId, GrupoId, Orden, Origen, OtorgadaEn)
                VALUES ($usuario, $recompensa, $grupo, $orden, $origen, $fecha);
            ";
            insertar.Parameters.AddWithValue("$usuario", usuarioId);
            insertar.Parameters.AddWithValue("$recompensa", elegido);
            insertar.Parameters.AddWithValue("$grupo", grupoId);
            insertar.Parameters.AddWithValue("$orden", orden);
            insertar.Parameters.AddWithValue(
                "$origen", orden == 3 ? "enlace" : "tareas");
            insertar.Parameters.AddWithValue("$fecha", DateTime.UtcNow.ToString("o"));
            insertar.ExecuteNonQuery();
        }

        return LeerDeSlot(conexion, transaccion, grupoId, usuarioId, orden);
    }

    private static Recompensa? LeerDeSlot(
        SqliteConnection conexion,
        SqliteTransaction transaccion,
        int grupoId,
        int usuarioId,
        int orden)
    {
        using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText = @"
            SELECT r.Id, r.Codigo, r.Nombre, r.Imagen, ur.OtorgadaEn,
                   ur.GrupoId, ur.Orden, ur.Origen
            FROM UsuarioRecompensas ur
            JOIN Recompensas r ON r.Id = ur.RecompensaId
            WHERE ur.UsuarioId = $usuario AND ur.GrupoId = $grupo
              AND ur.Orden = $orden
            LIMIT 1;
        ";
        comando.Parameters.AddWithValue("$usuario", usuarioId);
        comando.Parameters.AddWithValue("$grupo", grupoId);
        comando.Parameters.AddWithValue("$orden", orden);

        using var lector = comando.ExecuteReader();

        return lector.Read() ? LeerRecompensa(lector) : null;
    }
}
