# SpellHaul — GDD técnico (v4)

Actualizado: 1 de octubre de 2026. Reemplaza a `GDD_v3.docx`.

**Cómo usar este documento.** Si el código y este documento no coinciden, vale el código. El documento describe qué hay, cómo está hecho y qué reglas seguir al tocarlo. Está escrito para que otra persona o IA pueda entender el proyecto sin el historial de conversaciones. Cada sistema indica su estado: **Implementado**, **Parcial** o **Pendiente**.

---

## 0. Reglas de trabajo (leer primero)

### Idioma
- Comentarios de código, `[Tooltip]` y `[Header]`: en español.
- Todo texto que ve el jugador (UI, mensajes, nombres de items): en inglés.

### Unity y assets
- No borrar ni recrear scripts que algún asset referencia: el GUID del `.meta` es su identidad. Para renombrar, hacerlo desde el Project window de Unity.
- No escribir a mano YAML de prefabs, escenas ni NetworkObjects. Dar los pasos para hacerlo en el Editor.
- Al renombrar un `[SerializeField]`, agregar `[FormerlySerializedAs("viejo")]`. Sin eso, el dato se pierde en silencio.
- Unity serializa los enums por número. Los valores nuevos van siempre al final. Reordenar o insertar en el medio rompe los assets guardados, así que hay que avisar antes de hacerlo.
- Los `.meta` y los assets generados por Unity los commitea el usuario.

### Arquitectura y red
- No reorganizar `Presentation/Combat` (carpetas ni namespaces).
- **Core** no puede depender de **Presentation**.
- Autoridad estricta del servidor: el cliente pide y el servidor valida y ejecuta. El cliente solo predice lo visual o lo local.
- No tocar Prediction v2 (`PlayerMovementController`: Replicate/Reconcile) sin avisar antes y explicar el riesgo.
- Un proceso de servidor sirve exactamente una run y después se cierra solo.

### Secretos y git
- La clave secreta del título de PlayFab nunca va al repositorio ni al cliente.
- Se commitea directo a `main`.
- Los commits no llevan líneas de atribución a IA (`Co-Authored-By`, etc.).

---

## 1. Concepto

Extraction shooter PvPvE en primera persona, con combate solo de magia (sin armas de fuego).

- El jugador entra a una run con su equipo.
- En la run pelea contra IA y otros jugadores, y junta loot.
- Para conservar lo que lleva tiene que llegar a una zona de extracción.
- Si muere, pierde todo lo que llevaba (riesgo total, estilo Tarkov).

Entre runs, el jugador gestiona inventario y stash en el menú (el hub).

**Pilares**
- Tensión de extracción.
- Maestría de habilidades (el skill pesa tanto como el equipo).
- Builds con trade-offs.
- Progresión persistente (pendiente: trader/crafting).

## 2. Ficha técnica

| Elemento | Valor |
|---|---|
| Motor | Unity 6.3 LTS (6000.3.21f1) |
| Red | Fish-Networking 4.7.2, transporte Tugboat (UDP), Prediction v2 |
| Tick | 60 Hz (`TimeManager`) |
| DI | VContainer (versión fijada en `Packages/manifest.json`) |
| UI | UI Toolkit (UXML/USS). PanelSettings con resolución de referencia 1670x940 y modo Shrink |
| Input | Unity Input System |
| Async | `Task`/`async` (UniTask previsto en el diseño original; hoy se usa `Task`) |
| Hosting | Servidor dedicado Linux en container, PlayFab Multiplayer Servers (GSDK) |
| Matchmaking | Cola PlayFab `SpellHaulRaid` |
| Persistencia | PlayFab Player Data **ReadOnly**, escrita solo por CloudScript y por el servidor de la run |
| Identidad | `LoginWithCustomID` (id del dispositivo, o el flag `-playerid`). `LoginWithSteam` queda pendiente hasta tener AppID |
| Plataforma | PC (Steam) primero |
| Repo | `DiegoPereyra21/SpellHaul` |

## 3. Estructura del código

Todo el código está en `Assets/_Projec/Scripts/` (la carpeta se llama `_Projec`, sin la "t").

```
Core/                      lógica pura, sin depender de Presentation
  Abilities/               AbilitySO, AbilitySlots, AbilityCastContext, AbilityExecutor (puerto), IDamageable, habilidades concretas
  Items/                   ItemSO, EquipmentItemSO, GloveItemSO, ItemStack, InventorySnapshot, StashData,
                           ItemDatabase, LootTableSO, StartingKitSO, enums (EquipmentSlot, ItemCategory, Rarity,
                           StatType, GloveSchool), ItemSorting (ItemSortMode), StatModifier
  Pooling/                 ObjectPool
  Run/                     ActiveRunInfo, IPlayerLoadoutStorage, IStashStorage, IRunInventory, PlayerRunStatus, RunPhase
Presentation/
  Bootstrap/               NetworkBootstrap, LaunchArgs, PlayFabSession, MatchmakingService,
                           PlayerIdentityAuthenticator, NetworkDisconnectHandler, PlayerSpawnManager,
                           PlayerSpawnPoint, RunServerEndpoint, GameLifetimeScope
  Player/                  PlayerMovementController (Prediction v2), CameraLookController, CameraEffects,
                           LookSettings, PlayerAvatarState, PlayerRegistry
  Abilities/               AbilityController, NetworkAbilityExecutor, Projectile, ChargedOrbProjectile,
                           CosmeticProjectile(+Manager), TrajectoryPreviewController, ChargeVFXController
  Combat/                  Health, Mana, PlayerStats, RunInventory, LootContainer, WorldItem, LootDropper,
                           ChestSpawner/Point, EnemySpawner/Point, EnemyAI, RangedEnemyAI, PlantGuardianAI,
                           LobProjectile, ExtractionZone, PlayerExtractionState, PlayerDeathHandler,
                           PlayerInteraction, FallDeath, DamageFlash, ScreenShake, VFXManager, NetSyncRates
  Run/                     RunManager, PlayerLoadoutService, StashService, ProfileSaveQueue, ServerProfileStore,
                           PlayFabUserData, PlayFab*Storage, Local*Storage
  UI/                      MainMenuController, StashScreenController, InventoryUIController, HUDController,
                           HudExtras, PauseMenuController, ResultScreenController, RunSummary, ItemTooltipFormatter
  Settings/                DisplaySettings, HudSettings, SettingsPanel
  Audio/                   AudioLibrarySO, GameAudio, FootstepAudio, AudioVolumes
Editor/                    ItemDatabaseBuilder, ItemCatalogGenerator, LootTableGenerator, StarterKitExporter,
                           AudioLibraryBuilder
PlayFab/cloudscript.js     (en la raíz del repo) CloudScript Legacy
```

