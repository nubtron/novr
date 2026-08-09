"""Which draws touched pixel (x,y) of the final colour target, and what they wrote."""
import sys, renderdoc as rd

CAP, X, Y = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
TARGET = sys.argv[4] if len(sys.argv) > 4 else "_CameraColorAttachmentB_1512x1680"

rd.InitialiseReplay(rd.GlobalEnvironment(), [])
cap = rd.OpenCaptureFile()
if cap.OpenFile(CAP, "", None) != rd.ResultCode.Succeeded:
    sys.exit("open failed")
st, ctrl = cap.OpenCapture(rd.ReplayOptions(), None)
res = {r.resourceId: r.name for r in ctrl.GetResources()}
texs = {t.resourceId: t for t in ctrl.GetTextures()}

target = None
for t in ctrl.GetTextures():
    if TARGET in (res.get(t.resourceId) or ""):
        target = t
        break
if target is None:
    sys.exit(f"no target matching {TARGET}")
print(f"target {res.get(target.resourceId)} {target.width}x{target.height}", flush=True)

def walk(a):
    for x in a:
        yield x
        yield from walk(x.children)

draws = [d for d in walk(ctrl.GetRootActions()) if d.flags & rd.ActionFlags.Drawcall]
ctrl.SetFrameEvent(draws[-1].eventId, True)

hist = ctrl.PixelHistory(target.resourceId, X, Y, rd.Subresource(0, 0, 0), rd.CompType.Typeless)
print(f"{len(hist)} modifications at ({X},{Y})\n", flush=True)
for m in hist:
    ctrl.SetFrameEvent(m.eventId, True)
    state = ctrl.GetPipelineState()
    srv = []
    for u in state.GetReadOnlyResources(rd.ShaderStage.Pixel):
        rid = u.descriptor.resource
        if rid in texs:
            srv.append(res.get(rid) or str(rid))
    pre, post = m.preMod.col, m.postMod.col
    print(f"eid {m.eventId:>6} shaderOut->  "
          f"pre=({pre.floatValue[0]:.3f},{pre.floatValue[1]:.3f},{pre.floatValue[2]:.3f}) "
          f"post=({post.floatValue[0]:.3f},{post.floatValue[1]:.3f},{post.floatValue[2]:.3f})  "
          f"tex={','.join(srv[:2])}", flush=True)

ctrl.Shutdown(); cap.Shutdown(); rd.ShutdownReplay()
