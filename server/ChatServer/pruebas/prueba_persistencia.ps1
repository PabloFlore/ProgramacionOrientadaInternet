# Pruebas de persistencia del historial (API y hub SignalR).
#
# Requisitos: haber compilado el backend (dotnet build en server/ChatServer).
# Qué hace: copia web/ y el binario a una carpeta temporal con su propia base
# de datos, arranca el servidor y envía mensajes reales por SignalR (WebSocket).
# Luego cierra sesión, reinicia el servidor y compara el historial antes y
# después. También comprueba que quien salió de un grupo conserva la lectura
# pero no puede escribir. No toca server/ChatServer/data/chat.db.
#
# Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_persistencia.ps1

$ErrorActionPreference = "Stop"

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_persistencia"
$base = "https://localhost:5000"
$wsHub = "wss://localhost:5000/chat"

# El servidor solo habla HTTPS: usa el certificado del proyecto y exige que
# la CA local esté instalada en el equipo (tools\generar_certificados.ps1).
$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Genéralo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con: dotnet build (en server/ChatServer)."
}

if (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto 5000 ya está en uso. No se ejecuta la prueba para no tocar el servidor real."
}

Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$raiz\server" -Force | Out-Null
Copy-Item $webOrigen "$raiz\web" -Recurse
Copy-Item $binOrigen "$raiz\server\bin" -Recurse
Remove-Item "$raiz\server\bin\chat.db*" -Force -ErrorAction SilentlyContinue
$env:PIA_CARPETA_DATOS = "$raiz\datos"

function Llamar([string]$metodo, [string]$url, $cuerpo = $null, [string]$token = "") {
    $cabeceras = @{}
    if ($token -ne "") { $cabeceras["Authorization"] = "Bearer $token" }

    $params = @{
        Method = $metodo
        Uri = $url
        Headers = $cabeceras
        UseBasicParsing = $true
        ContentType = "application/json; charset=utf-8"
    }

    if ($null -ne $cuerpo) {
        $json = $cuerpo | ConvertTo-Json -Depth 5 -Compress
        $params["Body"] = [System.Text.Encoding]::UTF8.GetBytes($json)
    }

    try {
        $r = Invoke-WebRequest @params
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Raw = $r.Content; Body = ($r.Content | ConvertFrom-Json) }
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $lector = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $texto = $lector.ReadToEnd()
        $cuerpoRespuesta = $null
        try { $cuerpoRespuesta = $texto | ConvertFrom-Json } catch { }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Raw = $texto; Body = $cuerpoRespuesta }
    }
}

$resultados = New-Object System.Collections.ArrayList
function Afirmar([string]$nombre, [bool]$ok) {
    [void]$resultados.Add([pscustomobject]@{ Prueba = $nombre; Resultado = $(if ($ok) { "OK" } else { "FALLA" }) })
}

function Arrancar {
    $p = Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = Llamar "GET" "$base/api/usuarios"
            if ($x.Status -eq 401) { return $p }
        } catch { }
    }
    throw "El servidor de prueba no arrancó."
}

function Detener($p) {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "El puerto 5000 no se liberó al detener el servidor."
}

# ---- WebSocket de SignalR (protocolo JSON, registros separados por 0x1E) ----