**Escenas**
- `MainMenu`: tiene el NetworkManager persistente.
- `Run`: el mapa de la partida.

**Decisiones de arquitectura**
- Se usan GameObjects tradicionales, sin DOTS.
- Las habilidades son data-driven: un ScriptableObject por habilidad con patrón Strategy. Ejecutan a través del puerto `AbilityExecutor`, sin conocer Fish-Net.
- Las definiciones (`ItemSO`) están separadas de las instancias (`ItemStack`, struct de red con `ItemId` + `Quantity` + `Durability`).
- VContainer no inyecta automáticamente los objetos que spawnea Fish-Net. Por eso `AbilityController.OnStartNetwork` llama a `GameLifetimeScope.InjectSpawnedObject`.
- Singletons locales de presentación (por ejemplo `ScreenShake`): no se autoasignan en `Awake`. Los reclama el dueño en `OnStartClient`, porque cada jugador instancia su cámara y la desactiva recién después de `Awake`.
- Prediction v2 y NetworkTransform se excluyen: el Player no lleva NetworkTransform.
- El servidor no usa `Find*` por frame: los jugadores activos están en `PlayerRegistry.Active`.

## 4. Flujo de la aplicación

### Arranque (`NetworkBootstrap` + `LaunchArgs`)

**Roles** (`-server`, `-client`, `-host`, o ninguno)
- **None**: menú normal, login y matchmaking.
- **Server**: levanta la red y carga la escena `Run`.
- El campo `_editorRole` se aplica **solo en el Editor**. En un build manda la línea de comandos.

**Detección de servidor dedicado**
- `LaunchArgs.IsDedicatedServer` usa `UNITY_SERVER && !UNITY_EDITOR`.
- El guard `!UNITY_EDITOR` es necesario: con el build profile de servidor activo, el Editor también define `UNITY_SERVER`, y sin el guard cargaba la escena Run.
- Aun en un build Dedicated Server, sin `-server` la red no se levanta.

**Flags**

| Flag | Uso |
|---|---|
| `-address` / `-port` | Conexión directa. Default `127.0.0.1:7770` |
| `-playerid <x>` | Fuerza la identidad de PlayFab. Sirve para dos clientes en la misma PC (`launch_player2.bat`) |

### Menú → partida

1. **Login.** `PlayFabSession` hace `LoginWithCustomID` y llama a CloudScript `EnsureProfile`. Si es un jugador nuevo, recibe el kit inicial desde el Title Data `StarterKit`. Después se activan los storages reales de PlayFab.
2. **Sesión.** `PlayFabSession` guarda `SessionTicket` y tiene un loop de refresco (el ticket dura 24 h). `EnsureFreshAsync()` se llama antes de conectar.
3. **Matchmaking.** `MatchmakingService` crea un ticket en `SpellHaulRaid` y lo consulta cada 6 s.
   - La latencia se envía **fija** (150 ms hacia `EastUs`), porque la regla de región de la cola la exige. Medir QoS de verdad queda pendiente.
   - Ante `MatchmakingTicketMembershipLimitExceeded`, cancela todos los tickets del jugador y reintenta una vez.
4. **Conexión.** `MainMenuController.ConnectToMatchServerAsync`:
   - espera a que `PendingSync` quede vacío (no hay guardados pendientes);
   - guarda la dirección y el puerto en `RunServerEndpoint`, porque `Tugboat.GetPort()` devuelve el puerto local y no sirve para reconectar;
   - conecta.
5. **Autenticación.** `PlayerIdentityAuthenticator`:
   - El cliente envía `PlayerIdentityBroadcast { PlayerKey, SessionTicket, ServerAddress, ServerPort }`.
   - El servidor valida el ticket de forma asíncrona (`ServerProfileStore.VerifySessionTicketAsync`, Server API `AuthenticateSessionTicket`) y obtiene el PlayFabId real.
   - Con `_requireMatchMembership`, solo entran los jugadores de `GSDK GetInitialPlayers` (la comparación es tolerante). Si no está en la lista, se rechaza con `NotInMatch`.
   - Una conexión que no se autentica en 20 s se corta.
   - Al aceptar, el servidor envía `ServerPersistenceBroadcast { ServerOwnsProfile }`. El cliente lo guarda en `PlayerLoadoutService.ServerOwnsRun`.
6. **Spawn.** `PlayerSpawnManager` espera a que la conexión esté en la escena Run y spawnea en un `PlayerSpawnPoint`. Los puntos se eligen con una bolsa: no se repiten hasta agotarse.
7. **Carga del loadout.** `RunInventory.ServerLoadLoadoutAsync` (ver §6).

**`RunOutcome`** (enum de red; los valores nuevos van al final)

`None=0, DiedWhileAway=1, Extracted=2, LeftRun=3, NotInMatch=4, ProfileUnavailable=5, AlreadyInRun=6`

- Se envía con `RunOutcomeBroadcast`, que tiene un campo `Detail`.
- `NetworkDisconnectHandler` lo traduce a un mensaje en el menú.

### Ciclo de vida del servidor
- **Timer.** Arranca con el primer `RegisterPlayer`, no al bootear: con PlayFab MPS el proceso puede quedar minutos en StandingBy.
- **Gracia de fin.** `RunManager._endGraceSeconds` (75 s) evita que la run termine mientras siguen conectando jugadores del match. Se corta antes si ya se registraron todos los `InitialPlayers` esperados.
- **Servidor sin jugadores.** Si pasa a Active y nadie se registra en `_noPlayersShutdownSeconds` (120 s), se cierra.
- **Fin de la run.** Termina cuando no queda nadie vivo. Entonces `RunManager.OnRunEnded`, luego `NetworkBootstrap` espera a que los clientes vean los resultados, después espera a que `ServerProfileStore.HasPendingWrites` sea false (máximo 30 s) y llama a `Application.Quit()`.
- **Por qué se cierra.** Cerrar el proceso devuelve el cupo a PlayFab. Un servidor Active nunca vuelve solo a StandingBy.
- **Desconexión.** Un jugador que se desconecta sin morir ni extraer deja su cuerpo en el mundo, sin dueño (el prefab tiene `Prevent Despawn On Disconnect`).

