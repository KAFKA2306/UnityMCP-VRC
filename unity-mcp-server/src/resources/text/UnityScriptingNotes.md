# Unity C# scripting notes (for execute_editor_command)

Gotchas when writing C# that runs in the Editor via `execute_editor_command`. These bite
silently — the code compiles and often *looks* like it worked.

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