function Enviar-Trama($ws, [string]$texto) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto + [char]0x1e)
    $segmento = [System.ArraySegment[byte]]::new($bytes)
    $ws.SendAsync($segmento, [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
        [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Recibir-Trama($ws) {
    $buffer = New-Object byte[] 65536
    $segmento = [System.ArraySegment[byte]]::new($buffer)
    $texto = New-Object System.Text.StringBuilder
    do {
        $r = $ws.ReceiveAsync($segmento, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        [void]$texto.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
    } while (-not $r.EndOfMessage)
    return $texto.ToString()
}

# Invoca SendToGroup como el usuario del token. Devuelve Ok=$false si el hub
# respondió con error (por ejemplo, quien ya no es integrante).
function Enviar-Hub([string]$token, [string]$chatId, [string]$texto) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [System.Uri]::new("$wsHub`?access_token=$token")
    $ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()

    try {
        Enviar-Trama $ws '{"protocol":"json","version":1}'
        [void](Recibir-Trama $ws)

        $invocacion = @{
            type = 1
            invocationId = "1"
            target = "SendToGroup"
            arguments = @($chatId, $texto, "tmp-$([guid]::NewGuid())")
        } | ConvertTo-Json -Depth 5 -Compress

        Enviar-Trama $ws $invocacion

        for ($i = 0; $i -lt 10; $i++) {
            $tramas = Recibir-Trama $ws
            foreach ($linea in ($tramas -split [char]0x1e)) {
                if ($linea.Trim() -eq "") { continue }
                $mensaje = $linea | ConvertFrom-Json
                if ($mensaje.type -eq 3 -and $mensaje.invocationId -eq "1") {
                    return [pscustomobject]@{ Ok = -not $mensaje.error; Error = $mensaje.error }
                }
            }
        }
        throw "El hub no respondió a SendToGroup."
    } finally {
        if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "",
                [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        $ws.Dispose()
    }
}

function Recibir-TramaTimeout($ws, [int]$ms) {
    $buffer = New-Object byte[] 65536
    $segmento = [System.ArraySegment[byte]]::new($buffer)
    $texto = New-Object System.Text.StringBuilder
    $origen = [System.Threading.CancellationTokenSource]::new($ms)
    try {
        do {
            $r = $ws.ReceiveAsync($segmento, $origen.Token).GetAwaiter().GetResult()
            [void]$texto.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
        } while (-not $r.EndOfMessage)
        return $texto.ToString()
    } finally {
        $origen.Dispose()
    }
}

# Cierra la sesión con un hub abierto: comprueba el evento SesionRevocada y
# que las invocaciones posteriores se rechazan aunque el WebSocket siga vivo.
function Probar-RevocacionEnVivo([string]$token, [string]$chatId) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [System.Uri]::new("$wsHub`?access_token=$token")
    $ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()

    try {
        Enviar-Trama $ws '{"protocol":"json","version":1}'
        [void](Recibir-Trama $ws)

        $id = "rev-$([guid]::NewGuid())"
        $antes = @{
            type = 1
            invocationId = $id
            target = "SendToGroup"
            arguments = @($chatId, "mensaje antes de revocar", "tmp-$id")
        } | ConvertTo-Json -Depth 5 -Compress
        Enviar-Trama $ws $antes

        $okAntes = $false
        for ($i = 0; $i -lt 10; $i++) {
            foreach ($linea in ((Recibir-TramaTimeout $ws 5000) -split [char]0x1e)) {
                if ($linea.Trim() -eq "") { continue }
                $mensaje = $linea | ConvertFrom-Json
                if ($mensaje.type -eq 3 -and $mensaje.invocationId -eq $id) {
                    $okAntes = -not $mensaje.error
                    break
                }
            }
            if ($okAntes) { break }
        }
        if (-not $okAntes) {
            return [pscustomobject]@{ Ok = $false; Detalle = "la invocación previa al logout falló" }
        }

        $salida = Llamar "POST" "$base/api/logout" $null $token

        $aviso = $false
        for ($i = 0; $i -lt 10 -and -not $aviso; $i++) {
            foreach ($linea in ((Recibir-TramaTimeout $ws 5000) -split [char]0x1e)) {
                if ($linea.Trim() -eq "") { continue }
                $mensaje = $linea | ConvertFrom-Json
                if ($mensaje.target -eq "SesionRevocada") { $aviso = $true; break }
            }
        }

        $idDespues = "rev2-$([guid]::NewGuid())"
        $despues = @{
            type = 1
            invocationId = $idDespues
            target = "SendToGroup"
            arguments = @($chatId, "mensaje despues de revocar", "tmp-$idDespues")
        } | ConvertTo-Json -Depth 5 -Compress
        Enviar-Trama $ws $despues

        $errorHub = $null
        for ($i = 0; $i -lt 10; $i++) {
            foreach ($linea in ((Recibir-TramaTimeout $ws 5000) -split [char]0x1e)) {
                if ($linea.Trim() -eq "") { continue }
                $mensaje = $linea | ConvertFrom-Json
                if ($mensaje.type -eq 3 -and $mensaje.invocationId -eq $idDespues) {
                    $errorHub = $mensaje.error
                    break
                }
            }
            if ($null -ne $errorHub) { break }
        }

        return [pscustomobject]@{
            Ok = ($salida.Status -eq 200 -and $aviso -and $null -ne $errorHub)
            Detalle = "logout=$($salida.Status); aviso=$aviso; error=$([bool]$errorHub)"
        }
    } finally {
        if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "",
                [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        $ws.Dispose()
    }
}

# Textos del historial de un chat, en orden cronológico.
function Historial([string]$token, [string]$chat) {
    $r = Llamar "GET" "$base/api/mensajes?chatId=$chat&limite=50" $null $token
    if ($r.Status -ne 200) { return @("HTTP $($r.Status)") }
    return @($r.Body.Mensajes | ForEach-Object { $_.Texto })
}

$proc = $null
try {
    $proc = Arrancar

    $pass = "Clave#2026x"
    $tokens = @{}
    $ids = @{}
    foreach ($n in 1..4) {
        $correo = "persist$n@mail.com"
        Llamar "POST" "$base/api/registrar" @{ Nombre = "Persona$n"; Apellido = "Prueba"; Usuario = "p$n"; Correo = $correo; Contrasena = $pass } | Out-Null
        $l = Llamar "POST" "$base/api/login" @{ Correo = $correo; Contrasena = $pass }
        $tokens[$n] = $l.Body.Token
        $ids[$n] = $l.Body.Id
    }

    # ---- Chat privado entre 1 y 2 y canal público ----
    $menor = [Math]::Min($ids[1], $ids[2])
    $mayor = [Math]::Max($ids[1], $ids[2])
    $dm = "dm:$menor`:$mayor"

    $e1 = Enviar-Hub $tokens[1] $dm "dm-1 hola desde 1"
    $e2 = Enviar-Hub $tokens[2] $dm "dm-2 respuesta desde 2"
    $e3 = Enviar-Hub $tokens[2] "amigos" "canal-2 mensaje publico"
    Afirmar "el hub acepta mensajes privados y de canal (sin error)" ($e1.Ok -and $e2.Ok -and $e3.Ok)

    # ---- Grupo de 4 (creador 1) ----
    $grupo = Llamar "POST" "$base/api/grupos" @{ Nombre = "Persistencia"; Miembros = @($ids[2], $ids[3], $ids[4]) } $tokens[1]
    Afirmar "se crea el grupo de 4 integrantes (201)" ($grupo.Status -eq 201)
    $chatGrupo = "grupo:$($grupo.Body.Id)"

    Enviar-Hub $tokens[1] $chatGrupo "grp-1 hola al grupo" | Out-Null
    Enviar-Hub $tokens[2] $chatGrupo "grp-2 segundo mensaje" | Out-Null
    Enviar-Hub $tokens[3] $chatGrupo "grp-3 mensaje de quien saldra" | Out-Null
    Enviar-Hub $tokens[4] $chatGrupo "grp-4 ultimo antes de reiniciar" | Out-Null

    # El integrante 3 sale. Quedan 3, así que la salida se permite.
    $sale = Llamar "DELETE" "$base/api/grupos/$($grupo.Body.Id)/miembros/$($ids[3])" $null $tokens[3]
    Afirmar "un integrante sale del grupo (200)" ($sale.Status -eq 200)

    $intento = Enviar-Hub $tokens[3] $chatGrupo "grp-3 despues de salir (no debe guardarse)"
    Afirmar "quien salió no puede enviar al grupo (error del hub)" (-not $intento.Ok)

    $antesDm = Historial $tokens[1] $dm
    $antesGrupo = Historial $tokens[1] $chatGrupo
    $antesCanal = Historial $tokens[1] "amigos"

    Afirmar "antes del reinicio hay 2 mensajes privados" ($antesDm.Count -eq 2)
    Afirmar "antes del reinicio hay 1 mensaje en el canal publico" ($antesCanal.Count -eq 1)
    Afirmar "antes del reinicio hay 4 mensajes en el grupo (el intento de quien salió no se guarda)" ($antesGrupo.Count -eq 4)

    $lecturaAntes = Historial $tokens[3] $chatGrupo
    Afirmar "quien salió lee el historial del grupo antes del reinicio" ($lecturaAntes.Count -eq 4)

    # ---- Cerrar sesión de 1 (con hub abierto) y 2 ----
    $revocacion = Probar-RevocacionEnVivo $tokens[1] "revocacion"
    Afirmar "logout notifica SesionRevocada al hub abierto" $revocacion.Ok

    $out2 = Llamar "POST" "$base/api/logout" $null $tokens[2]
    Afirmar "cerrar sesión responde 200" ($out2.Status -eq 200)

    $despuesLogout = Llamar "GET" "$base/api/usuarios" $null $tokens[1]
    Afirmar "el token cerrado ya no sirve (401)" ($despuesLogout.Status -eq 401)

    # ---- Reinicio del servidor ----
    Detener $proc
    $proc = Arrancar

    Afirmar "la base de datos se conserva en la carpeta de datos" (Test-Path -LiteralPath "$raiz\datos\chat.db")

    $nuevo = @{}
    foreach ($n in 1..4) {
        $l = Llamar "POST" "$base/api/login" @{ Correo = "persist$n@mail.com"; Contrasena = $pass }
        $nuevo[$n] = $l.Body.Token
    }

    $despuesDm = Historial $nuevo[1] $dm
    $despuesGrupo = Historial $nuevo[1] $chatGrupo
    $despuesCanal = Historial $nuevo[1] "amigos"

    Afirmar "el chat privado sobrevive al reinicio, mismos mensajes y orden" (($despuesDm -join "|") -eq ($antesDm -join "|"))
    Afirmar "el grupo sobrevive al reinicio, mismos mensajes y orden" (($despuesGrupo -join "|") -eq ($antesGrupo -join "|"))
    Afirmar "el canal público sobrevive al reinicio" (($despuesCanal -join "|") -eq ($antesCanal -join "|"))

    $lecturaDespues = Historial $nuevo[3] $chatGrupo
    Afirmar "quien salió sigue leyendo el historial tras el reinicio" (($lecturaDespues -join "|") -eq ($lecturaAntes -join "|"))

    $escribe = Enviar-Hub $nuevo[1] $dm "dm-1 despues del reinicio"
    $despuesEscribir = Historial $nuevo[2] $dm
    Afirmar "tras el reinicio se puede seguir escribiendo y se guarda" ($escribe.Ok -and $despuesEscribir.Count -eq 3)

    $intentoDespues = Enviar-Hub $nuevo[3] $chatGrupo "grp-3 tras reinicio (no debe guardarse)"
    $grupoFinal = Historial $nuevo[1] $chatGrupo
    Afirmar "quien salió sigue sin poder escribir tras el reinicio" ((-not $intentoDespues.Ok) -and $grupoFinal.Count -eq 4)
}
finally {
    Detener $proc
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\PIA_CARPETA_DATOS -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200