### Reconexión
- **Marca de run activa.** Mientras dura la run, el loadout guardado tiene `ActiveRun { Active, Address, Port, StartedUnixSeconds, RunId }`.
- **Al abrir el menú.** Si `PlayerLoadoutService.IsInActiveRun`, se muestra el panel de rejoin y el inventario queda bloqueado. El panel ofrece:
  - **Reconnect**: hace `EnsureFreshAsync`, conecta a la dirección y puerto guardados y envía el mismo `PlayerKey`.
  - **Abandon Run**: pide un segundo clic para confirmar y llama a CloudScript `AbandonActiveRun`. El jugador pierde el equipo que se llevó.
- **Del lado del servidor** (`PlayerSpawnManager.TryReclaimBody`, si existe un cuerpo con esa key):
  - Si el cuerpo está vivo, `RemoveOwnership` a la conexión vieja y `GiveOwnership` a la nueva. El cliente reengancha la cámara y la UI en `OnOwnershipClient`.
  - Si murió, rechaza con `DiedWhileAway`.
  - Si extrajo, rechaza con `Extracted` y reenvía el loot (por si el guardado original no llegó).
  - Si el cuerpo ya no existe, rechaza con `LeftRun`. No se da un cuerpo nuevo.
  - Si es otra run, rechaza con `AlreadyInRun` (lo detecta `ServerLoadLoadoutAsync` según la marca).

### Campo de práctica
- **Entrada:** botón **Practice Range** del menú, que llama a `NetworkBootstrap.StartPractice()`. Levanta un host local (servidor y cliente en el mismo proceso, escuchando solo en 127.0.0.1:7790) y carga la escena `_practiceSceneName` (default `PracticeRange`). No pasa por matchmaking ni por PlayFab MPS.
- **Equipo:** se entra con el loadout actual. Mientras `PracticeSession.Active` está activo **no se persiste nada**:
  - `PlayerLoadoutService` (Save, Clear, ApplyRunResult, ApplyRunLost, MarkActiveRun) no hace nada;
  - los enemigos no sueltan loot;
  - al morir no se suelta nada ni aparece la pantalla de resultados.
- **Muerte:** `PlayerSpawnManager.ServerRespawnAfter` reemplaza el cuerpo por uno nuevo en un spawn a los 3 s. El dueño vuelve a enviar su loadout, que no cambió.
- **Enemigos:** `EnemySpawner._respawnSeconds > 0` los hace reaparecer en su punto. En la run vale 0.
- **Salida:** Esc → **Leave Practice** (`NetworkBootstrap.StopPractice`) vuelve al menú sin perder nada. Si el host se cae, `NetworkDisconnectHandler` cierra la sesión de práctica y vuelve al menú.
- **Escena:** necesita `GameLifetimeScope`, `VFXManager`, `InventoryUIDocument`, luz, suelo en la capa Ground con NavMesh, `PlayerSpawnPoint`, `EnemySpawnPoint` y un `EnemySpawner` con respawn. No lleva `RunManager`, `ExtractionZone` ni `ChestSpawner`.

## 5. Red

### Simulación y movimiento
- Todo en `TimeManager.OnTick` a 60 Hz.
- `NetworkManager.UpdateFramerate` pisa `Application.targetFrameRate`. `DisplaySettings` incluye un `FrameRateKeeper` que reaplica el límite de FPS elegido.
- **Movimiento** (`PlayerMovementController`):
  - Prediction v2 sobre un CharacterController.
  - `ReplicateData` lleva el **yaw absoluto** (no deltas), y la mirada es local cada frame.
  - El dash está integrado en los inputs replicados y se reconcilia.
  - El input de mirada se multiplica por `LookSettings.Sensitivity` y respeta `InvertY`. Esto es solo input local: no cambia la reconciliación.

### Frecuencia de envío
- `SyncTypeSettings(0f)` en Fish-Net significa "usar el default global" (0.1 s), no "cada tick". Por eso existe `NetSyncRates.EveryTick = 0.001f`.
- Usan `EveryTick`: Health, `PlayerExtractionState`, `LootContainer`, `WorldItem`, `RunManager` y las listas de `RunInventory`.
- Mana usa 0.05 s.

### Proyectiles
- **Server-authoritative con simulación en cliente.**
  - El servidor simula y hace hit-reg.
  - Los clientes reciben `FlightObserversRpc` (origen, dirección, velocidad, tick) y simulan el vuelo localmente, en lugar de seguir un NetworkTransform.
  - El tirador ve un proyectil cosmético instantáneo (`CosmeticProjectileManager`).
- **Detección.** OverlapSphere en la posición actual, más SphereCast entre la posición anterior y la actual (anti-tunneling). Descarta al caster por ObjectId.
- **Origen.** Sale del `SpellOrigin` autoritativo del servidor y converge al aimPoint. El origen enviado por el cliente nunca se usa.

### Compensación de lag híbrida (`Projectile.TryImpactCompensated`)
- **Contra IA:** rewind completo al tick de disparo del cliente (ColliderRollback/RollbackManager), con tope de 20 ticks.
- **Contra jugadores:** rewind con tope `_maxPlayerRewindTicks = 7` (unos 117 ms).
- **Regla de cobertura** (`IsBehindCoverNow`):
  - Hace un Linecast desde el proyectil hasta la posición **actual** de la víctima contra `_coverMask` (Ground).
  - Si la víctima ya está detrás de una pared, el golpe no cuenta.
  - Así se evita el "me pegaron detrás de la pared".
- **Arquitectura.** `FindImpactAt` / `FindImpact` reciben un `HitFilter` y devuelven `ImpactInfo`. El daño hecho vuelve al tirador con `AbilityController.NotifyProjectileImpact(..., damageDealt)` y alimenta los números de daño.

### Cooldowns y maná
- **Tick de disparo.** El cliente envía el tick de disparo y el servidor lo acota con `ClampFireTick`, para tolerar jitter sin permitir adelantar el disparo.
- **Cooldowns.** Se miden en ticks (`CooldownTicks`) desde ese tick acotado, no en `Time.time`. Duración efectiva: `Cooldown / max(0.1, CastSpeedMultiplier)`.
- **Maná.** El cliente lo predice (`PredictedLocalMana`) para no bloquear un cast que el servidor sí va a aceptar.
- **Rechazo.** El servidor responde por TargetRpc con el tiempo restante, el cliente lo convierte a ticks locales y suena `PlayCastRejectedSound`.

