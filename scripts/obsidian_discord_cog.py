"""Optional discord.py extension for an EXISTING bot, not a new bot.

Attach explicitly from the existing bot startup. No login/token, automatic
slash-command sync, or modification to unrelated channels/roles.
"""
import asyncio
import json
import os
import uuid
from datetime import datetime, timezone
from pathlib import Path
from dataclasses import dataclass

import discord
from discord.ext import commands
from obsidian_discord_client import Host, async_call

CHANNELS = {
    "status": "szerver-statusz", "control": "szerver-vezerles",
    "alerts": "szerver-riasztasok", "audit": "admin-naplo",
    "backup": "mentesek", "updates": "frissitesek",
}
ACTIONS = {
    "start": ("Start (mentett mód)", None, discord.ButtonStyle.success),
    "start-api": ("Start ASA API-val", True, discord.ButtonStyle.success),
    "start-direct": ("Start API nélkül", False, discord.ButtonStyle.success),
    "stop": ("Stop", None, discord.ButtonStyle.danger),
    "restart": ("Restart", None, discord.ButtonStyle.primary),
    "restart-api": ("Restart ASA API-val", True, discord.ButtonStyle.primary),
    "restart-direct": ("Restart API nélkül", False, discord.ButtonStyle.primary),
    "save": ("SaveWorld", None, discord.ButtonStyle.secondary),
    "wipe": ("Dino Wipe", None, discord.ButtonStyle.danger),
    "backup": ("Backup", None, discord.ButtonStyle.secondary),
    "update": ("SteamCMD Update", None, discord.ButtonStyle.primary),
    "players": ("Játékoslista", None, discord.ButtonStyle.secondary),
    "rcon": ("RCON", None, discord.ButtonStyle.secondary),
    "restore": ("Restore", None, discord.ButtonStyle.danger),
    "backupHistory": ("Mentési pontok", None, discord.ButtonStyle.secondary),
}
NONE = discord.AllowedMentions.none()

def clean(value, limit=200):
    return discord.utils.escape_markdown(str(value).replace("@", "＠").replace("\x00", ""))[:limit]

def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temp.open("x", encoding="utf-8") as f:
            json.dump(value, f, ensure_ascii=False, indent=2)
            f.flush()
            os.fsync(f.fileno())
        os.replace(temp, path)
    finally:
        temp.unlink(missing_ok=True)

@dataclass(frozen=True)
class Identity:
    user_id: int
    channel_id: int
    role_ids: tuple

