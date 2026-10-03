# Core Architecture Guide

How to use `Singleton<T>`, the `GameEvent`/`GameEventListener` pattern, and the manager
classes (`SaveManager`, `InputManager`, `AudioManager`, `GameManager`, `NetworkManager`,
`MenuManager`) in this base project — and how to add new ones without breaking the pattern.

---
## 1. Basic Structure

Each top-level folder under `Assets/` is a subsystem with a matching namespace:

| Folder / namespace | Contents |
|---|---|
| `Core` | `Singleton<T>`, `Bootstrap` (scene + `Bootstrapper`), `Events` (`GameEvent`), render-pipeline `Settings` |
| `Save` | `SaveManager` — the one persistence gateway |
| `Audio` | `AudioManager`, `AudioEmitter`, `AudioTrackData`, the mixer |
| `Player` | `PlayerController`, `NetworkPlayerOwnership`, `Input` (`InputManager`, generated input actions) |
| `Game` | `GameManager` (scene loading, graphics settings), `DefaultScene` |
| `Network` | `NetworkManager` (sessions, game start, late joins), `Spawning` (`SpawnManager`, `SpawnPoint`), `SoloOnly`, the Network/NetworkPlayer prefabs |
| `Menu` | `MenuManager`, UI Toolkit documents/styles, `Components` (custom controls) |
| `Resources` | Assets loaded by path at runtime (exception to the subsystem rule) |

### Dependency rules
Dependencies only point "down" this list:

1. **`Core`** depends on nothing in the project.
2. **`Save`** depends only on `Core`.
3. **Domain subsystems** (`Audio`, `Player`, `Game`, `Network`, and any you add) may depend
   on `Core` and `Save`, but **not on each other**. When one needs to react to another,
   use a `GameEvent` (section 3) or a C# event exposed by the lower-level side.
4. **`Menu` is the UI wiring layer.** It may call any manager directly (it reads volumes
   from `AudioManager`, applies graphics through `GameManager`, drives sessions through
   `NetworkManager`). **Nothing may depend on `Menu`.** Keep game logic out of it: the
   menu should translate clicks into manager calls and manager events into UI updates.

If a subsystem is closely related to another, it can live inside it (e.g. a `Combat`
folder inside `Player`). These rules are currently enforced by convention only — see
"Assembly definitions" in section 7.

---

## 2. Singleton<T\>

### What it's for
Scene-persistent, single-instance systems that live for the whole session: managers.
Everything instantiated by `Bootstrapper` should be one of these.

### What it's *not* for
`PlayerController`, UI screens, pooled objects, anything that legitimately has zero,
one, or many instances. If you're tempted to make something a Singleton just to get
easy global access, pass a reference or fire a `GameEvent` instead.

### Writing a new manager
```csharp
public class ExampleManager : Singleton<ExampleManager>
{
    protected override void Awake()
    {
        base.Awake();
        if (IsDuplicate) return; // <- always do this if you override Awake

        // your setup here
    }

    private void Start()
    {
        if (IsDuplicate) return; // <- and here, if Start does setup
        // cross-manager wiring here
    }

    protected override void OnDestroy()
    {
        base.OnDestroy(); // <- always do this if you override OnDestroy
        // your cleanup here
    }
}
```
`IsDuplicate` is `true` for the rest of the frame if this object just self-destructed
because another instance already existed. Skipping the check means your setup code
(loading data, subscribing to events, building object pools) runs once more on an
object that's about to disappear — wasted work at best, double-registered event
listeners at worst.

### Accessing a manager
```csharp
AudioManager.Instance.PlayUI(clickSound);
```
- Safe to call from **`Start()`**, **`OnEnable()`** on non-manager objects, or any
  gameplay code that runs after the scene has loaded — Bootstrapper guarantees all
  managers exist by then.
- **Don't** call another manager's `.Instance` from inside a *different* manager's
  `Awake()`. Bootstrapper instantiates managers synchronously in a fixed order, but
  `Awake()` isn't guaranteed to have *finished* running on every other object yet. Use
  `Start()` for cross-manager wiring instead — by then every `Awake()` in the scene has
  completed. Conversely, anything other managers read in *their* `Start()` must be
  ready by the end of your `Awake()` (e.g. `AudioManager` loads saved volumes in
  `Awake` so `MenuManager.Start` sees the right values whichever runs first).
