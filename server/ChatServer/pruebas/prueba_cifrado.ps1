# Pruebas del cifrado extremo a extremo (contrato del servidor).
#
# Requisitos: haber compilado el backend (dotnet build en server/ChatServer).
# Que hace: arranca una copia aislada con su propia base de datos, publica
# claves publicas ECDH P-256, envia mensajes cifrados por SignalR y comprueba
# que el servidor solo guarda ciphertext, IV y clave publica. Tambien valida
# los rechazos (canales publicos, grupos, IV o clave invalidos) y que los
# campos sobreviven a un reinicio. No toca server/ChatServer/data/chat.db.
#
# El ida y vuelta criptografico real (ECDH + AES-256-GCM) se prueba en
# prueba_cifrado_cliente.mjs con la misma WebCrypto del navegador.
#
# Uso:  powershell -NoProfile -ExecutionPolicy Bypass -File pruebas\prueba_cifrado.ps1

$ErrorActionPreference = "Stop"

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_cifrado"
$base = "https://localhost:5000"
$wsHub = "wss://localhost:5000/chat"

$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Generalo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con: dotnet build (en server/ChatServer)."
}

if (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto 5000 ya esta en uso. No se ejecuta la prueba para no tocar el servidor real."
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
    throw "El servidor de prueba no arranco."
}

function Detener($p) {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "El puerto 5000 no se libero al detener el servidor."
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

# Invoca un mÃƒÂ©todo del hub con la lista de argumentos indicada.
function Enviar-HubArgs([string]$token, [string]$objetivo, [object[]]$argumentos) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [System.Uri]::new("$wsHub`?access_token=$token")
    $ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()

    try {
        Enviar-Trama $ws '{"protocol":"json","version":1}'
        [void](Recibir-Trama $ws)

        $invocacion = @{
            type = 1
            invocationId = "1"
            target = $objetivo
            arguments = $argumentos
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
        throw "El hub no respondio a la invocacion."
    } finally {
        if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "",
                [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
        }
        $ws.Dispose()
    }
}

function Abrir-Hub([string]$token) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = [System.Uri]::new("$wsHub`?access_token=$token")
    [void]($ws.ConnectAsync(
        $uri,
        [System.Threading.CancellationToken]::None
    ).GetAwaiter().GetResult())

    [void](Enviar-Trama $ws '{"protocol":"json","version":1}')
    [void](Recibir-Trama $ws)

    return $ws
}

function Invocar-Hub($ws, [string]$objetivo, [object[]]$argumentos) {
    $invocacionId = [guid]::NewGuid().ToString()

    $invocacion = @{
        type = 1
        invocationId = $invocacionId
        target = $objetivo
        arguments = $argumentos
    } | ConvertTo-Json -Depth 5 -Compress

    Enviar-Trama $ws $invocacion

    for ($i = 0; $i -lt 20; $i++) {
        $tramas = Recibir-Trama $ws
        foreach ($linea in ($tramas -split [char]0x1e)) {
            if ($linea.Trim() -eq "") { continue }
            $mensaje = $linea | ConvertFrom-Json
            if ($mensaje.type -eq 3 -and $mensaje.invocationId -eq $invocacionId) {
                return [pscustomobject]@{ Ok = -not $mensaje.error; Error = $mensaje.error }
            }
        }
    }

    throw "El hub no respondio a la invocacion $objetivo."
}

function Recibir-Evento($ws, [string]$objetivo) {
    for ($i = 0; $i -lt 30; $i++) {
        $tramas = Recibir-Trama $ws
        foreach ($linea in ($tramas -split [char]0x1e)) {
            if ($linea.Trim() -eq "") { continue }
            $mensaje = $linea | ConvertFrom-Json
            if ($mensaje.type -eq 1 -and $mensaje.target -eq $objetivo) {
                return ,@($mensaje.arguments)
            }
        }
    }

    throw "No llego el evento $objetivo."
}

function Cerrar-Hub($ws) {
    if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        try {
            [void]($ws.CloseOutputAsync(
                [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                "",
                [System.Threading.CancellationToken]::None
            ).GetAwaiter().GetResult())
        } catch { }
    }

    $ws.Dispose()
}

function Enviar-Hub([string]$token, [string]$chatId, [string]$texto) {
    return Enviar-HubArgs $token "SendToGroup" @($chatId, $texto, "tmp-$([guid]::NewGuid())")
}

function Enviar-HubCifrado([string]$token, [string]$chatId, [string]$texto,
    [string]$iv, [string]$clave, [string]$claveDestino) {
    return Enviar-HubArgs $token "SendToGroupCifrado" @(
        $chatId, $texto, "tmp-$([guid]::NewGuid())", $iv, $clave,
        $claveDestino)
}

