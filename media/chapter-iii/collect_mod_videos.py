import json, pathlib, re, urllib.request, urllib.parse, subprocess, hashlib, shutil, html
root=pathlib.Path("mod-video-reference-pack")
root.mkdir(exist_ok=True)
report={"notice":"Public creator-hosted viewing references only; reuse rights not verified. No access-control bypass. Only Viking Age Primitive Plus allowlist: Primitive Plus, Human NPCs, Dark Age 1/2, planned CKF/ASR. No other cluster mods. No full third-party video in git.","sources":[],"clips":[]}
def get(url):
    req=urllib.request.Request(url,headers={"User-Agent":"ChapterIII-media-reference/1.0"})
    with urllib.request.urlopen(req,timeout=20) as r:
        return r.read(8_000_000)
def api(method,params):
    return json.loads(get("https://public.api.bsky.app/xrpc/"+method+"?"+urllib.parse.urlencode(params)))
def record(src,**kwargs):
    report["sources"].append({"source":src,**kwargs})
def probe(path):
    return json.loads(subprocess.check_output(["ffprobe","-v","error","-show_streams","-show_format","-of","json",str(path)],timeout=30))
seen=set()
budget=50_000_000
def sample(url,source,name):
    global budget
    if url in seen or budget<1_000_000 or len(report["clips"])>=8:return
    seen.add(url)
    if not shutil.which("ffmpeg"):
        record(source,status="ffmpeg missing");return
    # Only ordinary HTTPS URLs on creator-linked public media CDN, never authenticated media.
    if urllib.parse.urlparse(url).scheme!="https":return
    p=root/(name+".mp4")
    try:
        subprocess.run(["ffmpeg","-nostdin","-v","error","-y","-rw_timeout","15000000","-i",url,"-t","12","-map","0:v:0","-an","-c:v","libx264","-preset","fast","-crf","20","-pix_fmt","yuv420p","-movflags","+faststart","-fs",str(min(budget,12_000_000)),str(p)],check=True,timeout=55)
        meta=probe(p)
        v=next(x for x in meta["streams"] if x["codec_type"]=="video")
        dur=float(meta["format"]["duration"])
        if dur<2 or v["height"]<540:raise ValueError("short/low resolution")
        size=p.stat().st_size
        budget-=size
        report["clips"].append({"file":p.name,"source":source,"media":url,"width":v["width"],"height":v["height"],"fps":v["avg_frame_rate"],"seconds":dur,"bytes":size,"sha256":hashlib.sha256(p.read_bytes()).hexdigest(),"reuse_permission":"not verified; reference only"})
    except Exception as e:
        p.unlink(missing_ok=True)
        record(source,status="sample unavailable",error=str(e)[:400])
def find_video(embed):
    if not isinstance(embed,dict):return None
    if embed.get("$type")=="app.bsky.embed.video#view":return embed.get("playlist")
    if embed.get("$type")=="app.bsky.embed.recordWithMedia#view":return find_video(embed.get("media"))
    return None
# Scan the official mod pages for explicitly linked direct MP4 or WebM files.
for slug in ["human_npcs_asa","fantasy-weapons-dark-age","dark-age-2-weapons","ckf-castles-keeps-forts","arkitect-structures-remastered","primplus"]:
    src="https://www.curseforge.com/ark-survival-ascended/mods/"+slug
    try:
        page=html.unescape(get(src).decode("utf-8","replace")).replace("\\/","/")
        links=list(dict.fromkeys(re.findall(r'https://[^\s"\'<>\\]+?\.(?:mp4|webm)(?:\?[^\s"\'<>\\]*)?',page,re.I)))
        # Restrict to CurseForge's own public media CDN.
        links=[u for u in links if urllib.parse.urlparse(u).hostname and urllib.parse.urlparse(u).hostname.endswith(".forgecdn.net")]
        record(src,status="scanned",direct_video_links=links[:10])
        for j,u in enumerate(links[:2]):sample(u,src,slug+"_"+str(j+1))
    except Exception as e:record(src,status="unavailable",error=str(e))
(root/"REPORT.json").write_text(json.dumps(report,indent=2,ensure_ascii=False))
print(json.dumps(report,indent=2,ensure_ascii=False))
if not report["clips"]:print("NO VIDEO FILES ACQUIRED; report only.")
