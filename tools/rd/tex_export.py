"""Export textures matching a name from a capture, as PNG (decompressed)."""
import sys, os, renderdoc as rd

CAP, OUT = sys.argv[1], sys.argv[2]
NAMES = [n.lower() for n in sys.argv[3:]]

rd.InitialiseReplay(rd.GlobalEnvironment(), [])
cap = rd.OpenCaptureFile()
if cap.OpenFile(CAP, "", None) != rd.ResultCode.Succeeded:
    sys.exit("open failed")
st, ctrl = cap.OpenCapture(rd.ReplayOptions(), None)
res = {r.resourceId: r.name for r in ctrl.GetResources()}

os.makedirs(OUT, exist_ok=True)
for tex in ctrl.GetTextures():
    name = res.get(tex.resourceId, "")
    if not name or not any(n in name.lower() for n in NAMES):
        continue
    save = rd.TextureSave()
    save.resourceId = tex.resourceId
    save.destType = rd.FileType.PNG
    save.mip = 0
    save.slice.sliceIndex = 0
    path = os.path.join(OUT, f"{name}.png".replace("/", "_"))
    ok = ctrl.SaveTexture(save, path)
    print(f"{'ok ' if ok else 'FAIL'} {name} {tex.width}x{tex.height} {tex.format.Name()} -> {path}", flush=True)

ctrl.Shutdown(); cap.Shutdown(); rd.ShutdownReplay()
