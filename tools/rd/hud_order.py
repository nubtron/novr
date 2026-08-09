"""Ordered timeline of the end of the frame: is the HUD drawn before or after post?"""
import sys, time
import renderdoc as rd

CAP = sys.argv[1]
TAIL = int(sys.argv[2]) if len(sys.argv) > 2 else 600

FAC = {int(getattr(rd.BlendMultiplier, n)): n for n in dir(rd.BlendMultiplier)
       if not n.startswith('_') and isinstance(getattr(rd.BlendMultiplier, n), rd.BlendMultiplier)}
OP = {int(getattr(rd.BlendOperation, n)): n for n in dir(rd.BlendOperation)
      if not n.startswith('_') and isinstance(getattr(rd.BlendOperation, n), rd.BlendOperation)}
NAMED = {("Add","One","One"):"ADDITIVE", ("Add","SrcAlpha","InvSrcAlpha"):"ALPHA",
         ("Add","One","InvSrcAlpha"):"PREMUL", ("Add","SrcAlpha","One"):"SOFT-ADD"}

def bdesc(b):
    if not b.enabled: return "OFF"
    k = (OP.get(int(b.colorBlend.operation)), FAC.get(int(b.colorBlend.source)),
         FAC.get(int(b.colorBlend.destination)))
    return NAMED.get(k, f"{k[0]}({k[1]},{k[2]})")

rd.InitialiseReplay(rd.GlobalEnvironment(), [])
cap = rd.OpenCaptureFile()
cap.OpenFile(CAP, "", None)
st, ctrl = cap.OpenCapture(rd.ReplayOptions(), None)

def walk(a):
    for x in a:
        yield x
        yield from walk(x.children)

draws = [a for a in walk(ctrl.GetRootActions()) if a.flags & rd.ActionFlags.Drawcall]
res = {r.resourceId: r.name for r in ctrl.GetResources()}
texs = {t.resourceId: t for t in ctrl.GetTextures()}
print(f"draws {len(draws)}; timeline of last {TAIL}", flush=True)

for a in draws[-TAIL:]:
    ctrl.SetFrameEvent(a.eventId, True)
    s = ctrl.GetPipelineState()
    bl = s.GetColorBlends()
    blend = bdesc(bl[0]) if len(bl) else "n/a"
    outs = s.GetOutputTargets()
    rt = ""
    if len(outs) and outs[0].resource != rd.ResourceId.Null():
        rt = (res.get(outs[0].resource) or str(outs[0].resource))[:52]
    srv = []
    for u in s.GetReadOnlyResources(rd.ShaderStage.Pixel):
        rid = u.descriptor.resource
        if rid in texs:
            srv.append((res.get(rid) or str(rid))[:34])
    # A fullscreen blit is the signature of a post-processing pass.
    tag = "BLIT" if a.numIndices <= 6 else "    "
    print(f"{a.eventId:>7} {tag} idx={a.numIndices:<6} {blend:<10} RT={rt:<52} SRV={','.join(srv[:3])}", flush=True)

ctrl.Shutdown(); cap.Shutdown(); rd.ShutdownReplay()