### Otros
- **Enemigos y loot en el suelo:** NetworkTransform server-authoritative, con flags ajustados a lo que realmente cambia.
- **VFX de gameplay:** se disparan por ObserversRpc/TargetRpc. Los VFX puramente visuales son locales (`VFXManager`, con pooling).

## 6. Persistencia

### Modelo
- Las claves `PlayerLoadout` (`InventorySnapshot` en JSON) y `Stash` (`StashData`) viven en Player Data **ReadOnly**, privadas. El cliente puede leerlas pero no escribirlas.
- Escriben dos actores:
  - **CloudScript**, desde el menú: reacomodar, kit inicial y abandonar.
  - **El servidor de la run** (Server API con la clave secreta): marca de run y resultado.

### Cliente
- **Servicios.** `PlayerLoadoutService` y `StashService` son caches estáticas en memoria. Leen a través de `IPlayerLoadoutStorage` e `IStashStorage`: local por defecto y PlayFab después del login.
- **Escritura** (`ProfileSaveQueue`):
  - Hace commit del perfil completo con CloudScript `CommitProfile`, en background y con reintentos.
  - Si CloudScript rechaza, recarga desde PlayFab y dispara `OnProfileReloaded` para que la UI se redibuje.
  - El rechazo `in_run` se reintenta durante hasta 60 s, porque el servidor puede tardar en limpiar la marca.
- **`PlayFabUserData`.** Ofrece `ReadAsync`, `CallAsync(function, args)`, `RejectedException` y las constantes con los nombres de las funciones.

### Servidor (`ServerProfileStore`, `Presentation/Run`)
- **`Configure()`.** Lee la clave de la variable de entorno `SPELLHAUL_PLAYFAB_SECRET` o de `playfab_secret.txt` junto al ejecutable. Si no la encuentra, `IsActive = false` y el cliente guarda su propio loadout (modo dev/editor).
- **`LoadLoadoutAsync`.** Hace `GetUserReadOnlyData` con 3 reintentos. Si falla, devuelve `ok=false` y no se escribe nada para ese jugador.
- **`SaveLoadout(pfid, snapshot, requireRunId)`.**
  - Cola por jugador donde gana lo más nuevo, con backoff de hasta 5 s.
  - Con `requireRunId`, primero relee y solo escribe si la marca guardada sigue siendo esa run.
  - Si la marca de esa run todavía no se confirmó (`_confirmedRuns`), escribe sin condición.
- **`HasPendingWrites`.** El proceso no se cierra mientras sea true (máximo 30 s).

### Ciclo de una run, del lado del servidor (`RunInventory`)

1. `ServerLoadLoadoutAsync`:
   - Lee el loadout.
   - Si ya hay una marca activa de **otra** run, rechaza con `AlreadyInRun`.
   - Si no se pudo leer, rechaza con `ProfileUnavailable` y elimina el cuerpo (`RejectAndRemoveBody`).
   - Si el jugador murió mientras cargaba, no aplica nada.
   - Aplica el loadout y vuelve a agregar lo que el jugador recogió durante la carga.
   - Escribe la marca `ActiveRun` con un `RunId` nuevo.
2. **Al extraer**, `ServerWriteResult` guarda el inventario de la run sin marca (con `requireRunId`).
3. **Al morir o hacer Leave Run**, guarda un loadout vacío sin marca (con `requireRunId`). El loot queda en el cadáver.
4. **Avisos al cliente.** Le avisa por TargetRpc. El cliente aplica `ApplyRunResult` o `ApplyRunLost`, o hace `Invalidate` y luego `ReloadAsync` para releer.

### CloudScript (`PlayFab/cloudscript.js`, Legacy)
- **Subida.** Se sube a mano en Game Manager → Automation → CloudScript → Revisions (Legacy) y se despliega la revisión. No usar el add-on de GitHub.
- **Respuesta.** Todas las funciones devuelven un string JSON `{"ok":bool,"reason":"..."}`.
- **`EnsureProfile`.** Si no hay loadout, escribe el Title Data `StarterKit` con la marca inactiva, más un stash vacío de 30 slots.
- **`CommitProfile`.** Rechaza en estos casos:
  - `no_profile`, si no hay perfil;
  - `in_run`, si hay una marca activa;
  - `invalid_structure`, si la estructura es inválida (límites: Equipment ≤ 32, pockets ≤ 12, stash = 30, cantidad 1..9999, durabilidad 0..1);
  - `new_item:`, `more_items:` o `durability_up:`, si la **conservación** falla. Por cada ItemId, la cantidad total y la suma de durabilidad no pueden subir.

  Además fuerza `ActiveRun` inactivo: el cliente no puede marcar ni desmarcar runs.
- **`AbandonActiveRun`.** Vacía el equipo y los pockets y limpia la marca. El stash queda intacto.
- **`StarterKit`** (Title Data). Se genera con el menú del Editor **Game/Items/Copy Starter Kit JSON** (`StarterKitExporter`) y se pega en Game Manager → Content → Title Data.

## 7. Items, inventario y stash

### Definiciones
- `ItemSO` tiene: id, nombre, categoría, rareza, apilable, stack máximo y `WorldPrefab`.
- `EquipmentItemSO` agrega slot, `StatModifier[]` (aditivos) y `_pocketSlots`.
- `GloveItemSO` (categoría `Glove`, slot Glove) agrega `GloveSchool`, la `AbilitySO` del clic derecho, `_abilityPower` y `_cooldownMultiplier` (ver §8).
- `Rarity`: Common / Rare / Epic.

### Enums serializados (los valores nuevos van al final)

| Enum | Valores |
|---|---|
| `EquipmentSlot` | Boots, Hat, Robe, Glove, PocketL, PocketR |
| `GloveSchool` | Fire, Nature, Light, Earth (antes Destruction, Restoration, Illusion; mismo orden) |
| `ItemCategory`, `Rarity`, `StatType`, `RunPhase`, `PlayerRunStatus`, `RunOutcome` | (sin detallar) |

- El orden visual de los slots no es el orden del enum. Lo definen las extensiones de `EquipmentSlotExtensions` (`DisplayOrder`: Hat, Robe, Glove, Boots, PocketL, PocketR), `DisplayRank`, `DisplayIndices` y `DisplayName`.