- If `Instance` logs `"[Singleton] X is null!"`, it means something ran before
  Bootstrapper finished — for example a script in a scene that runs in `Awake`, or
  Play pressed with "Always Start From Bootstrap" turned off (section 4, "Bootstrapper").

---

## 3. GameEvent / GameEventListener

### What it's for
Decoupled, one-to-many notifications between systems that shouldn't hold direct
references to each other — "the player died," "the level was completed," "a checkpoint
was reached." The `GameManager` can raise `OnPlayerDied` without knowing that `Audio`,
`UI`, and analytics are all listening.

### What it's *not* for
- **High-frequency data.** Don't raise a `GameEvent` every frame for player position or
  health percentage — that's what a plain C# property or event is for. `GameEvent` is
  for *moments*, not *streams*.
- **Anything that needs a return value or guaranteed ordering.** Listeners fire in
  reverse registration order with no priority system; if system A must react before
  system B, a `GameEvent` is the wrong tool — call A and B directly and explicitly.
- **Payloads**, as shipped — the base `GameEvent.Raise()` takes no arguments. If you
  need to pass data along, add a generic variant rather than smuggling it through a
  static field (a ready-made `GameEvent<T>` is on the roadmap, section 7):
  ```csharp
  [CreateAssetMenu(menuName = "Events/GameEvent (int)")]
  public class IntGameEvent : ScriptableObject
  {
      private readonly List<Action<int>> _listeners = new();
      public void Raise(int value) { for (int i = _listeners.Count - 1; i >= 0; i--) _listeners[i](value); }
      public void RegisterListener(Action<int> l) => _listeners.Add(l);
      public void UnregisterListener(Action<int> l) => _listeners.Remove(l);
  }
  ```

### Setting one up
1. **Create the asset**: right-click in the Project window →
   `Create > Events > GameEvent`. Name it for what happened, in past tense:
   `On_PlayerDied`, `On_LevelComplete`, `On_CheckpointReached`. Keep them all under one
   `Assets/Core/Events/` (or per-feature) folder so they're easy to find and audit.
2. **Raise it from code**:
   ```csharp
   [SerializeField] private GameEvent onPlayerDied;
   private void Die() => onPlayerDied.Raise();
   ```
3. **Listen to it**: add a `GameEventListener` component to any GameObject, drag the
   event asset into the `Event` field, and wire up `Response` in the Inspector — or,
   for code-driven reactions, call `@event.RegisterListener(this)` /
   `UnregisterListener(this)` yourself in `OnEnable`/`OnDisable`.

### Built-in guardrails
- `GameEventListener` logs a warning and does nothing if `Event` isn't assigned.
- Each listener is invoked inside `try/catch` + `Debug.LogException`, so one faulty
  listener can't stop the others from reacting.
- Registering the same listener twice is ignored.

### Still your responsibility
- Don't chain: avoid a listener's response itself raising the *same* event
  (directly or via a few hops) — it's an easy way to get an infinite loop with no
  stack trace pointing at the real cause.
- `GameEvent` assets keep their listener list in memory. If you turn off **Domain
  Reload** in Enter Play Mode Settings, stale listeners (and `Singleton` instances)
  survive between play sessions — leave Domain Reload on unless you add reset code.

---

## 4. Manager classes

### Bootstrapper
Managers should never instantiate themselves or find themselves via
`FindObjectOfType`. `Bootstrapper` is the *only* thing that creates the persistent
manager prefabs, in **`Assets/Core/Bootstrap/Bootstrap.unity`** (build index 0), before
any gameplay scene loads. It then loads `firstSceneAfterBoot` (currently `Menu`); if
that field is empty it logs a warning and stays in the bootstrap scene.

**Press Play from any scene.** The editor script
`Core/Bootstrap/Editor/BootstrapPlayModeStarter.cs` sets
`EditorSceneManager.playModeStartScene` to the first enabled scene in Build Settings
(`Bootstrap.unity`), so Play always boots the managers first. It remembers the scene
you had open (in `SessionState`), and `Bootstrapper` loads that scene instead of
`firstSceneAfterBoot`. Pressing Play in `Bootstrap.unity` itself boots normally into `Menu`.

- Toggle it with **Tools › WavelineBase › Always Start From Bootstrap** (on by default,
  saved per machine in `EditorPrefs`). With it off, playing a non-bootstrap scene means
  no managers exist.
