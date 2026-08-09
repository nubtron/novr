"""Per-draw blend state + bound textures, for the whole frame.

NOVRFlightHudBehavior decides per texture whether to swap the game's additive
HUD material for an alpha-blended one, using a CPU-side heuristic (texture
format + a sampled grid of pixels, with "not readable" falling back to
alpha-safe). This reads what the GPU actually did instead.
"""
import sys, time
import renderdoc as rd

CAP = sys.argv[1]

BLEND_FACTOR = {}
for n in dir(rd.BlendMultiplier):
    if not n.startswith("_"):
        try:
            BLEND_FACTOR[int(getattr(rd.BlendMultiplier, n))] = n
        except (TypeError, ValueError):
            pass
BLEND_OP = {}
for n in dir(rd.BlendOperation):
    if not n.startswith("_"):
        try:
            BLEND_OP[int(getattr(rd.BlendOperation, n))] = n
        except (TypeError, ValueError):
            pass

NAMED = {
    ("Add", "One", "One"): "ADDITIVE",
    ("Add", "SrcAlpha", "InvSrcAlpha"): "ALPHA",
    ("Add", "One", "InvSrcAlpha"): "PREMUL-ALPHA",
    ("Add", "SrcAlpha", "One"): "SOFT-ADDITIVE",
    ("Add", "SrcAlpha", "OneMinusSrcAlpha"): "ALPHA",
}


def blend_desc(b):
    if not b.enabled:
        return "OFF"
    op = BLEND_OP.get(int(b.colorBlend.operation), str(b.colorBlend.operation))
    src = BLEND_FACTOR.get(int(b.colorBlend.source), str(b.colorBlend.source))
    dst = BLEND_FACTOR.get(int(b.colorBlend.destination), str(b.colorBlend.destination))
    return NAMED.get((op, src, dst), f"{op}({src},{dst})")


rd.InitialiseReplay(rd.GlobalEnvironment(), [])
cap = rd.OpenCaptureFile()
if cap.OpenFile(CAP, "", None) != rd.ResultCode.Succeeded:
    sys.exit("open failed")
status, ctrl = cap.OpenCapture(rd.ReplayOptions(), None)
if status != rd.ResultCode.Succeeded:
    sys.exit(f"replay failed: {status}")


def walk(acts):
    for a in acts:
        yield a
        yield from walk(a.children)


draws = [a for a in walk(ctrl.GetRootActions()) if a.flags & rd.ActionFlags.Drawcall]
res_name = {r.resourceId: r.name for r in ctrl.GetResources()}
textures = {t.resourceId: t for t in ctrl.GetTextures()}
print(f"draws in frame: {len(draws)}", flush=True)

rows = []
t0 = time.time()
for i, action in enumerate(draws):
    ctrl.SetFrameEvent(action.eventId, True)
    state = ctrl.GetPipelineState()
    blends = state.GetColorBlends()
    blend = blend_desc(blends[0]) if len(blends) else "n/a"
    if blend == "OFF":
        continue

    bound = []
    for used in state.GetReadOnlyResources(rd.ShaderStage.Pixel):
        rid = used.descriptor.resource
        tex = textures.get(rid)
        if tex is None:
            continue
        name = res_name.get(rid) or str(rid)
        bound.append(f"{name} {tex.width}x{tex.height} {tex.format.Name()}")

    target = ""
    outs = state.GetOutputTargets()
    if len(outs) and outs[0].resource != rd.ResourceId.Null():
        t = textures.get(outs[0].resource)
        target = f"{res_name.get(outs[0].resource) or outs[0].resource}"
        if t:
            target += f" {t.width}x{t.height}"

    rows.append((action.eventId, blend, target, bound))
    if i % 2000 == 0:
        print(f"  ... {i}/{len(draws)} ({time.time()-t0:.0f}s)", flush=True)

ctrl.Shutdown()
cap.Shutdown()
rd.ShutdownReplay()

print(f"\nblended draws: {len(rows)} of {len(draws)}")
counts = {}
for _, b, _, _ in rows:
    counts[b] = counts.get(b, 0) + 1
print("\n== blend modes ==")
for b, n in sorted(counts.items(), key=lambda kv: -kv[1]):
    print(f"{n:>6}  {b}")

print("\n== each texture, and the blend modes it was drawn with ==")
per_tex = {}
for _, b, _, bound in rows:
    for t in bound:
        per_tex.setdefault(t, {})
        per_tex[t][b] = per_tex[t].get(b, 0) + 1
for t, modes in sorted(per_tex.items(), key=lambda kv: -sum(kv[1].values())):
    summary = "  ".join(f"{m}x{n}" for m, n in sorted(modes.items(), key=lambda kv: -kv[1]))
    print(f"  {t:<58} {summary}")

print("\n== render targets of blended draws ==")
per_rt = {}
for _, b, target, _ in rows:
    per_rt.setdefault(target, {})
    per_rt[target][b] = per_rt[target].get(b, 0) + 1
for t, modes in sorted(per_rt.items(), key=lambda kv: -sum(kv[1].values())):
    summary = "  ".join(f"{m}x{n}" for m, n in sorted(modes.items(), key=lambda kv: -kv[1]))
    print(f"  {t:<42} {summary}")