### ItemDatabase
- `ItemDatabaseBuilder` (un AssetPostprocessor) la reconstruye sola cuando se crea, borra o mueve un `ItemSO`.
- También se puede reconstruir a mano con **Game > Items > Rebuild Item Database**.
- Avisa si hay ids vacíos o duplicados.

### Herramientas de catálogo
- `ItemCatalogGenerator` (**Game > Items > Generate Equipment Catalog**) crea los 36 equipos a partir de la tabla `BuildCatalog()`. No pisa los assets que ya existen.
- `LootTableGenerator` genera las tablas de loot, incluido el perfil `LootTable_Enemy_Ranged`.

### Inventario de run (`RunInventory`)
- **Estructura.** Server-authoritative, con SyncLists: equipamiento por slot, `PocketL` y `PocketR`.
- **Capacidad de un pocket.** La da el pocket equipado en ese lado (`_pocketSlots`: 6/9/12). Sin pocket, 1 slot.
- **Apilado.**
  - Al recoger, `StackIntoExisting` llena los stacks existentes de ambos pockets antes de usar slots vacíos (`FillEmptySlots`).
  - La recogida parcial está soportada.
  - Recoger una mochila la autoequipa. Quitar una mochila rescata los slots que quedan afuera.
- **Ordenar.** `TrySortPockets(ItemSortMode)` ordena por ServerRpc. El orden por rareza no mezcla items distintos.
- **Extracción y muerte.** Implementa `IRunInventory`. Al morir, `DropAll` crea un `LootContainer` (el cadáver) con todo lo que llevaba.

### Stash
- `StashData` tiene 30 slots fijos y la gestiona `StashService`.
- **Pantalla** (`StashScreenController`): Loadout, Pocket L y Pocket R (12 celdas cada uno; las que superan la capacidad quedan bloqueadas) y la grilla del Stash.
- **Interacciones** (igual que en la run):
  - clic para mover;
  - shift+clic para autoequipar;
  - drag & drop con swap o merge;
  - botón para ordenar el Stash. El último modo queda en `PlayerPrefs` `SpellHaul.SortMode`.
- **Pockets con contenido.** Achicar o quitar un pocket con items simula primero si el sobrante entra en el stash. Si no entra, la operación se cancela entera.
- **Mutaciones.** Todas pasan por `TryMutate`, que aplica en la cache, encola el commit y reproduce un sonido.
- **Bloqueo.** `SetLoadoutLocked` bloquea mientras hay una run activa sin resolver.

### Tooltip (`ItemTooltipFormatter`, compartido)
- Muestra nombre, tipo, descripción, "Size +N" en los pockets y los stats con signo y color.
- Protection se muestra en %.
- La rareza se indica con un color de clase en el borde de la celda. En el stash, el contorno remarcado se ve solo en partida.

## 8. Equipamiento y stats

### Bases (`PlayerStats`)

| Stat | Base |
|---|---|
| ManaRegen | 8 |
| JumpForce | 6 |
| MoveSpeed | 6 |
| DamageMultiplier | 1.0 |
| CastSpeedMultiplier | 1.0 |
| Protection | tope 0.6 |

- Todos los modificadores son deltas aditivos.
- **Pisos:** Damage ≥ 0.1, CastSpeed ≥ 0.1, MoveSpeed ≥ 0.5, JumpForce ≥ 0, ManaRegen ≥ 0. Protection queda en [0, 0.6].

### Patrón de arquetipos
- Se aplica a Boots, Hat y Robe, cada uno en 3 rarezas. Los guantes no siguen este patrón (ver Guantes).
- **Swift:** un stat ofensivo o de movilidad, sin penalidad.
- **Heavy:** ese mismo stat en negativo, más Protection.
- **Focus:** un stat de utilidad (ManaRegen o JumpForce).
- Damage y CastSpeed solo vienen de Hat y Robe (los guantes ya no dan stats pasivos).

### Catálogo (deltas; Common / Rare / Epic)

| Slot | Swift | Heavy | Focus |
|---|---|---|---|
| Boots | MoveSpeed +0.75 / +1.2 / +1.8 | MoveSpeed −0.5/−0.8/−1.2 · Prot +0.08/+0.12/+0.18 | JumpForce +1.3 / +2 / +3 |
| Hat | CastSpeed +0.05 / +0.08 / +0.12 | CastSpeed −0.03/−0.05/−0.08 · Prot +0.08/+0.12/+0.18 | ManaRegen +1.3 / +2 / +3 |
| Robe | Damage +0.05 / +0.08 / +0.12 | Damage −0.03/−0.05/−0.08 · Prot +0.08/+0.12/+0.18 | ManaRegen +1.3 / +2 / +3 |
| Pocket | Pocket_1: 6 slots, Pocket_2: 9 slots, Pocket_3: 12 slots | | |

- **ItemId:** `arquetipo_slot_rareza` en snake_case (por ejemplo `swift_hat_rare`, `heavy_boots_epic`).
- **DisplayName:** "Rareza Arquetipo Slot".
- **Kit inicial:** `StartingKitSO` (`_startingItems`, con `[FormerlySerializedAs("_backpack")]`). Para PlayFab se exporta a Title Data.

### Guantes
- Son una categoría propia (`ItemCategory.Glove`), ocupan el slot Glove y **definen la habilidad del clic derecho**. Sin guante equipado el clic derecho no hace nada.
- No dan stats pasivos. Tienen:
  - una **escuela** (`GloveSchool`), que se muestra en el tooltip ("Rare Fire Glove") y como color del ícono en el HUD;
  - una `AbilitySO`, compartida por todas las rarezas de esa familia;
  - **potencia** (`_abilityPower`, multiplica daño o curación) y **cooldown** (`_cooldownMultiplier`). Ambos mejoran con la rareza.
