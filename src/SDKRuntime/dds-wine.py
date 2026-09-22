import os,subprocess,sys
from pathlib import Path
root=Path(__file__).resolve().parent
wine=root/'wine/bin/wine'
def w(p): return 'Z:'+str(Path(p).resolve()).replace('/','\\')
cmd=[str(wine),str(root/'SDKFix12/prospero-dds2png.exe'),w(sys.argv[1]),w(sys.argv[2]),*sys.argv[3:]]
raise SystemExit(subprocess.run(cmd).returncode)
