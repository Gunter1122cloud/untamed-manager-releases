"""Offline UI and transport checks; no Discord/ASA network connections."""
import tempfile
import unittest
from pathlib import Path
from obsidian_discord_client import Host
from obsidian_discord_cog import ObsidianControl, MapPicker, ACTIONS

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
