$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_ubicacion"
$base = "https://localhost:5000"
$wsHub = "wss://localhost:5000/chat"
$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Genéralo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

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
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path

function Iniciar-Servidor {
    return Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" -PassThru -WindowStyle Hidden
}

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

function Esperar-Servidor {
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        try {
            $x = Llamar "GET" "$base/api/usuarios"
            if ($x.Status -eq 401) { return }
        } catch { }
    }
    throw "El servidor de prueba no arrancó."
}

function Enviar-Trama($ws, [string]$texto) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto + [char]0x1e)
    $segmento = [System.ArraySegment[byte]]::new($bytes)
    [void]$ws.SendAsync($segmento, [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
        [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Recibir-Trama($ws, [int]$ms = 5000) {
    $buffer = New-Object byte[] 65536
    $segmento = [System.ArraySegment[byte]]::new($buffer)
    $texto = New-Object System.Text.StringBuilder
    $cts = [System.Threading.CancellationTokenSource]::new($ms)

    try {
        do {
            $r = $ws.ReceiveAsync($segmento, $cts.Token).GetAwaiter().GetResult()
            if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                throw "El servidor cerró el WebSocket."
            }
            [void]$texto.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
        } while (-not $r.EndOfMessage)
    } catch [System.OperationCanceledException] {
        return $null
    } finally {
        $cts.Dispose()
    }

    return $texto.ToString()
}

function Convertir-Tramas([string]$texto) {
    $lista = @()
    foreach ($linea in ($texto -split [char]0x1e)) {
        if ($linea.Trim() -eq "") { continue }
        $lista += ($linea | ConvertFrom-Json)
    }
    return $lista
}

function Conectar-Hub([string]$token) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [System.Uri]::new("$wsHub`?access_token=$token")
    [void]$ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Enviar-Trama $ws '{"protocol":"json","version":1}'
    $saludo = Recibir-Trama $ws
    if ($null -eq $saludo) { throw "El hub no respondió al saludo." }
    return $ws
}

function Invocar($ws, [string]$target, $argumentos = @(), [int]$ms = 5000) {
    $id = [guid]::NewGuid().ToString()
    $invocacion = @{
        type = 1
        invocationId = $id
        target = $target
        arguments = @($argumentos)
    } | ConvertTo-Json -Depth 8 -Compress

    Enviar-Trama $ws $invocacion
    $limite = [DateTime]::UtcNow.AddMilliseconds($ms)

    while ([DateTime]::UtcNow -lt $limite) {
        $texto = Recibir-Trama $ws 1000
        if ($null -eq $texto) { continue }

        foreach ($m in (Convertir-Tramas $texto)) {
            if ($m.type -eq 3 -and $m.invocationId -eq $id) {
                return [pscustomobject]@{
                    Ok = -not $m.error
                    Error = $m.error
                    Result = $m.result
                }
            }
        }
    }

    throw "El hub no respondió a $target."
}

function Esperar-Evento($ws, [string]$target, [int]$ms = 5000) {
    $limite = [DateTime]::UtcNow.AddMilliseconds($ms)

    while ([DateTime]::UtcNow -lt $limite) {
        $texto = Recibir-Trama $ws 1000
        if ($null -eq $texto) { continue }

        foreach ($m in (Convertir-Tramas $texto)) {
            if ($m.type -eq 1 -and $m.target -eq $target) {
                return $m
            }
        }
    }

    return $null
}

$wsA = $null
$wsB = $null
$wsC = $null
$proc = $null

try {
    $proc = Iniciar-Servidor
    Esperar-Servidor

    $pass = "Clave#2026x"
    $cuentas = @(
        @{ Nombre = "Ana"; Apellido = "Uno"; Usuario = "ana"; Correo = "ana.ubicacion@mail.com" },
        @{ Nombre = "Beto"; Apellido = "Dos"; Usuario = "beto"; Correo = "beto.ubicacion@mail.com" },
        @{ Nombre = "Caro"; Apellido = "Tres"; Usuario = "caro"; Correo = "caro.ubicacion@mail.com" }
    )

    $tokens = @()
    $ids = @()

    foreach ($cuenta in $cuentas) {
        Llamar "POST" "$base/api/registrar" @{
            Nombre = $cuenta.Nombre
            Apellido = $cuenta.Apellido
            Usuario = $cuenta.Usuario
            Correo = $cuenta.Correo
            Contrasena = $pass
        } | Out-Null
        $l = Llamar "POST" "$base/api/login" @{ Correo = $cuenta.Correo; Contrasena = $pass }
        $tokens += $l.Body.Token
        $ids += [int]$l.Body.Id
    }

    Afirmar "se registran las 3 cuentas" ($ids.Count -eq 3)

    $dm = "dm:$([Math]::Min($ids[0], $ids[1])):$([Math]::Max($ids[0], $ids[1]))"
    $lat = 19.432608
    $lon = -99.133209

    $wsA = Conectar-Hub $tokens[0]
    $wsB = Conectar-Hub $tokens[1]
    $wsC = Conectar-Hub $tokens[2]

    Afirmar "A y B se unen al DM" (
        (Invocar $wsA "JoinGroup" @($dm)).Ok -and
        (Invocar $wsB "JoinGroup" @($dm)).Ok
    )

    $envio = Invocar $wsA "SendToGroupUbicacion" @($dm, $lat, $lon, "Mi ubicación", "temp-ubi-1")
    Afirmar "A comparte una ubicación en el DM" ($envio.Ok)

    $recibida = Esperar-Evento $wsB "ReceiveUbicacion"
    Afirmar "B recibe ReceiveUbicacion" ($null -ne $recibida)
    Afirmar "ReceiveUbicacion trae 10 argumentos" ($null -ne $recibida -and $recibida.arguments.Count -eq 10)
    Afirmar "el evento identifica al emisor y al chat" (
        $null -ne $recibida -and
        $recibida.arguments[0] -eq "Ana Uno" -and
        $recibida.arguments[1] -eq $dm -and
        $recibida.arguments[2] -eq $ids[0]
    )
    Afirmar "el evento trae coordenadas válidas y etiqueta" (
        $null -ne $recibida -and
        [Math]::Abs([double]$recibida.arguments[6] - $lat) -lt 0.000001 -and
        [Math]::Abs([double]$recibida.arguments[7] - $lon) -lt 0.000001 -and
        $recibida.arguments[8] -eq "Mi ubicación"
    )
    Afirmar "el evento conserva el tempId" ($null -ne $recibida -and $recibida.arguments[5] -eq "temp-ubi-1")

    $historial = Llamar "GET" "$base/api/mensajes?chatId=$dm&limite=50" $null $tokens[0]
    $ubicacion = @($historial.Body.Mensajes | Where-Object { $_.Tipo -eq "ubicacion" })[0]
    Afirmar "el historial marca Tipo=ubicacion" ($null -ne $ubicacion)
    Afirmar "el historial guarda coordenadas y etiqueta" (
        $null -ne $ubicacion -and
        [Math]::Abs([double]$ubicacion.Latitud - $lat) -lt 0.000001 -and
        [Math]::Abs([double]$ubicacion.Longitud - $lon) -lt 0.000001 -and
        $ubicacion.Etiqueta -eq "Mi ubicación"
    )
    Afirmar "el texto de una ubicación queda vacío" ($null -ne $ubicacion -and $ubicacion.Texto -eq "")

    $conversaciones = Llamar "GET" "$base/api/conversaciones" $null $tokens[1]
    $conv = @($conversaciones.Body.Conversaciones | Where-Object { $_.ChatId -eq $dm })[0]
    Afirmar "la vista previa de la conversación marca UltimoTipo=ubicacion" (
        $null -ne $conv -and $conv.UltimoTipo -eq "ubicacion"
    )

    $etiquetaVacia = Invocar $wsA "SendToGroupUbicacion" @($dm, 19.43, -99.13, "   ", "temp-ubi-2")
    Afirmar "una etiqueta vacía se acepta" ($etiquetaVacia.Ok)
    $eventoVacio = Esperar-Evento $wsB "ReceiveUbicacion"
    Afirmar "la etiqueta vacía se normaliza a 'Mi ubicación'" (
        $null -ne $eventoVacio -and $eventoVacio.arguments[8] -eq "Mi ubicación"
    )

    $latMala = Invocar $wsA "SendToGroupUbicacion" @($dm, 91, $lon, "Mi ubicación", "temp-malo-1")
    Afirmar "una latitud fuera de rango se rechaza" (-not $latMala.Ok)

    $lonMala = Invocar $wsA "SendToGroupUbicacion" @($dm, $lat, -181, "Mi ubicación", "temp-malo-2")
    Afirmar "una longitud fuera de rango se rechaza" (-not $lonMala.Ok)

    $etiquetaLarga = Invocar $wsA "SendToGroupUbicacion" @($dm, $lat, $lon, ("x" * 121), "temp-malo-3")
    Afirmar "una etiqueta de más de 120 caracteres se rechaza" (-not $etiquetaLarga.Ok)

    $ajeno = Invocar $wsC "SendToGroupUbicacion" @($dm, $lat, $lon, "Mi ubicación", "temp-ajeno")
    Afirmar "un tercero no puede compartir ubicación en el DM" (-not $ajeno.Ok)

    $grupo = Llamar "POST" "$base/api/grupos" @{
        Nombre = "Ubicacion"
        Miembros = @($ids[1], $ids[2])
    } $tokens[0]
    Afirmar "se crea el grupo de prueba" ($grupo.Status -eq 201)
    $chatGrupo = $grupo.Body.ChatId

    Afirmar "A, B y C se unen al grupo" (
        (Invocar $wsA "JoinGroup" @($chatGrupo)).Ok -and
        (Invocar $wsB "JoinGroup" @($chatGrupo)).Ok -and
        (Invocar $wsC "JoinGroup" @($chatGrupo)).Ok
    )

    $envioGrupo = Invocar $wsA "SendToGroupUbicacion" @($chatGrupo, 19.44, -99.14, "Mi ubicación", "temp-grupo")
    Afirmar "A comparte una ubicación en el grupo" ($envioGrupo.Ok)
    $recibidaGrupo = Esperar-Evento $wsB "ReceiveUbicacion"
    Afirmar "B recibe la ubicación del grupo" (
        $null -ne $recibidaGrupo -and $recibidaGrupo.arguments[1] -eq $chatGrupo
    )

    $historialGrupo = Llamar "GET" "$base/api/mensajes?chatId=$chatGrupo&limite=50" $null $tokens[2]
    $ubicacionGrupo = @($historialGrupo.Body.Mensajes | Where-Object { $_.Tipo -eq "ubicacion" })[0]
    Afirmar "el historial del grupo guarda la ubicación" ($null -ne $ubicacionGrupo)

    $texto = Invocar $wsA "SendToGroup" @($dm, "hola", "temp-texto")
    Afirmar "los mensajes de texto siguen funcionando" ($texto.Ok)
    $recibidoTexto = Esperar-Evento $wsB "ReceiveMessage"
    Afirmar "ReceiveMessage conserva 12 argumentos" (
        $null -ne $recibidoTexto -and
        $recibidoTexto.arguments.Count -eq 12 -and
        $recibidoTexto.arguments[1] -eq "hola"
    )

    Stop-Process -Id $proc.Id -Force
    Wait-Process -Id $proc.Id -ErrorAction SilentlyContinue
    $proc = $null
    Start-Sleep -Milliseconds 800

    $proc = Iniciar-Servidor
    Esperar-Servidor

    $historialPersistido = Llamar "GET" "$base/api/mensajes?chatId=$dm&limite=50" $null $tokens[0]
    $persistida = @($historialPersistido.Body.Mensajes | Where-Object { $_.Tipo -eq "ubicacion" })[0]
    Afirmar "la ubicación persiste tras reiniciar el servidor" (
        $null -ne $persistida -and
        [Math]::Abs([double]$persistida.Latitud - $lat) -lt 0.000001 -and
        $persistida.Etiqueta -eq "Mi ubicación"
    )
}
finally {
    foreach ($ws in @($wsA, $wsB, $wsC)) {
        if ($null -ne $ws) {
            try { $ws.Dispose() } catch { }
        }
    }

    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200
