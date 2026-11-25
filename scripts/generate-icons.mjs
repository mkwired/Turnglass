import { readFile, writeFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'

// SVG is the editable source; raster assets are checked in so app builds need no browser.
// Run after installing frontend dependencies and Playwright Chromium:
// node scripts/generate-icons.mjs
const require = createRequire(new URL('../frontend/package.json', import.meta.url))
const { chromium } = require('@playwright/test')
const assets = new URL('../frontend/public/', import.meta.url)
const svg = await readFile(new URL('turnglass.svg', assets), 'utf8')
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
const browser = await chromium.launch({ headless: true })
try {
  const page = await browser.newPage({ deviceScaleFactor: 1 })
  const frames = []
  for (const size of [...sizes, 512]) {
    await page.setViewportSize({ width: size, height: size })
    await page.setContent(`<style>html,body{margin:0;background:transparent}svg{display:block;width:100vw;height:100vh}</style>${svg}`)
    const png = await page.screenshot({ omitBackground: true, animations: 'disabled' })
    if (size === 512) await writeFile(new URL('turnglass.png', assets), png)
    else frames.push(png)
  }

  // Windows ICO directory followed by PNG-encoded, alpha-preserving image entries.
  const directory = Buffer.alloc(6 + 16 * sizes.length)
  directory.writeUInt16LE(1, 2)
  directory.writeUInt16LE(sizes.length, 4)
  let offset = directory.length
  for (const [index, size] of sizes.entries()) {
    const entry = 6 + 16 * index
    directory.writeUInt8(size === 256 ? 0 : size, entry)
    directory.writeUInt8(size === 256 ? 0 : size, entry + 1)
    directory.writeUInt16LE(1, entry + 4)
    directory.writeUInt16LE(32, entry + 6)
    directory.writeUInt32LE(frames[index].length, entry + 8)
    directory.writeUInt32LE(offset, entry + 12)
    offset += frames[index].length
  }
  await writeFile(new URL('turnglass.ico', assets), Buffer.concat([directory, ...frames]))
  console.log(`Generated ${fileURLToPath(assets)}turnglass.ico (${sizes.join(', ')} px) and turnglass.png (512 px).`)
} finally {
  await browser.close()
}