class ObsidianControl(commands.Cog):
    def __init__(self, bot, config):
        self.bot = bot
        self.guild_id = int(config["guild_id"])
        self.roles = frozenset(int(x) for x in config["role_ids"])
        if not self.roles or self.guild_id in self.roles:
            raise ValueError("Explicit owner/admin roles required; never @everyone")
        self.state_file = Path(config["state_file"])
        self.provision_only = bool(config.get("provision_only", False))
        self.hosts = {} if self.provision_only else {k: Host(**v) for k, v in config["hosts"].items()}
        if not self.provision_only and not 1 <= len(self.hosts) <= 25:
            raise ValueError("Configure 1–25 exact private host endpoints")
        if any(not 1 <= len(k) <= 50 for k in self.hosts):
            raise ValueError("Host labels must be 1–50 characters")
        self.provision = bool(config.get("provision_channels", False))
        self.state = json.loads(self.state_file.read_text(encoding="utf-8")) if self.state_file.exists() else {}
        if self.state and self.state.get("guild_id") != self.guild_id:
            raise ValueError("Channel state belongs to another guild")
        self.state.setdefault("guild_id", self.guild_id)
        self.state.setdefault("channels", {})
        self.state.setdefault("messages", {})
        self.state.setdefault("operations", {})
        self.guard = asyncio.Lock()
        self.host_locks = {k: asyncio.Lock() for k in self.hosts}
        self.task = None
        self.panel = ControlPanel(self)
        self.last_states = {}

    def persist(self):
        atomic_json(self.state_file, self.state)

    async def cog_load(self):
        if not self.provision_only:
            self.bot.add_view(self.panel)
        self.task = asyncio.create_task(self.worker())

    def cog_unload(self):
        if self.task:
            self.task.cancel()
        self.panel.stop()

    async def guild_and_roles(self):
        guild = self.bot.get_guild(self.guild_id)
        if guild is None or self.bot.user is None:
            raise PermissionError("Configured Discord guild unavailable")
        roles = {r.id: r for r in await guild.fetch_roles()}
        if not self.roles <= roles.keys() or guild.id in self.roles:
            raise PermissionError("Configured admin roles changed")
        member = await guild.fetch_member(self.bot.user.id)
        return guild, roles, member

    async def private_channel(self, kind):
        """Fresh channel/role permission proof, never trust a name match."""
        guild, roles, member = await self.guild_and_roles()
        channel_id = int(self.state["channels"][kind])
        channel = await self.bot.fetch_channel(channel_id)
        if not isinstance(channel, discord.TextChannel) or channel.guild.id != guild.id:
            raise PermissionError("Not the configured private text channel")
        if channel.category_id != self.state.get("category_id"):
            raise PermissionError("Channel moved out of protected category")
        # Only explicit allow overwrites for designated roles and the bot.
        # Reject any additional member or role with explicit view allow,
        # including a copied or externally added channel overwrite.
        everyone = channel.overwrites_for(guild.default_role)
        if everyone.view_channel is not False:
            raise PermissionError("@everyone must explicitly be denied")
        for subject, overwrite in channel.overwrites.items():
            if overwrite.view_channel is True and subject.id not in self.roles and subject.id != member.id:
                raise PermissionError("Unexpected channel view permission")
        for rid in self.roles:
            if channel.overwrites_for(roles[rid]).view_channel is not True:
                raise PermissionError("Admin role visibility cannot be verified")
        if channel.overwrites_for(member).view_channel is not True:
            raise PermissionError("Bot visibility cannot be verified")
        # Discord guild owner and Administrator permission bypass overwrites;
        # no channel permission scheme can exclude these principals.
        return channel

    async def provision_once(self):
        guild, roles, member = await self.guild_and_roles()
        if not self.state.get("category_id"):
            if not self.provision:
                raise PermissionError("Local provisioning not enabled")
            overwrites = {guild.default_role: discord.PermissionOverwrite(view_channel=False),
                member: discord.PermissionOverwrite(view_channel=True, send_messages=True, read_message_history=True)}
            for role_id in self.roles:
                overwrites[roles[role_id]] = discord.PermissionOverwrite(view_channel=True, send_messages=True, read_message_history=True)
            category = await guild.create_category("OBSIDIAN ADMIN", overwrites=overwrites,reason="Explicit local Obsidian provisioning")
            self.state["category_id"] = category.id
            self.persist()
        else:
            category = await self.bot.fetch_channel(int(self.state["category_id"]))
            if not isinstance(category, discord.CategoryChannel) or category.guild.id != guild.id:
                raise PermissionError("Configured category invalid")
        for kind, name in CHANNELS.items():
            if kind not in self.state["channels"]:
                if not self.provision:
                    raise PermissionError("Missing provisioned channel")
                overwrites = {guild.default_role: discord.PermissionOverwrite(view_channel=False),
                    member: discord.PermissionOverwrite(view_channel=True,send_messages=True,read_message_history=True)}
                for rid in self.roles:
                    overwrites[roles[rid]] = discord.PermissionOverwrite(view_channel=True,send_messages=True,read_message_history=True)
                ch = await guild.create_text_channel(name, category=category, overwrites=overwrites,reason="Explicit local Obsidian provisioning")
                self.state["channels"][kind] = ch.id
                self.persist()
            await self.private_channel(kind)

    async def identity(self, interaction, initiator=None):
        if interaction.guild_id != self.guild_id or interaction.channel_id != self.state["channels"].get("control"):
            raise PermissionError("Only the configured control channel")
        if initiator is not None and interaction.user.id != initiator:
            raise PermissionError("Only the original actor may confirm")
        await self.private_channel("control")
        guild, _, _ = await self.guild_and_roles()
        member = await guild.fetch_member(interaction.user.id)
        if member.bot or not {r.id for r in member.roles} & self.roles:
            raise PermissionError("Owner/Admin role required")
        return Identity(member.id, interaction.channel_id,tuple(r.id for r in member.roles))

    async def service(self):
        ch = await self.private_channel("status")
        _, _, member = await self.guild_and_roles()
        return Identity(member.id,ch.id,())

    async def call(self, label, who, kind, *, interaction=None, **data):
        async with self.host_locks[label]:
            # A request may have waited behind a long backup/update. Re-fetch
            # actual permissions once the lock is acquired, not before waiting.
            if kind in ("preview", "apply"):
                if interaction is None:
                    raise PermissionError("Human interaction required for mutation")
                who = await self.identity(interaction, who.user_id)
            result = await async_call(self.hosts[label],guild_id=self.guild_id,channel_id=who.channel_id,
                user_id=who.user_id,role_ids=who.role_ids,kind=kind,**data)
        if not result["ok"]:
            raise RuntimeError(clean(result.get("error","API rejected"),100))
        return result["data"]

    async def say(self, kind, text):
        try:
            ch = await self.private_channel(kind)
            await ch.send(content=clean(text,1900),allowed_mentions=NONE)
        except Exception:
            # Never fall back to a public or unverified channel.
            pass

    async def replace_message(self, kind, content=None, embed=None, view=None):
        ch = await self.private_channel(kind)
        existing = self.state["messages"].get(kind)
        args = dict(content=content,embed=embed,view=view,allowed_mentions=NONE)
        if existing:
            try:
                m = await ch.fetch_message(existing)
                if m.author.id != self.bot.user.id:
                    raise PermissionError("Not our status/control message")
                await m.edit(**args)
                return
            except discord.NotFound:
                pass
        m = await ch.send(**args)
        self.state["messages"][kind] = m.id
        self.persist()

    async def one_status(self, label, service):
        try:
            result = await self.call(label,service,"status",timeout=15)
            for m in result["maps"]:
                if m["running"]:
                    # Loopback native readiness checks happen on each host.
                    # No concurrent per-host RCON flood.
                    try:
                        await self.call(label,service,"players",mapId=m["mapId"],timeout=15)
                    except Exception:
                        pass
                    await asyncio.sleep(.4)
            return await self.call(label,service,"status",timeout=15)
        except Exception:
            return None

    async def refresh_status(self):
        service = await self.service()
        data = await asyncio.gather(*(self.one_status(k,service) for k in self.hosts))
        embed = discord.Embed(title="Untamed Kingdom · Obsidian Status",colour=0x3584E4,
            timestamp=datetime.now(timezone.utc))
        online=known_players=unknown_players=total=unreachable=0
        for label, status in zip(self.hosts,data):
            if status is None:
                unreachable+=1
                embed.add_field(name=clean(label,100),value="Nem elérhető · állapot ismeretlen",inline=False)
                continue
            lines=[]
            for m in status["maps"]:
                total+=1
                running=m["running"]
                online+=int(running)
                count=m.get("playerCount")
                if running:
                    if isinstance(count,int):
                        known_players+=count
                    else:
                        unknown_players+=1
                state="🟢" if running else "🔴" if m["confirmedStopped"] else "⚪"
                name=clean(m["mapName"],65)
                ram=m.get("memoryMiB")
                cpu=m.get("cpuPercent")
                free=m.get("freeBytes")
                up=m.get("uptimeSeconds")
                mode="API" if m["useApi"] else "ASA"
                lines.append(f'{state} {name} · {count if count is not None else "?"} fő · {mode}'
                    f' · RAM {round(ram/1024,1) if isinstance(ram,(int,float)) else "?"} GiB'
                    f' · CPU {round(cpu,1) if isinstance(cpu,(int,float)) else "?"}%'
                    f' · uptime {round(up/3600,1) if isinstance(up,(int,float)) else "?"}h'
                    f' · szabad {round(free/1073741824) if isinstance(free,int) else "?"} GiB')
                key=(label,m["mapId"])
                new=(running,m["confirmedStopped"])
                old=self.last_states.get(key)
                self.last_states[key]=new
                if old and old!=new:
                    await self.say("alerts",f'{label}: {m["mapName"]} · állapotváltozás: {state}')
            value="\n".join(lines) or "Nincs helyi map"
            # Discord field / total embed limits; truncate only display,
            # never discovery or control target IDs.
            if len(embed.fields)<20 and len(embed)+len(value[:1000])<5400:
                embed.add_field(name=clean(label,100),value=value[:1000],inline=False)
        embed.description=f'{online}/{total} ismert map fut · {known_players} igazolt játékos'
        if unknown_players or unreachable:
            embed.description+=f' · {unknown_players} futó map játékosszáma ismeretlen · {unreachable} host nem elérhető'
        embed.set_footer(text="Automatikus frissítés · ismeretlen ≠ nulla; részletek a vezérlőpulton")
        await self.replace_message("status",embed=embed)

    async def worker(self):
        await self.bot.wait_until_ready()
        # Transient Discord HTTP/cache failures at startup must not leave
        # the integration permanently offline. Retry only provisioning and
        # the private panel, never a previously submitted native operation.
        while True:
            try:
                await self.provision_once()
                if self.provision_only:
                    return
                await self.replace_message("control",content="Obsidian Command · Tulajdonos és Admin egyenlő joggal. Válassz műveletet, hostot, mapot; minden írás külön megerősítést igényel.",view=self.panel)
                break
            except asyncio.CancelledError:
                raise
            except Exception:
                # Every attempt repeats the private-channel/role proof.
                # No fallback channel, raw exception, or mutation replay.
                await asyncio.sleep(30)
        while True:
            try:
                await self.refresh_status()
                await self.poll_jobs()
            except asyncio.CancelledError:
                raise
            except Exception:
                pass
            await asyncio.sleep(30)

    async def poll_jobs(self):
        # Service principal may query existing receipts, never dispatch.
        who=await self.service()
        pending=[(i,e) for i,e in self.state["operations"].items()
            if e.get("state") in ("native-receipt","claimed-no-replay","uncertain-no-replay","running")
            and e.get("host") in self.hosts]
        for operation,entry in pending[:8]:
            try:
                result=await self.call(entry["host"],who,"job",operationId=operation,timeout=15)
                job=result.get("job")
                if job:
                    phase=job.get("state","unknown")
                    terminal=phase in ("completed","needsOperator","needsReview","reviewed","notDispatched","operatorReviewed")
                    new="completed-native" if phase=="completed" else "needs-review" if terminal else "running"
                else:
                    phase=result.get("state","unknown")
                    new="receipt-only" if phase=="native-receipt" else "needs-review"
                if new!=entry["state"]:
                    async with self.guard:
                        self.state["operations"][operation]["state"]=new
                        self.persist()
                    if new!="running":
                        await self.say("audit",f'{entry["host"]} · {entry["action"]} · {operation} · {new} / {phase}')
                        if entry["action"] in ("backup","restore","update"):
                            await self.say("updates" if entry["action"]=="update" else "backup",
                                f'{entry["host"]} · {entry["action"]} · {operation} · {new} / {phase}')
            except Exception:
                continue

    async def begin(self, interaction, action):
        await interaction.response.defer(ephemeral=True,thinking=True)
        try:
            who=await self.identity(interaction)
            await interaction.followup.send("Válassz helyi hostot. A két host külön művelet és külön jóváhagyás.",view=HostPicker(self,who.user_id,action),ephemeral=True,allowed_mentions=NONE)
        except Exception:
            await interaction.followup.send("Hozzáférés megtagadva.",ephemeral=True)

    async def host_selected(self, interaction, actor, action, label):
        await interaction.response.defer(ephemeral=True,thinking=True)
        try:
            who=await self.identity(interaction,actor)
            data=await self.call(label,who,"status",timeout=15)
            maps=data["maps"]
            if not maps:
                raise ValueError("No local maps")
            await interaction.followup.send("Válassz mapot vagy (Start/Stop/Restart) több helyi mapot.",view=MapPicker(self,actor,action,label,maps),ephemeral=True,allowed_mentions=NONE)
        except Exception as e:
            await interaction.followup.send("Host vagy jogosultság nem ellenőrizhető.",ephemeral=True)

    async def select_maps(self, interaction, actor, action, label, ids):
        await interaction.response.defer(ephemeral=True,thinking=True)
        try:
            who=await self.identity(interaction,actor)
            if action in ("players","backupHistory"):
                if len(ids)!=1:
                    raise ValueError("One map required")
                result=await self.call(label,who,action,mapId=ids[0],timeout=25)
                if action=="players":
                    content=f'Játékosszám: {result.get("playerCount") if result.get("playerCount") is not None else "ismeretlen"}\n{result.get("response","")}'
                else:
                    content="\n".join(f'{x.get("id")} · {x.get("createdUtc")} · {x.get("bytes")} byte' for x in result.get("points",[])[:20]) or "Nincs elérhető mentési pont"
                await interaction.followup.send(clean(content,1850),ephemeral=True,allowed_mentions=NONE)
                return
            if action in ("rcon","restore"):
                # Modals must be opened as initial response, therefore use
                # a separate explicit button in the following ephemeral view.
                await interaction.followup.send("Add meg a művelet paraméterét.",view=ParameterButton(self,actor,action,label,ids),ephemeral=True)
                return
            await self.show_preview(interaction,who,action,label,ids)
        except Exception:
            await interaction.followup.send("Művelet előnézete nem igazolható.",ephemeral=True)

    async def show_preview(self, interaction, who, action, label, ids, parameter=""):
        mode=ACTIONS[action][1]
        base=action.split("-")[0]
        fields=dict(action=base,mapIds=ids)
        if mode is not None:
            fields["useApi"]=mode
        if base=="rcon":
            fields["message"]=parameter
        if base=="restore":
            fields["point"]=parameter
        plan=await self.call(label,who,"preview",interaction=interaction,timeout=60,**fields)
        if plan["action"]!=base or {m["mapId"] for m in plan["maps"]}!=set(ids):
            raise ValueError("Native approval scope mismatch")
        names=", ".join(m["mapName"] for m in plan["maps"])
        summary=f'{label} · {ACTIONS[action][0]} · {names}'
        if parameter:
            summary+=f" · {parameter}"
        await interaction.followup.send("Jóváhagyás (90 mp): "+clean(summary,1500),view=Confirm(self,who.user_id,label,plan,summary),ephemeral=True,allowed_mentions=NONE)

    async def apply(self, interaction, actor, label, plan, summary):
        await interaction.response.defer(ephemeral=True,thinking=True)
        try:
            who=await self.identity(interaction,actor)
        except Exception:
            await interaction.followup.send("Jogosultság megtagadva.",ephemeral=True)
            return
        operation=str(uuid.uuid4())
        async with self.guard:
            self.state["operations"][operation]={"state":"claimed-no-replay","host":label,"actor":actor,
                "action":plan["action"],"createdUtc":datetime.now(timezone.utc).isoformat()}
            try:
                self.persist()
            except Exception:
                await interaction.followup.send("A helyi műveleti napló nem írható; nem küldtem parancsot.",ephemeral=True)
                return
        try:
            result=await self.call(label,who,"apply",interaction=interaction,token=plan["token"],confirm=True,request_id=operation,timeout=180)
            state="native-receipt"
            detail=clean(json.dumps(result.get("receipt",{}),ensure_ascii=False),1000)
        except Exception:
            state="uncertain-no-replay"
            detail="Az eredmény bizonytalan; ne ismételd automatikusan. Ellenőrizd a naplót."
        async with self.guard:
            self.state["operations"][operation]["state"]=state
            try:self.persist()
            except Exception:state="uncertain-no-replay"
        await self.say("audit",f'{actor} · {plan["action"]} · {label} · {operation} · {state}')
        if plan["action"] in ("backup","restore","update"):
            await self.say("updates" if plan["action"]=="update" else "backup",f'{label} · {plan["action"]} · {operation} · {state}')
        await interaction.followup.send(f'{state} · {operation}\n{detail}\nA natív nyugta nem igazolt játékbeli eredmény.',view=JobView(self,actor,label,operation),ephemeral=True,allowed_mentions=NONE)

