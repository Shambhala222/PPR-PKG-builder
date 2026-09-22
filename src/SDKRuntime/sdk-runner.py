"""macOS launcher for the SDK Fix 12 GP5 generator and publisher."""
import argparse, runpy, struct, termios, codecs, ctypes, errno, fcntl, json, os, pty, re, select, shutil, signal, subprocess, sys, time, uuid
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parent
ProgressDecoder=runpy.run_path(str(ROOT/'sdk-progress.py'))['ProgressDecoder']
ACTIVE=None
CANCELLED=False

def emit(kind, **values):
    print(json.dumps(dict(kind=kind,**values),ensure_ascii=False),flush=True)

def cancel(signum,frame):
    global CANCELLED
    CANCELLED=True
    if ACTIVE and ACTIVE.poll() is None:
        try: os.killpg(ACTIVE.pid,signal.SIGTERM)
        except ProcessLookupError: pass
        # Exit promptly even if a child ignores SIGTERM. The C# owner also has a timeout.
    else: raise InterruptedError('Cancelled')

signal.signal(signal.SIGTERM,cancel)
signal.signal(signal.SIGINT,cancel)

def win(path): return 'Z:'+str(Path(path).resolve()).replace('/','\\')
def inside(path,root):
    return path==root or root in path.parents

def run(command, env, logfile, stage, cwd=None, outputs=(), progress_text='Building PKG'):
    global ACTIVE
    if CANCELLED: raise InterruptedError('Cancelled')
    emit('stage',text=stage)
    master,slave=pty.openpty()
    fcntl.ioctl(slave,termios.TIOCSWINSZ,struct.pack('HHHH',40,240,0,0))
    parser=ProgressDecoder(emit, progress_text)
    sampled=time.monotonic(); previous_size=0
    cancelled_at=None
    decoder=codecs.getincrementaldecoder('utf-8')('replace')
    pending=''; last=0
    try:
        ACTIVE=subprocess.Popen([str(x) for x in command],stdin=subprocess.DEVNULL,stdout=slave,stderr=slave,env=env,cwd=cwd,start_new_session=True)
        os.close(slave);slave=-1
        with logfile.open('w',encoding='utf-8') as log:
            while True:
                now=time.monotonic()
                if outputs and now-sampled>=1:
                    size=sum(p.stat().st_size for p in outputs if p.exists())
                    emit('metrics',size=size,growth=max(0,size-previous_size)/(now-sampled))
                    sampled=now;previous_size=size
                if CANCELLED:
                    if cancelled_at is None: cancelled_at=time.monotonic()
                    if time.monotonic()-cancelled_at>3:
                        try: os.killpg(ACTIVE.pid,signal.SIGKILL)
                        except ProcessLookupError: pass
                        raise InterruptedError('Cancelled')
                if not select.select([master],[],[],0.25)[0]:
                    if ACTIVE.poll() is not None: break
                    continue
                try: data=os.read(master,16384)
                except OSError as e:
                    if e.errno==errno.EIO: break
                    raise
                if not data: break
                chunk=decoder.decode(data);log.write(chunk);log.flush()
                if 'Unhandled page fault' in pending+chunk or 'Unhandled exception' in pending+chunk:
                    raise RuntimeError('SDK publisher crashed. See '+str(logfile))
                pending=(pending+chunk)[-256:]
                parser.feed(chunk)
            parser.feed(decoder.decode(b'',final=True));parser.finish()
        code=ACTIVE.wait()
        ACTIVE=None
        if CANCELLED: raise InterruptedError('Cancelled')
        if code: raise RuntimeError(f'{stage} failed (exit {code}). Log: {logfile}')
    finally:
        if slave!=-1: os.close(slave)
        os.close(master)
        if ACTIVE and ACTIVE.poll() is None:
            try: os.killpg(ACTIVE.pid,signal.SIGKILL)
            except ProcessLookupError: pass
            ACTIVE.wait()
        ACTIVE=None

