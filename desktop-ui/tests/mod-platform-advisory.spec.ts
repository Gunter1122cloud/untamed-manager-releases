import {test,expect} from "@playwright/test";
import {platformAdvisory,platformAdvisoryText} from "../src/state/modPlatformAdvisory";
test("platform metadata is advisory, not proof of local crossplay support",()=>{
  expect(platformAdvisory(["Windows","WindowsServer","1.0"]).state).toBe("unknown");
  expect(platformAdvisory(["PC","Steam","Xbox"]).state).toBe("unknown");
  expect(platformAdvisory([" Cross-Platform "]).state).toBe("crossplay-label");
  expect(platformAdvisory(["PC Only"]).state).toBe("pc-only-label");
  expect(platformAdvisory(["Crossplay","PC-only"]).state).toBe("conflict");
  expect(platformAdvisory(["not crossplay","1.0"]).state).toBe("unknown");
  expect(platformAdvisoryText("crossplay-label")).toContain("nem igazolt");
});
