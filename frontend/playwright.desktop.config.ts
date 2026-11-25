import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './e2e-desktop',
  outputDir: './test-results/desktop',
  fullyParallel: false,
  workers: 1,
  timeout: 90000,
  expect: { timeout: 15000 },
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report-desktop' }]],
})
