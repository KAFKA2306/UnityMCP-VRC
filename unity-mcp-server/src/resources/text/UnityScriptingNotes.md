# Unity C# scripting notes (for execute_editor_command)

Gotchas when writing C# that runs in the Editor via `execute_editor_command` — what won't
compile, and what compiles but bites silently (the code *looks* like it worked).

## The executor compiles as C# 7.0

Commands are compiled at language version **C# 7.0**, so newer syntax is a hard compile
error. Most common one: the **bare `default` literal isn't allowed** — write the typed
form `default(T)`:

```csharp
int n = default;            // ERROR (C# 7.1 feature)
CancellationToken ct = default;
int n = default(int);                       // OK
CancellationToken ct = default(CancellationToken);   // OK
```

Other newer features that won't compile here — use the C# 7.0 equivalent instead:

- **switch expressions** (`x switch { ... }`, C# 8) → use a classic `switch` statement.
- **`using` declarations** (`using var x = ...;`, C# 8) → use a `using (...) { }` block.
- **ranges/indices** (`arr[^1]`, `arr[1..3]`, C# 8) → index/`GetRange` explicitly.
- **`??=`** (C# 8) → `if (x == null) x = ...;` (and mind the fake-null caveat below).
- **target-typed `new()`** (C# 9) → name the type: `new Foo()`.

`default(T)` aside, prefer plain, conservative C# and you'll stay inside 7.0. The
compiler rejects newer syntax with a recognizable message — `CS1644: Feature '…' cannot
be used because it is not part of the C# 7.0 language specification`, or a parse error
like `CS1525: Unexpected symbol`. If you see either, downgrade the syntax rather than
retrying the same code.

## Unity's "fake null" defeats `??`, `?.`, and `is null`

`UnityEngine.Object` (so `GameObject`, `Component`, `MonoBehaviour`, assets, …) overloads
`==`/`!=` to report a *destroyed or missing* object as equal to `null`. That overload is the
only thing that knows about the fake-null state — the C# null operators bypass it:

- `obj ?? fallback` does **not** fall back for a fake-null Unity object.
- `obj?.Foo()` does **not** short-circuit; it calls through and can throw.
- `obj is null` / `ReferenceEquals(obj, null)` report **false** for a fake-null object.

**The trap that started this note:**

```csharp
// WRONG: ?? sees a non-real reference, so it never adds the component, and a
// later access throws MissingComponentException.
var mr = go.GetComponent<MeshRenderer>() ?? go.AddComponent<MeshRenderer>();
```

**Do this instead — use the overloaded `==` via an explicit check:**

```csharp
var mr = go.GetComponent<MeshRenderer>();
if (mr == null) mr = go.AddComponent<MeshRenderer>();   // == is the Unity-aware comparison
```

Or `GetComponent` + `TryGetComponent` (which returns a real bool):

```csharp
if (!go.TryGetComponent<MeshRenderer>(out var mr)) mr = go.AddComponent<MeshRenderer>();
```

Rule of thumb: for anything deriving from `UnityEngine.Object`, test with `== null` /
`!= null`, never with `??`, `?.`, `is null`, or `ReferenceEquals`.

## Other quiet failure modes

- **`GetComponent<T>()` returns real `null`** (not fake-null) when the component is absent,
  so the `== null` check above is correct for both the "missing" and "destroyed" cases.
- **Edits aren't persisted automatically.** After changing a scene object call
  `EditorUtility.SetDirty(obj)` (and `EditorSceneManager.MarkSceneDirty(...)` for scene
  changes); for assets, `AssetDatabase.SaveAssets()`. Otherwise the change can be lost on
  reload and won't show up in a later read.
- **Writing a `.cs` file into `Assets/` triggers a recompile + domain reload.** That tears
  the MCP connection down briefly — expect the *next* call to retry/reconnect. Do the file
  write as the last step of a command, not mid-sequence with more work depending on it.
- **Return JSON-serializable data.** `EditorCommand.Execute()`'s return value is serialized
  back to the caller; hand back plain values / anonymous objects (ids, names, counts, paths),
  not live `UnityEngine.Object` references.
