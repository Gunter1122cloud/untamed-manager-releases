"""Offline UI and transport checks; no Discord/ASA network connections."""
import tempfile
import unittest
from unittest.mock import AsyncMock, MagicMock, patch
from pathlib import Path
from obsidian_discord_client import Host
from obsidian_discord_cog import ObsidianControl, MapPicker, ACTIONS, Identity, Confirm, ApiError

class TestHost(unittest.TestCase):
    def test_reject_public_and_invalid_credentials(self):
        for address in ("0.0.0.0","8.8.8.8","127.0.0.1"):
            with self.assertRaises(ValueError):
                Host(address,19443,"a"*64,"bad")
        with self.assertRaises(ValueError):
            Host("8.8.8.8",19443,"a"*64,"b"*64)
        host=Host("127.0.0.1",19443,"a"*64,"b"*64)
        self.assertNotIn("b"*64,repr(host))

class TestPanel(unittest.IsolatedAsyncioTestCase):
    async def test_control_view_and_map_picker(self):
        with tempfile.TemporaryDirectory() as temp:
            cog=ObsidianControl(None,dict(guild_id="123456789012345678",
                 role_ids=["223456789012345678","323456789012345678"],
                 state_file=str(Path(temp)/"state.json"),hosts={"test":{
                    "address":"127.0.0.1","port":19443,
                    "certificate_sha256":"a"*64,"credential":"b"*64}}))
            self.assertTrue(cog.panel.is_persistent())
            self.assertEqual(len(cog.panel.children),len(ACTIONS)+1)
            self.assertEqual(len({b.custom_id for b in cog.panel.children}),len(cog.panel.children))
            maps=[dict(mapName=f"Map {i}",mapId=f"map-{i}",confirmedStopped=True,
                       running=False,maintenance=False) for i in range(25)]
            picker=MapPicker(cog,1,"start","test",maps)
            self.assertEqual(picker.children[0].max_values,25)
            direct=MapPicker(cog,1,"start-direct","test",maps)
            self.assertEqual(direct.children[0].max_values,1)
            picker.stop();direct.stop();cog.panel.stop()

    async def test_provision_only_without_secrets(self):
        with tempfile.TemporaryDirectory() as temp:
            cog=ObsidianControl(None,dict(guild_id=123456789012345678,
                role_ids=[223456789012345678],state_file=str(Path(temp)/"state.json"),
                provision_only=True,hosts={}))
            self.assertEqual(cog.hosts,{})
            cog.panel.stop()

    async def test_worker_retries_transient_startup_without_dispatch(self):
        with tempfile.TemporaryDirectory() as temp:
            bot=AsyncMock()
            cog=ObsidianControl(bot,dict(guild_id=123456789012345678,
                role_ids=[223456789012345678],state_file=str(Path(temp)/"state.json"),
                provision_only=True,hosts={}))
            cog.provision_once=AsyncMock(side_effect=[OSError("temporary"),None])
            cog.replace_message=AsyncMock()
            cog.call=AsyncMock()
            try:
                with patch("obsidian_discord_cog.asyncio.sleep",new_callable=AsyncMock) as sleep:
                    await cog.worker()
                    sleep.assert_awaited_once_with(30)
                self.assertEqual(cog.provision_once.await_count,2)
                cog.replace_message.assert_not_awaited()
                cog.call.assert_not_awaited()
            finally:
                cog.panel.stop()

    async def test_mutation_rechecks_identity_in_host_lane(self):
        with tempfile.TemporaryDirectory() as temp:
            cog=ObsidianControl(None,dict(guild_id=123456789012345678,
                role_ids=[223456789012345678],state_file=str(Path(temp)/"state.json"),
                hosts={"test":{"address":"127.0.0.1","port":19443,
                    "certificate_sha256":"a"*64,"credential":"b"*64}}))
            who=Identity(323456789012345678,423456789012345678,(223456789012345678,))
            interaction=object()
            cog.identity=AsyncMock(side_effect=PermissionError("role revoked"))
            try:
                with patch("obsidian_discord_cog.async_call",new_callable=AsyncMock) as transport:
                    with self.assertRaises(PermissionError):
                        await cog.call("test",who,"apply",interaction=interaction,token="not-sent")
                    cog.identity.assert_awaited_once_with(interaction,who.user_id)
                    with self.assertRaises(PermissionError):
                        await cog.call("test",who,"preview")
                    transport.assert_not_awaited()
            finally:
                cog.panel.stop()

    async def test_confirm_defers_before_slow_role_check_and_is_single_use(self):
        actor=123456789012345678
        cog=MagicMock()
        interaction=MagicMock()
        interaction.user.id=actor
        interaction.response.defer=AsyncMock()
        interaction.followup.send=AsyncMock()
        async def identity(*args):
            interaction.response.defer.assert_awaited()
            return Identity(actor,223456789012345678,())
        cog.identity=AsyncMock(side_effect=identity)
        cog.apply=AsyncMock()
        view=Confirm(cog,actor,"test",{"action":"restart"},"fixture")
        try:
            await view.children[0].callback(interaction)
            await view.children[0].callback(interaction)
            cog.apply.assert_awaited_once()
            self.assertTrue(view.used)
            interaction.followup.send.assert_awaited_once()
        finally:
            view.stop()

    async def test_read_only_events_do_not_wait_for_mutation_lock(self):
        import asyncio
        with tempfile.TemporaryDirectory() as temp:
            cog=ObsidianControl(None,dict(guild_id=123456789012345678,
                role_ids=[223456789012345678],state_file=str(Path(temp)/"state.json"),
                hosts={"test":{"address":"127.0.0.1","port":19443,
                    "certificate_sha256":"a"*64,"credential":"b"*64}}))
            who=Identity(323456789012345678,423456789012345678,(223456789012345678,))
            try:
                with patch("obsidian_discord_cog.async_call",new_callable=AsyncMock) as transport:
                    transport.return_value={"ok":True,"data":{"events":[]}}
                    async with cog.host_locks["test"]:
                        result=await asyncio.wait_for(cog.call("test",who,"events"),timeout=1)
                    self.assertEqual(result,{"events":[]})
                    transport.assert_awaited_once()
            finally:
                cog.panel.stop()

    async def test_poll_rotates_past_unfinished_operations(self):
        with tempfile.TemporaryDirectory() as temp:
            cog=ObsidianControl(None,dict(guild_id=123456789012345678,
                role_ids=[223456789012345678],state_file=str(Path(temp)/"state.json"),
                hosts={"test":{"address":"127.0.0.1","port":19443,
                    "certificate_sha256":"a"*64,"credential":"b"*64}}))
            try:
                cog.service=AsyncMock(return_value=Identity(1,2,()))
                cog.call=AsyncMock(return_value={"state":"claimed-no-replay"})
                for i in range(12):
                    cog.state["operations"][str(i)]={"state":"claimed-no-replay","host":"test","action":"restart"}
                await cog.poll_jobs()
                await cog.poll_jobs()
                self.assertIn("11",[x.kwargs["operationId"] for x in cog.call.await_args_list])
                self.assertTrue(all(e["state"]=="claimed-no-replay" for e in cog.state["operations"].values()))
            finally:
                cog.panel.stop()

    async def test_busy_and_uncertain_classification(self):
        self.assertTrue(ApiError({"error":"UM-DC-MAP-BUSY"}).not_dispatched)
        self.assertFalse(ApiError({"error":"UM-DC-REPLY-PENDING"}).not_dispatched)
        self.assertFalse(ApiError({"error":"UM-DC-NATIVE-UNCERTAIN-NO-REPLAY",
            "nativeError":"UM-ASA-RESTART-JOURNAL-NEEDS-OPERATOR"}).not_dispatched)
