// Opens the WebGL benchmark player (served at the URL given) in headless Chrome and prints its NINO_WEBGL
// lines until NINO_WEBGL_DONE. usage: node webgl_run.mjs <url> <timeout seconds>
import { chromium } from 'playwright-core';

const [url, seconds = '1500'] = process.argv.slice(2);
const browser = await chromium.launch({
  executablePath: process.env.CHROME || '/usr/bin/google-chrome',
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'],
});
const page = await browser.newPage();
let failures = -1;
const done = new Promise((resolve) => {
  page.on('console', (message) => {
    const text = message.text();
    if (text.includes('NINO_WEBGL') || message.type() === 'error') console.log(text);
    const m = /NINO_WEBGL_DONE\|failures=(\d+)/.exec(text);
    if (m) {
      failures = Number(m[1]);
      resolve();
    }
  });
  page.on('pageerror', (error) => console.log('pageerror: ' + error));
});
console.log('user agent: ' + (await browser.version()));
await page.goto(url);
let timer;
await Promise.race([done, new Promise((r) => (timer = setTimeout(r, Number(seconds) * 1000)))]);
// The pending timer would keep Node running until it fires.
clearTimeout(timer);
await browser.close();
if (failures !== 0) {
  console.log(failures < 0 ? 'timed out' : `${failures} failures`);
  process.exit(1);
}