- `AbilityController` resuelve la habilidad con `RunInventory.EquippedGlove`. Funciona en el servidor y en todos los clientes, porque el equipo se sincroniza a todos.
- La potencia viaja en `AbilityCastContext.AbilityPower`. El cooldown efectivo es `Cooldown × CooldownMultiplier / CastSpeed`.
- El cooldown es del slot, no del guante: cambiar de guante no lo reinicia. Si el guante cambia en medio de una carga o un windup, el cast se descarta.
- **Tooltip:** `AbilitySO.DescribeEffect(power, lines)` arma las líneas de efecto ya escaladas (por ejemplo "Heals 39").
- **Estilo en la UI** (`GloveVisuals` + `UI/Gloves.uss`, compartido entre stash e inventario de run): los guantes se distinguen del resto del equipo.
  - En las grillas, la casilla es más redondeada, con un rombo y una banda inferior del color de la escuela. El borde sigue indicando la rareza.
  - En el loadout, la fila lleva una franja de la escuela y, debajo del nombre, "RMB · habilidad".
  - En el tooltip, la franja y el tipo van en el color de la escuela.

| Familia | ItemId | Escuela | Habilidad (`AbilityId`) | Potencia C/R/E | Cooldown C/R/E |
|---|---|---|---|---|---|
| Orb Gloves | `orb_gloves_<rareza>` | Fire | Orbe cargado (`id_chargedorb`) | ×1.0 / ×1.2 / ×1.45 | ×1.0 / ×0.9 / ×0.8 |
| Mending Gloves | `mending_gloves_<rareza>` | Nature | Cura instantánea (`id_heal`) | ×1.0 / ×1.3 / ×1.6 | ×1.0 / ×0.9 / ×0.8 |
| Flare Gloves | `flare_gloves_<rareza>` | Light | Orbe de destello (`id_flashorb`) | ×1.0 / ×1.2 / ×1.4 (duración del cegado) | ×1.0 / ×0.9 / ×0.8 |
| Stone Gloves | `stone_gloves_<rareza>` | Earth | Muro de tierra (`id_earthwall`) | ×1.0 / ×1.3 / ×1.6 (vida y duración) | ×1.0 / ×0.9 / ×0.8 |

**Escuelas como elementos:** Fire (daño), Nature (curación), Light (control y visión) y Earth (defensa). Las habilidades nuevas se agrupan por esa temática.

**Muro de tierra** (`EarthWallAbilitySO` + `EarthWall`)
- **Ubicación:** sale del suelo apuntado si está a 15 m o menos y es plano (normal.y ≥ 0.6). El servidor lo re-valida con su propio rayo contra Ground desde el SpellOrigin. Si no es válido, aparece a 4 m delante del jugador, apoyado en el suelo. Siempre mira al caster.
- **Base:** vida 150 y 6 s de duración (los dos escalan con la rareza); cooldown 12 s y maná 35.
- **Animación:** sube del suelo en 0.25 s y se hunde en 0.2 s al vencer o romperse. Se anima localmente en cada lado.
- **Bloqueo:** frena a todos. El cuerpo está en la capa Ground (movimiento, proyectiles, visión de IA, regla de cobertura y flash). Un hijo en Hitbox recibe el daño.
- **Vida:** usa `Health` con `ServerSetMaxHealth` y `CountsAsKill = false`, para que romperlo no cuente como kill.

**Orbe de destello** (`FlashOrbAbilitySO` + `FlashProjectile`)
- **Vuelo:** proyectil lento en línea recta (12 m/s, 3 s de vida, cooldown base 14 s). En vuelo es chico (`_flightScale` 0.35).
- **Choque:** `FlashProjectile` siempre agrega Hitbox y Ground a `_hitMask`. Antes, el prefab tenía Ground+Player y atravesaba a los enemigos.
- **Visual de la detonación** (`FlashBurst`, del lado del cliente): una copia del orbe crece rápido hasta aproximadamente el 45% del radio y una luz puntual se enciende y se apaga.
- **Sonidos:** `maximize_003` al lanzar y `glass_004` al detonar (Kenney).
- **Detonación:** detona en cuatro casos:
  - al chocar con Hitbox o Ground (menos el caster);
  - al terminar su vida;
  - al volver a apretar el clic derecho mientras vuela;
  - no hace daño.
- **Re-activación genérica:**
  - `AbilitySO.IsRecastable` / `RecastWindow` habilitan la re-activación de una habilidad.
  - Mientras la ventana está abierta, el slot se resalta en el HUD y el próximo clic envía `RecastServerRpc`.
  - El servidor busca lo lanzado en `RecastRegistry` (por caster y slot) y llama a `IRecastable.Recast()`.
  - Al detonar, `AbilityController.NotifyRecastEnded` cierra la ventana del dueño.
- **Cegado de jugadores** (`FlashOverlay`, del lado del cliente):
  - El servidor solo envía el punto, el radio y la duración máxima; cada cliente calcula su propio cegado.
  - Requisitos: línea de visión (Linecast contra Ground) y estar dentro del radio.
  - Intensidad según la mirada: de frente, completa; de costado, menor; de espaldas, casi nada. También baja con la distancia.
  - Se ve como una pantalla blanca que se mantiene y luego se desvanece.
  - Afecta también al caster.
  - Es visual: un cliente modificado podría ignorarlo, igual que en cualquier flashbang.
- **Cegado de IA** (servidor): `Blindness` se agrega en runtime. Mientras dure, `EnemyAI`, `RangedEnemyAI` y `PlantGuardianAI` pierden el objetivo y no atacan.

- **Guante nuevo:**
  1. Si hace falta, crear la `AbilitySO` con un `AbilityId` único.
  2. Si es una escuela nueva, agregarla al **final** de `GloveSchool`, con su color `.school-<nombre>` en `HUD.uss`.
  3. Agregar la familia en `ItemCatalogGenerator.BuildGloves()`.
  4. Correr **Game > Items > Generate Equipment Catalog** y después **Generate Loot Tables**.
- **Guantes retirados** (Swift/Heavy/Focus): CloudScript `EnsureProfile` los migra con `LEGACY_ITEM_IDS`. Swift y Heavy pasan a Orb y Focus pasa a Mending, con la misma rareza.

## 9. Combate

### Habilidades (3 slots, `AbilityController` + `AbilitySlots`)

El índice de slot viaja por red y define el binding (`CastSlot0..2`): no reordenar.

| Slot | Input | Habilidad | Notas |
|---|---|---|---|
| `Primary` (0) | Clic izquierdo | Proyectil básico (`_primaryAbility`) | Fijo. Windup de telegrafía opcional |
| `Mobility` (1) | Shift | Dash (`_mobilityAbility`) | Fijo. Predicho y reconciliado. Ease-out, en la dirección de mirada (permite dash vertical) |
| `Glove` (2) | Clic derecho | La del guante equipado | Vacío sin guante. Hoy: orbe cargado, cura instantánea, orbe de destello o muro de tierra |

