import { test, expect } from "@playwright/test";
import { createSnapshotRefreshQueue } from "../src/state/snapshotRefreshQueue";
import { describeNativeError } from "../src/state/nativeErrors";

function deferred() {
  let resolve!:()=>void;
  let reject!:(reason:Error)=>void;
  const promise=new Promise<void>((a,b)=>{resolve=a;reject=b;});
  return {promise,resolve,reject};
}
test("foreground refreshes coalesce behind a background read without overlapping",async()=>{
  const gates=[deferred(),deferred(),deferred()];
  const calls:boolean[]=[];
  const queue=createSnapshotRefreshQueue(background=>{
    const gate=gates[calls.length];calls.push(background);return gate.promise;
  });
  const old=queue.request(true);
  await Promise.resolve();
  expect(calls).toEqual([true]);
  const one=queue.request();
  const two=queue.request();
  expect(one).toBe(two);
  expect(queue.request(true)).toBe(old);
  gates[0].resolve();
  await old;
  await expect.poll(()=>calls).toEqual([true,false]);
  const next=queue.request();
  gates[1].resolve();
  await one;
  await expect.poll(()=>calls).toEqual([true,false,false]);
  gates[2].resolve();
  await next;
});
test("a failed read does not strand the queued foreground refresh",async()=>{
  const first=deferred();
  let count=0;
  const queue=createSnapshotRefreshQueue(async()=>{if(++count===1)await first.promise;});
  const old=queue.request(true);
  const observed=old.catch(e=>e.message);
  const next=queue.request();
  first.reject(new Error("offline"));
  expect(await observed).toBe("offline");
  await next;
  expect(count).toBe(2);
});
test("localized diagnostics retain stable codes without echoing arbitrary paths",()=>{
  expect(describeNativeError("UM-ASA-INSTALL-INCOMPLETE")).toContain("hiányos");
  expect(describeNativeError("UM-ASA-EXE-EMPTY")).toContain("üres");
  expect(describeNativeError("UM-FUTURE-CODE")).toContain("UM-FUTURE-CODE");
  expect(describeNativeError("C:\\private\\token")).not.toContain("private");
});