- Unsaved scene changes: entering Play offers to save them (Play mode loads scenes from
  disk); choosing Cancel cancels Play.
- Only the *active* scene is returned to; with several scenes open, additively loaded
  ones aren't restored in Play mode.
- Scripts in your scene still run one frame after the managers, so `Awake`/`Start` in
  that scene can safely use `.Instance`. Editor only — builds always start at build index 0.

If you add a new manager, add its prefab to `Bootstrapper`'s `managerPrefabs` list —
don't scatter manager prefabs across other scenes.

### SaveManager (`Save` namespace) — the one persistence gateway
Every other manager goes through `SaveManager` rather than touching `PlayerPrefs` or
`System.IO` directly:

| Use case | Call |
|---|---|
| A slider value, a toggle, a rebind blob | `SaveManager.SaveFloat/SaveInt/SaveBool/SaveString(...)` (static) |
| A small `[Serializable]` settings object | `SaveManager.SaveObject<T>(...)` / `TryLoadObject<T>(...)` (static) |
| Write pending settings to disk now | `SaveManager.Flush()` (static) |
| A full game save (progress, inventory, world state) | `SaveManager.Instance.SaveGame<T>(slot, data)` / `TryLoadGame<T>(...)` |

- **Settings methods are static** because `PlayerPrefs` is global; they work as soon
  as the game starts. **Game-save methods are on the instance.**
- Settings are **not** written to disk on every call (dragging a slider would write
  dozens of times a second). They're flushed on `Flush()`, on application
  pause/quit, and when the manager is destroyed. `MenuManager` flushes when the
  Settings panel closes. Call `Flush()` yourself after an important batch of changes.
- Game saves are written to a temp file, then swapped in with `File.Replace`, so a
  crash mid-write can't corrupt an existing save. Slot names become file names: empty
  names, path separators and other invalid characters are rejected.

Keeping this centralized means: one place to add encryption or cloud sync later, one
place that knows the on-disk layout, and no other manager needs to care whether a
value lives in `PlayerPrefs` or a JSON file on disk.

### InputManager — the one join/leave/rebind gateway
- Don't call `PlayerInputManager.JoinPlayer()` directly from gameplay code — go through
  `InputManager.JoinPlayer()` so join/leave bookkeeping (`ActivePlayers`, rebind
  loading) stays consistent.
- `autoJoinFirstPlayer` is **off** by default: `InputManager` starts in the bootstrap
  scene, so a player joined there is destroyed when the next scene loads. Instead,
  place a Player prefab in the gameplay scene (as `DefaultScene` does — it registers
  itself as player 0), or call `InputManager.Instance.JoinPlayer()` after the scene loads.
- `PlayerController` (and any other per-player script) should read input off its own
  `GetComponent<PlayerInput>()`, never construct its own copy of the generated actions
  class. That's what ties a script to *one player's* paired device(s) in local
  multiplayer.
- Rebinding: call `InputManager.Instance.StartRebind(playerIndex, actionName,
  bindingIndex)` from a Settings/Controls menu button. Subscribe to
  `OnRebindComplete`/`OnRebindCanceled` (or pass the per-call callbacks) to refresh
  whatever UI is showing the current binding. Overrides are saved per player index.
- The project-wide actions setting (Project Settings › Input System Package) is
  currently unset; the Player prefab uses `PlayerInputActions` directly.

