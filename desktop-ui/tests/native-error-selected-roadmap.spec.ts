import {test,expect} from "@playwright/test";
import {describeNativeError} from "../src/state/nativeErrors";
test("selected roadmap native diagnostics preserve stable code, never raw private path",()=>{
  expect(describeNativeError("UM-MOVE-NOT-ENOUGH-SPACE")).toContain("szabad tárhely");
  expect(describeNativeError("UM-MAP-MOD-CANNOT-DISABLE")).toContain("nem tiltható");
  expect(describeNativeError("UM-MOD-REPAIR-STOP-REQUIRED")).toContain("leállított");
  expect(describeNativeError("C:\\private\\secret")).toBe("A natív művelet eredménye nem igazolt. (UM-UNKNOWN)");
});