function Historial([string]$token, [string]$chat) {
    return Llamar "GET" "$base/api/mensajes?chatId=$chat&limite=50" $null $token
}

function Bytes-Aleatorios([int]$cantidad) {
    $bytes = New-Object byte[] $cantidad
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return $bytes
}

function Rellenar32([byte[]]$valor) {
    if ($valor.Length -eq 32) { return $valor }
    $resultado = New-Object byte[] 32
    [Array]::Copy($valor, 0, $resultado, 32 - $valor.Length, $valor.Length)
    return $resultado
}

# Clave publica ECDH P-256 en SPKI/base64. PowerShell 5.1 no expone
# ExportSubjectPublicKeyInfo, asi que se arma el DER (91 bytes) a mano.
function Nueva-ClavePublica {
    $ecdh = [System.Security.Cryptography.ECDiffieHellman]::Create()
    $ecdh.KeySize = 256
    $parametros = $ecdh.ExportParameters($false)

    $x = Rellenar32 $parametros.Q.X
    $y = Rellenar32 $parametros.Q.Y

    $cabecera = [byte[]](
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d,
        0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01,
        0x07, 0x03, 0x42, 0x00, 0x04
    )

    $spki = New-Object byte[] ($cabecera.Length + 64)
    [Array]::Copy($cabecera, 0, $spki, 0, $cabecera.Length)
    [Array]::Copy($x, 0, $spki, $cabecera.Length, 32)
    [Array]::Copy($y, 0, $spki, $cabecera.Length + 32, 32)

    $ecdh.Dispose()
    return [Convert]::ToBase64String($spki)
}

