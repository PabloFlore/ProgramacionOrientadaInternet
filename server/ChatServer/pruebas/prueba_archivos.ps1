$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

Add-Type -TypeDefinition @"
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class CertificadoPruebas {
    public static readonly RemoteCertificateValidationCallback Aceptar =
        (remitente, certificado, cadena, errores) => true;
}
"@
[Net.ServicePointManager]::ServerCertificateValidationCallback = [CertificadoPruebas]::Aceptar

$raizProyecto = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$binOrigen = Resolve-Path (Join-Path $PSScriptRoot "..\bin\Debug\net8.0")
$webOrigen = Join-Path $raizProyecto "web"
$raiz = Join-Path $env:TEMP "pia_prueba_archivos"
$base = "https://localhost:5000"
$wsHub = "wss://localhost:5000/chat"
$certificado = Join-Path $PSScriptRoot "..\certs\servidor.pfx"
$puertoStub = 5099
$stubDir = Join-Path $raiz "cloudinary"
$entrada = Join-Path $raiz "entrada"

if (-not (Test-Path -LiteralPath $certificado)) {
    throw "No existe el certificado HTTPS. Genéralo con: powershell -File tools\generar_certificados.ps1 -Ip <IP>"
}

if (-not (Test-Path -LiteralPath (Join-Path $binOrigen "ChatServer.dll"))) {
    throw "No existe ChatServer.dll. Compila primero con: dotnet build (en server/ChatServer)."
}

if (Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto 5000 ya está en uso. No se ejecuta la prueba para no tocar el servidor real."
}

if (Get-NetTCPConnection -LocalPort $puertoStub -State Listen -ErrorAction SilentlyContinue) {
    throw "El puerto $puertoStub ya está en uso. Cierra el proceso que lo ocupa."
}

Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$raiz\server" -Force | Out-Null
New-Item -ItemType Directory -Path $stubDir -Force | Out-Null
New-Item -ItemType Directory -Path $entrada -Force | Out-Null
Copy-Item $webOrigen "$raiz\web" -Recurse
Copy-Item $binOrigen "$raiz\server\bin" -Recurse
Remove-Item "$raiz\server\bin\chat.db*" -Force -ErrorAction SilentlyContinue
$env:PIA_CARPETA_DATOS = "$raiz\datos"
$env:PIA_CERTIFICADO = (Resolve-Path $certificado).Path
$env:Cloudinary__CloudName = "demo"
$env:Cloudinary__UploadPreset = "musichub_unsigned"
$env:Cloudinary__Carpeta = "musichub"
$env:Cloudinary__ApiBase = "http://127.0.0.1:$puertoStub"