El parry fue eliminado.

**Orbe cargado**
- El servidor mide la carga.
- El maná se cobra al empezar a cargar y el cooldown arranca al soltar.
- Trayectoria balística. El preview se dibuja con un LineRenderer en `LateUpdate`.
- Al terminar, `ChargedOrbProjectile` suma el daño hecho.

**Otros sistemas de combate**
- **Salud** (`Health`): SyncVar server-authoritative, implementa `IDamageable` (daño positivo, cura negativa) y expone el evento `OnDied`. Lo usan jugadores y enemigos. Protection reduce el daño.
- **Muerte del jugador** (`PlayerDeathHandler`):
  - `PlayerAvatarState` apaga el control (movimiento, habilidades, colisión y renderers; la cámara sigue viva).
  - Se dropea el cadáver.
  - `DeathSummaryTargetRpc` le envía al jugador la causa de muerte (`DescribeDeathCause`).
- **Leave Run** (pausa): `LeaveRunServerRpc` mata al jugador del lado del servidor. El cliente termina la run localmente al instante, sin esperar respuesta.
- **Extracción** (`ExtractionZone`): el jugador canaliza `_channelTime` = 5 s dentro del trigger. Si sale o muere, se reinicia. Al completarla queda invulnerable y la IA lo ignora.
- **Fase de peligro** (`RunManager`):
  - Al terminar `_runDuration` pasa a `RunPhase.DangerPhase`.
  - Aplica `_dangerPhaseDamagePerTick` a cada vivo cada `_dangerPhaseTickInterval`.
  - Los valores de la escena mandan sobre los defaults del script (60 s y 120 por tick, que son valores de prueba).
  - Contenido completo **pendiente**: noche más "hunters" cerca de los jugadores vivos.
- **Muerte por caída** (`FallDeath`): por debajo de un Y configurable.

### Enemigos (server-authoritative)

Todos requieren línea de visión (raycast contra Ground), ignoran a los muertos y extraídos, buscan objetivos en `PlayerRegistry.Active` y usan `LootDropper`.

| Enemigo | Comportamiento |
|---|---|
| `EnemyAI` | Melee con NavMesh: Idle → Chase con histéresis → Attack con windup esquivable. Patrulla cerca del spawn |
| `RangedEnemyAI` | Estático. Dispara proyectiles con altura de muzzle, factor de anticipación (lead) y velocidad de giro configurables. `HandleDied` y `OnStopServer` limpian el estado. Tabla `LootTable_Enemy_Ranged` |
| `PlantGuardianAI` | Torreta de dos fases: ráfagas de cerca, `LobProjectile` balístico de lejos |

### Loot
- **Tablas** (`LootTableSO`): rolls independientes. Con `_guaranteeAtLeastOne` la tabla nunca devuelve vacío.
- **Contenedores** (`LootContainer`): cadáveres y cofres. No se despawnean al vaciarse: cambian de color.
- **Spawn.** `ChestSpawner` y `EnemySpawner` recorren los puntos que están colocados a mano en la escena Run. La generación procedural está pendiente.
- **`WorldItem`.** Loot en el suelo con Rigidbody. Se recoge apuntando y apretando la tecla (`PlayerInteraction`).
- **Drop desde el inventario.** Con ctrl+clic o arrastrando fuera.

## 10. UI

Todo con UI Toolkit. El orden entre paneles se maneja con `sortingOrder` de cada UIDocument.

### Menú y stash
- **`MainMenuController`.**
  - Botones Play, Stash, Options y Quit, deshabilitados hasta que el login esté listo (`RefreshMenuState`).
  - Fondo modal y panel de avisos (`ShowNotice`) para los mensajes de desconexión y los `RunOutcome`.
  - Panel de rejoin (ver §4).
  - Durante la búsqueda se muestra un banner con temporizador y botón para cancelar. Se puede abrir el stash mientras busca: el menú pasa a modo overlay (`sortingOrder` 10) por encima del stash.
- **`StashScreenController`.** Ver §7. Expone los eventos `Shown` y `Hidden`.

- **Rareza del equipo puesto:** en el stash y en el inventario de la run, cada fila del loadout muestra una etiqueta COMMON/RARE/EPIC y el nombre en el color de la rareza (`ItemCommon.uss`). El tipo del tooltip incluye la rareza ("Rare Hat").
- **Tooltips** (stash e inventario): `TooltipPlacement` los ubica arriba del slot. Si no entran, abajo, y si tampoco, al costado. Siempre quedan dentro del panel.

### En partida
- **`InventoryUIController`** (se abre con Tab):
  - equipo, pockets, 3 "usables" (placeholder sin lógica) y la columna del contenedor abierto;
  - botones de orden y tecla R;
  - redibujo agrupado (`RequestRedraw` y redibujo en `LateUpdate`);
  - sonidos al abrir contenedores, con un acento para rare y epic;
  - expone `IsOpen` y `Close`.
- **`HUDController`.** Muestra:
  - vida y maná;
  - cooldowns de los 3 slots, con el del guante mostrando el nombre de la habilidad o "No Glove";
  - aro alrededor de la mira: la mitad izquierda es el cooldown del clic principal (violeta) y la derecha el del guante (color de la escuela; vacía sin guante). Cada mitad se llena de abajo hacia arriba;
  - barrita del dash debajo de la mira;
  - aro de cooldown del dash;
  - hitmarker y kill marker;
  - aviso de daño direccional;
  - timer de la run y fase de peligro;
  - conteo de vivos, extraídos y muertos;
  - barra de extracción;
  - bonos de equipo;
  - kills.

  También agrega `PauseMenuController` y `HudExtras`.
- **`HudExtras`.** Crosshair configurable, números de daño (con el evento `AbilityController.OnDamageDealt(point, damage, isKill)`), FPS/ping y aviso de conexión inestable.
- **`PauseMenuController`** (Esc):
  - Resume, Options (el mismo `SettingsPanel`) y Leave Run con confirmación.
  - No pausa la simulación: es un juego online.
- **`ResultScreenController`.**
  - Muestra EXTRACTED o ELIMINATED, el tiempo, las kills, la lista de items y la causa de muerte (datos de `RunSummary`).
  - Botón "Return to Menu".

## 11. Opciones (`Settings/`)