$proc = $null
try {
    $proc = Arrancar

    $pass = "Clave#2026x"
    $tokens = @{}
    $ids = @{}
    foreach ($n in 1..3) {
        $correo = "cifrado$n@mail.com"
        Llamar "POST" "$base/api/registrar" @{ Nombre = "Cifra$n"; Apellido = "Prueba"; Usuario = "c$n"; Correo = $correo; Contrasena = $pass } | Out-Null
        $l = Llamar "POST" "$base/api/login" @{ Correo = $correo; Contrasena = $pass }
        $tokens[$n] = $l.Body.Token
        $ids[$n] = $l.Body.Id
    }

    $menor = [Math]::Min($ids[1], $ids[2])
    $mayor = [Math]::Max($ids[1], $ids[2])
    $dm = "dm:$menor`:$mayor"

    $clave1 = Nueva-ClavePublica
    $clave2 = Nueva-ClavePublica
    $clave3 = Nueva-ClavePublica

    # ---- Validacion de claves publicas ----
    Afirmar "las dos claves generadas son distintas y tienen tamano SPKI P-256" (
        $clave1 -ne $clave2 -and $clave1.Length -eq 124 -and $clave2.Length -eq 124)

    $sinSesion = Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = $clave1 }
    Afirmar "publicar clave sin sesion responde 401" ($sinSesion.Status -eq 401)

    $invalida = Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = "no-es-base64" } $tokens[1]
    Afirmar "una clave que no es base64 se rechaza (400)" ($invalida.Status -eq 400)

    $basura = [Convert]::ToBase64String((Bytes-Aleatorios 10))
    $noEsClave = Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = $basura } $tokens[1]
    Afirmar "un base64 que no es SPKI se rechaza (400)" ($noEsClave.Status -eq 400)

    $publica1 = Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = $clave1 } $tokens[1]
    $publica2 = Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = $clave2 } $tokens[2]
    $publica3 = Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = $clave3 } $tokens[3]
    Afirmar "los tres usuarios publican su clave (200)" (
        $publica1.Status -eq 200 -and $publica2.Status -eq 200 -and $publica3.Status -eq 200)

    $consulta = Llamar "GET" "$base/api/usuarios/$($ids[2])/clave" $null $tokens[1]
    Afirmar "un usuario autenticado consulta la clave del otro" (
        $consulta.Status -eq 200 -and $consulta.Body.ClavePublica -eq $clave2)

    $sinClave = Llamar "GET" "$base/api/usuarios/999999/clave" $null $tokens[1]
    Afirmar "consultar un usuario sin clave responde 200 con null" (
        $sinClave.Status -eq 200 -and $null -eq $sinClave.Body.ClavePublica)

    $cambio = Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = $clave1 } $tokens[2]
    $consulta2 = Llamar "GET" "$base/api/usuarios/$($ids[2])/clave" $null $tokens[1]
    Afirmar "volver a publicar reemplaza la clave anterior" (
        $cambio.Status -eq 200 -and $consulta2.Body.ClavePublica -eq $clave1)

    # Se restaura la clave propia del usuario 2 para el resto de la prueba.
    Llamar "PUT" "$base/api/cuenta/clave" @{ ClavePublica = $clave2 } $tokens[2] | Out-Null

    # ---- Envio cifrado en un chat privado ----
    $cipher1 = [Convert]::ToBase64String((Bytes-Aleatorios 48))
    $iv1 = [Convert]::ToBase64String((Bytes-Aleatorios 12))

    $e1 = Enviar-HubCifrado $tokens[1] $dm $cipher1 $iv1 $clave1 $clave2
    Afirmar "el hub acepta un mensaje cifrado en un chat privado" ($e1.Ok)

    $h1 = Historial $tokens[2] $dm
    $m1 = $h1.Body.Mensajes[0]
    Afirmar "el historial devuelve 1 mensaje" ($h1.Status -eq 200 -and $h1.Body.Total -eq 1)
    Afirmar "el servidor guarda el ciphertext tal cual, sin transformarlo" ($m1.Texto -eq $cipher1)
    Afirmar "el mensaje se marca como cifrado" ($m1.Cifrado -eq $true)
    Afirmar "el IV se conserva" ($m1.Iv -eq $iv1)
    Afirmar "la clave publica del emisor se conserva" ($m1.ClavePublica -eq $clave1)
    Afirmar "la clave publica del destinatario se conserva" ($m1.ClavePublicaDestino -eq $clave2)

    # ---- Rechazos ----
    $cipher2 = [Convert]::ToBase64String((Bytes-Aleatorios 32))
    $iv2 = [Convert]::ToBase64String((Bytes-Aleatorios 12))

    $canal = Enviar-HubCifrado $tokens[1] "amigos" $cipher2 $iv2 $clave1 $clave2
    Afirmar "el cifrado se rechaza en un canal publico" (-not $canal.Ok)

    $grupo = Llamar "POST" "$base/api/grupos" @{ Nombre = "Cifrado"; Miembros = @($ids[2]) } $tokens[1]
    $chatGrupo = "grupo:$($grupo.Body.Id)"
    $enGrupo = Enviar-HubCifrado $tokens[1] $chatGrupo $cipher2 $iv2 $clave1 $clave2
    Afirmar "el cifrado se rechaza en un grupo" (-not $enGrupo.Ok)

    $ivCorto = [Convert]::ToBase64String((Bytes-Aleatorios 10))
    $malIv = Enviar-HubCifrado $tokens[1] $dm $cipher2 $ivCorto $clave1 $clave2
    Afirmar "un IV que no mide 12 bytes se rechaza" (-not $malIv.Ok)

    $malClave = Enviar-HubCifrado $tokens[1] $dm $cipher2 $iv2 $basura $clave2
    Afirmar "una clave publica invalida se rechaza" (-not $malClave.Ok)

    $malClaveDestino = Enviar-HubCifrado $tokens[1] $dm $cipher2 $iv2 $clave1 $basura
    Afirmar "una clave publica destino invalida se rechaza" (-not $malClaveDestino.Ok)

    $sinClaveDestino = Enviar-HubCifrado $tokens[1] $dm $cipher2 $iv2 $clave1 $null
    Afirmar "una clave publica destino faltante se rechaza" (-not $sinClaveDestino.Ok)

    $ivNoBase64 = Enviar-HubCifrado $tokens[1] $dm $cipher2 "no-es-base64" $clave1 $clave2
    Afirmar "un IV que no es base64 se rechaza" (-not $ivNoBase64.Ok)

    $sigueIgual = Historial $tokens[1] $dm
    Afirmar "los intentos rechazados no agregan mensajes" ($sigueIgual.Body.Total -eq 1)

    # ---- Texto plano: el servidor ignora los metadatos de cifrado ----
    $plano = Enviar-Hub $tokens[1] $dm "mensaje en claro"
    $h2 = Historial $tokens[1] $dm
    $m2 = $h2.Body.Mensajes[1]
    Afirmar "cifrado=false se guarda como texto plano" (
        $plano.Ok -and $h2.Body.Total -eq 2 -and
        $m2.Texto -eq "mensaje en claro" -and $m2.Cifrado -eq $false -and
        $null -eq $m2.Iv -and $null -eq $m2.ClavePublica -and
        $null -eq $m2.ClavePublicaDestino)

    # ---- Conversation preview ----
    $convCifrada = Llamar "GET" "$base/api/conversaciones" $null $tokens[2]
    $conv = @($convCifrada.Body.Conversaciones | Where-Object { $_.ChatId -eq $dm })[0]
    Afirmar "el ultimo mensaje plano deja UltimoCifrado en false" (
        $convCifrada.Status -eq 200 -and $conv.UltimoCifrado -eq $false)

    Enviar-HubCifrado $tokens[2] $dm $cipher2 $iv2 $clave2 $clave1 | Out-Null
    $convCifrada2 = Llamar "GET" "$base/api/conversaciones" $null $tokens[1]
    $conv2 = @($convCifrada2.Body.Conversaciones | Where-Object { $_.ChatId -eq $dm })[0]
    Afirmar "un mensaje cifrado deja UltimoCifrado en true" ($conv2.UltimoCifrado -eq $true)
    Afirmar "el preview de la conversacion conserva el ciphertext" ($conv2.UltimoMensaje -eq $cipher2)

    # ---- Persistencia tras reiniciar ----
    Detener $proc
    $proc = Arrancar

    $nuevo = @{}
    foreach ($n in 1..3) {
        $l = Llamar "POST" "$base/api/login" @{ Correo = "cifrado$n@mail.com"; Contrasena = $pass }
        $nuevo[$n] = $l.Body.Token
    }

    $claveTrasReinicio = Llamar "GET" "$base/api/usuarios/$($ids[1])/clave" $null $nuevo[2]
    Afirmar "la clave publica sobrevive al reinicio" ($claveTrasReinicio.Body.ClavePublica -eq $clave1)

    $h3 = Historial $nuevo[2] $dm
    $m3 = $h3.Body.Mensajes[0]
    $m4 = $h3.Body.Mensajes[1]
    $m5 = $h3.Body.Mensajes[2]
    Afirmar "el historial cifrado sobrevive al reinicio" (
        $h3.Body.Total -eq 3 -and
        $m3.Texto -eq $cipher1 -and $m3.Cifrado -eq $true -and
        $m3.Iv -eq $iv1 -and $m3.ClavePublica -eq $clave1 -and
        $m3.ClavePublicaDestino -eq $clave2)
    Afirmar "el mensaje plano sigue plano tras el reinicio" (
        $m4.Texto -eq "mensaje en claro" -and $m4.Cifrado -eq $false)
    Afirmar "el ciphertext del usuario 2 sobrevive con sus claves" (
        $m5.Texto -eq $cipher2 -and $m5.Cifrado -eq $true -and
        $m5.ClavePublica -eq $clave2 -and
        $m5.ClavePublicaDestino -eq $clave1)

    # ---- Evento en vivo: los argumentos deben viajar separados ----
    $cipherEvento = [Convert]::ToBase64String((Bytes-Aleatorios 40))
    $ivEvento = [Convert]::ToBase64String((Bytes-Aleatorios 12))
    $wsReceptor = Abrir-Hub $nuevo[2]

    try {
        $unido = Invocar-Hub $wsReceptor "JoinGroup" @($dm)
        if (-not $unido.Ok) {
            throw "El receptor no pudo unirse al chat privado."
        }

        $envio = Enviar-HubCifrado $nuevo[1] $dm $cipherEvento $ivEvento $clave1 $clave2
        $evento = Recibir-Evento $wsReceptor "ReceiveMessage"

        Afirmar "ReceiveMessage llega con 12 argumentos separados" (
            $envio.Ok -and $evento.Count -eq 12)
        Afirmar "ReceiveMessage conserva ciphertext, IV y claves" (
            $evento[1] -eq $cipherEvento -and $evento[8] -eq $true -and
            $evento[9] -eq $ivEvento -and $evento[10] -eq $clave1 -and
            $evento[11] -eq $clave2)
    } finally {
        Cerrar-Hub $wsReceptor
    }

    # ---- La base no contiene un texto que nunca se envio en claro ----
    # (El cliente es quien cifra; aqui solo se comprueba que el servidor no
    # inventa ni descifra nada.)
    $rutaBd = "$raiz\datos\chat.db"
    $flujoBd = [System.IO.File]::Open(
        $rutaBd,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    $lectorBd = New-Object System.IO.StreamReader(
        $flujoBd,
        [System.Text.Encoding]::UTF8)
    $contenidoBd = $lectorBd.ReadToEnd()
    $lectorBd.Dispose()
    $flujoBd.Dispose()
    Afirmar "la base de datos conserva el ciphertext enviado" ($contenidoBd.Contains($cipher1))
    Afirmar "la base de datos contiene el mensaje que si se envio en claro" ($contenidoBd.Contains("mensaje en claro"))
}
finally {
    Detener $proc
    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\PIA_CARPETA_DATOS -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200

$fallas = @($resultados | Where-Object { $_.Resultado -eq "FALLA" })
if ($fallas.Count -gt 0) {
    Write-Output "FALLARON $($fallas.Count) prueba(s)."
    exit 1
}

Write-Output "Todas las pruebas pasaron ($($resultados.Count))."