### AudioManager — categories and pooling
- **Pick the right call**: `PlayAtPoint` for something that happens once at a fixed
  spot, `PlayAttached` for something that should follow a moving object (and hang onto
  the returned `AudioEmitter` if you'll need to `Stop()` it early), `PlayUI` for
  anything that isn't diegetic.
- **Volume changes go through `SetVolume(AudioCategory, 0..1)`** — never set an
  `AudioSource.volume` directly on a pooled emitter for a persistent preference; that
  bypasses both the mixer and the save data. UI sliders that run 0–100 must divide by
  100 (as `MenuManager` does).
- The mixer must expose float parameters named `{AudioCategory}Volume`:
  `MasterVolume`, `MusicVolume`, `SfxVolume`, `UiVolume`.
- Saved volumes are read in `Awake` (so `GetVolume` is right for everyone's `Start`) and
  pushed to the mixer in `Start` (`AudioMixer.SetFloat` is unreliable during `Awake`).
- **Pooling details**: one-shot emitters return to the pool on a real-time timer, so UI
  sounds still recycle while `Time.timeScale` is 0. A one-shot attached to a target
  that gets deactivated is reclaimed (or resumes its timer if the target comes back).
  Looping attached sounds are never reclaimed automatically — `Stop()` them yourself
  before disabling or destroying the target.
- **Music** is a queue, not a stack: `PlayMusic` replaces what's playing right now and
  clears anything queued; `QueueMusic` appends and starts immediately if nothing is
  playing (including after a non-looping track has finished). Use `PlayMusic` for hard
  cuts (boss fight starts *now*), `QueueMusic` for playlists.
- **Keyframes** live on the `AudioTrackData` asset, not in code — add them next to the
  track so a designer can retime a cue without a script change.

### GameManager — scenes and graphics settings
- `GameManager.LoadScene(name)` wraps `SceneManager.LoadScene` (synchronous for now).
- Graphics settings are applied **and** saved through `GameManager.ApplyResolution`,
  `ApplyDisplayMode`, `ApplyVSync` and `ApplyMsaa`; saved values are restored in
  `GameManager.Start`. Menus should call these rather than setting `Screen`/
  `QualitySettings` themselves.
- **MSAA under URP** is set on the active URP asset (`msaaSampleCount`), because URP
  ignores `QualitySettings.antiAliasing`. In the Editor this edits the asset file
  itself, so changing MSAA during Play mode shows up as a modified `PC_RPAsset`/
  `Mobile_RPAsset` in version control — revert it if you didn't mean to change the default.
- **Recommended expansion** (not done yet): give it an actual state
  (`enum GameState { Boot, MainMenu, Playing, Paused }`) and have transitions raise
  `GameEvent`s (`OnGamePaused`, `OnGameResumed`) so `AudioManager` can duck the mix,
  `MenuManager` can show a pause screen, and gameplay can stop simulating — all without
  referencing each other.

### NetworkManager — sign-in, sessions and starting the game
`Network.NetworkManager` lives on `Assets/Network/NetworkManager.prefab` together with
Netcode for GameObjects' (NGO) `Unity.Netcode.NetworkManager`, `UnityTransport` and our
`SpawnManager`. The two NetworkManagers are different classes; in code inside the
`Network` namespace, refer to NGO's as `Unity.Netcode.NetworkManager` (or alias it,
as the scripts do with `NetcodeManager`). The code is split over two files of the same
component: `NetworkManager.cs` (sign-in, sessions, queries, quitting) and
`NetworkManager.GameFlow.cs` (game start, approval, late joins, spawning hooks).

**Sessions**
- Signs in anonymously to Unity Gaming Services in `Start`. `IsReady` becomes true (and
  `Ready` fires) once sign-in and the player-name fetch finish; every session call
  throws `InvalidOperationException` before that.
- `StartSessionAsHost(name, password)`, `JoinSessionById(id, password)` and
  `JoinSessionByCode(code, password)` leave any current session first, attach the
  player's name (`WithPlayerName`, visible to members), and raise `SessionJoined`.
  Sessions use `.WithRelayNetwork()`, which starts NGO as host/client automatically.
- `LeaveSession(reason)` clears `CurrentSession` and raises `SessionLeft` immediately,
  then tells the service. `SessionLeft` also fires if the player is kicked, the host
  deletes the session, or a client loses its NGO connection to the host (for example
  because approval rejected it) — the client then leaves the session itself so it
  doesn't sit in a lobby with no connection.
- **Passwords**: empty means "no password"; otherwise 8–64 characters
  (`NetworkManager.IsValidPassword`). Joining a password-protected session from the
  list currently fails because the menu has no password prompt yet.

**Session properties** (set by the host, public, readable in the session list)
| Key | Values | Purpose |
|---|---|---|
| `state` | `Lobby`, `Loading`, `InGame` | `SessionGameState`; clients mirror it into `GameState` |
| `joinable` | `"1"` / `"0"` | Can someone join right now? Indexed as **string property 4** so queries can filter on it |

`joinable` is `"1"` when the session isn't locked and is either in the lobby, or in game
with late join allowed. Custom string property **index 4 is reserved** for it
(`SetStringFilter(4, …)` is refused).

**Session list (queries)**
- `QuerySessionsAsync(continuationToken)` returns one page of `queryCount` sessions.
  Pass the previous result's `ContinuationToken` for the next page; an empty token
  means no more pages. Unless *Show Unjoinable Sessions* is on, only `joinable = "1"`
  sessions are returned. Calls closer together than *Min Query Interval Seconds*
  (default 1 s) are delayed, because the lobby service rate-limits queries.
- `SetStringFilter`/`SetNumberFilter(propertyIndex, …)` set (replace) a filter on the
  session's custom properties (string indexes 0–3, number indexes 0–4);
  `RemoveStringFilter`/`RemoveNumberFilter`/`ClearFilters` remove them. Sort options
  are kept in add order.

**Starting the game** (`StartGame(lockSession)`, host only — the lobby's Start button)
1. Refused unless this player is the session host, NGO is running as host with
   *Enable Scene Management* on, and the state is `Lobby`.
2. State → `Loading`. From this moment the approval callback holds or rejects new
   connections (below).
3. If locking (the lobby's *Lock on start* toggle, defaulting to *Lock Session On
   Start*), the session is locked; `state`/`joinable` are updated; all of it is saved to
   the lobby service **before** the level loads. If the lock can't be saved, the start is
   aborted and everything reverts to the lobby — a lock that was asked for is guaranteed.
4. NGO's scene manager loads *Gameplay Scene Name* (default `DefaultScene`, must be in
   Build Settings) in Single mode for everyone. Each client's menu hides when it loads.
5. When NGO reports the load finished (`OnLoadEventCompleted`): state → `InGame`, the
   properties are published, and every client that finished loading (plus the host) is
   spawned in one batch by `SpawnManager`.
6. If starting fails at any point, the state returns to `Lobby` and a lock taken by the
   start is released.

**Late joins and joining mid-transition** (enforced by NGO connection approval)
| State | *Allow Late Join* off | *Allow Late Join* on |
|---|---|---|
| `Lobby` | approved | approved |
| `Loading` | rejected: "The game is starting." | held (pending) until the load finishes, then approved |
| `InGame` | rejected: "The game is already in progress." | approved; spawned once NGO has synchronized them |

- Approval also tells NGO **not** to create player objects (`CreatePlayerObject = false`);
  `SpawnManager` spawns players only after they've loaded the level, which fixes players
  appearing at the origin of the Menu scene.
- Connection approval is part of NGO's shared config, so `NetworkManager` turns it on for
  every peer at startup (the prefab also has it ticked). If something replaces the
  approval callback, it's restored with a warning — with approval on and no callback,
  clients could never connect.
- Clients that time out while loading (NGO's *Load Scene Time Out*) are disconnected if
  *Disconnect Clients That Time Out* is on; otherwise they spawn whenever they finish.
- Rejected clients are disconnected with the reason, leave the session, and return to the
  menu. A session player who joined the lobby just before a lock is caught the same way
  when their NGO connection arrives.
- Held connections are still subject to NGO's *Client Connection Buffer Timeout*; for very
  long loads, raise it or leave late join off.
- *Not covered yet*: returning everyone to the lobby after a match (state stays `InGame`
  until the session ends) and host migration.

**Quitting**
- **In builds** (and anything that calls `Application.Quit`), `NetworkManager` uses
  `Application.wantsToQuit` to hold the quit, leaves the session while Netcode is still
  running, then quits (or quits anyway after `quitLeaveTimeoutSeconds`, default 3 s).
  Without this, the service's own quit handler leaves after Netcode has shut down and
  logs `NetworkManagerSession.StopAsync: Called after dispose`.
- **Stopping Play mode in the Editor while in a session** may log the package's
  `Called after dispose` warning. It's harmless: the service still removes the player.
  Root cause (from the package source): on `ExitingPlayMode` the Wire package disposes
  every lobby subscription, so any leave still in progress fails at its final
  unsubscribe step with `ObjectDisposedException`. The service's own quit handler
  swallows that; `LeaveSession` does too (it logs it as expected instead of as an
  error). Stopping Play mode can't be delayed, so the Editor doesn't try to leave first.
  Press Back in the lobby before stopping to avoid the warning entirely.

**Diagnostics**: with `logSessionLifecycle` on (default), `NetworkManager` logs every
join, leave (with the reason, e.g. `lobby Back button`, `application quit`), removal,
game-state change, approval decision, spawn, `wantsToQuit`, quitting and Play-mode exit,
each with the frame number. Pass a reason when calling `LeaveSession(reason)` from new code.

### SpawnManager and SpawnPoint — networked player spawning
- **Set up a level**: add empty GameObjects with a `SpawnPoint` component where players
  should appear. Position = where the player's pivot goes; rotation = facing.
  `DefaultScene` has four at (±2, 0.55, ±2).
- **Gizmo = the real check**: each SpawnPoint draws, in green, exactly the volume
  `SpawnManager` will test there (the player's collider + padding, rotated with the
  point). In edit mode it finds `SpawnManager` on a prefab in the project (the Network
  prefab); grey means none was found and the point's *Gizmo fallback* sizes are shown.
  If the shape overlaps the floor or a wall in the Scene view, that point will be skipped.
- `SpawnPoint` options: **Order** (used by Fixed mode) and **Group** (only points in
  `SpawnManager`'s active group are used; empty = all).
- `SpawnManager` (on the Network prefab, server only) settings:
  - **Mode** — `Fixed` (each player gets a slot by join order and always uses that slot's
    point), `Random`, or `RandomUnique` (default: random, but no point is reused until all
    have been used). `Mode` and `SpawnGroup` can be changed at runtime.
  - **Player Prefab** — empty uses the *Player Prefab* on NGO's NetworkManager.
  - **Overlap prevention** — a point is skipped if the clearance shape overlaps anything
    on *Blocking Layers* (triggers ignored), or if a spot within one body-width was handed
    out in the last *Reservation Seconds* (so players spawned in the same moment never
    share a point, even before physics updates). If every point is blocked, it searches
    rings around the points; if still nothing, it spawns at a blocked point with a
    warning rather than not spawning. *Floor Skin* lifts the test volume slightly so
    standing on the floor doesn't count.
  - **Clearance shape** — with *Auto Clearance From Player Prefab* on (default), it's read
    from the player prefab's collider: a `CharacterController` (radius and height
    including its skin width), else a `CapsuleCollider`, `SphereCollider` or
    `BoxCollider` — on the root first, then children; triggers ignored — including the
    collider's center, child offset, rotation and scale. Change the player's collider and
    spawning (and the gizmos) follow. *Clearance Padding* adds room on every side. If the
    prefab has none of those colliders (or auto is off), the *Manual clearance* capsule is
    used (*Radius*, *Height*, *Center Offset*; (0,0,0) suits a pivot at the collider's
    center, (0, height/2, 0) a pivot at the feet) and a warning is logged. The shape in
    use is logged once per session: `[SpawnManager] Spawn clearance: Capsule from
    NetworkPlayer: CharacterController`.
  - **Use Fixed Seed** — repeatable random picks for testing.
- **Consistent across the network**: only the server picks. It instantiates the player
  at the chosen pose *before* `SpawnAsPlayerObject`, so the spawn message carries the
  position and every peer creates the player in the same place — no teleport.
  Players are spawned with `destroyWithScene`, so they disappear with the level.
- **API** (server): `SpawnPlayer(clientId)`, `SpawnPlayers(ids)`, `RespawnPlayer(clientId)`
  (despawns the old player object and spawns a new one, so it works whatever the
  NetworkTransform authority), `TryGetSpawnPose(clientId, out pose)`. Any time:
  `GetClearance()` (the `SpawnClearance` shape, with `Overlaps(...)` and `DrawGizmo(...)`)
  and `ResolvePlayerPrefab()`. Reach it through `NetworkManager.Instance.Spawner`
  (or `SpawnManager.Active` while playing).

### NetworkPlayer prefab and solo-only objects
- `Assets/Network/NetworkPlayer.prefab` is a variant of `Player` with a `NetworkObject`,
  a `NetworkTransform` set to **Owner** authority (the owning client moves its player and
  everyone else follows), and `NetworkPlayerOwnership`, which leaves `PlayerInput` and
  `PlayerController` enabled only on the owner's copy. Without that, every copy of every
  player read the local keyboard/gamepad. The `CharacterController` stays enabled on all
  copies so players still collide and `SpawnManager` can see them.
- `SoloOnly` removes its GameObject when a level loads while NGO is running. The Player
  placed in `DefaultScene` for Solo has it, so it doesn't appear in networked games.
- New networked prefabs (anything spawned with a `NetworkObject`) must be registered:
  add them to `Assets/DefaultNetworkPrefabs.asset` and add that list to *Network Prefabs
  Lists* on NGO's NetworkManager (currently only the Player Prefab is registered).

#### Testing session changes
Run every case after any networking change and note the `[NetworkManager]` /
`[SpawnManager]` log lines plus any warnings/errors. A change counts as progress only if
no case gets worse. Two-instance cases: a build plus the Editor, or two builds.

| # | Steps | Expected |
|---|---|---|
| 1 | Editor: create lobby → Back | `Leaving…(lobby Back button)` then `Left…`; no warnings/errors |
| 2 | Editor: create lobby → Back → stop Play | as 1; nothing extra on stop |
| 3 | Editor: create lobby → stop Play | `Exiting Play mode (in session: True)`; at most the package's `Called after dispose` warning; no red errors |
| 4 | Editor: create lobby → Back → immediately stop Play | `Left…already disposed… expected, ignored` allowed; no red errors |
| 5 | Build: create lobby → Quit button | quits within ~3 s; Player.log shows `Leaving…(application quit)` then `Left…` |
| 6 | Build: create lobby → Alt+F4 / window close | as 5 |
| 7 | Two instances: host creates, client joins; client Back | host's player list drops to one; client back on Play panel; no errors on either |
| 8 | Host alone: create lobby → Start | `Game state: Loading` → `InGame`; one player spawned at a SpawnPoint; no player in the Menu scene; Solo Player absent |
| 9 | Two instances: host + client in lobby → Start | both load the level; two players on **different** points; each instance moves only its own player and sees the other move |
| 10 | Host starts with *Lock on start* on; second instance refreshes the list | session no longer listed (not joinable) |
| 11 | Late join off: client joins the lobby, host presses Start at the same moment | client either loads with everyone, or is rejected ("The game is starting.") and returns to the menu — never stuck in the lobby, never unspawned |
| 12 | Late join on, lock off: start, then a client joins from the list | client is approved, syncs the level and spawns at a free point |
| 13 | Late join on: client connects while the host is still loading | `Holding connection…` then `Approved 1 held connection(s)`; client spawns after |
| 14 | Spawn overlap: put an object on top of a SpawnPoint, start with 4 players and 4 points | blocked point skipped; warning `All spawn points blocked; using a free spot near…` only when every point is blocked |
| 17 | Edit mode: select a SpawnPoint in `DefaultScene`; change the Player's CharacterController radius | green gizmo matches the player's capsule and updates with the change; on Start the log shows `Spawn clearance: Capsule from NetworkPlayer: CharacterController` |
| 15 | In game, host quits | client returns to the menu with `Disconnected from the host…`; no errors |
| 16 | Session list with more sessions than *Query Count* | scrolling to the bottom loads the next page; no duplicates; reopening Lobbies starts from the top |

Build logs are in `%USERPROFILE%\AppData\LocalLow\<Company>\<Product>\Player.log`.

### MenuManager — one entry point for screen changes
- Screens are `*Panel` elements in `MainMenu.uxml`; sections inside a panel are
  `*Subpanel` elements. Navigation goes through `OpenPanel`/`ClosePanel` (a back-stack
  in `_history`) and `OpenSubpanel`/`CloseSubpanel`; don't toggle panel visibility
  directly elsewhere. Any button named `BackButton` closes the current panel.
- The menu shows itself (reset to the main panel) whenever the scene named
  `menuSceneName` (default `Menu`) loads, and hides when any other scene loads in
  single mode. `ShowMainMenu()`/`HideMenu()` do the same on demand (e.g. a pause menu). The `UIDocument` stays enabled — **don't disable it**: re-enabling a `UIDocument`
  rebuilds its element tree, so every cached reference and click handler would be lost.
- UI is bound in `Start` (by then the `UIDocument` has built its tree).
- Settings controls are filled with `SetValueWithoutNotify`, so opening the menu never
  re-applies or overwrites saved settings.
- **Lobby flow**: Lobbies subpanel → server list → click a session to join, or New
  Lobby → Create. On `SessionJoined` the Lobby panel opens and shows the session name,
  join code, state (`starting…`/`in game`) and player list, refreshed on every session
  change. The host also sees **Lock on start** and **Start** (disabled once starting).
  Back on the Lobby panel leaves the session; being kicked or the session closing also
  returns to the Play panel — or, if it happens during a level, loads the Menu scene.
- **Server list paging**: opening Lobbies loads the first page; scrolling within 40 px of
  the bottom loads the next one; if a page doesn't fill the list (nothing to scroll), up
  to 5 more pages load automatically. Sessions are de-duplicated by id and locked ones
  are skipped. Reopening Lobbies starts again from page 1; results from an older refresh
  are discarded.
- Errors (sign-in still in progress, invalid password, failed join) are currently only
  logged to the Console — an on-screen message area is on the roadmap.

---

## 5. Checklist: adding a new manager

1. Create the class in the right folder and namespace (`Core` only for generic
   infrastructure; otherwise a domain folder like `Game`/`Audio`/`Player`, or a new one).
2. Inherit `Singleton<YourManager>`.
3. If you override `Awake()`: call `base.Awake()` first, then `if (IsDuplicate)
   return;` before any setup. Do the same check at the top of `Start()`.
4. If you override `OnDestroy()`: call `base.OnDestroy()`.
5. Route all persistence through `SaveManager` — don't add a second place that talks to
   `PlayerPrefs`/disk.
6. Respect the dependency rules (section 1): don't reference other domain managers. If
   other systems need to react to something this manager does, expose a C# `event`
   and/or raise a `GameEvent` — don't have other managers poll it every frame.
7. Add the manager's prefab to `Bootstrapper`'s `managerPrefabs` list.

---

## 6. Known gaps and housekeeping

- **No return-to-lobby after a match**: the state stays `InGame` until the session ends.
- **InputManager joining in networked games**: `PlayerInputManager`'s join action can still
  add extra *local* (non-networked) players during a networked game; turn joining off or
  gate it on `GameState` if your game uses local co-op and networking together.
- **Password-protected sessions can't be joined** from the menu (no prompt).
- **No way back to the menu** from gameplay yet: loading the `Menu` scene shows it again,
  but nothing in gameplay does that yet (needs a pause menu).
- **Version control**: the folder contains both a Git repo (`.git`) and Plastic SCM
  metadata (`.plastic`, `ignore.conf`). Pick one so changes don't get committed to
  one and missed in the other.
- **Visual Scripting** (`com.unity.visualscripting`) is installed but unused. Remove it
  if no one on the jam team needs it, to cut compile/import time.

---

## 7. Roadmap — worth adding

Not implemented yet; listed roughly in order of usefulness for game jams.

### Flow and states
- **`GameState` + pause menu**: `Boot/MainMenu/Playing/Paused` in `GameManager`, with
  `OnGamePaused`/`OnGameResumed` events; a pause panel with Resume, Settings and
  "Return to main menu" (loads `Menu`, which re-shows the menu automatically).
- **Async scene loading** with a fade or loading screen (`SceneManager.LoadSceneAsync`),
  replacing the synchronous `GameManager.LoadScene`.

### Settings
- **Controls / rebinding screen** using the existing `InputManager.StartRebind`.
- **"Reset to defaults"** and optional Apply/Cancel on the settings screen.
- **One `[Serializable]` settings object** saved via `SaveObject<T>` and applied at
  boot, instead of separate keys.

### Lobby and networking
- **Ready** state per player (Start enabled when all are ready), **kick** (host),
  **join-by-code** field, **refresh** button / pull-to-refresh for the server list, and a
  **password prompt** for password-protected sessions.
- **Return to lobby** after a match (load the Menu scene for everyone, state → `Lobby`,
  unlock) and optional **host migration**.
- On-screen error/status messages (connecting, rejected, wrong password, session full).

### Reusable building blocks
- **`GameEvent<T>`** with typed payloads (int, float, string, Vector3) and matching
  listeners.
- **Generic object pooling** built on `UnityEngine.Pool.ObjectPool<T>`.
- **Timers/cooldowns**, a **simple state machine**, and **tweening** helpers
  (UI transitions, screen shake).
- **Credits screen**, **FPS/debug overlay**, and a **build version label** on the
  main menu.

### Project health
- **Assembly definitions** (one `.asmdef` per subsystem folder) so the dependency rules
  in section 1 are enforced by the compiler and recompiles are faster.
- **EditMode tests** for `SaveManager` (slot names, round-trips) and the settings
  conversions (volume, MSAA, display mode, resolution parsing).