class ControlPanel(discord.ui.View):
    def __init__(self,cog):
        super().__init__(timeout=None)
        for i,(action,(label,_,style)) in enumerate(ACTIONS.items()):
            button=discord.ui.Button(label=label,style=style,custom_id="obsidian:v1:"+action,row=i//5)
            async def callback(interaction,a=action):
                await cog.begin(interaction,a)
            button.callback=callback
            self.add_item(button)
        job=discord.ui.Button(label="Műveleti állapot",style=discord.ButtonStyle.secondary,custom_id="obsidian:v1:job",row=3)
        async def check(interaction):
            try:
                who=await cog.identity(interaction)
                await interaction.response.send_modal(JobModal(cog,who.user_id))
            except Exception:
                if not interaction.response.is_done():await interaction.response.send_message("Hozzáférés megtagadva.",ephemeral=True)
        job.callback=check
        self.add_item(job)

class HostPicker(discord.ui.View):
    def __init__(self,cog,actor,action):
        super().__init__(timeout=120)
        select=discord.ui.Select(placeholder="Host",options=[discord.SelectOption(label=k,value=k) for k in cog.hosts])
        async def pick(interaction):
            await cog.host_selected(interaction,actor,action,select.values[0])
        select.callback=pick
        self.add_item(select)

class MapPicker(discord.ui.View):
    def __init__(self,cog,actor,action,label,maps,page=0):
        super().__init__(timeout=120)
        batch=action in ("start","stop","restart")
        chunk=maps[page*25:(page+1)*25]
        select=discord.ui.Select(placeholder=f"Mapok · {page+1}/{(len(maps)+24)//25}",
            min_values=1,max_values=len(chunk) if batch else 1,
            options=[discord.SelectOption(label=str(m["mapName"])[:100],value=m["mapId"]) for m in chunk])
        async def pick(interaction):
            await cog.select_maps(interaction,actor,action,label,list(select.values))
        select.callback=pick
        self.add_item(select)
        if batch and len(maps)<=25:
            all_button=discord.ui.Button(label="Összes alkalmas helyi map",style=discord.ButtonStyle.secondary)
            async def all_maps(interaction):
                ids=[m["mapId"] for m in maps if not m["maintenance"] and
                    (m["confirmedStopped"] if action=="start" else m["running"])]
                if not ids:
                    await interaction.response.send_message("Nincs alkalmas map.",ephemeral=True)
                    return
                await cog.select_maps(interaction,actor,action,label,ids)
            all_button.callback=all_maps
            self.add_item(all_button)
        for offset,title in [(-1,"Előző"),(1,"Következő")]:
            if 0<=page+offset<(len(maps)+24)//25:
                b=discord.ui.Button(label=title)
                async def nav(interaction,p=page+offset):
                    try:
                        await cog.identity(interaction,actor)
                        await interaction.response.edit_message(view=MapPicker(cog,actor,action,label,maps,p))
                    except Exception:
                        if not interaction.response.is_done():await interaction.response.send_message("Hozzáférés megtagadva.",ephemeral=True)
                b.callback=nav
                self.add_item(b)

class ParameterButton(discord.ui.View):
    def __init__(self,cog,actor,action,label,ids):
        super().__init__(timeout=90)
        b=discord.ui.Button(label="Paraméter megadása")
        async def open_modal(interaction):
            try:
                await cog.identity(interaction,actor)
                await interaction.response.send_modal(ParameterModal(cog,actor,action,label,ids))
            except Exception:
                if not interaction.response.is_done():await interaction.response.send_message("Hozzáférés megtagadva.",ephemeral=True)
        b.callback=open_modal
        self.add_item(b)

class ParameterModal(discord.ui.Modal):
    def __init__(self,cog,actor,action,label,ids):
        super().__init__(title="Obsidian · "+action,timeout=90)
        self.cog,self.actor,self.action,self.label,self.ids=cog,actor,action,label,ids
        self.value=discord.ui.TextInput(label="RCON parancs" if action=="rcon" else "Ellenőrzött mentési pont UUID",max_length=240 if action=="rcon" else 36)
        self.add_item(self.value)
    async def on_submit(self,interaction):
        await interaction.response.defer(ephemeral=True,thinking=True)
        try:
            who=await self.cog.identity(interaction,self.actor)
            await self.cog.show_preview(interaction,who,self.action,self.label,self.ids,str(self.value.value))
        except Exception:
            await interaction.followup.send("Előnézet elutasítva.",ephemeral=True)

class Confirm(discord.ui.View):
    def __init__(self,cog,actor,label,plan,summary):
        super().__init__(timeout=90)
        self.used=False
        button=discord.ui.Button(label="Jóváhagyom",style=discord.ButtonStyle.danger)
        async def approve(interaction):
            # Reject another actor before consuming the local view.
            try:await cog.identity(interaction,actor)
            except Exception:
                if not interaction.response.is_done():await interaction.response.send_message("Hozzáférés megtagadva.",ephemeral=True)
                return
            if self.used:
                await interaction.response.send_message("A jóváhagyás már felhasznált.",ephemeral=True)
                return
            self.used=True
            self.stop()
            await cog.apply(interaction,actor,label,plan,summary)
        button.callback=approve
        self.add_item(button)

class JobView(discord.ui.View):
    def __init__(self,cog,actor,label,operation):
        super().__init__(timeout=900)
        b=discord.ui.Button(label="Natív művelet állapota")
        async def check(interaction):
            await job_status(cog,interaction,actor,label,operation)
        b.callback=check
        self.add_item(b)

async def job_status(cog,interaction,actor,label,operation):
    await interaction.response.defer(ephemeral=True,thinking=True)
    try:
        who=await cog.identity(interaction,actor)
        result=await cog.call(label,who,"job",operationId=operation,timeout=20)
        await interaction.followup.send(clean(json.dumps(result,ensure_ascii=False),1850),ephemeral=True,allowed_mentions=NONE)
    except Exception:
        await interaction.followup.send("A natív művelet állapota nem igazolható; nincs újraküldés.",ephemeral=True)

class JobModal(discord.ui.Modal,title="Műveleti napló"):
    operation=discord.ui.TextInput(label="Művelet UUID",max_length=36)
    def __init__(self,cog,actor):
        super().__init__()
        self.cog,self.actor=cog,actor
    async def on_submit(self,interaction):
        try:
            operation=str(uuid.UUID(str(self.operation.value)))
            entry=self.cog.state["operations"][operation]
        except Exception:
            await interaction.response.send_message("Ismeretlen helyi művelet.",ephemeral=True)
            return
        await job_status(self.cog,interaction,self.actor,entry["host"],operation)

async def install(bot,config_path):
    """Call once in existing bot.setup_hook, NOT on every reconnect."""
    config=json.loads(Path(config_path).read_text(encoding="utf-8"))
    await bot.add_cog(ObsidianControl(bot,config))
