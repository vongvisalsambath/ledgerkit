// The published site in Chromium, served with the production headers from vercel.json.
// Usage: node site/test/e2e.cjs <published wwwroot folder>
const { chromium, devices } = require("playwright");
const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");

const root = path.resolve(process.argv[2] || "out/wwwroot");
const repo = path.resolve(__dirname, "../..");
const port = 4300 + Math.floor(Math.random() * 500);
const base = `http://127.0.0.1:${port}/`;
const statement = fs.readFileSync(path.join(repo, "samples/statement.xml"), "utf8");
let failures = 0;
const check = (ok, name, detail = "") => {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${!ok && detail ? "  " + detail : ""}`);
  if (!ok) failures++;
};
const squash = s => (s || "").replace(/\s+/g, " ").trim();

(async () => {
  const server = spawn("python3", [path.join(__dirname, "serve.py"), process.env.VERCEL_JSON || path.join(repo, "vercel.json"), root, String(port)], { stdio: "inherit" });
  try {
    for (let i = 0; i < 50; i++) {
      try { if ((await fetch(base)).ok) break; } catch { /* starting */ }
      await new Promise(r => setTimeout(r, 100));
    }
    const head = await fetch(base);
    const csp = head.headers.get("content-security-policy") || "";
    check((csp.match(/script-src [^;]*/) || [""])[0] === "script-src 'self' 'wasm-unsafe-eval'" && csp.includes("frame-ancestors 'none'"), "strict Content-Security-Policy");
    for (const f of ["dotnet.js", "blazor.webassembly.js"])
      check((await fetch(base + "_framework/" + f)).headers.get("cache-control") === "no-cache", `${f} isn't cached as immutable`);

    const browser = await chromium.launch();
    const open = async (options = {}) => {
      const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, acceptDownloads: true, ...options });
      const p = await ctx.newPage();
      const problems = [];
      p.on("pageerror", e => problems.push("error: " + e.message));
      p.on("dialog", d => { problems.push("dialog: " + d.message()); d.dismiss(); });
      p.on("console", m => { if (m.type() === "error") problems.push("console: " + m.text().slice(0, 200)); });
      p.on("request", r => { if (!r.url().startsWith(base) && !r.url().startsWith("data:") && !r.url().startsWith("blob:")) problems.push("request: " + r.url()); });
      await p.addInitScript(() => { window.__csp = []; document.addEventListener("securitypolicyviolation", e => window.__csp.push(e.violatedDirective + " " + e.blockedURI)); });
      await p.goto(base);
      await p.waitForSelector(".verdict, .problems", { timeout: 60000 });
      const done = async () => [...problems, ...(await p.evaluate(() => window.__csp)).map(v => "csp: " + v)];
      return { ctx, p, done };
    };
    const setText = async (p, id, text) => {
      await p.evaluate(([id, text]) => { const t = document.getElementById(id); t.value = text; t.dispatchEvent(new Event("input", { bubbles: true })); }, [id, text]);
      await p.waitForTimeout(400);
    };

    {
      const { ctx, p, done } = await open();
      const verdict = squash(await p.textContent(".verdict"));
      const why = squash(await p.textContent(".why"));
      check(verdict.startsWith("Not reconciled") && why.includes("-1,500.00") && why.includes("-49.50") && why.includes("300.00") && why.includes("250.50"), "the sample: not reconciled, and the difference explained", why);
      check((await p.locator(".lines .matched").count()) === 2 && (await p.locator(".lines .bank").count()) === 1 && (await p.locator(".lines .ledger").count()) === 1, "2 matched, 1 only on the statement, 1 only in the ledger");

      // post the fee and cancel the cheque: now it reconciles (opening difference aside)
      await setText(p, "ledger", "2026-11-01,,10000.00,Opening balance\n2026-11-04,ORDER-7781,1500.00,Order 7781\n2026-11-04,RENT-NOV,-250.50,Rent\n2026-11-04,,-49.50,Bank fee");
      check(squash(await p.textContent(".verdict")).startsWith("Reconciled"), "with the fee posted and the sale dated right, it reconciles");

      const [download] = await Promise.all([p.waitForEvent("download"), p.click("text=Download the report (CSV)")]);
      const csv = fs.readFileSync(await download.path(), "utf8");
      check(csv.startsWith("statement,status,rule,") && csv.includes("matched"), "the report downloads as CSV");

      await p.click("text=Back to the sample");
      check(squash(await p.textContent(".verdict")).startsWith("Not reconciled"), "back to the sample");
      const issues = await done();
      check(issues.length === 0, "no errors, policy violations or outside requests", issues.join(" | "));
      await ctx.close();
    }

    {
      const { ctx, p, done } = await open();
      const hostile = [
        ["a DTD with an external entity", '<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]><d>&x;</d>', "XML-DTD"],
        ["not XML", "hello", "XML-MALFORMED"],
        ["a statement whose balances don't add up", statement.replace("<Amt Ccy=\"EUR\">11200.00</Amt>", "<Amt Ccy=\"EUR\">11300.00</Amt>"), "CAMT053-BALANCE"],
        ["markup in a statement", statement.replace("Monthly account fee", "&lt;img src=x onerror=alert(1)&gt;"), null],
      ];
      for (const [name, xml, code] of hostile) {
        await setText(p, "statement", xml);
        const text = squash(await p.textContent(".results"));
        const injected = await p.evaluate(() => document.querySelectorAll(".results img, .results script").length);
        check(injected === 0 && (code === null || text.includes(code)), "statement: " + name, text.slice(0, 160));
      }
      await setText(p, "statement", statement);
      for (const [name, csv, expect] of [
        ["a bad date", "04/11/2026,,10,x", "the date should look like"],
        ["a bad amount", "2026-11-04,,1e5,x", "the amount should be"],
        ["markup in a description", "2026-11-04,X,5,<script>alert(1)</script>", null],
        ["5,001 lines", "2026-11-04,,1,x\n".repeat(5001), "more than 5,000"],
      ]) {
        await setText(p, "ledger", csv);
        const text = squash(await p.textContent(".results"));
        const injected = await p.evaluate(() => document.querySelectorAll(".results script, .results img").length);
        check(injected === 0 && (expect === null || text.includes(expect)), "ledger: " + name, text.slice(0, 160));
      }
      const issues = await done();
      check(issues.length === 0, "hostile input: no errors, dialogs or outside requests", issues.join(" | "));
      await ctx.close();
    }

    {
      const { ctx, p, done } = await open({ ...devices["iPhone 13"] });
      const [scroll, width] = await p.evaluate(() => [document.documentElement.scrollWidth, innerWidth]);
      check(scroll <= width, "no sideways scrolling on a phone", `page ${scroll}px, screen ${width}px`);
      const spill = await p.evaluate(() => {
        const card = document.querySelector(".results").getBoundingClientRect();
        return [...document.querySelectorAll(".results > *")].filter(e => e.getBoundingClientRect().right > card.right + 1).map(e => e.className);
      });
      check(spill.length === 0, "nothing spills out of the results card on a phone", spill.join(", "));
      const scrolls = await p.evaluate(() => { const w = document.querySelector(".table-wrap"); return w.scrollWidth > w.clientWidth; });
      check(!scrolls, "the balances fit on a phone without scrolling");
      await p.screenshot({ path: path.join(require("os").tmpdir(), "ledgerkit-phone.png"), fullPage: true });
      check((await done()).length === 0, "phone: no errors");
      await ctx.close();
    }

    {
      const ctx = await browser.newContext();
      const p = await ctx.newPage();
      await p.setContent(`<iframe src="${base}" width="400" height="300"></iframe>`);
      await p.waitForTimeout(1500);
      const frame = p.frames().find(f => f !== p.mainFrame());
      const framed = frame ? await frame.evaluate(() => !!document.querySelector(".brand")).catch(() => false) : false;
      check(!framed, "the page can't be framed");
      await ctx.close();
    }
    await browser.close();
  } finally {
    server.kill();
  }
  console.log(failures ? `\n${failures} failed` : "\nall passed");
  process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
