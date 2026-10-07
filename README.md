# Deploy

Copias de referencia de los archivos de deploy (antes vivían fuera del repo). Ninguno tiene secretos.

| Archivo | Dónde se usa |
|---|---|
| `Dockerfile`, `.dockerignore` | Copiar a la carpeta del build Linux Dedicated Server (`SpellHaul-LinuxServer\`) |
| `MultiplayerSettings.json` | Carpeta de LocalMultiplayerAgent (`C:\PlayFabVmAgent`) |
| `launch_player2.bat` | Junto a `SpellHaul.exe`; solo para 2 instancias en la MISMA PC |

## Build de la imagen
    docker build -t spellhaul-server:v4 .

## Test local (LMA)
1. Crear `C:\output\SpellHaulServer` (LMA no la crea).
2. `LocalMultiplayerAgent.exe -lcow`
3. Si cambia la imagen, actualizar `ImageTag` en `MultiplayerSettings.json`.

## Azure
- Subir la imagen con "Upload to container registry" / "Copy Docker Login Command" en Game Manager.
- Después de cada test: Standby en 0 y borrar builds viejas (cuota 750 core-hours).

## Notas
- Puerto del juego: UDP 7770 (`LaunchArgs.cs`).
- El Dockerfile asume el binario `SpellHaul-LinuxServer.x86_64` y el target Dedicated Server.
