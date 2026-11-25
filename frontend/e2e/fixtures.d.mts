export interface FixturePaths {
  runtime: string
  codex: string
  copilotRoot: string
  vscodeRoot: string
  data: string
  emptySource: string
}
export function createFixture(outputDirectory: string): Promise<FixturePaths>
