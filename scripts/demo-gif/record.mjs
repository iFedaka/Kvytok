// Records the README's GIF: two windows on the seat-grid page go for the same seat at the same
// instant, and the API lets exactly one of them have it. Nothing is staged: both clicks are real
// requests, sent together, and which window wins is up to the database.
//
//   cd scripts/demo-gif && npm install
//   BASE=http://localhost:5224 node record.mjs     # writes docs/images/seat-race.gif
//
// Needs ffmpeg on PATH. Against a local Api, run it with Outbox__FailImmediatePublish=true when
// there is no Service Bus to reach, so a reservation does not wait out a send timeout.
import { chromium } from "playwright";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const base = (process.env.BASE ?? "http://localhost:5224").replace(/\/$/, "");
const out = resolve(process.env.OUT ?? "../../docs/images/seat-race.gif");
const contested = 14;
const width = 560, height = 600;

async function api(path, body) {
  const response = await fetch(`${base}${path}`, {
    method: body ? "POST" : "GET",
    headers: { "Content-Type": "application/json" },
    body: body && JSON.stringify(body),
  });
  return response;
}

const reserve = seat => api(`/events/${eventId}/reservations`, { seatNumber: seat, holderEmail: "someone@example.com" });

// A fresh event, already partly sold, so the grid does not look staged
const created = await api("/events", {
  name: "Kvytok live · Hall 1",
  startsAt: new Date(Date.now() + 30 * 86400_000).toISOString(),
  seatCount: 40,
});
if (created.status !== 201) throw new Error(`could not create an event: ${created.status}`);
const { eventId } = await created.json();
for (const seat of [2, 7, 8, 19, 25, 33, 38]) await reserve(seat);

// A visible pointer: headless video has none of its own
const cursor = () => {
  window.addEventListener("DOMContentLoaded", () => {
    const dot = document.createElement("div");
    dot.style.cssText = "position:fixed;left:0;top:0;width:18px;height:18px;margin:-9px 0 0 -9px;border-radius:50%;" +
      "background:rgba(30,30,30,.55);border:2px solid #fff;box-shadow:0 1px 4px rgba(0,0,0,.4);" +
      "pointer-events:none;z-index:9999;transition:transform .08s;transform:translate(-100px,-100px)";
    document.body.append(dot);
    let x = -100, y = -100;
    addEventListener("mousemove", e => { x = e.clientX; y = e.clientY; dot.style.transform = `translate(${x}px,${y}px)`; });
    addEventListener("mousedown", () => dot.style.transform = `translate(${x}px,${y}px) scale(.7)`);
    addEventListener("mouseup", () => dot.style.transform = `translate(${x}px,${y}px)`);
  });
};

const videos = mkdtempSync(join(tmpdir(), "kvytok-gif-"));
const browser = await chromium.launch();
const open = async name => {
  const started = Date.now();
  const context = await browser.newContext({
    viewport: { width, height },
    colorScheme: "light",
    recordVideo: { dir: join(videos, name), size: { width, height } },
  });
  await context.addInitScript(cursor);
  const page = await context.newPage();
  await page.goto(`${base}/demo/?event=${eventId}&as=${name}`);
  await page.locator(".seat").first().waitFor();
  await page.mouse.move(width / 2, height - 40);
  return { name, context, page, started };
};
const windows = [await open("anna"), await open("ben")];
const seat = (w, n) => w.page.locator(`.seat[data-seat="${n}"]`);
const centre = async (w, n) => {
  const box = await seat(w, n).boundingBox();
  return [box.x + box.width / 2, box.y + box.height / 2];
};
const pause = ms => new Promise(r => setTimeout(r, ms));
const t0 = Date.now();

await pause(1500);

// Both pointers travel to the same seat, then both press at once
await Promise.all(windows.map(async w => w.page.mouse.move(...await centre(w, contested), { steps: 25 })));
await pause(250);
await Promise.all(windows.map(w => w.page.mouse.down()));
await Promise.all(windows.map(w => w.page.mouse.up()));

await Promise.all(windows.map(w => w.page.locator(".message.ok, .message.warn").waitFor()));
const outcome = await Promise.all(windows.map(w => seat(w, contested).evaluate(b => b.classList.contains("mine"))));
if (outcome.filter(Boolean).length !== 1) throw new Error(`expected exactly one winner, got ${outcome}`);
const loser = windows[outcome.indexOf(false)];
console.log(`seat ${contested} went to ${windows[outcome.indexOf(true)].name}`);

// Someone else books two seats; both grids pick it up on their next read
await pause(1800);
await reserve(4);
await reserve(30);

// The loser tries the seat next door, and gets it
await pause(2200);
await loser.page.mouse.move(...await centre(loser, contested + 1), { steps: 20 });
await pause(200);
await seat(loser, contested + 1).click();
await loser.page.locator(".message.ok").waitFor();
await pause(2600);
const duration = (Date.now() - t0) / 1000;

const files = [];
for (const w of windows) {
  await w.context.close();  // flushes the video
  const dir = join(videos, w.name);
  files.push({ path: join(dir, readdirSync(dir)[0]), skip: (t0 - w.started) / 1000 });
}
await browser.close();

// Side by side, cut to the race, a light gap between the windows, palette per GIF for clean colours
const filter =
  `[0:v]trim=start=${files[0].skip}:duration=${duration},setpts=PTS-STARTPTS,pad=iw+12:ih:0:0:color=0xd8d6cf[a];` +
  `[1:v]trim=start=${files[1].skip}:duration=${duration},setpts=PTS-STARTPTS[b];` +
  `[a][b]hstack,fps=10,scale=960:-1:flags=lanczos,split[s0][s1];` +
  `[s0]palettegen=max_colors=64:stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle`;
execFileSync("ffmpeg", ["-y", "-loglevel", "error", "-i", files[0].path, "-i", files[1].path,
  "-filter_complex", filter, "-loop", "0", out], { stdio: "inherit" });
rmSync(videos, { recursive: true, force: true });
console.log(`${out}: ${duration.toFixed(1)} s`);
