# 006 — Attaching UdonSharp components from code

Adding a working UdonSharp ("U#") component to a GameObject programmatically is one of the easiest things
to get *almost* right and have silently not run in-world. This doc is the reliable recipe, why each step
is needed, and how `UnityMCP.VRChatUtils.UdonSharpHelper` packages it into one call. For the *caller-facing*
quick how-to see the `VRChatWorldNotes` MCP resource; for the C# the snippet itself must obey (mcs / C# 7.0,
fake null), see `UnityScriptingNotes`. Both live in `unity-mcp-server/src/resources/text/`. Verified against
VRChat Worlds SDK 3.10.x (UdonSharp 1.x).

## A live Udon component is three linked objects

What you see as "an UdonSharp behaviour on a GameObject" is really:

1. a **`UdonSharp.UdonSharpProgramAsset`** — the compiled program (a project asset, one per U# class);
2. a backing **`VRC.Udon.UdonBehaviour`** on the GameObject, whose `programSource` points at (1) and whose
   `serializedProgramAsset` holds the built Udon program;
3. the **proxy `UdonSharpBehaviour`** you see and edit in the inspector.

Only the proxy is "your" component; the program and backing behaviour are what actually execute. Miss any of
the three and the thing compiles, sits in the inspector, and does nothing at runtime.

## The trap: `AddComponent` and the missing builder

`gameObject.AddComponent<MyUSharpBehaviour>()` adds **only the proxy** — no program asset, no backing
UdonBehaviour. It looks fine in the editor and never runs in-world.

The supported builder is **`gameObject.AddUdonSharpComponent(type)`**. The catch that sends people the wrong
way: it's an **extension method in namespace `UdonSharpEditor`** (class `UdonSharpComponentExtensions`), **not**
a static on `UdonSharpEditorUtility`. If you reflect over `UdonSharpEditorUtility` looking for an "add"
method — the obvious move — you won't find it, and you may resort to hand-fabricating the program-asset YAML
(which is what this helper used to do, and it's brittle).

And even when you find it, `AddUdonSharpComponent` **does not create the program asset**. A brand-new U#
script has none, and it throws *"Unable to find valid U# program asset associated with script …"* until one
exists.

## The recipe

Four steps, in order:

1. **Ensure a program asset exists and is compiled.** Replicate what the U# inspector does:
   ```csharp
   var pa = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
   pa.sourceCsScript = monoScript;                     // the MonoScript for the .cs
   AssetDatabase.CreateAsset(pa, "Assets/.../MyUSharpBehaviour.asset");
   AssetDatabase.Refresh();
   UdonSharpProgramAsset.CompileAllCsPrograms(true);   // force -> serialized Udon program now exists
   ```
   (`UdonSharpProgramAsset.GetProgramAssetForClass(type)` returns the existing one, or null if absent.)
2. **Add the component** with the extension method: `var proxy = go.AddUdonSharpComponent(type);` — this
   creates the backing `UdonBehaviour` and links the now-existing program asset.
3. **Set public fields on the proxy** (plain reflection / assignment).
4. **`UdonSharpEditorUtility.CopyProxyToUdon(proxy)`** — without this, field values set on the proxy are
   **not** serialized into the UdonBehaviour heap and are lost at runtime.

## A brand-new program asset needs a SECOND pass

The single most surprising constraint, and the deepest reason this is "challenging": **you cannot create a
program asset and attach a component to it in the same synchronous call.** When the program asset for a
script didn't already exist, step 1 creates and compiles it, but `CopyProxyToUdon` in step 4 then throws:

```
InvalidOperationException: Cannot run serialization on U# behaviour '… (MyBehaviour)' with outdated
script version, wait until program assets have compiled.
```

This is *not* fixable by forcing the compile. Verified the hard way: calling `UdonSharpCompilerV1.CompileSync()`
(which logs `Compile of N scripts finished`) **and** invoking the editor manager's private `RunAllUpdates()`
reconcile pass, both in-call, still leave the new behaviour on an "outdated script version." UdonSharp only
reconciles a freshly-created program's compiled version **after control returns to the editor loop** (you can
see the deferred work in the later `Repaired reference to … on … (UdonBehaviour)` warnings).

So a brand-new script type is inherently **two editor passes**:

- **Pass 1** (one execute_editor_command): create + compile the program asset. Then *return* — let Unity tick.
- **Pass 2** (a separate execute_editor_command): attach the component(s); now it works.

An **already-existing** program asset attaches in a single call — so this only costs a second pass the first
time a given script is used in a project. `UdonSharpHelper.AddUdonSharpComponent` enforces this: if it had to
create the program asset it throws a clear *"created … call AddUdonSharpComponent again"* message rather than
failing deep inside `CopyProxyToUdon`. Batch callers (attaching to many objects) should call
`EnsureProgramAsset(type)` once, return, then do the whole batch on the next pass.

## How `UdonSharpHelper` does it

`UnityMCPPlugin/Editor/UdonSharpHelper.cs` wraps all four steps:

```csharp
var fields = new Dictionary<string, object> { { "speed", 90f }, { "axis", Vector3.up } };
UnityMCP.VRChatUtils.UdonSharpHelper.AddUdonSharpComponent(go, "MyUSharpBehaviour", fields);
```

Two deliberate choices:

- **It calls UdonSharp entirely by reflection.** The `UnityMCP.Editor` asmdef does **not** reference the
  VRChat SDK, so a hard dependency would stop the plugin compiling in non-VRChat projects. Reflection keeps
  UnityMCP SDK-agnostic; the Udon methods throw a clear "UdonSharp is not present" error where it's absent.
  Reflection over `AppDomain.CurrentDomain.GetAssemblies()` finds the types at runtime regardless of asmdef
  references.
- **`execute_editor_command` can call it directly** because the executor's reference set includes
  `UnityMCP*` assemblies (see `EditorCommandExecutor`). So an agent writes one line, not the whole dance.

## Timing & tooling gotchas

- **Write the `.cs`, let it compile, attach in a *separate* command.** A new script isn't a usable type
  until the recompile + domain reload finishes (see [004 — Unity Editor states](004-unity-editor-states.md)).
  That compile also confirms the behaviour's body is Udon-compatible — if it used a non-exposed API the U#
  compile would fail and produce no serialized program.
- **The executor is Mono `mcs` (C# 7.0): no local functions, no statement-bodied lambdas.** They emit a
  cascade of bogus parse errors; hoist helpers to static methods on the `EditorCommand` class. Details in the
  `UnityScriptingNotes` resource.

## Verifying the wiring (without Play mode)

On the backing `UdonBehaviour` (both are **fields**, not properties — reflect accordingly):

- `programSource` → the `UdonSharpProgramAsset`,
- `serializedProgramAsset` → a `SerializedUdonProgramAsset` (program built and linked),
- serialized property `serializedPublicVariablesBytesString` is non-empty (field values reached the heap).

**Don't use `GetProgramVariable` to check edit-mode wiring.** On the backing `UdonBehaviour` it reads the Udon
*runtime heap*, which only populates in Play mode, so it returns `null` for correctly-wired variables at edit
time — a false "unwired" alarm. To read a field's value without Play mode, read the **proxy**
`UdonSharpBehaviour`'s public field directly (what `get_object_details` does), or check
`serializedPublicVariablesBytesString` as above.

Watch for a **duplicate** backing UdonBehaviour from a half-finished attach (one wired, one orphan with a
null `programSource`) — a failed first attempt that left a proxy behind, then a second that succeeded. Drop
the unwired one. The definitive check is Play mode (ClientSim): the program only executes there.