Todas se guardan en `PlayerPrefs`. `SettingsPanel` construye las secciones y se usa tanto en el menú como en la pausa.

| Sección | Opciones |
|---|---|
| Audio | Volúmenes (`AudioVolumes`) |
| Controls | Sensibilidad, Invert Y (`LookSettings`) |
| Display | Modo de ventana, resolución, VSync, límite de FPS, calidad y FOV (`DisplaySettings`, evento `Changed`). El FOV se aplica a la Camera y a la CinemachineCamera; sin valor guardado se mantienen los originales |
| HUD | Crosshair, números de daño, FPS/ping (`HudSettings`) |

**Brillo:** no está implementado. El post-processing de la cámara está apagado, y activarlo encendería los efectos del Volume de la escena Run. Queda como decisión aparte.

## 12. Audio

- **`AudioLibrarySO`** está en `Resources` y se arma con los menús del Editor **Game/Audio/Build Default Audio Library** y **Game/Audio/Reset Audio Library To Defaults**. Usa los sonidos de Kenney que se agregaron al proyecto.
- **`GameAudio`** es un reproductor estático: UI, inventario, cast rechazado, contenedores, etc.
- **`FootstepAudio`** lo agrega `PlayerAvatarState.OnStartClient`.
- **Habilidades.** El audio de casteo e impacto está definido en cada `AbilitySO` y se resuelve por slot en cada cliente: los AudioClip no viajan por RPC.

## 13. Despliegue (PlayFab MPS)

### Infraestructura
- Región East US, VM Dasv4 de 2 cores, puerto UDP 7770 (`game_port`).
- La cuota gratuita es de 750 core-hours por mes, compartida entre todas las VMs. Con standby 1 encendido se gasta.
- **Housekeeping manual:**
  - después de probar, bajar el standby a 0 o borrar el Build;
  - hacer Shutdown de las instancias Active vacías;
  - limpiar los tags viejos del registry.
  - Si aparece "no virtual machines with available quota", hay que borrar Builds viejos.
- **Cola `SpellHaulRaid`:** mínimo 2 y máximo 6 jugadores, con regla de región (Latencies, máximo 1000 ms) y server allocation apuntando al Build vigente.

### Actualizar el servidor (cada vez que cambia código de servidor)
1. **Build.** En Unity: Build Profile Dedicated Server (Linux), botón **Build** (no Build and Run). Va a la carpeta `SpellHaul-LinuxServer`, que está fuera del repo.
2. **Secreto.** `playfab_secret.txt` (la clave secreta del título) va en esa carpeta, junto al `.x86_64`. Está en `.gitignore` del repo y no puede estar excluido en `.dockerignore`.
3. **Imagen.** `docker build -t spellhaul-server:vN .`, con N mayor que la última versión subida.
4. **Login al registry.** Game Manager → Multiplayer → Servers → New Build → "Upload to container registry" → "Copy Docker Login Command".
5. **Push.** `docker tag spellhaul-server:vN customerXXXX.azurecr.io/spellhaul-server:vN` y luego `docker push` de esa misma referencia.
6. **New Build.** Container Linux, Dasv4, imagen vN, UDP 7770 `game_port`, región East US.
7. **Cola.** Reapuntar `SpellHaulRaid` al Build nuevo.
8. **Probar.** Standby 1 y probar. Al terminar, standby 0 o borrar el Build.

Si cambió `cloudscript.js`, subir una revisión nueva (§6). Si cambió el kit inicial, actualizar el Title Data `StarterKit`.

### Dockerfile
```
FROM ubuntu:22.04
WORKDIR /game
COPY . .
RUN chmod +x /game/SpellHaul-LinuxServer.x86_64
RUN apt-get update && apt-get install -y --reinstall ca-certificates
CMD ["/game/SpellHaul-LinuxServer.x86_64", "-server", "-batchmode", "-nographics", "-logfile", "-"]
```

`.dockerignore`: `SpellHaul_BurstDebugInformation_DoNotShip/`

**Archivos fuera del repo:** el Dockerfile, `.dockerignore`, `playfab_secret.txt`, el `MultiplayerSettings.json` de LocalMultiplayerAgent y `launch_player2.bat` solo existen en el disco local. Conviene respaldarlos (sin subir el secreto).

### Pruebas
- **Nivel 1:** conexión directa por IP con los roles de `NetworkBootstrap`, o con Multiplayer Play Mode. Sin PlayFab.
- **Nivel 2:** LocalMultiplayerAgent con Docker (`.\LocalMultiplayerAgent.exe -lcow`). Valida el binario Linux, el GSDK y el ciclo de vida. `PortMappings` con protocolo **UDP**.
- **Nivel 3 (flujo real):**
  - Rol None, login, Play, cola y servidor en Azure. Gasta cuota.
  - Dos instancias en la misma PC: la segunda se lanza con `-playerid player2`.
  - Con standby 0 el primer match tarda más, porque el container arranca en frío.

## 14. Estado y pendientes

**Implementado y probado**
- Movimiento predicho, ataque básico, dash y habilidades por guante; lag compensation híbrida.
- Proyectiles simulados en el cliente.
- 3 tipos de enemigo, loot, cofres, extracción y fase de peligro simplificada.
- Inventario de run y stash con orden y apilado.
- Persistencia autoritativa: CloudScript, servidor y guard por `RunId`.
- Reconexión y abandono.
- Matchmaking real y deploy en MPS.
- Pausa, Leave Run y pantalla de resultados.
- Audio, opciones y HUD extras.

**Pendiente o abierto**
- **Progresión:** trader y crafting, economía del hub (la opción recomendada como siguiente paso).
- Rebinding de teclas.
- Íconos de items (faltan los assets; hoy son placeholders).
- Brillo, que requiere habilitar post-processing.
- Contenido de la fase de peligro (hunters y noche) y generación procedural del mapa (seed del servidor).
- Más guantes y escuelas (Control, Movilidad…).
- Qué son los "usables".
- `LoginWithSteam` (falta el AppID) y QoS real para la latencia del matchmaking.
- CI/CD del servidor.

**Preguntas abiertas de diseño**
- Ambientación y estilo visual.
- Tamaño de grupo.
- Reglas de pérdida al morir (hoy es total).
- Tipo de puntos de extracción.
- Moneda única o materiales.
- Escalado porcentual de stats (hoy todo es aditivo).