def rename_exclusive(source,destination):
    libc=ctypes.CDLL('/usr/lib/libSystem.B.dylib',use_errno=True)
    rename=libc.renamex_np
    rename.argtypes=[ctypes.c_char_p,ctypes.c_char_p,ctypes.c_uint];rename.restype=ctypes.c_int
    if rename(os.fsencode(source),os.fsencode(destination),4):
        code=ctypes.get_errno()
        if code not in {errno.ENOTSUP,errno.EOPNOTSUPP,errno.ENOSYS,errno.EINVAL}:
            raise OSError(code,os.strerror(code),str(destination))
        # exFAT does not implement RENAME_EXCL. Reserve a new destination first.
        fd=os.open(destination,os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600)
        reserved=os.fstat(fd)
        try:
            current=os.lstat(destination)
            if (current.st_dev,current.st_ino)!=(reserved.st_dev,reserved.st_ino):
                raise FileExistsError('Output changed during publication: '+str(destination))
            os.replace(source,destination)
        except Exception:
            try:
                current=os.lstat(destination)
                if (current.st_dev,current.st_ino)==(reserved.st_dev,reserved.st_ino):
                    os.unlink(destination)
            except FileNotFoundError: pass
            raise
        finally: os.close(fd)

def publish_outputs(pairs,force):
    backups=[];published=[]
    try:
        for staged,destination in pairs:
            if destination.exists():
                if not force: raise FileExistsError(f'Output already exists: {destination}')
                if not destination.is_file() or destination.is_symlink(): raise ValueError('Output is not a regular file.')
                backup=destination.with_name(destination.name+'.previous-'+uuid.uuid4().hex)
                rename_exclusive(destination,backup);backups.append((backup,destination))
        for staged,destination in pairs:
            rename_exclusive(staged,destination);published.append((staged,destination))
    except Exception:
        for staged,destination in reversed(published):
            rename_exclusive(destination,staged)
        for backup,destination in reversed(backups):
            rename_exclusive(backup,destination)
        raise
    for backup,destination in backups:
        try: backup.unlink()
        except OSError: emit('log',text=f'Old output retained at: {backup}')

def wine_setup():
    prefix=Path.home()/'Library/Application Support/fpkg-gui-0.8.2/WinePrefix'
    prefix.parent.mkdir(parents=True,exist_ok=True)
    lock=(prefix.parent/'sdk-build.lock').open('a')
    try: fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    except BlockingIOError: raise RuntimeError('Another SDKFix operation is already running.')
    env=os.environ.copy();env.update(WINEPREFIX=str(prefix),WINEDEBUG='-all',WINEDLLOVERRIDES='mscoree,mshtml,winemenubuilder.exe,winedbg.exe=d',MVK_CONFIG_LOG_LEVEL='0',PYTHONUNBUFFERED='1',PYTHONDONTWRITEBYTECODE='1')
    env.pop('PYTHONHOME',None);env.pop('PYTHONPATH',None)
    wine=ROOT/'wine/bin/wine'
    return prefix,lock,env,wine

def resolve_package(path):
    package=Path(path).expanduser().resolve()
    if not package.is_file() or package.suffix.lower()!='.pkg':
        raise ValueError('Select an existing .pkg file.')
    selected=package
    companion=Path(str(package)+'.remastered.pkg')
    if companion.is_file():
        emit('log',text='Using remastered companion for this operation: '+str(companion))
        package=companion
    return selected,package

def verify(args):
    selected,package=resolve_package(args.package)
    if len(args.passcode)!=32: raise ValueError('Passcode must contain exactly 32 characters.')
    integrity='off' if args.format_only else 'on'
    checks='format only' if args.format_only else 'format + integrity'
    logs=package.parent/(package.stem+'-verify-logs')/time.strftime('%Y%m%d-%H%M%S')
    logs.mkdir(parents=True,exist_ok=True)
    emit('log',text=f'Logs: {logs}')
    emit('log',text=f'Package: {package}')
    emit('log',text=f'Checks: {checks}')
    if package!=selected: emit('log',text=f'Patch input: {selected}')
    prefix,lock,env,wine=wine_setup()
    try:
        if not (prefix/'system.reg').exists():
            run([wine,'wineboot','-u'],env,logs/'00-wine-init.log','Preparing bundled Wine',progress_text='Preparing Wine')
        command=[wine,ROOT/'SDKFix12/toolchain/prospero-pub-cmd.exe','img_verify','--passcode',args.passcode,'--format_check','on','--integrity_check',integrity,'--no_progress_bar',win(package)]
        run(command,env,logs/'01-img-verify.log','Verifying PKG',cwd=ROOT/'SDKFix12/toolchain',progress_text='Verifying PKG')
        emit('done',text=str(package))
    finally:
        lock.close()

