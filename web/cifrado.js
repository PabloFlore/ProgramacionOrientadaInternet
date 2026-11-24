/* ============================================================
   Music Hub — cifrado extremo a extremo (chats privados)
   ECDH P-256 para acordar la clave + AES-256-GCM para el texto.

   Cada navegador genera su par de claves una vez y lo guarda en
   IndexedDB. La clave privada nunca sale del dispositivo; la pública
   se publica en el servidor para que el otro extremo derive la misma
   clave AES. El servidor solo almacena texto cifrado.
   ============================================================ */

(function (global) {
    "use strict";

    const BASE_DATOS = "musicHubCifrado";
    const VERSION = 1;
    const ALMACEN = "claves";
    const CLAVE_PAR = "par-ecdh";

    const ECDH = { name: "ECDH", namedCurve: "P-256" };
    const AES = { name: "AES-GCM", length: 256 };
    const IV_BYTES = 12;

    /* Par cargado en memoria y claves AES derivadas por clave pública. */
    let parActual = null;
    const clavesDerivadas = new Map();

    function soportado() {
        return typeof global.crypto !== "undefined" &&
            !!global.crypto.subtle &&
            typeof global.indexedDB !== "undefined" &&
            typeof global.TextEncoder !== "undefined";
    }

    function aBase64(buffer) {
        const bytes = new Uint8Array(buffer);
        let binario = "";

        for (let i = 0; i < bytes.length; i++) {
            binario += String.fromCharCode(bytes[i]);
        }

        return global.btoa(binario);
    }

    function deBase64(texto) {
        const binario = global.atob(texto);
        const bytes = new Uint8Array(binario.length);

        for (let i = 0; i < binario.length; i++) {
            bytes[i] = binario.charCodeAt(i);
        }

        return bytes;
    }

    function abrirBd() {
        return new Promise((resolve, reject) => {
            const solicitud = global.indexedDB.open(BASE_DATOS, VERSION);

            solicitud.onupgradeneeded = () => {
                const bd = solicitud.result;

                if (!bd.objectStoreNames.contains(ALMACEN)) {
                    bd.createObjectStore(ALMACEN);
                }
            };

            solicitud.onsuccess = () => resolve(solicitud.result);
            solicitud.onerror = () => reject(solicitud.error);
        });
    }

    async function leerParGuardado() {
        const bd = await abrirBd();

        try {
            return await new Promise((resolve, reject) => {
                const tx = bd.transaction(ALMACEN, "readonly");
                const solicitud = tx.objectStore(ALMACEN).get(CLAVE_PAR);

                solicitud.onsuccess = () => resolve(solicitud.result || null);
                solicitud.onerror = () => reject(solicitud.error);
            });
        } finally {
            bd.close();
        }
    }

    async function guardarPar(publica, privada) {
        const bd = await abrirBd();

        try {
            await new Promise((resolve, reject) => {
                const tx = bd.transaction(ALMACEN, "readwrite");

                tx.objectStore(ALMACEN).put(
                    { publica, privada },
                    CLAVE_PAR
                );

                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
                tx.onabort = () => reject(tx.error);
            });
        } finally {
            bd.close();
        }
    }

    /**
     * Devuelve el par ECDH del navegador. La primera vez lo genera y lo
     * guarda en IndexedDB; después se reutiliza aunque se recargue la
     * página. Si el almacenamiento falla, el par sigue en memoria.
     *
     * Las claves nuevas se generan con la privada no extraíble. Los pares
     * antiguos se conservan tal cual para no perder el historial cifrado.
     */
    async function obtenerPar() {
        if (parActual) {
            return parActual;
        }

        if (!soportado()) {
            throw new Error(
                "Este navegador no permite cifrado web. Usa HTTPS en Chrome, Edge o Firefox.");
        }

        try {
            const guardado = await leerParGuardado();

            if (guardado && guardado.publica && guardado.privada) {
                parActual = guardado;
                return parActual;
            }
        } catch (error) {
            /* Sin IndexedDB se sigue con un par temporal en memoria. */
        }

        const generado = await global.crypto.subtle.generateKey(
            ECDH,
            false,
            ["deriveKey", "deriveBits"]
        );

        parActual = {
            publica: generado.publicKey,
            privada: generado.privateKey
        };

        try {
            await guardarPar(parActual.publica, parActual.privada);
        } catch (error) {
            /* Se conserva en memoria durante esta sesión. */
        }

        return parActual;
    }

    /** Clave pública del dispositivo en SPKI/base64. */
    async function publicaBase64() {
        const par = await obtenerPar();
        const spki = await global.crypto.subtle.exportKey(
            "spki",
            par.publica
        );

        return aBase64(spki);
    }

    /**
     * Deriva la clave AES-256 del chat a partir de la clave pública del
     * otro extremo. Las claves derivadas se cachean por clave pública.
     */
    async function derivarClave(publicaBase64) {
        if (!publicaBase64 || typeof publicaBase64 !== "string") {
            throw new Error(
                "Falta la clave pública del otro usuario.");
        }

        if (clavesDerivadas.has(publicaBase64)) {
            return clavesDerivadas.get(publicaBase64);
        }

        const par = await obtenerPar();
        let importada;

        try {
            importada = await global.crypto.subtle.importKey(
                "spki",
                deBase64(publicaBase64),
                ECDH,
                true,
                []
            );
        } catch (error) {
            throw new Error(
                "La clave pública recibida no es válida.");
        }

        const clave = await global.crypto.subtle.deriveKey(
            { name: "ECDH", public: importada },
            par.privada,
            AES,
            false,
            ["encrypt", "decrypt"]
        );

        clavesDerivadas.set(publicaBase64, clave);
        return clave;
    }

    /** Cifra texto plano. Devuelve { texto, iv } en base64. */
    async function cifrar(texto, clave) {
        const iv = global.crypto.getRandomValues(
            new Uint8Array(IV_BYTES)
        );

        const datos = new global.TextEncoder().encode(texto);
        const cifrado = await global.crypto.subtle.encrypt(
            { name: "AES-GCM", iv },
            clave,
            datos
        );

        return {
            texto: aBase64(cifrado),
            iv: aBase64(iv)
        };
    }

    /** Descifra texto base64 con el IV base64 indicado. */
    async function descifrar(texto, iv, clave) {
        if (!texto || !iv) {
            throw new Error("El mensaje cifrado está incompleto.");
        }

        let datos;
        let vector;

        try {
            datos = deBase64(texto);
            vector = deBase64(iv);
        } catch (error) {
            throw new Error("El mensaje cifrado está dañado.");
        }

        const plano = await global.crypto.subtle.decrypt(
            { name: "AES-GCM", iv: vector },
            clave,
            datos
        );

        return new global.TextDecoder().decode(plano);
    }

    global.Cifrado = {
        soportado,
        obtenerPar,
        publicaBase64,
        derivarClave,
        cifrar,
        descifrar,
        IV_BYTES
    };
})(window);