function Iniciar-Servidor {
    $script:arranques++
    $salida = Join-Path $raiz "servidor$($script:arranques).out.log"
    $errores = Join-Path $raiz "servidor$($script:arranques).err.log"
    return Start-Process dotnet -ArgumentList "`"$raiz\server\bin\ChatServer.dll`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput $salida -RedirectStandardError $errores
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

function Subir-Archivo([string]$ruta, [string]$chatId, [string]$token, [string]$tempId = "", [string]$nombre = "") {
    $cliente = New-Object System.Net.Http.HttpClient
    $form = $null

    try {
        if ($token -ne "") {
            $cliente.DefaultRequestHeaders.Authorization =
                [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
        }

        $form = [System.Net.Http.MultipartFormDataContent]::new()
        $bytes = [System.IO.File]::ReadAllBytes($ruta)
        $parte = [System.Net.Http.ByteArrayContent]::new($bytes)
        $parte.Headers.ContentType =
            [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/octet-stream")

        if ($nombre -eq "") {
            $nombre = [System.IO.Path]::GetFileName($ruta)
        }

        $form.Add($parte, "archivo", $nombre)
        $form.Add([System.Net.Http.StringContent]::new($chatId), "chatId")

        if ($tempId -ne "") {
            $form.Add([System.Net.Http.StringContent]::new($tempId), "tempId")
        }

        $resp = $cliente.PostAsync("$base/api/archivos", $form)
        $resp.Wait()
        $resultado = $resp.Result
        $texto = $resultado.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $cuerpo = $null
        try { $cuerpo = $texto | ConvertFrom-Json } catch { }

        return [pscustomobject]@{
            Status = [int]$resultado.StatusCode
            Raw = $texto
            Body = $cuerpo
        }
    } finally {
        if ($null -ne $form) { $form.Dispose() }
        $cliente.Dispose()
    }
}

function Descargar-Archivo([string]$url, [string]$token) {
    $cliente = New-Object System.Net.Http.HttpClient

    try {
        if ($token -ne "") {
            $cliente.DefaultRequestHeaders.Authorization =
                [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
        }

        $resp = $cliente.GetAsync($url)
        $resp.Wait()
        $resultado = $resp.Result
        $bytes = $resultado.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $cabeceras = @{}

        foreach ($h in $resultado.Content.Headers) {
            $cabeceras[$h.Key.ToLowerInvariant()] = ($h.Value -join ",")
        }

        return [pscustomobject]@{
            Status = [int]$resultado.StatusCode
            Bytes = $bytes
            Headers = $cabeceras
        }
    } finally {
        $cliente.Dispose()
    }
}

$script:arranques = 0
$script:recepciones = @{}

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
    $detalle = ""
    foreach ($log in Get-ChildItem "$raiz\servidor*.log" -ErrorAction SilentlyContinue) {
        $detalle += "`n--- $($log.Name) ---`n" + (Get-Content $log.FullName -Raw -ErrorAction SilentlyContinue)
    }
    throw "El servidor de prueba no arrancó.$detalle"
}

function Enviar-Trama($ws, [string]$texto) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($texto + [char]0x1e)
    $segmento = [System.ArraySegment[byte]]::new($bytes)
    [void]$ws.SendAsync($segmento, [System.Net.WebSockets.WebSocketMessageType]::Text, $true,
        [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Recibir-Trama($ws, [int]$ms = 5000) {
    $estado = $script:recepciones[$ws]
    if ($null -eq $estado) {
        $buffer = New-Object byte[] 65536
        $estado = [pscustomobject]@{
            Tarea = $null
            Buffer = $buffer
            Segmento = [System.ArraySegment[byte]]::new($buffer)
            Texto = New-Object System.Text.StringBuilder
        }
        $script:recepciones[$ws] = $estado
    }

    $limite = [DateTime]::UtcNow.AddMilliseconds($ms)

    while ([DateTime]::UtcNow -lt $limite) {
        if ($null -eq $estado.Tarea) {
            $estado.Tarea = $ws.ReceiveAsync(
                $estado.Segmento,
                [System.Threading.CancellationToken]::None)
        }

        $restante = [int][Math]::Max(0, ($limite - [DateTime]::UtcNow).TotalMilliseconds)
        if (-not $estado.Tarea.Wait($restante)) { return $null }

        try {
            $r = $estado.Tarea.Result
        } catch {
            $estado.Tarea = $null
            throw $_.Exception.GetBaseException()
        }

        $estado.Tarea = $null

        if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            throw "El servidor cerró el WebSocket."
        }

        [void]$estado.Texto.Append(
            [System.Text.Encoding]::UTF8.GetString($estado.Buffer, 0, $r.Count))

        if ($r.EndOfMessage) {
            $texto = $estado.Texto.ToString()
            [void]$estado.Texto.Clear()
            return $texto
        }
    }

    return $null
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

function Crear-Archivo([string]$ruta, [byte[]]$contenido) {
    [System.IO.File]::WriteAllBytes($ruta, $contenido)
}

$stub = $null
$wsA = $null
$wsB = $null
$wsC = $null
$proc = $null

try {
    $stub = Start-Job -ArgumentList $puertoStub, $stubDir -ScriptBlock {
        param($puerto, $carpeta)

        function Leer-Request($stream) {
            $memoria = New-Object System.IO.MemoryStream
            $buffer = New-Object byte[] 8192
            $finCabecera = -1
            $texto = ""

            while ($finCabecera -lt 0) {
                $n = $stream.Read($buffer, 0, $buffer.Length)
                if ($n -le 0) { return $null }
                $memoria.Write($buffer, 0, $n)
                $texto = [System.Text.Encoding]::GetEncoding(28591).GetString($memoria.ToArray())
                $finCabecera = $texto.IndexOf("`r`n`r`n")
            }

            $lineas = $texto.Substring(0, $finCabecera) -split "`r`n"
            $partes = $lineas[0] -split " "
            $cabeceras = @{}

            for ($i = 1; $i -lt $lineas.Count; $i++) {
                $indice = $lineas[$i].IndexOf(":")
                if ($indice -gt 0) {
                    $cabeceras[$lineas[$i].Substring(0, $indice).ToLowerInvariant()] =
                        $lineas[$i].Substring($indice + 1).Trim()
                }
            }

            $longitud = 0
            if ($cabeceras.ContainsKey("content-length")) {
                $longitud = [int]$cabeceras["content-length"]
            }

            $todos = $memoria.ToArray()
            $inicioCuerpo = $finCabecera + 4
            $cuerpo = New-Object System.IO.MemoryStream

            if ($todos.Length -gt $inicioCuerpo) {
                $cuerpo.Write($todos, $inicioCuerpo, $todos.Length - $inicioCuerpo)
            }

            $restantes = $longitud - $cuerpo.Length

            while ($restantes -gt 0) {
                $n = $stream.Read($buffer, 0, [Math]::Min($buffer.Length, $restantes))
                if ($n -le 0) { break }
                $cuerpo.Write($buffer, 0, $n)
                $restantes -= $n
            }

            return [pscustomobject]@{
                Metodo = $partes[0]
                Ruta = $partes[1]
                Cabeceras = $cabeceras
                Cuerpo = $cuerpo.ToArray()
            }
        }

        function Responder($stream, [int]$codigo, [string]$mime, [byte[]]$cuerpo) {
            $motivos = @{ 200 = "OK"; 400 = "Bad Request"; 404 = "Not Found"; 500 = "Error" }
            $motivo = $motivos[$codigo]
            if ($null -eq $motivo) { $motivo = "Error" }
            $cabecera = "HTTP/1.1 $codigo $motivo`r`n" +
                "Content-Type: $mime`r`n" +
                "Content-Length: $($cuerpo.Length)`r`n" +
                "Connection: close`r`n`r`n"
            $bytes = [System.Text.Encoding]::ASCII.GetBytes($cabecera)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Write($cuerpo, 0, $cuerpo.Length)
            $stream.Flush()
        }

        function Extraer-Parte([byte[]]$cuerpo, [string]$contentType) {
            $boundary = [regex]::Match($contentType, 'boundary="?([^";]+)"?').Groups[1].Value
            if ($boundary -eq "") { return $null }

            $latin = [System.Text.Encoding]::GetEncoding(28591)
            $texto = $latin.GetString($cuerpo)
            $coincidencia = [regex]::Match(
                $texto,
                'Content-Disposition:\s*form-data;\s*name\s*=\s*"?file"?')
            if (-not $coincidencia.Success) { return $null }
            $marca = $coincidencia.Index

            $finCabecera = $texto.IndexOf("`r`n`r`n", $marca)
            if ($finCabecera -lt 0) { return $null }

            $inicio = $finCabecera + 4
            $fin = $texto.IndexOf("`r`n--$boundary", $inicio)
            if ($fin -lt 0) { return $null }

            $cabecera = $texto.Substring($marca, $finCabecera - $marca)
            $mime = [regex]::Match($cabecera, 'Content-Type:\s*([^\r\n]+)').Groups[1].Value.Trim()

            return [pscustomobject]@{
                Datos = $latin.GetBytes($texto.Substring($inicio, $fin - $inicio))
                Mime = $mime
            }
        }

        $listener = [System.Net.Sockets.TcpListener]::new(
            [System.Net.IPAddress]::Loopback,
            $puerto
        )
        $listener.Start()
        $contador = 0

        while ($true) {
            $cliente = $listener.AcceptTcpClient()

            try {
                $stream = $cliente.GetStream()
                $peticion = Leer-Request $stream

                if ($null -eq $peticion) { continue }

                if ($peticion.Metodo -eq "GET" -and $peticion.Ruta -eq "/estado") {
                    Responder $stream 200 "text/plain" ([System.Text.Encoding]::ASCII.GetBytes("ok"))
                    continue
                }

                if ($peticion.Metodo -eq "GET" -and $peticion.Ruta -match "^/files/(archivo\d+)$") {
                    $id = $Matches[1]
                    $rutaBin = Join-Path $carpeta "$id.bin"

                    if (Test-Path $rutaBin) {
                        $meta = Get-Content (Join-Path $carpeta "$id.json") -Raw | ConvertFrom-Json
                        $mime = if ($meta.Mime -ne "") { $meta.Mime } else { "application/octet-stream" }
                        Responder $stream 200 $mime ([System.IO.File]::ReadAllBytes($rutaBin))
                    } else {
                        Responder $stream 404 "text/plain" ([System.Text.Encoding]::ASCII.GetBytes("no"))
                    }
                    continue
                }

                if ($peticion.Metodo -eq "POST" -and
                    $peticion.Ruta -match "^/v1_1/[^/]+/(image|raw)/upload$") {
                    $recurso = $Matches[1]
                    $texto = [System.Text.Encoding]::GetEncoding(28591).GetString($peticion.Cuerpo)

                    if ($texto -notmatch 'name\s*=\s*"?upload_preset"?' -or
                        $texto -notmatch 'name\s*=\s*"?folder"?') {
                        Write-Output "STUB faltan campos: $($texto.Substring(0, [Math]::Min(600, $texto.Length)))"
                        Responder $stream 400 "application/json" ([System.Text.Encoding]::UTF8.GetBytes('{"error":"faltan campos"}'))
                        continue
                    }

                    $parte = Extraer-Parte $peticion.Cuerpo $peticion.Cabeceras["content-type"]

                    if ($null -eq $parte) {
                        Write-Output "STUB sin archivo; cuerpo len=$($peticion.Cuerpo.Length)"
                        Responder $stream 400 "application/json" ([System.Text.Encoding]::UTF8.GetBytes('{"error":"sin archivo"}'))
                        continue
                    }

                    Write-Output "STUB archivo $($parte.Datos.Length) bytes mime=$($parte.Mime)"

                    $contador++
                    $id = "archivo$contador"
                    [System.IO.File]::WriteAllBytes((Join-Path $carpeta "$id.bin"), $parte.Datos)
                    @{ Recurso = $recurso; Mime = $parte.Mime } |
                        ConvertTo-Json -Compress |
                        Set-Content (Join-Path $carpeta "$id.json") -Encoding UTF8

                    $url = "http://127.0.0.1:$puerto/files/$id"
                    $json = @{
                        secure_url = $url
                        public_id = "musichub/$id"
                        bytes = $parte.Datos.Length
                        resource_type = $recurso
                    } | ConvertTo-Json -Compress

                    Responder $stream 200 "application/json" ([System.Text.Encoding]::UTF8.GetBytes($json))
                    continue
                }

                Responder $stream 404 "text/plain" ([System.Text.Encoding]::ASCII.GetBytes("no"))
            } catch {
                try {
                    Responder $cliente.GetStream() 500 "text/plain" ([System.Text.Encoding]::ASCII.GetBytes("error"))
                } catch { }
            } finally {
                try { $cliente.Close() } catch { }
            }
        }
    }

    $stubListo = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        try {
            $r = Invoke-WebRequest "http://127.0.0.1:$puertoStub/estado" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { $stubListo = $true; break }
        } catch { }
    }

    if (-not $stubListo) {
        throw "El stub de Cloudinary no arrancó: $(Receive-Job $stub -Keep)"
    }

    $proc = Iniciar-Servidor
    Esperar-Servidor

    $pass = "Clave#2026x"
    $cuentas = @(
        @{ Nombre = "Ana"; Apellido = "Uno"; Usuario = "ana"; Correo = "ana.archivos@mail.com" },
        @{ Nombre = "Beto"; Apellido = "Dos"; Usuario = "beto"; Correo = "beto.archivos@mail.com" },
        @{ Nombre = "Caro"; Apellido = "Tres"; Usuario = "caro"; Correo = "caro.archivos@mail.com" },
        @{ Nombre = "Dora"; Apellido = "Cuatro"; Usuario = "dora"; Correo = "dora.archivos@mail.com" }
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

    Afirmar "se registran las 4 cuentas" ($ids.Count -eq 4)

    $dm = "dm:$([Math]::Min($ids[0], $ids[1])):$([Math]::Max($ids[0], $ids[1]))"

    $wsA = Conectar-Hub $tokens[0]
    $wsB = Conectar-Hub $tokens[1]
    $wsC = Conectar-Hub $tokens[2]

    Afirmar "A y B se unen al DM" (
        (Invocar $wsA "JoinGroup" @($dm)).Ok -and
        (Invocar $wsB "JoinGroup" @($dm)).Ok
    )

    $nota = Join-Path $entrada "nota.txt"
    $textoNota = "Hola desde Music Hub.`nSegunda línea con acentos: áéíóú.`n"
    [System.IO.File]::WriteAllText($nota, $textoNota, (New-Object System.Text.UTF8Encoding($false)))

    $png = Join-Path $entrada "foto.png"
    $pngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="
    Crear-Archivo $png ([Convert]::FromBase64String($pngBase64))

    $grande = Join-Path $entrada "grande.txt"
    Crear-Archivo $grande ([byte[]]::new((2 * 1024 * 1024) + 1))

    $malo = Join-Path $entrada "malo.exe"
    Crear-Archivo $malo ([System.Text.Encoding]::ASCII.GetBytes("MZ programa"))

    $falso = Join-Path $entrada "falso.txt"
    Crear-Archivo $falso ([byte[]](0x00, 0xFF, 0xFE, 0x00))

    $vacio = Join-Path $entrada "vacio.txt"
    Crear-Archivo $vacio ([byte[]]::new(0))

    $envioTexto = Subir-Archivo $nota $dm $tokens[0] "temp-arch-1"
    Write-Output "DEBUG upload: $($envioTexto.Status) $($envioTexto.Raw)"
    Afirmar "A sube un TXT al DM" ($envioTexto.Status -eq 200)
    Afirmar "el TXT se guarda como Tipo=archivo" (
        $null -ne $envioTexto.Body -and $envioTexto.Body.Tipo -eq "archivo"
    )
    Afirmar "el TXT conserva nombre, MIME y tamaño" (
        $null -ne $envioTexto.Body -and
        $envioTexto.Body.ArchivoNombre -eq "nota.txt" -and
        $envioTexto.Body.ArchivoMime -eq "text/plain" -and
        [long]$envioTexto.Body.ArchivoTamano -eq (New-Object System.IO.FileInfo($nota)).Length
    )
    Afirmar "el texto del mensaje adjunto queda vacío" (
        $null -ne $envioTexto.Body -and $envioTexto.Body.Texto -eq ""
    )
    Afirmar "Cloudinary recibe resource_type=raw y el preset" (
        $null -ne $envioTexto.Body -and
        $envioTexto.Body.ArchivoUrl -like "http://127.0.0.1:$puertoStub/files/*"
    )

    $eventoTexto = Esperar-Evento $wsB "ReceiveArchivo"
    Afirmar "B recibe ReceiveArchivo" ($null -ne $eventoTexto)
    Afirmar "ReceiveArchivo trae 11 argumentos" (
        $null -ne $eventoTexto -and $eventoTexto.arguments.Count -eq 11
    )
    Afirmar "el evento identifica emisor, chat y archivo" (
        $null -ne $eventoTexto -and
        $eventoTexto.arguments[0] -eq "Ana Uno" -and
        $eventoTexto.arguments[1] -eq $dm -and
        $eventoTexto.arguments[2] -eq $ids[0] -and
        $eventoTexto.arguments[6] -eq "nota.txt" -and
        $eventoTexto.arguments[7] -eq "text/plain"
    )
    Afirmar "el evento conserva el tempId" (
        $null -ne $eventoTexto -and $eventoTexto.arguments[10] -eq "temp-arch-1"
    )

    $historial = Llamar "GET" "$base/api/mensajes?chatId=$dm&limite=50" $null $tokens[1]
    $adjunto = @($historial.Body.Mensajes | Where-Object { $_.Tipo -eq "archivo" })[0]
    Afirmar "el historial guarda el adjunto con su URL y MIME" (
        $null -ne $adjunto -and
        $adjunto.ArchivoNombre -eq "nota.txt" -and
        $adjunto.ArchivoMime -eq "text/plain" -and
        $adjunto.ArchivoUrl -eq $envioTexto.Body.ArchivoUrl
    )

    $descarga = Descargar-Archivo "$base/api/archivos/$($envioTexto.Body.Id)?descargar=true" $tokens[1]
    Afirmar "B descarga el TXT" ($descarga.Status -eq 200)
    Afirmar "la descarga devuelve el contenido original" (
        [System.Text.Encoding]::UTF8.GetString($descarga.Bytes) -eq $textoNota
    )
    Afirmar "la descarga usa Content-Disposition attachment" (
        $descarga.Headers.ContainsKey("content-disposition") -and
        $descarga.Headers["content-disposition"] -like "*attachment*" -and
        $descarga.Headers["content-disposition"] -like "*nota.txt*"
    )

    $vista = Llamar "GET" "$base/api/archivos/$($envioTexto.Body.Id)/texto" $null $tokens[1]
    Afirmar "B obtiene la vista previa del TXT" (
        $vista.Status -eq 200 -and
        $vista.Body.Texto -eq $textoNota -and
        $vista.Body.Truncado -eq $false
    )

    $ajena = Descargar-Archivo "$base/api/archivos/$($envioTexto.Body.Id)" $tokens[2]
    Afirmar "C no puede descargar un adjunto del DM" ($ajena.Status -eq 403)

    $sinSesion = Subir-Archivo $nota $dm "" "temp-sin"
    Afirmar "subir sin sesión responde 401" ($sinSesion.Status -eq 401)

    $ajenaSubida = Subir-Archivo $nota $dm $tokens[2] "temp-ajena"
    Afirmar "C no puede subir al DM de A y B" ($ajenaSubida.Status -eq 403)

    $envioImagen = Subir-Archivo $png $dm $tokens[0] "temp-img-1"
    Afirmar "A sube una PNG al DM" ($envioImagen.Status -eq 200)
    Afirmar "la PNG se detecta por sus bytes" (
        $null -ne $envioImagen.Body -and
        $envioImagen.Body.ArchivoMime -eq "image/png"
    )
    $eventoImagen = Esperar-Evento $wsB "ReceiveArchivo"
    Afirmar "B recibe la PNG por SignalR" (
        $null -ne $eventoImagen -and
        $eventoImagen.arguments[7] -eq "image/png"
    )

    $descargaImagen = Descargar-Archivo "$base/api/archivos/$($envioImagen.Body.Id)" $tokens[1]
    Afirmar "la imagen descargada coincide con la original" (
        $descargaImagen.Status -eq 200 -and
        [Convert]::ToBase64String($descargaImagen.Bytes) -eq $pngBase64
    )

    $grandeSubida = Subir-Archivo $grande $dm $tokens[0] "temp-grande"
    Afirmar "un archivo de más de 2 MB se rechaza con 413" ($grandeSubida.Status -eq 413)

    $maloSubida = Subir-Archivo $malo $dm $tokens[0] "temp-malo"
    Afirmar "un .exe se rechaza con 415" ($maloSubida.Status -eq 415)

    $falsoSubida = Subir-Archivo $falso $dm $tokens[0] "temp-falso"
    Afirmar "un .txt binario se rechaza con 415" ($falsoSubida.Status -eq 415)

    $vacioSubida = Subir-Archivo $vacio $dm $tokens[0] "temp-vacio"
    Afirmar "un archivo vacío se rechaza con 400" ($vacioSubida.Status -eq 400)

    $traversal = Subir-Archivo $nota $dm $tokens[0] "temp-ruta" "..\..\evil.txt"
    Afirmar "el nombre con ruta se sanea a evil.txt" (
        $traversal.Status -eq 200 -and
        $traversal.Body.ArchivoNombre -eq "evil.txt"
    )

    $conversaciones = Llamar "GET" "$base/api/conversaciones" $null $tokens[1]
    $conv = @($conversaciones.Body.Conversaciones | Where-Object { $_.ChatId -eq $dm })[0]
    Afirmar "la vista previa de la conversación marca UltimoTipo=archivo" (
        $null -ne $conv -and $conv.UltimoTipo -eq "archivo"
    )

    $grupo = Llamar "POST" "$base/api/grupos" @{
        Nombre = "Archivos"
        Miembros = @($ids[1], $ids[2])
    } $tokens[0]
    Afirmar "se crea el grupo de prueba" ($grupo.Status -eq 201)
    $chatGrupo = $grupo.Body.ChatId

    Afirmar "A, B y C se unen al grupo" (
        (Invocar $wsA "JoinGroup" @($chatGrupo)).Ok -and
        (Invocar $wsB "JoinGroup" @($chatGrupo)).Ok -and
        (Invocar $wsC "JoinGroup" @($chatGrupo)).Ok
    )

    $envioGrupo = Subir-Archivo $nota $chatGrupo $tokens[0] "temp-grupo"
    Afirmar "A sube un TXT al grupo" ($envioGrupo.Status -eq 200)
    $eventoGrupo = Esperar-Evento $wsB "ReceiveArchivo"
    Afirmar "B recibe el adjunto del grupo" (
        $null -ne $eventoGrupo -and $eventoGrupo.arguments[1] -eq $chatGrupo
    )

    $historialGrupo = Llamar "GET" "$base/api/mensajes?chatId=$chatGrupo&limite=50" $null $tokens[2]
    Afirmar "C lee el adjunto del grupo en el historial" (
        @($historialGrupo.Body.Mensajes | Where-Object { $_.Tipo -eq "archivo" }).Count -eq 1
    )

    $grupoAjeno = Llamar "POST" "$base/api/grupos" @{
        Nombre = "SinCaro"
        Miembros = @($ids[1], $ids[3])
    } $tokens[0]
    $chatSoloAB = $grupoAjeno.Body.ChatId
    $ajenoGrupo = Subir-Archivo $nota $chatSoloAB $tokens[2] "temp-grupo-ajeno"
    Afirmar "C no puede subir a un grupo del que no es miembro" ($ajenoGrupo.Status -eq 403)

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
    $persistido = @($historialPersistido.Body.Mensajes | Where-Object {
        $_.Tipo -eq "archivo" -and $_.ArchivoNombre -eq "nota.txt"
    })[0]
    Afirmar "el adjunto persiste tras reiniciar el servidor" (
        $null -ne $persistido -and
        $persistido.ArchivoUrl -eq $envioTexto.Body.ArchivoUrl
    )

    Write-Output "--- STUB ---"
    Receive-Job $stub -Keep -ErrorAction SilentlyContinue
    foreach ($log in Get-ChildItem "$raiz\servidor*.log" -ErrorAction SilentlyContinue) {
        Write-Output "--- $($log.Name) ---"
        Get-Content $log.FullName -ErrorAction SilentlyContinue
    }
}
catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    Write-Output "--- STUB ---"
    if ($stub) { Receive-Job $stub -Keep -ErrorAction SilentlyContinue }
    foreach ($log in Get-ChildItem "$raiz\servidor*.log" -ErrorAction SilentlyContinue) {
        Write-Output "--- $($log.Name) ---"
        Get-Content $log.FullName -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    foreach ($ws in @($wsA, $wsB, $wsC)) {
        if ($null -ne $ws) {
            try { $ws.Dispose() } catch { }
        }
    }

    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }

    if ($stub) {
        Stop-Job $stub -ErrorAction SilentlyContinue
        Remove-Job $stub -Force -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 500
    Remove-Item $raiz -Recurse -Force -ErrorAction SilentlyContinue
}

$resultados | Format-Table -AutoSize | Out-String -Width 200