def extract(args):
    selected,package=resolve_package(args.package)
    if len(args.passcode)!=32: raise ValueError('Passcode must contain exactly 32 characters.')
    dest=Path(args.extract).expanduser().resolve()
    if inside(dest,ROOT): raise ValueError('Extraction folder must be outside the app.')
    if dest.exists():
        if not dest.is_dir(): raise ValueError('The extraction path exists but is not a folder.')
        if any(n.name not in {'.DS_Store'} and not n.name.startswith('._') for n in dest.iterdir()): raise ValueError('The extraction folder must be empty.')
    else:
        dest.mkdir(parents=True)
    logs=dest.parent/(dest.name+'-extract-logs')/time.strftime('%Y%m%d-%H%M%S')
    logs.mkdir(parents=True,exist_ok=True)
    emit('log',text=f'Logs: {logs}')
    emit('log',text=f'Package: {package}')
    emit('log',text=f'Destination: {dest}')
    if package!=selected: emit('log',text=f'Patch input: {selected}')
    prefix,lock,env,wine=wine_setup()
    try:
        if not (prefix/'system.reg').exists():
            run([wine,'wineboot','-u'],env,logs/'00-wine-init.log','Preparing bundled Wine',progress_text='Preparing Wine')
        command=[wine,ROOT/'SDKFix12/toolchain/prospero-pub-cmd.exe','img_extract','--passcode',args.passcode,'--no_progress_bar',win(package),win(dest)]
        run(command,env,logs/'01-img-extract.log','Extracting PKG',cwd=ROOT/'SDKFix12/toolchain',progress_text='Extracting PKG')
        emit('done',text=str(dest))
    finally:
        lock.close()

