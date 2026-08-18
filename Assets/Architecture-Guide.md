# Core Architecture Guide

How to use `Singleton<T>`, the `GameEvent`/`GameEventListener` pattern, and the manager
classes (`SaveManager`, `InputManager`, `AudioManager`, `GameManager`, `MenuManager`) in
this base project  — and how to add new ones without breaking the pattern.

---
## 1. Basic Structure
This project follows best-practices for file structure, assuming a large or growing 
project. This means that each primary folder belongs to a subsystem, though the 
`Settings` and `Resources` folders are exceptions. Each subsystem should be 
independent and functional without any other subsystem besides `Core`. For 
this reason, subsystems should not reference other subsystems or their classes. 
Ideally, use a `GameEvent` for major events requiring communication to different 
subsystems (see section 3). If a subsystem is closely related to another, it could 
be contained within that subsystem, such as a `Combat` subsystem inside the `Player` 
subsystem for a game with advanced combat mechanics. 

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
  completed.
- If `Instance` logs `"[Singleton] X is null!"`, it means something ran before
  Bootstrapper finished (a scene opened directly in the Editor without going through
  `Bootstrap.unity` first is the most common cause).

### Dependency direction
Keep it one-way: `Core` (Singleton, SaveManager) should never reference `Game`-,
`Audio`-, or `Player`-specific types. Those layers depend on `Core`, not the other way
around. If you find `SaveManager` wanting to know about `AudioCategory`, stop — that's
a sign the data belongs in the calling manager, passed in as a plain value or a
`[Serializable]` type. 

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
  registration order with no priority system; if system A must react before system B,
  a `GameEvent` is the wrong tool — call A and B directly and explicitly.
- **Payloads**, as shipped — the base `GameEvent.Raise()` takes no arguments. If you
  need to pass data along, add a generic variant rather than smuggling it through a
  static field:
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
   `UnregisterListener(this)` yourself in `OnEnable`/`OnDisable` on a plain
   `MonoBehaviour` if you don't want a full `GameEventListener` component.

### Guardrails worth adding as you build on this
- `GameEventListener.OnEnable` will throw if `Event` isn't assigned in the Inspector —
  guard with `if (@event == null) return;` before shipping a menu that uses it.
- One listener throwing inside `Raise()` currently breaks every listener queued after
  it that frame. Wrap the invoke in `try/catch` + `Debug.LogException` once you have
  more than a couple of listeners on any one event, so one bug can't silently eat
  everyone else's reaction.
- Don't chain: avoid a listener's response itself raising the *same* event
  (directly or via a few hops) — it's an easy way to get an infinite loop with no
  stack trace pointing at the real cause.

---

## 4. Manager classes

### The role of Bootstrapper
Managers should never instantiate themselves or find themselves via
`FindObjectOfType`. `Bootstrapper` is the *only* thing that creates the persistent
manager prefabs, in `Boot.unity`, before any gameplay scene loads. If you add a new
manager, add its prefab to `Bootstrapper`'s serialized fields and instantiate it there
— don't scatter manager prefabs across other scenes.

### SaveManager — the one persistence gateway
Every other manager should go through `SaveManager` rather than touching
`PlayerPrefs` or `System.IO` directly:

| Use case | Call |
|---|---|
| A slider value, a toggle, a rebind blob | `SaveManager.Instance.SaveFloat/SaveBool/SaveString(...)` |
| A small `[Serializable]` settings object | `SaveManager.Instance.SaveObject<T>(...)` / `TryLoadObject<T>(...)` |
| A full game save (progress, inventory, world state) | `SaveManager.Instance.SaveGame<T>(slot, data)` / `TryLoadGame<T>(...)` |

Keeping this centralized means: one place to add encryption or cloud sync later, one
place that knows the on-disk layout, and no other manager needs to care whether a
value lives in `PlayerPrefs` or a JSON file on disk.

### InputManager — the one join/leave/rebind gateway
- Don't call `PlayerInputManager.JoinPlayer()` directly from gameplay code — go through
  `InputManager.JoinPlayer()` so join/leave bookkeeping (`ActivePlayers`, rebind
  loading) stays consistent.
- `PlayerController` (and any other per-player script) should read input off its own
  `GetComponent<PlayerInput>()`, never construct its own copy of the generated actions
  class. That's what actually ties a script to *one player's* paired device(s) in
  local multiplayer.
- Rebinding: call `InputManager.Instance.StartRebind(playerIndex, actionName,
  bindingIndex)` from a Settings/Controls menu button. Subscribe to
  `OnRebindComplete`/`OnRebindCanceled` (or pass the per-call callbacks) to refresh
  whatever UI is showing the current binding.

### AudioManager — categories and pooling
- **Pick the right call**: `PlayAtPoint` for something that happens once at a fixed
  spot, `PlayAttached` for something that should follow a moving object (and hang onto
  the returned `AudioEmitter` if you'll need to `Stop()` it early), `PlayUI` for
  anything that isn't diegetic.
- **Volume changes go through `SetVolume(AudioCategory, 0..1)`** — never set an
  `AudioSource.volume` directly on a pooled emitter for a persistent preference; that
  bypasses both the mixer and the save data.
- **Music** is a queue, not a stack: `PlayMusic` replaces what's playing right now and
  clears anything queued; `QueueMusic` appends and only starts immediately if nothing
  is currently playing. Use `PlayMusic` for hard cuts (boss fight starts *now*),
  `QueueMusic` for playlists.
- **Keyframes** live on the `AudioTrackData` asset, not in code — add them next to the
  track so a designer can retime a cue without a script change, the same way
  `GameEventListener` keeps response wiring in the Inspector instead of code.

### GameManager — recommended expansion
As shipped, `GameManager` only wraps scene loading. Before this template is "done,"
give it an actual state (`enum GameState { Boot, MainMenu, Playing, Paused }`) and have
state transitions raise `GameEvent`s (`OnGamePaused`, `OnGameResumed`) so `AudioManager`
can duck the mix, `MenuManager` can show the pause screen, and gameplay systems can
stop simulating — all without referencing each other.

### MenuManager — one entry point for screen changes
Always go through `OpenMenu`/`GoBack`/`CloseMenu` rather than calling `.Open()`/
`.Close()` on a `MenuBase` directly — that's what keeps the back-stack (`_history`)
correct. If you add a new screen, prefer extending `MenuManager`'s menu list over
calling `menuInstance.Open()` from gameplay code.

---

## 5. Checklist: adding a new manager

1. Create the class under the right namespace/folder (`Core` for generic
   infrastructure, `Game`/`Audio`/`Player`/etc. for domain-specific ones).
2. Inherit `Singleton<YourManager>`.
3. If you override `Awake()`: call `base.Awake()` first, then `if (IsDuplicate)
   return;` before any setup.
4. If you override `OnDestroy()`: call `base.OnDestroy()`.
5. Route all persistence through `SaveManager` — don't add a second place that talks to
   `PlayerPrefs`/disk.
6. If other systems need to react to something this manager does, expose a C# `event`
   (for tight, code-level integrations) and/or raise a `GameEvent` (for loose,
   designer-wireable integrations) — don't have other managers poll it every frame.
7. Add the manager's prefab to `Bootstrapper`'s serialized fields and instantiate it
   there alongside the others.
