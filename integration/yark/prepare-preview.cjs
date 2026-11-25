// Isolated build-only overlay for the pinned upstream source. Never touches server files.
const fs = require("node:fs");
const path = require("node:path");
const cp = require("node:child_process");
const root = path.resolve(__dirname, "../../vendor/yark");
const expected = "a9b5e0be72b3bd3618a917caf207bbd80c3451ef";
const actual = cp.execFileSync("git", ["rev-parse", "HEAD"], {cwd:root,encoding:"utf8"}).trim();
if (actual !== expected) throw new Error("Unexpected YARK source revision");
function replaceOnce(file, before, after) {
  const full = path.join(root,file);
  const content = fs.readFileSync(full,"utf8");
  if (content.split(before).length !== 2) throw new Error("Overlay source changed: "+file);
  fs.writeFileSync(full,content.replace(before,after));
}
const manifestPath=path.join(root,"package.json");
const manifest=JSON.parse(fs.readFileSync(manifestPath,"utf8"));
manifest.build.productName="Obsidian Command Engine Preview";
manifest.build.appId="com.obsidiancommand.enginepreview";
manifest.build.artifactName="Obsidian-Engine-Preview-\${version}.\${ext}".replaceAll("\\","");
delete manifest.build.publish;
fs.writeFileSync(manifestPath,JSON.stringify(manifest,null,2)+"\n");
replaceOnce("src/main/index.ts",'app.setAppUserModelId("com.yark.servermanager")','app.setAppUserModelId("com.obsidiancommand.enginepreview")');
replaceOnce("src/main/index.ts",'title: "YARK server manager"','title: "Obsidian Command — Engine Preview"');
replaceOnce("src/main/index.ts",'  app.setPath("userData", e2eUserData);\n}','  app.setPath("userData", e2eUserData);\n} else {\n  app.setPath("userData", join(app.getPath("appData"), "Obsidian Command Engine Preview"));\n}');
fs.appendFileSync(path.join(root,"src/renderer/src/styles/globals.css"),"\n/* Obsidian preview palette; original UI layout is not yet migrated. */\n:root[data-mantine-color-scheme=dark] { --mantine-color-body: #0c1418; --mantine-color-text: #eaf3f1; --app-color-border: #365259; --app-color-muted: #a0beb9; }\nbody { font-family: Segoe UI, system-ui, sans-serif; }\n");
console.log("Prepared isolated Obsidian engine preview from "+expected);
