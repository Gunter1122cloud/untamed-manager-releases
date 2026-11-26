# Uploaded manager source audit — 2026-11-26

This is a reference-source inventory, not a claim that every source line or every upstream executable has been tested. All 14 uploaded ZIPs in the current set were opened. Two ASMA archives share the same revision and server implementation. One ASAM package contains no editable source. Do not describe a comparison as a source-code import.

| Archive / revision | Inspected source | Finding and adoption status |
| --- | --- | --- |
| ObeliskPanel 3389140218906843fb01fa6c72119ad018c44195 | SteamCmd.cs:28–66, 69–120; ServerLauncher.cs:114–125; IniFile.cs | Native launch, live Steam output, cached builds. Obsidian already has exact app/branch validation; do not replace it with a looser parser. ConPTY fallback may dispatch a second update if the first run already began, so not copied. |
| ArkKeeper 507d24b196c5f24812b40806e4423cdcb32a7d30 | src/ArkKeeper.Core/Servers/ServerProcess.cs:68–131, 236–247 | Process lifetime and short lock; behavior reference in lifecycle changes. GPL source not copied. |
| ASA-Server-Manager 16e52f0a476447aaa0dc28a64be1aee3f0ade243 | ASA Server Manager/Helpers/ProcessMonitor.cs:22–31 | Timer start/dispose. Existing native supervisor uses cancellable periodic timer; no new overlapping timer copied. |
| ASA-Server-Manager da2f0fbe71848159c2502f0bf59e031874995cc3 | Functions.cpp:129–146, 231–235 | PowerShell process launch. Keep typed native argument list instead of shell wrapper; no direct copy. |
| ArkAscendedServerManager 0937486eea8ebc9ade8b95eeb91df0d3808375b7 | core/src/main/kotlin/managers/PowerManager.kt:166–189 | Direct start / authenticated DoExit. Obsidian already has native SaveWorld + DoExit; no weaker stop replacement. |
| asm-ascended 50c95edfa257a86acf53f0e7a623a8f761c93c4e | asma/src/server/mod.rs:247–252 | Same ASMA revision; not counted as distinct implementation. |
| asma 50c95edfa257a86acf53f0e7a623a8f761c93c4e | asma/src/server/mod.rs:247–252; asma/src/utils/steamcmd_utils.rs:11–82 | Native process launch and streaming SteamCMD bootstrap. Existing validated ZIP pipeline retained; unchecked extraction not copied. |
| ASAM e7206678b78c3c832825c8879aee00f48bfe0298 | Archive listing | Binary distribution, no editable source found; no source import. |
| Cerious 40a918fa6acc3295d812a4c24dce6ab19df00ac3 | electron/services/server-instance/server-lifecycle.service.ts:27–65, 199–201 | Per-instance lifecycle admission and graceful stop. Adaptation in #259/#263/#264/#265; not verbatim copy. |
| ChaosQK Ark-Server-Manager cb9e50a2393fdf939e231df87b0d9d11e1da5268 | core/server_process.py:75–149; core/steamcmd.py:105–148 | **Adopted** exact SteamCMD noninteractive switches at lines 116–117 in shared SteamCmdInvocation. Keep Obsidian's force_install_dir-before-login order, manifest verification and bounded retry; do not copy exit-code-only success. |
| Niexiawei asa-server-manager 9364a6b6b0385556f80cb209cc766df351a8e577 | internal/instance/server.go:844–874, 995–1045 | Stop/Restart failure classification; behavior reference, no license assumed and no direct code copy. |
| Ch4r0ne ARK-Ascended-Server-Manager 1de9ed6d86b3553007aa0193d764fdcd0ad4f72c | ARK-Ascended-Server-Manager.py:1568–1640 | **Adopted** noninteractive SteamCMD variables from script lines 1591–1595 in native argv. Keep existing stronger proof and retries. |
| DiFuks ark-manager 63ab649f12efdebd036a8474d8aa1cb676fc5aae | src/ArkManager.Core/Services/ServerManager.cs:68–166; Services/Rcon/RconErrors.cs | Verbatim MIT socket error mapping in #255; exit polling adaptation in #256. |
| YARK a9b5e0be72b3bd3618a917caf207bbd80c3451ef | src/backend/infra/process/process-start.ts; process-graceful-stop.ts; process-ready-wait.ts | Launch/adoption, bounded shutdown/readiness references used in earlier PRs. GPL code not copied. |

## Implementation in this PR
`SteamCmdInvocation.UpdateArguments` now carries the two SteamCMD control variables used in ChaosQK and Ch4r0ne. Fresh install, stopped update and shared payload staging use the same existing command contract; the isolated updater is routed through that contract too. Bootstrap `+quit` stays independent. Preserve explicit argument separation, anonymous login, exact ASA app ID, credentials scrubbing and verification. No new operator gate.

## Verification and limits
Windows CI checks the argument contract with isolated synthetic fixtures. No real SteamCMD process or ASA server has been invoked in the live environment. The two switches are present in both uploaded source implementations; the current external Valve search did not return a corroborating result. Do not claim real-world execution until independently tested. Subsequent PRs must document exact upstream file/revision, actual copied/adapted code and non-adoption rationale, and must keep the canonical #250 integration ledger current.
