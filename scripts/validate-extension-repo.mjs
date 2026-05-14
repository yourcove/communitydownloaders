import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(root, "extensions", "catalog.json");
const errors = [];

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
}

function isLowerKebab(value) {
  return value === value.toLowerCase() && !value.includes(" ");
}

const catalog = readJson(catalogPath);
const entries = Array.isArray(catalog.extensions) ? catalog.extensions : [];

if (!catalog.schemaVersion) errors.push("extensions/catalog.json missing schemaVersion");
if (entries.length === 0) errors.push("extensions/catalog.json has no extensions");

const ids = new Set();
const tagPrefixes = new Set();
for (const entry of entries) {
  for (const field of ["name", "id", "path", "tagPrefix"]) {
    if (!entry[field]) errors.push(`${entry.id ?? entry.name ?? "catalog entry"}: missing ${field}`);
  }

  if (entry.id && ids.has(entry.id)) errors.push(`${entry.id}: duplicate extension id`);
  if (entry.id) ids.add(entry.id);

  if (entry.tagPrefix && tagPrefixes.has(entry.tagPrefix)) errors.push(`${entry.id}: duplicate tagPrefix ${entry.tagPrefix}`);
  if (entry.tagPrefix) tagPrefixes.add(entry.tagPrefix);
  if (entry.tagPrefix && !entry.tagPrefix.endsWith("/")) errors.push(`${entry.id}: tagPrefix must end with /`);

  const extensionDir = path.join(root, entry.path ?? "");
  const manifestPath = path.join(extensionDir, "extension.json");
  const projectPath = path.join(extensionDir, `${entry.name}.csproj`);

  if (!fs.existsSync(extensionDir)) {
    errors.push(`${entry.id}: path does not exist: ${entry.path}`);
    continue;
  }
  if (!fs.existsSync(manifestPath)) {
    errors.push(`${entry.id}: missing extension.json at ${entry.path}`);
    continue;
  }
  if (!entry.manifestOnly && !fs.existsSync(projectPath)) {
    errors.push(`${entry.id}: missing project ${entry.name}.csproj at ${entry.path}`);
  }

  const manifest = readJson(manifestPath);
  if (manifest.id !== entry.id) errors.push(`${entry.id}: catalog id does not match extension.json id ${manifest.id}`);
  if (!manifest.version) errors.push(`${entry.id}: extension.json missing version`);
  if (!manifest.minCoveVersion) errors.push(`${entry.id}: extension.json missing minCoveVersion`);
  if (!entry.manifestOnly && !manifest.entryDll) errors.push(`${entry.id}: extension.json missing entryDll`);
  if (entry.manifestOnly && manifest.entryDll) errors.push(`${entry.id}: manifestOnly entry must not declare entryDll`);
  if (entry.manifestOnly && manifest.kind !== "bundle") errors.push(`${entry.id}: manifestOnly entries must use kind=bundle`);
  if (!manifest.url) errors.push(`${entry.id}: extension.json missing url`);
  if (!Array.isArray(manifest.categories) || manifest.categories.length === 0) {
    errors.push(`${entry.id}: extension.json missing categories`);
  } else {
    for (const category of manifest.categories) {
      if (!isLowerKebab(category)) errors.push(`${entry.id}: category must be lowercase kebab-case: ${category}`);
    }
  }
}

if (errors.length > 0) {
  for (const error of errors) console.error(`ERROR: ${error}`);
  process.exit(1);
}

console.log(`Validated ${entries.length} extension catalog entries.`);
