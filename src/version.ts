import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createLogger } from "./utils/logger.js";

const logger = createLogger("version");

function readVersion(path: string): string {
  const value = JSON.parse(readFileSync(path, "utf8")) as { version?: string };
  return value.version ?? "unknown";
}

// import.meta.url-relative paths only resolve correctly when this module runs
// from its original build/ location. A bundled (esbuild + pkg) build flattens
// everything into one file at a different relative depth, so both fall back
// to a path relative to the process's working directory instead - correct as
// long as the process is launched with the submodule root as its cwd (see
// ProcessSupervisor.cs).
function moduleRelativePackageJsonPath(): string | null {
  // import.meta.url is empty under esbuild's CJS output format (the shape the
  // bundled build compiles to), which makes the URL constructor below throw -
  // caught here so the cwd-relative candidate still gets a chance.
  try {
    return fileURLToPath(new URL("../package.json", import.meta.url));
  } catch {
    return null;
  }
}

function resolveAppVersion(): string {
  const candidates = [moduleRelativePackageJsonPath(), resolve(process.cwd(), "package.json")].filter(
    (candidate): candidate is string => candidate !== null
  );
  for (const candidate of candidates) {
    try {
      return readVersion(candidate);
    } catch {
      continue;
    }
  }
  logger.warn("Could not resolve application package.json for version reporting", { candidates });
  return "unknown";
}

function resolveMcpSdkVersion(): string {
  try {
    const require = createRequire(import.meta.url);
    const sdkEntry = require.resolve("@modelcontextprotocol/sdk/server/mcp.js");
    return readVersion(resolve(dirname(sdkEntry), "../../../package.json"));
  } catch (error) {
    logger.warn("Could not resolve MCP SDK version", {
      error: error instanceof Error ? error.message : String(error),
    });
    return "unknown";
  }
}

export const APP_VERSION = resolveAppVersion();
export const MCP_SDK_VERSION = resolveMcpSdkVersion();

export function dependencyVersions() {
  return {
    application: APP_VERSION,
    mcpSdk: MCP_SDK_VERSION,
    node: process.version,
  };
}