def build(args):
    source=Path(args.source).resolve();output=Path(args.output).resolve()
    if not source.is_dir(): raise ValueError('Choose an existing source folder.')
    key=source/'sce_sys/keystone'
    if not key.is_file() or key.stat().st_size!=96: raise ValueError('SDKFix12 requires sce_sys/keystone with exactly 96 bytes.')
    if not (source/'sce_sys/param.json').is_file(): raise ValueError('sce_sys/param.json is missing.')
    if len(args.passcode)!=32: raise ValueError('Passcode must contain exactly 32 characters.')
    if type(args.chunk_count) is not int or not 1<=args.chunk_count<=255:
        raise ValueError('PlayGo chunk count must be 1 through 255.')
    if output.suffix.lower()!='.pkg': raise ValueError('Choose an output ending in .pkg.')
    if inside(output,source) or inside(output,ROOT): raise ValueError('Output must be outside the source folder and app.')
    reference=Path(args.reference).resolve() if args.reference else None
    companion=Path(str(output)+'.remastered.pkg')
    if reference and (not reference.is_file() or reference in [output,companion]): raise ValueError('Choose an existing, separate reference PKG.')
    if not args.force and (output.exists() or (reference and companion.exists())): raise FileExistsError('Output already exists. Choose a new filename or enable Overwrite existing files (Force).')
    temporary=Path(args.temp).expanduser().resolve() if args.temp else output.parent
    if inside(temporary,source) or inside(temporary,ROOT): raise ValueError('Temporary folder must be outside the source folder and app.')
    output.parent.mkdir(parents=True,exist_ok=True);temporary.mkdir(parents=True,exist_ok=True)
    work=temporary/('fpkg-sdkfix12-'+uuid.uuid4().hex);work.mkdir()
    logs=output.parent/(output.stem+'-build-logs')/time.strftime('%Y%m%d-%H%M%S')
    logs.mkdir(parents=True,exist_ok=True)
    emit('log',text=f'Logs: {logs}')
    emit('log',text=f'Temporary workspace: {work}')
    prefix,lock,env,wine=wine_setup()
    python=ROOT/'python/bin/python3.12'
    partial=output.parent/(output.stem+'.sdkfix-'+uuid.uuid4().hex+'.partial.pkg')
    partial_companion=Path(str(partial)+'.remastered.pkg')
    success=False
    publishing=False
    try:
        if not (prefix/'system.reg').exists():
            run([wine,'wineboot','-u'],env,logs/'00-wine-init.log','Preparing bundled Wine')
        (work/'temp').mkdir()
        # Keep Windows temporary paths short even when the selected Mac folder is long.
        drive=prefix/'dosdevices/t:'
        if drive.is_symlink(): drive.unlink()
        elif drive.exists(): raise RuntimeError('Private Wine T: mapping is occupied.')
        drive.symlink_to(work/'temp',target_is_directory=True)
        env['TEMP']='T:\\';env['TMP']=env['TEMP'];env['TMPDIR']=str(work/'temp')
        for name in ['TEMP','TMP']:
            run([wine,'reg','add',r'HKCU\Environment','/v',name,'/t','REG_EXPAND_SZ','/d',env['TEMP'],'/f'],env,logs/('00-set-'+name+'.log'),'Preparing temporary folder')
        gp5=work/'project.gp5'
        command=[python,'-B',ROOT/'SDKFix12/scripts/create-gp5-from-folder.py',source,gp5,'--passcode',args.passcode,'--absolute-paths','--keep-keystone','--dds-converter',ROOT/'dds-wine.py','--chunk-count',str(args.chunk_count)]
        if not reference:
            command+=['--auto-size-profile','sdk279']
        run(command,env,logs/'01-create-gp5.log','1/2 · Preparing SDK Fix 12 project')
        tree=ET.parse(gp5)
        for node in tree.iter():
            path=node.get('src_path')
            if path and path.startswith('/'): node.set('src_path',win(path))
        tree.write(gp5,encoding='utf-8',xml_declaration=True)
        command=[wine,ROOT/'SDKFix12/toolchain/prospero-pub-cmd.exe','img_create','--oformat','nwonly','--compression_level',str(args.compression)]
        if reference: command+=['--ref_pkg_path',win(reference)]
        command += [win(gp5),win(partial)]
        run(command,env,logs/'02-img-create.log','2/2 · Building PKG',cwd=ROOT/'SDKFix12/toolchain',outputs=(partial,partial_companion),progress_text='Building PKG')
        if not partial.is_file() or not partial.stat().st_size: raise RuntimeError('Publisher produced no PKG.')
        if reference and not partial_companion.is_file(): raise RuntimeError('Publisher produced no remastered companion.')
        if CANCELLED: raise InterruptedError('Cancelled')
        pairs=[(partial,output)]
        if reference: pairs.append((partial_companion,companion))
        publishing=True
        publish_outputs(pairs,args.force)
        success=True
        emit('done',text=str(output),size=output.stat().st_size)
        if reference: emit('log',text=f'Remastered companion: {companion}')
    finally:
        # Clear only this app's private Wine prefix settings before deleting its workspace.
        for name in ['TEMP','TMP']:
            try: subprocess.run([str(wine),'reg','delete',r'HKCU\Environment','/v',name,'/f'],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=10)
            except Exception: pass
        drive=prefix/'dosdevices/t:'
        if drive.is_symlink() and drive.resolve()==(work/'temp').resolve(): drive.unlink()
        lock.close()
        if args.keep: emit('log',text=f'Workspace retained: {work}')
        else: shutil.rmtree(work,ignore_errors=True)
        for p in [partial,partial_companion,Path(str(partial)+'.naps_metric.json')]:
            if p.exists():
                if args.keep or publishing: emit('log',text=f'Intermediate retained: {p}')
                else: p.unlink()
        if not success: emit('log',text=f'Build did not complete. Source unchanged. Logs: {logs}')

if __name__ == '__main__':
    p=argparse.ArgumentParser()
    p.add_argument('--mode',choices=['build','verify','extract'],default='build')
    p.add_argument('--source',default='')
    p.add_argument('--output',default='')
    p.add_argument('--temp',default='')
    p.add_argument('--reference',default='')
    p.add_argument('--package',default='')
    p.add_argument('--extract',default='')
    p.add_argument('--compression',type=int,choices=range(-4,10),default=7)
    p.add_argument('--chunk-count',type=int,default=100)
    p.add_argument('--passcode',default='0'*32)
    p.add_argument('--keep',action='store_true')
    p.add_argument('--force',action='store_true')
    p.add_argument('--format-only',action='store_true')
    args=p.parse_args()
    try:
        if args.mode=='verify': verify(args)
        elif args.mode=='extract': extract(args)
        else: build(args)
    except InterruptedError: emit('error',text='Cancelled.');sys.exit(130)
    except Exception as e: emit('error',text=str(e));sys.exit(1)
